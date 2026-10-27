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

namespace Coflnet.Sky.Indexer.Archive
{
    /// <summary>Auction row picked for deletion.</summary>
    /// <param name="SellerId"><c>Auctions.SellerId</c> (<c>Players.Id</c>); 0 is not necessarily anonymous (the Numberer sets 0 for duplicate players and old rows were never numbered)</param>
    /// <param name="AuctioneerId"><c>Auctions.AuctioneerId</c> (32-char hex uuid), resolved to <c>Players.Id</c> when <paramref name="SellerId"/> is 0</param>
    public record DeleteCandidate(int Id, long UId, string Tag, DateTime End, int? NbtDataId, int SellerId = 0, string AuctioneerId = null);

    /// <summary>The bookkeeping columns of one <c>archived_months</c> row that the retention gates read.</summary>
    public record ArchivedMonthRowState(ushort TagId, uint Rows, uint? IndexRows, DateTime? VerifiedAt, DateTime ArchivedAt, DateTime? DeletedAt);

    /// <summary>A tag of a month with its part count and the combined hash <c>archived_months.sha256</c> (null when unknown).</summary>
    public record RetentionTagParts(string Tag, int Parts, string Sha256);

    /// <summary>Result of the MariaDB health gate.</summary>
    public enum HealthStatus { Ok, Exceeded, Unavailable }

    /// <summary>Health gate result with a human readable reason.</summary>
    public record HealthResult(HealthStatus Status, string Reason);

    /// <summary>How deleting one month ended.</summary>
    public enum MonthDeleteOutcome { Completed, HealthStopped, Aborted }

    /// <summary>
    /// One DELETE of a batch. With <see cref="KeepIfReferencedByTable"/> rows that are still referenced
    /// (shared <c>NbtData</c>) are kept via <c>NOT EXISTS</c>.
    /// </summary>
    public sealed record DeleteStatement(string Table, string Column, IReadOnlyList<long> Ids, string KeepIfReferencedByTable = null, string KeepIfReferencedByColumn = null)
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
    /// <c>index_rows = rows</c>), only with <c>ARCHIVE:DELETE_MARIADB=true</c>. Per batch: health gate (binlog size,
    /// purge lag), safety check (every row's tag verified for the month, every uid in <c>auction_locations</c>), one short
    /// transaction. At most <c>MAX_DELETE_MONTHS_PER_DAY</c> months per UTC day. <c>OPTIMIZE TABLE</c> stays an operator step.
    /// Before each batch is deleted its `auction_sellers` rows are inserted (the seller backfill for MariaDB rows); the permanent index tables are never deleted from.
    /// </summary>
    public class AuctionRetentionWorker : BackgroundService
    {
        private readonly IAuctionBlobStore store;
        private readonly IConfiguration config;
        private readonly ILogger<AuctionRetentionWorker> logger;
        private bool unavailableLogged;

        private static readonly Prometheus.Counter rowsDeleted = Prometheus.Metrics.CreateCounter("sky_indexer_retention_rows_deleted", "MariaDB rows deleted by the retention worker", "table");
        private static readonly Prometheus.Counter sellerRows = Prometheus.Metrics.CreateCounter("sky_indexer_retention_seller_rows", "auction_sellers rows written by the retention worker before a batch was deleted");
        private static readonly Prometheus.Counter sellerPassRows = Prometheus.Metrics.CreateCounter("sky_indexer_archive_seller_rows_backfilled", "auction_sellers rows written from the archived parts by the retention pass");
        private static readonly Prometheus.Counter sellerPassParts = Prometheus.Metrics.CreateCounter("sky_indexer_retention_seller_pass_parts", "Archived parts read back (verified and decoded once) by the retention seller pass");
        private static readonly Prometheus.Counter sellerPassMonths = Prometheus.Metrics.CreateCounter("sky_indexer_archive_seller_backfill_months_done", "Months whose archived parts were fully read back for seller rows");
        private static readonly Prometheus.Counter batches = Prometheus.Metrics.CreateCounter("sky_indexer_retention_batches", "Delete batches committed");
        private static readonly Prometheus.Counter monthsDeleted = Prometheus.Metrics.CreateCounter("sky_indexer_retention_months_deleted", "Months whose MariaDB rows were fully deleted");
        private static readonly Prometheus.Counter healthStops = Prometheus.Metrics.CreateCounter("sky_indexer_retention_health_stops", "Delete runs stopped by the binlog / purge lag gate");
        private static readonly Prometheus.Counter aborts = Prometheus.Metrics.CreateCounter("sky_indexer_retention_aborts", "Months aborted by the safety check (unverified tag, missing index row, no progress)");

