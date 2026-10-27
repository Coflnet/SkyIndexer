using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

public class AuctionRetentionWorkerTests
{
    private static readonly DateTime Now = new(2026, 10, 21, 12, 0, 0, DateTimeKind.Utc);
    private const int OldMonth = 202301;   // older than 3 years
    private const int OldMonth2 = 202302;
    private const ushort Tag = 5;
    private const string ItemTag = "HYPERION";

    /// <summary>Retention worker over in-memory tables; deletes are applied by interpreting the structured statements.</summary>
    private class TestableRetentionWorker : AuctionRetentionWorker
    {
        public readonly List<DeleteCandidate> Auctions = new();
        public readonly Dictionary<(string table, string column), List<long>> Children = new();
        public readonly HashSet<long> NbtData = new();
        public readonly Dictionary<int, List<ArchivedMonthRowState>> MonthRows = new();
        public readonly Dictionary<int, Dictionary<string, ushort>> VerifiedTags = new();
        public readonly HashSet<(ushort month, long uid)> Locations = new();
        public readonly Queue<HealthResult> Health = new();
        public readonly List<List<DeleteStatement>> Executed = new();
        public readonly List<(int month, long total, bool completed)> Recorded = new();
        public int DeletedToday;
        /// <summary>Seller rows committed so far and the order of seller inserts versus deletes.</summary>
        public readonly HashSet<(uint seller, uint end, long uid)> Sellers = new();
        public readonly List<string> Order = new();
        public Exception SellerFailure;
        public readonly Dictionary<string, int> Players = new();

        public readonly InMemoryAuctionBlobStore Store;
        public readonly Dictionary<int, List<RetentionTagParts>> MonthParts = new();
        public readonly HashSet<int> SellerPassDone = new();

        public TestableRetentionWorker(InMemoryAuctionBlobStore store, IConfiguration config) : base(store, config, NullLogger<AuctionRetentionWorker>.Instance) => Store = store;

        protected override Task<IReadOnlyList<RetentionTagParts>> GetMonthPartsAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RetentionTagParts>>(MonthParts.GetValueOrDefault(month) ?? new List<RetentionTagParts>());
        protected override Task<bool> IsSellerPassDoneAsync(int month, CancellationToken cancellationToken) => Task.FromResult(SellerPassDone.Contains(month));
        protected override Task MarkSellerBackfillDoneAsync(int month, CancellationToken cancellationToken)
        {
            Order.Add("marker:" + month);
            SellerPassDone.Add(month);
            return Task.CompletedTask;
        }

        protected override Task<int> CountMonthsDeletedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken) => Task.FromResult(DeletedToday);

        protected override Task<IReadOnlyList<ArchivedMonthRowState>> GetArchivedMonthRowsAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchivedMonthRowState>>(MonthRows.GetValueOrDefault(month) ?? new List<ArchivedMonthRowState>());

        protected override Task<IReadOnlyDictionary<string, ushort>> GetVerifiedTagsAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, ushort>>(VerifiedTags.GetValueOrDefault(month) ?? new Dictionary<string, ushort>());

