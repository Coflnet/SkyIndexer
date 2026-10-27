using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

public class ArchivePlayerErasureTests
{
    private const int Sep = 202009;
    private const string Tag = "HYPERION";
    private const ushort TagId = 7;
    private const uint PlayerId = 42;
    private static string Uuid(long n) => (n + 5000).ToString("x32");
    private static readonly string Player = Uuid(42);

    private class FakeLookup : IArchiveLocationLookup
    {
        public readonly Dictionary<long, AuctionLocation> Locations = new();
        public ArchivedMonth Month;
        public Task<AuctionLocation> GetLocationAsync(long uid, CancellationToken ct = default) => Task.FromResult(Locations.GetValueOrDefault(uid));
        public Task<IReadOnlyDictionary<long, AuctionLocation>> GetLocationsAsync(IReadOnlyCollection<long> uids, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyDictionary<long, AuctionLocation>>(uids.Where(Locations.ContainsKey).Distinct().ToDictionary(u => u, u => Locations[u]));
        public Task<string> GetTagNameAsync(ushort tagId, CancellationToken ct = default) => Task.FromResult(tagId == TagId ? Tag : null);
        public Task<IReadOnlyList<AuctionBidder>> GetBidderRowsAsync(uint bidderId, uint from, uint to, int limit, AuctionIndexCursor before = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AuctionSeller>> GetSellerRowsAsync(uint sellerId, uint from, uint to, int limit, AuctionIndexCursor before = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<ArchivedMonth> GetArchivedMonthAsync(string tag, int month, CancellationToken ct = default) => Task.FromResult(Month);
    }

    private class TestableErasure : ArchivePlayerErasure
    {
        public readonly List<AuctionSeller> Sellers = new();
        public readonly List<AuctionBidder> Bidders = new();
        public readonly List<string> Shas = new();
        public readonly List<string> Order;
        public readonly List<long> MariaDbUids = new();
        public readonly List<ErasureMonth> Unmarked = new();
        public TestableErasure(InMemoryAuctionBlobStore store, FakeLookup lookup, List<string> order) : base(store, store, lookup, NullLogger.Instance) { Order = order; this.lookup = lookup; }
        private readonly FakeLookup lookup;

        protected override Task<IReadOnlyList<long>> GetIndexedUidsAsync(uint playerId, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<long>>(Sellers.Where(s => s.SellerId == playerId).Select(s => s.Uid).Concat(Bidders.Where(b => b.BidderId == playerId).Select(b => b.Uid)).ToList());

        protected override Task<IReadOnlyList<long>> GetMariaDbUidsAsync(uint playerId, string uuid, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<long>>(MariaDbUids.ToList());
        protected override Task<IReadOnlyList<ErasureMonth>> GetUnmarkedMonthsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ErasureMonth>>(Unmarked.ToList());

        protected override Task UpdateMonthShaAsync(ushort tagId, int month, string sha, CancellationToken cancellationToken)
        {
            Order.Add("archived_months_sha");
            Shas.Add(sha);
            lookup.Month.Sha256 = sha;
            return Task.CompletedTask;
        }

        protected override Task<long> DeleteIndexRowsAsync(uint playerId, CancellationToken cancellationToken)
        {
            Order.Add("delete_index_rows");
            var n = Sellers.RemoveAll(s => s.SellerId == playerId) + Bidders.RemoveAll(b => b.BidderId == playerId);
            return Task.FromResult((long)n);
        }
    }

    private List<string> events;
    private InMemoryAuctionBlobStore store;
    private FakeLookup lookup;
    private TestableErasure erasure;

    private static SaveAuction Auction(long uid, int day, string seller, params (string bidder, long amount)[] bids) => new()
    {
        Uuid = uid.ToString("x32"),
        UId = uid,
        Tag = Tag,
        End = new DateTime(2020, 9, day, 12, 0, 0, DateTimeKind.Utc),
        Start = new DateTime(2020, 9, day, 0, 0, 0, DateTimeKind.Utc),
        AuctioneerId = seller,
        SellerId = seller == Player ? (int)PlayerId : 9,
        ProfileId = seller,
        HighestBidAmount = bids.Length == 0 ? 0 : bids.Max(b => b.amount),
        NbtData = new NbtData { data = new byte[] { 10, 0, (byte)uid } },
        Bids = bids.Select((b, i) => new SaveBids { Bidder = b.bidder, BidderId = b.bidder == Player ? (int)PlayerId : 8, ProfileId = b.bidder, Amount = b.amount, Timestamp = new DateTime(2020, 9, day, 6, i, 0, DateTimeKind.Utc) }).ToList(),
        CoopMembers = new List<UuId> { new(Player), new(Uuid(1)) },
        ClaimedBids = new List<UuId> { new(Player) },
        Enchantments = new List<Enchantment>(),
        NBTLookup = new List<NBTLookup>(),
    };

    private static ScyllaAuctionExtras Names() => new()
    {
        HighestBidderName = "Name", ProfileName = "Profile", CoopName = "Coop",
        NbtLookupStrings = new Dictionary<string, string>(), UnknownEnchantments = new Dictionary<string, int>(),
    };

    /// <summary>Part 0 (MariaDB): 1 sold by the player, 2 bid on by the player (highest), 3 unrelated. Part 1 (Scylla): 4 sold by the player.</summary>
    private void Scenario()
    {
        var part0 = ArchivedAuctionBlock.FromAuctions(Tag, Sep, 0, ArchivedAuctionBlock.SourceMariaDb, new[]
        {
            Auction(1, 1, Player, (Uuid(2), 100)),
            Auction(2, 2, Uuid(3), (Uuid(2), 100), (Player, 300)),
            Auction(3, 3, Uuid(3), (Uuid(2), 100)),
        });
        var part1 = ArchivedAuctionBlock.FromAuctions(Tag, Sep, 1, ArchivedAuctionBlock.SourceScylla, new[] { (Auction(4, 4, Player), Names()) });
        store.WriteBlockAsync(part0).Wait();
        store.WriteBlockAsync(part1).Wait();
        var hashes = new List<string> { part0.Sha256(), part1.Sha256() };
        store.WriteIndexAsync(new ArchivedTagIndex { Tag = Tag }.Upsert(new ArchivedMonthEntry { Month = Sep, Parts = 2, Rows = 4, PartSha256 = hashes, Source = "merged" })).Wait();
        lookup.Month = new ArchivedMonth { TagId = TagId, Month = ArchiveMonths.ToIndex(Sep), Parts = 2, Sha256 = ArchivedAuctionBlock.CombinedSha256(hashes) };
        foreach (var (uid, part) in new[] { (1L, 0), (2L, 0), (3L, 0), (4L, 1) })
            lookup.Locations[uid] = new AuctionLocation { Uid = uid, TagId = TagId, Month = ArchiveMonths.ToIndex(Sep), Part = (byte)part };
        AddIndexRows();
        events.Clear();
    }

    private void AddIndexRows()
    {
        erasure.Sellers.Clear();
        erasure.Bidders.Clear();
        erasure.Sellers.AddRange(new[] { new AuctionSeller { SellerId = PlayerId, Uid = 1, End = 1 }, new AuctionSeller { SellerId = PlayerId, Uid = 4, End = 4 }, new AuctionSeller { SellerId = 9, Uid = 3, End = 3 } });
        erasure.Bidders.AddRange(new[] { new AuctionBidder { BidderId = PlayerId, Uid = 2, End = 2 }, new AuctionBidder { BidderId = 8, Uid = 2, End = 2 } });
    }

    [SetUp]
    public void SetUp()
    {
        PlayerOptOut.ResetMemory();
        events = new List<string>();
        store = new InMemoryAuctionBlobStore(events);
        lookup = new FakeLookup();
        erasure = new TestableErasure(store, lookup, events);
    }

    [Test]
    public async Task AnonymisesTheIdentityLikeTheSqlErasure_UpdatesHashes_ThenDeletesTheIndexRows()
    {
        Scenario();

        var result = await erasure.EraseAsync(PlayerId, Player, CancellationToken.None);
        var order = events.ToList();

        Assert.That((result.IndexedAuctions, result.PartsRewritten, result.RowsChanged, result.IndexRowsDeleted), Is.EqualTo((3, 2, 4, 3L)));
        var part0 = (await store.ReadBlockAsync(Tag, Sep, 0)).ToAuctions().ToDictionary(a => a.UId);
        var sold = part0[1];
        Assert.That(sold.AuctioneerId, Does.StartWith(PlayerOptOut.AnonymousUuidPrefix));
        Assert.That((sold.SellerId, sold.ProfileId), Is.EqualTo((0, (string)null)));
        var bidOn = part0[2];
        Assert.That(bidOn.Bids.Single(b => b.Amount == 300).BidderId, Is.Zero);
        Assert.That(bidOn.Bids.Single(b => b.Amount == 300).Bidder, Does.StartWith(PlayerOptOut.AnonymousUuidPrefix));
        Assert.That(bidOn.Bids.Single(b => b.Amount == 100).Bidder, Is.EqualTo(Uuid(2)), "other bidders untouched");
        Assert.That(bidOn.AuctioneerId, Is.EqualTo(Uuid(3)));
        Assert.That(part0.Values.SelectMany(a => a.CoopMembers.Concat(a.ClaimedBids)).Select(m => m.value), Does.Not.Contain(Player), "relation entries removed");
        Assert.That(part0.Values.Any(a => a.CoopMembers.Any(m => m.value == Uuid(1))), Is.True, "others' coop entries kept");
        Assert.That(part0[3].AuctioneerId, Is.EqualTo(Uuid(3)));
        var scylla = await store.ReadBlockAsync(Tag, Sep, 1);
        Assert.That(scylla.ExtrasAt(0).ProfileName, Is.Null);
        Assert.That(scylla.ExtrasAt(0).CoopName, Is.Null);
        Assert.That(scylla.ExtrasAt(0).HighestBidderName, Is.EqualTo("Name"), "the highest bidder of auction 4 is not the player");

        // hashes follow the rewritten parts
        var entry = (await store.ReadIndexAsync(Tag)).Find(Sep);
        Assert.That(entry.PartSha256, Is.EqualTo(new[] { (await store.ReadBlockAsync(Tag, Sep, 0)).Sha256(), scylla.Sha256() }));
        Assert.That(erasure.Shas.Single(), Is.EqualTo(ArchivedAuctionBlock.CombinedSha256(entry.PartSha256)));
        // ordering: every write and the hash updates before the index rows are deleted
        Assert.That(order.IndexOf("delete_index_rows"), Is.EqualTo(order.Count - 1));
        Assert.That(order.Count(e => e.StartsWith("write:")), Is.EqualTo(2));
        Assert.That(order.IndexOf("archived_months_sha"), Is.LessThan(order.IndexOf("delete_index_rows")));
        Assert.That(order.FindLastIndex(e => e.StartsWith("write:")), Is.LessThan(order.IndexOf("delete_index_rows")));
        Assert.That(erasure.Sellers.Select(s => s.SellerId), Is.EqualTo(new[] { 9u }), "other players' index rows stay");
        Assert.That(erasure.Bidders.Select(b => b.BidderId), Is.EqualTo(new[] { 8u }));
    }

    [Test]
    public async Task IsIdempotent_ARerunAfterACrashBeforeTheIndexDeleteRewritesNothing()
    {
        Scenario();
        await erasure.EraseAsync(PlayerId, Player, CancellationToken.None);
        AddIndexRows();   // as if the process died before the index rows were deleted
        events.Clear();
        var before = store.Objects.ToDictionary(kv => kv.Key, kv => kv.Value);

        var result = await erasure.EraseAsync(PlayerId, Player, CancellationToken.None);

        Assert.That(result.PartsRewritten, Is.Zero);
        Assert.That(events.Any(e => e.StartsWith("write:")), Is.False);
        Assert.That(store.Objects.Keys.All(k => before[k].SequenceEqual(store.Objects[k])), Is.True, "blobs and tag index byte-identical");
        Assert.That(erasure.Sellers.Select(s => s.SellerId), Is.EqualTo(new[] { 9u }));
        Assert.That(erasure.Shas, Has.Count.EqualTo(1), "no second hash update");
    }

    [Test]
    public async Task ARunThatDiedAfterRewritingAPartButBeforeTheHashUpdateHealsTheIndex()
    {
        Scenario();
        // rewrite part 0 only and leave the tag index with the old hashes (crash between the blob write and the index write)
        var original = await store.ReadBlockAsync(Tag, Sep, 0);
        var (replacement, _) = ArchivePlayerErasure.AnonymizeBlock(original, Player);
        await store.WriteBlockAsync(replacement);
        events.Clear();

        await erasure.EraseAsync(PlayerId, Player, CancellationToken.None);

        var entry = (await store.ReadIndexAsync(Tag)).Find(Sep);
        Assert.That(entry.PartSha256[0], Is.EqualTo(replacement.Sha256()), "stale hash of the already rewritten part is corrected");
        Assert.That(erasure.Shas.Single(), Is.EqualTo(ArchivedAuctionBlock.CombinedSha256(entry.PartSha256)));
        Assert.That(erasure.Sellers.Any(s => s.SellerId == PlayerId), Is.False);
    }

    // ------------------------------------------------------------ completeness: months without seller rows

    /// <summary>The state before the deletion pass reached the month: no index rows at all, the month is not marked.</summary>
    private void UnmarkedMonthWithoutIndexRows(bool withManifest = true)
    {
        Scenario();
        erasure.Sellers.Clear();
        erasure.Bidders.Clear();
        erasure.Unmarked.Add(new ErasureMonth(Sep, new[] { new ErasureTag(TagId, Tag, 2, ArchivedAuctionBlock.SourceMerged) }));
        if (withManifest)
            store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), new ScyllaMonthManifest { Month = Sep, CompletedAt = DateTime.UtcNow }
                .Upsert(new ScyllaTagImport { Tag = Tag, FirstNewPart = 1, NewParts = 1, NewRows = 1 })).Wait();
        lookup.Month.ScyllaAt = DateTime.UtcNow;   // the import finished
        events.Clear();
    }

    [Test]
    public async Task SoldAuctionsInAnUnmarkedMonthAreFoundInMariaDbAndInTheScyllaParts()
    {
        UnmarkedMonthWithoutIndexRows();
        erasure.MariaDbUids.AddRange(new[] { 1L, 2L });   // (a) still in MariaDB (phase-1 part 0); auction 4 is Scylla-only, nobody indexed it

        var result = await erasure.EraseAsync(PlayerId, Player, CancellationToken.None);

        var reads = events.Where(e => e.StartsWith("read:")).ToList();
        Assert.That(result.PartsRewritten, Is.EqualTo(2));
        var part0 = (await store.ReadBlockAsync(Tag, Sep, 0)).ToAuctions().ToDictionary(a => a.UId);
        Assert.That(part0[1].AuctioneerId, Does.StartWith(PlayerOptOut.AnonymousUuidPrefix), "found through MariaDB");
        var scylla = (await store.ReadBlockAsync(Tag, Sep, 1)).ToAuctions().Single();
        Assert.That(scylla.AuctioneerId, Does.StartWith(PlayerOptOut.AnonymousUuidPrefix), "found by decoding the unmarked month's Scylla part");
        Assert.That(reads.Count(e => e.EndsWith(":1:cached")), Is.EqualTo(1), "the Scylla part was scanned");
        Assert.That(reads.Any(e => e.EndsWith(":0:cached")), Is.False, "the MariaDB part (not in the manifest) is not scanned");
    }

    [Test]
    public async Task MarkedMonthsAreNotScanned()
    {
        UnmarkedMonthWithoutIndexRows();
        erasure.Unmarked.Clear();
        AddIndexRows();

        await erasure.EraseAsync(PlayerId, Player, CancellationToken.None);

        Assert.That(events.Any(e => e.EndsWith(":cached")), Is.False, "only the uncached reads of the indexed parts");
    }

    [Test]
    public void MissingTagIndexEntryThrowsBeforeAnyWriteOrDelete()
    {
        Scenario();
        store.Objects.Remove(AuctionBlobKeys.Index(Tag));
        events.Clear();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => erasure.EraseAsync(PlayerId, Player, CancellationToken.None));

        Assert.That(ex.Message, Does.Contain("tag index"));
        Assert.That(events.Any(e => e.StartsWith("write:") || e == "delete_index_rows"), Is.False);
        Assert.That(erasure.Sellers.Count(s => s.SellerId == PlayerId), Is.EqualTo(2));
    }

    [Test]
    public async Task PendingScyllaImportAbortsBeforeWritingAnything_AndRunsAfterTheImport()
    {
        Scenario();
        await store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), new ScyllaMonthManifest { Month = Sep }
            .Upsert(new ScyllaTagImport { Tag = Tag, FirstNewPart = 1, NewParts = 1, NewRows = 1 }));
        lookup.Month.ScyllaAt = null;
        events.Clear();

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => erasure.EraseAsync(PlayerId, Player, CancellationToken.None));