        /// <summary>DI constructor.</summary>
        public AuctionRetentionWorker(IAuctionBlobStore store, IConfiguration config, ILogger<AuctionRetentionWorker> logger)
        {
            this.store = store;
            this.config = config;
            this.logger = logger;
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
                    try
                    {
                        await Delay(TimeSpan.FromMinutes(15), stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>One run; returns the number of months completed.</summary>
        internal async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            var options = ArchiveOptions.From(config);
            if (!options.Enabled || !options.DeleteMariaDb)
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
                var rows = await GetArchivedMonthRowsAsync(month, cancellationToken).ConfigureAwait(false);
                if (rows.FirstOrDefault(r => r.TagId == 0)?.DeletedAt != null)
                    continue;
                if (!IsFullyArchived(rows))
                {
                    logger.LogDebug("Retention: {Month} is not fully archived and verified yet", month);
                    continue;
                }
                var (outcome, deleted) = await DeleteMonthAsync(month, options, cancellationToken).ConfigureAwait(false);
                if (outcome != MonthDeleteOutcome.Completed)
                    break;
                if (deleted > 0)
                    completed++;
            }
            return completed;
        }

        /// <summary>Gate: month sentinel archived and every tag row verified with as many index rows as archived rows.</summary>
        internal static bool IsFullyArchived(IReadOnlyCollection<ArchivedMonthRowState> rows)
        {
            if (rows == null || !rows.Any(r => r.TagId == 0))
                return false;
            return rows.Where(r => r.TagId != 0).All(r => r.VerifiedAt != null && r.IndexRows == r.Rows);
        }

        /// <summary>Delete all MariaDB rows of one fully archived month in batches.</summary>
        internal async Task<(MonthDeleteOutcome outcome, long deleted)> DeleteMonthAsync(int month, ArchiveOptions options, CancellationToken cancellationToken)
        {
            var (monthStart, monthEnd) = ArchiveMonths.MonthRange(month);
            var monthIndex = ArchiveMonths.ToIndex(month);
            var verifiedTags = await GetVerifiedTagsAsync(month, cancellationToken).ConfigureAwait(false);
            var deletedPerTag = new Dictionary<ushort, long>();
            long total = 0;
            var cursor = monthStart;
            // The archived parts of the month are read back once (uncached, verified against the R2 tag index and archived_months.sha256)
            // and the seller rows of EVERY archived auction (MariaDB rows and Scylla-only/merged parts alike) are derived from that same
            // decode, before the first MariaDB row of the month is deleted. A month without MariaDB rows left still passes through here once.
            if (!await IsSellerPassDoneAsync(month, cancellationToken).ConfigureAwait(false))
            {
                string problem;
                try
                {
                    problem = await SellerPassAsync(month, options, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    problem = "seller pass failed: " + ex.GetType().Name + " " + ex.Message;
                }
                if (problem != null)
                {
                    aborts.Inc();
                    logger.LogError("Retention: aborting {Month}, nothing deleted: {Problem}", month, problem);
                    return (MonthDeleteOutcome.Aborted, 0);
                }
                await MarkSellerBackfillDoneAsync(month, cancellationToken).ConfigureAwait(false);
                sellerPassMonths.Inc();
            }
            logger.LogInformation("Retention: deleting MariaDB rows of {Month}", month);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var health = await CheckHealthAsync(options, cancellationToken).ConfigureAwait(false);
                if (health.Status == HealthStatus.Exceeded || health.Status == HealthStatus.Unavailable && options.RequireHealthChecks)
                {
                    healthStops.Inc();
                    if (health.Status == HealthStatus.Exceeded || !unavailableLogged)
                        logger.LogWarning("Retention: health gate stopped deleting {Month}: {Reason}", month, health.Reason);
                    unavailableLogged |= health.Status == HealthStatus.Unavailable;
                    await RecordDeletedAsync(month, deletedPerTag, total, completed: false, cancellationToken).ConfigureAwait(false);
                    return (MonthDeleteOutcome.HealthStopped, total);
                }

                var batch = await LoadDeleteBatchAsync(cursor, monthEnd, options.DeleteBatchSize, cancellationToken).ConfigureAwait(false);
                if (batch.Count == 0)
                    break;

                var problem = await SafetyProblemAsync(monthIndex, batch, verifiedTags, cancellationToken).ConfigureAwait(false);
                if (problem != null)
                {
                    aborts.Inc();
                    logger.LogError("Retention: aborting {Month}, nothing of this batch deleted: {Problem}", month, problem);
                    await RecordDeletedAsync(month, deletedPerTag, total, completed: false, cancellationToken).ConfigureAwait(false);
                    return (MonthDeleteOutcome.Aborted, total);
                }

                // seller index first: the MariaDB row is the only source of Auctions.SellerId, so no batch is deleted before its
                // auction_sellers rows are committed (INSERT IGNORE, safe to repeat after a crash between the insert and the delete)
                List<AuctionSeller> sellers = null;
                try
                {
                    var unnumbered = batch.Where(b => b.SellerId <= 0 && ScyllaArchiveImportWorker.IsIndexableSeller(b.AuctioneerId)).Select(b => b.AuctioneerId).ToList();
                    var ids = unnumbered.Count == 0
                        ? new Dictionary<string, int>()
                        : await PlayerIdLookup.ResolveAsync(unnumbered, ResolvePlayerIdsAsync, cancellationToken).ConfigureAwait(false);
                    sellers = BuildSellerRows(batch, ids);
                    await WriteSellerRowsAsync(sellers, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    aborts.Inc();
                    logger.LogError(ex, "Retention: writing the auction_sellers rows of {Month} failed, nothing of this batch deleted", month);
                    await RecordDeletedAsync(month, deletedPerTag, total, completed: false, cancellationToken).ConfigureAwait(false);
                    return (MonthDeleteOutcome.Aborted, total);
                }
                sellerRows.Inc(sellers.Count);

                var counts = await ExecuteDeleteAsync(BuildDeleteStatements(batch), cancellationToken).ConfigureAwait(false);
                foreach (var (table, count) in counts)
                    rowsDeleted.WithLabels(table).Inc(count);
                var auctionsDeleted = counts.GetValueOrDefault("Auctions");
                if (auctionsDeleted == 0)
                {
                    aborts.Inc();
                    logger.LogError("Retention: a batch of {Month} deleted no auction rows; aborting to avoid a loop", month);
                    await RecordDeletedAsync(month, deletedPerTag, total, completed: false, cancellationToken).ConfigureAwait(false);
                    return (MonthDeleteOutcome.Aborted, total);
                }
                batches.Inc();
                total += auctionsDeleted;
                foreach (var row in batch)
                {
                    var tagId = verifiedTags[row.Tag];
                    deletedPerTag[tagId] = deletedPerTag.GetValueOrDefault(tagId) + 1;
                }
                // deleted rows vanish from the End range, so the cursor only has to move forward to the last End seen
                cursor = batch.Max(b => b.End);
                await Delay(TimeSpan.FromMilliseconds(options.DeletePauseMs), cancellationToken).ConfigureAwait(false);
            }

            await RecordDeletedAsync(month, deletedPerTag, total, completed: true, cancellationToken).ConfigureAwait(false);
            monthsDeleted.Inc();
            logger.LogInformation("Retention: deleted {Rows} auctions of {Month}", total, month);
            await LogSpaceHintAsync(cancellationToken).ConfigureAwait(false);
            return (MonthDeleteOutcome.Completed, total);
        }

        /// <summary>
        /// Read every part of every tag-month once (uncached), verify it against the R2 tag index (and the combined hash against
        /// <c>archived_months.sha256</c>) and INSERT IGNORE the seller rows of all its auctions. Returns a problem text, null when the month is done.
        /// A part is written only after its hash matched, so rows never come from a corrupted blob.
        /// </summary>
        private async Task<string> SellerPassAsync(int month, ArchiveOptions options, CancellationToken cancellationToken)
        {
            long rows = 0;
            var parts = 0;
            foreach (var tag in (await GetMonthPartsAsync(month, cancellationToken).ConfigureAwait(false)).OrderBy(t => t.Tag, StringComparer.Ordinal))
            {
                var index = await store.ReadIndexAsync(tag.Tag, cancellationToken).ConfigureAwait(false);
                var entry = index?.Find(month);
                if (entry == null || entry.PartSha256.Count < tag.Parts)
                    return $"{tag.Tag} has {tag.Parts} parts in archived_months but the R2 tag index lists {entry?.PartSha256.Count ?? 0}";
                var hashes = new List<string>();
                for (var part = 0; part < tag.Parts; part++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var block = await store.ReadBlockAsync(tag.Tag, month, part, true, cancellationToken).ConfigureAwait(false);
                    if (block == null)
                        return $"part {part} of {tag.Tag} is missing from the store";
                    var sha = block.Sha256();
                    if (!string.Equals(sha, entry.PartSha256[part], StringComparison.OrdinalIgnoreCase))
                        return $"part {part} of {tag.Tag} hashes to {sha}, the tag index says {entry.PartSha256[part]}";
                    hashes.Add(sha);
                    var sellers = await BuildSellerRowsOfBlockAsync(block, cancellationToken).ConfigureAwait(false);
                    await WriteSellerRowsAsync(sellers, cancellationToken).ConfigureAwait(false);
                    rows += sellers.Count;
                    parts++;
                    sellerPassParts.Inc();
                    sellerPassRows.Inc(sellers.Count);
                    await Delay(TimeSpan.FromMilliseconds(options.DeletePauseMs), cancellationToken).ConfigureAwait(false);
                }
                if (tag.Sha256 != null && !string.Equals(ArchivedAuctionBlock.CombinedSha256(hashes), tag.Sha256, StringComparison.OrdinalIgnoreCase))
                    return $"the parts of {tag.Tag} no longer combine to archived_months.sha256";
            }
            logger.LogInformation("Retention: {Month} seller pass wrote {Rows} seller rows from {Parts} archived parts", month, rows, parts);
            return null;
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

        /// <summary>The ordered DELETE statements of one batch: children, then Auctions, then NbtData no longer referenced.</summary>
        internal static List<DeleteStatement> BuildDeleteStatements(IReadOnlyList<DeleteCandidate> batch)
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
                new("Auctions", "Id", ids),
            };
            if (nbtIds.Count > 0)
                statements.Add(new DeleteStatement("NbtData", "Id", nbtIds, "Auctions", "NbtDataId"));
            return statements;
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
                .Select(m => new ArchivedMonthRowState(m.TagId, m.Rows, m.IndexRows, m.VerifiedAt, m.ArchivedAt, m.DeletedAt))
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

        /// <summary>Run the statements of one batch in one transaction; affected rows per table.</summary>
        protected virtual async Task<Dictionary<string, long>> ExecuteDeleteAsync(IReadOnlyList<DeleteStatement> statements, CancellationToken cancellationToken)
        {
            var counts = new Dictionary<string, long>();
            using var db = new HypixelContext();
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            foreach (var statement in statements)
            {
                var affected = await db.Database.ExecuteSqlRawAsync(statement.ToSql(), cancellationToken).ConfigureAwait(false);
                counts[statement.Table] = counts.GetValueOrDefault(statement.Table) + affected;
            }
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return counts;
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

        /// <summary>Add the deleted counts to <c>archived_months</c>; with <paramref name="completed"/> also set <c>deleted_at</c> on every row of the month.</summary>
        protected virtual async Task RecordDeletedAsync(int month, IReadOnlyDictionary<ushort, long> deletedPerTag, long total, bool completed, CancellationToken cancellationToken)
        {
            int index = ArchiveMonths.ToIndex(month);
            var now = DateTime.UtcNow;
            using var db = new ArchiveContext();
            foreach (var (tagId, count) in deletedPerTag)
            {
                int tag = tagId;
                await db.Database.ExecuteSqlAsync($"UPDATE archived_months SET deleted_rows = COALESCE(deleted_rows, 0) + {count} WHERE tag_id = {tag} AND month = {index}", cancellationToken).ConfigureAwait(false);
            }
            if (total > 0)
                await db.Database.ExecuteSqlAsync($"UPDATE archived_months SET deleted_rows = COALESCE(deleted_rows, 0) + {total} WHERE tag_id = 0 AND month = {index}", cancellationToken).ConfigureAwait(false);
            if (completed)
                await db.Database.ExecuteSqlAsync($"UPDATE archived_months SET deleted_at = {now}, deleted_rows = COALESCE(deleted_rows, 0) WHERE month = {index} AND deleted_at IS NULL", cancellationToken).ConfigureAwait(false);
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
}