        protected override Task<IReadOnlyList<DeleteCandidate>> LoadDeleteBatchAsync(DateTime cursor, DateTime monthEnd, int batchSize, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeleteCandidate>>(Auctions.Where(a => a.End >= cursor && a.End < monthEnd).OrderBy(a => a.End).Take(batchSize).ToList());

        protected override Task<int> CountLocatedUidsAsync(ushort monthIndex, IReadOnlyList<long> uids, CancellationToken cancellationToken)
            => Task.FromResult(uids.Count(u => Locations.Contains((monthIndex, u))));

        protected override Task<IReadOnlyDictionary<string, int>> ResolvePlayerIdsAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, int>>(uuids.Where(Players.ContainsKey).ToDictionary(u => u, u => Players[u]));

        protected override Task WriteSellerRowsAsync(IReadOnlyList<AuctionSeller> sellers, CancellationToken cancellationToken)
        {
            Order.Add("sellers:" + sellers.Count);
            if (SellerFailure != null)
                throw SellerFailure;
            foreach (var x in sellers)
                Sellers.Add((x.SellerId, x.End, x.Uid));
            return Task.CompletedTask;
        }

        protected override Task<Dictionary<string, long>> ExecuteDeleteAsync(IReadOnlyList<DeleteStatement> statements, CancellationToken cancellationToken)
        {
            Order.Add("delete");
            Executed.Add(statements.ToList());
            var counts = new Dictionary<string, long>();
            foreach (var s in statements)
            {
                long affected;
                if (s.Table == "Auctions")
                    affected = Auctions.RemoveAll(a => s.Ids.Contains(a.Id));
                else if (s.Table == "NbtData")
                {
                    Assert.That(s.KeepIfReferencedByTable, Is.EqualTo("Auctions"));
                    var deletable = s.Ids.Where(id => !Auctions.Any(a => a.NbtDataId == id)).ToList();
                    affected = NbtData.RemoveWhere(deletable.Contains);
                }
                else
                    affected = Children.TryGetValue((s.Table, s.Column), out var rows) ? rows.RemoveAll(s.Ids.Contains) : 0;
                counts[s.Table] = counts.GetValueOrDefault(s.Table) + affected;
            }
            return Task.FromResult(counts);
        }

        protected override Task<HealthResult> CheckHealthAsync(ArchiveOptions options, CancellationToken cancellationToken)
            => Task.FromResult(Health.Count > 0 ? Health.Dequeue() : new HealthResult(HealthStatus.Ok, "ok"));

        protected override Task RecordDeletedAsync(int month, IReadOnlyDictionary<ushort, long> deletedPerTag, long total, bool completed, CancellationToken cancellationToken)
        {
            Recorded.Add((month, total, completed));
            if (completed)
            {
                var rows = MonthRows[month];
                for (var i = 0; i < rows.Count; i++)
                    rows[i] = rows[i] with { DeletedAt = Now };
            }
            return Task.CompletedTask;
        }

        protected override Task LogSpaceHintAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> Run() => RunOnceAsync(Now, CancellationToken.None);
    }

    private static int nextId = 1;

    private static TestableRetentionWorker Worker(params (string, string)[] config)
    {
        var all = new List<(string, string)> { ("ARCHIVE:DELETE_MARIADB", "true"), ("ARCHIVE:OLDEST_MONTH", "2023-01") };
        all.AddRange(config);
        return new TestableRetentionWorker(new InMemoryAuctionBlobStore(), ArchiveTestConfig.Build(all.ToArray()));
    }

    /// <summary>Adds an archived (sentinel + verified tag row), fully indexed month with <paramref name="rows"/> auctions and their children.</summary>
    private static List<DeleteCandidate> AddArchivedMonth(TestableRetentionWorker w, int month, int rows, int? sharedNbt = null)
    {
        var (start, _) = ArchiveMonths.MonthRange(month);
        var index = ArchiveMonths.ToIndex(month);
        var added = new List<DeleteCandidate>();
        for (var i = 0; i < rows; i++)
        {
            var id = nextId++;
            var nbt = sharedNbt ?? 10_000 + id;
            var row = new DeleteCandidate(id, 500_000 + id, ItemTag, start.AddHours(i), nbt, i % 3 == 2 ? 0 : 7_000 + i % 2, (2_000 + id).ToString("x32"));
            added.Add(row);
            w.Auctions.Add(row);
            w.NbtData.Add(nbt);
            w.Locations.Add((index, row.UId));
            foreach (var (table, column) in new[] { ("Bids", "Uuid"), ("NBTLookups", "AuctionId"), ("Enchantment", "SaveAuctionId"), ("UuId", "SaveAuctionId"), ("UuId", "SaveAuctionId1") })
            {
                if (!w.Children.TryGetValue((table, column), out var list))
                    w.Children[(table, column)] = list = new List<long>();
                list.Add(id);
            }
        }
        w.MonthRows[month] = new List<ArchivedMonthRowState>
        {
            new(0, (uint)rows, (uint)rows, Now, Now, null),
            new(Tag, (uint)rows, (uint)rows, Now, Now, null),
        };
        w.VerifiedTags[month] = new Dictionary<string, ushort> { [ItemTag] = Tag };
        return added;
    }

