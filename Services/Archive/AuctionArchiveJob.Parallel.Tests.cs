using System;
using System.Collections.Concurrent;
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

/// <summary>Concurrent child queries, statement-timeout splitting and concurrent items of the archive job (no database).</summary>
public class AuctionArchiveJobParallelTests
{
    private const int Sep = 202609;
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>MariaDB error 1969, "Query execution was interrupted (max_statement_time exceeded)".</summary>
    private static MySqlConnector.MySqlException Timeout() => ServerError(1969, "Query execution was interrupted (max_statement_time exceeded)");

    /// <summary>MySqlConnector only exposes its server-error constructor internally.</summary>
    private static MySqlConnector.MySqlException ServerError(int number, string message)
    {
        var ctor = typeof(MySqlConnector.MySqlException).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(MySqlConnector.MySqlErrorCode));
        var args = ctor.GetParameters().Select((p, i) => i == 0 ? (object)(MySqlConnector.MySqlErrorCode)number : p.ParameterType == typeof(string) ? message : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        return (MySqlConnector.MySqlException)ctor.Invoke(args);
    }

    private class Checkpoints : IArchiveItemCheckpoints
    {
        public readonly ConcurrentDictionary<int, ItemMonthCheckpoint> Data = new();
        public readonly ConcurrentQueue<string> Log = new();
        public int Cleared;

        public Task<IReadOnlyDictionary<int, ItemMonthCheckpoint>> LoadAsync(int month, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, ItemMonthCheckpoint>>(new Dictionary<int, ItemMonthCheckpoint>(Data));

        public Task SaveAsync(int month, int itemId, ItemMonthCheckpoint checkpoint, TimeSpan ttl, CancellationToken ct)
        {
            Data[itemId] = checkpoint;
            Log.Enqueue("saved:" + itemId);
            return Task.CompletedTask;
        }

        public Task ClearAsync(int month, CancellationToken ct)
        {
            Interlocked.Increment(ref Cleared);
            Data.Clear();
            return Task.CompletedTask;
        }
    }

    /// <summary>Thread-safe job over in-memory tables; the child queries read <see cref="Children"/> and can be made to time out.</summary>
    private class Job : AuctionArchiveJob
    {
        public readonly List<SaveAuction> Auctions = new();
        public readonly ConcurrentDictionary<string, ushort> Tags = new();
        public readonly ConcurrentDictionary<(ushort tag, ushort month), ArchivedMonth> Months = new();
        public readonly Checkpoints Cp;
        public readonly ConcurrentQueue<string> Log;
        public readonly ConcurrentBag<int> Paged = new();
        public Func<int, int, CancellationToken, Task> OnPage = (item, take, ct) => Task.CompletedTask;
        public Func<string, IReadOnlyList<int>, bool> TimesOut = (table, ids) => false;
        public Func<IReadOnlyList<int>, Task> BeforeQuery = ids => Task.CompletedTask;
        public readonly ConcurrentDictionary<string, int> Queries = new();
        public int Running, MaxRunning;

        public Job(IAuctionBlobStore store, Checkpoints cp, params (string, string)[] config)
            : base(store, ArchiveTestConfig.Build(new[] { ("ARCHIVE:OLDEST_MONTH", "2026-09"), ("ARCHIVE:MAX_ROWS_PER_PART", "2"), ("ARCHIVE:MONTH_PAUSE_MS", "0") }.Concat(config).ToArray()), NullLogger<AuctionArchiveJob>.Instance, cp)
        {
            Cp = cp;
            Log = cp.Log;
        }

        protected override Task<IReadOnlyDictionary<int, DateTime>> GetAuctionItemFirstEndsAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<int, DateTime>>(Auctions.GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.Min(a => a.End)));
        protected override Task<ArchiveMonthState> GetMonthStateAsync(int month, CancellationToken ct)
            => Task.FromResult(new ArchiveMonthState(Months.ContainsKey((0, ArchiveMonths.ToIndex(month))), new HashSet<ushort>()));
        protected override Task<ushort> GetOrCreateTagIdAsync(string tag, CancellationToken ct) => Task.FromResult(Tags.GetOrAdd(tag, _ => (ushort)(Tags.Count + 1)));

