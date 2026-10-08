using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer;

[TestFixture, NonParallelizable]
public class OptOutRequestsTests
{
    private const string Uuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string AuctionUuid = "cccccccccccccccccccccccccccccccc";
    private static string ConnectionString => Environment.GetEnvironmentVariable("SKY_PRIVACY_TEST_DB");
    private DbContextOptions<HypixelContext> options;
    private HypixelContext Db() => new(options);

    [OneTimeSetUp]
    public void InitializeDisposableDatabase()
    {
        var connection = Environment.GetEnvironmentVariable("SKY_PRIVACY_TEST_DB");
        if (string.IsNullOrEmpty(connection)) Assert.Ignore("Set SKY_PRIVACY_TEST_DB to an isolated local privacy_test MariaDB database.");
        var parsed = new MySqlConnectionStringBuilder(connection);
        Assert.That(parsed.Database, Is.EqualTo("privacy_test"), "Tests refuse other databases.");
        Assert.That(parsed.Server, Is.AnyOf("127.0.0.1", "localhost"), "Tests refuse non-local databases.");
        options = new DbContextOptionsBuilder<HypixelContext>().UseMySql(connection, new MariaDbServerVersion("10.11")).Options;
        HypixelContext.SetConfiguration(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["DBConnection"] = connection, ["DBVersion"] = "10.11", ["ActiveAuctionsDBConnection"] = "" }).Build());
        using var db = Db();
        db.Database.EnsureCreated();
    }
    [SetUp]
    public void ClearSyntheticRows()
    {
        // the in-memory set is static and shared between tests
        PlayerOptOut.ResetMemory();
        if (options == null) return;
        using var db = Db();
        db.RemoveRange(db.AgreementAcceptances);
        db.RemoveRange(db.Users);
        db.RemoveRange(db.Set<UuId>());
        db.RemoveRange(db.Bids);
        db.RemoveRange(db.Enchantment);
        db.RemoveRange(db.Auctions);
        db.RemoveRange(db.Players);
        db.SaveChanges();
        // same DDL and seed as the core migration
        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS PlayerOptOutRequests");
        db.Database.ExecuteSqlRaw(PlayerOptOut.CreateTableSql);
        db.Database.ExecuteSqlRaw(PlayerOptOut.SeedLegacySql);
        PlayerOptOut.Reload(db);
    }
    [TearDown]
    public void ResetMemory() => PlayerOptOut.ResetMemory();

    private static List<string> Persisted(HypixelContext db) => db.Database.SqlQueryRaw<string>("SELECT PlayerUuid AS Value FROM PlayerOptOutRequests").ToList();

    private PrivacyExport Seed()
    {
        using var db = Db();
        var player = new Player { UuId = Uuid, Id = 42, Name = "synthetic", UpdatedAt = new DateTime(2026, 10, 2), HitCount = 0 };
        var auction = new SaveAuction { Uuid = AuctionUuid, UId = 123, SellerId = 42, AuctioneerId = Uuid, ProfileId = Other, Start = new DateTime(2026, 10, 1), End = new DateTime(2026, 10, 2), StartingBid = 10, HighestBidAmount = 100, Bids = [], CoopMembers = [new UuId(Uuid), new UuId(Other)], ClaimedBids = [] };
        var bid = new SaveBids { Auction = auction, BidderId = 42, Bidder = Uuid, ProfileId = Other, Amount = 100, Timestamp = new DateTime(2026, 10, 2) };
        auction.Bids.Add(bid);
        auction.Bids.Add(new SaveBids { Auction = auction, BidderId = 43, Bidder = Other, ProfileId = Other, Amount = 90, Timestamp = bid.Timestamp });
        db.Players.Add(player);
        db.Players.Add(new Player { UuId = Other, Id = 43, Name = "bystander", UpdatedAt = new DateTime(2026, 10, 2) });
        db.Auctions.Add(auction);
        db.SaveChanges();
        return new PrivacyExport
        {
            Uuid = Uuid, Sha256 = new string('d', 64), Player = player,
            Auctions = [new SaveAuction { Id = auction.Id, Uuid = AuctionUuid, UId = 123, SellerId = 42, AuctioneerId = Uuid, ProfileId = Other, Start = auction.Start, End = auction.End, StartingBid = 10, HighestBidAmount = 100 }],
            Bids = [new PrivacyExport.BidRecord { AuctionUuid = AuctionUuid, Record = new PrivacyExport.BidSnapshot { Id = bid.Id, Uuid = auction.Id, BidderId = 42, Bidder = Uuid, ProfileId = Other, Amount = 100, Timestamp = bid.Timestamp } }],
            Relations = [new PrivacyExport.Relation { RelationType = "coop_member", AuctionId = auction.Id, AuctionUuid = AuctionUuid, PlayerUuid = Uuid }]
        };
    }
    private void OptOut() { using var db = Db(); PlayerOptOut.Add(db, Uuid); }

    [Test]
    public void MigrationSqlIsIdempotentSeedsLegacyAndLoadFillsMemory()
    {
        using var db = Db();
        db.Database.ExecuteSqlRaw("DROP TABLE PlayerOptOutRequests");
        PlayerOptOut.ResetMemory();
        for (int i = 0; i < 2; i++)
        {
            db.Database.ExecuteSqlRaw(PlayerOptOut.CreateTableSql);
            db.Database.ExecuteSqlRaw(PlayerOptOut.SeedLegacySql);
        }
        Assert.That(Persisted(db), Is.EquivalentTo(PermanentAnonymization.LegacyPlayerUuids));
        db.Database.ExecuteSqlRaw("INSERT INTO PlayerOptOutRequests (PlayerUuid, RequestedAtUtc) VALUES ({0}, UTC_TIMESTAMP(6))", Other);
        PlayerOptOut.Load(ConnectionString); // picks up existing rows
        Assert.That(Persisted(db), Has.Count.EqualTo(3));
        Assert.That(PermanentAnonymization.IsProtectedPlayer(Other), Is.True);
        Assert.That(PermanentAnonymization.PlayerUuids, Is.EquivalentTo(PermanentAnonymization.LegacyPlayerUuids.Append(Other)));
    }
    [Test]
    public void AddPersistsRefreshesMemoryAndRejectsNonCanonicalUuids()
    {
        using var db = Db();
        Assert.That(PermanentAnonymization.IsProtectedPlayer(Uuid), Is.False);
        PlayerOptOut.Add(db, Uuid);
        PlayerOptOut.Add(db, Uuid);
        Assert.That(PermanentAnonymization.IsProtectedPlayer(Uuid), Is.True);
        Assert.That(Persisted(db).Count(u => u == Uuid), Is.EqualTo(1));
        foreach (var bad in new[] { "bad", Uuid.ToUpperInvariant(), "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", null })
            Assert.Throws<ArgumentException>(() => PlayerOptOut.Add(db, bad));
        Assert.That(Persisted(db), Has.Count.EqualTo(3));
    }
    [Test]
    public void UuidAddedDirectlyInSqlIsOnlySeenAfterLoad()
    {
        using var db = Db();
        db.Database.ExecuteSqlRaw("INSERT INTO PlayerOptOutRequests (PlayerUuid, RequestedAtUtc) VALUES ({0}, UTC_TIMESTAMP(6))", Uuid);
        Assert.That(PermanentAnonymization.IsProtectedPlayer(Uuid), Is.False, "checks must not query the database");
        PlayerOptOut.Load(ConnectionString);
        Assert.That(PermanentAnonymization.IsProtectedPlayer(Uuid), Is.True);
    }
    [Test]
    public void PlayerUuidsWorksInEfQueries()
    {
        Seed();
        OptOut();
        using var db = Db();
        var names = db.Players.Where(p => !PermanentAnonymization.PlayerUuids.Contains(p.UuId)).Select(p => p.Name).ToList();
        Assert.That(names, Is.EqualTo(new[] { "bystander" }));
    }
    [Test]
    public async Task IngestionMasksOptedOutPlayer()
    {
        Seed();
        OptOut();
        var indexer = new Indexer(null, new ConcurrentQueue<AuctionResult>(), null, null, null, new ActiveAuctionIndexService(null));
        await indexer.ToDb([new SaveAuction
        {
            Uuid = AuctionUuid, UId = 123, AuctioneerId = Uuid, SellerId = 42, ProfileId = Other,
            End = DateTime.UtcNow.AddHours(1), HighestBidAmount = 110,
            Bids = [new SaveBids { Bidder = Uuid, BidderId = 42, ProfileId = Other, Amount = 110, Timestamp = DateTime.UtcNow }]
        }]);
        using var fresh = Db();
        var newBid = fresh.Bids.Single(b => b.Amount == 110);
        Assert.That(newBid.Bidder, Is.Not.EqualTo(Uuid));
        Assert.That(newBid.BidderId, Is.Zero);
        Assert.That(newBid.ProfileId, Is.Null);
        Assert.That(fresh.Bids.Single(b => b.BidderId == 43).Bidder, Is.EqualTo(Other));
        // existing rows of a loaded auction are scrubbed at read time
        Assert.That(fresh.Bids.Count(b => b.BidderId == 42 || b.Bidder == Uuid), Is.Zero);
        Assert.That(fresh.Bids.Single(b => b.Amount == 100).BidderId, Is.Zero);
    }
    [Test]
    public async Task RepeatedAnonymousBidIsNotInsertedAgain()
    {
        Seed();
        OptOut();
        var indexer = new Indexer(null, new ConcurrentQueue<AuctionResult>(), null, null, null, new ActiveAuctionIndexService(null));
        var timestamp = new DateTime(2026, 10, 2, 10, 0, 0);
        SaveAuction Incoming(DateTime time) => new()
        {
            Uuid = AuctionUuid, UId = 123, AuctioneerId = Uuid, SellerId = 42,
            End = DateTime.UtcNow.AddHours(1), HighestBidAmount = 110,
            Bids = [new SaveBids { Bidder = Uuid, BidderId = 42, Amount = 110, Timestamp = time }]
        };
        await indexer.ToDb([Incoming(timestamp)]);
        await indexer.ToDb([Incoming(timestamp)]);
        using (var db = Db()) Assert.That(db.Bids.Count(b => b.Amount == 110), Is.EqualTo(1));
        await indexer.ToDb([Incoming(timestamp.AddMilliseconds(1))]);
        using var fresh = Db();
        Assert.That(fresh.Bids.Count(b => b.Amount == 110), Is.EqualTo(2));
    }

    [Test]
    public void EraseAndCheckRequireOptOut()
    {
        var plan = Seed();
        foreach (var apply in new[] { false, true })
        {
            using var db = Db();
            var error = Assert.Throws<InvalidDataException>(() => plan.Execute(db, apply));
            Assert.That(error.Message, Does.Contain("optout"));
        }
        using var fresh = Db();
        Assert.That(fresh.Auctions.Single().AuctioneerId, Is.EqualTo(Uuid));
        Assert.That(fresh.Players.Count(), Is.EqualTo(2));
    }
    [Test]
    public void CheckDoesNotWrite()
    {
        var plan = Seed();
        OptOut();
        using (var db = Db()) plan.Execute(db, false);
        using var fresh = Db();
        Assert.That(fresh.Auctions.Single().AuctioneerId, Is.EqualTo(Uuid));
        Assert.That(fresh.Players.Count(), Is.EqualTo(2));
    }
    [Test]
    public void ChangedSnapshotAndNewTargetRowsRejectBeforeAnyWrite()
    {
        var plan = Seed();
        OptOut();
        using (var db = Db()) { db.Bids.Single(b => b.BidderId == 42).Amount++; db.SaveChanges(); }
        using (var db = Db()) Assert.Throws<InvalidDataException>(() => plan.Execute(db, true));
        using (var db = Db())
        {
            db.Bids.Single(b => b.BidderId == 42).Amount--;
            db.Bids.Add(new SaveBids { Auction = db.Auctions.Single(), Bidder = Uuid, BidderId = 42, Amount = 5 });
            db.SaveChanges();
        }
        using (var db = Db()) Assert.Throws<InvalidDataException>(() => plan.Execute(db, true));
        using var fresh = Db();
        Assert.That(fresh.Auctions.Single().AuctioneerId, Is.EqualTo(Uuid));
        Assert.That(fresh.Bids.Count(b => b.Bidder == Uuid), Is.EqualTo(2));
        Assert.That(fresh.Players.Count(), Is.EqualTo(2));
    }
    [Test]
    public void EraseAnonymizesRemovesRelationsDeletesPlayerAndKeepsOthers()
    {
        var plan = Seed();
        OptOut();
        using (var db = Db()) plan.Execute(db, true);
        using var fresh = Db();
        var auction = fresh.Auctions.Include(a => a.CoopMembers).Single();
        Assert.That(auction.AuctioneerId, Is.Not.EqualTo(Uuid));
        Assert.That(auction.SellerId, Is.Zero);
        Assert.That(auction.HighestBidAmount, Is.EqualTo(100));
        Assert.That(auction.CoopMembers.Select(p => p.value), Is.EquivalentTo(new[] { Other }));
        Assert.That(fresh.Set<UuId>().Any(p => p.value == Uuid), Is.False);
        Assert.That(fresh.Bids.Single(b => b.BidderId == 43).Bidder, Is.EqualTo(Other));
        Assert.That(fresh.Bids.Single(b => b.BidderId == 0).Amount, Is.EqualTo(100));
        Assert.That(fresh.Players.Select(p => p.UuId), Is.EqualTo(new[] { Other }));
        Assert.That(auction.ProfileId, Is.Null);
        Assert.That(fresh.Bids.Where(b => b.BidderId == 0).Select(b => b.ProfileId), Is.All.Null);
        Assert.That(Persisted(fresh), Does.Contain(Uuid), "opt-out stays so the player is not re-ingested");
    }
    private void SeedUser(string minecraftUuid)
    {
        using var db = Db();
        var user = new GoogleUser { GoogleId = "g", Email = "synthetic@example.com", MinecraftUuid = minecraftUuid, CreatedAt = new DateTime(2026, 10, 1) };
        db.Users.Add(user);
        db.SaveChanges();
        db.AgreementAcceptances.Add(new AgreementAcceptanceRecord(user.Id, "terms", new TermsAcceptance("1", new string('a', 64), new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), "test")));
        db.SaveChanges();
    }
    [Test]
    public void EraseUnlinksUserButKeepsAccountAndAcceptances()
    {
        var plan = Seed();
        SeedUser(Uuid);
        SeedUser(Other);
        OptOut();
        int unlinked;
        using (var db = Db()) unlinked = plan.Execute(db, true);
        Assert.That(unlinked, Is.EqualTo(1));
        using var fresh = Db();
        Assert.That(fresh.Users.Count(), Is.EqualTo(2));
        Assert.That(fresh.Users.Count(u => u.MinecraftUuid == Uuid), Is.Zero);
        Assert.That(fresh.Users.Count(u => u.MinecraftUuid == Other), Is.EqualTo(1));
        Assert.That(fresh.Users.Count(u => u.Email == "synthetic@example.com"), Is.EqualTo(2));
        Assert.That(fresh.AgreementAcceptances.Count(), Is.EqualTo(2));
    }
    [Test]
    public void CheckDoesNotUnlinkUser()
    {
        var plan = Seed();
        SeedUser(Uuid);
        OptOut();
        int unlinked;
        using (var db = Db()) unlinked = plan.Execute(db, false);
        Assert.That(unlinked, Is.Zero);
        using var fresh = Db();
        Assert.That(fresh.Users.Single().MinecraftUuid, Is.EqualTo(Uuid));
    }
    [Test]
    public void EraseNullsProfileIdEqualToPlayerOnLoadedForeignAuction()
    {
        var plan = Seed();
        OptOut();
        using (var db = Db())
        {
            var foreign = new SaveAuction { Uuid = "dddddddddddddddddddddddddddddddd", UId = 124, SellerId = 43, AuctioneerId = Other, ProfileId = Uuid, Start = new DateTime(2026, 10, 1), End = new DateTime(2026, 10, 2), CoopMembers = [new UuId(Uuid)], ClaimedBids = [] };
            db.Auctions.Add(foreign);
            db.SaveChanges();
            plan.Relations.Add(new PrivacyExport.Relation { RelationType = "coop_member", AuctionId = foreign.Id, AuctionUuid = foreign.Uuid, PlayerUuid = Uuid });
        }
        using (var db = Db()) plan.Execute(db, true);
        using var fresh = Db();
        var left = fresh.Auctions.Single(a => a.UId == 124);
        Assert.That(left.ProfileId, Is.Null);
        Assert.That(left.AuctioneerId, Is.EqualTo(Other));
    }
    [Test]
    public async Task IngestionUpdateDeletesOptedOutCoopMemberRowFromDb()
    {
        Seed();
        OptOut();
        var indexer = new Indexer(null, new ConcurrentQueue<AuctionResult>(), null, null, null, new ActiveAuctionIndexService(null));
        await indexer.ToDb([new SaveAuction { Uuid = AuctionUuid, UId = 123, AuctioneerId = Other, SellerId = 43, End = DateTime.UtcNow.AddHours(1), HighestBidAmount = 100, Bids = [] }]);
        using var fresh = Db();
        Assert.That(fresh.Set<UuId>().Count(p => p.value == Uuid), Is.Zero, "row deleted, not just detached");
        Assert.That(fresh.Set<UuId>().Count(p => p.value == Other), Is.EqualTo(1));
    }
    [Test]
    public void SaveFailureRollsBackEveryMutation()
    {
        var plan = Seed();
        OptOut();
        using (var db = Db())
            db.Database.ExecuteSqlRaw("CREATE TRIGGER privacy_force_bid_failure BEFORE UPDATE ON Bids FOR EACH ROW SIGNAL SQLSTATE '45000' SET MESSAGE_TEXT='synthetic rollback test'");
        try
        {
            using (var db = Db()) Assert.Throws<DbUpdateException>(() => plan.Execute(db, true));
            using var fresh = Db();
            Assert.That(fresh.Auctions.Single().AuctioneerId, Is.EqualTo(Uuid));
            Assert.That(fresh.Bids.Single(b => b.BidderId == 42).Bidder, Is.EqualTo(Uuid));
            Assert.That(fresh.Set<UuId>().Any(p => p.value == Uuid), Is.True);
            Assert.That(fresh.Players.Count(), Is.EqualTo(2));
        }
        finally { using var db = Db(); db.Database.ExecuteSqlRaw("DROP TRIGGER privacy_force_bid_failure"); }
    }
}