    [Test]
    public async Task InsertsTheSellerRowsOfEveryBatchBeforeDeletingIt()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "3"));
        var rows = AddArchivedMonth(w, OldMonth, 7);
        var expected = rows.Where(r => r.SellerId > 0)
            .Select(r => ((uint)r.SellerId, (uint)new DateTimeOffset(DateTime.SpecifyKind(r.End, DateTimeKind.Utc)).ToUnixTimeSeconds(), r.UId)).ToList();

        Assert.That(await w.Run(), Is.EqualTo(1));

        Assert.That(w.Auctions, Is.Empty);
        Assert.That(w.Order.Where(o => !o.StartsWith("marker")), Is.EqualTo(new[] { "sellers:2", "delete", "sellers:2", "delete", "sellers:1", "delete" }), "7 rows in batches of 3; auctions without a seller id have no row");
        Assert.That(w.Sellers, Is.EquivalentTo(expected));
        Assert.That(expected, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task DoesNotDeleteABatchWhoseSellerInsertFailed()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "3"));
        AddArchivedMonth(w, OldMonth, 7);
        w.SellerFailure = new InvalidOperationException("table auction_sellers does not exist");

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty, "nothing deleted");
        Assert.That(w.Auctions, Has.Count.EqualTo(7));
        Assert.That(w.Recorded.Single().completed, Is.False);
        Assert.That(w.MonthRows[OldMonth].All(r => r.DeletedAt == null), Is.True, "the month stays open");
    }

    [Test]
    public void SellerRowsSkipUnknownOptedOutAndAnonymisedSellersAndUseUnixEnd()
    {
        var end = new DateTime(2020, 9, 2, 12, 0, 0, DateTimeKind.Unspecified);
        var rows = AuctionRetentionWorker.BuildSellerRows(new[]
        {
            new DeleteCandidate(1, 77, ItemTag, end, null, 123, (3000L).ToString("x32")),
            new DeleteCandidate(2, 12, ItemTag, end, null, 0),
            new DeleteCandidate(3, 13, ItemTag, end, null, 124, PlayerOptOut.LegacyPlayerUuids[0]),
            new DeleteCandidate(4, 14, ItemTag, end, null, 125, PlayerOptOut.AnonymousUuidPrefix + "7f"),
        });
        var row = rows.Single();
        Assert.That((row.SellerId, row.End, row.Uid), Is.EqualTo((123u, 1599048000u, 77L)), "same key as the archive job, import and backfill tests");
    }

    /// <summary>SellerId 0 is not always anonymous: resolved auctioneers get a row before the delete; anonymised and unresolved uuids none (documented: unresolved means no Players row, like bidders).</summary>
    [Test]
    public async Task SellerIdZeroFallsBackToTheAuctioneerUuidBeforeTheDelete()
    {
        var w = Worker();
        var rows = AddArchivedMonth(w, OldMonth, 3);
        string Uuid(int n) => n == 0 ? PlayerOptOut.AnonymousUuidPrefix + "7f" : (n + 1000).ToString("x32");
        w.Auctions[0] = rows[0] with { SellerId = 0, AuctioneerId = Uuid(11) };   // resolvable
        w.Auctions[1] = rows[1] with { SellerId = 0, AuctioneerId = Uuid(0) };    // anonymised
        w.Auctions[2] = rows[2] with { SellerId = 0, AuctioneerId = Uuid(13) };   // no Players row
        w.Players[Uuid(11)] = 4711;
        w.Players[Uuid(0)] = 99;

        await w.Run();

        Assert.That(w.Sellers.Select(s => (s.seller, s.uid)), Is.EqualTo(new[] { (4711u, rows[0].UId) }));
        Assert.That(w.Order.Where(o => !o.StartsWith("marker")), Is.EqualTo(new[] { "sellers:1", "delete" }));
        Assert.That(w.Auctions, Is.Empty);
    }

    // ------------------------------------------------------------ seller pass over the archived parts (one decode: verification + seller rows)

    private static string PassUuid(int n) => (n + 5000).ToString("x32");

    private static SaveAuction PassAuction(long uid, int day, int seller) => new()
    {
        Uuid = uid.ToString("x32"),
        UId = uid,
        Tag = ItemTag,
        End = new DateTime(2023, 1, day, 12, 0, 0, DateTimeKind.Utc),
        Start = new DateTime(2023, 1, day, 0, 0, 0, DateTimeKind.Utc),
        AuctioneerId = PassUuid(seller),
        NbtData = new NbtData { data = new byte[] { 10, 0, (byte)uid } },
        Bids = new List<SaveBids>(),
        Enchantments = new List<Enchantment>(),
        NBTLookup = new List<NBTLookup>(),
    };

    /// <summary>Writes the parts (part 0 phase 1, further parts Scylla) to the store and tag index, registers them as the month's parts; returns the part hashes.</summary>
    private static List<string> ArchiveParts(TestableRetentionWorker w, int month, params SaveAuction[][] parts)
    {
        var hashes = new List<string>();
        for (var i = 0; i < parts.Length; i++)
        {
            var block = ArchivedAuctionBlock.FromAuctions(ItemTag, month, i, i == 0 ? ArchivedAuctionBlock.SourceMariaDb : ArchivedAuctionBlock.SourceScylla, parts[i]);
            w.Store.WriteBlockAsync(block).Wait();
            hashes.Add(block.Sha256());
        }
        w.Store.WriteIndexAsync(new ArchivedTagIndex { Tag = ItemTag }.Upsert(new ArchivedMonthEntry
        {
            Month = month, Parts = parts.Length, Rows = parts.Sum(p => p.Length), PartSha256 = hashes, Source = ArchivedAuctionBlock.SourceMerged,
        })).Wait();
        w.MonthParts[month] = new List<RetentionTagParts> { new(ItemTag, parts.Length, ArchivedAuctionBlock.CombinedSha256(hashes)) };
        w.Store.Events.Clear();
        return hashes;
    }

    [Test]
    public async Task ReadsEveryPartOnceAndWritesSellerRowsOfAllPartsBeforeTheFirstDelete()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        // part 0 = the MariaDB auctions, part 1 = Scylla-only auctions that have no MariaDB row
        ArchiveParts(w, OldMonth, new[] { PassAuction(1, 1, 1), PassAuction(2, 2, 2) }, new[] { PassAuction(3, 3, 3), PassAuction(4, 4, 99) });
        foreach (var n in new[] { 1, 2, 3 })
            w.Players[PassUuid(n)] = 100 + n;   // 99 has no Players row

        await w.Run();

        Assert.That(w.Store.Events.Where(e => e.StartsWith("read:")), Is.EqualTo(new[] { $"read:{ItemTag}:{OldMonth}:0:uncached", $"read:{ItemTag}:{OldMonth}:1:uncached" }),
            "each part is decoded exactly once, for the verification and the seller rows");
        Assert.That(w.Sellers.Select(x => x.uid), Is.SupersetOf(new[] { 1L, 2L, 3L }).And.No.Member(4L), "includes the Scylla-only part (uid 3); the unresolved seller 99 (uid 4) is skipped");
        Assert.That(w.Sellers.Select(x => x.seller), Does.Contain(103u));
        var firstDelete = w.Order.IndexOf("delete");
        Assert.That(w.Order.Take(firstDelete).Count(o => o == "sellers:2" || o == "sellers:1"), Is.GreaterThanOrEqualTo(2), "both parts' seller rows are written first");
        Assert.That(w.Order.IndexOf("marker:" + OldMonth), Is.LessThan(firstDelete).And.GreaterThan(-1), "marker after the pass, before any delete");
        Assert.That(w.SellerPassDone, Does.Contain(OldMonth));
    }

    [Test]
    public async Task ScyllaOnlyMonthWithoutMariaDbRowsIsStillPassedOnce()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 0);
        ArchiveParts(w, OldMonth, new[] { PassAuction(7, 5, 7) });
        w.Players[PassUuid(7)] = 107;

        await w.Run();

        Assert.That(w.Sellers.Select(x => (x.seller, x.uid)), Is.EqualTo(new[] { (107u, 7L) }));
        Assert.That(w.SellerPassDone, Does.Contain(OldMonth));
        Assert.That(w.Recorded.Single(), Is.EqualTo((OldMonth, 0L, true)), "the month is completed although it had no MariaDB rows");
        w.Store.Events.Clear();
        await w.Run();
        Assert.That(w.Store.Events.Any(e => e.StartsWith("read:")), Is.False, "a finished month is not read again");
    }

    /// <summary>Key shared with the archive job, import and retention batch tests: seller 123, auction 77, end 2020-09-02 12:00 UTC.</summary>
    [Test]
    public async Task SellerKeyOfTheDecodedPartIsIdenticalToTheOtherWriters()
    {
        var w = Worker();
        var auction = PassAuction(77, 2, 1);
        auction.End = new DateTime(2020, 9, 2, 12, 0, 0, DateTimeKind.Utc);
        auction.Start = new DateTime(2020, 9, 2, 0, 0, 0, DateTimeKind.Utc);
        w.Players[auction.AuctioneerId] = 123;
        var block = ArchivedAuctionBlock.FromAuctions(ItemTag, 202009, 0, ArchivedAuctionBlock.SourceScylla, new[] { auction });

        var row = (await w.BuildSellerRowsOfBlockAsync(block, CancellationToken.None)).Single();

        Assert.That((row.SellerId, row.End, row.Uid), Is.EqualTo((123u, 1599048000u, 77L)));
    }

    [Test]
    public async Task MonthWithMarkerIsNotReadAgain_ButStillDeleted()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        ArchiveParts(w, OldMonth, new[] { PassAuction(1, 1, 1) });
        w.SellerPassDone.Add(OldMonth);   // the pass already ran for this month

        Assert.That(await w.Run(), Is.EqualTo(1));

        Assert.That(w.Store.Events.Any(e => e.StartsWith("read:")), Is.False);
        Assert.That(w.Auctions, Is.Empty);
    }

    [TestCase("hash")]
    [TestCase("missing part")]
    [TestCase("combined")]
    public async Task AbortsWithoutDeletingWhenAPartDoesNotVerify(string defect)
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        var hashes = ArchiveParts(w, OldMonth, new[] { PassAuction(1, 1, 1) }, new[] { PassAuction(3, 3, 3) });
        w.Players[PassUuid(1)] = 101;
        switch (defect)
        {
            case "hash":
                var index = await w.Store.ReadIndexAsync(ItemTag);
                index.Find(OldMonth).PartSha256[1] = new string('a', 64);
                await w.Store.WriteIndexAsync(index);
                break;
            case "missing part":
                w.Store.Objects.Remove(AuctionBlobKeys.Block(ItemTag, OldMonth, 1));
                break;
            case "combined":
                w.MonthParts[OldMonth] = new List<RetentionTagParts> { new(ItemTag, 2, new string('b', 64)) };
                break;
        }

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty, "nothing deleted");
        Assert.That(w.Auctions, Has.Count.EqualTo(2));
        Assert.That(w.SellerPassDone, Is.Empty, "no marker");
        Assert.That(w.MonthRows[OldMonth].All(r => r.DeletedAt == null), Is.True);
    }

    [Test]
    public async Task SellerWriteFailureInThePassBlocksTheMonth()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        ArchiveParts(w, OldMonth, new[] { PassAuction(1, 1, 1) });
        w.Players[PassUuid(1)] = 101;
        w.SellerFailure = new InvalidOperationException("insert failed");

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.SellerPassDone, Is.Empty);
    }

    [Test]
    public async Task DeletesNothingWithoutTheDeleteFlag()
    {
        var w = Worker(("ARCHIVE:DELETE_MARIADB", "false"));
        AddArchivedMonth(w, OldMonth, 3);
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task NeverDeletesAMonthInsideTheWindow()
    {
        var w = Worker(("ARCHIVE:OLDEST_MONTH", "2023-10"));
        AddArchivedMonth(w, 202310, 2); // 2023-10 is the oldest month inside 3 years on 2026-10-21
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
    }

    [TestCase("no sentinel")]
    [TestCase("unverified tag")]
    [TestCase("index rows missing")]
    public async Task NeverDeletesAnIncompletelyArchivedMonth(string defect)
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        var rows = w.MonthRows[OldMonth];
        switch (defect)
        {
            case "no sentinel": rows.RemoveAll(r => r.TagId == 0); break;
            case "unverified tag": rows[1] = rows[1] with { VerifiedAt = null }; break;
            case "index rows missing": rows[1] = rows[1] with { IndexRows = 1 }; break;
        }
        Assert.That(AuctionRetentionWorker.IsFullyArchived(rows), Is.False);
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
    }

    [Test]
    public async Task AbortsWithoutDeletingWhenAUidHasNoLocationRow()
    {
        var w = Worker();
        var rows = AddArchivedMonth(w, OldMonth, 3);
        w.Locations.Remove((ArchiveMonths.ToIndex(OldMonth), rows[1].UId));
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
        Assert.That(w.Recorded.Single().completed, Is.False);
    }

    [Test]
    public async Task AbortsWithoutDeletingWhenATagIsNotVerified()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        var (start, _) = ArchiveMonths.MonthRange(OldMonth);
        var stray = new DeleteCandidate(nextId++, 999, "STRAY", start.AddMinutes(30), null);
        w.Auctions.Add(stray);
        w.Locations.Add((ArchiveMonths.ToIndex(OldMonth), stray.UId));
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
    }

    /// <summary>Regression: a row whose tag has no verified run for the month (its ItemId was never archived) blocks deleting it.</summary>
    [Test]
    public async Task RefusesTheMonthWhenARowBelongsToAnUnverifiedTag()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        var (start, _) = ArchiveMonths.MonthRange(OldMonth);
        w.Auctions.Add(new DeleteCandidate(nextId++, 900_001, "NEVER_ARCHIVED", start.AddDays(3), null));
        w.Locations.Add((ArchiveMonths.ToIndex(OldMonth), 900_001));

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
        Assert.That(w.MonthRows[OldMonth].All(r => r.DeletedAt == null), Is.True);
    }

    [Test]
    public async Task RefusesTheMonthWhenARowHasNoTag()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        var (start, _) = ArchiveMonths.MonthRange(OldMonth);
        w.Auctions.Add(new DeleteCandidate(nextId++, 900_002, null, start.AddDays(3), null));
        w.Locations.Add((ArchiveMonths.ToIndex(OldMonth), 900_002));

        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
    }

    [Test]
    public async Task DeletesChildrenFirstAndKeepsSharedNbtData()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2, sharedNbt: 7);
        var (inWindowStart, _) = ArchiveMonths.MonthRange(202501);
        w.Auctions.Add(new DeleteCandidate(nextId++, 42, ItemTag, inWindowStart, 7)); // same item bytes row, not deleted

        Assert.That(await w.Run(), Is.EqualTo(1));

        var order = w.Executed.Single().Select(s => s.Table + "." + s.Column).ToList();
        Assert.That(order, Is.EqualTo(new[] { "Bids.Uuid", "NBTLookups.AuctionId", "Enchantment.SaveAuctionId", "UuId.SaveAuctionId", "UuId.SaveAuctionId1", "Auctions.Id", "NbtData.Id" }));
        Assert.That(w.NbtData, Does.Contain(7L), "still referenced by an auction of another month");
        Assert.That(w.Auctions.Select(a => a.UId), Is.EqualTo(new[] { 42L }));
        Assert.That(w.Children.Values.All(l => l.Count == 0), Is.True);
        Assert.That(w.Recorded.Single(), Is.EqualTo((OldMonth, 2L, true)));
    }

    [Test]
    public async Task UnsharedNbtDataIsDeletedInBatches()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "2"));
        AddArchivedMonth(w, OldMonth, 5);
        Assert.That(await w.Run(), Is.EqualTo(1));
        Assert.That(w.Executed, Has.Count.EqualTo(3));
        Assert.That(w.Auctions, Is.Empty);
        Assert.That(w.NbtData, Is.Empty);
        Assert.That(w.MonthRows[OldMonth].All(r => r.DeletedAt != null), Is.True);
    }

    [Test]
    public async Task HealthStopHaltsTheBatchLoop()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "2"));
        AddArchivedMonth(w, OldMonth, 5);
        w.Health.Enqueue(new HealthResult(HealthStatus.Ok, "ok"));
        w.Health.Enqueue(new HealthResult(HealthStatus.Exceeded, "binlog too big"));

        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Has.Count.EqualTo(1));
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
        Assert.That(w.Recorded.Single(), Is.EqualTo((OldMonth, 2L, false)), "partial counts recorded, month not marked deleted");
    }

    [Test]
    public async Task UnavailableHealthChecksStopOnlyWhenRequired()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        w.Health.Enqueue(new HealthResult(HealthStatus.Unavailable, "no privilege"));
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);

        var lenient = Worker(("ARCHIVE:REQUIRE_HEALTH_CHECKS", "false"));
        AddArchivedMonth(lenient, OldMonth, 2);
        lenient.Health.Enqueue(new HealthResult(HealthStatus.Unavailable, "no privilege"));
        Assert.That(await lenient.Run(), Is.EqualTo(1));
    }

    [Test]
    public async Task DailyMonthCapIsRespected()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 1);
        AddArchivedMonth(w, OldMonth2, 1);
        Assert.That(await w.Run(), Is.EqualTo(1));
        Assert.That(w.Recorded.Select(r => r.month), Is.EqualTo(new[] { OldMonth }), "oldest month first, one per day");

        var capped = Worker();
        AddArchivedMonth(capped, OldMonth, 1);
        capped.DeletedToday = 1;
        Assert.That(await capped.Run(), Is.EqualTo(0));
        Assert.That(capped.Executed, Is.Empty);
    }

    [Test]
    public void HealthEvaluation()
    {
        var o = new ArchiveOptions { MaxBinlogGb = 100, MaxHistoryListLength = 1000 };
        Assert.That(AuctionRetentionWorker.EvaluateHealth(50_000_000_000, 10, o).Status, Is.EqualTo(HealthStatus.Ok));
        Assert.That(AuctionRetentionWorker.EvaluateHealth(150_000_000_000, 10, o).Status, Is.EqualTo(HealthStatus.Exceeded));
        Assert.That(AuctionRetentionWorker.EvaluateHealth(0, 1001, o).Status, Is.EqualTo(HealthStatus.Exceeded));
        Assert.That(AuctionRetentionWorker.EvaluateHealth(null, 10, o).Status, Is.EqualTo(HealthStatus.Unavailable));
        Assert.That(AuctionRetentionWorker.EvaluateHealth(0, null, o).Status, Is.EqualTo(HealthStatus.Unavailable));
    }

    [Test]
    public void DeleteSqlKeepsReferencedNbtData()
    {
        var statements = AuctionRetentionWorker.BuildDeleteStatements(new[] { new DeleteCandidate(3, 30, "T", Now, 9), new DeleteCandidate(4, 40, "T", Now, 9) });
        Assert.That(statements[0].ToSql(), Is.EqualTo("DELETE FROM `Bids` WHERE `Uuid` IN (3,4)"));
        Assert.That(statements.Last().ToSql(), Is.EqualTo("DELETE FROM `NbtData` WHERE `Id` IN (9) AND NOT EXISTS (SELECT 1 FROM `Auctions` r WHERE r.`NbtDataId` = `NbtData`.`Id`)"));
        Assert.That(AuctionRetentionWorker.BuildDeleteStatements(new[] { new DeleteCandidate(3, 30, "T", Now, null) }).Select(s => s.Table), Does.Not.Contain("NbtData"));
    }

    /// <summary>The raw delete SQL and the archive job's child queries rely on these EF shadow FK columns and table names.</summary>
    [Test]
    public void HypixelContextColumnsUsedByRawSqlExist()
    {
        var options = new DbContextOptionsBuilder<HypixelContext>().UseMySql("server=localhost", new MariaDbServerVersion("10.5.5")).Options;
        using var context = new HypixelContext(options);
        var model = context.Model;
        string Table<T>() => model.FindEntityType(typeof(T)).GetTableName();
        string Fk(string navigation) => model.FindEntityType(typeof(SaveAuction)).FindNavigation(navigation).ForeignKey.Properties.Single().GetColumnName();
        Assert.Multiple(() =>
        {
            Assert.That(Table<SaveAuction>(), Is.EqualTo("Auctions"));
            Assert.That(Table<SaveBids>(), Is.EqualTo("Bids"));
            Assert.That(Table<NBTLookup>(), Is.EqualTo("NBTLookups"));
            Assert.That(Table<Enchantment>(), Is.EqualTo("Enchantment"));
            Assert.That(Table<UuId>(), Is.EqualTo("UuId"));
            Assert.That(Table<NbtData>(), Is.EqualTo("NbtData"));
            Assert.That(Fk(nameof(SaveAuction.Bids)), Is.EqualTo("Uuid"));
            Assert.That(Fk(nameof(SaveAuction.ClaimedBids)), Is.EqualTo("SaveAuctionId"));
            Assert.That(Fk(nameof(SaveAuction.CoopMembers)), Is.EqualTo("SaveAuctionId1"));
            Assert.That(Fk(nameof(SaveAuction.Enchantments)), Is.EqualTo("SaveAuctionId"));
            Assert.That(Fk(nameof(SaveAuction.NBTLookup)), Is.EqualTo("AuctionId"));
            Assert.That(model.FindEntityType(typeof(SaveAuction)).FindNavigation(nameof(SaveAuction.NbtData)).ForeignKey.Properties.Single().GetColumnName(), Is.EqualTo("NbtDataId"));
        });
    }

    [Test]
    public void HistoryLengthSqlUsesShowGlobalStatusWithoutInnodbMetrics()
    {
        var sql = AuctionRetentionWorker.HistoryLengthSql;
        Assert.That(sql, Does.Contain("SHOW GLOBAL STATUS"));
        Assert.That(sql, Does.Contain("Innodb_history_list_length"));
        Assert.That(sql, Does.Not.Contain("INNODB_METRICS"));
        Assert.That(sql, Does.Not.Contain("`STATUS`"));
        Assert.That(sql, Does.Not.Contain("SELECT"));
    }

    [TestCase("1234", 1234L)]
    [TestCase("0", 0L)]
    public void ParseHistoryLengthReadsNumericValue(string value, long expected)
        => Assert.That(AuctionRetentionWorker.ParseHistoryLength(value), Is.EqualTo(expected));

    [Test]
    public void ParseHistoryLengthIgnoresNonNumeric()
    {
        Assert.That(AuctionRetentionWorker.ParseHistoryLength(null), Is.Null);
        Assert.That(AuctionRetentionWorker.ParseHistoryLength("n/a"), Is.Null);
    }
}