        protected override async Task<List<SaveAuction>> LoadPageAsync(int itemId, DateTime cursorEnd, int cursorId, DateTime monthEnd, int pageSize, CancellationToken ct)
        {
            Paged.Add(itemId);
            await OnPage(itemId, pageSize, ct);
            var filter = PageFilter(itemId, cursorEnd, cursorId, monthEnd).Compile();
            return Auctions.Where(filter).OrderBy(a => a.End).ThenBy(a => a.Id).Take(pageSize).ToList();
        }

        protected override async Task<List<(int? AuctionId, SaveBids Bid)>> QueryBidsAsync(IReadOnlyList<int> chunk, CancellationToken ct)
            => (await Run("Bids", chunk, ct)).SelectMany(a => a.Bids.Select(b => ((int?)a.Id, b))).ToList();
        protected override async Task<List<NBTLookup>> QueryNbtLookupsAsync(IReadOnlyList<int> chunk, CancellationToken ct)
            => (await Run("NBTLookups", chunk, ct)).SelectMany(a => a.NBTLookup).ToList();
        protected override async Task<List<Enchantment>> QueryEnchantmentsAsync(IReadOnlyList<int> chunk, CancellationToken ct)
            => (await Run("Enchantment", chunk, ct)).SelectMany(a => a.Enchantments).ToList();
        protected override async Task<List<(int? AuctionId, string value)>> QueryCoopAsync(IReadOnlyList<int> chunk, CancellationToken ct)
            => (await Run("UuId_coop", chunk, ct)).SelectMany(a => a.CoopMembers.Select(c => ((int?)a.Id, c.value))).ToList();
        protected override async Task<List<(int? AuctionId, string value)>> QueryClaimedAsync(IReadOnlyList<int> chunk, CancellationToken ct)
            => (await Run("UuId_claimed", chunk, ct)).SelectMany(a => a.ClaimedBids.Select(c => ((int?)a.Id, c.value))).ToList();
        protected override async Task<List<NbtData>> QueryNbtDataAsync(IReadOnlyList<int> chunk, CancellationToken ct)
            => (await Run("NbtData", chunk, ct)).Select(a => Source.NbtData).Where(n => n != null && chunk.Contains(n.Id)).ToList();

        /// <summary>The "database": children by auction id.</summary>
        public readonly Dictionary<int, SaveAuction> Children = new();
        private static readonly SaveAuction Source = new();

        private async Task<List<SaveAuction>> Run(string table, IReadOnlyList<int> chunk, CancellationToken ct)
        {
            var running = Interlocked.Increment(ref Running);
            int max;
            while ((max = MaxRunning) < running && Interlocked.CompareExchange(ref MaxRunning, running, max) != max) { }
            try
            {
                Queries.AddOrUpdate(table, 1, (_, n) => n + 1);
                await BeforeQuery(chunk);
                if (TimesOut(table, chunk))
                    throw Timeout();
                return chunk.Where(Children.ContainsKey).Select(i => Children[i]).ToList();
            }
            finally
            {
                Interlocked.Decrement(ref Running);
            }
        }

        protected override Task<IReadOnlyDictionary<string, int>> ResolvePlayerIdsAsync(IReadOnlyList<string> uuids, CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());
        protected override Task WriteIndexRowsAsync(ushort tagId, ushort month, IReadOnlyList<(long uid, byte part)> locations, IReadOnlyList<AuctionBidder> bidders, IReadOnlyList<AuctionSeller> sellers, CancellationToken ct)
        {
            foreach (var (uid, part) in locations)
                Locations[uid] = (tagId, part);
            return Task.CompletedTask;
        }
        public readonly ConcurrentDictionary<long, (ushort tag, byte part)> Locations = new();
        protected override Task UpsertArchivedMonthAsync(ArchivedMonth row, CancellationToken ct)
        {
            Log.Enqueue($"finalized:{row.TagId}");
            Months[(row.TagId, row.Month)] = row;
            return Task.CompletedTask;
        }
        protected override Task WriteMonthSentinelAsync(int month, CancellationToken ct)
        {
            Log.Enqueue("sentinel");
            Months[(0, ArchiveMonths.ToIndex(month))] = new ArchivedMonth { TagId = 0, Month = ArchiveMonths.ToIndex(month), ArchivedAt = Now, VerifiedAt = Now, Source = "mariadb", Sha256 = EmptySha256 };
            return Task.CompletedTask;
        }
        protected override Task Delay(TimeSpan delay, CancellationToken ct) => Task.CompletedTask;

