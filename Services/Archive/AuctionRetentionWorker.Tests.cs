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
        /// <summary>Committed batches (month, Auctions rows deleted) and months closed with deleted_at.</summary>
        public readonly List<(int month, long total)> Batches = new();
        public readonly List<int> Closed = new();
        /// <summary>archived_months.deleted_rows per (month, tag id), booked in the same step as the delete like the real transaction; tag id 0 is the sentinel.</summary>
        public readonly Dictionary<(int month, ushort tag), long> DeletedByTag = new();
        /// <summary>Simulates a process crash right after the Nth committed batch (the delete and its booking are already durable).</summary>
        public int? CrashAfterBatch;
        /// <summary>Rows deleted but (wrongly) not booked, to prove the final reconcile catches a bookkeeping drift.</summary>
        public long UnbookedRows;
        public int DeletedToday;
        /// <summary>Seller rows committed so far and the order of seller inserts versus deletes.</summary>
        public readonly HashSet<(uint seller, uint end, long uid)> Sellers = new();
        public readonly List<string> Order = new();
        public Exception SellerFailure;
        public readonly Dictionary<string, int> Players = new();

        public readonly InMemoryAuctionBlobStore Store;
        public readonly Dictionary<int, List<RetentionTagParts>> MonthParts = new();
        public readonly HashSet<int> SellerPassDone = new();

        public TestableRetentionWorker(InMemoryAuctionBlobStore store, IConfiguration config, bool withProbe = true, Microsoft.Extensions.Logging.ILogger<AuctionRetentionWorker> logger = null)
            : base(store, config, logger ?? NullLogger<AuctionRetentionWorker>.Instance, store, withProbe ? store : null) => Store = store;

        protected override Task<IReadOnlyList<RetentionTagParts>> GetMonthPartsAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<RetentionTagParts>>(MonthParts.GetValueOrDefault(month) ?? new List<RetentionTagParts>());
        protected override Task<bool> IsSellerPassDoneAsync(int month, CancellationToken cancellationToken) => Task.FromResult(SellerPassDone.Contains(month));
        protected override Task MarkSellerBackfillDoneAsync(int month, CancellationToken cancellationToken)
        {
            Order.Add("marker:" + month);
            SellerPassDone.Add(month);
            return Task.CompletedTask;
        }

        /// <summary>Replication state the gate reads: lag (null = NULL), thread states, or an unreadable status.</summary>
        public double? Lag = 0;
        public string IoRunning = "Yes";
        public string SqlRunning = "Yes";
        public bool ReplicaUnreadable;
        /// <summary>Makes the replica read throw, or run the real implementation (which needs a configured DBReadOnlyConnection).</summary>
        public Exception ReplicaFailure;
        public bool UseRealReplicaRead;
        public int LagReads;
        protected override Task<ReplicaStatus> ReadReplicaStatusAsync(CancellationToken cancellationToken)
        {
            LagReads++;
            if (ReplicaFailure != null)
                throw ReplicaFailure;
            if (UseRealReplicaRead)
                return base.ReadReplicaStatusAsync(cancellationToken);
            return Task.FromResult(ReplicaUnreadable ? null : new ReplicaStatus(IoRunning, SqlRunning, Lag));
        }

        /// <summary>Rows the pretended primary has on top of the in-memory Auctions (rows the archive does not know), under <see cref="ExtraPrimaryTag"/>.</summary>
        public long ExtraPrimaryRows;
        public string ExtraPrimaryTag = ItemTag;
        public string ReconcileSource = ArchivedAuctionBlock.SourceMariaDb;
        public long DeletedByMonth(int month) => DeletedByTag.Where(kv => kv.Key.month == month && kv.Key.tag != 0).Sum(kv => kv.Value);
        /// <summary>Whole month count queries run against the primary (the final reconcile) and the replica snapshots (the pre-delete gate).</summary>
        public int PrimaryCountQueries;
        public int ReplicaSnapshotReads;
        /// <summary>ItemIds the final reconcile counted, per call.</summary>
        public readonly List<IReadOnlyList<int>> PrimaryCountedItems = new();
        protected override Task<IReadOnlyDictionary<string, long>> CountPrimaryAuctionsByTagAsync(IReadOnlyList<int> itemIds, DateTime monthStart, DateTime monthEnd, CancellationToken cancellationToken)
        {
            PrimaryCountQueries++;
            PrimaryCountedItems.Add(itemIds.ToList());
            return Task.FromResult(CountByTag(monthStart, monthEnd, itemIds));
        }

        /// <summary>Items of the pretended Auctions table that have no row in the in-memory list (their first auction ends after the tested months).</summary>
        public readonly Dictionary<int, DateTime> OtherItems = new() { [999] = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        public int ItemListReads;
        /// <summary>Runs after each ItemId list read, e.g. to insert a row of a new item while the walk runs.</summary>
        public Action<int> AfterItemListRead;
        protected override Task<IReadOnlyDictionary<int, DateTime>> GetPrimaryItemFirstEndsAsync(CancellationToken cancellationToken)
        {
            ItemListReads++;
            var firstEnds = new Dictionary<int, DateTime>(OtherItems);
            foreach (var g in Auctions.GroupBy(a => a.ItemId))
                firstEnds[g.Key] = g.Min(a => a.End);
            AfterItemListRead?.Invoke(ItemListReads);
            return Task.FromResult<IReadOnlyDictionary<int, DateTime>>(firstEnds);
        }

        protected override Task<CountSnapshot> ReadCountSnapshotAsync(int month, DateTime monthStart, DateTime monthEnd, CancellationToken cancellationToken)
        {
            ReplicaSnapshotReads++;
            return Task.FromResult(new CountSnapshot(ReconcileRows(month), CountByTag(monthStart, monthEnd)));
        }

        /// <summary>Notes written to the month sentinels by the abort backoff.</summary>
        protected override Task<bool> SetMonthBackoffAsync(int month, string note, CancellationToken cancellationToken)
        {
            var rows = MonthRows[month];
            var sentinel = rows.FindIndex(r => r.TagId == 0);
            if (rows[sentinel].Note != null && !rows[sentinel].Note.StartsWith(BackoffNotePrefix))
                return Task.FromResult(false);
            rows[sentinel] = rows[sentinel] with { Note = note };
            return Task.FromResult(true);
        }

        private IReadOnlyDictionary<string, long> CountByTag(DateTime monthStart, DateTime monthEnd, IReadOnlyList<int> itemIds = null)
        {
            var counts = Auctions.Where(a => a.End >= monthStart && a.End < monthEnd && (itemIds == null || itemIds.Contains(a.ItemId))).GroupBy(a => a.Tag ?? "").ToDictionary(g => g.Key, g => (long)g.Count());
            if (ExtraPrimaryRows != 0)
                counts[ExtraPrimaryTag] = counts.GetValueOrDefault(ExtraPrimaryTag) + ExtraPrimaryRows;
            return counts.Where(kv => kv.Value != 0).ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        protected override Task<IReadOnlyList<ReconcileTagRow>> GetReconcileRowsAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult(ReconcileRows(month));

        private IReadOnlyList<ReconcileTagRow> ReconcileRows(int month)
        {
            var names = VerifiedTags.GetValueOrDefault(month) ?? new Dictionary<string, ushort>();
            string NameOf(ushort id) => names.Where(kv => kv.Value == id).Select(kv => kv.Key).FirstOrDefault() ?? ItemTag;
            return (MonthRows.GetValueOrDefault(month) ?? new()).Where(r => r.TagId != 0)
                .Select(r => new ReconcileTagRow(NameOf(r.TagId), ReconcileSource, r.Rows, DeletedByTag.GetValueOrDefault((month, r.TagId)))).ToList();
        }

        protected override Task<int> CountMonthsDeletedSinceAsync(DateTime sinceUtc, long minRows, CancellationToken cancellationToken)
        {
            CountedMinRows = minRows;
            return Task.FromResult(DeletedToday);
        }
        public long CountedMinRows;

        protected override Task<IReadOnlyList<ArchivedMonthRowState>> GetArchivedMonthRowsAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<ArchivedMonthRowState>>(MonthRows.GetValueOrDefault(month) ?? new List<ArchivedMonthRowState>());

        protected override Task<IReadOnlyDictionary<string, ushort>> GetVerifiedTagsAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<string, ushort>>(VerifiedTags.GetValueOrDefault(month) ?? new Dictionary<string, ushort>());

        /// <summary>Every delete batch load (item, rows returned), in order.</summary>
        public readonly List<(int itemId, int rows)> Loads = new();
        protected override Task<IReadOnlyList<DeleteCandidate>> LoadDeleteBatchAsync(int itemId, DateTime cursorEnd, int cursorId, DateTime monthEnd, int batchSize, CancellationToken cancellationToken)
        {
            // the keyset semantics of AuctionArchiveJob.PageFilter
            var batch = Auctions.Where(a => a.ItemId == itemId && a.End >= cursorEnd && a.End < monthEnd && (a.End > cursorEnd || a.Id > cursorId))
                .OrderBy(a => a.End).ThenBy(a => a.Id).Take(batchSize).ToList();
            Loads.Add((itemId, batch.Count));
            if (Loads.Count > 10_000)
                throw new InvalidOperationException("delete walk does not terminate");
            return Task.FromResult<IReadOnlyList<DeleteCandidate>>(batch);
        }

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

        protected override Task<Dictionary<string, long>> ExecuteDeleteAsync(IReadOnlyList<DeleteStatement> statements, int month, CancellationToken cancellationToken)
        {
            Order.Add("delete");
            Executed.Add(statements.ToList());
            var counts = new Dictionary<string, long>();
            foreach (var s in statements)
            {
                var affected = Apply(s);
                counts[s.Table] = counts.GetValueOrDefault(s.Table) + affected;
                if (s.TagId != null)
                    Book(month, s.TagId.Value, affected);
            }
            Batches.Add((month, counts.GetValueOrDefault("Auctions")));
            if (CrashAfterBatch == Batches.Count)
                throw new SimulatedCrash();
            return Task.FromResult(counts);
        }

        /// <summary>The committed state of a batch: the rows are gone and deleted_rows grew, there is no state in between.</summary>
        private void Book(int month, ushort tag, long affected)
        {
            var booked = affected - Math.Min(UnbookedRows, affected);
            UnbookedRows -= affected - booked;
            DeletedByTag[(month, tag)] = DeletedByTag.GetValueOrDefault((month, tag)) + booked;
            DeletedByTag[(month, 0)] = DeletedByTag.GetValueOrDefault((month, (ushort)0)) + booked;
        }

        /// <summary>Auction ids a delete leaves in place (a row deleted concurrently or kept for any other reason).</summary>
        public readonly HashSet<long> KeepAuctionIds = new();

        private long Apply(DeleteStatement s)
        {
            if (s.Table == "Auctions")
                return Auctions.RemoveAll(a => s.Ids.Contains(a.Id) && !KeepAuctionIds.Contains(a.Id));
            if (s.Table != "NbtData")
                return Children.TryGetValue((s.Table, s.Column), out var rows) ? rows.RemoveAll(s.Ids.Contains) : 0;
            Assert.That(s.KeepIfReferencedByTable, Is.EqualTo("Auctions"));
            var deletable = s.Ids.Where(id => !Auctions.Any(a => a.NbtDataId == id)).ToList();
            return NbtData.RemoveWhere(deletable.Contains);
        }

        protected override Task<HealthResult> CheckHealthAsync(ArchiveOptions options, CancellationToken cancellationToken)
            => Task.FromResult(Health.Count > 0 ? Health.Dequeue() : new HealthResult(HealthStatus.Ok, "ok"));

        protected override Task MarkMonthDeletedAsync(int month, CancellationToken cancellationToken)
        {
            Closed.Add(month);
            var rows = MonthRows[month];
            for (var i = 0; i < rows.Count; i++)
                rows[i] = rows[i] with { DeletedAt = Now, Note = rows[i].Note != null && rows[i].Note.StartsWith(BackoffNotePrefix) ? null : rows[i].Note };
            return Task.CompletedTask;
        }

        protected override Task LogSpaceHintAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        protected override Task Delay(TimeSpan delay, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<int> Run() => RunOnceAsync(Now, CancellationToken.None);
    }

    private class SimulatedCrash : Exception { }

    private static int nextId = 1;

    private static TestableRetentionWorker Worker(params (string, string)[] config)
    {
        var all = new List<(string, string)> { ("ARCHIVE:DELETE_MARIADB", "true"), ("ARCHIVE:OLDEST_MONTH", "2023-01") };
        all.AddRange(config);
        return new TestableRetentionWorker(new InMemoryAuctionBlobStore(), ArchiveTestConfig.Build(all.ToArray()));
    }

    /// <summary>A receipt without tags: valid for a month whose archive has no tag with parts (the tests that archive parts replace it, see <see cref="ArchiveParts"/>).</summary>
    private static string EmptyReceipt(int month) => new BackupReceipt { Version = 1, Month = ArchiveMonths.Format(month), Snapshot = "auction-archive-20261101T0300Z", CreatedAt = Now }.ToJson();

    /// <summary>Adds an archived (sentinel + verified tag row), fully indexed month with <paramref name="rows"/> auctions and their children; scylla_at and a backup receipt are in place.</summary>
    private static List<DeleteCandidate> AddArchivedMonth(TestableRetentionWorker w, int month, int rows, int? sharedNbt = null)
        => AddArchivedMonth(w, month, sharedNbt, (ItemTag, Tag, rows));

    /// <summary>Like the single tag overload with several tags; their auctions end alternately (hour i of tag k is i * tags + k).</summary>
    private static List<DeleteCandidate> AddArchivedMonth(TestableRetentionWorker w, int month, int? sharedNbt, params (string tag, ushort id, int rows)[] tags)
    {
        var added = tags.SelectMany((t, k) => AddTagAuctions(w, month, sharedNbt, t.tag, t.rows, k, tags.Length)).ToList();
        w.MonthRows[month] = new List<ArchivedMonthRowState> { new(0, (uint)tags.Sum(t => t.rows), (uint)tags.Sum(t => t.rows), Now, Now, null, Now) };
        foreach (var t in tags)
            w.MonthRows[month].Add(new(t.id, (uint)t.rows, (uint)t.rows, Now, Now, null));
        w.VerifiedTags[month] = tags.ToDictionary(t => t.tag, t => t.id);
        w.Store.RawObjects[BackupReceipt.Key(month)] = EmptyReceipt(month);
        return added;
    }

    private static IEnumerable<DeleteCandidate> AddTagAuctions(TestableRetentionWorker w, int month, int? sharedNbt, string tag, int rows, int tagNumber, int tagCount)
    {
        var (start, _) = ArchiveMonths.MonthRange(month);
        var index = ArchiveMonths.ToIndex(month);
        for (var i = 0; i < rows; i++)
        {
            var id = nextId++;
            var nbt = sharedNbt ?? 10_000 + id;
            var row = new DeleteCandidate(id, 500_000 + id, tag, start.AddHours(i * tagCount + tagNumber), nbt, i % 3 == 2 ? 0 : 7_000 + i % 2, (2_000 + id).ToString("x32"));
            w.Auctions.Add(row);
            w.NbtData.Add(nbt);
            w.Locations.Add((index, row.UId));
            AddChildren(w, id);
            yield return row;
        }
    }

    private static void AddChildren(TestableRetentionWorker w, int id)
    {
        foreach (var (table, column) in new[] { ("Bids", "Uuid"), ("NBTLookups", "AuctionId"), ("Enchantment", "SaveAuctionId"), ("UuId", "SaveAuctionId"), ("UuId", "SaveAuctionId1") })
        {
            if (!w.Children.TryGetValue((table, column), out var list))
                w.Children[(table, column)] = list = new List<long>();
            list.Add(id);
        }
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
        Assert.That(w.Batches, Is.Empty);
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
        w.Store.RawObjects[BackupReceipt.Key(month)] = ReceiptFor(w, month);   // the NAS mirror saw exactly these parts
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
        Assert.That(w.Closed, Is.EqualTo(new[] { OldMonth }), "the month is completed although it had no MariaDB rows");
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

    /// <summary>The marker only skips the seller rows; every part is still re-read uncached and hash-checked right before the month's first delete.</summary>
    [Test]
    public async Task MonthWithMarkerIsStillReVerifiedUncached_WithoutWritingSellerRows()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        ArchiveParts(w, OldMonth, new[] { PassAuction(1, 1, 1) }, new[] { PassAuction(3, 3, 3) });
        w.Players[PassUuid(1)] = 101;
        w.SellerPassDone.Add(OldMonth);   // the pass already ran for this month

        Assert.That(await w.Run(), Is.EqualTo(1));

        Assert.That(w.Store.Events.Where(e => e.StartsWith("read:")), Is.EqualTo(new[] { $"read:{ItemTag}:{OldMonth}:0:uncached", $"read:{ItemTag}:{OldMonth}:1:uncached" }));
        Assert.That(w.Order.Where(o => o.StartsWith("sellers:") && o != "sellers:2" && o != "sellers:1"), Is.Empty);
        Assert.That(w.Order.IndexOf("delete"), Is.GreaterThan(-1));
        Assert.That(w.Auctions, Is.Empty);
    }

    [TestCase("hash")]
    [TestCase("missing part")]
    [TestCase("combined")]
    public async Task MonthWithMarkerButAPartThatNoLongerVerifiesIsNotDeleted(string defect)
    {
        var w = Worker(("ARCHIVE:REQUIRE_BACKUP_RECEIPT", "false"));   // off so that the receipt gate does not pre-empt the part verification under test
        AddArchivedMonth(w, OldMonth, 2);
        ArchiveParts(w, OldMonth, new[] { PassAuction(1, 1, 1) }, new[] { PassAuction(3, 3, 3) });
        w.SellerPassDone.Add(OldMonth);
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

        Assert.That(w.Executed, Is.Empty, "nothing deleted although the seller backfill marker exists");
        Assert.That(w.Auctions, Has.Count.EqualTo(2));
    }

    [TestCase("hash")]
    [TestCase("missing part")]
    [TestCase("combined")]
    public async Task AbortsWithoutDeletingWhenAPartDoesNotVerify(string defect)
    {
        var w = Worker(("ARCHIVE:REQUIRE_BACKUP_RECEIPT", "false"));   // off so that the receipt gate does not pre-empt the part verification under test
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
        Assert.That(w.Batches, Is.Empty);
    }

    [Test]
    public async Task AbortsWithoutDeletingWhenATagIsNotVerified()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        w.ExtraPrimaryRows = -1; // the stray row below is not part of the archived count; the count gate must not pre-empt this test
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
        w.ExtraPrimaryRows = -1; // the stray row below is not part of the archived count; the count gate must not pre-empt this test
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
        w.ExtraPrimaryRows = -1; // the stray row below is not part of the archived count; the count gate must not pre-empt this test
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
        Assert.That(w.Batches, Is.EqualTo(new[] { (OldMonth, 2L) }), "the batch is booked as it is deleted");
        Assert.That(w.Closed, Is.EqualTo(new[] { OldMonth }), "then the month is closed");
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
        w.Health.Enqueue(new HealthResult(HealthStatus.Ok, "ok"));   // month start
        w.Health.Enqueue(new HealthResult(HealthStatus.Ok, "ok"));   // first batch
        w.Health.Enqueue(new HealthResult(HealthStatus.Exceeded, "binlog too big"));

        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Has.Count.EqualTo(1));
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
        Assert.That(w.Batches, Is.EqualTo(new[] { (OldMonth, 2L) }), "partial counts booked per batch");
        Assert.That(w.Closed, Is.Empty, "month not marked deleted");
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
        var w = Worker(("ARCHIVE:SMALL_MONTH_ROWS", "1"));
        AddArchivedMonth(w, OldMonth, 1);
        AddArchivedMonth(w, OldMonth2, 1);
        Assert.That(await w.Run(), Is.EqualTo(1));
        Assert.That(w.Closed, Is.EqualTo(new[] { OldMonth }), "oldest month first, one per day");

        var capped = Worker(("ARCHIVE:SMALL_MONTH_ROWS", "1"));
        AddArchivedMonth(capped, OldMonth, 1);
        capped.DeletedToday = 1;
        Assert.That(await capped.Run(), Is.EqualTo(0));
        Assert.That(capped.Executed, Is.Empty);
    }

    [Test]
    public async Task SmallMonthsDoNotCountTowardTheDailyCap()
    {
        var w = Worker(("ARCHIVE:SMALL_MONTH_ROWS", "3"));
        AddArchivedMonth(w, OldMonth, 2);
        AddArchivedMonth(w, OldMonth2, 3);
        Assert.That(await w.Run(), Is.EqualTo(2));
        Assert.That(w.Closed, Is.EqualTo(new[] { OldMonth, OldMonth2 }), "the 2-row month is below the threshold, the 3-row month is the one counted month of the day");
        Assert.That(w.CountedMinRows, Is.EqualTo(3), "earlier months today are counted with the same threshold");
    }

    [Test]
    public void SmallMonthRowsDefaultsTo100kAndEmptyMonthsNeverCount()
    {
        Assert.That(ArchiveOptions.From(ArchiveTestConfig.Build()).SmallMonthRows, Is.EqualTo(100_000));
        var zero = ArchiveOptions.From(ArchiveTestConfig.Build(("ARCHIVE:SMALL_MONTH_ROWS", "0")));
        Assert.That(AuctionRetentionWorker.MinCountedRows(zero), Is.EqualTo(1));
    }

    // ------------------------------------------------------------ phase B gates: replica lag, count reconcile, backup receipt

    private static ReplicaStatus Replica(double? lag, string io = "Yes", string sql = "Yes") => new(io, sql, lag);

    [Test]
    public void ReplicaEvaluation()
    {
        var o = ArchiveOptions.From(ArchiveTestConfig.Build());
        Assert.That(o.MaxReplicaLagSeconds, Is.EqualTo(300));
        Assert.That(o.RequireBackupReceipt, Is.True, "default on");
        Assert.That(o.RequireReplicaHealthy, Is.True, "default on");
        Assert.That(ArchiveOptions.From(ArchiveTestConfig.Build(("ARCHIVE:REQUIRE_REPLICA_HEALTHY", "false"))).RequireReplicaHealthy, Is.False);
        Assert.That(AuctionRetentionWorker.EvaluateReplica(Replica(0), o).Status, Is.EqualTo(HealthStatus.Ok));
        Assert.That(AuctionRetentionWorker.EvaluateReplica(Replica(300), o).Status, Is.EqualTo(HealthStatus.Ok));
        Assert.That(AuctionRetentionWorker.EvaluateReplica(Replica(301), o).Status, Is.EqualTo(HealthStatus.Exceeded));
        Assert.That(AuctionRetentionWorker.EvaluateReplica(Replica(null), o).Status, Is.EqualTo(HealthStatus.Unavailable), "NULL lag");
        Assert.That(AuctionRetentionWorker.EvaluateReplica(null, o).Status, Is.EqualTo(HealthStatus.Unavailable), "unreadable status");
        Assert.That(ArchiveOptions.From(ArchiveTestConfig.Build(("ARCHIVE:MAX_REPLICA_LAG_SECONDS", "5"))).MaxReplicaLagSeconds, Is.EqualTo(5));
        Assert.That(AuctionRetentionWorker.ReplicaStatusSql, Is.EqualTo("SHOW SLAVE STATUS"));
    }

    /// <summary>A stopped IO or SQL thread reports a small or frozen lag at best: both must say Yes.</summary>
    [TestCase("No", "Yes")]
    [TestCase("Yes", "No")]
    [TestCase("Connecting", "Yes")]
    [TestCase(null, "Yes")]
    [TestCase("Yes", null)]
    public void ReplicaWithAStoppedThreadIsNotHealthyEvenWithZeroLag(string io, string sql)
    {
        var result = AuctionRetentionWorker.EvaluateReplica(Replica(0, io, sql), ArchiveOptions.From(ArchiveTestConfig.Build()));
        Assert.That(result.Status, Is.EqualTo(HealthStatus.Unavailable));
        Assert.That(result.Reason, Does.Contain("Slave_"));
    }

    [TestCase("No", "Yes")]
    [TestCase("Yes", "No")]
    public async Task StoppedReplicationThreadStopsDeleting(string io, string sql)
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        w.IoRunning = io;
        w.SqlRunning = sql;

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task ReplicaLagTooHighStopsDeleting()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        w.Lag = 301;

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
        Assert.That(w.MonthRows[OldMonth].All(r => r.DeletedAt == null), Is.True);
    }

    /// <summary>M1: the replica gate does not depend on REQUIRE_HEALTH_CHECKS; only its own option switches it off.</summary>
    [TestCase("unreadable")]
    [TestCase("null lag")]
    [TestCase("too high")]
    public async Task ReplicaGateIgnoresRequireHealthChecks(string defect)
    {
        var lenient = Worker(("ARCHIVE:REQUIRE_HEALTH_CHECKS", "false"));
        AddArchivedMonth(lenient, OldMonth, 3);
        lenient.Lag = defect == "too high" ? 301 : defect == "null lag" ? null : 0;
        lenient.ReplicaUnreadable = defect == "unreadable";

        Assert.That(await lenient.Run(), Is.EqualTo(0));

        Assert.That(lenient.Executed, Is.Empty);
        Assert.That(lenient.Auctions, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task ReplicaGateCanBeSwitchedOffOnlyByItsOwnOption()
    {
        var off = Worker(("ARCHIVE:REQUIRE_REPLICA_HEALTHY", "false"));
        AddArchivedMonth(off, OldMonth, 3);
        off.ReplicaUnreadable = true;
        Assert.That(await off.Run(), Is.EqualTo(1));
        Assert.That(off.LagReads, Is.EqualTo(0), "not even read");

        var on = Worker();
        AddArchivedMonth(on, OldMonth, 3);
        on.ReplicaUnreadable = true;
        Assert.That(await on.Run(), Is.EqualTo(0), "no replica status: fail closed");
    }

    [Test]
    public async Task ReplicaQueryFailureStopsWithTheCauseAndLogsAWarning()
    {
        var log = new ListLogger<AuctionRetentionWorker>();
        var w = new TestableRetentionWorker(new InMemoryAuctionBlobStore(), ArchiveTestConfig.Build(), logger: log);
        w.ReplicaFailure = new InvalidOperationException("Access denied; you need (at least one of) the SUPER, SLAVE MONITOR privilege(s)");

        var reason = await w.OperationalStopReasonAsync(ArchiveOptions.From(ArchiveTestConfig.Build()), default);

        Assert.That(reason, Does.Contain("InvalidOperationException").And.Contain("SLAVE MONITOR privilege(s)"));
        Assert.That(log.Entries.Any(e => e.level == Microsoft.Extensions.Logging.LogLevel.Warning && e.message.Contains("SLAVE MONITOR privilege(s)")), Is.True);
    }

    [Test]
    public async Task ReplicaWithoutStatusRowStopsWithANotAReplicaReason()
    {
        var w = Worker();
        w.ReplicaUnreadable = true;

        var reason = await w.OperationalStopReasonAsync(ArchiveOptions.From(ArchiveTestConfig.Build()), default);

        Assert.That(reason, Is.EqualTo(AuctionRetentionWorker.NoReplicaRowReason));
        Assert.That(reason, Does.Contain("no row").And.Contain("not a replica"));
    }

    [Test]
    public async Task MissingReadConnectionStopsWithANotConfiguredReason()
    {
        var w = Worker();
        w.UseRealReplicaRead = true;

        var reason = await w.OperationalStopReasonAsync(ArchiveOptions.From(ArchiveTestConfig.Build()), default);

        Assert.That(reason, Is.EqualTo(AuctionRetentionWorker.NoReplicaConfiguredReason));
        Assert.That(reason, Does.Contain("no DBReadOnlyConnection configured"));
    }

    /// <summary>Minor 3: the cheap gates run before the primary count, the receipt HEADs and the uncached part re-read.</summary>
    [Test]
    public async Task CheapGatesRunBeforeTheExpensiveMonthGates()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 2);
        ArchiveParts(w, OldMonth, new[] { PassAuction(1, 1, 1) });
        w.Lag = 301;

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Store.Events, Is.Empty, "no part was read");
        Assert.That(w.Order, Is.Empty, "no seller rows");
        Assert.That(w.LagReads, Is.EqualTo(1));
    }

    [Test]
    public async Task LagIsReadBeforeEveryBatch()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "2"));
        AddArchivedMonth(w, OldMonth, 5);
        await w.Run();
        Assert.That(w.LagReads, Is.EqualTo(4), "the month start + right before each of the 3 batches (an empty load needs no gate)");
    }

    [Test]
    public async Task CountMismatchAbortsTheMonth_MoreRowsOnThePrimaryThanArchived()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        w.ExtraPrimaryRows = 1;   // a row ending in the month that phase 1 never archived

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
        Assert.That(await w.CountProblemAsync(OldMonth, ArchiveMonths.MonthRange(OldMonth).start, ArchiveMonths.MonthRange(OldMonth).end, CancellationToken.None),
            Does.Contain("HYPERION").And.Contain("4 auctions").And.Contain("archived 3"));
    }

    [Test]
    public async Task PreDeleteCountReadsTheReplicaSnapshotAndOnlyTheFinalReconcileThePrimary()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);

        Assert.That(await w.Run(), Is.EqualTo(1));

        Assert.That(w.ReplicaSnapshotReads, Is.EqualTo(1), "expectations and counts of the gate come from one replica snapshot");
        Assert.That(w.PrimaryCountQueries, Is.EqualTo(1), "only the final reconcile (empty month) runs on the primary");
    }

    [Test]
    public async Task MissingReceiptStopsBeforeAnyCountQuery()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        w.Store.RawObjects.Remove(BackupReceipt.Key(OldMonth));

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.ReplicaSnapshotReads + w.PrimaryCountQueries, Is.EqualTo(0), "the receipt gate is cheaper than the month long GROUP BY");
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
    }

    [Test]
    public async Task AbortedMonthBacksOffAndIsRetriedAfterTheBackoff()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        w.ExtraPrimaryRows = 1;
        Assert.That(await w.RunOnceAsync(Now, CancellationToken.None), Is.EqualTo(0));
        Assert.That(w.ReplicaSnapshotReads, Is.EqualTo(1));
        Assert.That(w.MonthRows[OldMonth][0].Note, Does.StartWith("retention_backoff_until=2026-10-22T12:00:00Z; "), "persisted on the sentinel");

        w.ExtraPrimaryRows = 0;
        Assert.That(await w.RunOnceAsync(Now.AddHours(23), CancellationToken.None), Is.EqualTo(0), "still backing off");
        Assert.That(w.ReplicaSnapshotReads, Is.EqualTo(1), "no count query within the backoff");

        Assert.That(await w.RunOnceAsync(Now.AddHours(25), CancellationToken.None), Is.EqualTo(1), "retried after the backoff");
        Assert.That(w.Auctions, Is.Empty);
    }

    [Test]
    public async Task BackoffHoursOfZeroRetriesEveryRun()
    {
        var w = Worker(("ARCHIVE:ABORT_BACKOFF_HOURS", "0"));
        AddArchivedMonth(w, OldMonth, 3);
        w.ExtraPrimaryRows = 1;
        await w.Run();
        await w.Run();
        Assert.That(w.ReplicaSnapshotReads, Is.EqualTo(2));
        Assert.That(w.MonthRows[OldMonth][0].Note, Is.Null);
    }

    [Test]
    public void BackoffNoteRoundTripsAndIsBoundedTo255Characters()
    {
        var until = new DateTime(2026, 10, 22, 12, 0, 0, DateTimeKind.Utc);
        var note = AuctionRetentionWorker.BuildBackoffNote(until, new string('x', 500));
        Assert.That(note, Has.Length.EqualTo(255));
        Assert.That(AuctionRetentionWorker.BackoffUntil(note), Is.EqualTo(until));
        Assert.That(AuctionRetentionWorker.BackoffUntil("phase one note"), Is.Null);
        Assert.That(AuctionRetentionWorker.BackoffUntil(null), Is.Null);
    }

    private static HypixelContext MariaDbModel()
        => new(new DbContextOptionsBuilder<HypixelContext>().UseMySql("server=localhost", new MariaDbServerVersion("10.5.5")).Options);

    /// <summary>
    /// Regression: Auctions has no End index in production (only (ItemId, End)), so an End-only filter full-scans ~700 M rows. The count must be one
    /// ItemId equality plus the End range with one GROUP BY, no IN list, no subquery.
    /// </summary>
    [Test]
    public void CountQueryIsOneItemIdWithAnEndRangeAndOneGroupByTag()
    {
        using var db = MariaDbModel();
        var sql = AuctionRetentionWorker.ItemCountByTagQuery(db, 42, new DateTime(2023, 1, 1), new DateTime(2023, 2, 1)).ToQueryString();
        TestContext.Out.WriteLine(sql);

        Assert.That(sql, Does.Contain("FROM `Auctions`").And.Contain("`a`.`ItemId` = @").And.Contain("`a`.`End` >= @").And.Contain("`a`.`End` < @").And.Contain("GROUP BY `a`.`Tag`"));
        Assert.That(sql, Does.Not.Contain(" IN ").And.Not.Contain("JSON").And.Not.Contain("JOIN"));
        Assert.That(System.Text.RegularExpressions.Regex.Matches(sql, "SELECT").Count, Is.EqualTo(1), "no correlated subquery");
    }

    /// <summary>Regression: the delete batch was <c>WHERE End &gt;= cursor ORDER BY End LIMIT n</c> (full scan + filesort per batch on the primary).</summary>
    [Test]
    public void DeleteBatchQueryIsOneItemIdKeysetOrderedByEndThenId()
    {
        using var db = MariaDbModel();
        var sql = AuctionRetentionWorker.DeleteBatchQuery(db, 42, new DateTime(2023, 1, 1), 0, new DateTime(2023, 2, 1), 5000).ToQueryString();
        TestContext.Out.WriteLine(sql);

        Assert.That(sql, Does.Contain("FROM `Auctions`").And.Contain("`a`.`ItemId` = @").And.Contain("`a`.`End` >= @").And.Contain("`a`.`End` < @").And.Contain("`a`.`Id` > @"));
        Assert.That(sql, Does.Contain("ORDER BY `a`.`End`, `a`.`Id`").And.Contain("LIMIT 5000"));
        Assert.That(sql, Does.Not.Contain(" IN ").And.Not.Contain("JSON").And.Not.Contain("JOIN"));
    }

    /// <summary>The ItemId list is shared with the archive job: one GROUP BY ItemId with MIN(End), the loose index scan on (ItemId, End).</summary>
    [Test]
    public void ItemListQueryIsOneGroupByItemIdWithMinEnd()
    {
        using var db = MariaDbModel();
        var sql = AuctionItemFirstEnds.Query(db).ToQueryString();
        TestContext.Out.WriteLine(sql);
        Assert.That(sql, Does.Contain("MIN(`a`.`End`)").And.Contain("GROUP BY `a`.`ItemId`"));
    }

    [Test]
    public void ItemsFirstSeenAtOrAfterTheMonthEndAreSkipped()
    {
        var monthEnd = new DateTime(2023, 2, 1);
        var firstEnds = new Dictionary<int, DateTime> { [7] = new(2020, 1, 1), [3] = new(2023, 1, 31, 23, 59, 59), [9] = monthEnd, [5] = new(2024, 1, 1) };
        Assert.That(AuctionItemFirstEnds.ItemsEndingBefore(firstEnds, monthEnd), Is.EqualTo(new[] { 3, 7 }), "ascending, first End before the month end only");
    }

    [Test]
    public void EmptyItemListFailsClosed()
    {
        Assert.Throws<InvalidOperationException>(() => AuctionItemFirstEnds.ItemsEndingBefore(new Dictionary<int, DateTime>(), new DateTime(2023, 2, 1)));
        Assert.Throws<InvalidOperationException>(() => AuctionItemFirstEnds.ItemsEndingBefore(null, new DateTime(2023, 2, 1)));
    }

    [Test]
    public void ReplicaSnapshotIsReadOnlyAndConsistent()
    {
        Assert.That(AuctionRetentionWorker.SnapshotSql, Is.EqualTo("START TRANSACTION WITH CONSISTENT SNAPSHOT, READ ONLY"));
        Assert.That(AuctionRetentionWorker.SnapshotIsolationSql, Is.EqualTo("SET TRANSACTION ISOLATION LEVEL REPEATABLE READ"), "runs before the snapshot on the same connection");
    }

    [Test]
    public async Task BackoffThatCannotBeStoredBecauseOfAnOperatorNoteWarnsAndKeepsTheNote()
    {
        var log = new ListLogger<AuctionRetentionWorker>();
        var w = new TestableRetentionWorker(new InMemoryAuctionBlobStore(), ArchiveTestConfig.Build(("ARCHIVE:DELETE_MARIADB", "true"), ("ARCHIVE:OLDEST_MONTH", "2023-01")), logger: log);
        AddArchivedMonth(w, OldMonth, 3);
        w.MonthRows[OldMonth][0] = w.MonthRows[OldMonth][0] with { Note = "operator: hold" };
        w.ExtraPrimaryRows = 1;

        await w.Run();

        Assert.That(w.MonthRows[OldMonth][0].Note, Is.EqualTo("operator: hold"));
        Assert.That(log.Entries.Any(e => e.level == Microsoft.Extensions.Logging.LogLevel.Warning && e.message.Contains("could not be stored") && e.message.Contains("operator note")), Is.True);
        Assert.That(log.Entries.Any(e => e.message.Contains("backs off until")), Is.False);
    }

    [Test]
    public async Task DeletedMonthClearsItsBackoffNote()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        w.ExtraPrimaryRows = 1;
        await w.RunOnceAsync(Now, CancellationToken.None);
        Assert.That(w.MonthRows[OldMonth][0].Note, Does.StartWith(AuctionRetentionWorker.BackoffNotePrefix));

        w.ExtraPrimaryRows = 0;
        Assert.That(await w.RunOnceAsync(Now.AddHours(25), CancellationToken.None), Is.EqualTo(1));

        Assert.That(w.MonthRows[OldMonth][0].Note, Is.Null);
    }

    [Test]
    public async Task CountMismatchAbortsTheMonth_FewerRowsOnThePrimaryThanArchived()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        w.ExtraPrimaryRows = -1;
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
    }

    [Test]
    public async Task MergedTagMonthsSubtractTheScyllaOnlyRowsOfTheManifest()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        // 3 MariaDB rows + 2 Scylla-only rows archived in total
        w.MonthRows[OldMonth] = new List<ArchivedMonthRowState> { new(0, 5, 5, Now, Now, null, Now), new(Tag, 5, 5, Now, Now, null) };
        w.ReconcileSource = ArchivedAuctionBlock.SourceMerged;
        await w.Store.WriteMetaAsync(AuctionBlobKeys.ScyllaManifest(OldMonth), new ScyllaMonthManifest { Month = OldMonth, CompletedAt = Now }
            .Upsert(new ScyllaTagImport { Tag = ItemTag, FirstNewPart = 1, NewParts = 1, NewRows = 2, NewPartSha256 = new() { "x" } }));
        var (start, end) = ArchiveMonths.MonthRange(OldMonth);

        Assert.That(await w.CountProblemAsync(OldMonth, start, end, CancellationToken.None), Is.Null);
        w.ExtraPrimaryRows = 2;
        Assert.That(await w.CountProblemAsync(OldMonth, start, end, CancellationToken.None), Is.Not.Null);
    }

    [Test]
    public async Task MergedTagMonthWithoutManifestEntryCanNotBeReconciled()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        w.ReconcileSource = ArchivedAuctionBlock.SourceMerged;
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
    }

    [Test]
    public async Task ScyllaOnlyTagMonthsHaveNoMariaDbRowsToCount()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 0);
        w.MonthRows[OldMonth] = new List<ArchivedMonthRowState> { new(0, 9, 9, Now, Now, null, Now), new(Tag, 9, 9, Now, Now, null) };
        w.ReconcileSource = ArchivedAuctionBlock.SourceScylla;
        await w.Run();
        Assert.That(w.Closed, Is.EqualTo(new[] { OldMonth }), "the month closes: 9 Scylla-only rows, 0 expected on the primary");
    }

    [Test]
    public async Task ResumedMonthExpectsArchivedMinusAlreadyDeleted()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "2"));
        AddArchivedMonth(w, OldMonth, 6);
        w.Health.Enqueue(new HealthResult(HealthStatus.Ok, "ok"));   // month start
        w.Health.Enqueue(new HealthResult(HealthStatus.Ok, "ok"));   // first batch
        w.Health.Enqueue(new HealthResult(HealthStatus.Exceeded, "binlog"));
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Auctions, Has.Count.EqualTo(4), "one batch of 2 deleted, then the health stop");
        Assert.That(w.DeletedByMonth(OldMonth), Is.EqualTo(2));

        Assert.That(await w.Run(), Is.EqualTo(1), "the resumed month reconciles 6 - 2 = 4");
        Assert.That(w.Auctions, Is.Empty);
    }

    /// <summary>M2: the booking is atomic with the delete, so a resumed month matches exactly; any difference aborts.</summary>
    [Test]
    public async Task ResumedMonthMatchesExactlyWithoutTolerance()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "2"));
        AddArchivedMonth(w, OldMonth, 6);
        w.DeletedByTag[(OldMonth, Tag)] = 1;
        var (start, end) = ArchiveMonths.MonthRange(OldMonth);
        // archived 6, 1 booked deleted -> exactly 5 must be left; the primary has 4 (one batch of the booking is missing: rows vanished unbooked)
        w.Auctions.RemoveRange(0, 2);
        Assert.That(await w.CountProblemAsync(OldMonth, start, end, CancellationToken.None), Does.Contain("expected 5"));
        w.Auctions.Add(new DeleteCandidate(nextId++, 1, ItemTag, start.AddDays(1), null));
        Assert.That(await w.CountProblemAsync(OldMonth, start, end, CancellationToken.None), Is.Null, "5 == 6 - 1");
        w.ExtraPrimaryRows = 1;
        Assert.That(await w.CountProblemAsync(OldMonth, start, end, CancellationToken.None), Does.Contain("6 auctions"), "6 > 5");
    }

    /// <summary>M2: a crash right after a committed batch leaves delete and booking in step, the next run finishes the month.</summary>
    [Test]
    public async Task CrashBetweenBatchesResumesExactlyAndBooksEveryRowOnce()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "2"));
        AddArchivedMonth(w, OldMonth, 6);
        w.CrashAfterBatch = 1;

        Assert.ThrowsAsync<SimulatedCrash>(() => w.Run());

        Assert.That(w.Auctions, Has.Count.EqualTo(4), "one batch of 2 committed before the crash");
        Assert.That(w.DeletedByMonth(OldMonth), Is.EqualTo(2), "booked in the same step");
        Assert.That(w.Closed, Is.Empty);
        w.CrashAfterBatch = null;

        Assert.That(await w.Run(), Is.EqualTo(1), "the resumed month reconciles 6 - 2 = 4 exactly");

        Assert.That(w.Auctions, Is.Empty);
        Assert.That(w.DeletedByTag[(OldMonth, Tag)], Is.EqualTo(6));
        Assert.That(w.DeletedByTag[(OldMonth, 0)], Is.EqualTo(6), "sentinel");
        Assert.That(w.Closed, Is.EqualTo(new[] { OldMonth }));
    }

    /// <summary>Minor 5: a month total that matches is not enough, every tag has to match (one extra HYPERION row hidden by one missing TERMINATOR row).</summary>
    [Test]
    public async Task CompensatingPerTagDifferencesAbortTheMonth()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, null, (ItemTag, Tag, 3), ("TERMINATOR", 6, 2));
        var terminator = w.Auctions.First(a => a.Tag == "TERMINATOR");
        w.Auctions.Remove(terminator);
        w.ExtraPrimaryRows = 1;   // same total as archived, wrong split

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty);
        Assert.That(await w.CountProblemAsync(OldMonth, ArchiveMonths.MonthRange(OldMonth).start, ArchiveMonths.MonthRange(OldMonth).end, CancellationToken.None),
            Does.Contain("TERMINATOR").Or.Contain("HYPERION"));
    }

    /// <summary>Minor 5 + test gap: a multi tag month deletes per tag, books per tag (affected rows) and closes only when every tag adds up.</summary>
    [Test]
    public async Task MultiTagMonthBooksEveryTagAndTheSentinel()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "3"));
        AddArchivedMonth(w, OldMonth, null, (ItemTag, Tag, 5), ("TERMINATOR", 6, 3));

        Assert.That(await w.Run(), Is.EqualTo(1));

        Assert.That(w.Auctions, Is.Empty);
        Assert.That(w.DeletedByTag[(OldMonth, Tag)], Is.EqualTo(5));
        Assert.That(w.DeletedByTag[(OldMonth, 6)], Is.EqualTo(3));
        Assert.That(w.DeletedByTag[(OldMonth, 0)], Is.EqualTo(8));
        Assert.That(w.Executed.SelectMany(b => b).Where(st => st.Table == "Auctions").Select(st => st.TagId).Distinct().Order(), Is.EqualTo(new ushort?[] { Tag, 6 }), "one Auctions delete per tag");
        Assert.That(w.Closed, Is.EqualTo(new[] { OldMonth }));
    }

    /// <summary>Minor 5: after the loop the month only closes when the booked rows add up to what phase 1 archived.</summary>
    [Test]
    public async Task MonthIsNotClosedWhenTheBookedRowsDoNotAddUp()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        w.UnbookedRows = 1;   // a bookkeeping bug: one deleted row never reached deleted_rows

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Auctions, Is.Empty, "the rows are gone");
        Assert.That(w.Closed, Is.Empty, "but the month stays open: deleted 2 booked of 3 archived");
        Assert.That(w.MonthRows[OldMonth].All(r => r.DeletedAt == null), Is.True);
    }

    [Test]
    public async Task MonthWithoutScyllaAtWaitsAndNothingIsReadOrWritten()
    {
        var w = Worker(("ARCHIVE:REQUIRE_SCYLLA_IMPORT", "true"));
        AddArchivedMonth(w, OldMonth, 3);
        AddArchivedMonth(w, OldMonth2, 2);
        w.MonthRows[OldMonth][0] = w.MonthRows[OldMonth][0] with { ScyllaAt = null };   // the newer month (scylla_at set) must wait behind the older one
        ArchiveParts(w, OldMonth, new[] { PassAuction(1, 1, 1) });

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.Batches.Count + w.Closed.Count, Is.EqualTo(0), "no state written");
        Assert.That(w.Order, Is.Empty, "no seller rows or markers");
        Assert.That(w.Store.Events, Is.Empty, "no R2 read");
        Assert.That(w.LagReads, Is.EqualTo(0));
        Assert.That(w.Auctions, Has.Count.EqualTo(5));
    }

    [Test]
    public async Task MonthWithScyllaAtProceedsToTheNextGates()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        Assert.That(await w.Run(), Is.EqualTo(1));
        Assert.That(w.Auctions, Is.Empty);

        var blocked = Worker();
        AddArchivedMonth(blocked, OldMonth, 3);
        blocked.Store.RawObjects.Remove(BackupReceipt.Key(OldMonth));
        Assert.That(await blocked.Run(), Is.EqualTo(0), "scylla_at passed, the missing backup receipt gate now blocks");
        Assert.That(blocked.Executed, Is.Empty);
    }

    // --- backup receipt

    private static string ReceiptFor(TestableRetentionWorker w, int month, Action<BackupReceipt> tweak = null)
    {
        var entry = w.Store.ReadIndexAsync(ItemTag).Result.Find(month);
        var receipt = new BackupReceipt
        {
            Version = 1, Month = ArchiveMonths.Format(month), Snapshot = "auction-archive-20261101T0300Z", CreatedAt = Now,
            Tags = new List<BackupReceiptTag>
            {
                new()
                {
                    Tag = ItemTag, Parts = entry.Parts, Rows = entry.Rows, PartSha256 = entry.PartSha256.ToList(),
                    Objects = Enumerable.Range(0, entry.Parts).Select(p => new BackupReceiptObject
                    {
                        Key = AuctionBlobKeys.Block(ItemTag, month, p), ETag = w.Store.GetETagAsync(AuctionBlobKeys.Block(ItemTag, month, p)).Result.Trim('"'), Size = 1,
                    }).ToList(),
                },
            },
        };
        tweak?.Invoke(receipt);
        return receipt.ToJson();
    }

    private static TestableRetentionWorker ReceiptWorker(bool withReceipt = true, Action<BackupReceipt> tweak = null)
    {
        var w = Worker(("ARCHIVE:REQUIRE_BACKUP_RECEIPT", "true"));
        AddArchivedMonth(w, OldMonth, 2);
        ArchiveParts(w, OldMonth, new[] { PassAuction(1, 1, 1) }, new[] { PassAuction(3, 3, 3) });
        w.SellerPassDone.Add(OldMonth);
        if (withReceipt)
            w.Store.RawObjects[BackupReceipt.Key(OldMonth)] = ReceiptFor(w, OldMonth, tweak);
        else
            w.Store.RawObjects.Remove(BackupReceipt.Key(OldMonth));
        return w;
    }

    [Test]
    public async Task DeletesAMonthCoveredByTheBackupReceipt()
    {
        var w = ReceiptWorker();
        Assert.That(await w.Run(), Is.EqualTo(1));
        Assert.That(w.Auctions, Is.Empty);
    }

    [Test]
    public async Task MissingBackupReceiptBlocksTheMonth()
    {
        var w = ReceiptWorker(withReceipt: false);
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.Store.Events.Any(e => e.StartsWith("read:")), Is.False, "the cheap receipt gate runs before the R2 re-verification");
    }

    [Test]
    public async Task ReceiptGateCanBeDisabledExplicitly()
    {
        var w = Worker(("ARCHIVE:REQUIRE_BACKUP_RECEIPT", "false"));
        AddArchivedMonth(w, OldMonth, 2);
        Assert.That(await w.Run(), Is.EqualTo(1));
    }

    [Test]
    public async Task UnstampedOrInvalidReceiptBlocksTheMonth([Values("unstamped", "garbage")] string kind)
    {
        var w = ReceiptWorker(tweak: r => { if (kind == "unstamped") r.Snapshot = ""; });
        if (kind == "garbage")
            w.Store.RawObjects[BackupReceipt.Key(OldMonth)] = "{ nope";
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
    }

    [Test]
    public async Task ChangedETagBlocksTheMonth()
    {
        var w = ReceiptWorker();
        // a privacy erasure rewrote part 1 after the backup
        var block = await w.Store.ReadBlockAsync(ItemTag, OldMonth, 1);
        block.ItemBytes[0] = new byte[] { 1, 2, 3 };
        await w.Store.WriteBlockAsync(block);
        w.Store.Events.Clear();

        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(w.Executed, Is.Empty);
        Assert.That(await w.BackupReceiptProblemAsync(OldMonth, CancellationToken.None), Does.Contain("rewritten since the backup"));
    }

    [Test]
    public async Task PartsAddedSinceTheBackupBlockTheMonth()
    {
        var w = ReceiptWorker();
        // the Scylla import added part 2 to the tag index and archived_months after the receipt was written
        var third = ArchivedAuctionBlock.FromAuctions(ItemTag, OldMonth, 2, ArchivedAuctionBlock.SourceScylla, new[] { PassAuction(9, 9, 9) });
        await w.Store.WriteBlockAsync(third);
        var index = await w.Store.ReadIndexAsync(ItemTag);
        var entry = index.Find(OldMonth);
        entry.Parts = 3;
        entry.Rows += 1;
        entry.PartSha256.Add(third.Sha256());
        await w.Store.WriteIndexAsync(index);
        var hashes = entry.PartSha256.ToList();
        w.MonthParts[OldMonth] = new List<RetentionTagParts> { new(ItemTag, 3, ArchivedAuctionBlock.CombinedSha256(hashes)) };

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty);
        Assert.That(await w.BackupReceiptProblemAsync(OldMonth, CancellationToken.None), Does.Contain("changed since the backup"));
    }

    [Test]
    public async Task ReceiptThatMissesATagBlocksTheMonth()
    {
        var w = ReceiptWorker(tweak: r => r.Tags.Clear());
        Assert.That(await w.Run(), Is.EqualTo(0));
        Assert.That(await w.BackupReceiptProblemAsync(OldMonth, CancellationToken.None), Does.Contain("is not in the backup receipt"));
    }

    [Test]
    public async Task ReceiptForAnotherMonthBlocksTheMonth()
    {
        var w = ReceiptWorker(tweak: r => r.Month = "2023-02");
        Assert.That(await w.Run(), Is.EqualTo(0));
    }

    [Test]
    public async Task NoObjectProbeFailsClosed()
    {
        var w = new TestableRetentionWorker(new InMemoryAuctionBlobStore(), ArchiveTestConfig.Build(("ARCHIVE:DELETE_MARIADB", "true"), ("ARCHIVE:OLDEST_MONTH", "2023-01")), withProbe: false);
        AddArchivedMonth(w, OldMonth, 3);

        Assert.That(await w.Run(), Is.EqualTo(0), "the run aborts the month");

        Assert.That(w.Executed, Is.Empty, "nothing deleted");
        Assert.That(w.Auctions, Has.Count.EqualTo(3));
        Assert.That(w.Closed, Is.Empty);
        Assert.ThrowsAsync<InvalidOperationException>(() => w.BackupReceiptProblemAsync(OldMonth, CancellationToken.None), "and the gate itself throws instead of passing");
    }

    // --- minor 1: the GET of the re-verify must return the receipt's ETag

    /// <summary>The object was replaced between the receipt HEAD and the part GET: the decoded bytes are not the backed up ones.</summary>
    [Test]
    public async Task PartReplacedBetweenTheReceiptCheckAndTheReadBackBlocksTheMonth()
    {
        var w = ReceiptWorker();
        w.Store.GetETagOverride = key => "\"replaced-after-head\"";

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Executed, Is.Empty);
        Assert.That(w.Auctions, Has.Count.EqualTo(2));
        Assert.That(w.Closed, Is.Empty);
    }

    [Test]
    public void PartProblemComparesTheGetETagWithTheReceipt()
    {
        var w = ReceiptWorker();
        var receipt = BackupReceipt.Parse(w.Store.RawObjects[BackupReceipt.Key(OldMonth)]);
        var block = w.Store.ReadBlockAsync(ItemTag, OldMonth, 0).Result;
        var expected = receipt.Tags.Single().Objects[0].ETag;
        var hash = block.Sha256();

        Assert.That(AuctionRetentionWorker.PartProblem(ItemTag, OldMonth, 0, block, "\"" + expected + "\"", hash, receipt), Is.Null, "quotes do not matter");
        Assert.That(AuctionRetentionWorker.PartProblem(ItemTag, OldMonth, 0, block, "\"other\"", hash, receipt), Does.Contain("ETag"));
        Assert.That(AuctionRetentionWorker.PartProblem(ItemTag, OldMonth, 0, block, null, hash, receipt), Does.Contain("ETag"), "no ETag from the GET fails closed");
        Assert.That(AuctionRetentionWorker.PartProblem(ItemTag, OldMonth, 0, block, null, hash, null), Is.Null, "no receipt gate, no ETag check");
        Assert.That(AuctionRetentionWorker.PartProblem(ItemTag, OldMonth, 0, null, expected, hash, receipt), Does.Contain("missing"));
    }

    // --- minor 7: disabled safety gates are loud

    [Test]
    public void DisabledGatesListsEveryGateSwitchedOff()
    {
        Assert.That(AuctionRetentionWorker.DisabledGates(ArchiveOptions.From(ArchiveTestConfig.Build())), Is.Empty);
        var all = ArchiveOptions.From(ArchiveTestConfig.Build(("ARCHIVE:REQUIRE_BACKUP_RECEIPT", "false"), ("ARCHIVE:REQUIRE_SCYLLA_IMPORT", "false"),
            ("ARCHIVE:REQUIRE_REPLICA_HEALTHY", "false"), ("ARCHIVE:REQUIRE_HEALTH_CHECKS", "false")));
        Assert.That(AuctionRetentionWorker.DisabledGates(all), Is.EquivalentTo(new[] { "backup_receipt", "scylla_import", "replica_healthy", "health_checks" }));
    }

    [Test]
    public async Task DisabledGatesWarnAndSetTheGauge()
    {
        var log = new ListLogger<AuctionRetentionWorker>();
        var config = ArchiveTestConfig.Build(("ARCHIVE:DELETE_MARIADB", "true"), ("ARCHIVE:OLDEST_MONTH", "2023-01"), ("ARCHIVE:REQUIRE_REPLICA_HEALTHY", "false"));
        var w = new TestableRetentionWorker(new InMemoryAuctionBlobStore(), config, logger: log);

        await w.Run();

        Assert.That(log.Entries.Any(e => e.level == Microsoft.Extensions.Logging.LogLevel.Warning && e.message.Contains("SAFETY GATES DISABLED") && e.message.Contains("replica_healthy")), Is.True);
        Assert.That(SafetyDisabled("replica_healthy"), Is.EqualTo(1));
        Assert.That(SafetyDisabled("backup_receipt"), Is.EqualTo(0));
        await Worker().Run();
        Assert.That(SafetyDisabled("replica_healthy"), Is.EqualTo(0), "the gauge follows the configuration of the latest run");
    }

    private static double SafetyDisabled(string gate) => AuctionRetentionWorker.SafetyDisabledValue(gate);

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

    // ---- per item tag count ----

    private static MySqlConnector.MySqlException StatementTimeout()
    {
        var ctor = typeof(MySqlConnector.MySqlException).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(MySqlConnector.MySqlErrorCode));
        var args = ctor.GetParameters().Select((p, i) => i == 0 ? (object)(MySqlConnector.MySqlErrorCode)1969 : p.ParameterType == typeof(string) ? "Query execution was interrupted (max_statement_time exceeded)" : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        return (MySqlConnector.MySqlException)ctor.Invoke(args);
    }

    private static readonly DateTime CountStart = new(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CountEnd = CountStart.AddMonths(1);

    [Test]
    public async Task PerItemCountQueriesEveryItemOnceAndMergesTagsCaseInsensitively()
    {
        var calls = new List<(int item, DateTime from, DateTime to)>();
        var map = await AuctionRetentionWorker.CountByTagPerItemAsync(new[] { 1, 2, 3 }, CountStart, CountEnd, (item, from, to, ct) =>
        {
            calls.Add((item, from, to));
            return Task.FromResult(new List<TagCount>
            {
                new() { Tag = item == 2 ? "hyperion" : "HYPERION", Count = 10 },
                new() { Tag = null, Count = item },
            });
        }, NullLogger.Instance, CancellationToken.None);

        Assert.That(calls, Is.EqualTo(new[] { (1, CountStart, CountEnd), (2, CountStart, CountEnd), (3, CountStart, CountEnd) }), "one month long query per item");
        Assert.That(map["Hyperion"], Is.EqualTo(30));
        Assert.That(map[""], Is.EqualTo(6));
        Assert.That(map, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task TimedOutItemSplitsItsEndRangeAndCountsStayComplete()
    {
        var calls = new List<(int item, DateTime from, DateTime to)>();
        var mid = CountStart + (CountEnd - CountStart) / 2;
        var map = await AuctionRetentionWorker.CountByTagPerItemAsync(new[] { 1, 2 }, CountStart, CountEnd, (item, from, to, ct) =>
        {
            calls.Add((item, from, to));
            if (item == 2 && from == CountStart && to == CountEnd)
                throw StatementTimeout();
            return Task.FromResult(new List<TagCount> { new() { Tag = "A", Count = (long)(to - from).TotalHours } });
        }, NullLogger.Instance, CancellationToken.None);

        Assert.That(calls, Is.EqualTo(new[] { (1, CountStart, CountEnd), (2, CountStart, CountEnd), (2, CountStart, mid), (2, mid, CountEnd) }), "only the timed out item is split");
        Assert.That(map["A"], Is.EqualTo(2 * 31 * 24));
    }

    [Test]
    public void ItemStillTimingOutAtTheMinimumRangeRethrows()
    {
        var smallest = TimeSpan.MaxValue;
        Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => AuctionRetentionWorker.CountByTagPerItemAsync(new[] { 1 }, CountStart, CountStart.AddDays(1), (item, from, to, ct) =>
        {
            smallest = TimeSpan.FromTicks(Math.Min(smallest.Ticks, (to - from).Ticks));
            throw StatementTimeout();
        }, NullLogger.Instance, CancellationToken.None));
        Assert.That(smallest, Is.GreaterThanOrEqualTo(AuctionRetentionWorker.MinCountWindow));
    }

    [Test]
    public void NonTimeoutExceptionIsRethrownWithoutSplitting()
    {
        var calls = 0;
        Assert.ThrowsAsync<InvalidOperationException>(() => AuctionRetentionWorker.CountByTagPerItemAsync(new[] { 1, 2 }, CountStart, CountEnd, (item, from, to, ct) =>
        {
            calls++;
            throw new InvalidOperationException("boom");
        }, NullLogger.Instance, CancellationToken.None));
        Assert.That(calls, Is.EqualTo(1));
    }

    [Test]
    public void SnapshotLostIsRethrownWithoutSplittingEvenThoughItWrapsATimeout()
    {
        var calls = 0;
        Assert.ThrowsAsync<AuctionRetentionWorker.SnapshotLostException>(() => AuctionRetentionWorker.CountByTagPerItemAsync(new[] { 1, 2 }, CountStart, CountEnd, (item, from, to, ct) =>
        {
            calls++;
            throw new AuctionRetentionWorker.SnapshotLostException(StatementTimeout());
        }, NullLogger.Instance, CancellationToken.None));
        Assert.That(calls, Is.EqualTo(1), "a lost snapshot must fail the gate, not be split and counted in autocommit");
    }

    // ---- item by item delete walk ----

    /// <summary>Re-assigns the month's in-memory auctions to ItemIds round robin (the tag stays the row's own).</summary>
    private static void SpreadOverItems(TestableRetentionWorker w, params int[] itemIds)
    {
        for (var i = 0; i < w.Auctions.Count; i++)
            w.Auctions[i] = w.Auctions[i] with { ItemId = itemIds[i % itemIds.Length] };
    }

    [Test]
    public async Task DeleteWalkGoesItemByItemWithSeveralBatchesPerItem()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "2"));
        AddArchivedMonth(w, OldMonth, 7);
        SpreadOverItems(w, 10, 20);   // 4 rows of item 10, 3 of item 20
        var (start, _) = ArchiveMonths.MonthRange(OldMonth);
        // item 30 is listed (first End before the month) but has no row in it; item 999 is first seen after the month
        w.OtherItems[30] = start.AddYears(-1);

        Assert.That(await w.Run(), Is.EqualTo(1));

        Assert.That(w.Loads, Is.EqualTo(new[] { (10, 2), (10, 2), (10, 0), (20, 2), (20, 1), (20, 0), (30, 0) }), "ascending ItemIds, item 999 never loaded");
        Assert.That(w.Batches.Select(b => b.total), Is.EqualTo(new[] { 2L, 2L, 2L, 1L }));
        Assert.That(w.Auctions, Is.Empty);
        Assert.That(w.Closed, Is.EqualTo(new[] { OldMonth }));
        Assert.That(w.LagReads, Is.EqualTo(5), "month start + right before each of the 4 batches, none for empty loads");
    }

    [Test]
    public async Task BatchesNeverSpanTwoItems()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "3"));
        AddArchivedMonth(w, OldMonth, 4);
        SpreadOverItems(w, 10, 20);
        var itemOf = w.Auctions.ToDictionary(a => (long)a.Id, a => a.ItemId);

        Assert.That(await w.Run(), Is.EqualTo(1));

        foreach (var batch in w.Executed)
            Assert.That(batch.Single(s => s.Table == "Auctions").Ids.Select(id => itemOf[id]).Distinct().Count(), Is.EqualTo(1));
        Assert.That(w.Executed, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task BatchProblemInALaterItemAbortsTheMonth()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "2"));
        AddArchivedMonth(w, OldMonth, 4);
        SpreadOverItems(w, 10, 20);
        // an item 20 row lost its auction_locations row: the batch safety check refuses it
        var unlocated = w.Auctions.First(a => a.ItemId == 20);
        w.Locations.Remove((ArchiveMonths.ToIndex(OldMonth), unlocated.UId));

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Batches, Is.EqualTo(new[] { (OldMonth, 2L) }), "item 10 was deleted, nothing of item 20's first batch");
        Assert.That(w.Auctions.Count(a => a.ItemId == 20), Is.EqualTo(2));
        Assert.That(w.Closed, Is.Empty);
        Assert.That(w.MonthRows[OldMonth][0].Note, Does.Contain("auction_locations"));
    }

    [Test]
    public async Task HealthStopBetweenItemsStopsTheWalk()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "5"));
        AddArchivedMonth(w, OldMonth, 4);
        SpreadOverItems(w, 10, 20);
        w.Health.Enqueue(new HealthResult(HealthStatus.Ok, "ok"));   // month start
        w.Health.Enqueue(new HealthResult(HealthStatus.Ok, "ok"));   // item 10
        w.Health.Enqueue(new HealthResult(HealthStatus.Exceeded, "binlog"));   // item 20

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Batches, Is.EqualTo(new[] { (OldMonth, 2L) }));
        Assert.That(w.Auctions.Select(a => a.ItemId).Distinct(), Is.EqualTo(new[] { 20 }));
        Assert.That(w.Closed, Is.Empty);
        Assert.That(w.MonthRows[OldMonth][0].Note, Is.Null, "a health stop is no abort, no backoff");
    }

    /// <summary>A row the delete leaves in place is never loaded again (keyset cursor), the walk ends and the final reconcile finds the leftover.</summary>
    [Test]
    public async Task RowsKeptByADeleteDoNotLoopTheWalkAndFailTheFinalReconcile()
    {
        var w = Worker(("ARCHIVE:DELETE_BATCH_SIZE", "2"));
        var rows = AddArchivedMonth(w, OldMonth, 5);
        w.KeepAuctionIds.Add(rows[0].Id);

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.Loads, Is.EqualTo(new[] { (0, 2), (0, 2), (0, 1), (0, 0) }), "the kept row is behind the cursor");
        Assert.That(w.Auctions.Select(a => a.Id), Is.EqualTo(new[] { rows[0].Id }));
        Assert.That(w.Closed, Is.Empty);
        Assert.That(w.MonthRows[OldMonth][0].Note, Does.Contain("final reconcile failed"));
    }

    [Test]
    public async Task FinalReconcileReadsTheItemListAgainAndCatchesAnItemAddedDuringTheWalk()
    {
        var w = Worker();
        AddArchivedMonth(w, OldMonth, 3);
        var (start, _) = ArchiveMonths.MonthRange(OldMonth);
        // right after the walk read its list, a row of a brand new item ends in the month
        w.AfterItemListRead = reads =>
        {
            if (reads == 1)
                w.Auctions.Add(new DeleteCandidate(nextId++, 900_002, ItemTag, start.AddDays(5), null, ItemId: 77));
        };

        Assert.That(await w.Run(), Is.EqualTo(0));

        Assert.That(w.ItemListReads, Is.EqualTo(2), "the walk and the final reconcile each read the list");
        Assert.That(w.PrimaryCountedItems.Single(), Does.Contain(77));
        Assert.That(w.Loads.Select(l => l.itemId), Does.Not.Contain(77), "the walk missed the new item");
        Assert.That(w.Closed, Is.Empty, "the month is not marked deleted");
        Assert.That(w.MonthRows[OldMonth][0].Note, Does.Contain("final reconcile failed"));
    }
}
