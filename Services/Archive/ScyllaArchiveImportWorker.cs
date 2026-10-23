using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>Sentinel state of a month for the import: does it exist, and its <c>scylla_at</c>.</summary>
    public record ScyllaImportMonthState(bool HasSentinel, DateTime? ScyllaAt);

    /// <summary>
    /// Imports the Scylla months that SkyAuctions' sweep archived to R2 (SkyAuctions' MariaDB user is read-only). Every
    /// <c>ARCHIVE:IMPORT_POLL_MINUTES</c> it lists <c>auctions/v2/_meta/scylla/</c>; for each <see cref="ScyllaMonthManifest"/> whose
    /// month sentinel exists without <c>scylla_at</c>, and each tag entry whose <c>archived_months</c> row has no <c>scylla_at</c>:
    /// the new parts are read uncached, one at a time, in two passes. Pass 1 verifies that every SHA-256 equals the manifest's
    /// (and the tag index ends with them) and that the row count matches, writing nothing. Pass 2 re-reads and re-verifies each part
    /// and writes its <c>auction_locations</c> (never moving another tag-month's location) and <c>auction_bidders</c>, so memory is
    /// bounded by one part; then the <c>archived_months</c> row is upserted with the merged totals and <c>scylla_at</c> (written last). When the manifest is
    /// completed and every entry is imported, the sentinel gets <c>scylla_at</c>, which lets the sweep start the next month.
    /// Idempotent: a re-run skips imported tags; an interrupted tag is imported again (upserted locations, INSERT IGNORE bidders).
    /// </summary>
    public class ScyllaArchiveImportWorker : BackgroundService
    {
        /// <summary>Uuids per <c>Players</c> lookup; MariaDB stops index dives at 200 IN values (phase 1 outage), stay well below.</summary>
        internal const int BidderLookupChunkSize = 100;
        /// <summary>Note column width of <c>archived_months</c>.</summary>
        internal const int NoteMaxLength = 255;
        /// <summary>Start of the import part of a note.</summary>
        internal const string NotePrefix = "scylla import:";

        private readonly IAuctionBlobStore store;
        private readonly IArchiveMetaStore meta;
        private readonly IConfiguration config;
        private readonly ILogger<ScyllaArchiveImportWorker> logger;
        private readonly Dictionary<string, ushort> tagIds = new(StringComparer.Ordinal);

        private static readonly Prometheus.Counter tagsImported = Prometheus.Metrics.CreateCounter("sky_indexer_scylla_import_tags", "Scylla tag-months imported into MariaDB");
        private static readonly Prometheus.Counter partsVerified = Prometheus.Metrics.CreateCounter("sky_indexer_scylla_import_parts_verified", "Scylla parts whose SHA-256 matched the manifest");
        private static readonly Prometheus.Counter hashMismatches = Prometheus.Metrics.CreateCounter("sky_indexer_scylla_import_hash_mismatches", "Scylla parts or tag index entries that did not match the manifest (tag skipped, nothing written)");
        private static readonly Prometheus.Counter indexRowsWritten = Prometheus.Metrics.CreateCounter("sky_indexer_scylla_import_index_rows", "auction_locations rows written for Scylla parts");
        private static readonly Prometheus.Counter bidderRowsWritten = Prometheus.Metrics.CreateCounter("sky_indexer_scylla_import_bidder_rows", "auction_bidders rows written for Scylla parts");
        private static readonly Prometheus.Counter bidsWithoutBidder = Prometheus.Metrics.CreateCounter("sky_indexer_scylla_import_bids_without_bidder", "Bids of Scylla parts not indexed (empty/anonymous bidder or no Players row)");
        private static readonly Prometheus.Counter monthsCompleted = Prometheus.Metrics.CreateCounter("sky_indexer_scylla_import_months_completed", "Month sentinels that got scylla_at");
        private static readonly Prometheus.Counter errors = Prometheus.Metrics.CreateCounter("sky_indexer_scylla_import_errors", "Scylla import errors (failed tags, failed passes)");

        /// <summary>DI constructor.</summary>
        public ScyllaArchiveImportWorker(IAuctionBlobStore store, IArchiveMetaStore meta, IConfiguration config, ILogger<ScyllaArchiveImportWorker> logger)
        {
            this.store = store;
            this.meta = meta;
            this.config = config;
            this.logger = logger;
        }

        /// <inheritdoc/>
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            var options = ArchiveOptions.From(config);
            if (!options.Enabled)
                return;
            if (!store.IsEnabled || meta == null)
            {
                logger.LogWarning("ARCHIVE:ENABLED=true but the S3 archive store is not configured; Scylla import will not run.");
                return;
            }
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
                    var completed = await RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    if (completed > 0)
                        logger.LogInformation("Scylla import: {Completed} months completed", completed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // e.g. the archive tables are not migrated yet (AuctionArchiveJob migrates them) or MariaDB is down
                    errors.Inc();
                    logger.LogError(ex, "Scylla import pass failed; retried in {Minutes} min", ArchiveOptions.From(config).ImportPollMinutes);
                }
                try
                {
                    await Delay(TimeSpan.FromMinutes(ArchiveOptions.From(config).ImportPollMinutes), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }

        /// <summary>One pass over every manifest; returns the number of month sentinels that got <c>scylla_at</c>.</summary>
        internal async Task<int> RunOnceAsync(CancellationToken cancellationToken)
        {
            var completed = 0;
            var keys = await meta.ListMetaKeysAsync(AuctionBlobKeys.ScyllaManifestPrefix, cancellationToken).ConfigureAwait(false);
            foreach (var key in keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var month = AuctionBlobKeys.ScyllaManifestMonth(key);
                if (month == null)
                    continue;
                var state = await GetMonthStateAsync(month.Value, cancellationToken).ConfigureAwait(false);
                if (!state.HasSentinel || state.ScyllaAt != null)
                    continue;
                var manifest = await meta.ReadMetaAsync<ScyllaMonthManifest>(key, cancellationToken).ConfigureAwait(false);
                if (manifest == null)
                    continue;
                if (manifest.Month != month.Value)
                {
                    errors.Inc();
                    logger.LogError("Scylla import: manifest {Key} is for month {Month}; skipped", key, manifest.Month);
                    continue;
                }
                var allImported = true;
                foreach (var entry in manifest.Tags ?? new List<ScyllaTagImport>())
                {
                    var row = await GetTagRowAsync(entry.Tag, month.Value, cancellationToken).ConfigureAwait(false);
                    if (row?.ScyllaAt != null)
                        continue;
                    bool imported;
                    try
                    {
                        imported = await ImportTagAsync(month.Value, entry, row, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Scylla import of {Tag} {Month} failed; retried next poll", entry.Tag, ArchiveMonths.Format(month.Value));
                        imported = false;
                    }
                    if (!imported)
                    {
                        errors.Inc();
                        allImported = false;
                    }
                }
                if (manifest.CompletedAt != null && allImported)
                {
                    await MarkMonthScyllaCompleteAsync(month.Value, cancellationToken).ConfigureAwait(false);
                    monthsCompleted.Inc();
                    completed++;
                    logger.LogInformation("Scylla import: {Month} complete ({Tags} tags with Scylla parts); sentinel marked", ArchiveMonths.Format(month.Value), manifest.Tags?.Count ?? 0);
                }
            }
            return completed;
        }

        /// <summary>Verify and index one tag entry. False (nothing written) when the parts or the tag index do not match the manifest.</summary>
        internal async Task<bool> ImportTagAsync(int month, ScyllaTagImport entry, ArchivedMonth row, CancellationToken cancellationToken)
        {
            var monthText = ArchiveMonths.Format(month);
            bool Reject(string error, bool hashMismatch = false)
            {
                if (hashMismatch)
                    hashMismatches.Inc();
                logger.LogError("Scylla import of {Tag} {Month} skipped: {Error}; nothing written", entry.Tag, monthText, error);
                return false;
            }

            var phaseOneParts = row?.Parts ?? 0;
            var newHashes = entry.NewPartSha256 ?? new List<string>();
            if (entry.NewParts <= 0 || newHashes.Count != entry.NewParts)
                return Reject($"manifest entry has {entry.NewParts} parts and {newHashes.Count} hashes");
            if (entry.FirstNewPart != phaseOneParts)
                return Reject($"manifest says the first new part is {entry.FirstNewPart}, archived_months has {phaseOneParts} parts");
            if (phaseOneParts + entry.NewParts > byte.MaxValue)
                return Reject($"{phaseOneParts + entry.NewParts} parts do not fit archived_months.parts");

            // the tag index must be the phase-1 entry followed by exactly the manifest's parts
            var indexEntry = (await store.ReadIndexAsync(entry.Tag, cancellationToken).ConfigureAwait(false))?.Find(month);
            var allHashes = indexEntry?.PartSha256 ?? new List<string>();
            if (indexEntry == null || indexEntry.Parts != phaseOneParts + entry.NewParts || allHashes.Count != indexEntry.Parts
                || !allHashes.Skip(phaseOneParts).SequenceEqual(newHashes, StringComparer.OrdinalIgnoreCase))
                return Reject("the tag index entry does not end with the manifest's part hashes", hashMismatch: true);
            if (row != null && !string.Equals(ArchivedAuctionBlock.CombinedSha256(allHashes.Take(phaseOneParts).ToList()), row.Sha256, StringComparison.OrdinalIgnoreCase))
                return Reject("the tag index's phase-1 hashes no longer combine to archived_months.sha256", hashMismatch: true);

            // pass 1: verify every new part (one block in memory at a time), nothing is written
            var rows = 0;
            var sold = 0;
            for (var i = 0; i < entry.NewParts; i++)
            {
                var part = entry.FirstNewPart + i;
                var block = await store.ReadBlockAsync(entry.Tag, month, part, uncached: true, cancellationToken).ConfigureAwait(false);
                if (block == null)
                    return Reject($"part {part} is missing");
                var sha = block.Sha256();
                if (!string.Equals(sha, newHashes[i], StringComparison.OrdinalIgnoreCase))
                    return Reject($"part {part} hashes to {sha}, the manifest says {newHashes[i]}", hashMismatch: true);
                partsVerified.Inc();
                rows += block.RowCount;
                sold += block.IsSold.Count(s => s);
            }
            if (rows != entry.NewRows)
                return Reject($"the parts hold {rows} rows, the manifest says {entry.NewRows}");

            // pass 2: write the index rows part by part (idempotent, so a crash here is safe for the re-run)
            var tagId = await GetOrCreateTagIdAsync(entry.Tag, cancellationToken).ConfigureAwait(false);
            var monthIndex = ArchiveMonths.ToIndex(month);
            long locationCount = 0;
            long bidderCount = 0;
            long withoutBidder = 0;
            for (var i = 0; i < entry.NewParts; i++)
            {
                var part = entry.FirstNewPart + i;
                var block = await store.ReadBlockAsync(entry.Tag, month, part, uncached: true, cancellationToken).ConfigureAwait(false);
                if (block == null)
                    return Reject($"part {part} is missing");
                var sha = block.Sha256();
                if (!string.Equals(sha, newHashes[i], StringComparison.OrdinalIgnoreCase))
                    return Reject($"part {part} hashes to {sha}, the manifest says {newHashes[i]}", hashMismatch: true);
                var locations = new List<(long uid, byte part)>();
                var bids = new Dictionary<(string bidder, long uid), (uint end, ulong amount)>();
                long partWithoutBidder = 0;
                foreach (var auction in block.ToAuctions())
                {
                    locations.Add((auction.UId, (byte)part));
                    var end = (uint)new DateTimeOffset(DateTime.SpecifyKind(auction.End, DateTimeKind.Utc)).ToUnixTimeSeconds();
                    foreach (var bid in auction.Bids)
                    {
                        if (bid.Amount < 0 || !IsPlayerUuid(bid.Bidder))
                        {
                            partWithoutBidder++;
                            continue;
                        }
                        var key = (bid.Bidder, auction.UId);
                        if (!bids.TryGetValue(key, out var existing) || existing.amount < (ulong)bid.Amount)
                            bids[key] = (end, (ulong)bid.Amount);
                    }
                }
                var bidders = await ResolveBiddersAsync(bids, cancellationToken).ConfigureAwait(false);
                partWithoutBidder += bids.Count - bidders.Count;
                await WriteIndexRowsAsync(tagId, monthIndex, locations, bidders, cancellationToken).ConfigureAwait(false);
                indexRowsWritten.Inc(locations.Count);
                bidderRowsWritten.Inc(bidders.Count);
                locationCount += locations.Count;
                bidderCount += bidders.Count;
                withoutBidder += partWithoutBidder;
            }
            bidsWithoutBidder.Inc(withoutBidder);

            // the archived_months row with scylla_at (the marker) last
            var now = DateTime.UtcNow;
            await UpsertArchivedMonthAsync(new ArchivedMonth
            {
                TagId = tagId,
                Month = monthIndex,
                Parts = (byte)(phaseOneParts + entry.NewParts),
                Rows = (row?.Rows ?? 0) + (uint)rows,
                SoldRows = (row?.SoldRows ?? 0) + (uint)sold,
                Source = phaseOneParts > 0 ? ArchivedAuctionBlock.SourceMerged : ArchivedAuctionBlock.SourceScylla,
                Sha256 = ArchivedAuctionBlock.CombinedSha256(allHashes),
                ArchivedAt = row?.ArchivedAt ?? entry.VerifiedAt,
                VerifiedAt = now,
                // the retention worker deletes MariaDB rows only while index_rows == rows; both grow by the new rows
                IndexRows = (row?.IndexRows ?? 0) + (uint)locationCount,
                IndexBidderRows = (row?.IndexBidderRows ?? 0) + (uint)bidderCount,
                DeletedRows = row?.DeletedRows,
                DeletedAt = row?.DeletedAt,
                Note = BuildNote(row?.Note, rows, entry.NewParts, withoutBidder),
                ScyllaAt = now,
            }, cancellationToken).ConfigureAwait(false);
            tagsImported.Inc();
            logger.LogInformation("Scylla import {Tag} {Month}: +{Rows} rows in {Parts} parts, {Locations} locations, {Bidders} bidder rows",
                entry.Tag, monthText, rows, entry.NewParts, locationCount, bidderCount);
            return true;
        }

        /// <summary>32-char lowercase hex, not all zeros (anonymised bids carry the empty uuid).</summary>
        internal static bool IsPlayerUuid(string uuid)
        {
            if (uuid == null || uuid.Length != 32)
                return false;
            var nonZero = false;
            foreach (var c in uuid)
            {
                if (!(c >= '0' && c <= '9' || c >= 'a' && c <= 'f'))
                    return false;
                nonZero |= c != '0';
            }
            return nonZero;
        }

        /// <summary><c>auction_bidders</c> rows: bidder uuids resolved to <c>Players.Id</c> in chunks of at most <see cref="BidderLookupChunkSize"/>; unknown players are left out.</summary>
        private async Task<List<AuctionBidder>> ResolveBiddersAsync(Dictionary<(string bidder, long uid), (uint end, ulong amount)> bids, CancellationToken cancellationToken)
        {
            var uuids = bids.Keys.Select(k => k.bidder).Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal).ToList();
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var offset = 0; offset < uuids.Count; offset += BidderLookupChunkSize)
            {
                var chunk = uuids.GetRange(offset, Math.Min(BidderLookupChunkSize, uuids.Count - offset));
                foreach (var (uuid, id) in await ResolveBidderIdsAsync(chunk, cancellationToken).ConfigureAwait(false))
                    ids[uuid] = id;
            }
            var result = new List<AuctionBidder>(bids.Count);
            foreach (var ((bidder, uid), (end, amount)) in bids)
                if (ids.TryGetValue(bidder, out var id) && id > 0)
                    result.Add(new AuctionBidder { BidderId = (uint)id, Uid = uid, End = end, Amount = amount });
            return result;
        }

        /// <summary>
        /// <c>archived_months.note</c> after the import: <c>scylla import: +{rows} rows in {parts} parts[, {n} bids without bidder]</c>
        /// appended to the phase-1 note (an earlier import note is replaced), at most 255 chars.
        /// </summary>
        internal static string BuildNote(string existing, int rows, int parts, long bidsWithoutBidder)
        {
            var note = $"{NotePrefix} +{rows} rows in {parts} parts";
            if (bidsWithoutBidder > 0)
                note += $", {bidsWithoutBidder} bids without bidder";
            if (note.Length > NoteMaxLength)
                note = note[..NoteMaxLength];
            var kept = existing;
            var earlier = kept?.IndexOf(NotePrefix, StringComparison.Ordinal) ?? -1;
            if (earlier >= 0)
                kept = kept[..earlier].TrimEnd(' ', ';');
            if (string.IsNullOrEmpty(kept))
                return note;
            const string separator = "; ";
            var room = NoteMaxLength - note.Length - separator.Length;
            if (room <= 0)
                return note;
            return (kept.Length > room ? kept[..room] : kept) + separator + note;
        }

        /// <summary>The SQL of the index rows: locations never move an auction archived in another tag-month, bidders are INSERT IGNORE.</summary>
        internal static IEnumerable<string> BuildIndexSql(ushort tagId, ushort month, IReadOnlyList<(long uid, byte part)> locations, IReadOnlyList<AuctionBidder> bidders)
            => ArchiveIndexSql.BuildLocationInserts(tagId, month, locations, keepOtherTagMonths: true).Concat(ArchiveIndexSql.BuildBidderInserts(bidders));

        // ---------------------------------------------------------------- data access seams

        /// <summary>Sentinel (<c>tag_id = 0</c>) of a month: does it exist, and its <c>scylla_at</c>.</summary>
        protected virtual async Task<ScyllaImportMonthState> GetMonthStateAsync(int month, CancellationToken cancellationToken)
        {
            var index = ArchiveMonths.ToIndex(month);
            using var db = new ArchiveContext();
            var sentinel = await db.ArchivedMonths.AsNoTracking().Where(m => m.TagId == 0 && m.Month == index)
                .Select(m => new { m.ScyllaAt }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            return new ScyllaImportMonthState(sentinel != null, sentinel?.ScyllaAt);
        }

        /// <summary>The <c>archived_months</c> row of a tag-month, null when the tag or the row does not exist.</summary>
        protected virtual async Task<ArchivedMonth> GetTagRowAsync(string tag, int month, CancellationToken cancellationToken)
        {
            var index = ArchiveMonths.ToIndex(month);
            using var db = new ArchiveContext();
            var tagId = await db.AuctionTags.AsNoTracking().Where(t => t.Tag == tag).Select(t => (ushort?)t.Id).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
            if (tagId == null)
                return null;
            return await db.ArchivedMonths.AsNoTracking().FirstOrDefaultAsync(m => m.TagId == tagId && m.Month == index, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Id of a tag in <c>auction_tags</c>, inserted on first use (same race handling as <see cref="AuctionArchiveJob"/>).</summary>
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

        /// <summary><c>Players.Id</c> of up to <see cref="BidderLookupChunkSize"/> uuids (32-char hex, primary key lookups); unknown uuids are absent.</summary>
        protected virtual async Task<IReadOnlyDictionary<string, int>> ResolveBidderIdsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken)
        {
            using var db = new HypixelContext();
            var list = uuids.ToList();
            var rows = await db.Players.AsNoTracking().Where(p => list.Contains(p.UuId)).Select(p => new { p.UuId, p.Id }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in rows)
                result[r.UuId] = r.Id;
            return result;
        }

        /// <summary>Write the index rows (<see cref="BuildIndexSql"/>, idempotent).</summary>
        protected virtual async Task WriteIndexRowsAsync(ushort tagId, ushort month, IReadOnlyList<(long uid, byte part)> locations, IReadOnlyList<AuctionBidder> bidders, CancellationToken cancellationToken)
        {
            using var db = new ArchiveContext();
            foreach (var sql in BuildIndexSql(tagId, month, locations, bidders))
                await db.Database.ExecuteSqlRawAsync(sql, cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Insert the row, or update only the columns the import owns on an existing one (never <c>deleted_rows</c>/<c>deleted_at</c>/<c>archived_at</c>,
        /// which the retention worker may be updating concurrently).
        /// </summary>
        protected virtual async Task UpsertArchivedMonthAsync(ArchivedMonth row, CancellationToken cancellationToken)
        {
            using var db = new ArchiveContext();
            var existing = await db.ArchivedMonths.FindAsync(new object[] { row.TagId, row.Month }, cancellationToken).ConfigureAwait(false);
            if (existing == null)
            {
                db.ArchivedMonths.Add(row);
            }
            else
            {
                existing.Parts = row.Parts;
                existing.Rows = row.Rows;
                existing.SoldRows = row.SoldRows;
                existing.Source = row.Source;
                existing.Sha256 = row.Sha256;
                existing.VerifiedAt = row.VerifiedAt;
                existing.IndexRows = row.IndexRows;
                existing.IndexBidderRows = row.IndexBidderRows;
                existing.Note = row.Note;
                existing.ScyllaAt = row.ScyllaAt;
            }
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Set <c>scylla_at</c> on the month sentinel (lets the sweep start the next month).</summary>
        protected virtual async Task MarkMonthScyllaCompleteAsync(int month, CancellationToken cancellationToken)
        {
            int index = ArchiveMonths.ToIndex(month);
            var now = DateTime.UtcNow;
            using var db = new ArchiveContext();
            await db.Database.ExecuteSqlAsync($"UPDATE archived_months SET scylla_at = {now} WHERE tag_id = 0 AND month = {index} AND scylla_at IS NULL", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Wait between polls (overridden in tests).</summary>
        protected virtual Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            return delay <= TimeSpan.Zero ? Task.CompletedTask : Task.Delay(delay, cancellationToken);
        }
    }
}