        public Task<bool> Month(IReadOnlyList<int> items) => ArchiveMonthAsync(Sep, items, ArchiveOptions.From(TestConfig), CancellationToken.None);
        public Task<int> Run() => RunOnceAsync(Now, CancellationToken.None);
        public IConfiguration TestConfig => (IConfiguration)typeof(AuctionArchiveJob).GetField("config", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(this);
        public Task LoadChildren(List<SaveAuction> page) => LoadChildrenAsync(page, CancellationToken.None);
    }

    private static int nextId = 1;

    private static SaveAuction Auction(int itemId, string tag)
    {
        var id = nextId++;
        var end = new DateTime(2026, 9, 3).AddMinutes(id);
        return new SaveAuction
        {
            Id = id, UId = 7_000_000 + id, Uuid = (7_000_000L + id).ToString("x32"), ItemId = itemId, Tag = tag,
            End = end, Start = end.AddDays(-1), AuctioneerId = (0xabcL).ToString("x32"),
            Bids = new List<SaveBids>(), NbtData = new NbtData { data = new byte[] { 10, 0, (byte)id } },
            Enchantments = new List<Enchantment>(), NBTLookup = new List<NBTLookup>(),
            CoopMembers = new List<UuId>(), ClaimedBids = new List<UuId>(),
        };
    }

    private static SaveAuction Child(int id)
    {
        var a = Auction(1, "A");
        a.Id = id;
        a.Bids = Enumerable.Range(0, id % 3).Select(i => new SaveBids { Id = id * 10 + i, Amount = i }).ToList();
        a.NBTLookup = Enumerable.Range(0, id % 2).Select(i => new NBTLookup((short)i, id) { AuctionId = id }).ToList();
        a.Enchantments = Enumerable.Range(0, id % 4).Select(i => new Enchantment { Id = id * 10 + i, SaveAuctionId = id }).ToList();
        a.CoopMembers = id % 5 == 0 ? new List<UuId> { new("coop" + id) } : new List<UuId>();
        a.ClaimedBids = id % 7 == 0 ? new List<UuId> { new("claim" + id) } : new List<UuId>();
        return a;
    }

    private InMemoryAuctionBlobStore store;
    private Checkpoints cp;

    [SetUp]
    public void Setup()
    {
        store = new InMemoryAuctionBlobStore(new List<string>());
        cp = new Checkpoints();
    }

    private static string Describe(SaveAuction a)
        => string.Join("|", a.Id, string.Join(",", a.Bids.Select(b => b.Id)), string.Join(",", a.NBTLookup.Select(l => l.Value)),
            string.Join(",", a.Enchantments.Select(e => e.Id)), string.Join(",", a.CoopMembers.Select(c => c.value)), string.Join(",", a.ClaimedBids.Select(c => c.value)));

    private static List<SaveAuction> Page(int count) => Enumerable.Range(1, count).Select(i => { var a = Auction(1, "A"); a.Id = i; a.NbtDataId = null; return a; }).ToList();

    // ---------------------------------------------------------------- parallel child loader

