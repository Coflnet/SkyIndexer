using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Indexer.Archive
{
    /// <summary>Archive state of one month in <c>archived_months</c>.</summary>
    public record ArchiveMonthState(bool SentinelArchived, IReadOnlySet<ushort> VerifiedTagIds);

    /// <summary>
    /// Archives closed months of MariaDB auctions into immutable R2 blobs, oldest month first.
    /// Per (item, month): keyset pages over the <c>(ItemId, End)</c> index, children by batched id queries,
    /// parts of at most <c>MAX_ROWS_PER_PART</c> rows, each written and verified by an uncached read-back.
    /// Only when every part verified: <c>auction_locations</c> and <c>auction_bidders</c> rows, the per-tag R2 index
    /// and finally the verified <c>archived_months</c> row (the marker is written last). After every item of a month,
    /// the month sentinel (<c>tag_id = 0</c>) is written. Nothing is ever deleted here (see <see cref="AuctionRetentionWorker"/>).
    /// </summary>
    public class AuctionArchiveJob : BackgroundService
    {
        /// <summary>Hash stored for tag-months without rows and for month sentinels (SHA-256 of nothing).</summary>
        public static readonly string EmptySha256 = ArchivedAuctionBlock.CombinedSha256(Array.Empty<string>());
        /// <summary>Rows per INSERT of the index tables.</summary>
        public const int IndexBatchSize = ArchiveIndexSql.BatchSize;
        private const int MaxConsecutiveItemFailures = 5;

        private readonly IAuctionBlobStore store;
        private readonly IConfiguration config;
        private readonly ILogger<AuctionArchiveJob> logger;
        private readonly IArchiveItemCheckpoints checkpoints;
        private readonly ConcurrentDictionary<string, ushort> tagIds = new(StringComparer.Ordinal);

        private static readonly Prometheus.Counter monthsTotal = Prometheus.Metrics.CreateCounter("sky_indexer_archive_months_total", "Months fully archived (sentinel written)");
        private static readonly Prometheus.Counter partsWritten = Prometheus.Metrics.CreateCounter("sky_indexer_archive_parts_written", "Archive parts written");
        private static readonly Prometheus.Counter partsVerified = Prometheus.Metrics.CreateCounter("sky_indexer_archive_parts_verified", "Archive parts verified by uncached read-back");
        private static readonly Prometheus.Counter verifyFailures = Prometheus.Metrics.CreateCounter("sky_indexer_archive_verify_failures", "Archive parts whose read-back did not match");
        private static readonly Prometheus.Counter rowsArchived = Prometheus.Metrics.CreateCounter("sky_indexer_archive_rows_archived", "Auction rows in verified parts");
        private static readonly Prometheus.Counter indexRowsWritten = Prometheus.Metrics.CreateCounter("sky_indexer_archive_index_rows", "auction_locations rows written");
        private static readonly Prometheus.Counter bidderRowsWritten = Prometheus.Metrics.CreateCounter("sky_indexer_archive_bidder_rows", "auction_bidders rows written");
        private static readonly Prometheus.Counter sellerRowsWritten = Prometheus.Metrics.CreateCounter("sky_indexer_archive_seller_rows", "auction_sellers rows written");
        private static readonly Prometheus.Counter bidsWithoutBidder = Prometheus.Metrics.CreateCounter("sky_indexer_archive_bids_without_bidder", "Bids skipped for auction_bidders because BidderId is 0");
        private static readonly Prometheus.Counter itemMonthsSkipped = Prometheus.Metrics.CreateCounter("sky_indexer_archive_item_months_skipped", "ItemId-months skipped because the item's first auction ends in a later month");
        private static readonly Prometheus.Counter itemsSkippedByCheckpoint = Prometheus.Metrics.CreateCounter("sky_indexer_archive_items_skipped_by_checkpoint", "ItemId-months skipped after a restart because a Redis checkpoint says they are done");
        private static readonly Prometheus.Counter pages = Prometheus.Metrics.CreateCounter("sky_indexer_archive_pages", "Auction pages read from MariaDB");
        private static readonly Prometheus.Counter errors = Prometheus.Metrics.CreateCounter("sky_indexer_archive_errors", "Archive errors (tag-months that failed with an exception, tag collisions)");
        private static readonly Prometheus.Counter childQueryRetries = Prometheus.Metrics.CreateCounter("sky_indexer_archive_child_query_retries_total", "Child or page queries re-run in smaller pieces after a statement timeout", new Prometheus.CounterConfiguration { LabelNames = new[] { "table" } });
        private static readonly Prometheus.Counter indexWriteRetries = Prometheus.Metrics.CreateCounter("sky_indexer_archive_index_write_retries_total", "Primary writes re-run after a timeout, deadlock or lock wait timeout", new Prometheus.CounterConfiguration { LabelNames = new[] { "operation" } });
        private static readonly Prometheus.Gauge currentMonth = Prometheus.Metrics.CreateGauge("sky_indexer_archive_current_month", "Month (yyyyMM) being archived");
        private static readonly Prometheus.Gauge lastMonthCompleted = Prometheus.Metrics.CreateGauge("sky_indexer_archive_last_month_completed_timestamp", "Unix time the last month sentinel was written");

        /// <summary>DI constructor.</summary>
        public AuctionArchiveJob(IAuctionBlobStore store, IConfiguration config, ILogger<AuctionArchiveJob> logger, IArchiveItemCheckpoints checkpoints = null)
        {
            this.checkpoints = checkpoints ?? new NoArchiveItemCheckpoints();
            this.store = store;
            this.config = config;
            this.logger = logger;
        }

        /// <summary>
        /// Keyset page predicate on the <c>(ItemId, End)</c> index: the range <c>End &gt;= cursorEnd AND End &lt; monthEnd</c>,
        /// minus the rows at <c>cursorEnd</c> already emitted (<c>Id &lt;= cursorId</c>), so a page full of rows with the same End
        /// can not repeat forever. Ordered by End, Id (index order, no filesort).
        /// </summary>
        public static Expression<Func<SaveAuction, bool>> PageFilter(int itemId, DateTime cursorEnd, int cursorId, DateTime monthEnd)
        {
            return a => a.ItemId == itemId && a.End >= cursorEnd && a.End < monthEnd && (a.End > cursorEnd || a.Id > cursorId);
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var options = ArchiveOptions.From(config);
            if (!options.Enabled)
                return;
            if (!store.IsEnabled)
            {
                logger.LogWarning("ARCHIVE:ENABLED=true but the S3 archive store is not configured; auction archive job will not run.");
                return;
            }
            try
            {
                await Delay(TimeSpan.FromSeconds(options.StartDelaySeconds), stoppingToken).ConfigureAwait(false);
                await EnsureSchemaAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            logger.LogInformation("Auction archive job reads from {Source}", ArchiveReadContext.ReadConnectionDescription(config));
            logger.LogInformation("Auction archive job started; oldest month {Oldest}, margin {Margin} days, max {Rows} rows per part", options.OldestMonth, options.ClosedMarginDays, options.MaxRowsPerPart);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var completed = await RunOnceAsync(DateTime.UtcNow, stoppingToken).ConfigureAwait(false);
                    var idle = ArchiveOptions.From(config).IdlePauseMinutes;
                    logger.LogInformation("Auction archive pass done ({Completed} months completed); idling {Idle} min", completed, idle);
                    await Delay(TimeSpan.FromMinutes(idle), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    errors.Inc();
                    logger.LogError(ex, "Auction archive pass failed, retrying after backoff");
                    try
                    {
                        await Delay(TimeSpan.FromMinutes(5), stoppingToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                }
            }
        }

        /// <summary>One pass over all closed months; returns the number of months completed (sentinel written).</summary>
        internal async Task<int> RunOnceAsync(DateTime nowUtc, CancellationToken cancellationToken)
        {
            var options = ArchiveOptions.From(config);
            if (options.RequestedChildQueryBatchSize != options.ChildQueryBatchSize)
                logger.LogWarning("ARCHIVE:CHILD_QUERY_BATCH_SIZE={Requested} is outside 1..{Max} (MariaDB eq_range_index_dive_limit is 200); using {Used}", options.RequestedChildQueryBatchSize, ArchiveOptions.MaxChildQueryBatchSize, options.ChildQueryBatchSize);
            var newest = ArchiveMonths.NewestClosed(nowUtc, options.ClosedMarginDays);
            var completed = 0;
            IReadOnlyList<int> itemIds = null;
            IReadOnlyDictionary<int, int> firstMonths = null;
            for (var month = options.OldestMonth; month <= newest; month = ArchiveMonths.Next(month))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var state = await GetMonthStateAsync(month, cancellationToken).ConfigureAwait(false);
                if (state.SentinelArchived)
                    continue;
                if (itemIds == null)
                {
                    // the ItemIds that occur in Auctions with the month of their first auction, loaded once per pass; without the
                    // list no month may be marked complete. Items released later are skipped for the months before their first auction.
                    try
                    {
                        var firstEnds = await GetAuctionItemFirstEndsAsync(cancellationToken).ConfigureAwait(false);
                        firstMonths = firstEnds.ToDictionary(kv => kv.Key, kv => ArchiveMonths.MonthOf(kv.Value));
                        itemIds = firstMonths.Keys.OrderBy(i => i).ToList();
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
                    {
                        errors.Inc();
                        logger.LogError(ex, "Could not read the distinct ItemIds of Auctions; no month is archived or marked complete in this pass, retrying next pass");
                        return completed;
                    }
                    if (itemIds.Count == 0)
                    {
                        errors.Inc();
                        logger.LogError("Auctions has no ItemIds; no month is marked complete in this pass");
                        return completed;
                    }
                }
                currentMonth.Set(month);
                var laterItems = firstMonths.Values.Count(first => first > month);
                logger.LogInformation("Archiving auctions of {Month} ({Items} item ids, {Later} first seen later)", month, itemIds.Count - laterItems, laterItems);
                if (await ArchiveMonthAsync(month, itemIds, options, cancellationToken, firstMonths).ConfigureAwait(false))
                {
                    await WriteMonthSentinelAsync(month, cancellationToken).ConfigureAwait(false);
                    await checkpoints.ClearAsync(month, cancellationToken).ConfigureAwait(false);
                    monthsTotal.Inc();
                    lastMonthCompleted.Set(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                    completed++;
                    logger.LogInformation("Archived every item id of {Month}", month);
                }
                else
                {
                    logger.LogWarning("Month {Month} is not complete yet; it is retried on the next pass", month);
                }
                await Delay(TimeSpan.FromMilliseconds(options.MonthPauseMs), cancellationToken).ConfigureAwait(false);
            }
            return completed;
        }

        /// <summary>
        /// Archive a month: page every ItemId by <c>(ItemId, End)</c>, group each page by the row's own <c>Tag</c> and write
        /// each tag's rows as parts of that tag-month. The tag comes from the rows, never from an id-to-tag map, so one ItemId can
        /// feed several tags and several ItemIds can feed one tag; part numbers of a tag-month run over all ItemIds (in ascending
        /// ItemId order, so a re-run after a crash overwrites the same part keys with the same rows). The per-tag markers
        /// (<c>archived_months</c>, R2 tag index) are written last, after every ItemId has been paged and every part verified.
        /// False when a part did not verify or a row has no tag (nothing is marked, the month is retried next pass).
        /// </summary>
        /// <param name="firstMonths">Month (<c>yyyyMM</c>) of each ItemId's first auction; items first seen after <paramref name="month"/>
        /// are skipped without a query. Null skips nothing.</param>
        internal async Task<bool> ArchiveMonthAsync(int month, IReadOnlyList<int> itemIds, ArchiveOptions options, CancellationToken cancellationToken, IReadOnlyDictionary<int, int> firstMonths = null)
        {
            var ttl = TimeSpan.FromHours(options.CheckpointTtlHours);
            var state = new MonthRuns();
            var done = await RestoreCheckpointsAsync(month, itemIds, state, firstMonths, ttl, cancellationToken).ConfigureAwait(false);
            if (done.Count > 0 && !await RestoredPartsExistAsync(month, state.Runs, cancellationToken).ConfigureAwait(false))
            {
                state = new MonthRuns();
                done = new Dictionary<int, ItemMonthCheckpoint>();
            }
            var work = new List<int>();
            foreach (var itemId in itemIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (firstMonths != null && firstMonths.TryGetValue(itemId, out var first) && first > month)
                {
                    itemMonthsSkipped.Inc();
                    continue;
                }
                if (done.ContainsKey(itemId))
                {
                    itemsSkippedByCheckpoint.Inc();
                    continue;
                }
                work.Add(itemId);
            }
            // Items run concurrently, each with its own private runs; a finished item merges them into the shared month state
            // (part numbers of a tag are reserved atomically) and then saves its own checkpoint. The first failing item cancels the others.
            using var failure = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var failed = 0;
            try
            {
                await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.ItemParallelism), CancellationToken = failure.Token }, async (itemId, token) =>
                {
                    var startedAt = DateTime.UtcNow;
                    var (ok, rows, checkpoint) = await ArchiveItemMonthAsync(itemId, month, state, options, token, startedAt).ConfigureAwait(false);
                    if (!ok)
                    {
                        Interlocked.Exchange(ref failed, 1);
                        failure.Cancel();
                        return;
                    }
                    await checkpoints.SaveAsync(month, itemId, checkpoint, ttl, token).ConfigureAwait(false);
                    // an empty ItemId-month was one cheap index lookup; only pause after real work
                    if (rows > 0)
                        await Delay(TimeSpan.FromMilliseconds(options.TagPauseMs), token).ConfigureAwait(false);
                }).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (Volatile.Read(ref failed) != 0 && !cancellationToken.IsCancellationRequested)
            {
                // another item failed and cancelled this one
            }
            if (Volatile.Read(ref failed) != 0)
                return false;
            var allFinalized = true;
            foreach (var run in state.Runs.Values.OrderBy(r => r.Tag, StringComparer.Ordinal))
                allFinalized &= await FinalizeTagMonthAsync(run, cancellationToken).ConfigureAwait(false);
            return allFinalized;
        }

        /// <summary>
        /// The restored part hashes go into the tag index and the sentinel unchecked, so make sure the parts really are in the store.
        /// On a missing part the checkpoints are dropped (and counted as an error) and the month is walked again.
        /// </summary>
        private async Task<bool> RestoredPartsExistAsync(int month, Dictionary<string, TagMonthRun> runs, CancellationToken cancellationToken)
        {
            foreach (var run in runs.Values.OrderBy(r => r.Tag, StringComparer.Ordinal))
                for (var part = 0; part < run.Parts.Count; part++)
                    if (!await store.BlockExistsAsync(run.Tag, month, part, cancellationToken).ConfigureAwait(false))
                    {
                        errors.Inc();
                        logger.LogWarning("Checkpointed part {Part} of {Tag} {Month} is missing from the store; ignoring all checkpoints and re-walking the month", part, run.Tag, month);
                        await checkpoints.ClearAsync(month, cancellationToken).ConfigureAwait(false);
                        return false;
                    }
            return true;
        }

        /// <summary>The checkpoint of a finished item: per touched tag the parts it wrote (with their month-wide numbers) and its counters.</summary>
        private static ItemMonthCheckpoint BuildCheckpoint(Dictionary<string, TagMonthRun> local, DateTime archivedAt)
        {
            var tags = new Dictionary<string, CheckpointTagDelta>(StringComparer.Ordinal);
            foreach (var (tag, run) in local)
            {
                var parts = run.Parts.Select(kv => new CheckpointPart(kv.Key, kv.Value)).ToList();
                var delta = new CheckpointTagDelta(parts, run.Rows, run.SoldRows, run.IndexRows, run.IndexBidderRows, run.BidsWithoutBidder);
                if (parts.Count > 0 || delta.Rows != 0 || delta.SoldRows != 0 || delta.IndexRows != 0 || delta.IndexBidderRows != 0 || delta.BidsWithoutBidder != 0)
                    tags[tag] = delta;
            }
            return new ItemMonthCheckpoint(archivedAt, tags);
        }

        /// <summary>
        /// Rebuild the month state from the month's checkpoints. Returns the finished items (empty when there are none, Redis
        /// failed or a part number occurs twice; then everything is re-walked and <paramref name="state"/> stays empty).
        /// Items run concurrently, so a crash can leave a finished item whose part numbers lie behind the number of an unfinished
        /// item (a gap in the part numbers of a tag). Those items are dropped and redone, the others stay; the Redis hash is rewritten
        /// without the dropped items so no stale part numbers survive.
        /// </summary>
        private async Task<IReadOnlyDictionary<int, ItemMonthCheckpoint>> RestoreCheckpointsAsync(int month, IReadOnlyList<int> itemIds, MonthRuns state, IReadOnlyDictionary<int, int> firstMonths, TimeSpan ttl, CancellationToken cancellationToken)
        {
            var empty = new Dictionary<int, ItemMonthCheckpoint>();
            var loaded = await checkpoints.LoadAsync(month, cancellationToken).ConfigureAwait(false);
            if (loaded.Count == 0)
                return empty;
            var seen = new HashSet<(string tag, int part)>();
            foreach (var (itemId, checkpoint) in loaded.OrderBy(c => c.Key))
                foreach (var (tag, delta) in checkpoint.Tags)
                    foreach (var part in delta.Parts ?? new List<CheckpointPart>())
                        if (!seen.Add((tag, part.Part)))
                        {
                            logger.LogWarning("Checkpoints of {Month} contain part {Part} of {Tag} twice (item {Item}); ignoring all checkpoints and re-walking the month", month, part.Part, tag, itemId);
                            await checkpoints.ClearAsync(month, cancellationToken).ConfigureAwait(false);
                            return empty;
                        }
            var kept = new Dictionary<int, ItemMonthCheckpoint>(loaded);
            var dropped = new List<int>();
            while (true)
            {
                var present = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
                foreach (var checkpoint in kept.Values)
                    foreach (var (tag, delta) in checkpoint.Tags)
                    {
                        if (!present.TryGetValue(tag, out var set))
                            present[tag] = set = new HashSet<int>();
                        foreach (var part in delta.Parts ?? new List<CheckpointPart>())
                            set.Add(part.Part);
                    }
                var dense = present.ToDictionary(kv => kv.Key, kv => { var n = 0; while (kv.Value.Contains(n)) n++; return n; }, StringComparer.Ordinal);
                var bad = kept.Where(kv => kv.Value.Tags.Any(t => (t.Value.Parts ?? new List<CheckpointPart>()).Any(p => p.Part < 0 || p.Part >= dense[t.Key]))).Select(kv => kv.Key).ToList();
                if (bad.Count == 0)
                    break;
                foreach (var itemId in bad)
                {
                    kept.Remove(itemId);
                    dropped.Add(itemId);
                }
            }
            if (dropped.Count > 0)
            {
                logger.LogWarning("Checkpoints of {Month} have gaps in the part numbers (items {Items} were in flight when the last run ended); redoing those items and keeping {Kept} others", month, string.Join(",", dropped.OrderBy(i => i)), kept.Count);
                await checkpoints.ClearAsync(month, cancellationToken).ConfigureAwait(false);
                foreach (var (itemId, checkpoint) in kept.OrderBy(k => k.Key))
                    await checkpoints.SaveAsync(month, itemId, checkpoint, ttl, cancellationToken).ConfigureAwait(false);
                if (kept.Count == 0)
                    return empty;
            }
            var rebuilt = new Dictionary<string, TagMonthRun>(StringComparer.Ordinal);
            foreach (var (itemId, checkpoint) in kept.OrderBy(c => c.Key))
            {
                foreach (var (tag, delta) in checkpoint.Tags)
                {
                    if (!rebuilt.TryGetValue(tag, out var run))
                        rebuilt[tag] = run = new TagMonthRun(tag, month);
                    foreach (var part in delta.Parts ?? new List<CheckpointPart>())
                        run.Parts.Add(part.Part, part.Sha256);
                    run.Rows += delta.Rows;
                    run.SoldRows += delta.SoldRows;
                    run.IndexRows += delta.IndexRows;
                    run.IndexBidderRows += delta.IndexBidderRows;
                    run.BidsWithoutBidder += delta.BidsWithoutBidder;
                    if (run.ArchivedAt == null || checkpoint.ArchivedAt < run.ArchivedAt)
                        run.ArchivedAt = DateTime.SpecifyKind(checkpoint.ArchivedAt, DateTimeKind.Utc);
                }
            }
            state.Restore(rebuilt);
            var resume = itemIds.FirstOrDefault(i => !kept.ContainsKey(i) && !(firstMonths != null && firstMonths.TryGetValue(i, out var first) && first > month), -1);
            logger.LogInformation("Resuming {Month} from checkpoints: {Skipped} finished items skipped, walk resumes at item {Item}", month, itemIds.Count(kept.ContainsKey), resume);
            return kept;
        }

        /// <summary>
        /// Page one ItemId of a month; the item collects its parts and counters in private runs, the index rows are written after its parts
        /// verified, and only then are the runs merged into <paramref name="state"/> (so a failing item leaves the shared state untouched).
        /// Part numbers are reserved from <paramref name="state"/>, which keeps them unique per tag-month while items run concurrently.
        /// </summary>
        internal async Task<(bool ok, int rows, ItemMonthCheckpoint checkpoint)> ArchiveItemMonthAsync(int itemId, int month, MonthRuns state, ArchiveOptions options, CancellationToken cancellationToken, DateTime? startedAt = null)
        {
            var archivedAt = startedAt ?? DateTime.UtcNow;
            var local = new Dictionary<string, TagMonthRun>(StringComparer.Ordinal);
            var (monthStart, monthEnd) = ArchiveMonths.MonthRange(month);
            var buffers = new Dictionary<string, List<SaveAuction>>(StringComparer.Ordinal);
            var indexes = new Dictionary<string, ItemRunIndex>(StringComparer.Ordinal);
            var cursorEnd = monthStart;
            var cursorId = 0;
            var total = 0;
            var take = options.PageSize;
            while (true)
            {
                List<SaveAuction> page;
                (page, take) = await LoadPageResilientAsync(itemId, cursorEnd, cursorId, monthEnd, take, cancellationToken).ConfigureAwait(false);
                pages.Inc();
                if (page.Count == 0)
                    break;
                await LoadChildrenAsync(page, cancellationToken).ConfigureAwait(false);
                foreach (var row in page)
                {
                    if (row.End < cursorEnd || row.End == cursorEnd && row.Id <= cursorId)
                        throw new InvalidOperationException($"Keyset page of item {itemId} went backwards at {row.End:O}/{row.Id}");
                    cursorEnd = row.End;
                    cursorId = row.Id;
                    if (string.IsNullOrEmpty(row.Tag))
                    {
                        errors.Inc();
                        logger.LogError("Auction {Id} (uid {UId}, item {Item}) ended in {Month} has no tag; the month can not be archived and gets no sentinel", row.Id, row.UId, itemId, month);
                        return (false, total, null);
                    }
                    if (!buffers.TryGetValue(row.Tag, out var buffer))
                        buffers[row.Tag] = buffer = new List<SaveAuction>();
                    buffer.Add(row);
                    total++;
                    if (buffer.Count >= options.MaxRowsPerPart && !await FlushPartAsync(state, local, indexes, row.Tag, month, buffer, cancellationToken).ConfigureAwait(false))
                        return (false, total, null);
                }
                if (page.Count < take)
                    break;
                await Delay(TimeSpan.FromMilliseconds(options.PagePauseMs), cancellationToken).ConfigureAwait(false);
            }
            foreach (var (tag, buffer) in buffers.OrderBy(b => b.Key, StringComparer.Ordinal))
                if (buffer.Count > 0 && !await FlushPartAsync(state, local, indexes, tag, month, buffer, cancellationToken).ConfigureAwait(false))
                    return (false, total, null);

            var monthIndex = ArchiveMonths.ToIndex(month);
            foreach (var (tag, index) in indexes.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                var tagId = await RetryPrimaryWriteAsync("tag_id", () => GetOrCreateTagIdAsync(tag, cancellationToken), cancellationToken).ConfigureAwait(false);
                var bidders = index.Bidders.Select(kv => new AuctionBidder { BidderId = kv.Key.bidder, Uid = kv.Key.uid, End = kv.Value.end }).ToList();
                var sellers = index.Sellers.Select(kv => new AuctionSeller { SellerId = kv.Key.seller, Uid = kv.Key.uid, End = kv.Value }).ToList();
                if (index.UnnumberedSellers.Count > 0)
                {
                    var ids = await PlayerIdLookup.ResolveAsync(index.UnnumberedSellers.Keys.Select(k => k.uuid), ResolvePlayerIdsAsync, cancellationToken).ConfigureAwait(false);
                    sellers.AddRange(ScyllaArchiveImportWorker.ResolveSellers(index.UnnumberedSellers, ids));
                }
                // bulk inserts on the primary: serialized (default one at a time) so parallel items do not pile up on its disk
                var gate = IndexWriteGate(options);
                await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await WriteIndexRowsAsync(tagId, monthIndex, index.Locations, bidders, sellers, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
                indexRowsWritten.Inc(index.Locations.Count);
                bidderRowsWritten.Inc(bidders.Count);
                sellerRowsWritten.Inc(sellers.Count);
                local[tag].IndexRows += index.Locations.Count;
                local[tag].IndexBidderRows += bidders.Count;
            }
            var checkpoint = BuildCheckpoint(local, archivedAt);
            state.Merge(local);
            return (true, total, checkpoint);
        }

        private const int MinPageSize = 100;
        private const int MinChildChunk = 10;
        private const int MaxChildSplitDepth = 3;

        /// <summary>Waits before the 1st, 2nd and 3rd re-run of a failed primary write (so up to 4 attempts).</summary>
        internal static readonly TimeSpan[] PrimaryWriteRetryDelays = { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(15) };

        private SemaphoreSlim indexWriteGate;
        private readonly object indexWriteGateLock = new();

        /// <summary>Limits concurrent index bulk inserts on the primary, sized by <c>INDEX_WRITE_PARALLELISM</c> on first use.</summary>
        private SemaphoreSlim IndexWriteGate(ArchiveOptions options)
        {
            lock (indexWriteGateLock)
                return indexWriteGate ??= new SemaphoreSlim(Math.Max(1, options.IndexWriteParallelism));
        }

        /// <summary>Statement timeout (see <see cref="IsStatementTimeout"/>), deadlock (1213) or lock wait timeout (1205): worth re-running an idempotent write.</summary>
        internal static bool IsRetryableWriteError(Exception ex)
        {
            if (IsStatementTimeout(ex))
                return true;
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is MySqlConnector.MySqlException my && (my.Number == 1213 || my.Number == 1205))
                    return true;
                if (e is AggregateException agg && agg.InnerExceptions.Any(IsRetryableWriteError))
                    return true;
            }
            return false;
        }

        /// <summary>Runs an idempotent primary write, re-running it after <see cref="PrimaryWriteRetryDelays"/> when it hits
        /// <see cref="IsRetryableWriteError"/>. Other errors, and the failure after the last re-run, propagate.</summary>
        private async Task<T> RetryPrimaryWriteAsync<T>(string operation, Func<Task<T>> write, CancellationToken cancellationToken)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    return await write().ConfigureAwait(false);
                }
                catch (Exception ex) when (IsRetryableWriteError(ex) && !cancellationToken.IsCancellationRequested && attempt < PrimaryWriteRetryDelays.Length)
                {
                    indexWriteRetries.WithLabels(operation).Inc();
                    logger.LogWarning(ex, "Primary write {Operation} failed (attempt {Attempt}); retrying in {Delay}", operation, attempt + 1, PrimaryWriteRetryDelays[attempt]);
                    await Delay(PrimaryWriteRetryDelays[attempt], cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private Task RetryPrimaryWriteAsync(string operation, Func<Task> write, CancellationToken cancellationToken)
            => RetryPrimaryWriteAsync<bool>(operation, async () => { await write().ConfigureAwait(false); return true; }, cancellationToken);

        /// <summary>A context on the primary with the (longer) <c>INDEX_WRITE_TIMEOUT_SECONDS</c> command timeout.</summary>
        private ArchiveContext OpenPrimaryWriteContext()
        {
            var db = new ArchiveContext();
            db.Database.SetCommandTimeout(TimeSpan.FromSeconds(ArchiveOptions.From(config).IndexWriteTimeoutSeconds));
            return db;
        }

        /// <summary>
        /// MariaDB <c>max_statement_time</c> (error 1969, "Query execution was interrupted (max_statement_time exceeded)"), MySQL's
        /// <c>max_execution_time</c> (3024) or MySqlConnector's client side command timeout, looking through inner and aggregate exceptions.
        /// </summary>
        internal static bool IsStatementTimeout(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (e is MySqlConnector.MySqlException my && (my.Number == 1969 || my.Number == 3024 || my.ErrorCode == MySqlConnector.MySqlErrorCode.CommandTimeoutExpired))
                    return true;
                if (e is AggregateException agg && agg.InnerExceptions.Any(IsStatementTimeout))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// <see cref="LoadPageAsync"/>, halving the page size (down to <see cref="MinPageSize"/>) when the page query hits the statement timeout.
        /// Returns the page size that worked; it stays reduced for the rest of the item (a short page only ends the item when it is below that size).
        /// </summary>
        private async Task<(List<SaveAuction> page, int take)> LoadPageResilientAsync(int itemId, DateTime cursorEnd, int cursorId, DateTime monthEnd, int take, CancellationToken cancellationToken)
        {
            while (true)
            {
                try
                {
                    return (await LoadPageAsync(itemId, cursorEnd, cursorId, monthEnd, take, cancellationToken).ConfigureAwait(false), take);
                }
                catch (Exception ex) when (IsStatementTimeout(ex) && !cancellationToken.IsCancellationRequested && take / 2 >= MinPageSize)
                {
                    childQueryRetries.WithLabels("Auctions").Inc();
                    logger.LogWarning("Page query of item {Item} timed out with {Take} rows; retrying with {Half}", itemId, take, take / 2);
                    take /= 2;
                }
            }
        }

        /// <summary>
        /// Run a child-table query for <paramref name="ids"/>; on a statement timeout the ids are split in half and each half is retried
        /// (recursively, at most <see cref="MaxChildSplitDepth"/> times and never below <see cref="MinChildChunk"/> ids). The results of the halves
        /// are concatenated in id order, so they equal the unsplit result. A timeout that can not be split further is rethrown.
        /// </summary>
        internal static async Task<List<T>> QueryWithSplitAsync<T>(string table, IReadOnlyList<int> ids, Func<IReadOnlyList<int>, CancellationToken, Task<List<T>>> query, ILogger logger, CancellationToken cancellationToken, int depth = 0)
        {
            try
            {
                return await query(ids, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsStatementTimeout(ex) && !cancellationToken.IsCancellationRequested && depth < MaxChildSplitDepth && ids.Count / 2 >= MinChildChunk)
            {
                logger?.LogWarning("Child query on {Table} with {Count} ids timed out; retrying as two halves (split depth {Depth})", table, ids.Count, depth + 1);
                childQueryRetries.WithLabels(table).Inc(2);
                var half = ids.Count / 2;
                var left = await QueryWithSplitAsync(table, ids.Take(half).ToList(), query, logger, cancellationToken, depth + 1).ConfigureAwait(false);
                var right = await QueryWithSplitAsync(table, ids.Skip(half).ToList(), query, logger, cancellationToken, depth + 1).ConfigureAwait(false);
                left.AddRange(right);
                return left;
            }
        }

        /// <summary>Current value of <c>sky_indexer_archive_child_query_retries_total</c> for a table (tests).</summary>
        internal static double ChildQueryRetries(string table) => childQueryRetries.WithLabels(table).Value;

        /// <summary>Current value of <c>sky_indexer_archive_index_write_retries_total</c> for an operation (tests).</summary>
        internal static double IndexWriteRetries(string operation) => indexWriteRetries.WithLabels(operation).Value;

        /// <summary>
        /// The R2 tag index and the verified <c>archived_months</c> marker of a tag-month (written last). The index write is
        /// conditional (the SkyAuctions Scylla sweep writes the same per-tag object); on a conflict the index is re-read and the
        /// entry re-applied, up to <see cref="ArchiveIndexConflictException.MaxWriteAttempts"/> times. False (no marker) when it never got through.
        /// </summary>
        private async Task<bool> FinalizeTagMonthAsync(TagMonthRun run, CancellationToken cancellationToken)
        {
            if (!run.IsDense)
            {
                errors.Inc();
                logger.LogError("Parts of {Tag} {Month} are not numbered 0..n-1 ({Parts}); not marked, retried next pass", run.Tag, run.Month, string.Join(",", run.Parts.Keys));
                return false;
            }
            var tagId = await GetOrCreateTagIdAsync(run.Tag, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var partHashes = run.PartHashes;
            var entry = new ArchivedMonthEntry
            {
                Month = run.Month,
                Parts = partHashes.Count,
                Rows = run.Rows,
                SoldRows = run.SoldRows,
                PartSha256 = partHashes.ToList(),
                Source = ArchivedAuctionBlock.SourceMariaDb,
                VerifiedAt = now,
            };
            for (var attempt = 1; ; attempt++)
            {
                var index = await store.ReadIndexAsync(run.Tag, cancellationToken).ConfigureAwait(false) ?? new ArchivedTagIndex { Tag = run.Tag };
                index.Upsert(entry);
                try
                {
                    await store.WriteIndexAsync(index, cancellationToken).ConfigureAwait(false);
                    break;
                }
                catch (ArchiveIndexConflictException) when (attempt < ArchiveIndexConflictException.MaxWriteAttempts)
                {
                    logger.LogInformation("Tag index of {Tag} changed while archiving {Month} (attempt {Attempt}); re-reading", run.Tag, run.Month, attempt);
                }
                catch (ArchiveIndexConflictException)
                {
                    errors.Inc();
                    logger.LogError("Tag index of {Tag} kept changing ({Attempts} attempts); {Month} not marked, retried next pass", run.Tag, attempt, run.Month);
                    return false;
                }
            }
            var monthRow = new ArchivedMonth
            {
                TagId = tagId,
                Month = ArchiveMonths.ToIndex(run.Month),
                Parts = checked((byte)partHashes.Count),
                Rows = (uint)run.Rows,
                SoldRows = (uint)run.SoldRows,
                Source = ArchivedAuctionBlock.SourceMariaDb,
                Sha256 = ArchivedAuctionBlock.CombinedSha256(partHashes),
                ArchivedAt = run.ArchivedAt ?? now,
                VerifiedAt = now,
                IndexRows = (uint)run.IndexRows,
                IndexBidderRows = (uint)run.IndexBidderRows,
                Note = run.BidsWithoutBidder > 0 ? $"{run.BidsWithoutBidder} bids without bidder id not indexed" : null,
            };
            await RetryPrimaryWriteAsync("archived_month", () => UpsertArchivedMonthAsync(monthRow, cancellationToken), cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Archived {Rows} auctions of {Tag} {Month} in {Parts} parts", run.Rows, run.Tag, run.Month, partHashes.Count);
            return true;
        }

        /// <summary>Write the buffered rows of a tag as that tag-month's next part, verify by uncached read-back and collect the index rows. Clears <paramref name="rows"/>.</summary>
        private async Task<bool> FlushPartAsync(MonthRuns state, Dictionary<string, TagMonthRun> runs, Dictionary<string, ItemRunIndex> indexes, string tag, int month, List<SaveAuction> rows, CancellationToken cancellationToken)
        {
            if (!runs.TryGetValue(tag, out var run))
                runs[tag] = run = new TagMonthRun(tag, month);
            if (!indexes.TryGetValue(tag, out var index))
                indexes[tag] = index = new ItemRunIndex();
            var part = state.ReservePart(tag);
            if (part > byte.MaxValue)
                throw new InvalidOperationException($"{run.Tag} {run.Month} needs more than {byte.MaxValue + 1} parts; raise MAX_ROWS_PER_PART");
            var block = ArchivedAuctionBlock.FromAuctions(run.Tag, run.Month, part, ArchivedAuctionBlock.SourceMariaDb, rows);
            await store.WriteBlockAsync(block, cancellationToken).ConfigureAwait(false);
            run.ArchivedAt ??= DateTime.UtcNow;
            partsWritten.Inc();
            var readBack = await store.ReadBlockAsync(run.Tag, run.Month, part, uncached: true, cancellationToken).ConfigureAwait(false);
            var difference = block.FirstDifference(readBack);
            if (difference != null)
            {
                verifyFailures.Inc();
                logger.LogWarning("Read-back of {Tag} {Month} part {Part} differs in {Column}; month not marked, retried next pass", run.Tag, run.Month, part, difference);
                return false;
            }
            partsVerified.Inc();
            rowsArchived.Inc(block.RowCount);
            run.Parts[part] = block.Sha256();
            run.Rows += block.RowCount;
            run.SoldRows += block.IsSold.Count(s => s);
            foreach (var row in rows)
            {
                index.Locations.Add((row.UId, (byte)part));
                var end = (uint)new DateTimeOffset(DateTime.SpecifyKind(row.End, DateTimeKind.Utc)).ToUnixTimeSeconds();
                if (!ScyllaArchiveImportWorker.IsIndexableSeller(row.AuctioneerId))
                {
                    // anonymised or opted-out auctioneer (whatever SellerId says), or no auctioneer uuid: no seller row
                }
                else if (row.SellerId > 0)
                    index.Sellers[((uint)row.SellerId, row.UId)] = end;
                else
                    index.UnnumberedSellers[(row.AuctioneerId, row.UId)] = end; // SellerId 0 is not always anonymous (Numberer: duplicate players, old rows)
                if (row.Bids == null)
                    continue;
                foreach (var bid in row.Bids)
                {
                    if (bid.BidderId <= 0 || bid.Amount < 0 || PlayerOptOut.IsOptedOut(bid.Bidder))
                    {
                        run.BidsWithoutBidder++;
                        bidsWithoutBidder.Inc();
                        continue;
                    }
                    var key = ((uint)bid.BidderId, row.UId);
                    if (!index.Bidders.TryGetValue(key, out var existing) || existing.amount < (ulong)bid.Amount)
                        index.Bidders[key] = (end, (ulong)bid.Amount);
                }
            }
            rows.Clear();
            return true;
        }

        /// <summary>State of one tag-month across all ItemIds (only hashes and counters are kept, never rows).</summary>
        internal sealed class TagMonthRun
        {
            public TagMonthRun(string tag, int month)
            {
                Tag = tag;
                Month = month;
            }
            public string Tag { get; }
            public int Month { get; }
            /// <summary>Verified part hashes by part number inside the tag-month.</summary>
            public SortedDictionary<int, string> Parts { get; } = new();
            /// <summary>The hashes in part order.</summary>
            public IReadOnlyList<string> PartHashes => Parts.Values.ToList();
            /// <summary>True when the part numbers are exactly 0..n-1.</summary>
            public bool IsDense => Parts.Count == 0 || Parts.Keys.First() == 0 && Parts.Keys.Last() == Parts.Count - 1;
            public int Rows { get; set; }
            public int SoldRows { get; set; }
            public int IndexRows { get; set; }
            public int IndexBidderRows { get; set; }
            public long BidsWithoutBidder { get; set; }
            public DateTime? ArchivedAt { get; set; }
        }

        /// <summary>
        /// Shared state of one month while its items run concurrently: the merged per-tag runs and the next part number of each tag.
        /// Everything is guarded by one lock; items only touch it to reserve a part number and, once finished, to merge their private runs.
        /// </summary>
        internal sealed class MonthRuns
        {
            private readonly object gate = new();
            private readonly Dictionary<string, int> nextPart = new(StringComparer.Ordinal);

            /// <summary>Merged runs of the restored and the finished items. Read it only when no item is running.</summary>
            public Dictionary<string, TagMonthRun> Runs { get; } = new(StringComparer.Ordinal);

            /// <summary>The next unused part number of a tag-month; every call returns a new one.</summary>
            public int ReservePart(string tag)
            {
                lock (gate)
                {
                    var part = nextPart.GetValueOrDefault(tag);
                    nextPart[tag] = part + 1;
                    return part;
                }
            }

            /// <summary>Take over runs rebuilt from checkpoints (dense part numbers), new parts continue behind them.</summary>
            public void Restore(Dictionary<string, TagMonthRun> restored)
            {
                lock (gate)
                    foreach (var (tag, run) in restored)
                    {
                        Runs[tag] = run;
                        nextPart[tag] = Math.Max(nextPart.GetValueOrDefault(tag), run.Parts.Count);
                    }
            }

            /// <summary>Add the runs of a finished item (its parts keep the numbers they reserved).</summary>
            public void Merge(Dictionary<string, TagMonthRun> item)
            {
                lock (gate)
                    foreach (var (tag, local) in item)
                    {
                        if (!Runs.TryGetValue(tag, out var run))
                            Runs[tag] = run = new TagMonthRun(tag, local.Month);
                        foreach (var (part, hash) in local.Parts)
                            run.Parts.Add(part, hash);
                        run.Rows += local.Rows;
                        run.SoldRows += local.SoldRows;
                        run.IndexRows += local.IndexRows;
                        run.IndexBidderRows += local.IndexBidderRows;
                        run.BidsWithoutBidder += local.BidsWithoutBidder;
                        if (local.ArchivedAt != null && (run.ArchivedAt == null || local.ArchivedAt < run.ArchivedAt))
                            run.ArchivedAt = local.ArchivedAt;
                    }
            }
        }

        /// <summary>Index tuples of one tag collected while one ItemId is paged.</summary>
        internal sealed class ItemRunIndex
        {
            public List<(long uid, byte part)> Locations { get; } = new();
            public Dictionary<(uint bidder, long uid), (uint end, ulong amount)> Bidders { get; } = new();
            /// <summary>Seller (<c>Players.Id</c>) and uid to the auction end; SellerId 0 (unknown) is left out like bids without a bidder.</summary>
            public Dictionary<(uint seller, long uid), uint> Sellers { get; } = new();
            /// <summary>Rows with SellerId 0 but a real auctioneer uuid, resolved to <c>Players.Id</c> when the index rows are written.</summary>
            public Dictionary<(string uuid, long uid), uint> UnnumberedSellers { get; } = new();
        }

        // ---------------------------------------------------------------- data access seams

        /// <summary>Command timeout for the schema migrations: index-table rebuilds (e.g. PAGE_COMPRESSED) take many minutes.</summary>
        internal static readonly TimeSpan MigrationCommandTimeout = TimeSpan.FromHours(4);

        /// <summary>Create the archive tables (own migrations history table); only called when the job is enabled.
        /// Runs with a <see cref="MigrationCommandTimeout"/> (4 h) command timeout and logs start and elapsed time, because
        /// migrations such as the index compression rebuild tables of ~50-80 M rows. Not unit-tested (needs a database).</summary>
        protected virtual async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            using var db = new ArchiveContext();
            // Migrations can ALTER the permanent index tables (tens of millions of rows, rebuilt on the primary): no short command timeout.
            db.Database.SetCommandTimeout(MigrationCommandTimeout);
            logger.LogInformation("Applying archive schema migrations (may take long on large index tables)");
            var sw = System.Diagnostics.Stopwatch.StartNew();
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Archive schema migrations applied in {Elapsed}", sw.Elapsed);
        }

        /// <summary>Distinct <c>ItemId</c>s of <c>Auctions</c> with the <c>End</c> of their first auction (loose index scan on
        /// <c>IX_Auctions_ItemId_End</c>, a few thousand rows).</summary>
        protected virtual async Task<IReadOnlyDictionary<int, DateTime>> GetAuctionItemFirstEndsAsync(CancellationToken cancellationToken)
        {
            using var db = ArchiveReadContext.Open(config);
            db.Database.SetCommandTimeout(TimeSpan.FromMinutes(10));
            var rows = await db.Auctions.AsNoTracking().GroupBy(a => a.ItemId).Select(g => new { ItemId = g.Key, First = g.Min(a => a.End) }).ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.ToDictionary(r => r.ItemId, r => r.First);
        }

        /// <summary>Sentinel and verified tags of a month.</summary>
        protected virtual async Task<ArchiveMonthState> GetMonthStateAsync(int month, CancellationToken cancellationToken)
        {
            var index = ArchiveMonths.ToIndex(month);
            using var db = new ArchiveContext();
            var sentinel = await db.ArchivedMonths.AsNoTracking().AnyAsync(m => m.TagId == 0 && m.Month == index, cancellationToken).ConfigureAwait(false);
            if (sentinel)
                return new ArchiveMonthState(true, new HashSet<ushort>());
            var verified = await db.ArchivedMonths.AsNoTracking()
                .Where(m => m.Month == index && m.TagId != 0 && m.VerifiedAt != null)
                .Select(m => m.TagId).ToListAsync(cancellationToken).ConfigureAwait(false);
            return new ArchiveMonthState(false, verified.ToHashSet());
        }

        /// <summary>Id of a tag in <c>auction_tags</c>, inserted on first use (select first so ignored inserts do not burn auto increment values).</summary>
        protected virtual async Task<ushort> GetOrCreateTagIdAsync(string tag, CancellationToken cancellationToken)
        {
            if (tagIds.TryGetValue(tag, out var cached))
                return cached;
            using var db = OpenPrimaryWriteContext();
            var existing = await db.AuctionTags.AsNoTracking().Where(t => t.Tag == tag).Select(t => (ushort?)t.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (existing == null)
            {
                try
                {
                    var entity = new AuctionTag { Tag = tag };
                    db.AuctionTags.Add(entity);
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    existing = entity.Id;
                }
                catch (DbUpdateException ex) when (!IsRetryableWriteError(ex)) // a duplicate key from a concurrent insert, not a timeout
                {
                    using var retry = OpenPrimaryWriteContext();
                    existing = await retry.AuctionTags.AsNoTracking().Where(t => t.Tag == tag).Select(t => (ushort?)t.Id).FirstAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            tagIds[tag] = existing.Value;
            return existing.Value;
        }

        /// <summary>One keyset page of auctions without children, see <see cref="PageFilter"/>.</summary>
        protected virtual async Task<List<SaveAuction>> LoadPageAsync(int itemId, DateTime cursorEnd, int cursorId, DateTime monthEnd, int pageSize, CancellationToken cancellationToken)
        {
            using var db = ArchiveReadContext.Open(config);
            return await db.Auctions.AsNoTracking()
                .Where(PageFilter(itemId, cursorEnd, cursorId, monthEnd))
                .OrderBy(a => a.End).ThenBy(a => a.Id)
                .Take(pageSize)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Number of auction ids per <c>IN (...)</c> list of the child queries in <see cref="LoadChildrenAsync"/>.
        /// MariaDB's <c>eq_range_index_dive_limit</c> defaults to 200: with that many IN values or more the optimizer
        /// stops doing index dives and uses index statistics instead. On an all-NULL indexed column such as
        /// <c>UuId.SaveAuctionId</c> the statistics say "every value matches every row", which produced a full index
        /// scan over 583M rows (command timeout, month 2020-10 failed 52 times). Keep this well below 200.
        /// </summary>
        /// <summary>Split <paramref name="items"/> into consecutive lists of at most <paramref name="size"/>, preserving order.</summary>
        internal static IEnumerable<List<T>> Chunk<T>(IReadOnlyList<T> items, int size)
        {
            if (size <= 0)
                throw new ArgumentOutOfRangeException(nameof(size));
            for (int i = 0; i < items.Count; i += size)
            {
                var chunk = new List<T>(Math.Min(size, items.Count - i));
                for (int j = i; j < Math.Min(i + size, items.Count); j++)
                    chunk.Add(items[j]);
                yield return chunk;
            }
        }

        /// <summary>
        /// Load bids, item bytes, nbt lookups, enchantments, coop members and claimed bidders of a page with one query per table and
        /// <see cref="ArchiveOptions.ChildQueryBatchSize"/> ids. The queries of all chunks run concurrently (at most
        /// <see cref="ArchiveOptions.ChildQueryParallelism"/>, each on its own connection); every result lands in the slot of its chunk, so the
        /// assembled children equal those of a sequential load. A query that hits the statement timeout is split, see <see cref="QueryWithSplitAsync{T}"/>.
        /// </summary>
        protected virtual async Task LoadChildrenAsync(List<SaveAuction> page, CancellationToken cancellationToken)
        {
            var ids = page.Select(a => a.Id).ToList();
            var nbtIds = page.Where(a => a.NbtDataId != null).Select(a => a.NbtDataId.Value).Distinct().ToList();
            var options = ArchiveOptions.From(config);
            var chunks = Chunk(ids, options.ChildQueryBatchSize).ToList();
            var nbtChunks = Chunk(nbtIds, options.ChildQueryBatchSize).ToList();
            var bids = new List<(int? AuctionId, SaveBids Bid)>[chunks.Count];
            var lookups = new List<NBTLookup>[chunks.Count];
            var enchantments = new List<Enchantment>[chunks.Count];
            var coop = new List<(int? AuctionId, string value)>[chunks.Count];
            var claimed = new List<(int? AuctionId, string value)>[chunks.Count];
            var nbtRows = new List<NbtData>[nbtChunks.Count];

            var work = new List<Func<CancellationToken, Task>>();
            for (var i = 0; i < chunks.Count; i++)
            {
                var (index, chunk) = (i, chunks[i]);
                work.Add(async ct => bids[index] = await QueryWithSplitAsync("Bids", chunk, QueryBidsAsync, logger, ct).ConfigureAwait(false));
                work.Add(async ct => lookups[index] = await QueryWithSplitAsync("NBTLookups", chunk, QueryNbtLookupsAsync, logger, ct).ConfigureAwait(false));
                work.Add(async ct => enchantments[index] = await QueryWithSplitAsync("Enchantment", chunk, QueryEnchantmentsAsync, logger, ct).ConfigureAwait(false));
                work.Add(async ct => coop[index] = await QueryWithSplitAsync("UuId_coop", chunk, QueryCoopAsync, logger, ct).ConfigureAwait(false));
                work.Add(async ct => claimed[index] = await QueryWithSplitAsync("UuId_claimed", chunk, QueryClaimedAsync, logger, ct).ConfigureAwait(false));
            }
            for (var i = 0; i < nbtChunks.Count; i++)
            {
                var (index, chunk) = (i, nbtChunks[i]);
                work.Add(async ct => nbtRows[index] = await QueryWithSplitAsync("NbtData", chunk, QueryNbtDataAsync, logger, ct).ConfigureAwait(false));
            }
            await Parallel.ForEachAsync(work, new ParallelOptions { MaxDegreeOfParallelism = options.ChildQueryParallelism, CancellationToken = cancellationToken },
                async (query, ct) => await query(ct).ConfigureAwait(false)).ConfigureAwait(false);

            var nbt = new Dictionary<int, NbtData>();
            foreach (var n in nbtRows.SelectMany(r => r))
                nbt[n.Id] = n;
            var bidsBy = bids.SelectMany(r => r).ToLookup(b => b.AuctionId, b => b.Bid);
            var lookupsBy = lookups.SelectMany(r => r).ToLookup(l => l.AuctionId);
            var enchantmentsBy = enchantments.SelectMany(r => r).ToLookup(e => e.SaveAuctionId);
            var coopBy = coop.SelectMany(r => r).ToLookup(c => c.AuctionId, c => new UuId(c.value));
            var claimedBy = claimed.SelectMany(r => r).ToLookup(c => c.AuctionId, c => new UuId(c.value));
            foreach (var a in page)
            {
                a.Bids = bidsBy[a.Id].ToList();
                a.NbtData = a.NbtDataId != null && nbt.TryGetValue(a.NbtDataId.Value, out var data) ? data : null;
                a.NBTLookup = lookupsBy[a.Id].ToList();
                a.Enchantments = enchantmentsBy[a.Id].ToList();
                a.CoopMembers = coopBy[a.Id].ToList();
                a.ClaimedBids = claimedBy[a.Id].ToList();
            }
        }

        // One method per child table, each opens its own read context (a DbContext is not thread-safe) and returns its rows ordered by Id.

        /// <summary>Bids of the auctions (by <c>Uuid</c> foreign key), ordered by bid id.</summary>
        protected virtual async Task<List<(int? AuctionId, SaveBids Bid)>> QueryBidsAsync(IReadOnlyList<int> chunk, CancellationToken cancellationToken)
        {
            using var db = ArchiveReadContext.Open(config);
            var nullableIds = chunk.Select(i => (int?)i).ToList();
            var rows = await db.Bids.AsNoTracking()
                .Where(b => nullableIds.Contains(EF.Property<int?>(b, "Uuid")))
                .OrderBy(b => b.Id)
                .Select(b => new { AuctionId = EF.Property<int?>(b, "Uuid"), Bid = b })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(b => (b.AuctionId, b.Bid)).ToList();
        }

        /// <summary>NBT lookups of the auctions.</summary>
        protected virtual async Task<List<NBTLookup>> QueryNbtLookupsAsync(IReadOnlyList<int> chunk, CancellationToken cancellationToken)
        {
            using var db = ArchiveReadContext.Open(config);
            var ids = chunk.ToList();
            return await db.NBTLookups.AsNoTracking()
                .Where(l => ids.Contains(l.AuctionId))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Enchantments of the auctions, ordered by id.</summary>
        protected virtual async Task<List<Enchantment>> QueryEnchantmentsAsync(IReadOnlyList<int> chunk, CancellationToken cancellationToken)
        {
            using var db = ArchiveReadContext.Open(config);
            var ids = chunk.ToList();
            return await db.Enchantment.AsNoTracking()
                .Where(e => ids.Contains(e.SaveAuctionId))
                .OrderBy(e => e.Id)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Coop members (<c>UuId.SaveAuctionId1</c>), ordered by id.</summary>
        protected virtual async Task<List<(int? AuctionId, string value)>> QueryCoopAsync(IReadOnlyList<int> chunk, CancellationToken cancellationToken)
        {
            using var db = ArchiveReadContext.Open(config);
            var nullableIds = chunk.Select(i => (int?)i).ToList();
            var rows = await db.Set<UuId>().AsNoTracking()
                .Where(u => nullableIds.Contains(EF.Property<int?>(u, "SaveAuctionId1")))
                .OrderBy(u => u.Id)
                .Select(u => new { AuctionId = EF.Property<int?>(u, "SaveAuctionId1"), u.value })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(c => (c.AuctionId, c.value)).ToList();
        }

        /// <summary>Claimed bidders (<c>UuId.SaveAuctionId</c>), ordered by id.</summary>
        protected virtual async Task<List<(int? AuctionId, string value)>> QueryClaimedAsync(IReadOnlyList<int> chunk, CancellationToken cancellationToken)
        {
            using var db = ArchiveReadContext.Open(config);
            var nullableIds = chunk.Select(i => (int?)i).ToList();
            var rows = await db.Set<UuId>().AsNoTracking()
                .Where(u => nullableIds.Contains(EF.Property<int?>(u, "SaveAuctionId")))
                .OrderBy(u => u.Id)
                .Select(u => new { AuctionId = EF.Property<int?>(u, "SaveAuctionId"), u.value })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return rows.Select(c => (c.AuctionId, c.value)).ToList();
        }

        /// <summary>Item bytes (<c>NbtData</c>) by their own ids.</summary>
        protected virtual async Task<List<NbtData>> QueryNbtDataAsync(IReadOnlyList<int> chunk, CancellationToken cancellationToken)
        {
            using var db = ArchiveReadContext.Open(config);
            var ids = chunk.ToList();
            return await db.Set<NbtData>().AsNoTracking()
                .Where(n => ids.Contains(n.Id))
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Insert the index rows of a verified tag-month in batches of <see cref="IndexBatchSize"/>. Idempotent:
        /// locations are upserted (a re-run points them at the latest verified part), bidders and sellers use INSERT IGNORE.
        /// All values are integers formatted invariantly, so the statements can not carry injected SQL.
        /// </summary>
        protected virtual async Task WriteIndexRowsAsync(ushort tagId, ushort month, IReadOnlyList<(long uid, byte part)> locations, IReadOnlyList<AuctionBidder> bidders, IReadOnlyList<AuctionSeller> sellers, CancellationToken cancellationToken)
        {
            // every statement is idempotent, so each is re-run on its own after a timeout or deadlock (see RetryPrimaryWriteAsync)
            foreach (var sql in BuildLocationInserts(tagId, month, locations))
                await RetryPrimaryWriteAsync("index_sql", () => ExecuteIndexSqlAsync(sql, cancellationToken), cancellationToken).ConfigureAwait(false);
            foreach (var sql in BuildBidderInserts(bidders))
                await RetryPrimaryWriteAsync("index_sql", () => ExecuteIndexSqlAsync(sql, cancellationToken), cancellationToken).ConfigureAwait(false);
            foreach (var sql in ArchiveIndexSql.BuildSellerInserts(sellers))
                await RetryPrimaryWriteAsync("index_sql", () => ExecuteIndexSqlAsync(sql, cancellationToken), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Executes one index INSERT on the primary with a fresh context (the connection of a timed out statement may be broken).</summary>
        protected virtual async Task ExecuteIndexSqlAsync(string sql, CancellationToken cancellationToken)
        {
            using var db = OpenPrimaryWriteContext();
            await db.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
        }

        /// <summary><c>Players.Id</c> of up to 100 uuids (sellers whose <c>SellerId</c> is 0); unknown uuids are absent.</summary>
        protected virtual Task<IReadOnlyDictionary<string, int>> ResolvePlayerIdsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken)
            => PlayerIdLookup.QueryPlayersAsync(uuids, cancellationToken);

        /// <summary>Insert or replace the <c>archived_months</c> row of a tag-month.</summary>
        protected virtual async Task UpsertArchivedMonthAsync(ArchivedMonth row, CancellationToken cancellationToken)
        {
            using var db = OpenPrimaryWriteContext();
            var existing = await db.ArchivedMonths.FindAsync(new object[] { row.TagId, row.Month }, cancellationToken).ConfigureAwait(false);
            if (existing == null)
                db.ArchivedMonths.Add(row);
            else
                db.Entry(existing).CurrentValues.SetValues(row);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Write the month sentinel (<c>tag_id = 0</c>) with the month totals.</summary>
        protected virtual async Task WriteMonthSentinelAsync(int month, CancellationToken cancellationToken)
        {
            int index = ArchiveMonths.ToIndex(month);
            var now = DateTime.UtcNow;
            var sha = EmptySha256;
            using var db = new ArchiveContext();
            await db.Database.ExecuteSqlAsync($@"INSERT INTO archived_months (tag_id, month, parts, `rows`, sold_rows, source, sha256, archived_at, verified_at, index_rows, index_bidder_rows)
SELECT 0, {index}, 0, COALESCE(SUM(`rows`), 0), COALESCE(SUM(sold_rows), 0), 'mariadb', {sha}, {now}, {now}, COALESCE(SUM(index_rows), 0), COALESCE(SUM(index_bidder_rows), 0)
FROM archived_months WHERE month = {index} AND tag_id <> 0
ON DUPLICATE KEY UPDATE `rows` = VALUES(`rows`), sold_rows = VALUES(sold_rows), archived_at = VALUES(archived_at), verified_at = VALUES(verified_at), index_rows = VALUES(index_rows), index_bidder_rows = VALUES(index_bidder_rows)", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Wait between steps (overridden in tests).</summary>
        protected virtual Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
        }

        // ---------------------------------------------------------------- SQL builders (moved to core ArchiveIndexSql, shared with SkyAuctions)

        internal static IEnumerable<string> BuildLocationInserts(ushort tagId, ushort month, IReadOnlyList<(long uid, byte part)> locations)
            => ArchiveIndexSql.BuildLocationInserts(tagId, month, locations);

        internal static IEnumerable<string> BuildBidderInserts(IReadOnlyList<AuctionBidder> bidders)
            => ArchiveIndexSql.BuildBidderInserts(bidders);
    }
}