[TestFixture, NonParallelizable]
public class PlayerOptOutRefresherTests
{
    private const string Uuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private string connection;
    private HypixelContext Db() => new(new DbContextOptionsBuilder<HypixelContext>().UseMySql(connection, new MariaDbServerVersion("10.11")).Options);

    [OneTimeSetUp]
    public void InitializeDisposableDatabase()
    {
        connection = Environment.GetEnvironmentVariable("SKY_PRIVACY_TEST_DB");
        if (string.IsNullOrEmpty(connection)) { connection = null; return; } // only the DB tests need it
        var parsed = new MySqlConnectionStringBuilder(connection);
        Assert.That(parsed.Database, Is.EqualTo("privacy_test"), "Tests refuse other databases.");
        Assert.That(parsed.Server, Is.AnyOf("127.0.0.1", "localhost"), "Tests refuse non-local databases.");
    }
    [SetUp]
    public void Reset()
    {
        PlayerOptOut.ResetMemory();
        if (connection == null) return;
        using var db = Db();
        db.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS PlayerOptOutRequests");
        db.Database.ExecuteSqlRaw(PlayerOptOut.CreateTableSql);
        db.Database.ExecuteSqlRaw(PlayerOptOut.SeedLegacySql);
    }
    [TearDown]
    public void ResetMemory() => PlayerOptOut.ResetMemory();

