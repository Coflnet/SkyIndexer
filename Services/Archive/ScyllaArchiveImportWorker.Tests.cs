using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

public class ScyllaArchiveImportWorkerTests
{
    private const int Sep = 202009;
    private const string Hyperion = "HYPERION";
    private const string Terminator = "TERMINATOR";
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);
    private static readonly ushort SepIndex = ArchiveMonths.ToIndex(Sep);

    private class TestableImportWorker : ScyllaArchiveImportWorker
    {
        public readonly Dictionary<string, ushort> Tags = new();
        public readonly Dictionary<(ushort tag, ushort month), ArchivedMonth> Months = new();
        public readonly Dictionary<long, (ushort tag, ushort month, byte part)> Locations = new();
        public readonly Dictionary<(uint bidder, uint end, long uid), ulong> Bidders = new();
        public readonly HashSet<(uint seller, uint end, long uid)> Sellers = new();
        public readonly Dictionary<string, int> Players = new();
        public readonly List<int> BidderChunks = new();
        public readonly List<string> Events;
        public readonly List<(List<(long uid, byte part)> locations, int bidders)> WriteCalls = new();

        public TestableImportWorker(InMemoryAuctionBlobStore store, List<string> events, Microsoft.Extensions.Logging.ILogger<ScyllaArchiveImportWorker> logger)
            : base(store, store, ArchiveTestConfig.Build(), logger) => Events = events;

        protected override Task<ScyllaImportMonthState> GetMonthStateAsync(int month, CancellationToken cancellationToken)
        {
            var found = Months.TryGetValue((0, ArchiveMonths.ToIndex(month)), out var sentinel);
            return Task.FromResult(new ScyllaImportMonthState(found, sentinel?.ScyllaAt));
        }

        protected override Task<ArchivedMonth> GetTagRowAsync(string tag, int month, CancellationToken cancellationToken)
            => Task.FromResult(Row(tag, month));

        protected override Task<ushort> GetOrCreateTagIdAsync(string tag, CancellationToken cancellationToken)
        {
            if (!Tags.TryGetValue(tag, out var id))
                Tags[tag] = id = (ushort)(Tags.Count + 1);
            return Task.FromResult(id);
        }

        protected override Task<IReadOnlyDictionary<string, int>> ResolveBidderIdsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken)
        {
            BidderChunks.Add(uuids.Count);
            IReadOnlyDictionary<string, int> found = uuids.Where(Players.ContainsKey).ToDictionary(u => u, u => Players[u]);
            return Task.FromResult(found);
        }

        protected override Task WriteIndexRowsAsync(ushort tagId, ushort month, IReadOnlyList<(long uid, byte part)> locations, IReadOnlyList<AuctionBidder> bidders, IReadOnlyList<AuctionSeller> sellers, CancellationToken cancellationToken)
        {
            Events.Add($"index_rows:{tagId}");
            WriteCalls.Add((locations.ToList(), bidders.Count));
            foreach (var (uid, part) in locations)
                if (!Locations.TryGetValue(uid, out var existing) || existing.tag == tagId && existing.month == month)
                    Locations[uid] = (tagId, month, part); // keepOtherTagMonths semantics, see BuildIndexSql test
            foreach (var b in bidders)
                Bidders.TryAdd((b.BidderId, b.End, b.Uid), 0ul);
            foreach (var x in sellers)
                Sellers.Add((x.SellerId, x.End, x.Uid));
            return Task.CompletedTask;
        }

        protected override Task UpsertArchivedMonthAsync(ArchivedMonth row, CancellationToken cancellationToken)
        {
            Events.Add($"archived_month:{row.TagId}");
            Months[(row.TagId, row.Month)] = row;
            return Task.CompletedTask;
        }

        protected override Task MarkMonthScyllaCompleteAsync(int month, CancellationToken cancellationToken)
        {
            Events.Add("sentinel_scylla_at:" + month);
            Months[(0, ArchiveMonths.ToIndex(month))].ScyllaAt = Now;
            return Task.CompletedTask;
        }

        public ArchivedMonth Row(string tag, int month) => Tags.TryGetValue(tag, out var id) && Months.TryGetValue((id, ArchiveMonths.ToIndex(month)), out var r) ? r : null;
        public bool SchemaReady = true;
        public int SchemaChecks;
        protected override Task<bool> SchemaReadyAsync(CancellationToken cancellationToken) { SchemaChecks++; return Task.FromResult(SchemaReady); }

        public Task<int> Run() => RunOnceAsync(CancellationToken.None);
    }

    private List<string> events;
    private InMemoryAuctionBlobStore store;
    private ListLogger<ScyllaArchiveImportWorker> log;
    private TestableImportWorker worker;

    [SetUp]
    public void Setup()
    {
        events = new List<string>();
        store = new InMemoryAuctionBlobStore(events);
        log = new ListLogger<ScyllaArchiveImportWorker>();
        worker = new TestableImportWorker(store, events, log);
    }

    private static string Hex(long value) => value.ToString("x32");

    private static SaveAuction Auction(long uid, int day, string tag, params (long bidder, long amount)[] bids) => new()
    {
        Uuid = Hex(uid),
        UId = uid,
        Tag = tag,
        End = new DateTime(2020, 9, day, 12, 0, 0, DateTimeKind.Utc),
        Start = new DateTime(2020, 9, day, 0, 0, 0, DateTimeKind.Utc),
        AuctioneerId = Hex(1000 + uid),
        HighestBidAmount = bids.Length == 0 ? 0 : bids.Max(b => b.amount),
        NbtData = new NbtData { data = new byte[] { 10, 0, (byte)uid } },
        Bids = bids.Select(b => new SaveBids { Bidder = Hex(b.bidder), Amount = b.amount, Timestamp = new DateTime(2020, 9, day, 6, 0, 0, DateTimeKind.Utc) }).ToList(),
        Enchantments = new List<Enchantment>(),
        NBTLookup = new List<NBTLookup>(),
    };

    private string WriteBlock(string tag, int part, string source, params SaveAuction[] rows)
    {
        var block = ArchivedAuctionBlock.FromAuctions(tag, Sep, part, source, rows);
        store.WriteBlockAsync(block).Wait();
        return block.Sha256();
    }

    /// <summary>
    /// What phase 1 and the sweep leave behind: HYPERION has a MariaDB part 0 and archived_months row plus a Scylla part 1;
    /// TERMINATOR only a Scylla part 0. The tag indexes already list the Scylla parts, the manifest hands them over.
    /// </summary>
    private ScyllaMonthManifest Scenario(bool completed = true)
    {
        var p0 = WriteBlock(Hyperion, 0, ArchivedAuctionBlock.SourceMariaDb, Auction(1, 1, Hyperion));
        var p1 = WriteBlock(Hyperion, 1, ArchivedAuctionBlock.SourceScylla, Auction(2, 2, Hyperion, (500, 70), (500, 90), (501, 80)), Auction(3, 3, Hyperion, (0, 10)));
        var t0 = WriteBlock(Terminator, 0, ArchivedAuctionBlock.SourceScylla, Auction(4, 4, Terminator, (502, 40)));
        store.WriteIndexAsync(new ArchivedTagIndex { Tag = Hyperion }.Upsert(new ArchivedMonthEntry { Month = Sep, Parts = 2, Rows = 3, SoldRows = 2, PartSha256 = new() { p0, p1 }, Source = ArchivedAuctionBlock.SourceMerged })).Wait();
        store.WriteIndexAsync(new ArchivedTagIndex { Tag = Terminator }.Upsert(new ArchivedMonthEntry { Month = Sep, Parts = 1, Rows = 1, SoldRows = 1, PartSha256 = new() { t0 }, Source = ArchivedAuctionBlock.SourceScylla })).Wait();
        worker.Tags[Hyperion] = 1;
        worker.Months[(1, SepIndex)] = new ArchivedMonth
        {
            TagId = 1, Month = SepIndex, Parts = 1, Rows = 1, SoldRows = 0, Source = ArchivedAuctionBlock.SourceMariaDb, Sha256 = ArchivedAuctionBlock.CombinedSha256(new[] { p0 }),
            ArchivedAt = Now.AddDays(-30), VerifiedAt = Now.AddDays(-30), IndexRows = 1, IndexBidderRows = 0, DeletedRows = 1, DeletedAt = Now.AddDays(-2), Note = "phase one note",
        };
        worker.Locations[1] = (1, SepIndex, 0);
        worker.Months[(0, SepIndex)] = new ArchivedMonth { TagId = 0, Month = SepIndex, Source = "mariadb", Sha256 = new string('0', 64), ArchivedAt = Now, VerifiedAt = Now };
        worker.Players[Hex(500)] = 50;
        worker.Players[Hex(502)] = 52; // 501 is unknown
        var manifest = new ScyllaMonthManifest { Month = Sep, CompletedAt = completed ? Now : null }
            .Upsert(new ScyllaTagImport { Tag = Hyperion, FirstNewPart = 1, NewParts = 1, NewRows = 2, NewSold = 2, NewPartSha256 = new() { p1 }, VerifiedAt = Now })
            .Upsert(new ScyllaTagImport { Tag = Terminator, FirstNewPart = 0, NewParts = 1, NewRows = 1, NewSold = 1, NewPartSha256 = new() { t0 }, VerifiedAt = Now });
        store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), manifest).Wait();
        events.Clear();
        return manifest;
    }

    [Test]
    public async Task ManifestWithTwoTags_WritesLocationsBiddersRowThenSentinel_InThatOrder()
    {
        Scenario();

        Assert.That(await worker.Run(), Is.EqualTo(1));

        Assert.That(events.Where(e => !e.StartsWith("read:")), Is.EqualTo(new[] { "index_rows:1", "archived_month:1", "index_rows:2", "archived_month:2", "sentinel_scylla_at:" + Sep }));
        Assert.That(events.Where(e => e.StartsWith("read:")), Is.EqualTo(new[] { $"read:{Hyperion}:{Sep}:1:uncached", $"read:{Hyperion}:{Sep}:1:uncached", $"read:{Terminator}:{Sep}:0:uncached", $"read:{Terminator}:{Sep}:0:uncached" }), "only the new parts, uncached, once per pass");

        var hyperion = worker.Row(Hyperion, Sep);
        var index = (await store.ReadIndexAsync(Hyperion)).Find(Sep);
        Assert.Multiple(() =>
        {
            Assert.That((hyperion.Parts, hyperion.Rows, hyperion.SoldRows, hyperion.Source), Is.EqualTo(((byte)2, 3u, 2u, ArchivedAuctionBlock.SourceMerged)));
            Assert.That(hyperion.Sha256, Is.EqualTo(ArchivedAuctionBlock.CombinedSha256(index.PartSha256)));
            Assert.That(hyperion.IndexRows, Is.EqualTo(hyperion.Rows), "retention worker invariant index_rows == rows");
            Assert.That(hyperion.IndexBidderRows, Is.EqualTo(1u), "bidder 500 (max 90); 501 unknown, 0 anonymous");
            Assert.That(hyperion.ScyllaAt, Is.Not.Null);
            Assert.That(hyperion.ArchivedAt, Is.EqualTo(Now.AddDays(-30)));
            Assert.That((hyperion.DeletedRows, hyperion.DeletedAt), Is.EqualTo(((uint?)1, (DateTime?)Now.AddDays(-2))), "retention columns carried, never changed");
            Assert.That(hyperion.Note, Is.EqualTo("phase one note; scylla import: +2 rows in 1 parts, 2 bids without bidder"));
        });
        var terminator = worker.Row(Terminator, Sep);
        Assert.That((terminator.Parts, terminator.Rows, terminator.Source, terminator.IndexRows), Is.EqualTo(((byte)1, 1u, ArchivedAuctionBlock.SourceScylla, (uint?)1)));
        Assert.That(worker.Locations[2], Is.EqualTo(((ushort)1, SepIndex, (byte)1)));
        Assert.That(worker.Locations[4], Is.EqualTo((worker.Tags[Terminator], SepIndex, (byte)0)));
        var end2 = (uint)new DateTimeOffset(new DateTime(2020, 9, 2, 12, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        Assert.That(worker.Bidders.ContainsKey((50u, end2, 2)), Is.True, "one row per bidder and auction");
        Assert.That(worker.Months[(0, SepIndex)].ScyllaAt, Is.Not.Null);
    }

    [Test]
    public async Task SellerRowsAreWrittenForResolvedAuctioneersOnly()
    {
        Scenario();
        worker.Players[Hex(1002)] = 72; // auctioneer of uid 2
        worker.Players[Hex(1004)] = 74; // auctioneer of uid 4; uid 3's auctioneer is unknown, uid 1 is a MariaDB part (not imported)

        await worker.Run();

        uint End(int day) => (uint)new DateTimeOffset(new DateTime(2020, 9, day, 12, 0, 0, DateTimeKind.Utc)).ToUnixTimeSeconds();
        Assert.That(worker.Sellers, Is.EquivalentTo(new[] { (72u, End(2), 2L), (74u, End(4), 4L) }));
    }

    /// <summary>Key shared with the archive job, retention and backfill tests: seller 123, auction 77, end 2020-09-02 12:00 UTC.</summary>
    [Test]
    public async Task SellerKeyIsIdenticalAcrossWriters()
    {
        var sha = WriteBlock(Terminator, 0, ArchivedAuctionBlock.SourceScylla, Auction(77, 2, Terminator));
        await store.WriteIndexAsync(new ArchivedTagIndex { Tag = Terminator }.Upsert(new ArchivedMonthEntry { Month = Sep, Parts = 1, Rows = 1, PartSha256 = new() { sha }, Source = ArchivedAuctionBlock.SourceScylla }));
        await store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), new ScyllaMonthManifest { Month = Sep, CompletedAt = Now }
            .Upsert(new ScyllaTagImport { Tag = Terminator, FirstNewPart = 0, NewParts = 1, NewRows = 1, NewPartSha256 = new() { sha } }));
        worker.Months[(0, SepIndex)] = new ArchivedMonth { TagId = 0, Month = SepIndex, Source = "mariadb", Sha256 = new string('0', 64) };
        worker.Players[Hex(1077)] = 123;

        await worker.Run();

        Assert.That(worker.Sellers.Single(), Is.EqualTo((123u, 1599048000u, 77L)));
    }

    [Test]
    public async Task OptedOutAndAnonymisedBiddersAreNotIndexed_ParityWithSellers()
    {
        var a = Auction(100, 1, Terminator, (700, 5), (701, 6), (702, 7));
        a.Bids[1].Bidder = PlayerOptOut.LegacyPlayerUuids[0];
        a.Bids[2].Bidder = PlayerOptOut.AnonymousUuidPrefix + "7f";
        var sha = WriteBlock(Terminator, 0, ArchivedAuctionBlock.SourceScylla, a);
        await store.WriteIndexAsync(new ArchivedTagIndex { Tag = Terminator }.Upsert(new ArchivedMonthEntry { Month = Sep, Parts = 1, Rows = 1, PartSha256 = new() { sha }, Source = ArchivedAuctionBlock.SourceScylla }));
        await store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), new ScyllaMonthManifest { Month = Sep, CompletedAt = Now }
            .Upsert(new ScyllaTagImport { Tag = Terminator, FirstNewPart = 0, NewParts = 1, NewRows = 1, NewPartSha256 = new() { sha } }));
        worker.Months[(0, SepIndex)] = new ArchivedMonth { TagId = 0, Month = SepIndex, Source = "mariadb", Sha256 = new string('0', 64) };
        worker.Players[Hex(700)] = 70;
        worker.Players[PlayerOptOut.LegacyPlayerUuids[0]] = 71;
        worker.Players[PlayerOptOut.AnonymousUuidPrefix + "7f"] = 72;

        await worker.Run();

        Assert.That(worker.Bidders.Keys.Select(k => k.bidder), Is.EqualTo(new[] { 70u }));
    }

    [Test]
    public async Task AnonymisedAndOptedOutAuctioneersAreNotIndexed()
    {
        var anonymous = Auction(100, 1, Terminator);
        anonymous.AuctioneerId = Hex(0);
        var prefixed = Auction(102, 3, Terminator);
        prefixed.AuctioneerId = PlayerOptOut.AnonymousUuidPrefix + "7f";
        var optedOut = Auction(103, 4, Terminator);
        optedOut.AuctioneerId = PlayerOptOut.LegacyPlayerUuids[0];
        var named = Auction(101, 2, Terminator);
        var sha = WriteBlock(Terminator, 0, ArchivedAuctionBlock.SourceScylla, anonymous, named, prefixed, optedOut);
        await store.WriteIndexAsync(new ArchivedTagIndex { Tag = Terminator }.Upsert(new ArchivedMonthEntry { Month = Sep, Parts = 1, Rows = 4, PartSha256 = new() { sha }, Source = ArchivedAuctionBlock.SourceScylla }));
        await store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), new ScyllaMonthManifest { Month = Sep, CompletedAt = Now }
            .Upsert(new ScyllaTagImport { Tag = Terminator, FirstNewPart = 0, NewParts = 1, NewRows = 4, NewPartSha256 = new() { sha } }));
        worker.Months[(0, SepIndex)] = new ArchivedMonth { TagId = 0, Month = SepIndex, Source = "mariadb", Sha256 = new string('0', 64) };
        // even if Players rows existed for the identities that must never be indexed
        worker.Players[Hex(0)] = 99;
        worker.Players[prefixed.AuctioneerId] = 98;
        worker.Players[optedOut.AuctioneerId] = 97;
        worker.Players[Hex(1101)] = 81;

        await worker.Run();

        Assert.That(worker.Sellers.Select(x => x.seller), Is.EqualTo(new[] { 81u }));
        Assert.That(worker.Sellers.Single().uid, Is.EqualTo(101));
    }

    [Test]
    public async Task HashMismatch_SkipsTheTagWithoutWrites_AndTheSentinelStaysOpen()
    {
        var manifest = Scenario();
        manifest.Find(Hyperion).NewPartSha256[0] = new string('a', 64);
        await store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), manifest);
        events.Clear();

        Assert.That(await worker.Run(), Is.Zero);

        Assert.That(events.Where(e => !e.StartsWith("read:")), Is.EqualTo(new[] { "index_rows:2", "archived_month:2" }), "only TERMINATOR");
        Assert.That(worker.Row(Hyperion, Sep).ScyllaAt, Is.Null);
        Assert.That(worker.Locations.ContainsKey(2), Is.False);
        Assert.That(worker.Months[(0, SepIndex)].ScyllaAt, Is.Null);
        Assert.That(log.Entries.Any(e => e.level == Microsoft.Extensions.Logging.LogLevel.Error && e.message.Contains("tag index entry does not end with the manifest's part hashes")), Is.True);
    }

    [Test]
    public async Task PendingSchemaMigrations_SkipThePollWithoutWritesOrErrors_ThenProceedAndCacheReadiness()
    {
        Scenario();
        events.Clear();
        worker.SchemaReady = false;

        Assert.That(await worker.Run(), Is.Zero);
        Assert.That(events, Is.Empty, "no manifest import, index write or archived_months write while a migration is pending");
        Assert.That(worker.WriteCalls, Is.Empty);
        Assert.That(worker.Months[(0, SepIndex)].ScyllaAt, Is.Null);

        worker.SchemaReady = true;
        Assert.That(await worker.Run(), Is.EqualTo(1));
        Assert.That(worker.WriteCalls, Is.Not.Empty);
        var checks = worker.SchemaChecks;
        await worker.Run();
        Assert.That(worker.SchemaChecks, Is.EqualTo(checks), "a positive result is cached");
    }

    [Test]
    public async Task CorruptPart_IsAHashMismatch_NothingWritten()
    {
        Scenario();
        var block = ArchivedAuctionBlock.FromAuctions(Terminator, Sep, 0, ArchivedAuctionBlock.SourceScylla, new[] { Auction(4, 4, Terminator, (502, 41)) });
        await store.WriteBlockAsync(block); // the part changed after the sweep hashed it
        events.Clear();

        await worker.Run();

        Assert.That(events.Any(e => e.StartsWith("index_rows:2") || e.StartsWith("archived_month:2")), Is.False);
        Assert.That(log.Entries.Any(e => e.message.Contains("part 0 hashes to")), Is.True);
        Assert.That(worker.Months[(0, SepIndex)].ScyllaAt, Is.Null);
    }

    [Test]
    public async Task AlreadyImportedTags_AreSkipped_AndARerunWritesNothing()
    {
        Scenario();
        await worker.Run();
        events.Clear();
        worker.Months[(0, SepIndex)].ScyllaAt = null; // even if the sentinel were reset, the tags are not imported twice

        Assert.That(await worker.Run(), Is.EqualTo(1));
        Assert.That(events.Where(e => !e.StartsWith("read:")), Is.EqualTo(new[] { "sentinel_scylla_at:" + Sep }));

        events.Clear();
        Assert.That(await worker.Run(), Is.Zero);
        Assert.That(events, Is.Empty, "an imported month is not even read");
    }

    [Test]
    public async Task ManifestWithoutCompletedAt_ImportsTagsButLeavesTheSentinel()
    {
        Scenario(completed: false);

        Assert.That(await worker.Run(), Is.Zero);

        Assert.That(worker.Row(Hyperion, Sep).ScyllaAt, Is.Not.Null);
        Assert.That(worker.Row(Terminator, Sep).ScyllaAt, Is.Not.Null);
        Assert.That(events, Does.Not.Contain("sentinel_scylla_at:" + Sep));
        Assert.That(worker.Months[(0, SepIndex)].ScyllaAt, Is.Null);
    }

    [Test]
    public async Task CompletedManifestWithoutTags_StillMarksTheSentinel_ButOnlyWithAPhaseOneSentinel()
    {
        await store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), new ScyllaMonthManifest { Month = Sep, CompletedAt = Now });

        Assert.That(await worker.Run(), Is.Zero, "no sentinel yet");

        worker.Months[(0, SepIndex)] = new ArchivedMonth { TagId = 0, Month = SepIndex, Source = "mariadb", Sha256 = new string('0', 64) };
        Assert.That(await worker.Run(), Is.EqualTo(1));
        Assert.That(worker.Months[(0, SepIndex)].ScyllaAt, Is.Not.Null);
    }

    [Test]
    public async Task FirstNewPartMustMatchThePhaseOneParts()
    {
        var manifest = Scenario();
        manifest.Find(Hyperion).FirstNewPart = 2;
        await store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), manifest);
        events.Clear();

        await worker.Run();

        Assert.That(worker.Row(Hyperion, Sep).ScyllaAt, Is.Null);
        Assert.That(events, Does.Not.Contain("index_rows:1"));
        Assert.That(log.Entries.Any(e => e.message.Contains("first new part is 2, archived_months has 1 parts")), Is.True);
    }

    [Test]
    public async Task BiddersResolvedInChunksBelowTheDiveLimit()
    {
        var rows = Enumerable.Range(0, 250).Select(i => Auction(100 + i, 1 + i % 28, Terminator, (10_000 + i, 5))).ToArray();
        var sha = WriteBlock(Terminator, 0, ArchivedAuctionBlock.SourceScylla, rows);
        await store.WriteIndexAsync(new ArchivedTagIndex { Tag = Terminator }.Upsert(new ArchivedMonthEntry { Month = Sep, Parts = 1, Rows = 250, PartSha256 = new() { sha }, Source = ArchivedAuctionBlock.SourceScylla }));
        await store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(Sep), new ScyllaMonthManifest { Month = Sep, CompletedAt = Now }
            .Upsert(new ScyllaTagImport { Tag = Terminator, FirstNewPart = 0, NewParts = 1, NewRows = 250, NewPartSha256 = new() { sha } }));
        worker.Months[(0, SepIndex)] = new ArchivedMonth { TagId = 0, Month = SepIndex, Source = "mariadb", Sha256 = new string('0', 64) };
        for (var i = 0; i < 200; i++)
            worker.Players[Hex(10_000 + i)] = i + 1;

        await worker.Run();

        Assert.That(worker.BidderChunks, Has.All.LessThanOrEqualTo(100));
        Assert.That(worker.BidderChunks.Sum(), Is.EqualTo(500), "250 bidders and 250 auctioneers share one lookup per part");
        Assert.That(worker.Bidders, Has.Count.EqualTo(200));
        Assert.That(worker.Row(Terminator, Sep).Note, Does.Contain("50 bids without bidder"));
    }

    /// <summary>A tag with three Scylla parts (uids 10-11, 20-21, 30-31), each part with one resolvable bid; returns the manifest entry.</summary>
    private ScyllaTagImport ThreePartScenario(int manifestRows = 6, int corruptPart = -1)
    {
        var shas = new List<string>();
        for (var p = 0; p < 3; p++)
        {
            var uid = 10 * (p + 1);
            shas.Add(WriteBlock(Terminator, p, ArchivedAuctionBlock.SourceScylla,
                Auction(uid, 1, Terminator, (600 + p, 5)), Auction(uid + 1, 2, Terminator, (0, 1))));
            worker.Players[Hex(600 + p)] = 60 + p;
        }
        var real = shas.ToList();
        if (corruptPart >= 0)
            shas[corruptPart] = new string('a', 64);
        store.WriteIndexAsync(new ArchivedTagIndex { Tag = Terminator }.Upsert(new ArchivedMonthEntry { Month = Sep, Parts = 3, Rows = 6, PartSha256 = shas, Source = ArchivedAuctionBlock.SourceScylla })).Wait();
        worker.Months[(0, SepIndex)] = new ArchivedMonth { TagId = 0, Month = SepIndex, Source = "mariadb", Sha256 = new string('0', 64) };
        events.Clear();
        return new ScyllaTagImport { Tag = Terminator, FirstNewPart = 0, NewParts = 3, NewRows = manifestRows, NewSold = 0, NewPartSha256 = shas, VerifiedAt = Now };
    }

    [Test]
    public async Task ImportTag_WritesIndexRowsOncePerPart()
    {
        var entry = ThreePartScenario();

        Assert.That(await worker.ImportTagAsync(Sep, entry, null, CancellationToken.None), Is.True);

        Assert.That(worker.WriteCalls.Select(c => c.locations), Is.EqualTo(new[]
        {
            new[] { (10L, (byte)0), (11L, (byte)0) },
            new[] { (20L, (byte)1), (21L, (byte)1) },
            new[] { (30L, (byte)2), (31L, (byte)2) },
        }));
        Assert.That(worker.WriteCalls.Select(c => c.bidders), Is.EqualTo(new[] { 1, 1, 1 }));
        var row = worker.Row(Terminator, Sep);
        Assert.Multiple(() =>
        {
            Assert.That((row.Parts, row.Rows, row.SoldRows), Is.EqualTo(((byte)3, 6u, 6u)));
            Assert.That((row.IndexRows, row.IndexBidderRows), Is.EqualTo((6u, 3u)));
            Assert.That(row.ScyllaAt, Is.Not.Null);
        });
    }

    [Test]
    public async Task ImportTag_HashMismatchInLaterPart_WritesNothing()
    {
        var entry = ThreePartScenario(corruptPart: 2);

        Assert.That(await worker.ImportTagAsync(Sep, entry, null, CancellationToken.None), Is.False);

        Assert.That(worker.WriteCalls, Is.Empty);
        Assert.That(events.Any(e => e.StartsWith("archived_month:")), Is.False);
        Assert.That(log.Entries.Any(e => e.message.Contains("part 2 hashes to")), Is.True);
    }

    [Test]
    public async Task ImportTag_PartChangedAfterIndexedHash_WritesNothing()
    {
        var entry = ThreePartScenario();
        await store.WriteBlockAsync(ArchivedAuctionBlock.FromAuctions(Terminator, Sep, 2, ArchivedAuctionBlock.SourceScylla, new[] { Auction(30, 1, Terminator), Auction(31, 2, Terminator) }));

        Assert.That(await worker.ImportTagAsync(Sep, entry, null, CancellationToken.None), Is.False);

        Assert.That(worker.WriteCalls, Is.Empty, "part 2 is verified before part 0 is written");
        Assert.That(events.Any(e => e.StartsWith("archived_month:")), Is.False);
        Assert.That(log.Entries.Any(e => e.message.Contains("part 2 hashes to")), Is.True);
    }

    [Test]
    public async Task ImportTag_RowCountMismatch_WritesNothing()
    {
        var entry = ThreePartScenario(manifestRows: 7);

        Assert.That(await worker.ImportTagAsync(Sep, entry, null, CancellationToken.None), Is.False);

        Assert.That(worker.WriteCalls, Is.Empty);
        Assert.That(events.Any(e => e.StartsWith("archived_month:")), Is.False);
        Assert.That(log.Entries.Any(e => e.message.Contains("the parts hold 6 rows, the manifest says 7")), Is.True);
    }

    [Test]
    public void IndexSqlNeverMovesALocationOfAnotherTagMonth()
    {
        var sql = ScyllaArchiveImportWorker.BuildIndexSql(3, SepIndex, new[] { (7L, (byte)1) }, new[] { new AuctionBidder { BidderId = 1, End = 2, Uid = 7 } }, new[] { new AuctionSeller { SellerId = 4, End = 2, Uid = 7 } }).ToList();
        Assert.That(sql[0], Does.EndWith("ON DUPLICATE KEY UPDATE part = IF(tag_id = VALUES(tag_id) AND month = VALUES(month), VALUES(part), part)"));
        Assert.That(sql[1], Does.StartWith("INSERT IGNORE INTO auction_bidders"));
        Assert.That(sql[2], Is.EqualTo("INSERT IGNORE INTO auction_sellers (seller_id, `end`, uid) VALUES (4,2,7)"));
    }

    [Test]
    public void NoteAndUuidHelpers()
    {
        Assert.That(ScyllaArchiveImportWorker.BuildNote(null, 2, 1, 0), Is.EqualTo("scylla import: +2 rows in 1 parts"));
        Assert.That(ScyllaArchiveImportWorker.BuildNote("x; scylla import: +1 rows in 1 parts", 2, 1, 3), Is.EqualTo("x; scylla import: +2 rows in 1 parts, 3 bids without bidder"));
        Assert.That(ScyllaArchiveImportWorker.BuildNote(new string('n', 400), 1, 1, 1).Length, Is.LessThanOrEqualTo(255));
        Assert.That(ScyllaArchiveImportWorker.IsPlayerUuid(Hex(5)), Is.True);
        Assert.That(ScyllaArchiveImportWorker.IsPlayerUuid(Hex(0)), Is.False);
        Assert.That(ScyllaArchiveImportWorker.IsPlayerUuid("unknown"), Is.False);
    }
}
