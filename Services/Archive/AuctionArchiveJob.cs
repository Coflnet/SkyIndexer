using System;
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
        private readonly Dictionary<string, ushort> tagIds = new(StringComparer.Ordinal);

        private static readonly Prometheus.Counter monthsTotal = Prometheus.Metrics.CreateCounter("sky_indexer_archive_months_total", "Months fully archived (sentinel written)");
        private static readonly Prometheus.Counter partsWritten = Prometheus.Metrics.CreateCounter("sky_indexer_archive_parts_written", "Archive parts written");
        private static readonly Prometheus.Counter partsVerified = Prometheus.Metrics.CreateCounter("sky_indexer_archive_parts_verified", "Archive parts verified by uncached read-back");
        private static readonly Prometheus.Counter verifyFailures = Prometheus.Metrics.CreateCounter("sky_indexer_archive_verify_failures", "Archive parts whose read-back did not match");
        private static readonly Prometheus.Counter rowsArchived = Prometheus.Metrics.CreateCounter("sky_indexer_archive_rows_archived", "Auction rows in verified parts");
        private static readonly Prometheus.Counter indexRowsWritten = Prometheus.Metrics.CreateCounter("sky_indexer_archive_index_rows", "auction_locations rows written");
        private static readonly Prometheus.Counter bidderRowsWritten = Prometheus.Metrics.CreateCounter("sky_indexer_archive_bidder_rows", "auction_bidders rows written");
        private static readonly Prometheus.Counter bidsWithoutBidder = Prometheus.Metrics.CreateCounter("sky_indexer_archive_bids_without_bidder", "Bids skipped for auction_bidders because BidderId is 0");
        private static readonly Prometheus.Counter itemMonthsSkipped = Prometheus.Metrics.CreateCounter("sky_indexer_archive_item_months_skipped", "ItemId-months skipped because the item's first auction ends in a later month");
        private static readonly Prometheus.Counter pages = Prometheus.Metrics.CreateCounter("sky_indexer_archive_pages", "Auction pages read from MariaDB");
        private static readonly Prometheus.Counter errors = Prometheus.Metrics.CreateCounter("sky_indexer_archive_errors", "Archive errors (tag-months that failed with an exception, tag collisions)");
        private static readonly Prometheus.Gauge currentMonth = Prometheus.Metrics.CreateGauge("sky_indexer_archive_current_month", "Month (yyyyMM) being archived");
        private static readonly Prometheus.Gauge lastMonthCompleted = Prometheus.Metrics.CreateGauge("sky_indexer_archive_last_month_completed_timestamp", "Unix time the last month sentinel was written");

        /// <summary>DI constructor.</summary>
        public AuctionArchiveJob(IAuctionBlobStore store, IConfiguration config, ILogger<AuctionArchiveJob> logger)
        {
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
            var runs = new Dictionary<string, TagMonthRun>(StringComparer.Ordinal);
            foreach (var itemId in itemIds)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (firstMonths != null && firstMonths.TryGetValue(itemId, out var first) && first > month)
                {
                    itemMonthsSkipped.Inc();
                    continue;
                }
                var (ok, rows) = await ArchiveItemMonthAsync(itemId, month, runs, options, cancellationToken).ConfigureAwait(false);
                if (!ok)
                    return false;
                // an empty ItemId-month was one cheap index lookup; only pause after real work
                if (rows > 0)
                    await Delay(TimeSpan.FromMilliseconds(options.TagPauseMs), cancellationToken).ConfigureAwait(false);
            }
            var allFinalized = true;
            foreach (var run in runs.Values.OrderBy(r => r.Tag, StringComparer.Ordinal))
                allFinalized &= await FinalizeTagMonthAsync(run, cancellationToken).ConfigureAwait(false);
            return allFinalized;
        }

        /// <summary>Page one ItemId of a month into the per-tag runs; the index rows of the item are written after its parts verified.</summary>
        internal async Task<(bool ok, int rows)> ArchiveItemMonthAsync(int itemId, int month, Dictionary<string, TagMonthRun> runs, ArchiveOptions options, CancellationToken cancellationToken)
        {
            var (monthStart, monthEnd) = ArchiveMonths.MonthRange(month);
            var buffers = new Dictionary<string, List<SaveAuction>>(StringComparer.Ordinal);
            var indexes = new Dictionary<string, ItemRunIndex>(StringComparer.Ordinal);
            var cursorEnd = monthStart;
            var cursorId = 0;
            var total = 0;
            while (true)
            {
                var page = await LoadPageAsync(itemId, cursorEnd, cursorId, monthEnd, options.PageSize, cancellationToken).ConfigureAwait(false);
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
                        return (false, total);
                    }
                    if (!buffers.TryGetValue(row.Tag, out var buffer))
                        buffers[row.Tag] = buffer = new List<SaveAuction>();
                    buffer.Add(row);
                    total++;
                    if (buffer.Count >= options.MaxRowsPerPart && !await FlushPartAsync(runs, indexes, row.Tag, month, buffer, cancellationToken).ConfigureAwait(false))
                        return (false, total);
                }
                if (page.Count < options.PageSize)
                    break;
                await Delay(TimeSpan.FromMilliseconds(options.PagePauseMs), cancellationToken).ConfigureAwait(false);
            }
            foreach (var (tag, buffer) in buffers.OrderBy(b => b.Key, StringComparer.Ordinal))
                if (buffer.Count > 0 && !await FlushPartAsync(runs, indexes, tag, month, buffer, cancellationToken).ConfigureAwait(false))
                    return (false, total);

            var monthIndex = ArchiveMonths.ToIndex(month);
            foreach (var (tag, index) in indexes.OrderBy(i => i.Key, StringComparer.Ordinal))
            {
                var tagId = await GetOrCreateTagIdAsync(tag, cancellationToken).ConfigureAwait(false);
                var bidders = index.Bidders.Select(kv => new AuctionBidder { BidderId = kv.Key.bidder, Uid = kv.Key.uid, End = kv.Value.end, Amount = kv.Value.amount }).ToList();
                await WriteIndexRowsAsync(tagId, monthIndex, index.Locations, bidders, cancellationToken).ConfigureAwait(false);
                indexRowsWritten.Inc(index.Locations.Count);
                bidderRowsWritten.Inc(bidders.Count);
                runs[tag].IndexRows += index.Locations.Count;
                runs[tag].IndexBidderRows += bidders.Count;
            }
            return (true, total);
        }

        /// <summary>
        /// The R2 tag index and the verified <c>archived_months</c> marker of a tag-month (written last). The index write is
        /// conditional (the SkyAuctions Scylla sweep writes the same per-tag object); on a conflict the index is re-read and the
        /// entry re-applied, up to <see cref="ArchiveIndexConflictException.MaxWriteAttempts"/> times. False (no marker) when it never got through.
        /// </summary>
        private async Task<bool> FinalizeTagMonthAsync(TagMonthRun run, CancellationToken cancellationToken)
        {
            var tagId = await GetOrCreateTagIdAsync(run.Tag, cancellationToken).ConfigureAwait(false);
            var now = DateTime.UtcNow;
            var entry = new ArchivedMonthEntry
            {
                Month = run.Month,
                Parts = run.PartHashes.Count,
                Rows = run.Rows,
                SoldRows = run.SoldRows,
                PartSha256 = run.PartHashes.ToList(),
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
            await UpsertArchivedMonthAsync(new ArchivedMonth
            {
                TagId = tagId,
                Month = ArchiveMonths.ToIndex(run.Month),
                Parts = checked((byte)run.PartHashes.Count),
                Rows = (uint)run.Rows,
                SoldRows = (uint)run.SoldRows,
                Source = ArchivedAuctionBlock.SourceMariaDb,
                Sha256 = ArchivedAuctionBlock.CombinedSha256(run.PartHashes),
                ArchivedAt = run.ArchivedAt ?? now,
                VerifiedAt = now,
                IndexRows = (uint)run.IndexRows,
                IndexBidderRows = (uint)run.IndexBidderRows,
                Note = run.BidsWithoutBidder > 0 ? $"{run.BidsWithoutBidder} bids without bidder id not indexed" : null,
            }, cancellationToken).ConfigureAwait(false);
            logger.LogInformation("Archived {Rows} auctions of {Tag} {Month} in {Parts} parts", run.Rows, run.Tag, run.Month, run.PartHashes.Count);
            return true;
        }

        /// <summary>Write the buffered rows of a tag as that tag-month's next part, verify by uncached read-back and collect the index rows. Clears <paramref name="rows"/>.</summary>
        private async Task<bool> FlushPartAsync(Dictionary<string, TagMonthRun> runs, Dictionary<string, ItemRunIndex> indexes, string tag, int month, List<SaveAuction> rows, CancellationToken cancellationToken)
        {
            if (!runs.TryGetValue(tag, out var run))
                runs[tag] = run = new TagMonthRun(tag, month);
            if (!indexes.TryGetValue(tag, out var index))
                indexes[tag] = index = new ItemRunIndex();
            var part = run.PartHashes.Count;
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
            run.PartHashes.Add(block.Sha256());
            run.Rows += block.RowCount;
            run.SoldRows += block.IsSold.Count(s => s);
            foreach (var row in rows)
            {
                index.Locations.Add((row.UId, (byte)part));
                if (row.Bids == null)
                    continue;
                var end = (uint)new DateTimeOffset(DateTime.SpecifyKind(row.End, DateTimeKind.Utc)).ToUnixTimeSeconds();
                foreach (var bid in row.Bids)
                {
                    if (bid.BidderId <= 0 || bid.Amount < 0)
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
            public List<string> PartHashes { get; } = new();
            public int Rows { get; set; }
            public int SoldRows { get; set; }
            public int IndexRows { get; set; }
            public int IndexBidderRows { get; set; }
            public long BidsWithoutBidder { get; set; }
            public DateTime? ArchivedAt { get; set; }
        }

        /// <summary>Index tuples of one tag collected while one ItemId is paged.</summary>
        internal sealed class ItemRunIndex
        {
            public List<(long uid, byte part)> Locations { get; } = new();
            public Dictionary<(uint bidder, long uid), (uint end, ulong amount)> Bidders { get; } = new();
        }

        // ---------------------------------------------------------------- data access seams

        /// <summary>Create the archive tables (own migrations history table); only called when the job is enabled.</summary>
        protected virtual async Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            using var db = new ArchiveContext();
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Distinct <c>ItemId</c>s of <c>Auctions</c> with the <c>End</c> of their first auction (loose index scan on
        /// <c>IX_Auctions_ItemId_End</c>, a few thousand rows).</summary>
        protected virtual async Task<IReadOnlyDictionary<int, DateTime>> GetAuctionItemFirstEndsAsync(CancellationToken cancellationToken)
        {
            using var db = new HypixelContext();
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
            using var db = new ArchiveContext();
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
                catch (DbUpdateException)
                {
                    using var retry = new ArchiveContext();
                    existing = await retry.AuctionTags.AsNoTracking().Where(t => t.Tag == tag).Select(t => (ushort?)t.Id).FirstAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            tagIds[tag] = existing.Value;
            return existing.Value;
        }

        /// <summary>One keyset page of auctions without children, see <see cref="PageFilter"/>.</summary>
        protected virtual async Task<List<SaveAuction>> LoadPageAsync(int itemId, DateTime cursorEnd, int cursorId, DateTime monthEnd, int pageSize, CancellationToken cancellationToken)
        {
            using var db = new HypixelContext();
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
        internal const int ChildQueryBatchSize = 100;

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

        /// <summary>Load bids, item bytes, nbt lookups, enchantments, coop members and claimed bidders of a page with one query per table and <see cref="ChildQueryBatchSize"/> ids.</summary>
        protected virtual async Task LoadChildrenAsync(List<SaveAuction> page, CancellationToken cancellationToken)
        {
            var ids = page.Select(a => a.Id).ToList();
            var nbtIds = page.Where(a => a.NbtDataId != null).Select(a => a.NbtDataId.Value).Distinct().ToList();
            using var db = new HypixelContext();
            var bids = new List<(int? AuctionId, SaveBids Bid)>();
            var nbt = new Dictionary<int, NbtData>();
            var lookups = new List<NBTLookup>();
            var enchantments = new List<Enchantment>();
            var coop = new List<(int? AuctionId, string value)>();
            var claimed = new List<(int? AuctionId, string value)>();
            foreach (var chunk in Chunk(ids, ChildQueryBatchSize))
            {
                var nullableIds = chunk.Select(i => (int?)i).ToList();
                var chunkBids = await db.Bids.AsNoTracking()
                    .Where(b => nullableIds.Contains(EF.Property<int?>(b, "Uuid")))
                    .OrderBy(b => b.Id)
                    .Select(b => new { AuctionId = EF.Property<int?>(b, "Uuid"), Bid = b })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                bids.AddRange(chunkBids.Select(b => (b.AuctionId, b.Bid)));
                lookups.AddRange(await db.NBTLookups.AsNoTracking()
                    .Where(l => chunk.Contains(l.AuctionId))
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                enchantments.AddRange(await db.Enchantment.AsNoTracking()
                    .Where(e => chunk.Contains(e.SaveAuctionId))
                    .OrderBy(e => e.Id)
                    .ToListAsync(cancellationToken).ConfigureAwait(false));
                var chunkCoop = await db.Set<UuId>().AsNoTracking()
                    .Where(u => nullableIds.Contains(EF.Property<int?>(u, "SaveAuctionId1")))
                    .OrderBy(u => u.Id)
                    .Select(u => new { AuctionId = EF.Property<int?>(u, "SaveAuctionId1"), u.value })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                coop.AddRange(chunkCoop.Select(c => (c.AuctionId, c.value)));
                var chunkClaimed = await db.Set<UuId>().AsNoTracking()
                    .Where(u => nullableIds.Contains(EF.Property<int?>(u, "SaveAuctionId")))
                    .OrderBy(u => u.Id)
                    .Select(u => new { AuctionId = EF.Property<int?>(u, "SaveAuctionId"), u.value })
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                claimed.AddRange(chunkClaimed.Select(c => (c.AuctionId, c.value)));
            }
            foreach (var chunk in Chunk(nbtIds, ChildQueryBatchSize))
            {
                var rows = await db.Set<NbtData>().AsNoTracking()
                    .Where(n => chunk.Contains(n.Id))
                    .ToListAsync(cancellationToken).ConfigureAwait(false);
                foreach (var n in rows)
                    nbt[n.Id] = n;
            }

            var bidsBy = bids.ToLookup(b => b.AuctionId, b => b.Bid);
            var lookupsBy = lookups.ToLookup(l => l.AuctionId);
            var enchantmentsBy = enchantments.ToLookup(e => e.SaveAuctionId);
            var coopBy = coop.ToLookup(c => c.AuctionId, c => new UuId(c.value));
            var claimedBy = claimed.ToLookup(c => c.AuctionId, c => new UuId(c.value));
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

        /// <summary>
        /// Insert the index rows of a verified tag-month in batches of <see cref="IndexBatchSize"/>. Idempotent:
        /// locations are upserted (a re-run points them at the latest verified part), bidders use INSERT IGNORE.
        /// All values are integers formatted invariantly, so the statements can not carry injected SQL.
        /// </summary>
        protected virtual async Task WriteIndexRowsAsync(ushort tagId, ushort month, IReadOnlyList<(long uid, byte part)> locations, IReadOnlyList<AuctionBidder> bidders, CancellationToken cancellationToken)
        {
            using var db = new ArchiveContext();
            foreach (var sql in BuildLocationInserts(tagId, month, locations))
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
            foreach (var sql in BuildBidderInserts(bidders))
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Insert or replace the <c>archived_months</c> row of a tag-month.</summary>
        protected virtual async Task UpsertArchivedMonthAsync(ArchivedMonth row, CancellationToken cancellationToken)
        {
            using var db = new ArchiveContext();
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
