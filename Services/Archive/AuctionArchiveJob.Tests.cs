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

public class AuctionArchiveJobTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private const int Aug = 202608;
    private const int Sep = 202609;

    /// <summary>Archive job over in-memory tables; the page query uses the real <see cref="AuctionArchiveJob.PageFilter"/>.</summary>
    private class TestableArchiveJob : AuctionArchiveJob
    {
        public readonly List<SaveAuction> Auctions = new();
        public Exception ItemIdsFailure;
        public int ItemIdScans;
        public readonly Dictionary<string, ushort> Tags = new();
        public readonly Dictionary<(ushort tag, ushort month), ArchivedMonth> Months = new();
        public readonly Dictionary<long, (ushort tag, ushort month, byte part)> Locations = new();
        public readonly Dictionary<(uint bidder, uint end, long uid), ulong> Bidders = new();
        public readonly List<string> Events;
        public readonly List<(int item, DateTime cursor, int cursorId)> Pages = new();
        public bool SchemaEnsured;

        public TestableArchiveJob(IAuctionBlobStore store, IConfiguration config, List<string> events, Microsoft.Extensions.Logging.ILogger<AuctionArchiveJob> logger = null)
            : base(store, config, logger ?? NullLogger<AuctionArchiveJob>.Instance) => Events = events;

        public readonly List<TimeSpan> Delays = new();

        protected override Task<IReadOnlyDictionary<int, DateTime>> GetAuctionItemFirstEndsAsync(CancellationToken cancellationToken)
        {
            ItemIdScans++;
            if (ItemIdsFailure != null)
                throw ItemIdsFailure;
            return Task.FromResult<IReadOnlyDictionary<int, DateTime>>(Auctions.GroupBy(a => a.ItemId).ToDictionary(g => g.Key, g => g.Min(a => a.End)));
        }

        protected override Task EnsureSchemaAsync(CancellationToken cancellationToken)
        {
            SchemaEnsured = true;
            return Task.CompletedTask;
        }

        protected override Task<ArchiveMonthState> GetMonthStateAsync(int month, CancellationToken cancellationToken)
        {
            var index = ArchiveMonths.ToIndex(month);
            var rows = Months.Values.Where(m => m.Month == index).ToList();
            return Task.FromResult(new ArchiveMonthState(rows.Any(r => r.TagId == 0),
                rows.Where(r => r.TagId != 0 && r.VerifiedAt != null).Select(r => r.TagId).ToHashSet()));
        }

        protected override Task<ushort> GetOrCreateTagIdAsync(string tag, CancellationToken cancellationToken)
        {
            if (!Tags.TryGetValue(tag, out var id))
                Tags[tag] = id = (ushort)(Tags.Count + 1);
            return Task.FromResult(id);
        }

        protected override Task<List<SaveAuction>> LoadPageAsync(int itemId, DateTime cursorEnd, int cursorId, DateTime monthEnd, int pageSize, CancellationToken cancellationToken)
        {
            Pages.Add((itemId, cursorEnd, cursorId));
            var filter = PageFilter(itemId, cursorEnd, cursorId, monthEnd).Compile();
            return Task.FromResult(Auctions.Where(filter).OrderBy(a => a.End).ThenBy(a => a.Id).Take(pageSize).ToList());
        }

        protected override Task LoadChildrenAsync(List<SaveAuction> page, CancellationToken cancellationToken)
        {
            Events.Add("children");
            return Task.CompletedTask;
        }

        protected override Task WriteIndexRowsAsync(ushort tagId, ushort month, IReadOnlyList<(long uid, byte part)> locations, IReadOnlyList<AuctionBidder> bidders, CancellationToken cancellationToken)
        {
            Events.Add("index_rows");
            foreach (var (uid, part) in locations)
                Locations[uid] = (tagId, month, part);
            foreach (var b in bidders)
                Bidders.TryAdd((b.BidderId, b.End, b.Uid), b.Amount);
            return Task.CompletedTask;
        }

        protected override Task UpsertArchivedMonthAsync(ArchivedMonth row, CancellationToken cancellationToken)
        {
            Events.Add($"archived_month:{row.TagId}:{row.Month}");
            Months[(row.TagId, row.Month)] = row;
            return Task.CompletedTask;
        }

        protected override Task WriteMonthSentinelAsync(int month, CancellationToken cancellationToken)
        {
            Events.Add("sentinel:" + month);
            var index = ArchiveMonths.ToIndex(month);
            var rows = Months.Values.Where(m => m.Month == index && m.TagId != 0).ToList();
            Months[(0, index)] = new ArchivedMonth { TagId = 0, Month = index, Rows = (uint)rows.Sum(r => r.Rows), ArchivedAt = Now, VerifiedAt = Now, Source = "mariadb", Sha256 = EmptySha256 };
            return Task.CompletedTask;
        }

        protected override Task Delay(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delays.Add(delay);
            return Task.CompletedTask;
        }

        public Task<int> Run(DateTime now) => RunOnceAsync(now, CancellationToken.None);
    }

    private List<string> events;
    private InMemoryAuctionBlobStore store;

    [SetUp]
    public void Setup()
    {
        events = new List<string>();
        store = new InMemoryAuctionBlobStore(events);
    }

    private TestableArchiveJob Job(params (string, string)[] config)
    {
        var job = new TestableArchiveJob(store, ArchiveTestConfig.Build(config), events);
        return job;
    }

    private static int nextId = 1;

    private static SaveAuction Auction(int itemId, string tag, DateTime end, params SaveBids[] bids)
    {
        var id = nextId++;
        return new SaveAuction
        {
            Id = id,
            UId = 1_000_000 + id,
            Uuid = (1_000_000L + id).ToString("x32"),
            ItemId = itemId,
            Tag = tag,
            End = DateTime.SpecifyKind(end, DateTimeKind.Unspecified),
            Start = DateTime.SpecifyKind(end.AddDays(-1), DateTimeKind.Unspecified),
            AuctioneerId = "a".PadLeft(32, '0'),
            HighestBidAmount = bids.Length == 0 ? 0 : bids.Max(b => b.Amount),
            Bids = bids.ToList(),
            NbtData = new NbtData { data = new byte[] { 10, 0, (byte)id } },
            Enchantments = new List<Enchantment>(),
            NBTLookup = new List<NBTLookup>(),
        };
    }

    private static SaveBids Bid(int bidderId, long amount) => new() { BidderId = bidderId, Bidder = bidderId.ToString("x32"), Amount = amount, Timestamp = new DateTime(2026, 8, 3) };

    [Test]
    public async Task ItemsFirstSeenLater_AreSkippedWithoutAQuery_AndEmptyItemMonthsDoNotPause()
    {
        // 4,771 ItemIds x 88 months with one query + a pause each made the backfill mostly sleep; items released later
        // must not be paged for the months before their first auction, and an empty ItemId-month must not pause.
        var job = Job(("ARCHIVE:OLDEST_MONTH", "2026-07"), ("ARCHIVE:TAG_PAUSE_MS", "100"), ("ARCHIVE:PAGE_PAUSE_MS", "200"), ("ARCHIVE:MONTH_PAUSE_MS", "60000"));
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 7, 3)));
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 9, 3)));   // item 1 exists in july, has nothing in august
        job.Auctions.Add(Auction(2, "TERMINATOR", new DateTime(2026, 9, 5))); // item 2 is first seen in september

        var completed = await job.Run(Now);

        Assert.That(completed, Is.EqualTo(3), "july, august and september are complete");
        Assert.That(job.Pages.Where(p => p.item == 2).Select(p => ArchiveMonths.MonthOf(p.cursor)).Distinct(), Is.EqualTo(new[] { Sep }), "item 2 is only paged from its first month on");
        Assert.That(job.Pages.Where(p => p.item == 1).Select(p => ArchiveMonths.MonthOf(p.cursor)).Distinct(), Is.EquivalentTo(new[] { 202607, 202608, Sep }), "an existing item is still checked in a month without rows");
        Assert.That(job.Delays.Where(d => d.TotalMilliseconds == 200), Is.Empty, "no page pause after an empty ItemId-month");
        Assert.That(job.Delays.Count(d => d.TotalMilliseconds == 100), Is.EqualTo(3), "one tag pause per ItemId-month with rows");
        Assert.That(store.Objects.Keys, Does.Contain("auctions/v2/TERMINATOR/2026-09/000.msgpack.br"));
    }

    [Test]
    public async Task ArchivesClosedMonthsOldestFirstWithIndexMarkerAndSentinel()
    {
        var job = Job();
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 8, 3), Bid(7, 100)));
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 8, 31, 23, 59, 59)));
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 9, 1)));
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 10, 2))); // october is not closed on 2026-10-21

        var completed = await job.Run(Now);

        Assert.That(completed, Is.EqualTo(2));
        Assert.That(store.Objects.Keys, Is.EquivalentTo(new[]
        {
            "auctions/v2/HYPERION/2026-08/000.msgpack.br",
            "auctions/v2/HYPERION/2026-09/000.msgpack.br",
            "auctions/v2/HYPERION/index.msgpack.br",
        }), "nothing for october");
        var hyperion = job.Tags["HYPERION"];
        var aug = ArchiveMonths.ToIndex(Aug);
        Assert.That(job.Months[(hyperion, aug)].Rows, Is.EqualTo(2));
        Assert.That(job.Months[(hyperion, aug)].IndexRows, Is.EqualTo(2));
        Assert.That(job.Months[(hyperion, aug)].VerifiedAt, Is.Not.Null);
        Assert.That(job.Months.ContainsKey((0, aug)) && job.Months.ContainsKey((0, ArchiveMonths.ToIndex(Sep))), Is.True);
        Assert.That(job.Locations, Has.Count.EqualTo(3));
        Assert.That(job.Locations.Values.Select(l => l.month).Distinct(), Is.EquivalentTo(new[] { aug, ArchiveMonths.ToIndex(Sep) }));
        Assert.That(events.IndexOf("sentinel:" + Aug), Is.LessThan(events.IndexOf("write:HYPERION:" + Sep + ":0")), "oldest month first");

        var index = await store.ReadIndexAsync("HYPERION");
        Assert.That(index.Months.Select(m => (m.Month, m.Rows)), Is.EqualTo(new[] { (Aug, 2), (Sep, 1) }));
        var block = await store.ReadBlockAsync("HYPERION", Aug, 0);
        Assert.That(block.Sha256(), Is.EqualTo(job.Months[(hyperion, aug)].Sha256));

        events.Clear();
        Assert.That(await job.Run(Now), Is.EqualTo(0), "second pass skips archived months");
        Assert.That(job.Pages, Has.Count.EqualTo(2), "no page loaded on the second pass");
    }

    [Test]
    public async Task WritesVerifiesThenIndexesThenMarksInThatOrder()
    {
        var job = Job(("ARCHIVE:MAX_ROWS_PER_PART", "2"), ("ARCHIVE:OLDEST_MONTH", "2026-09"));
        for (var i = 0; i < 3; i++)
            job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 9, 2).AddHours(i)));

        await job.Run(Now);

        var hyperionEvents = events.Where(e => e != "children").ToList();
        var tag = job.Tags["HYPERION"];
        Assert.That(hyperionEvents, Is.EqualTo(new[]
        {
            $"write:HYPERION:{Sep}:0", $"read:HYPERION:{Sep}:0:uncached",
            $"write:HYPERION:{Sep}:1", $"read:HYPERION:{Sep}:1:uncached",
            "index_rows", "tag_index:HYPERION", $"archived_month:{tag}:{ArchiveMonths.ToIndex(Sep)}",
            "sentinel:" + Sep,
        }));
    }

    [Test]
    public async Task SplitsIntoPartsOfMaxRows()
    {
        var job = Job(("ARCHIVE:MAX_ROWS_PER_PART", "2"), ("ARCHIVE:OLDEST_MONTH", "2026-09"));
        for (var i = 0; i < 5; i++)
            job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 9, 2).AddHours(i)));

        await job.Run(Now);

        var row = job.Months[(job.Tags["HYPERION"], ArchiveMonths.ToIndex(Sep))];
        Assert.That(row.Parts, Is.EqualTo(3));
        Assert.That(row.Rows, Is.EqualTo(5));
        var parts = new List<string>();
        for (var p = 0; p < 3; p++)
        {
            var block = await store.ReadBlockAsync("HYPERION", Sep, p);
            Assert.That(block.RowCount, Is.EqualTo(p < 2 ? 2 : 1));
            parts.Add(block.Sha256());
        }
        Assert.That(row.Sha256, Is.EqualTo(ArchivedAuctionBlock.CombinedSha256(parts)));
        Assert.That(job.Locations.Values.Select(l => l.part).OrderBy(p => p), Is.EqualTo(new byte[] { 0, 0, 1, 1, 2 }));
    }

    /// <summary>Regression: with a plain <c>End &gt;= cursor</c> keyset, a page full of rows sharing one End returned the same page forever.</summary>
    [Test]
    public async Task PagingEmitsEveryRowOnceWhenMoreRowsShareAnEndThanFitInAPage()
    {
        var job = Job(("ARCHIVE:PAGE_SIZE", "2"), ("ARCHIVE:OLDEST_MONTH", "2026-09"));
        var end = new DateTime(2026, 9, 5, 12, 0, 0);
        for (var i = 0; i < 5; i++)
            job.Auctions.Add(Auction(1, "HYPERION", end));
        job.Auctions.Add(Auction(1, "HYPERION", end.AddSeconds(1)));

        await job.Run(Now);

        var block = await store.ReadBlockAsync("HYPERION", Sep, 0);
        Assert.That(block.AuctionUid, Is.EquivalentTo(job.Auctions.Select(a => a.UId)));
        Assert.That(block.AuctionUid.Distinct().Count(), Is.EqualTo(6));
        Assert.That(job.Pages.Count(p => p.item == 1), Is.EqualTo(4), "3 full pages and one empty");
    }

    [Test]
    public async Task CorruptReadBackLeavesNoIndexNoMarkerNoSentinelAndRetries()
    {
        var job = Job(("ARCHIVE:OLDEST_MONTH", "2026-09"));
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 9, 2)));
        store.CorruptReadBack = true;

        Assert.That(await job.Run(Now), Is.EqualTo(0));
        Assert.That(job.Locations, Is.Empty);
        Assert.That(events, Does.Not.Contain("index_rows"));
        Assert.That(job.Months, Is.Empty);
        Assert.That(job.Months.Keys.Any(k => k.tag == 0), Is.False, "no sentinel");
        Assert.That(await store.ReadIndexAsync("HYPERION"), Is.Null);

        store.CorruptReadBack = false;
        Assert.That(await job.Run(Now), Is.EqualTo(1));
        Assert.That(job.Locations, Has.Count.EqualTo(1));
    }

    /// <summary>The tag of a block comes from the rows: one ItemId with several tags gets one tag run (own blocks) per tag.</summary>
    [Test]
    public async Task ItemIdWithTwoTagsGetsOneVerifiedRunPerTag()
    {
        var job = Job(("ARCHIVE:OLDEST_MONTH", "2026-09"));
        job.Auctions.Add(Auction(1, "TAG_A", new DateTime(2026, 9, 2)));
        job.Auctions.Add(Auction(1, "TAG_B", new DateTime(2026, 9, 3)));
        job.Auctions.Add(Auction(1, "TAG_A", new DateTime(2026, 9, 4)));

        Assert.That(await job.Run(Now), Is.EqualTo(1));

        Assert.That((await store.ReadBlockAsync("TAG_A", Sep, 0)).RowCount, Is.EqualTo(2));
        Assert.That((await store.ReadBlockAsync("TAG_B", Sep, 0)).RowCount, Is.EqualTo(1));
        var month = ArchiveMonths.ToIndex(Sep);
        foreach (var tag in new[] { "TAG_A", "TAG_B" })
        {
            var row = job.Months[(job.Tags[tag], month)];
            Assert.That(row.VerifiedAt, Is.Not.Null);
            Assert.That(row.IndexRows, Is.EqualTo(row.Rows));
        }
        Assert.That(job.Months.ContainsKey((0, month)), Is.True, "sentinel");
        Assert.That(job.Locations, Has.Count.EqualTo(3));
    }

    /// <summary>Several ItemIds feeding one tag-month (ItemId 0 rows carry real tags) land in distinct parts, nothing is overwritten.</summary>
    [Test]
    public async Task TwoItemIdsWithTheSameTagUseDistinctParts()
    {
        var job = Job(("ARCHIVE:OLDEST_MONTH", "2026-09"));
        var fromZero = Auction(0, "PET_SKIN_X", new DateTime(2026, 9, 2));
        var fromReal = Auction(77, "PET_SKIN_X", new DateTime(2026, 9, 3));
        job.Auctions.Add(fromReal);
        job.Auctions.Add(fromZero);

        Assert.That(await job.Run(Now), Is.EqualTo(1));

        var month = ArchiveMonths.ToIndex(Sep);
        var row = job.Months[(job.Tags["PET_SKIN_X"], month)];
        Assert.That((row.Rows, row.Parts, row.IndexRows), Is.EqualTo((2u, (byte)2, 2u)));
        foreach (var auction in new[] { fromZero, fromReal })
        {
            var (tag, locMonth, part) = job.Locations[auction.UId];
            Assert.That((tag, locMonth), Is.EqualTo((job.Tags["PET_SKIN_X"], month)));
            var block = await store.ReadBlockAsync("PET_SKIN_X", Sep, part);
            Assert.That(block.AuctionUid, Is.EqualTo(new[] { auction.UId }));
        }
        Assert.That(job.Locations.Values.Select(l => l.part).Distinct().Count(), Is.EqualTo(2));
        Assert.That(job.Months.ContainsKey((0, month)), Is.True);
        var index = await store.ReadIndexAsync("PET_SKIN_X");
        Assert.That(index.Months.Single().Parts, Is.EqualTo(2));
    }

    /// <summary>A crash after the parts were written (no sentinel) is re-run: same part keys, same rows, no extra parts.</summary>
    [Test]
    public async Task RerunAfterACrashOverwritesTheSamePartsInsteadOfAddingNew()
    {
        var job = Job(("ARCHIVE:OLDEST_MONTH", "2026-09"));
        job.Auctions.Add(Auction(0, "PET_SKIN_X", new DateTime(2026, 9, 2)));
        job.Auctions.Add(Auction(77, "PET_SKIN_X", new DateTime(2026, 9, 3)));
        await job.Run(Now);
        var keys = store.Objects.Keys.OrderBy(k => k).ToList();
        var locations = job.Locations.ToDictionary(kv => kv.Key, kv => kv.Value);
        job.Months.Clear(); // crash before the markers and the sentinel were written

        Assert.That(await job.Run(Now), Is.EqualTo(1));

        Assert.That(store.Objects.Keys.OrderBy(k => k), Is.EqualTo(keys));
        Assert.That(job.Locations, Is.EquivalentTo(locations));
    }

    [Test]
    public async Task RowWithoutTagFailsTheMonthAndLogsItsId()
    {
        var log = new ListLogger<AuctionArchiveJob>();
        var job = new TestableArchiveJob(store, ArchiveTestConfig.Build(("ARCHIVE:OLDEST_MONTH", "2026-09")), events, log);
        var broken = Auction(1, null, new DateTime(2026, 9, 3));
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 9, 2)));
        job.Auctions.Add(broken);
        job.Auctions.Add(Auction(2, "", new DateTime(2026, 9, 4)));

        Assert.That(await job.Run(Now), Is.EqualTo(0));

        Assert.That(job.Months, Is.Empty, "no markers and no sentinel");
        Assert.That(events.Any(e => e.StartsWith("sentinel:")), Is.False);
        Assert.That(log.Entries.Any(e => e.level == Microsoft.Extensions.Logging.LogLevel.Error && e.message.Contains($"Auction {broken.Id} ")), Is.True);
    }

    [Test]
    public async Task FailingItemIdScanWritesNoSentinelAndTheNextPassRetries()
    {
        var log = new ListLogger<AuctionArchiveJob>();
        var job = new TestableArchiveJob(store, ArchiveTestConfig.Build(("ARCHIVE:OLDEST_MONTH", "2026-09")), events, log);
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 9, 2)));
        job.ItemIdsFailure = new InvalidOperationException("timeout");

        Assert.That(await job.Run(Now), Is.EqualTo(0));
        Assert.That(job.Months, Is.Empty);
        Assert.That(job.Pages, Is.Empty);
        Assert.That(log.Entries.Any(e => e.level == Microsoft.Extensions.Logging.LogLevel.Error), Is.True);

        job.ItemIdsFailure = null;
        Assert.That(await job.Run(Now), Is.EqualTo(1));
    }

    [Test]
    public async Task ItemIdScanRunsOncePerPassAndOnlyWhenAMonthNeedsWork()
    {
        var job = Job(("ARCHIVE:OLDEST_MONTH", "2026-08"));
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 8, 2)));
        job.Auctions.Add(Auction(1, "HYPERION", new DateTime(2026, 9, 2)));

        Assert.That(await job.Run(Now), Is.EqualTo(2));
        Assert.That(job.ItemIdScans, Is.EqualTo(1));
        await job.Run(Now);
        Assert.That(job.ItemIdScans, Is.EqualTo(1), "nothing left to archive, no scan");
    }

    [Test]
    public async Task BidderIndexKeepsMaxAmountPerBidderAndSkipsBidsWithoutId()
    {
        var job = Job(("ARCHIVE:OLDEST_MONTH", "2026-09"));
        var end = new DateTime(2026, 9, 2, 3, 4, 5);
        var auction = Auction(1, "HYPERION", end, Bid(7, 100), Bid(8, 150), Bid(7, 200), Bid(0, 250));
        job.Auctions.Add(auction);

        await job.Run(Now);

        var unix = (uint)new DateTimeOffset(DateTime.SpecifyKind(end, DateTimeKind.Utc)).ToUnixTimeSeconds();
        Assert.That(job.Bidders, Is.EquivalentTo(new Dictionary<(uint, uint, long), ulong>
        {
            [(7, unix, auction.UId)] = 200,
            [(8, unix, auction.UId)] = 150,
        }));
        var row = job.Months[(job.Tags["HYPERION"], ArchiveMonths.ToIndex(Sep))];
        Assert.That(row.IndexBidderRows, Is.EqualTo(2));
        Assert.That(row.SoldRows, Is.EqualTo(1));
        Assert.That(row.Note, Does.Contain("1 bids without bidder id"));
        var block = await store.ReadBlockAsync("HYPERION", Sep, 0);
        Assert.That(block.ToAuctions().Single().Bids, Has.Count.EqualTo(4), "the blob keeps every bid");
    }

    [Test]
    public void IndexInsertsAreBatchedAndIdempotent()
    {
        var locations = Enumerable.Range(0, AuctionArchiveJob.IndexBatchSize + 1).Select(i => ((long)i - 5, (byte)1)).ToList();
        var sql = AuctionArchiveJob.BuildLocationInserts(7, 93, locations).ToList();
        Assert.That(sql, Has.Count.EqualTo(2));
        Assert.That(sql[0], Does.StartWith("INSERT INTO auction_locations (uid, tag_id, month, part) VALUES (-5,7,93,1),(-4,7,93,1)"));
        Assert.That(sql[0], Does.EndWith("ON DUPLICATE KEY UPDATE tag_id = VALUES(tag_id), month = VALUES(month), part = VALUES(part)"));
        var bidders = AuctionArchiveJob.BuildBidderInserts(new[] { new AuctionBidder { BidderId = 3, End = 1700000000, Uid = -9, Amount = 12 } }).Single();
        Assert.That(bidders, Is.EqualTo("INSERT IGNORE INTO auction_bidders (bidder_id, `end`, uid, amount) VALUES (3,1700000000,-9,12)"));
    }

    [Test]
    public async Task DisabledJobDoesNothing()
    {
        var job = new TestableArchiveJob(store, ArchiveTestConfig.Build(("ARCHIVE:ENABLED", "false")), events);
        await job.StartAsync(CancellationToken.None);
        await job.ExecuteTask;
        Assert.That(job.SchemaEnsured, Is.False);
        Assert.That(events, Is.Empty);
    }
}