        Assert.That(ex.Message, Does.Contain("not complete yet"));
        Assert.That(events.Any(e => e.StartsWith("write:") || e == "delete_index_rows" || e == "archived_months_sha"), Is.False, "nothing written, nothing deleted");
        Assert.That(erasure.Sellers.Count(s => s.SellerId == PlayerId), Is.EqualTo(2));

        lookup.Month.ScyllaAt = DateTime.UtcNow;   // SkyIndexer finished the import
        var result = await erasure.EraseAsync(PlayerId, Player, CancellationToken.None);
        Assert.That(result.PartsRewritten, Is.EqualTo(2));
    }

    [Test]
    public void MissingScyllaPartDuringTheScanChangesNothing()
    {
        UnmarkedMonthWithoutIndexRows();
        erasure.MariaDbUids.AddRange(new[] { 1L, 2L });
        store.Objects.Remove(AuctionBlobKeys.Block(Tag, Sep, 1));
        events.Clear();

        Assert.ThrowsAsync<InvalidOperationException>(() => erasure.EraseAsync(PlayerId, Player, CancellationToken.None));

        Assert.That(events.Any(e => e.StartsWith("write:") || e == "delete_index_rows"), Is.False);
    }

    [Test]
    public void SelectScyllaPartsFollowsSourceAndManifest()
    {
        var manifest = new ScyllaMonthManifest { Month = Sep }.Upsert(new ScyllaTagImport { Tag = Tag, FirstNewPart = 2, NewParts = 2 });
        Assert.That(ArchivePlayerErasure.SelectScyllaParts(new(TagId, Tag, 4, "merged"), manifest), Is.EqualTo(new[] { 2, 3 }));
        Assert.That(ArchivePlayerErasure.SelectScyllaParts(new(TagId, Tag, 4, "merged"), null), Is.EqualTo(new[] { 0, 1, 2, 3 }));
        Assert.That(ArchivePlayerErasure.SelectScyllaParts(new(TagId, Tag, 3, "mariadb"), manifest), Is.Empty);
        Assert.That(ArchivePlayerErasure.SelectScyllaParts(new(TagId, Tag, 3, "scylla"), manifest), Is.EqualTo(new[] { 0, 1, 2 }));
    }

    [Test]
    public void FailedReadBackKeepsTheIndexRows()
    {
        Scenario();
        store.CorruptReadBack = true;

        var ex = Assert.ThrowsAsync<InvalidOperationException>(() => erasure.EraseAsync(PlayerId, Player, CancellationToken.None));

        Assert.That(ex.Message, Does.Contain("read-back"));
        Assert.That(events, Does.Not.Contain("delete_index_rows"));
        Assert.That(erasure.Sellers.Count(s => s.SellerId == PlayerId), Is.EqualTo(2), "index rows stay, so the erasure can be repeated");
        Assert.That(erasure.Bidders.Count(b => b.BidderId == PlayerId), Is.EqualTo(1));
    }

    [Test]
    public void MissingPartKeepsTheIndexRows()
    {
        Scenario();
        store.Objects.Remove(AuctionBlobKeys.Block(Tag, Sep, 1));

        Assert.ThrowsAsync<InvalidOperationException>(() => erasure.EraseAsync(PlayerId, Player, CancellationToken.None));

        Assert.That(events, Does.Not.Contain("delete_index_rows"));
        Assert.That(erasure.Sellers.Count(s => s.SellerId == PlayerId), Is.EqualTo(2));
    }

    [Test]
    public async Task PlayerWithoutIndexRowsOrAppearancesIsANoOp()
    {
        Scenario();
        erasure.Sellers.Clear();
        erasure.Bidders.Clear();
        var before = store.Objects.ToDictionary(kv => kv.Key, kv => kv.Value);

        var result = await erasure.EraseAsync(PlayerId, Player, CancellationToken.None);

        Assert.That((result.Parts, result.PartsRewritten), Is.EqualTo((0, 0)));
        Assert.That(store.Objects.Keys.All(k => before[k].SequenceEqual(store.Objects[k])), Is.True);
    }

    [Test]
    public void ErasureCommandSkipsTheArchiveWhenItIsNotEnabled()
    {
        Assert.That(PrivacyExport.EraseFromArchive(ArchiveTestConfig.Build(("ARCHIVE:ENABLED", "false"), ("S3:ENABLED", "true")), Player, 42), Is.Null);
        Assert.That(PrivacyExport.EraseFromArchive(ArchiveTestConfig.Build(("S3:ENABLED", "false")), Player, 42), Is.Null);
    }

    [Test]
    public void RejectsAnUnnumberedPlayerOrBadUuid()
    {
        Assert.ThrowsAsync<ArgumentException>(() => erasure.EraseAsync(0, Player, CancellationToken.None));
        Assert.ThrowsAsync<ArgumentException>(() => erasure.EraseAsync(PlayerId, "Not-A-Uuid", CancellationToken.None));
    }
}
