using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

public class ArchiveItemCheckpointsTests
{
    private const int Sep = 202609;
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    private class FakeCheckpoints : IArchiveItemCheckpoints
    {
        public readonly Dictionary<int, Dictionary<int, ItemMonthCheckpoint>> Data = new();
        public readonly List<int> Cleared = new();
        public readonly List<(int month, int item, TimeSpan ttl)> Saves = new();

        public Task<IReadOnlyDictionary<int, ItemMonthCheckpoint>> LoadAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<int, ItemMonthCheckpoint>>(Data.TryGetValue(month, out var d) ? new Dictionary<int, ItemMonthCheckpoint>(d) : new());

        public Task SaveAsync(int month, int itemId, ItemMonthCheckpoint checkpoint, TimeSpan ttl, CancellationToken cancellationToken)
        {
            if (!Data.TryGetValue(month, out var d))
                Data[month] = d = new();
            d[itemId] = checkpoint;
            Saves.Add((month, itemId, ttl));
            return Task.CompletedTask;
        }

        public Task ClearAsync(int month, CancellationToken cancellationToken)
        {
            Cleared.Add(month);
            Data.Remove(month);
            return Task.CompletedTask;
        }
    }

    /// <summary>Redis store over an in-memory hash; <see cref="Failure"/> makes every call throw.</summary>
    private class FakeRedisCheckpoints : RedisArchiveItemCheckpoints
    {
        public readonly Dictionary<string, Dictionary<string, string>> Hashes = new();
        public readonly Dictionary<string, TimeSpan> Ttls = new();
        public Exception Failure;

        public FakeRedisCheckpoints() : base(null, NullLogger<RedisArchiveItemCheckpoints>.Instance) { }

        protected override Task<IReadOnlyList<(string field, string value)>> HashGetAllAsync(string key)
        {
            if (Failure != null) throw Failure;
            return Task.FromResult<IReadOnlyList<(string, string)>>(Hashes.TryGetValue(key, out var h) ? h.Select(kv => (kv.Key, kv.Value)).ToList() : new());
        }

        public int Batches;

        protected override Task HashSetWithExpiryAsync(string key, string field, string value, TimeSpan ttl)
        {
            if (Failure != null) throw Failure;
            Batches++;
            if (!Hashes.TryGetValue(key, out var h))
                Hashes[key] = h = new();
            h[field] = value; // value and TTL land in one call, there is no state with a hash but no TTL
            Ttls[key] = ttl;
            return Task.CompletedTask;
        }

        protected override Task DeleteAsync(string key)
        {
            if (Failure != null) throw Failure;
            Hashes.Remove(key);
            return Task.CompletedTask;
        }
    }

    private class Job : AuctionArchiveJob
    {
        public readonly List<SaveAuction> Auctions = new();
        public readonly Dictionary<string, ushort> Tags = new();
        public readonly Dictionary<(ushort tag, ushort month), ArchivedMonth> Months = new();
        public readonly List<int> PagedItems = new();

        public Job(IAuctionBlobStore store, IArchiveItemCheckpoints checkpoints, Microsoft.Extensions.Logging.ILogger<AuctionArchiveJob> logger = null)
            : base(store, Config(), logger ?? NullLogger<AuctionArchiveJob>.Instance, checkpoints) { }

