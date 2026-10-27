using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Indexer.Archive
{
    /// <summary>What <see cref="ArchivePlayerErasure.EraseAsync"/> did.</summary>
    public record ArchiveErasureResult(int IndexedAuctions, int Parts, int PartsRewritten, int RowsChanged, long IndexRowsDeleted);

    /// <summary>An archived tag-month as the erasure needs it (<c>archived_months</c> tag row plus the tag name).</summary>
    public record ErasureTag(ushort TagId, string Tag, int Parts, string Source);

    /// <summary>An archived month whose seller rows are not complete yet (no <c>auction_seller_backfill</c> marker).</summary>
    public record ErasureMonth(int Month, IReadOnlyList<ErasureTag> Tags);

    /// <summary>
    /// Erasure of one player from the auction archive, mirroring <c>privacy erase</c> for MariaDB (<see cref="PrivacyExport"/>): the
    /// auctions stay (economic data), the identity is replaced exactly like <see cref="PlayerOptOut.Anonymize(SaveAuction)"/> /
    /// <see cref="PlayerOptOut.Anonymize(SaveBids)"/> do (seller id and bidder id 0, a fresh anonymous uuid, profile id cleared), the
    /// player's coop-member and claimed-bid entries are removed, and the Scylla-only names (highest bidder, profile, coop) of those rows are cleared.
    /// <para>Ordering (resumable, idempotent): 1. the player's auctions are located through <c>auction_sellers</c> + <c>auction_bidders</c>
    /// + <c>auction_locations</c>, 2. every affected part is rewritten, uploaded and read back uncached (any difference throws),
    /// 3. the part hashes in the R2 tag index and <c>archived_months.sha256</c> are updated, 4. only then the player's rows in both
    /// index tables are deleted. A failure anywhere before step 4 leaves the index rows in place, so a re-run finds the same parts
    /// (already anonymised parts are not rewritten again) and continues; index rows are never deleted while a blob still holds the identity.</para>
    /// <para>Finding the player's auctions is complete without relying on the seller index alone: (a) the index rows, (b) the player's
    /// auctions still in MariaDB mapped to their archive location, (c) for archived months without the retention pass marker, every
    /// Scylla-sourced/merged part is decoded and matched on the uuid (those rows are neither in MariaDB nor guaranteed to have seller rows yet).
    /// Before anything is written, every affected tag-month must be in the R2 tag index and must not have a pending Scylla import (a rewrite
    /// would break the import's manifest hash check); otherwise the run throws and nothing is changed.</para>
    /// Scope as for the SQL erasure: numbered seller/bidder identities, not coop-only appearances.
    /// </summary>
    public class ArchivePlayerErasure
    {
        private readonly IAuctionBlobStore store;
        private readonly IArchiveMetaStore meta;
        private readonly IArchiveLocationLookup lookup;
        private readonly ILogger logger;

        private static readonly Prometheus.Counter players = Prometheus.Metrics.CreateCounter("sky_indexer_archive_erasure_players", "Players erased from the auction archive");
        private static readonly Prometheus.Counter partsRewritten = Prometheus.Metrics.CreateCounter("sky_indexer_archive_erasure_parts_rewritten", "Archive parts rewritten (anonymised, uploaded, verified) by player erasure");
        private static readonly Prometheus.Counter rowsAnonymised = Prometheus.Metrics.CreateCounter("sky_indexer_archive_erasure_rows", "Archived auctions anonymised by player erasure");
        private static readonly Prometheus.Counter indexRowsDeleted = Prometheus.Metrics.CreateCounter("sky_indexer_archive_erasure_index_rows_deleted", "auction_sellers/auction_bidders rows deleted by player erasure");

        /// <summary>Create with the blob store, its meta store (Scylla manifests), the location lookup and a logger.</summary>
        public ArchivePlayerErasure(IAuctionBlobStore store, IArchiveMetaStore meta, IArchiveLocationLookup lookup, ILogger logger)
        {
            this.store = store;
            this.meta = meta;
            this.lookup = lookup;
            this.logger = logger;
        }

        /// <summary>Erase <paramref name="uuid"/> (32-char hex, <c>Players.Id</c> <paramref name="playerId"/>) from the archive.</summary>
        public async Task<ArchiveErasureResult> EraseAsync(uint playerId, string uuid, CancellationToken cancellationToken)
        {
            if (playerId == 0 || !PrivacyExport.CanonicalUuid(uuid))
                throw new ArgumentException("A numbered player and its canonical uuid are required");
            // (a) index rows and (b) the player's auctions still in MariaDB
            var uids = (await GetIndexedUidsAsync(playerId, cancellationToken).ConfigureAwait(false))
                .Concat(await GetMariaDbUidsAsync(playerId, uuid, cancellationToken).ConfigureAwait(false)).Distinct().ToList();
            var located = new Dictionary<long, AuctionLocation>();
            foreach (var chunk in uids.Chunk(EfArchiveLocationLookup.LocationChunkSize * 10))
                foreach (var (uid, location) in await lookup.GetLocationsAsync(chunk, cancellationToken).ConfigureAwait(false))
                    located[uid] = location;
            if (located.Count < uids.Count)
                logger.LogInformation("Archive erasure: {Missing} of {Count} auctions of player {PlayerId} are not archived (yet) and need no rewrite", uids.Count - located.Count, uids.Count, playerId);

            var targets = new Dictionary<(string tag, int month, int part), ushort>();
            foreach (var l in located.Values)
            {
                var tagName = await lookup.GetTagNameAsync(l.TagId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"auction_tags has no row for tag id {l.TagId}");
                targets[(tagName, ArchiveMonths.FromIndex(l.Month), l.Part)] = l.TagId;
            }
            // (c) Scylla-sourced rows of months whose seller rows are not complete yet: decode and match the uuid
            var scanned = 0;
            foreach (var month in await GetUnmarkedMonthsAsync(cancellationToken).ConfigureAwait(false))
            {
                ScyllaMonthManifest manifest = null;
                if (month.Tags.Any(t => t.Source == ArchivedAuctionBlock.SourceMerged))
                    manifest = await meta.ReadMetaAsync<ScyllaMonthManifest>(AuctionBlobKeys.ScyllaManifest(month.Month), cancellationToken).ConfigureAwait(false);
                foreach (var tag in month.Tags)
                    foreach (var part in SelectScyllaParts(tag, manifest))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (targets.ContainsKey((tag.Tag, month.Month, part)))
                            continue;
                        var block = await store.ReadBlockAsync(tag.Tag, month.Month, part, false, cancellationToken).ConfigureAwait(false)
                            ?? throw new InvalidOperationException($"part {part} of {tag.Tag} {month.Month} is missing from the store; nothing changed");
                        scanned++;
                        if (AnonymizeBlock(block, uuid).block != null)
                            targets[(tag.Tag, month.Month, part)] = tag.TagId;
                    }
            }

            // validate everything before the first write: tag-month in the R2 tag index, no pending Scylla import
            foreach (var group in targets.Keys.GroupBy(k => (k.tag, k.month)))
            {
                var entry = (await store.ReadIndexAsync(group.Key.tag, cancellationToken).ConfigureAwait(false))?.Find(group.Key.month)
                    ?? throw new InvalidOperationException($"{group.Key.tag} {group.Key.month} is not in the R2 tag index; nothing changed, index rows kept");
                if (group.Any(k => k.part >= entry.PartSha256.Count))
                    throw new InvalidOperationException($"{group.Key.tag} {group.Key.month} is listed with {entry.PartSha256.Count} parts in the tag index but the player is in part {group.Max(k => k.part)}; nothing changed");
                if (await IsImportPendingAsync(group.Key.tag, group.Key.month, cancellationToken).ConfigureAwait(false))
                    throw new InvalidOperationException($"the Scylla import of {group.Key.tag} {group.Key.month} is not complete yet; rewriting its parts would break the import's manifest hash check. Nothing changed, run the erasure again after SkyIndexer imported the month");
            }

            var rewritten = 0;
            var changedRows = 0;
            var partCount = 0;
            foreach (var month in targets.GroupBy(k => (k.Key.tag, k.Key.month, tagId: k.Value)).OrderBy(g => g.Key.tag, StringComparer.Ordinal).ThenBy(g => g.Key.month))
            {
                var tag = month.Key.tag;
                var monthNumber = month.Key.month;
                var shas = new Dictionary<int, string>();
                foreach (var part in month.Select(k => k.Key.part).Distinct().OrderBy(p => p))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var block = await store.ReadBlockAsync(tag, monthNumber, part, true, cancellationToken).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"part {part} of {tag} {monthNumber} is indexed for player {playerId} but missing from the store; index rows kept");
                    partCount++;
                    var (replacement, changed) = AnonymizeBlock(block, uuid);
                    if (replacement == null)
                    {
                        shas[part] = block.Sha256();
                        continue;
                    }
                    await store.WriteBlockAsync(replacement, cancellationToken).ConfigureAwait(false);
                    var readBack = await store.ReadBlockAsync(tag, monthNumber, part, true, cancellationToken).ConfigureAwait(false);
                    var difference = replacement.FirstDifference(readBack);
                    if (difference != null)
                        throw new InvalidOperationException($"read-back of the rewritten part {part} of {tag} {monthNumber} differs in {difference}; index rows kept");
                    shas[part] = replacement.Sha256();
                    rewritten++;
                    changedRows += changed;
                    partsRewritten.Inc();
                    rowsAnonymised.Inc(changed);
                    logger.LogInformation("Archive erasure: anonymised {Rows} auctions of player {PlayerId} in {Tag} {Month} part {Part}", changed, playerId, tag, monthNumber, part);
                }
                await UpdateHashesAsync(tag, month.Key.tagId, monthNumber, shas, cancellationToken).ConfigureAwait(false);
            }

            // last: only now no blob holds the identity any more
            var deleted = await DeleteIndexRowsAsync(playerId, cancellationToken).ConfigureAwait(false);
            indexRowsDeleted.Inc(deleted);
            players.Inc();
            logger.LogInformation("Archive erasure of player {PlayerId}: {Parts} parts read (+{Scanned} scanned for the uuid), {Rewritten} rewritten ({Rows} auctions), {Deleted} index rows deleted", playerId, partCount, scanned, rewritten, changedRows, deleted);
            return new ArchiveErasureResult(uids.Count, partCount, rewritten, changedRows, deleted);
        }

        /// <summary>
        /// The block with every appearance of <paramref name="uuid"/> anonymised, null when it does not contain the player (then nothing
        /// is rewritten, which makes a re-run a no-op). Also returns the number of changed auctions.
        /// </summary>
        internal static (ArchivedAuctionBlock block, int changedRows) AnonymizeBlock(ArchivedAuctionBlock block, string uuid)
        {
            var auctions = block.ToAuctions().ToList();
            var extras = block.ToExtras();
            var changedRows = 0;
            for (var i = 0; i < auctions.Count; i++)
            {
                var auction = auctions[i];
                var x = extras[i];
                var seller = auction.AuctioneerId == uuid;
                var highestBidderHit = ArchivedAuctionBlock.HighestBid(auction.Bids)?.Bidder == uuid;
                var profileHit = auction.ProfileId == uuid;
                var coopHit = (auction.CoopMembers?.Any(m => m.value == uuid) ?? false) || (auction.ClaimedBids?.Any(m => m.value == uuid) ?? false);
                var bidderHit = auction.Bids?.Any(b => b.Bidder == uuid) ?? false;
                var bidProfileHit = auction.Bids?.Any(b => b.ProfileId == uuid) ?? false;
                if (!seller && !bidderHit && !profileHit && !coopHit && !bidProfileHit)
                    continue;
                changedRows++;
                if (seller)
                    PlayerOptOut.Anonymize(auction);
                foreach (var bid in auction.Bids?.Where(b => b.Bidder == uuid) ?? [])
                    PlayerOptOut.Anonymize(bid);
                if (auction.ProfileId == uuid)
                    auction.ProfileId = null;
                foreach (var bid in auction.Bids?.Where(b => b.ProfileId == uuid) ?? [])
                    bid.ProfileId = null;
                auction.CoopMembers?.RemoveAll(m => m.value == uuid);
                auction.ClaimedBids?.RemoveAll(m => m.value == uuid);
                if (x != null)
                {
                    if (highestBidderHit)
                        x.HighestBidderName = null;
                    if (seller || profileHit)
                        x.ProfileName = null;
                    if (coopHit)
                        x.CoopName = null;
                }
            }
            if (changedRows == 0)
                return (null, 0);
            return (ArchivedAuctionBlock.FromAuctions(block.Tag, block.Month, block.Part, block.Source, auctions.Select((a, i) => (a, extras[i]))), changedRows);
        }

        /// <summary>
        /// The parts that can hold rows which are not in MariaDB: <c>mariadb</c> tag-months none (the MariaDB lookup finds those), <c>scylla</c>
        /// all, <c>merged</c> the parts the Scylla manifest of the month lists (all parts when there is no manifest).
        /// </summary>
        internal static IEnumerable<int> SelectScyllaParts(ErasureTag tag, ScyllaMonthManifest manifest)
        {
            switch (tag.Source)
            {
                case ArchivedAuctionBlock.SourceMariaDb:
                    return Enumerable.Empty<int>();
                case ArchivedAuctionBlock.SourceMerged:
                    var entry = manifest?.Find(tag.Tag);
                    return entry == null ? Enumerable.Range(0, tag.Parts) : Enumerable.Range(entry.FirstNewPart, entry.NewParts).Where(p => p < tag.Parts);
                default:
                    return Enumerable.Range(0, tag.Parts);
            }
        }

        /// <summary>True while SkyIndexer still has to import the tag-month's Scylla parts (manifest entry present, <c>archived_months.scylla_at</c> not set).</summary>
        private async Task<bool> IsImportPendingAsync(string tag, int month, CancellationToken cancellationToken)
        {
            var manifest = await meta.ReadMetaAsync<ScyllaMonthManifest>(AuctionBlobKeys.ScyllaManifest(month), cancellationToken).ConfigureAwait(false);
            if (manifest?.Find(tag) == null)
                return false;
            var row = await lookup.GetArchivedMonthAsync(tag, month, cancellationToken).ConfigureAwait(false);
            return row?.ScyllaAt == null;
        }

        /// <summary>Set the verified hashes of the touched parts in the R2 tag index (conditional write, re-applied on a conflict) and recompute <c>archived_months.sha256</c>.</summary>
        private async Task UpdateHashesAsync(string tag, ushort tagId, int month, Dictionary<int, string> shas, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                var index = await store.ReadIndexAsync(tag, cancellationToken).ConfigureAwait(false);
                var entry = index?.Find(month);
                if (entry == null)
                    throw new InvalidOperationException($"{tag} {month} is not in the R2 tag index; hashes not updated, index rows kept");
                var stale = false;
                foreach (var (part, sha) in shas)
                {
                    if (part >= entry.PartSha256.Count || string.Equals(entry.PartSha256[part], sha, StringComparison.OrdinalIgnoreCase))
                        continue;
                    entry.PartSha256[part] = sha;
                    stale = true;
                }
                if (stale)
                {
                    try
                    {
                        await store.WriteIndexAsync(index, cancellationToken).ConfigureAwait(false);
                    }
                    catch (ArchiveIndexConflictException) when (attempt < ArchiveIndexConflictException.MaxWriteAttempts)
                    {
                        continue;
                    }
                }
                var row = await lookup.GetArchivedMonthAsync(tag, month, cancellationToken).ConfigureAwait(false);
                if (row != null && row.Parts > 0 && row.Parts <= entry.PartSha256.Count)
                {
                    var combined = ArchivedAuctionBlock.CombinedSha256(entry.PartSha256.Take(row.Parts).ToList());
                    if (!string.Equals(combined, row.Sha256, StringComparison.OrdinalIgnoreCase))
                        await UpdateMonthShaAsync(tagId, month, combined, cancellationToken).ConfigureAwait(false);
                }
                return;
            }
        }

        // ---------------------------------------------------------------- data access seams (MariaDB, write access)

        /// <summary>Uids of the player's rows in <c>auction_sellers</c> and <c>auction_bidders</c>.</summary>
        protected virtual async Task<IReadOnlyList<long>> GetIndexedUidsAsync(uint playerId, CancellationToken cancellationToken)
        {
            using var db = new ArchiveContext();
            var sold = await db.AuctionSellers.AsNoTracking().Where(s => s.SellerId == playerId).Select(s => s.Uid).ToListAsync(cancellationToken).ConfigureAwait(false);
            var bid = await db.AuctionBidders.AsNoTracking().Where(b => b.BidderId == playerId).Select(b => b.Uid).ToListAsync(cancellationToken).ConfigureAwait(false);
            return sold.Concat(bid).ToList();
        }

        /// <summary>Uids of the player's auctions still in MariaDB (as seller by id or uuid, or with a bid by the player).</summary>
        protected virtual async Task<IReadOnlyList<long>> GetMariaDbUidsAsync(uint playerId, string uuid, CancellationToken cancellationToken)
        {
            using var db = new HypixelContext();
            var id = (int)playerId;
            var sold = await db.Auctions.AsNoTracking().Where(a => a.SellerId == id).Select(a => a.UId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var byUuid = await db.Auctions.AsNoTracking().Where(a => a.AuctioneerId == uuid).Select(a => a.UId).ToListAsync(cancellationToken).ConfigureAwait(false);
            var bid = await db.Bids.AsNoTracking().Where(b => b.BidderId == id).Select(b => b.Auction.UId).ToListAsync(cancellationToken).ConfigureAwait(false);
            return sold.Concat(byUuid).Concat(bid).ToList();
        }

        /// <summary>Archived months (sentinel present) without a row in <c>auction_seller_backfill</c>, with their tag rows.</summary>
        protected virtual async Task<IReadOnlyList<ErasureMonth>> GetUnmarkedMonthsAsync(CancellationToken cancellationToken)
        {
            using var db = new ArchiveContext();
            var marked = (await db.AuctionSellerBackfillMonths.AsNoTracking().Select(m => m.Month).ToListAsync(cancellationToken).ConfigureAwait(false)).ToHashSet();
            var rows = await db.ArchivedMonths.AsNoTracking().Where(m => m.VerifiedAt != null || m.TagId != 0).Select(m => new { m.TagId, m.Month, m.Parts, m.Source }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var tags = await db.AuctionTags.AsNoTracking().ToDictionaryAsync(t => t.Id, t => t.Tag, cancellationToken).ConfigureAwait(false);
            var sentinels = rows.Where(r => r.TagId == 0).Select(r => r.Month).ToHashSet();
            return rows.Where(r => r.TagId != 0 && sentinels.Contains(r.Month) && !marked.Contains(r.Month) && tags.ContainsKey(r.TagId))
                .GroupBy(r => r.Month).OrderBy(g => g.Key)
                .Select(g => new ErasureMonth(ArchiveMonths.FromIndex(g.Key), g.Select(r => new ErasureTag(r.TagId, tags[r.TagId], r.Parts, r.Source)).ToList()))
                .ToList();
        }

        /// <summary>Set <c>archived_months.sha256</c> of a tag-month.</summary>
        protected virtual async Task UpdateMonthShaAsync(ushort tagId, int month, string sha, CancellationToken cancellationToken)
        {
            int tag = tagId;
            int index = ArchiveMonths.ToIndex(month);
            using var db = new ArchiveContext();
            await db.Database.ExecuteSqlAsync($"UPDATE archived_months SET sha256 = {sha} WHERE tag_id = {tag} AND month = {index}", cancellationToken).ConfigureAwait(false);
        }

        /// <summary>Delete the player's rows in both index tables (in slices); returns the number of rows.</summary>
        protected virtual async Task<long> DeleteIndexRowsAsync(uint playerId, CancellationToken cancellationToken)
        {
            long total = 0;
            using var db = new ArchiveContext();
            int id = (int)playerId;
            int n;
            do
            {
                n = await db.Database.ExecuteSqlAsync($"DELETE FROM auction_sellers WHERE seller_id = {id} LIMIT 10000", cancellationToken).ConfigureAwait(false);
                total += n;
            } while (n > 0);
            do
            {
                n = await db.Database.ExecuteSqlAsync($"DELETE FROM auction_bidders WHERE bidder_id = {id} LIMIT 10000", cancellationToken).ConfigureAwait(false);
                total += n;
            } while (n > 0);
            return total;
        }
    }
}