    [Test]
    public async Task ParallelChildLoader_ReturnsTheSameChildrenAsTheSequentialOne_WithinTheParallelismBound()
    {
        var results = new Dictionary<int, List<string>>();
        foreach (var parallelism in new[] { 1, 6 })
        {
            var job = new Job(store, cp, ("ARCHIVE:CHILD_QUERY_BATCH_SIZE", "10"), ("ARCHIVE:CHILD_QUERY_PARALLELISM", parallelism.ToString()));
            for (var i = 1; i <= 95; i++)
                job.Children[i] = Child(i);
            var rnd = new Random(parallelism);
            job.BeforeQuery = async _ => await Task.Delay(rnd.Next(1, 15)); // scramble the completion order
            var page = Page(95);
            await job.LoadChildren(page);
            results[parallelism] = page.Select(Describe).ToList();
            Assert.That(job.Queries["Bids"], Is.EqualTo(10), "ceil(95 / 10) chunks, one query per table each");
            Assert.That(job.MaxRunning, parallelism == 1 ? Is.EqualTo(1) : Is.InRange(2, 6), "bounded by CHILD_QUERY_PARALLELISM");
        }
        Assert.That(results[6], Is.EqualTo(results[1]));
        Assert.That(results[1].Any(d => d.Split('|')[1].Length > 0), "the fixture has bids");
        Assert.That(results[1].Any(d => d.Split('|')[4].Length > 0 && d.Split('|')[5].Length > 0 || d.Split('|')[5].Length > 0), "and claimed bidders");
    }

    // ---------------------------------------------------------------- statement timeout

    [Test]
    public async Task ChunkTimeout_IsSplitInHalf_RetriedAndCounted_AndTheResultIsUnchanged()
    {
        var baseline = new Job(store, cp, ("ARCHIVE:CHILD_QUERY_BATCH_SIZE", "80"));
        var job = new Job(store, cp, ("ARCHIVE:CHILD_QUERY_BATCH_SIZE", "80"));
        foreach (var j in new[] { baseline, job })
            for (var i = 1; i <= 80; i++)
                j.Children[i] = Child(i);
        job.TimesOut = (table, ids) => table == "Bids" && ids.Count > 25; // 80 -> 40 -> 20: needs two levels of splitting
        var expected = Page(80);
        await baseline.LoadChildren(expected);
        var before = AuctionArchiveJob.ChildQueryRetries("Bids");
        var other = AuctionArchiveJob.ChildQueryRetries("Enchantment");

        var page = Page(80);
        await job.LoadChildren(page);

        Assert.That(page.Select(Describe), Is.EqualTo(expected.Select(Describe)));
        Assert.That(AuctionArchiveJob.ChildQueryRetries("Bids") - before, Is.EqualTo(6), "80 -> 2 halves, both 40 -> 4 quarters");
        Assert.That(AuctionArchiveJob.ChildQueryRetries("Enchantment"), Is.EqualTo(other), "labelled by table");
        Assert.That(job.Queries["Bids"], Is.EqualTo(1 + 2 + 4), "80 ids, two 40s (both timed out), four 20s");
    }

    [Test]
    public void ChunkTimeout_AtMinimumSize_FailsThePass()
    {
        var job = new Job(store, cp, ("ARCHIVE:CHILD_QUERY_BATCH_SIZE", "80"));
        for (var i = 1; i <= 80; i++)
            job.Children[i] = Child(i);
        job.TimesOut = (table, ids) => table == "UuId_coop"; // never succeeds
        var before = AuctionArchiveJob.ChildQueryRetries("UuId_coop");

        var ex = Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => job.LoadChildren(Page(80)));

        Assert.That(ex.Number, Is.EqualTo(1969));
        Assert.That(AuctionArchiveJob.ChildQueryRetries("UuId_coop") - before, Is.EqualTo(6), "80 -> 40 -> 20 -> 10 on the first path (two retries per split, three splits); the first 10 id query that still times out fails the pass");
    }

    [Test]
    public void ChunkTimeout_BelowTwiceTheMinimum_IsNotSplit()
    {
        var job = new Job(store, cp, ("ARCHIVE:CHILD_QUERY_BATCH_SIZE", "19"));
        for (var i = 1; i <= 19; i++)
            job.Children[i] = Child(i);
        job.TimesOut = (table, ids) => true;
        var before = AuctionArchiveJob.ChildQueryRetries("Bids");

        Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => job.LoadChildren(Page(19)));

