using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MySqlConnector;

namespace Coflnet.Sky.Indexer.Archive;

/// <summary>Auction row picked for deletion.</summary>
/// <param name="SellerId"><c>Auctions.SellerId</c> (<c>Players.Id</c>); 0 is not necessarily anonymous (the Numberer sets 0 for duplicate players and old rows were never numbered)</param>
/// <param name="AuctioneerId"><c>Auctions.AuctioneerId</c> (32-char hex uuid), resolved to <c>Players.Id</c> when <paramref name="SellerId"/> is 0</param>
public record DeleteCandidate(int Id, long UId, string Tag, DateTime End, int? NbtDataId, int SellerId = 0, string AuctioneerId = null);

/// <summary>The bookkeeping columns of one <c>archived_months</c> row that the retention gates read.</summary>
public record ArchivedMonthRowState(ushort TagId, uint Rows, uint? IndexRows, DateTime? VerifiedAt, DateTime ArchivedAt, DateTime? DeletedAt, DateTime? ScyllaAt = null, string Note = null);

/// <summary>A tag-month row as the count reconcile needs it: <c>archived_months.source</c> (mariadb|scylla|merged), rows and rows already deleted.</summary>
public record ReconcileTagRow(string Tag, string Source, long Rows, long DeletedRows);

/// <summary>A tag of a month with its part count and the combined hash <c>archived_months.sha256</c> (null when unknown).</summary>
public record RetentionTagParts(string Tag, int Parts, string Sha256);

/// <summary>Result of the MariaDB health gate.</summary>
public enum HealthStatus { Ok, Exceeded, Unavailable }

/// <summary>Health gate result with a human readable reason.</summary>
public record HealthResult(HealthStatus Status, string Reason);

/// <summary>The replication state of the read replica as <c>SHOW SLAVE STATUS</c> reports it (null columns stay null).</summary>
public record ReplicaStatus(string IoRunning, string SqlRunning, double? SecondsBehind);

/// <summary>How deleting one month ended.</summary>
public enum MonthDeleteOutcome { Completed, HealthStopped, Aborted }

/// <summary>What the retention loop does with a month.</summary>
internal enum MonthReadiness { Delete, Skip, WaitForScylla, BackedOff }

/// <summary>
/// The per tag state one consistent read of the replica returned: the <c>archived_months</c> tag rows and the <c>Auctions</c> count per tag of the month,
/// both from the same replicated point in time.
/// </summary>
public record CountSnapshot(IReadOnlyList<ReconcileTagRow> Rows, IReadOnlyDictionary<string, long> Counts);

/// <summary>One <c>archived_months</c> tag row joined with its tag name (result of <see cref="AuctionRetentionWorker.ReconcileRowsQuery"/>).</summary>
public class ReconcileRowResult
{
    public int TagId { get; set; }
    public string Tag { get; set; }
    public string Source { get; set; }
    public long Rows { get; set; }
    public long DeletedRows { get; set; }
}

/// <summary>One group of <see cref="AuctionRetentionWorker.CountByTagQuery"/>.</summary>
public class TagCount
{
    public string Tag { get; set; }
    public long Count { get; set; }
}

/// <summary>What the primary should still hold for one tag of a month: archived by phase 1 minus already deleted.</summary>
internal record TagExpectation(string Tag, long PhaseOneRows, long DeletedRows)
{
    public long Expected => PhaseOneRows - DeletedRows;
}

/// <summary>
/// One DELETE of a batch. With <see cref="KeepIfReferencedByTable"/> rows that are still referenced
/// (shared <c>NbtData</c>) are kept via <c>NOT EXISTS</c>. <see cref="TagId"/> marks the <c>Auctions</c> delete of one tag: its affected rows
/// are what is added to that tag's <c>archived_months.deleted_rows</c> in the same transaction.
/// </summary>
public sealed record DeleteStatement(string Table, string Column, IReadOnlyList<long> Ids, string KeepIfReferencedByTable = null, string KeepIfReferencedByColumn = null, ushort? TagId = null)
{
    /// <summary>SQL with the id list inlined as invariant integers (no user input can reach it).</summary>
    public string ToSql()
    {
        var ids = string.Join(",", Ids.Select(i => i.ToString(CultureInfo.InvariantCulture)));
        var sql = $"DELETE FROM `{Table}` WHERE `{Column}` IN ({ids})";
        if (KeepIfReferencedByTable != null)
            sql += $" AND NOT EXISTS (SELECT 1 FROM `{KeepIfReferencedByTable}` r WHERE r.`{KeepIfReferencedByColumn}` = `{Table}`.`{Column}`)";
        return sql;
    }
}

/// <summary>
/// Deletes MariaDB auction rows (children first, then <c>Auctions</c>, then unreferenced <c>NbtData</c>) of months that are
/// older than <c>ARCHIVE:WINDOW_YEARS</c> and fully archived (month sentinel plus every tag row verified with
/// <c>index_rows = rows</c>), only with <c>ARCHIVE:DELETE_MARIADB=true</c>. Order per month: health and replica gate, per tag count reconcile,
/// backup receipt, uncached re-read of every part (ETag compared with the receipt), then per batch: health and replica gate, safety check,
/// seller rows, one transaction with the deletes AND the <c>archived_months.deleted_rows</c> bookkeeping. At most <c>MAX_DELETE_MONTHS_PER_DAY</c>
/// months per UTC day. <c>OPTIMIZE TABLE</c> stays an operator step. The permanent index tables are never deleted from.
/// </summary>
public class AuctionRetentionWorker : BackgroundService
{
    private readonly IAuctionBlobStore store;
    private readonly IConfiguration config;
    private readonly ILogger<AuctionRetentionWorker> logger;
    private readonly IArchiveMetaStore meta;
    private readonly IArchiveObjectProbe probe;

    private static readonly Prometheus.Counter rowsDeleted = Prometheus.Metrics.CreateCounter("sky_indexer_retention_rows_deleted", "MariaDB rows deleted by the retention worker", "table");
    private static readonly Prometheus.Counter sellerRows = Prometheus.Metrics.CreateCounter("sky_indexer_retention_seller_rows", "auction_sellers rows written by the retention worker before a batch was deleted");
    private static readonly Prometheus.Counter sellerPassRows = Prometheus.Metrics.CreateCounter("sky_indexer_archive_seller_rows_backfilled", "auction_sellers rows written from the archived parts by the retention pass");
    private static readonly Prometheus.Counter sellerPassParts = Prometheus.Metrics.CreateCounter("sky_indexer_retention_seller_pass_parts", "Archived parts read back (verified and decoded once) by the retention seller pass");
    private static readonly Prometheus.Counter sellerPassMonths = Prometheus.Metrics.CreateCounter("sky_indexer_archive_seller_backfill_months_done", "Months whose archived parts were fully read back for seller rows");
    private static readonly Prometheus.Counter waitingForScylla = Prometheus.Metrics.CreateCounter("sky_indexer_retention_waiting_for_scylla_import", "Retention runs stopped at a month whose sentinel has no scylla_at yet");
    private static readonly Prometheus.Counter countWindowSplits = Prometheus.Metrics.CreateCounter("sky_indexer_retention_count_window_splits", "Per tag count windows split in half after a statement timeout");
    private static readonly Prometheus.Counter batches = Prometheus.Metrics.CreateCounter("sky_indexer_retention_batches", "Delete batches committed");
    private static readonly Prometheus.Counter monthsDeleted = Prometheus.Metrics.CreateCounter("sky_indexer_retention_months_deleted", "Months whose MariaDB rows were fully deleted");
    private static readonly Prometheus.Counter healthStops = Prometheus.Metrics.CreateCounter("sky_indexer_retention_health_stops", "Delete runs stopped by the binlog / purge lag / replica gate");
    private static readonly Prometheus.Counter aborts = Prometheus.Metrics.CreateCounter("sky_indexer_retention_aborts", "Months aborted by the safety check (unverified tag, missing index row, no progress)");
    private static readonly Prometheus.Counter backoffSkips = Prometheus.Metrics.CreateCounter("sky_indexer_retention_backoff_skips", "Retention runs stopped at a month that is backing off after an abort");
    private static readonly Prometheus.Gauge safetyDisabled = Prometheus.Metrics.CreateGauge("sky_indexer_retention_safety_disabled", "1 while a retention safety gate is switched off by configuration", "gate");

    /// <summary>DI constructor.</summary>
    /// <param name="meta">Scylla month manifests, needed to tell phase-1 rows from Scylla-only rows in the count reconcile.</param>
    /// <param name="probe">Raw receipt read, block HEAD and block GET with ETag for the backup-receipt gate.</param>
    public AuctionRetentionWorker(IAuctionBlobStore store, IConfiguration config, ILogger<AuctionRetentionWorker> logger, IArchiveMetaStore meta = null, IArchiveObjectProbe probe = null)
    {
        this.store = store;
        this.config = config;
        this.logger = logger;
        this.meta = meta;
        this.probe = probe;
    }

