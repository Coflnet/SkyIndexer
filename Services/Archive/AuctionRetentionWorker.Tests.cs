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

        public TestableRetentionWorker(IConfiguration config) : base(config, NullLogger<AuctionRetentionWorker>.Instance) { }

        protected override Task<int> CountMonthsDeletedSinceAsync(DateTime sinceUtc, CancellationToken cancellationToken) => Task.FromResult(DeletedToday);

        protected override Task<IReadOnlyList<ArchivedMonthRowState>> GetArchivedMonthRowsAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchivedMonthRowState>>(MonthRows.GetValueOrDefault(month) ?? new List<ArchivedMonthRowState>());

        protected override Task<IReadOnlyDictionary<string, ushort>> GetVerifiedTagsAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, ushort>>(VerifiedTags.GetValueOrDefault(month) ?? new Dictionary<string, ushort>());

        protected override Task<IReadOnlyList<DeleteCandidate>> LoadDeleteBatchAsync(DateTime cursor, DateTime monthEnd, int batchSize, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<DeleteCandidate>>(Auctions.Where(a => a.End >= cursor && a.End < monthEnd).OrderBy(a => a.End).Take(batchSize).ToList());

        protected override Task<int> CountLocatedUidsAsync(ushort monthIndex, IReadOnlyList<long> uids, CancellationToken cancellationToken)
            => Task.FromResult(uids.Count(u => Locations.Contains((monthIndex, u))));

        protected override Task<Dictionary<string, long>> ExecuteDeleteAsync(IReadOnlyList<DeleteStatement> statements, CancellationToken cancellationToken)
        {
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
        return new TestableRetentionWorker(ArchiveTestConfig.Build(all.ToArray()));
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
            var row = new DeleteCandidate(id, 500_000 + id, ItemTag, start.AddHours(i), nbt);
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
}