        Assert.That(AuctionArchiveJob.ChildQueryRetries("Bids"), Is.EqualTo(before));
    }

    [Test]
    public void OtherQueryErrors_AreNotRetried()
    {
        Assert.That(AuctionArchiveJob.IsStatementTimeout(new InvalidOperationException("boom")), Is.False);
        Assert.That(AuctionArchiveJob.IsStatementTimeout(ServerError(1213, "deadlock")), Is.False);
        Assert.That(AuctionArchiveJob.IsStatementTimeout(Timeout()), Is.True);
        Assert.That(AuctionArchiveJob.IsStatementTimeout(new Exception("wrapped", Timeout())), Is.True, "EF wraps provider exceptions");
        Assert.That(AuctionArchiveJob.IsStatementTimeout(new AggregateException(Timeout())), Is.True);
    }

    [Test]
    public async Task PageTimeout_HalvesThePageSize_AndStillArchivesEveryRow()
    {
        var job = new Job(store, cp, ("ARCHIVE:PAGE_SIZE", "2000"), ("ARCHIVE:MAX_ROWS_PER_PART", "1000"), ("ARCHIVE:ITEM_PARALLELISM", "1"));
        for (var i = 0; i < 1500; i++)
            job.Auctions.Add(Auction(1, "A"));
        var takes = new ConcurrentQueue<int>();
        job.OnPage = (item, take, ct) =>
        {
            takes.Enqueue(take);
            return take > 600 ? throw Timeout() : Task.CompletedTask;
        };
        var before = AuctionArchiveJob.ChildQueryRetries("Auctions");

        Assert.That(await job.Month(new[] { 1 }), Is.True);

        Assert.That(takes.Take(3), Is.EqualTo(new[] { 2000, 1000, 500 }));
        Assert.That(takes.Skip(2).All(t => t == 500), "the reduced size is kept for the rest of the item");
        Assert.That(AuctionArchiveJob.ChildQueryRetries("Auctions") - before, Is.EqualTo(2));
        Assert.That((await store.ReadIndexAsync("A")).Months.Single().Rows, Is.EqualTo(1500));
        Assert.That(job.Locations.Count, Is.EqualTo(1500));
    }

    [Test]
    public void PageTimeout_AtMinimumPageSize_Fails()
    {
        var job = new Job(store, cp, ("ARCHIVE:PAGE_SIZE", "250"), ("ARCHIVE:ITEM_PARALLELISM", "1"));
        job.Auctions.Add(Auction(1, "A"));
        var takes = new ConcurrentQueue<int>();
        job.OnPage = (item, take, ct) => { takes.Enqueue(take); throw Timeout(); };

        Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => job.Month(new[] { 1 }));

        Assert.That(takes, Is.EqualTo(new[] { 250, 125 }), "125 / 2 would be below 100");
    }

    // ---------------------------------------------------------------- parallel items

    private static void Seed(Job job, int items, int rowsPerItem)
    {
        for (var item = 1; item <= items; item++)
            for (var i = 0; i < rowsPerItem; i++)
            {
                job.Auctions.Add(Auction(item, "SHARED"));          // every item feeds the same tag
                if (i % 2 == 0)
                    job.Auctions.Add(Auction(item, "OWN_" + item)); // and one of its own
            }
    }

    [Test]
    public async Task ParallelItems_CheckpointsHoldOnlyTheirOwnRuns_PartNumbersStayUniqueAndDense()
    {
        var job = new Job(store, cp, ("ARCHIVE:ITEM_PARALLELISM", "4"));
        Seed(job, 8, 5);
        var rnd = new Random(7);
        job.OnPage = async (item, take, ct) => await Task.Delay(rnd.Next(1, 20), ct);
        var items = Enumerable.Range(1, 8).ToList();

        Assert.That(await job.Month(items), Is.True);

        Assert.That(cp.Data.Keys, Is.EquivalentTo(items));
        foreach (var item in items)
        {
            var tags = cp.Data[item].Tags;
            Assert.That(tags["SHARED"].Rows, Is.EqualTo(5), $"item {item} counted only its own rows");
            Assert.That(tags["SHARED"].IndexRows, Is.EqualTo(5));
            Assert.That(tags.Keys, Is.EquivalentTo(new[] { "SHARED", "OWN_" + item }), "no other item's tags");
            Assert.That(tags["OWN_" + item].Rows, Is.EqualTo(3));
            Assert.That(tags["OWN_" + item].Parts.Select(p => p.Part), Is.EqualTo(new[] { 0, 1 }));
        }
        var shared = cp.Data.Values.SelectMany(c => c.Tags["SHARED"].Parts).ToList();
        Assert.That(shared.Select(p => p.Part).OrderBy(p => p), Is.EqualTo(Enumerable.Range(0, shared.Count)), "unique, dense part numbers over all items");
        var entry = (await store.ReadIndexAsync("SHARED")).Months.Single();
        Assert.That(entry.Rows, Is.EqualTo(40));
        Assert.That(entry.PartSha256.Count, Is.EqualTo(shared.Count));
        foreach (var part in shared)
            Assert.That(entry.PartSha256[part.Part], Is.EqualTo(part.Sha256), "the checkpoint hash is the hash at its part number");
        Assert.That(job.Locations.Count, Is.EqualTo(job.Auctions.Count), "every row is located exactly once");
        foreach (var a in job.Auctions)
            Assert.That(job.Locations[a.UId].tag, Is.EqualTo(job.Tags[a.Tag]));
    }

    [Test]
    public async Task ParallelItems_ArchiveTheSameRowsAsASequentialWalk()
    {
        var all = new Dictionary<int, (long rows, List<long> uids)>();
        var seed = new Job(store, cp);
        Seed(seed, 6, 4);
        foreach (var parallelism in new[] { 1, 4 })
        {
            store = new InMemoryAuctionBlobStore(new List<string>());
            cp = new Checkpoints();
            var job = new Job(store, cp, ("ARCHIVE:ITEM_PARALLELISM", parallelism.ToString()));
            job.Auctions.AddRange(seed.Auctions.Select(a => { var c = Auction(a.ItemId, a.Tag); c.Id = a.Id; c.UId = a.UId; c.End = a.End; return c; }));
            Assert.That(await job.Run(), Is.EqualTo(1));
            long rows = 0;
            foreach (var tag in job.Tags.Keys)
                rows += (await store.ReadIndexAsync(tag)).Months.Single().Rows;
            all[parallelism] = (rows, job.Locations.Keys.OrderBy(u => u).ToList());
            Assert.That(job.Months.ContainsKey((0, ArchiveMonths.ToIndex(Sep))), Is.True);
        }
        Assert.That(all[4].rows, Is.EqualTo(all[1].rows));
        Assert.That(all[4].uids, Is.EqualTo(all[1].uids));
    }

    [Test]
    public async Task ParallelItems_OneFailingItemCancelsTheOthers_FinishedItemsKeepTheirCheckpoints_NothingIsFinalized()
    {
        var job = new Job(store, cp, ("ARCHIVE:ITEM_PARALLELISM", "3"));
        Seed(job, 5, 3);
        job.Auctions.First(a => a.ItemId == 3).Tag = null; // item 3 can not be archived
        var cancelled = new ConcurrentBag<int>();
        job.OnPage = async (item, take, ct) =>
        {
            if (item == 3)
            {
                while (cp.Data.Count < 2) // let items 1 and 2 finish first
                    await Task.Delay(1);
            }
            else if (item >= 4)
            {
                try { await Task.Delay(Timeout_Infinite, ct); }
                catch (OperationCanceledException) { cancelled.Add(item); throw; }
            }
        };

        var ok = await job.Month(new[] { 1, 2, 3, 4, 5 });

        Assert.That(ok, Is.False);
        Assert.That(cp.Data.Keys, Is.EquivalentTo(new[] { 1, 2 }), "finished items stay checkpointed, the failed and the cancelled ones are not");
        Assert.That(cancelled.OrderBy(i => i), Is.EqualTo(new[] { 4, 5 }));
        Assert.That(await store.ReadIndexAsync("SHARED"), Is.Null, "no tag-month is finalized");
        Assert.That(job.Months, Is.Empty);
        Assert.That(cp.Log.Where(l => l.StartsWith("finalized")), Is.Empty);
    }

    private const int Timeout_Infinite = System.Threading.Timeout.Infinite;

    [Test]
    public async Task ParallelItems_FinalizeOnceAfterEveryItem_ThenTheSentinel()
    {
        var job = new Job(store, cp, ("ARCHIVE:ITEM_PARALLELISM", "4"));
        Seed(job, 7, 3);
        var rnd = new Random(3);
        job.OnPage = async (item, take, ct) => await Task.Delay(rnd.Next(1, 10), ct);

        Assert.That(await job.Run(), Is.EqualTo(1));

        var log = cp.Log.ToList();
        var lastSave = log.FindLastIndex(l => l.StartsWith("saved:"));
        var finalized = log.Select((l, i) => (l, i)).Where(x => x.l.StartsWith("finalized:")).ToList();
        Assert.That(finalized.Count, Is.EqualTo(8), "one per tag (shared + 7 own), not one per item");
        Assert.That(finalized.Select(f => f.l).Distinct().Count(), Is.EqualTo(8));
        Assert.That(finalized.All(f => f.i > lastSave), "after the last item checkpoint");
        Assert.That(log.Count(l => l == "sentinel"), Is.EqualTo(1));
        Assert.That(log.IndexOf("sentinel"), Is.GreaterThan(finalized.Max(f => f.i)));
        Assert.That(cp.Cleared, Is.EqualTo(1));
    }

    [Test]
    public async Task Restart_ItemsInFlightLeaveAGap_OnlyThoseItemsAreRedone()
    {
        // first run: items 1..4 complete, then pretend items 2 and 4 never saved their checkpoints while 3 did (parts 1.. of SHARED lie before and behind them)
        var first = new Job(store, cp, ("ARCHIVE:ITEM_PARALLELISM", "1"), ("ARCHIVE:MAX_ROWS_PER_PART", "100"));
        Seed(first, 4, 2);
        Assert.That(await first.Month(new[] { 1, 2, 3, 4 }), Is.True);
        var saved = cp.Data.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.That(saved.Select(s => s.Value.Tags["SHARED"].Parts.Single().Part), Is.EqualTo(new[] { 0, 1, 2, 3 }));

        cp = new Checkpoints();
        cp.Data[1] = saved[1];
        cp.Data[3] = saved[3]; // SHARED part 2; part 1 (item 2) is missing
        var restarted = new Job(store, cp, ("ARCHIVE:ITEM_PARALLELISM", "1"), ("ARCHIVE:MAX_ROWS_PER_PART", "100"));
        restarted.Auctions.AddRange(first.Auctions);

        Assert.That(await restarted.Month(new[] { 1, 2, 3, 4 }), Is.True);

        Assert.That(restarted.Paged.Distinct().OrderBy(i => i), Is.EqualTo(new[] { 2, 3, 4 }), "item 3's part number is behind the gap: it is redone, item 1 is not");
        Assert.That(cp.Cleared, Is.EqualTo(1), "stale checkpoints are replaced");
        Assert.That(cp.Data.Keys, Is.EquivalentTo(new[] { 1, 2, 3, 4 }));
        var shared = cp.Data.Values.SelectMany(c => c.Tags["SHARED"].Parts).Select(p => p.Part).OrderBy(p => p).ToList();
        Assert.That(shared, Is.EqualTo(new[] { 0, 1, 2, 3 }));
        Assert.That((await store.ReadIndexAsync("SHARED")).Months.Single().Rows, Is.EqualTo(8));
    }

    [Test]
    public async Task ChildBatchSizeAboveTheDiveLimit_IsClamped_AndWarnedAboutOncePerPass()
    {
        var log = new ListLogger<AuctionArchiveJob>();
        var job = new LoggingJob(store, cp, log, ("ARCHIVE:CHILD_QUERY_BATCH_SIZE", "500"));
        Seed(job, 1, 2);

        await job.Run();

        Assert.That(log.Entries.Count(e => e.level == Microsoft.Extensions.Logging.LogLevel.Warning && e.message.Contains("CHILD_QUERY_BATCH_SIZE=500") && e.message.Contains("199")), Is.EqualTo(1));
    }

    private class LoggingJob : Job
    {
        public LoggingJob(IAuctionBlobStore store, Checkpoints cp, ListLogger<AuctionArchiveJob> log, params (string, string)[] config) : base(store, cp, config)
            => typeof(AuctionArchiveJob).GetField("logger", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(this, log);
    }
}