    /// <inheritdoc/>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = ArchiveOptions.From(config);
        if (!options.Enabled)
            return;
        try
        {
            await Delay(TimeSpan.FromSeconds(options.StartDelaySeconds), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
                await Delay(TimeSpan.FromMinutes(ArchiveOptions.From(config).IdlePauseMinutes), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Auction retention run failed, retrying after backoff");
                if (!await TryBackoffAsync(stoppingToken).ConfigureAwait(false))
                    return;
            }
        }
    }

    private async Task<bool> TryBackoffAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Delay(TimeSpan.FromMinutes(15), stoppingToken).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    /// <summary>Names of the safety gates that configuration has switched off.</summary>
    internal static IReadOnlyList<string> DisabledGates(ArchiveOptions options)
    {
        var disabled = new List<string>();
        if (!options.RequireBackupReceipt)
            disabled.Add("backup_receipt");
        if (!options.RequireScyllaImport)
            disabled.Add("scylla_import");
        if (!options.RequireReplicaHealthy)
            disabled.Add("replica_healthy");
        if (!options.RequireHealthChecks)
            disabled.Add("health_checks");
        return disabled;
    }

    /// <summary>Current value of the <c>sky_indexer_retention_safety_disabled</c> gauge of a gate.</summary>
    internal static double SafetyDisabledValue(string gate) => safetyDisabled.WithLabels(gate).Value;

    private static readonly string[] AllGates = { "backup_receipt", "scylla_import", "replica_healthy", "health_checks" };

    /// <summary>Sets the <c>sky_indexer_retention_safety_disabled</c> gauge of every gate and warns loudly when deleting is enabled with a gate off.</summary>
    private void PublishSafetyGates(ArchiveOptions options)
    {
        var disabled = DisabledGates(options);
        foreach (var gate in AllGates)
            safetyDisabled.WithLabels(gate).Set(disabled.Contains(gate) ? 1 : 0);
        if (options.DeleteMariaDb && disabled.Count > 0)
            logger.LogWarning("Retention: SAFETY GATES DISABLED by configuration while ARCHIVE:DELETE_MARIADB=true: {Gates}. Production rows are deleted without these checks", string.Join(", ", disabled));
    }