        private static IConfiguration Config() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["ARCHIVE:ENABLED"] = "true", ["ARCHIVE:OLDEST_MONTH"] = "2026-09", ["ARCHIVE:MAX_ROWS_PER_PART"] = "2",
            ["ARCHIVE:PAGE_PAUSE_MS"] = "0", ["ARCHIVE:TAG_PAUSE_MS"] = "0", ["ARCHIVE:MONTH_PAUSE_MS"] = "0",
        }).Build();

        protected override Task<IReadOnlyDictionary<int, DateTime>> GetAuctionItemFirstEndsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, DateTime>>(Auctions.GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.Min(a => a.End)));
        protected override Task<ArchiveMonthState> GetMonthStateAsync(int month, CancellationToken ct)
        {
            var index = ArchiveMonths.ToIndex(month);
            return Task.FromResult(new ArchiveMonthState(Months.ContainsKey((0, index)), new HashSet<ushort>()));
        }
        protected override Task<ushort> GetOrCreateTagIdAsync(string tag, CancellationToken ct)
        {
            if (!Tags.TryGetValue(tag, out var id))
                Tags[tag] = id = (ushort)(Tags.Count + 1);
            return Task.FromResult(id);
        }
        protected override Task<List<SaveAuction>> LoadPageAsync(int itemId, DateTime cursorEnd, int cursorId, DateTime monthEnd, int pageSize, CancellationToken ct)
        {
            if (cursorId == 0) PagedItems.Add(itemId);
            var filter = PageFilter(itemId, cursorEnd, cursorId, monthEnd).Compile();
            return Task.FromResult(Auctions.Where(filter).OrderBy(a => a.End).ThenBy(a => a.Id).Take(pageSize).ToList());
        }
        protected override Task LoadChildrenAsync(List<SaveAuction> page, CancellationToken ct) => Task.CompletedTask;
        protected override Task<IReadOnlyDictionary<string, int>> ResolvePlayerIdsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        protected override Task WriteIndexRowsAsync(ushort tagId, ushort month, IReadOnlyList<(long uid, byte part)> locations, IReadOnlyList<AuctionBidder> bidders, IReadOnlyList<AuctionSeller> sellers, CancellationToken ct) => Task.CompletedTask;
        protected override Task UpsertArchivedMonthAsync(ArchivedMonth row, CancellationToken ct)
        {
            Months[(row.TagId, row.Month)] = row;
            return Task.CompletedTask;
        }
        protected override Task WriteMonthSentinelAsync(int month, CancellationToken ct)
        {
            var index = ArchiveMonths.ToIndex(month);
            Months[(0, index)] = new ArchivedMonth { TagId = 0, Month = index, ArchivedAt = Now, VerifiedAt = Now, Source = "mariadb", Sha256 = EmptySha256 };
            return Task.CompletedTask;
        }
        protected override Task Delay(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> Month(IReadOnlyList<int> items) => ArchiveMonthAsync(Sep, items, ArchiveOptions.From(Config()), CancellationToken.None);
        public Task<int> Run() => RunOnceAsync(Now, CancellationToken.None);
    }

    private static int nextId = 1;

    private static SaveAuction Auction(int itemId, string tag)
    {
        var id = nextId++;
        var end = new DateTime(2026, 9, 3).AddMinutes(id);
        return new SaveAuction
        {
            Id = id, UId = 5_000_000 + id, Uuid = (5_000_000L + id).ToString("x32"), ItemId = itemId, Tag = tag,
            End = end, Start = end.AddDays(-1), AuctioneerId = (0xabcL).ToString("x32"),
            Bids = new List<SaveBids>(), NbtData = new NbtData { data = new byte[] { 10, 0, (byte)id } },
            Enchantments = new List<Enchantment>(), NBTLookup = new List<NBTLookup>(),
        };
    }

    private InMemoryAuctionBlobStore store;
    private FakeCheckpoints checkpoints;
    private List<string> events;

    [SetUp]
    public void Setup()
    {
        events = new List<string>();
        store = new InMemoryAuctionBlobStore(events);
        checkpoints = new FakeCheckpoints();
    }

    /// <summary>Item 1: tag A x3 (2 parts at 2 rows per part), tag B x1. Item 2: A x1. Item 3: nothing. Item 4: B x1, A x1.</summary>
    private Job Seeded(IArchiveItemCheckpoints cp = null, Microsoft.Extensions.Logging.ILogger<AuctionArchiveJob> logger = null)
    {
        var job = new Job(store, cp ?? checkpoints, logger);
        job.Auctions.AddRange(new[]
        {
            Auction(1, "A"), Auction(1, "A"), Auction(1, "A"), Auction(1, "B"),
            Auction(2, "A"),
            Auction(4, "B"), Auction(4, "A"),
        });
        // item 3 exists with an auction in another month only
        var other = Auction(3, "A");
        other.End = new DateTime(2026, 9, 1).AddMonths(-2);
        job.Auctions.Add(other);
        return job;
    }

    private static readonly int[] Items = { 1, 2, 3, 4 };

    [Test]
    public async Task ItemCheckpoint_RecordsExactPartsAndCounters()
    {
        var job = Seeded();
        Assert.That(await job.Month(Items), Is.True);

        var first = checkpoints.Data[Sep][1];
        Assert.That(first.Tags.Keys, Is.EquivalentTo(new[] { "A", "B" }));
        var a = first.Tags["A"];
        Assert.That(a.Parts.Select(p => p.Part), Is.EqualTo(new[] { 0, 1 }));
        Assert.That(a.Rows, Is.EqualTo(3));
        Assert.That(a.IndexRows, Is.EqualTo(3));
        Assert.That(first.Tags["B"].Parts.Select(p => p.Part), Is.EqualTo(new[] { 0 }));
        Assert.That(first.Tags["B"].Rows, Is.EqualTo(1));
        var index = await store.ReadIndexAsync("A");
        Assert.That(index.Months.Single().PartSha256.Take(2), Is.EqualTo(a.Parts.Select(p => p.Sha256)), "recorded hashes are the verified part hashes");
        // item 2 continues the part numbering of tag A
        Assert.That(checkpoints.Data[Sep][2].Tags["A"].Parts.Select(p => p.Part), Is.EqualTo(new[] { 2 }));
        Assert.That(checkpoints.Data[Sep][2].Tags.ContainsKey("B"), Is.False, "only tags the item touched");
        Assert.That(checkpoints.Saves.All(s => s.ttl == TimeSpan.FromHours(48)));
    }

    [Test]
    public async Task ItemWithoutRows_StillGetsAnEmptyCheckpoint()
    {
        var job = Seeded();
        await job.Month(Items);

        Assert.That(checkpoints.Data[Sep].ContainsKey(3), Is.True);
        Assert.That(checkpoints.Data[Sep][3].Tags, Is.Empty);
    }

    [Test]
    public async Task Restart_SkipsFinishedItems_AndEndsWithTheSameResultAsAFreshWalk()
    {
        var fresh = Seeded();
        await fresh.Month(Items);
        var expected = (await store.ReadIndexAsync("A")).Months.Single();
        var expectedB = (await store.ReadIndexAsync("B")).Months.Single();
        var freshMonths = fresh.Months.ToDictionary(m => m.Key, m => m.Value);

        // restart: checkpoints of items 1..3 only
        var saved = checkpoints.Data[Sep]; // the blobs stay in the store: restored parts must exist there
        checkpoints = new FakeCheckpoints();
        foreach (var i in new[] { 1, 2, 3 })
            checkpoints.Data[Sep] = (checkpoints.Data.GetValueOrDefault(Sep) ?? new()).Also(d => d[i] = saved[i]);
        var logger = new ListLogger<AuctionArchiveJob>();
        var restarted = Seeded(logger: logger);
        // the seeded job above reads the same checkpoints
        Assert.That(await restarted.Month(Items), Is.True);

        Assert.That(restarted.PagedItems, Is.EqualTo(new[] { 4 }), "items 1..3 are not paged again");
        var actual = (await store.ReadIndexAsync("A")).Months.Single();
        Assert.That(actual.Parts, Is.EqualTo(expected.Parts));
        Assert.That(actual.Rows, Is.EqualTo(expected.Rows));
        Assert.That(actual.PartSha256.Count, Is.EqualTo(expected.PartSha256.Count));
        Assert.That(actual.PartSha256.Take(3), Is.EqualTo(expected.PartSha256.Take(3)), "parts of finished items come from the checkpoints");
        Assert.That((await store.ReadIndexAsync("B")).Months.Single().Rows, Is.EqualTo(expectedB.Rows));
        Assert.That(restarted.Months[(restarted.Tags["A"], ArchiveMonths.ToIndex(Sep))].IndexRows, Is.EqualTo(freshMonths[(fresh.Tags["A"], ArchiveMonths.ToIndex(Sep))].IndexRows));
        Assert.That(store.Objects.Keys, Does.Contain("auctions/v2/A/2026-09/003.msgpack.br").Or.Contain("auctions/v2/A/2026-09/002.msgpack.br"), "item 4 continues after the restored parts");
        Assert.That(logger.Entries.Any(e => e.message.Contains("3 finished items skipped") && e.message.Contains("item 4")), Is.True, string.Join("\n", logger.Entries.Select(e => e.message)));
    }

    [Test]
    public async Task PartNumberGapOrDuplicate_IgnoresAllCheckpoints()
    {
        var seed = Seeded();
        await seed.Month(Items);
        var good = checkpoints.Data[Sep];

        foreach (var broken in new[] { "gap", "duplicate" })
        {
            store = new InMemoryAuctionBlobStore(events);
            checkpoints = new FakeCheckpoints();
            checkpoints.Data[Sep] = new Dictionary<int, ItemMonthCheckpoint>(good);
            var a = good[2].Tags["A"];
            var part = broken == "gap" ? 7 : 0; // item 2 produced part 2; 7 leaves a hole, 0 repeats item 1's part 0
            checkpoints.Data[Sep][2] = good[2] with { Tags = new() { ["A"] = a with { Parts = new() { new CheckpointPart(part, "x") } } } };
            var logger = new ListLogger<AuctionArchiveJob>();
            var job = Seeded(logger: logger);

            Assert.That(await job.Month(Items), Is.True, broken);

            Assert.That(job.PagedItems.Distinct(), Is.EqualTo(new[] { 1, 2, 3, 4 }), broken + ": full walk");
            Assert.That(logger.Entries.Any(e => e.level == Microsoft.Extensions.Logging.LogLevel.Warning), Is.True, broken);
            Assert.That((await store.ReadIndexAsync("A")).Months.Single().Rows, Is.EqualTo(5), broken + ": correct totals, nothing doubled");
        }
    }

    [Test]
    public async Task ItemCheckpoint_OnlyContainsTagsTheItemTouched_WithCorrectDeltas()
    {
        var job = Seeded();
        await job.Month(Items);

        // item 4 touches B (which has 1 row, 1 part from item 1) and A (3 + 1 rows, parts 0..2 so far)
        var fourth = checkpoints.Data[Sep][4];
        Assert.That(fourth.Tags.Keys, Is.EquivalentTo(new[] { "A", "B" }));
        Assert.That(fourth.Tags["B"].Rows, Is.EqualTo(1));
        Assert.That(fourth.Tags["B"].Parts.Select(p => p.Part), Is.EqualTo(new[] { 1 }));
        Assert.That(fourth.Tags["A"].Rows, Is.EqualTo(1));
        Assert.That(fourth.Tags["A"].Parts.Select(p => p.Part), Is.EqualTo(new[] { 3 }));
        // item 2 only wrote A although B already had runs
        var second = checkpoints.Data[Sep][2];
        Assert.That(second.Tags.Keys, Is.EqualTo(new[] { "A" }));
        Assert.That((second.Tags["A"].Rows, second.Tags["A"].IndexRows), Is.EqualTo((1, 1)));
    }

    [Test]
    public async Task DiscardedCheckpoints_AreClearedFromTheStore()
    {
        var seed = Seeded();
        await seed.Month(Items);
        var good = checkpoints.Data[Sep];
        foreach (var part in new[] { 7, 0 }) // gap, duplicate
        {
            checkpoints = new FakeCheckpoints();
            checkpoints.Data[Sep] = new Dictionary<int, ItemMonthCheckpoint>(good);
            checkpoints.Data[Sep][2] = good[2] with { Tags = new() { ["A"] = good[2].Tags["A"] with { Parts = new() { new CheckpointPart(part, "x") } } } };
            await Seeded().Month(Items);
            Assert.That(checkpoints.Cleared, Does.Contain(Sep), "part " + part);
        }
    }

    [Test]
    public async Task RestoredPartMissingFromTheStore_ReWalksInsteadOfWritingTheSentinel()
    {
        var seed = Seeded();
        await seed.Month(Items);
        var saved = checkpoints.Data[Sep];
        store = new InMemoryAuctionBlobStore(events); // the parts are gone
        checkpoints = new FakeCheckpoints();
        checkpoints.Data[Sep] = new Dictionary<int, ItemMonthCheckpoint>(saved);
        var job = Seeded();

        Assert.That(await job.Month(Items), Is.True);

        Assert.That(job.PagedItems.Distinct(), Is.EqualTo(new[] { 1, 2, 3, 4 }), "full re-walk");
        Assert.That(checkpoints.Cleared, Does.Contain(Sep));
        Assert.That(await store.BlockExistsAsync("A", Sep, 0), Is.True);
        Assert.That((await store.ReadIndexAsync("A")).Months.Single().Rows, Is.EqualTo(5));
    }

    [Test]
    public async Task RedisFailure_DoesNotFailThePass_AndCountsErrors()
    {
        var redis = new FakeRedisCheckpoints { Failure = new InvalidOperationException("redis down") };
        var before = RedisArchiveItemCheckpoints.ErrorCount;
        var job = Seeded(redis);

        Assert.That(await job.Month(Items), Is.True);

        Assert.That(job.PagedItems.Distinct(), Is.EqualTo(new[] { 1, 2, 3, 4 }));
        Assert.That(RedisArchiveItemCheckpoints.ErrorCount - before, Is.GreaterThanOrEqualTo(5), "load plus four saves");
        Assert.That((await store.ReadIndexAsync("A")).Months.Single().Rows, Is.EqualTo(5));
    }

    [Test]
    public async Task MonthSentinel_ClearsTheCheckpoints()
    {
        var job = Seeded();

        Assert.That(await job.Run(), Is.EqualTo(1));

        Assert.That(job.Months.ContainsKey((0, ArchiveMonths.ToIndex(Sep))), Is.True);
        Assert.That(checkpoints.Cleared, Is.EqualTo(new[] { Sep }));
        Assert.That(checkpoints.Data.ContainsKey(Sep), Is.False);
    }

    [Test]
    public async Task RedisStore_RoundTripsJson_SetsTtlAndClears()
    {
        var redis = new FakeRedisCheckpoints();
        var item = new ItemMonthCheckpoint(new DateTime(2026, 10, 1, 1, 2, 3, DateTimeKind.Utc),
            new() { ["A"] = new CheckpointTagDelta(new() { new(0, "aa"), new(1, "bb") }, 3, 1, 3, 2, 4) });
        await redis.SaveAsync(Sep, 12, item, TimeSpan.FromHours(48), CancellationToken.None);
        await redis.SaveAsync(Sep, 13, new ItemMonthCheckpoint(item.ArchivedAt, new()), TimeSpan.FromHours(48), CancellationToken.None);

        Assert.That(redis.Batches, Is.EqualTo(2), "every save sets field and TTL in one batch");
        Assert.That(redis.Hashes.Keys.Single(), Is.EqualTo("sky-indexer:archive:itemmonths:202609"));
        Assert.That(redis.Ttls["sky-indexer:archive:itemmonths:202609"], Is.EqualTo(TimeSpan.FromHours(48)));
        var loaded = await redis.LoadAsync(Sep, CancellationToken.None);
        Assert.That(loaded.Keys, Is.EquivalentTo(new[] { 12, 13 }));
        var a = loaded[12].Tags["A"];
        Assert.That(a.Parts, Is.EqualTo(item.Tags["A"].Parts));
        Assert.That((a.Rows, a.SoldRows, a.IndexRows, a.IndexBidderRows, a.BidsWithoutBidder), Is.EqualTo((3, 1, 3, 2, 4L)));
        Assert.That(loaded[12].ArchivedAt, Is.EqualTo(item.ArchivedAt));
        Assert.That(loaded[13].Tags, Is.Empty);

        await redis.ClearAsync(Sep, CancellationToken.None);
        Assert.That(await redis.LoadAsync(Sep, CancellationToken.None), Is.Empty);
    }
}

internal static class CheckpointTestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