    private PlayerOptOutRefresher Create(Func<System.Threading.CancellationToken, Task> load, TimeSpan interval) =>
        new(load, Microsoft.Extensions.Logging.Abstractions.NullLogger<PlayerOptOutRefresher>.Instance, interval, TimeSpan.FromMilliseconds(10));

    private void RequireDb()
    {
        if (connection == null) Assert.Ignore("Set SKY_PRIVACY_TEST_DB to an isolated local privacy_test MariaDB database.");
    }

    private static async Task<bool> WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        return condition();
    }

    [Test]
    public async Task FirstLoadPopulatesBeforeStartReturnsAndLaterRowsWaitForRefresh()
    {
        RequireDb();
        using (var db = Db())
            db.Database.ExecuteSqlRaw("INSERT INTO PlayerOptOutRequests (PlayerUuid, RequestedAtUtc) VALUES ({0}, UTC_TIMESTAMP(6))", Uuid);
        using var refresher = Create(token => PlayerOptOut.LoadAsync(connection, token), TimeSpan.FromMilliseconds(400));
        await refresher.StartAsync(default);
        try
        {
            Assert.That(PlayerOptOut.IsOptedOut(Uuid), Is.True, "first load happens inside StartAsync");
            var later = new string('e', 32);
            using (var db = Db())
                db.Database.ExecuteSqlRaw("INSERT INTO PlayerOptOutRequests (PlayerUuid, RequestedAtUtc) VALUES ({0}, UTC_TIMESTAMP(6))", later);
            Assert.That(PlayerOptOut.IsOptedOut(later), Is.False, "not visible before a refresh");
            Assert.That(await WaitFor(() => PlayerOptOut.IsOptedOut(later)), Is.True, "visible after the periodic refresh");
        }
        finally { await refresher.StopAsync(default); }
    }

    [Test]
    public async Task FailingRefreshKeepsPreviousSnapshotAndKeepsRunning()
    {
        RequireDb();
        int calls = 0;
        bool fail = false;
        using var refresher = Create(async token =>
        {
            Interlocked.Increment(ref calls);
            if (fail) throw new InvalidOperationException("database down");
            await PlayerOptOut.LoadAsync(connection, token);
        }, TimeSpan.FromMilliseconds(50));
        using (var db = Db())
            db.Database.ExecuteSqlRaw("INSERT INTO PlayerOptOutRequests (PlayerUuid, RequestedAtUtc) VALUES ({0}, UTC_TIMESTAMP(6))", Uuid);
        await refresher.StartAsync(default);
        try
        {
            Assert.That(PlayerOptOut.IsOptedOut(Uuid), Is.True);
            fail = true;
            var before = Volatile.Read(ref calls);
            Assert.That(await WaitFor(() => Volatile.Read(ref calls) >= before + 2), Is.True, "keeps retrying after failures");
            Assert.That(PlayerOptOut.IsOptedOut(Uuid), Is.True, "previous snapshot kept");
        }
        finally { await refresher.StopAsync(default); }
    }

    [Test]
    public void StartupFailsClosedAfterRetries()
    {
        int calls = 0;
        using var refresher = Create(_ => { calls++; throw new InvalidOperationException("database down"); }, TimeSpan.FromHours(1));
        Assert.ThrowsAsync<InvalidOperationException>(() => refresher.StartAsync(default));
        Assert.That(calls, Is.GreaterThan(1));
    }

    [Test]
    public async Task StartupRecoversWhenLoadSucceedsOnRetry()
    {
        int calls = 0;
        using var refresher = Create(_ => ++calls < 3 ? throw new InvalidOperationException("not yet") : Task.CompletedTask, TimeSpan.FromHours(1));
        await refresher.StartAsync(default);
        await refresher.StopAsync(default);
        Assert.That(calls, Is.EqualTo(3));
    }

    [Test]
    public void ConnectionStringPrefersReadOnlyKey()
    {
        string Resolve(params (string, string)[] values) => PlayerOptOut.ResolveConnectionString(
            new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Item1, v => v.Item2)).Build());
        Assert.That(Resolve(("DBConnection", "rw"), ("DBReadOnlyConnection", "ro")), Is.EqualTo("ro"));
        Assert.That(Resolve(("DBConnection", "rw")), Is.EqualTo("rw"));
    }
}