    /// <summary>One run; returns the number of months completed.</summary>
    internal async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
    {
        var options = ArchiveOptions.From(config);
        if (!options.Enabled)
            return 0;
        PublishSafetyGates(options);
        if (!options.DeleteMariaDb)
            return 0;
        if (store == null || !store.IsEnabled)
        {
            logger.LogWarning("Retention: the S3 archive store is not configured; nothing is deleted without the read-back of the archived parts");
            return 0;
        }
        var dayStart = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, 0, 0, 0, DateTimeKind.Utc);
        var doneToday = await CountMonthsDeletedSinceAsync(dayStart, cancellationToken).ConfigureAwait(false);
        var firstInWindow = ArchiveMonths.OldestInWindow(nowUtc, options.WindowYears);
        var completed = 0;
        for (var month = options.OldestMonth; month < firstInWindow; month = ArchiveMonths.Next(month))
        {
            if (doneToday + completed >= options.MaxDeleteMonthsPerDay)
                break;
            var readiness = await GetReadinessAsync(month, options, nowUtc, cancellationToken).ConfigureAwait(false);
            if (readiness == MonthReadiness.WaitForScylla || readiness == MonthReadiness.BackedOff)
                break;
            if (readiness == MonthReadiness.Skip)
                continue;
            var (outcome, deleted) = await DeleteMonthAsync(month, options, nowUtc, cancellationToken).ConfigureAwait(false);
            if (outcome != MonthDeleteOutcome.Completed)
                break;
            if (deleted > 0)
                completed++;
        }
        return completed;
    }

    private async Task<MonthReadiness> GetReadinessAsync(int month, ArchiveOptions options, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var rows = await GetArchivedMonthRowsAsync(month, cancellationToken).ConfigureAwait(false);
        var sentinel = rows.FirstOrDefault(r => r.TagId == 0);
        if (sentinel?.DeletedAt != null)
            return MonthReadiness.Skip;
        if (!IsFullyArchived(rows))
        {
            logger.LogDebug("Retention: {Month} is not fully archived and verified yet", month);
            return MonthReadiness.Skip;
        }
        if (IsBackingOff(sentinel, nowUtc))
            return MonthReadiness.BackedOff;
        if (options.RequireScyllaImport && sentinel.ScyllaAt == null)
        {
            // not an error: the SkyAuctions sweep + SkyIndexer import of this month has not finished; later months wait too (oldest first)
            waitingForScylla.Inc();
            logger.LogInformation("Retention: {Month} waits for the Scylla import (sentinel has no scylla_at); nothing is read or deleted", month);
            return MonthReadiness.WaitForScylla;
        }
        return MonthReadiness.Delete;
    }

    /// <summary>True while the sentinel's note carries a backoff that has not expired yet (an earlier abort of the month); logs and counts the skip.</summary>
    private bool IsBackingOff(ArchivedMonthRowState sentinel, DateTime nowUtc)
    {
        var until = BackoffUntil(sentinel.Note);
        if (until == null || until <= nowUtc)
            return false;
        backoffSkips.Inc();
        logger.LogWarning("Retention: a month aborted earlier backs off until {Until:u}; it and the later months wait (clear the sentinel note to retry now): {Note}", until, sentinel.Note);
        return true;
    }

    /// <summary>Prefix of the <c>archived_months</c> sentinel <c>note</c> that persists the backoff after an aborted month (no schema change, survives restarts).</summary>
    internal const string BackoffNotePrefix = "retention_backoff_until=";

    /// <summary>Instant until which the note asks to leave the month alone, null when the note is no backoff note.</summary>
    internal static DateTime? BackoffUntil(string note)
    {
        if (note == null || !note.StartsWith(BackoffNotePrefix, StringComparison.Ordinal))
            return null;
        var text = note.Substring(BackoffNotePrefix.Length).Split(';')[0];
        var parsed = DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var until);
        return parsed ? until : null;
    }

    /// <summary>The backoff note (fits the 255 char column): the deadline, then the abort reason.</summary>
    internal static string BuildBackoffNote(DateTime untilUtc, string problem)
    {
        var note = BackoffNotePrefix + untilUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) + "; " + problem;
        return note.Length <= 255 ? note : note.Substring(0, 255);
    }

    /// <summary>Gate: month sentinel archived and every tag row verified with as many index rows as archived rows.</summary>
    internal static bool IsFullyArchived(IReadOnlyCollection<ArchivedMonthRowState> rows)
    {
        if (rows == null || !rows.Any(r => r.TagId == 0))
            return false;
        return rows.Where(r => r.TagId != 0).All(r => r.VerifiedAt != null && r.IndexRows == r.Rows);
    }

    /// <summary>Delete all MariaDB rows of one fully archived month in batches.</summary>
    internal async Task<(MonthDeleteOutcome outcome, long deleted)> DeleteMonthAsync(int month, ArchiveOptions options, DateTime nowUtc, CancellationToken cancellationToken)
    {
        // cheap operational gates first: the month gates below read the primary and R2 for minutes to hours
        var stopReason = await OperationalStopReasonAsync(options, cancellationToken).ConfigureAwait(false);
        if (stopReason != null)
            return HealthStop(month, stopReason, 0);
        var gateProblem = await MonthGateProblemAsync(month, options, cancellationToken).ConfigureAwait(false);
        if (gateProblem != null)
            return await AbortAsync(month, "nothing deleted", gateProblem, 0, options, nowUtc, cancellationToken).ConfigureAwait(false);
        return await DeleteBatchesAsync(month, options, nowUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Counts and logs the abort and backs the month off for <c>ABORT_BACKOFF_HOURS</c> so the expensive gates do not repeat on every run.</summary>
    private async Task<(MonthDeleteOutcome, long)> AbortAsync(int month, string what, string problem, long deleted, ArchiveOptions options, DateTime nowUtc, CancellationToken cancellationToken)
    {
        aborts.Inc();
        logger.LogError("Retention: aborting {Month}, {What}: {Problem}", month, what, problem);
        await TryBackOffAsync(month, problem, options, nowUtc, cancellationToken).ConfigureAwait(false);
        return (MonthDeleteOutcome.Aborted, deleted);
    }

    private async Task TryBackOffAsync(int month, string problem, ArchiveOptions options, DateTime nowUtc, CancellationToken cancellationToken)
    {
        if (options.AbortBackoffHours <= 0)
            return;
        var until = nowUtc.AddHours(options.AbortBackoffHours);
        try
        {
            var stored = await SetMonthBackoffAsync(month, BuildBackoffNote(until, problem), cancellationToken).ConfigureAwait(false);
            if (!stored)
                logger.LogWarning("Retention: the backoff of {Month} could not be stored because an operator note is present on the month sentinel; the month is retried on every run", month);
            else
                logger.LogWarning("Retention: {Month} backs off until {Until:u} ({Hours} h)", month, until, options.AbortBackoffHours);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Retention: could not persist the backoff of {Month}; the next run retries it", month);
        }
    }

    private (MonthDeleteOutcome, long) HealthStop(int month, string reason, long deleted)
    {
        healthStops.Inc();
        logger.LogWarning("Retention: health gate stopped deleting {Month}: {Reason}", month, reason);
        return (MonthDeleteOutcome.HealthStopped, deleted);
    }

    /// <summary>
    /// The month level gates, in this order: 1. the backup receipt against the current R2 state, 2. the per tag row count (read from the replica in one
    /// consistent snapshot, see <see cref="ReadCountSnapshotAsync"/>) against what phase 1 archived, 3. every archived part re-read uncached, hash-verified and (with a receipt) ETag-compared. Without a seller backfill marker the
    /// same decode also derives the seller rows of EVERY archived auction (MariaDB rows and Scylla-only/merged parts alike). All of them run on every
    /// start of the month, also when a crashed run is resumed. A month without MariaDB rows left still passes through here once. Null when all passed.
    /// </summary>
    private async Task<string> MonthGateProblemAsync(int month, ArchiveOptions options, CancellationToken cancellationToken)
    {
        BackupReceipt receipt = null;
        try
        {
            // cheapest first: the receipt (one small R2 read plus HEADs) before the per tag count over the whole month
            if (options.RequireBackupReceipt)
            {
                var loaded = await LoadBackupReceiptAsync(month, cancellationToken).ConfigureAwait(false);
                if (loaded.Problem != null)
                    return loaded.Problem;
                receipt = loaded.Receipt;
            }
            var countProblem = await ReplicaCountProblemAsync(month, cancellationToken).ConfigureAwait(false);
            if (countProblem != null)
                return countProblem;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "pre-delete gate failed: " + ex.GetType().Name + " " + ex.Message;
        }
        return await VerifyPartsAsync(month, options, receipt, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> VerifyPartsAsync(int month, ArchiveOptions options, BackupReceipt receipt, CancellationToken cancellationToken)
    {
        var sellerPassDone = await IsSellerPassDoneAsync(month, cancellationToken).ConfigureAwait(false);
        string problem;
        try
        {
            problem = await SellerPassAsync(month, options, !sellerPassDone, receipt, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            problem = "part verification / seller pass failed: " + ex.GetType().Name + " " + ex.Message;
        }
        if (problem != null || sellerPassDone)
            return problem;
        await MarkSellerBackfillDoneAsync(month, cancellationToken).ConfigureAwait(false);
        sellerPassMonths.Inc();
        return null;
    }

    private async Task<(MonthDeleteOutcome outcome, long deleted)> DeleteBatchesAsync(int month, ArchiveOptions options, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var (monthStart, monthEnd) = ArchiveMonths.MonthRange(month);
        var verifiedTags = await GetVerifiedTagsAsync(month, cancellationToken).ConfigureAwait(false);
        long total = 0;
        var cursor = monthStart;
        logger.LogInformation("Retention: deleting MariaDB rows of {Month}", month);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopReason = await OperationalStopReasonAsync(options, cancellationToken).ConfigureAwait(false);
            if (stopReason != null)
                return HealthStop(month, stopReason, total);
            var batch = await LoadDeleteBatchAsync(cursor, monthEnd, options.DeleteBatchSize, cancellationToken).ConfigureAwait(false);
            if (batch.Count == 0)
                break;
            var (problem, deleted) = await DeleteBatchAsync(month, batch, verifiedTags, cancellationToken).ConfigureAwait(false);
            if (problem != null)
                return await AbortAsync(month, "nothing of this batch deleted", problem, total, options, nowUtc, cancellationToken).ConfigureAwait(false);
            total += deleted;
            // deleted rows vanish from the End range, so the cursor only has to move forward to the last End seen
            cursor = batch.Max(b => b.End);
            await Delay(TimeSpan.FromMilliseconds(options.DeletePauseMs), cancellationToken).ConfigureAwait(false);
        }
        return await CompleteMonthAsync(month, total, options, nowUtc, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One batch: safety check, seller rows, then the deletes with the <c>deleted_rows</c> bookkeeping in ONE transaction (see <see cref="ExecuteDeleteAsync"/>).
    /// A problem text means nothing of the batch was deleted.
    /// </summary>
    private async Task<(string problem, long deleted)> DeleteBatchAsync(int month, IReadOnlyList<DeleteCandidate> batch, IReadOnlyDictionary<string, ushort> verifiedTags, CancellationToken cancellationToken)
    {
        var problem = await SafetyProblemAsync(ArchiveMonths.ToIndex(month), batch, verifiedTags, cancellationToken).ConfigureAwait(false);
        if (problem != null)
            return (problem, 0);
        problem = await WriteBatchSellersAsync(batch, cancellationToken).ConfigureAwait(false);
        if (problem != null)
            return (problem, 0);
        var counts = await ExecuteDeleteAsync(BuildDeleteStatements(batch, verifiedTags), month, cancellationToken).ConfigureAwait(false);
        foreach (var (table, count) in counts)
            rowsDeleted.WithLabels(table).Inc(count);
        var auctionsDeleted = counts.GetValueOrDefault("Auctions");
        if (auctionsDeleted == 0)
            return ("a batch deleted no auction rows; aborting to avoid a loop", 0);
        batches.Inc();
        return (null, auctionsDeleted);
    }

    /// <summary>
    /// Seller index first: the MariaDB row is the only source of <c>Auctions.SellerId</c>, so no batch is deleted before its auction_sellers rows are
    /// committed (INSERT IGNORE, safe to repeat after a crash between the insert and the delete). Returns a problem text on failure.
    /// </summary>
    private async Task<string> WriteBatchSellersAsync(IReadOnlyList<DeleteCandidate> batch, CancellationToken cancellationToken)
    {
        try
        {
            var unnumbered = batch.Where(b => b.SellerId <= 0 && ScyllaArchiveImportWorker.IsIndexableSeller(b.AuctioneerId)).Select(b => b.AuctioneerId).ToList();
            var ids = unnumbered.Count == 0
                ? new Dictionary<string, int>()
                : await PlayerIdLookup.ResolveAsync(unnumbered, ResolvePlayerIdsAsync, cancellationToken).ConfigureAwait(false);
            var sellers = BuildSellerRows(batch, ids);
            await WriteSellerRowsAsync(sellers, cancellationToken).ConfigureAwait(false);
            sellerRows.Inc(sellers.Count);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "writing the auction_sellers rows failed: " + ex.GetType().Name + " " + ex.Message;
        }
    }

    /// <summary>
    /// Closes the month. The per tag reconcile runs once more against the now empty range (on the primary: a lagging replica would still show the just deleted rows): it only passes when the primary holds no row of the month
    /// AND the recorded <c>deleted_rows</c> of every tag add up to what phase 1 archived (nothing was deleted that was not booked, nothing was booked twice).
    /// </summary>
    private async Task<(MonthDeleteOutcome outcome, long deleted)> CompleteMonthAsync(int month, long total, ArchiveOptions options, DateTime nowUtc, CancellationToken cancellationToken)
    {
        var (monthStart, monthEnd) = ArchiveMonths.MonthRange(month);
        var problem = await CountProblemAsync(month, monthStart, monthEnd, cancellationToken).ConfigureAwait(false);
        if (problem != null)
            return await AbortAsync(month, "the month is not marked deleted", "final reconcile failed: " + problem, total, options, nowUtc, cancellationToken).ConfigureAwait(false);
        await MarkMonthDeletedAsync(month, cancellationToken).ConfigureAwait(false);
        monthsDeleted.Inc();
        logger.LogInformation("Retention: deleted {Rows} auctions of {Month}", total, month);
        await LogSpaceHintAsync(cancellationToken).ConfigureAwait(false);
        return (MonthDeleteOutcome.Completed, total);
    }

    /// <summary>
    /// The cheap operational gates: binlog / purge lag (stops on exceeded, and on unavailable while <c>REQUIRE_HEALTH_CHECKS</c>) and the replica
    /// (<c>REQUIRE_REPLICA_HEALTHY</c>, evaluated independent of the health checks). Returns the reason to stop, null when deleting may go on.
    /// </summary>
    internal async Task<string> OperationalStopReasonAsync(ArchiveOptions options, CancellationToken cancellationToken)
    {
        var health = await CheckHealthAsync(options, cancellationToken).ConfigureAwait(false);
        if (health.Status == HealthStatus.Exceeded || health.Status == HealthStatus.Unavailable && options.RequireHealthChecks)
            return health.Reason;
        if (!options.RequireReplicaHealthy)
            return null;
        var (status, unreadableReason) = await ReadReplicaStatusOrReasonAsync(cancellationToken).ConfigureAwait(false);
        var replica = EvaluateReplica(status, options, unreadableReason);
        return replica.Status == HealthStatus.Ok ? null : replica.Reason;
    }

    /// <summary>
    /// Read every part of every tag-month once (uncached), verify it against the R2 tag index (and the combined hash against
    /// <c>archived_months.sha256</c>) and INSERT IGNORE the seller rows of all its auctions. Returns a problem text, null when the month is done.
    /// A part is written only after its hash matched, so rows never come from a corrupted blob. With <paramref name="writeSellers"/> false (the
    /// backfill marker exists) the seller rows are not derived again, but every part is still re-read uncached and hash-checked: the hash
    /// verification is never skipped, it must be fresh right before a month's first delete batch. With a <paramref name="receipt"/> the ETag of the
    /// GET response of every part must equal the receipt's, so the bytes decoded here are the bytes that were backed up (no HEAD/GET gap).
    /// </summary>
    private async Task<string> SellerPassAsync(int month, ArchiveOptions options, bool writeSellers, BackupReceipt receipt, CancellationToken cancellationToken)
    {
        long rows = 0;
        var parts = 0;
        foreach (var tag in (await GetMonthPartsAsync(month, cancellationToken).ConfigureAwait(false)).OrderBy(t => t.Tag, StringComparer.Ordinal))
        {
            var (problem, tagRows) = await VerifyTagPartsAsync(tag, month, options, writeSellers, receipt, cancellationToken).ConfigureAwait(false);
            if (problem != null)
                return problem;
            rows += tagRows;
            parts += tag.Parts;
        }
        logger.LogInformation("Retention: {Month} verified {Parts} archived parts uncached; seller pass {Mode}, {Rows} seller rows written", month, parts, writeSellers ? "ran" : "skipped (marker exists)", rows);
        return null;
    }

    private async Task<(string problem, long sellerRows)> VerifyTagPartsAsync(RetentionTagParts tag, int month, ArchiveOptions options, bool writeSellers, BackupReceipt receipt, CancellationToken cancellationToken)
    {
        var index = await store.ReadIndexAsync(tag.Tag, cancellationToken).ConfigureAwait(false);
        var entry = index?.Find(month);
        if (entry == null || entry.PartSha256.Count < tag.Parts)
            return ($"{tag.Tag} has {tag.Parts} parts in archived_months but the R2 tag index lists {entry?.PartSha256.Count ?? 0}", 0);
        var hashes = new List<string>();
        long rows = 0;
        for (var part = 0; part < tag.Parts; part++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var (block, etag) = await ReadPartUncachedAsync(tag.Tag, month, part, receipt != null, cancellationToken).ConfigureAwait(false);
            var problem = PartProblem(tag.Tag, month, part, block, etag, entry.PartSha256[part], receipt);
            if (problem != null)
                return (problem, rows);
            hashes.Add(block.Sha256());
            sellerPassParts.Inc();
            if (writeSellers)
                rows += await WritePartSellersAsync(block, options, cancellationToken).ConfigureAwait(false);
        }
        if (tag.Sha256 != null && !string.Equals(ArchivedAuctionBlock.CombinedSha256(hashes), tag.Sha256, StringComparison.OrdinalIgnoreCase))
            return ($"the parts of {tag.Tag} no longer combine to archived_months.sha256", rows);
        return (null, rows);
    }

    private async Task<long> WritePartSellersAsync(ArchivedAuctionBlock block, ArchiveOptions options, CancellationToken cancellationToken)
    {
        var sellers = await BuildSellerRowsOfBlockAsync(block, cancellationToken).ConfigureAwait(false);
        await WriteSellerRowsAsync(sellers, cancellationToken).ConfigureAwait(false);
        sellerPassRows.Inc(sellers.Count);
        await Delay(TimeSpan.FromMilliseconds(options.DeletePauseMs), cancellationToken).ConfigureAwait(false);
        return sellers.Count;
    }

    /// <summary>Uncached read of one part; with <paramref name="withETag"/> through the probe so the GET response's ETag comes back (fails closed without a probe).</summary>
    private async Task<(ArchivedAuctionBlock block, string etag)> ReadPartUncachedAsync(string tag, int month, int part, bool withETag, CancellationToken cancellationToken)
    {
        if (!withETag)
            return (await store.ReadBlockAsync(tag, month, part, true, cancellationToken).ConfigureAwait(false), null);
        var reader = probe ?? throw new InvalidOperationException("no IArchiveObjectProbe configured, the ETag of the part can not be read");
        return await reader.ReadBlockWithETagAsync(tag, month, part, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Why a part read back must not be trusted (missing, hash differs from the tag index, ETag differs from the receipt); null when fine.</summary>
    internal static string PartProblem(string tag, int month, int part, ArchivedAuctionBlock block, string etag, string indexHash, BackupReceipt receipt)
    {
        if (block == null)
            return $"part {part} of {tag} is missing from the store";
        var sha = block.Sha256();
        if (!string.Equals(sha, indexHash, StringComparison.OrdinalIgnoreCase))
            return $"part {part} of {tag} hashes to {sha}, the tag index says {indexHash}";
        return receipt == null ? null : ReceiptETagProblem(receipt, tag, month, part, etag);
    }

    private static string ReceiptETagProblem(BackupReceipt receipt, string tag, int month, int part, string etag)
    {
        var key = AuctionBlobKeys.Block(tag, month, part);
        var expected = receipt.Tags?.Find(t => string.Equals(t.Tag, tag, StringComparison.Ordinal))?.Objects?.Find(o => string.Equals(o.Key, key, StringComparison.Ordinal))?.ETag;
        if (BackupReceipt.ETagEquals(etag, expected))
            return null;
        return $"{key} was read with ETag {etag?.Trim('"') ?? "(none)"} but the backup receipt has {expected?.Trim('"') ?? "(none)"}: the object changed after the receipt check";
    }

    /// <summary>
    /// The <c>auction_sellers</c> rows of one decoded part: the auctioneer uuid of every row, anonymised/opted-out/unknown ones skipped
    /// (same rules as the other writers), resolved to <c>Players.Id</c> in chunks of 100.
    /// </summary>
    internal async Task<List<AuctionSeller>> BuildSellerRowsOfBlockAsync(ArchivedAuctionBlock block, CancellationToken cancellationToken)
    {
        var candidates = new Dictionary<(string seller, long uid), uint>();
        for (var i = 0; i < block.RowCount; i++)
        {
            var seller = ArchiveIds.Decode(block.Auctioneer[i]);
            if (!ScyllaArchiveImportWorker.IsIndexableSeller(seller))
                continue;
            candidates[(seller, block.AuctionUid[i])] = (uint)new DateTimeOffset(new DateTime(block.EndTicksAt(i), DateTimeKind.Utc)).ToUnixTimeSeconds();
        }
        var ids = await PlayerIdLookup.ResolveAsync(candidates.Keys.Select(k => k.seller), ResolvePlayerIdsAsync, cancellationToken).ConfigureAwait(false);
        return ScyllaArchiveImportWorker.ResolveSellers(candidates, ids);
    }

    /// <summary>
    /// The replica gate from its replication status: both threads (<c>Slave_IO_Running</c>, <c>Slave_SQL_Running</c>) must say <c>Yes</c> and
    /// <c>Seconds_Behind_Master</c> must be known and at most <c>MAX_REPLICA_LAG_SECONDS</c>. Anything unreadable is <see cref="HealthStatus.Unavailable"/> (stops).
    /// </summary>
    internal static HealthResult EvaluateReplica(ReplicaStatus status, ArchiveOptions options, string unreadableReason = null)
    {
        if (status == null)
            return new HealthResult(HealthStatus.Unavailable, unreadableReason ?? NoReplicaRowReason);
        if (!IsYes(status.IoRunning))
            return new HealthResult(HealthStatus.Unavailable, $"replica Slave_IO_Running is '{status.IoRunning}', not Yes");
        if (!IsYes(status.SqlRunning))
            return new HealthResult(HealthStatus.Unavailable, $"replica Slave_SQL_Running is '{status.SqlRunning}', not Yes");
        if (status.SecondsBehind == null)
            return new HealthResult(HealthStatus.Unavailable, "replica Seconds_Behind_Master is NULL");
        if (status.SecondsBehind.Value > options.MaxReplicaLagSeconds)
            return new HealthResult(HealthStatus.Exceeded, $"replica is {status.SecondsBehind.Value:F0} s behind > {options.MaxReplicaLagSeconds} s");
        return new HealthResult(HealthStatus.Ok, "ok");
    }

    private static bool IsYes(string value) => string.Equals(value, "Yes", StringComparison.OrdinalIgnoreCase);

    internal const string NoReplicaConfiguredReason = "replica status not read: no DBReadOnlyConnection configured (the archive job reads from the primary, which is not a replica)";
    internal const string NoReplicaRowReason = "replica status not read: SHOW SLAVE STATUS returned no row (the read connection is not a replica)";

    /// <summary>Thrown by <see cref="ReadReplicaStatusAsync"/> when no <c>DBReadOnlyConnection</c> is configured.</summary>
    internal sealed class ReplicaNotConfiguredException : Exception
    {
    }

    /// <summary>The replica status, or null plus the reason it is unavailable (not configured, no row, or the query failed; failures are logged at Warning).</summary>
    private async Task<(ReplicaStatus status, string reason)> ReadReplicaStatusOrReasonAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await ReadReplicaStatusAsync(cancellationToken).ConfigureAwait(false);
            return (status, status == null ? NoReplicaRowReason : null);
        }
        catch (ReplicaNotConfiguredException)
        {
            return (null, NoReplicaConfiguredReason);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Reading the replica status failed, deleting stops");
            return (null, $"replica status query failed: {ex.GetType().Name}: {ex.Message} (SHOW SLAVE STATUS needs SLAVE MONITOR / REPLICA MONITOR)");
        }
    }

    /// <summary>
    /// Per tag count reconcile: the primary's <c>Auctions</c> ending in the month, grouped by tag, must equal for EVERY tag what phase 1 archived
    /// minus what earlier runs already deleted (the deletes book <c>deleted_rows</c> in their own transaction, so there is no tolerance). More rows than
    /// expected mean rows missing from the archive, fewer mean rows vanished. Null when every tag matches (also after the last batch, when all are 0).
    /// </summary>
    internal async Task<string> CountProblemAsync(int month, DateTime monthStart, DateTime monthEnd, CancellationToken cancellationToken)
    {
        var rows = await GetReconcileRowsAsync(month, cancellationToken).ConfigureAwait(false);
        var (expectations, problem) = await ReadExpectationsAsync(month, rows, cancellationToken).ConfigureAwait(false);
        if (problem != null)
            return problem;
        var actual = await CountPrimaryAuctionsByTagAsync(monthStart, monthEnd, cancellationToken).ConfigureAwait(false);
        return FindCountMismatch(expectations, actual);
    }

    /// <summary>
    /// The same reconcile for the pre-delete gate, but read from the replica: the expectations and the (month long, GROUP BY over ~18 M rows) count come from
    /// ONE read only snapshot, so they are consistent with each other (a delete and its <c>deleted_rows</c> booking commit atomically on the primary and
    /// replicate together). The replica gate bounds the lag before this runs.
    /// </summary>
    internal async Task<string> ReplicaCountProblemAsync(int month, CancellationToken cancellationToken)
    {
        var (monthStart, monthEnd) = ArchiveMonths.MonthRange(month);
        var snapshot = await ReadCountSnapshotAsync(month, monthStart, monthEnd, cancellationToken).ConfigureAwait(false);
        var (expectations, problem) = await ReadExpectationsAsync(month, snapshot.Rows, cancellationToken).ConfigureAwait(false);
        return problem ?? FindCountMismatch(expectations, snapshot.Counts);
    }

    /// <summary>
    /// Phase-1 rows of a tag are the tag row's <c>rows</c> of source <c>mariadb</c>; for <c>merged</c> rows the Scylla-only rows (<c>NewRows</c> of the month's
    /// Scylla manifest) are subtracted, <c>scylla</c> rows have no MariaDB counterpart.
    /// </summary>
    private async Task<(Dictionary<string, TagExpectation> expectations, string problem)> ReadExpectationsAsync(int month, IReadOnlyList<ReconcileTagRow> rows, CancellationToken cancellationToken)
    {
        var expectations = new Dictionary<string, TagExpectation>(StringComparer.OrdinalIgnoreCase);
        ScyllaMonthManifest manifest = null;
        foreach (var row in rows)
        {
            if (row.Source == ArchivedAuctionBlock.SourceMerged && manifest == null)
                manifest = await ReadScyllaManifestAsync(month, cancellationToken).ConfigureAwait(false);
            var (phaseOne, problem) = PhaseOneRows(row, manifest);
            if (problem != null)
                return (expectations, problem);
            expectations[row.Tag] = new TagExpectation(row.Tag, phaseOne, row.DeletedRows);
        }
        return (expectations, null);
    }

    private async Task<ScyllaMonthManifest> ReadScyllaManifestAsync(int month, CancellationToken cancellationToken)
        => meta == null ? null : await meta.ReadMetaAsync<ScyllaMonthManifest>(AuctionBlobKeys.ScyllaManifest(month), cancellationToken).ConfigureAwait(false);

    private (long phaseOne, string problem) PhaseOneRows(ReconcileTagRow row, ScyllaMonthManifest manifest)
    {
        switch (row.Source)
        {
            case ArchivedAuctionBlock.SourceMariaDb:
                return (row.Rows, null);
            case ArchivedAuctionBlock.SourceScylla:
                return (0, null);
            case ArchivedAuctionBlock.SourceMerged:
                if (meta == null)
                    return (0, $"{row.Tag} is a merged tag-month but no Scylla manifest reader is configured, phase-1 rows can not be separated");
                var entry = manifest?.Find(row.Tag);
                if (entry == null || entry.NewRows > row.Rows)
                    return (0, $"{row.Tag} is a merged tag-month but the Scylla manifest of the month has no usable entry for it, phase-1 rows can not be separated");
                return (row.Rows - entry.NewRows, null);
            default:
                return (0, $"{row.Tag} has unknown archived_months source '{row.Source}'");
        }
    }

    private static string FindCountMismatch(IReadOnlyDictionary<string, TagExpectation> expectations, IReadOnlyDictionary<string, long> actual)
    {
        var tags = expectations.Keys.Concat(actual.Keys).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(t => t, StringComparer.Ordinal);
        foreach (var tag in tags)
        {
            var expectation = expectations.GetValueOrDefault(tag) ?? new TagExpectation(tag, 0, 0);
            var count = actual.Where(kv => string.Equals(kv.Key, tag, StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).DefaultIfEmpty(0).Sum();
            if (count != expectation.Expected)
                return $"tag '{tag}': the primary has {count} auctions ending in the month, phase 1 archived {expectation.PhaseOneRows} (already deleted {expectation.DeletedRows}, expected {expectation.Expected})";
        }
        return null;
    }

    /// <summary>
    /// The backup receipt <c>auctions/v2/_meta/backup/{yyyy-MM}.json</c> must exist, name a snapshot and cover the current R2 state of every tag of the
    /// month exactly: same parts, rows and part hashes as the live tag index entry, and every block's current ETag (HEAD) equal to the receipt's.
    /// Parts added or rewritten after the backup (Scylla import, privacy erasure) therefore block the month until the NAS is re-mirrored.
    /// </summary>
    internal async Task<string> BackupReceiptProblemAsync(int month, CancellationToken cancellationToken)
        => (await LoadBackupReceiptAsync(month, cancellationToken).ConfigureAwait(false)).Problem;

    private async Task<(BackupReceipt Receipt, string Problem)> LoadBackupReceiptAsync(int month, CancellationToken cancellationToken)
    {
        var key = BackupReceipt.Key(month);
        var text = await ReadBackupReceiptAsync(month, cancellationToken).ConfigureAwait(false);
        if (text == null)
            return (null, $"no backup receipt at {key}");
        BackupReceipt receipt;
        try
        {
            receipt = BackupReceipt.Parse(text);
        }
        catch (FormatException ex)
        {
            return (null, $"invalid backup receipt {key}: {ex.Message}");
        }
        var problem = await FindReceiptCoverageProblemAsync(receipt, month, cancellationToken).ConfigureAwait(false);
        return problem == null ? (receipt, null) : (null, "backup receipt does not cover the current archive: " + problem);
    }

    private async Task<string> FindReceiptCoverageProblemAsync(BackupReceipt receipt, int month, CancellationToken cancellationToken)
    {
        var current = new List<(string Tag, ArchivedMonthEntry Entry)>();
        foreach (var tag in (await GetMonthPartsAsync(month, cancellationToken).ConfigureAwait(false)).OrderBy(t => t.Tag, StringComparer.Ordinal))
        {
            var entry = (await store.ReadIndexAsync(tag.Tag, cancellationToken).ConfigureAwait(false))?.Find(month);
            if (entry != null && entry.Parts != tag.Parts)
                return $"{tag.Tag} has {tag.Parts} parts in archived_months but {entry.Parts} in the R2 tag index";
            current.Add((tag.Tag, entry));
        }
        return await receipt.FindProblemAsync(month, current, k => GetObjectETagAsync(k, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<string> SafetyProblemAsync(ushort monthIndex, IReadOnlyList<DeleteCandidate> batch, IReadOnlyDictionary<string, ushort> verifiedTags, CancellationToken cancellationToken)
    {
        // the tag of every row must have a verified archived_months row for the month; a row without a tag can never be covered
        var unverified = batch.Where(b => string.IsNullOrEmpty(b.Tag) || !verifiedTags.ContainsKey(b.Tag)).ToList();
        if (unverified.Count > 0)
            return "auctions whose tag has no verified archived_months row (id/tag): " + string.Join(",", unverified.Take(10).Select(u => u.Id + "/" + u.Tag));
        var uids = batch.Select(b => b.UId).Distinct().ToList();
        var located = await CountLocatedUidsAsync(monthIndex, uids, cancellationToken).ConfigureAwait(false);
        if (located != uids.Count)
            return $"{uids.Count - located} of {uids.Count} uids have no auction_locations row for this month";
        return null;
    }

    /// <summary>
    /// <c>auction_sellers</c> rows of a batch. A row with <c>SellerId</c> 0 falls back to <paramref name="resolvedAuctioneers"/>
    /// (uuid to <c>Players.Id</c>); anonymised or opted-out uuids (never looked up, also when <c>SellerId</c> is set) and uuids without a <c>Players</c> row get no row, like bidders.
    /// </summary>
    internal static List<AuctionSeller> BuildSellerRows(IReadOnlyList<DeleteCandidate> batch, IReadOnlyDictionary<string, int> resolvedAuctioneers = null)
    {
        var rows = new List<AuctionSeller>(batch.Count);
        foreach (var b in batch)
        {
            // opted-out / anonymised auctioneers get no row, whatever SellerId says
            if (!ScyllaArchiveImportWorker.IsIndexableSeller(b.AuctioneerId))
                continue;
            var id = b.SellerId;
            if (id <= 0 && resolvedAuctioneers != null)
                resolvedAuctioneers.TryGetValue(b.AuctioneerId, out id);
            if (id <= 0)
                continue;
            rows.Add(new AuctionSeller
            {
                SellerId = (uint)id,
                Uid = b.UId,
                End = (uint)new DateTimeOffset(DateTime.SpecifyKind(b.End, DateTimeKind.Utc)).ToUnixTimeSeconds(),
            });
        }
        return rows;
    }

    /// <summary>
    /// The ordered DELETE statements of one batch: children, then Auctions (one statement per tag when <paramref name="tagIds"/> is given, so the
    /// affected rows of each tag can be booked), then NbtData no longer referenced.
    /// </summary>
    internal static List<DeleteStatement> BuildDeleteStatements(IReadOnlyList<DeleteCandidate> batch, IReadOnlyDictionary<string, ushort> tagIds = null)
    {
        var ids = batch.Select(b => (long)b.Id).Distinct().ToList();
        var nbtIds = batch.Where(b => b.NbtDataId != null).Select(b => (long)b.NbtDataId.Value).Distinct().ToList();
        var statements = new List<DeleteStatement>
        {
            new("Bids", "Uuid", ids),
            new("NBTLookups", "AuctionId", ids),
            new("Enchantment", "SaveAuctionId", ids),
            new("UuId", "SaveAuctionId", ids),
            new("UuId", "SaveAuctionId1", ids),
        };
        statements.AddRange(BuildAuctionStatements(batch, tagIds));
        if (nbtIds.Count > 0)
            statements.Add(new DeleteStatement("NbtData", "Id", nbtIds, "Auctions", "NbtDataId"));
        return statements;
    }

    private static IEnumerable<DeleteStatement> BuildAuctionStatements(IReadOnlyList<DeleteCandidate> batch, IReadOnlyDictionary<string, ushort> tagIds)
    {
        if (tagIds == null)
            return new[] { new DeleteStatement("Auctions", "Id", batch.Select(b => (long)b.Id).Distinct().ToList()) };
        return batch.GroupBy(b => b.Tag, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => new DeleteStatement("Auctions", "Id", g.Select(b => (long)b.Id).Distinct().ToList(), TagId: tagIds[g.Key]));
    }

    /// <summary>Evaluate the health gate from measured values (null = could not be measured).</summary>
    internal static HealthResult EvaluateHealth(long? binlogBytes, long? historyListLength, ArchiveOptions options)
    {
        if (binlogBytes == null)
            return new HealthResult(HealthStatus.Unavailable, "binary log size could not be read (missing privilege?)");
        if (historyListLength == null)
            return new HealthResult(HealthStatus.Unavailable, "trx_rseg_history_len could not be read (missing PROCESS privilege or metric disabled)");
        var binlogGb = binlogBytes.Value / 1e9;
        if (binlogGb > options.MaxBinlogGb)
            return new HealthResult(HealthStatus.Exceeded, $"binary logs {binlogGb:F1} GB > {options.MaxBinlogGb} GB");
        if (historyListLength.Value > options.MaxHistoryListLength)
            return new HealthResult(HealthStatus.Exceeded, $"history list length {historyListLength} > {options.MaxHistoryListLength}");
        return new HealthResult(HealthStatus.Ok, "ok");
    }

    // ---------------------------------------------------------------- data access seams

    /// <summary>The tag rows of a month with source, rows and deleted rows (tag names resolved through <c>auction_tags</c>).</summary>
    protected virtual async Task<IReadOnlyList<ReconcileTagRow>> GetReconcileRowsAsync(int month, CancellationToken cancellationToken)
    {
        var index = ArchiveMonths.ToIndex(month);
        using var db = new ArchiveContext();
        var rows = await db.ArchivedMonths.AsNoTracking().Where(m => m.Month == index && m.TagId != 0).Select(m => new { m.TagId, m.Source, m.Rows, m.DeletedRows }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tags = await db.AuctionTags.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Tag, cancellationToken).ConfigureAwait(false);
        return rows.Select(r => new ReconcileTagRow(tags.GetValueOrDefault(r.TagId) ?? ("tag#" + r.TagId), r.Source, r.Rows, r.DeletedRows ?? 0)).ToList();
    }

    /// <summary>Start of the replica snapshot: one consistent, read only view for the expectation read and the count.</summary>
    internal const string SnapshotIsolationSql = "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ";

    /// <summary>Starts the consistent snapshot; <see cref="SnapshotIsolationSql"/> runs before it on the same connection.</summary>
    internal const string SnapshotSql = "START TRANSACTION WITH CONSISTENT SNAPSHOT, READ ONLY";

    /// <summary>
    /// The <c>archived_months</c> tag rows of a month with their tag names, rows and booked deleted rows (read through the replica context, which has no
    /// <c>archived_months</c> entity, hence raw SQL).
    /// </summary>
    internal static IQueryable<ReconcileRowResult> ReconcileRowsQuery(HypixelContext db, int monthIndex)
        => db.Database.SqlQuery<ReconcileRowResult>($"SELECT CAST(m.tag_id AS SIGNED) AS TagId, t.tag AS Tag, m.source AS Source, CAST(m.`rows` AS SIGNED) AS `Rows`, CAST(COALESCE(m.deleted_rows, 0) AS SIGNED) AS DeletedRows FROM archived_months m LEFT JOIN auction_tags t ON t.id = m.tag_id WHERE m.month = {monthIndex} AND m.tag_id <> 0");

    /// <summary>Auctions ending in the month per tag: an <c>End</c> index range with one GROUP BY, no subquery.</summary>
    internal static IQueryable<TagCount> CountByTagQuery(HypixelContext db, DateTime monthStart, DateTime monthEnd)
        => db.Auctions.AsNoTracking().Where(a => a.End >= monthStart && a.End < monthEnd)
            .GroupBy(a => a.Tag).Select(g => new TagCount { Tag = g.Key, Count = g.LongCount() });

    /// <summary>
    /// Expectations (<c>archived_months</c>) and per tag counts of the month from the READ REPLICA (<c>DBReadOnlyConnection</c>, the primary when none is
    /// configured) inside one <c>START TRANSACTION WITH CONSISTENT SNAPSHOT, READ ONLY</c>, so both reflect the same replicated point in time.
    /// </summary>
    protected virtual async Task<CountSnapshot> ReadCountSnapshotAsync(int month, DateTime monthStart, DateTime monthEnd, CancellationToken cancellationToken)
    {
        using var db = ArchiveReadContext.Open(config);
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
        await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(SnapshotIsolationSql, cancellationToken).ConfigureAwait(false);
        await db.Database.ExecuteSqlRawAsync(SnapshotSql, cancellationToken).ConfigureAwait(false);
        try
        {
            var rows = await ReconcileRowsQuery(db, ArchiveMonths.ToIndex(month)).ToListAsync(cancellationToken).ConfigureAwait(false);
            var counts = await CountByTagInWindowsAsync(monthStart, monthEnd, (from, to, ct) => CountWindowInSnapshotAsync(db, from, to, ct), logger, cancellationToken).ConfigureAwait(false);
            return new CountSnapshot(rows.Select(ToReconcileRow).ToList(), counts);
        }
        finally
        {
            await RollbackSnapshotAsync(db).ConfigureAwait(false);
        }
    }

    /// <summary>Counts one window; after a statement timeout the snapshot transaction must still be open, otherwise later windows would read a different point in time.</summary>
    private static async Task<List<TagCount>> CountWindowInSnapshotAsync(HypixelContext db, DateTime from, DateTime to, CancellationToken cancellationToken)
    {
        try
        {
            return await CountByTagQuery(db, from, to).ToListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (AuctionArchiveJob.IsStatementTimeout(ex))
        {
            var inTransaction = await db.Database.SqlQueryRaw<long>("SELECT CAST(@@in_transaction AS SIGNED) AS `Value`").SingleAsync(cancellationToken).ConfigureAwait(false);
            if (inTransaction == 0)
                throw new SnapshotLostException(ex);
            throw;
        }
    }

    /// <summary>
    /// The snapshot transaction ended after a statement timeout. It wraps that timeout for diagnostics, so the window splitter must exclude it
    /// explicitly (<see cref="AuctionArchiveJob.IsStatementTimeout"/> walks inner exceptions); it always fails the gate.
    /// </summary>
    internal sealed class SnapshotLostException : InvalidOperationException
    {
        public SnapshotLostException(Exception timeout)
            : base("the replica snapshot transaction ended after a statement timeout, the counts would not share one snapshot", timeout)
        {
        }
    }

    private static readonly TimeSpan CountWindow = TimeSpan.FromDays(1);
    internal static readonly TimeSpan MinCountWindow = TimeSpan.FromMinutes(15);

    /// <summary>The half-open day windows that exactly tile [monthStart, monthEnd); the last one is clipped.</summary>
    internal static IEnumerable<(DateTime From, DateTime To)> CountWindows(DateTime monthStart, DateTime monthEnd)
    {
        for (var from = monthStart; from < monthEnd; from += CountWindow)
            yield return (from, from + CountWindow < monthEnd ? from + CountWindow : monthEnd);
    }

    /// <summary>
    /// Per tag counts of [monthStart, monthEnd) summed over day windows, so no single statement runs into the server's <c>max_statement_time</c>. A window that
    /// still times out is split in half, but never into halves shorter than <see cref="MinCountWindow"/> (from a day that ends at 22.5 min windows); then the timeout is rethrown.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, long>> CountByTagInWindowsAsync(DateTime monthStart, DateTime monthEnd,
        Func<DateTime, DateTime, CancellationToken, Task<List<TagCount>>> countWindow, ILogger logger, CancellationToken cancellationToken)
    {
        var all = new List<TagCount>();
        foreach (var (from, to) in CountWindows(monthStart, monthEnd))
            await CountWindowSplittingAsync(from, to, countWindow, logger, all, cancellationToken).ConfigureAwait(false);
        return ToCountMap(all);
    }

    private static async Task CountWindowSplittingAsync(DateTime from, DateTime to, Func<DateTime, DateTime, CancellationToken, Task<List<TagCount>>> countWindow,
        ILogger logger, List<TagCount> sink, CancellationToken cancellationToken)
    {
        try
        {
            sink.AddRange(await countWindow(from, to, cancellationToken).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not SnapshotLostException && AuctionArchiveJob.IsStatementTimeout(ex) && !cancellationToken.IsCancellationRequested && (to - from) / 2 >= MinCountWindow)
        {
            var mid = from + (to - from) / 2;
            countWindowSplits.Inc();
            logger.LogWarning("Retention: count window {From:u} - {To:u} hit the statement timeout, splitting at {Mid:u}", from, to, mid);
            await CountWindowSplittingAsync(from, mid, countWindow, logger, sink, cancellationToken).ConfigureAwait(false);
            await CountWindowSplittingAsync(mid, to, countWindow, logger, sink, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Ends the snapshot transaction; a failure is only logged so it cannot mask the exception of the reads.</summary>
    private async Task RollbackSnapshotAsync(HypixelContext db)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("ROLLBACK", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Retention: rolling back the replica snapshot failed");
        }
    }

    private static ReconcileTagRow ToReconcileRow(ReconcileRowResult r) => new(r.Tag ?? ("tag#" + r.TagId), r.Source, r.Rows, r.DeletedRows);

    private static IReadOnlyDictionary<string, long> ToCountMap(IEnumerable<TagCount> counts)
        => counts.GroupBy(r => r.Tag ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Sum(r => r.Count), StringComparer.OrdinalIgnoreCase);

    /// <summary>Persists the abort backoff in the <c>note</c> of the month sentinel; leaves an operator's own note alone. False when no row was updated (operator note present).</summary>
    protected virtual async Task<bool> SetMonthBackoffAsync(int month, string note, CancellationToken cancellationToken)
    {
        int index = ArchiveMonths.ToIndex(month);
        using var db = new ArchiveContext();
        var affected = await db.Database.ExecuteSqlAsync($"UPDATE archived_months SET note = {note} WHERE tag_id = 0 AND month = {index} AND deleted_at IS NULL AND (note IS NULL OR note LIKE 'retention_backoff_until=%')", cancellationToken).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>
    /// <c>COUNT(*)</c> per <c>Tag</c> of the primary's <c>Auctions</c> with <c>End</c> in the month (End index range, long command timeout). A row without a tag
    /// is counted under the empty tag, which no archive row can expect.
    /// </summary>
    protected virtual async Task<IReadOnlyDictionary<string, long>> CountPrimaryAuctionsByTagAsync(DateTime monthStart, DateTime monthEnd, CancellationToken cancellationToken)
    {
        using var db = new HypixelContext();
        db.Database.SetCommandTimeout(TimeSpan.FromMinutes(30));
        return await CountByTagInWindowsAsync(monthStart, monthEnd, (from, to, ct) => CountByTagQuery(db, from, to).ToListAsync(ct), logger, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The receipt JSON of a month, null when it does not exist. Fails (fail closed) when no object probe is configured.</summary>
    protected virtual Task<string> ReadBackupReceiptAsync(int month, CancellationToken cancellationToken)
        => (probe ?? throw new InvalidOperationException("no IArchiveObjectProbe configured, the backup receipt can not be read")).ReadTextAsync(BackupReceipt.Key(month), cancellationToken);

    /// <summary>Current ETag (HEAD) of an R2 object, null when it does not exist.</summary>
    protected virtual Task<string> GetObjectETagAsync(string key, CancellationToken cancellationToken)
        => (probe ?? throw new InvalidOperationException("no IArchiveObjectProbe configured, block ETags can not be read")).GetETagAsync(key, cancellationToken);

    /// <summary>SQL to read the replication state on the replica (MariaDB; the primary has no such status). Needs <c>SLAVE MONITOR</c> / <c>REPLICA MONITOR</c> (MariaDB 10.5.9+).</summary>
    internal const string ReplicaStatusSql = "SHOW SLAVE STATUS";

    /// <summary>
    /// Replication state of the replica behind <c>DBReadOnlyConnection</c> (the connection the archive job reads from), null when the statement
    /// returns no row. No replica configured throws <see cref="ReplicaNotConfiguredException"/>, a missing privilege throws too (the caller treats both as unreadable).
    /// </summary>
    protected virtual async Task<ReplicaStatus> ReadReplicaStatusAsync(CancellationToken cancellationToken)
    {
        if (!ArchiveReadContext.UsesReadReplica(config))
            throw new ReplicaNotConfiguredException();
        using var db = ArchiveReadContext.Open(config);
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        using var cmd = connection.CreateCommand();
        cmd.CommandText = ReplicaStatusSql;
        using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return null;
        var lag = reader.GetValue(reader.GetOrdinal("Seconds_Behind_Master"));
        return new ReplicaStatus(ColumnText(reader, "Slave_IO_Running"), ColumnText(reader, "Slave_SQL_Running"),
            lag is DBNull ? null : Convert.ToDouble(lag, CultureInfo.InvariantCulture));
    }

    private static string ColumnText(System.Data.Common.DbDataReader reader, string column)
    {
        var value = reader.GetValue(reader.GetOrdinal(column));
        return value is DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    /// <summary>Months deleted (with rows) since the given UTC instant, from the sentinels.</summary>
    protected virtual async Task<int> CountMonthsDeletedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken)
    {
        using var db = new ArchiveContext();
        return await db.ArchivedMonths.AsNoTracking().CountAsync(m => m.TagId == 0 && m.DeletedAt >= sinceUtc && m.DeletedRows > 0, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tags of the month with their part counts and combined hashes (tag rows of <c>archived_months</c>).</summary>
    protected virtual async Task<IReadOnlyList<RetentionTagParts>> GetMonthPartsAsync(int month, CancellationToken cancellationToken)
    {
        var index = ArchiveMonths.ToIndex(month);
        using var db = new ArchiveContext();
        var rows = await db.ArchivedMonths.AsNoTracking().Where(m => m.Month == index && m.TagId != 0).Select(m => new { m.TagId, m.Parts, m.Sha256 }).ToListAsync(cancellationToken).ConfigureAwait(false);
        var tags = await db.AuctionTags.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Tag, cancellationToken).ConfigureAwait(false);
        return rows.Where(r => tags.ContainsKey(r.TagId)).Select(r => new RetentionTagParts(tags[r.TagId], r.Parts, r.Sha256)).ToList();
    }

    /// <summary>True when the month has a row in <c>auction_seller_backfill</c> (the retention pass already derived the month's seller rows from its parts; this pass is the only writer of the marker).</summary>
    protected virtual async Task<bool> IsSellerPassDoneAsync(int month, CancellationToken cancellationToken)
    {
        var index = ArchiveMonths.ToIndex(month);
        using var db = new ArchiveContext();
        return await db.AuctionSellerBackfillMonths.AsNoTracking().AnyAsync(m => m.Month == index, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Insert the month's marker in <c>auction_seller_backfill</c> (idempotent).</summary>
    protected virtual async Task MarkSellerBackfillDoneAsync(int month, CancellationToken cancellationToken)
    {
        int index = ArchiveMonths.ToIndex(month);
        var now = DateTime.UtcNow;
        using var db = new ArchiveContext();
        await db.Database.ExecuteSqlAsync($"INSERT IGNORE INTO auction_seller_backfill (month, done_at) VALUES ({index}, {now})", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>All <c>archived_months</c> rows of a month (sentinel included).</summary>
    protected virtual async Task<IReadOnlyList<ArchivedMonthRowState>> GetArchivedMonthRowsAsync(int month, CancellationToken cancellationToken)
    {
        var index = ArchiveMonths.ToIndex(month);
        using var db = new ArchiveContext();
        return await db.ArchivedMonths.AsNoTracking().Where(m => m.Month == index)
            .Select(m => new ArchivedMonthRowState(m.TagId, m.Rows, m.IndexRows, m.VerifiedAt, m.ArchivedAt, m.DeletedAt, m.ScyllaAt, m.Note))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Tag to tag id for every tag that is verified for the month with <c>index_rows = rows</c>.</summary>
    protected virtual async Task<IReadOnlyDictionary<string, ushort>> GetVerifiedTagsAsync(int month, CancellationToken cancellationToken)
    {
        var index = ArchiveMonths.ToIndex(month);
        using var db = new ArchiveContext();
        var verified = (await db.ArchivedMonths.AsNoTracking()
            .Where(m => m.Month == index && m.TagId != 0 && m.VerifiedAt != null && m.IndexRows == m.Rows)
            .Select(m => m.TagId).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
        var tags = await db.AuctionTags.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return tags.Where(t => verified.Contains(t.Id)).ToDictionary(t => t.Tag, t => t.Id, StringComparer.Ordinal);
    }

    /// <summary>The next batch of the month by the End index (rows already deleted are gone from the range).</summary>
    protected virtual async Task<IReadOnlyList<DeleteCandidate>> LoadDeleteBatchAsync(DateTime cursor, DateTime monthEnd, int batchSize, CancellationToken cancellationToken)
    {
        using var db = new HypixelContext();
        return await db.Auctions.AsNoTracking()
            .Where(a => a.End >= cursor && a.End < monthEnd)
            .OrderBy(a => a.End)
            .Take(batchSize)
            .Select(a => new DeleteCandidate(a.Id, a.UId, a.Tag, a.End, a.NbtDataId, a.SellerId, a.AuctioneerId))
            .ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>How many of the uids have an <c>auction_locations</c> row for the month (primary key lookups).</summary>
    protected virtual async Task<int> CountLocatedUidsAsync(ushort monthIndex, IReadOnlyList<long> uids, CancellationToken cancellationToken)
    {
        using var db = new ArchiveContext();
        var list = uids.ToList();
        return await db.AuctionLocations.AsNoTracking().CountAsync(l => list.Contains(l.Uid) && l.Month == monthIndex, cancellationToken).ConfigureAwait(false);
    }

    /// <summary><c>Players.Id</c> of up to 100 uuids; unknown uuids are absent.</summary>
    protected virtual Task<IReadOnlyDictionary<string, int>> ResolvePlayerIdsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken)
        => PlayerIdLookup.QueryPlayersAsync(uuids, cancellationToken);

    /// <summary>
    /// INSERT IGNORE the batch's <c>auction_sellers</c> rows (in <see cref="ArchiveIndexSql.BatchSize"/> statements). <c>ArchiveContext</c> and
    /// <c>HypixelContext</c> share the <c>DBConnection</c> database, so the values read with the batch are inserted as plain integers; a
    /// failure propagates and the batch is not deleted.
    /// </summary>
    protected virtual async Task WriteSellerRowsAsync(IReadOnlyList<AuctionSeller> sellers, CancellationToken cancellationToken)
    {
        if (sellers.Count == 0)
            return;
        using var db = new ArchiveContext();
        foreach (var sql in ArchiveIndexSql.BuildSellerInserts(sellers))
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Run the statements of one batch in one transaction together with the bookkeeping: the affected rows of each tag's <c>Auctions</c> delete are added to
    /// that tag's <c>archived_months.deleted_rows</c> (and their sum to the month sentinel) on the SAME connection before the commit. <c>HypixelContext</c> and
    /// <c>ArchiveContext</c> both use <c>DBConnection</c>, so the tables share one database and the delete and its booking commit or roll back together: after a crash
    /// <c>deleted_rows</c> is exact, the archive write guard sees it with the first batch, and the resumed month reconciles without tolerance.
    /// Returns the affected rows per table.
    /// </summary>
    protected virtual async Task<Dictionary<string, long>> ExecuteDeleteAsync(IReadOnlyList<DeleteStatement> statements, int month, CancellationToken cancellationToken)
    {
        var counts = new Dictionary<string, long>();
        var perTag = new Dictionary<ushort, long>();
        using var db = new HypixelContext();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        foreach (var statement in statements)
        {
            var affected = await db.Database.ExecuteSqlRawAsync(statement.ToSql(), cancellationToken).ConfigureAwait(false);
            counts[statement.Table] = counts.GetValueOrDefault(statement.Table) + affected;
            if (statement.TagId != null)
                perTag[statement.TagId.Value] = perTag.GetValueOrDefault(statement.TagId.Value) + affected;
        }
        await BookDeletedRowsAsync(db, month, perTag, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return counts;
    }

    private static async Task BookDeletedRowsAsync(HypixelContext db, int month, IReadOnlyDictionary<ushort, long> deletedPerTag, CancellationToken cancellationToken)
    {
        int index = ArchiveMonths.ToIndex(month);
        foreach (var (tagId, count) in deletedPerTag.Where(kv => kv.Value > 0))
            await AddDeletedRowsAsync(db, tagId, index, count, cancellationToken).ConfigureAwait(false);
        var total = deletedPerTag.Values.Sum();
        if (total > 0)
            await AddDeletedRowsAsync(db, 0, index, total, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Adds to <c>deleted_rows</c> of one archived_months row; a missing row throws so the whole batch transaction rolls back (fail closed).</summary>
    private static async Task AddDeletedRowsAsync(HypixelContext db, int tagId, int month, long count, CancellationToken cancellationToken)
    {
        var updated = await db.Database.ExecuteSqlAsync($"UPDATE archived_months SET deleted_rows = COALESCE(deleted_rows, 0) + {count} WHERE tag_id = {tagId} AND month = {month}", cancellationToken).ConfigureAwait(false);
        if (updated != 1)
            throw new InvalidOperationException($"archived_months row (tag_id {tagId}, month {month}) not found, the delete is rolled back");
    }

    /// <summary>Purge lag read; SHOW GLOBAL STATUS needs no PROCESS privilege (INNODB_METRICS does).</summary>
    internal const string HistoryLengthSql = "SHOW GLOBAL STATUS LIKE 'Innodb_history_list_length'";

    internal static long? ParseHistoryLength(object? value)
        => long.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>Best-effort binlog size and purge lag measurement.</summary>
    protected virtual async Task<HealthResult> CheckHealthAsync(ArchiveOptions options, CancellationToken cancellationToken)
    {
        using var db = new HypixelContext();
        var connection = db.Database.GetDbConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        long? binlog = null;
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SHOW BINARY LOGS";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            var size = reader.GetOrdinal("File_size");
            long sum = 0;
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                sum += Convert.ToInt64(reader.GetValue(size), CultureInfo.InvariantCulture);
            binlog = sum;
        }
        catch (MySqlException ex) when (ex.Number == 1381)
        {
            binlog = 0; // binary logging is off, nothing can grow
        }
        catch (MySqlException ex)
        {
            logger.LogDebug(ex, "SHOW BINARY LOGS failed");
        }
        long? history = null;
        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandText = HistoryLengthSql;
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                history = ParseHistoryLength(reader.GetValue(reader.GetOrdinal("Value")));
        }
        catch (MySqlException ex)
        {
            logger.LogDebug(ex, "Reading Innodb_history_list_length failed");
        }
        return EvaluateHealth(binlog, history, options);
    }

    /// <summary>Set <c>deleted_at</c> on every row of the month (the per batch counts were booked with the deletes) and drop a leftover backoff note, never an operator note.</summary>
    protected virtual async Task MarkMonthDeletedAsync(int month, CancellationToken cancellationToken)
    {
        int index = ArchiveMonths.ToIndex(month);
        var now = DateTime.UtcNow;
        using var db = new ArchiveContext();
        await db.Database.ExecuteSqlAsync($"UPDATE archived_months SET deleted_at = {now}, deleted_rows = COALESCE(deleted_rows, 0), note = CASE WHEN note LIKE 'retention_backoff_until=%' THEN NULL ELSE note END WHERE month = {index} AND deleted_at IS NULL", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Log how much space <c>OPTIMIZE TABLE</c> (operator step) could return; best effort.</summary>
    protected virtual async Task LogSpaceHintAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var db = new HypixelContext();
            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            using var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT TABLE_NAME, DATA_FREE, DATA_LENGTH + INDEX_LENGTH FROM information_schema.tables WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME IN ('Auctions','NbtData','Bids','NBTLookups','Enchantment','UuId')";
            using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                logger.LogInformation("Retention: table {Table} has {Free:F1} GB free inside its file ({Size:F1} GB used); OPTIMIZE TABLE is an operator step and needs free disk of about the used size",
                    reader.GetString(0), Convert.ToDouble(reader.GetValue(1), CultureInfo.InvariantCulture) / 1e9, Convert.ToDouble(reader.GetValue(2), CultureInfo.InvariantCulture) / 1e9);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogDebug(ex, "Could not read table space for the OPTIMIZE hint");
        }
    }

    /// <summary>Wait between steps (overridden in tests).</summary>
    protected virtual Task Delay(TimeSpan delay, CancellationToken cancellationToken)
    {
        return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
    }
}
