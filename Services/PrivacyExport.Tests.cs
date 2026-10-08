using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using NUnit.Framework;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.Indexer;

public class PrivacyExportTests
{
    private const string PlayerUuid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string AuctionUuid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static Dictionary<string, string> Files() => new()
    {
        ["format.json"] = "{\"schema\":\"coflnet-player-export/v1\",\"playerUuid\":\"" + PlayerUuid + "\",\"createdAtUtc\":\"2026-10-02T10:00:00Z\"}",
        ["identity.json"] = "{\"minecraftUuid\":\"" + PlayerUuid + "\"}",
        ["sql/player.jsonl"] = "{\"Id\":42,\"UuId\":\"" + PlayerUuid + "\",\"Name\":\"example\",\"ChangedFlag\":0,\"UpdatedAt\":\"2026-10-02 10:00:00.123456\"}",
        ["sql/auctions.jsonl"] = "{\"Id\":8,\"Uuid\":\"" + AuctionUuid + "\",\"UId\":0,\"ProfileId\":null,\"Start\":\"2026-10-02 09:00:00\",\"End\":\"2026-10-02 10:00:00\",\"HighestBidAmount\":100,\"StartingBid\":10,\"Claimed\":0,\"Bin\":1,\"SellerId\":42,\"AuctioneerId\":\"" + PlayerUuid + "\"}",
        ["sql/bids.jsonl"] = "{\"record\":{\"Id\":9,\"Uuid\":8,\"BidderId\":42,\"Bidder\":\"" + PlayerUuid + "\",\"ProfileId\":null,\"Amount\":100,\"Timestamp\":\"2026-10-02 10:00:00\"},\"auctionUuid\":\"" + AuctionUuid + "\"}",
        ["sql/auction-target-relations.jsonl"] = "{\"relation\":\"coop_member\",\"auctionUuid\":\"" + AuctionUuid + "\",\"playerUuid\":\"" + PlayerUuid + "\"}"
    };
    private static string Archive(Dictionary<string, string> files, string extra = null, bool corruptChecksum = false, bool duplicate = false, bool symlink = false, string prefix = "arbitrary-folder/")
    {
        var path = Path.Combine(Path.GetTempPath(), "privacy-test-" + Guid.NewGuid() + ".zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var manifest = string.Join("\n", files.Select(p => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(p.Value))).ToLowerInvariant() + "  " + p.Key)) + "\n";
        foreach (var file in files) Write(zip, prefix + file.Key, file.Value, symlink && file.Key == "identity.json");
        Write(zip, prefix + "SHA256SUMS", corruptChecksum ? manifest.Replace(manifest[..64], new string('0', 64)) : manifest);
        if (extra != null) Write(zip, extra, "unlisted");
        if (duplicate) Write(zip, prefix + "identity.json", files["identity.json"]);
        return path;
    }
    private static void Write(ZipArchive archive, string name, string contents, bool symlink = false)
    {
        var entry = archive.CreateEntry(name);
        if (symlink) entry.ExternalAttributes = unchecked((int)0xa1ff0000);
        using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
        writer.Write(contents);
    }
    private static void Rejected(string path)
    {
        try { Assert.Throws<InvalidDataException>(() => PrivacyExport.Read(path)); }
        finally { File.Delete(path); }
    }
    [Test]
    public void ReadsRawSqlIdsAndBoundedTargetRelations()
    {
        var path = Archive(Files());
        try
        {
            var plan = PrivacyExport.Read(path);
            Assert.That(plan.Player.Id, Is.EqualTo(42));
            Assert.That(plan.Bids.Single().Record.Id, Is.EqualTo(9));
            Assert.That(plan.Bids.Single().Record.Uuid, Is.EqualTo(8));
            Assert.That(plan.Relations.Single().AuctionId, Is.EqualTo(8));
            Assert.That(plan.Sha256, Has.Length.EqualTo(64));
        }
        finally { File.Delete(path); }
    }
    [TestCase("another-folder/unlisted.json")]
    [TestCase("arbitrary-folder/unlisted.json")]
    [TestCase("arbitrary-folder/../outside")]
    [TestCase("arbitrary-folder/sub\\outside")]
    public void RejectsUnsafeOrUnlistedPaths(string path) => Rejected(Archive(Files(), path));
    [Test]
    public void RejectsOversizedArchiveBeforeDecompression()
    {
        var path = Archive(Files());
        var bytes = File.ReadAllBytes(path);
        var header = Enumerable.Range(0, bytes.Length - 4).First(i => BitConverter.ToUInt32(bytes, i) == 0x02014b50);
        BitConverter.GetBytes(129u * 1024 * 1024).CopyTo(bytes, header + 24);
        File.WriteAllBytes(path, bytes);
        Rejected(path);
    }
    [Test]
    public void RejectsChecksumMismatch() => Rejected(Archive(Files(), corruptChecksum: true));
    [Test]
    public void RejectsDuplicateEntries() => Rejected(Archive(Files(), duplicate: true));
    [Test]
    public void RejectsSymbolicLinkEntries() => Rejected(Archive(Files(), symlink: true));
    [Test]
    public void RejectsIncompleteSnapshot()
    {
        var files = Files(); files["sql/bids.jsonl"] = files["sql/bids.jsonl"].Replace("\"ProfileId\":null,", ""); Rejected(Archive(files));
    }
    [Test]
    public void RejectsMissingUtcZone()
    {
        var files = Files(); files["format.json"] = files["format.json"].Replace("2026-10-02T10:00:00Z", "2026-10-02T10:00:00"); Rejected(Archive(files));
    }
    [Test]
    public void RejectsMissingRequiredFile()
    {
        var files = Files(); files.Remove("sql/bids.jsonl"); Rejected(Archive(files));
    }
    [Test]
    public void RejectsIdentityMismatch()
    {
        var files = Files(); files["identity.json"] = files["identity.json"].Replace(PlayerUuid, AuctionUuid); Rejected(Archive(files));
    }
    [Test]
    public void RejectsCaseConflictingJsonProperties()
    {
        var files = Files(); files["sql/player.jsonl"] = files["sql/player.jsonl"].Replace("\"Id\":42", "\"Id\":42,\"id\":43"); Rejected(Archive(files));
    }
    [Test]
    public void RejectsUnrelatedBidOwnership()
    {
        var files = Files(); files["sql/bids.jsonl"] = files["sql/bids.jsonl"].Replace("\"BidderId\":42", "\"BidderId\":43"); Rejected(Archive(files));
    }
    [Test]
    public void RejectsDuplicateAuctionAndBidIds()
    {
        foreach (var name in new[] { "sql/auctions.jsonl", "sql/bids.jsonl" })
        { var files = Files(); files[name] += "\n" + files[name]; Rejected(Archive(files)); }
    }
    [Test]
    public void RejectsConflictingAuctionForeignKeyMapping()
    {
        var files = Files(); files["sql/bids.jsonl"] = files["sql/bids.jsonl"].Replace("\"Uuid\":8", "\"Uuid\":7"); Rejected(Archive(files));
    }
    [Test]
    public void RejectsRelationOutsideExportScope()
    {
        var files = Files(); files["sql/auction-target-relations.jsonl"] = files["sql/auction-target-relations.jsonl"].Replace(AuctionUuid, "cccccccccccccccccccccccccccccccc"); Rejected(Archive(files));
    }
    [Test]
    public void PreviewIsOfflineAndRemovedCommandsAreRejected()
    {
        var path = Archive(Files());
        try
        {
            Assert.That(PrivacyExport.Run(["privacy", "preview", path]), Is.Zero);
            Assert.That(PrivacyExport.Run(["privacy", "anonymize", path]), Is.EqualTo(1));
            Assert.That(PrivacyExport.Run(["privacy", "opt-out", path]), Is.EqualTo(1));
            Assert.That(PrivacyExport.Run(["privacy", "initialize"]), Is.EqualTo(1));
        }
        finally { File.Delete(path); }
    }
    [TestCase("Uuid")]
    [TestCase("SellerId")]
    [TestCase("AuctioneerId")]
    [TestCase("ProfileId")]
    [TestCase("End")]
    [TestCase("HighestBidAmount")]
    public void RejectsChangedAuctionSnapshotBeforeMutation(string field)
    {
        var snapshot = new SaveAuction { Id = 8, Uuid = AuctionUuid, SellerId = 42, AuctioneerId = PlayerUuid, End = new DateTime(2026, 10, 2), HighestBidAmount = 100 };
        var current = new SaveAuction { Id = 8, Uuid = AuctionUuid, SellerId = 42, AuctioneerId = PlayerUuid, End = snapshot.End, HighestBidAmount = 100 };
        PrivacyExport.ValidateAuction(snapshot, current);
        var property = typeof(SaveAuction).GetProperty(field);
        object changed = property.PropertyType == typeof(string) ? "cccccccccccccccccccccccccccccccc" : property.PropertyType == typeof(DateTime) ? snapshot.End.AddSeconds(1) : property.PropertyType == typeof(long) ? 101L : (object)43;
        property.SetValue(current, changed);
        Assert.Throws<InvalidDataException>(() => PrivacyExport.ValidateAuction(snapshot, current));
        Assert.That(current.AuctioneerId, Is.EqualTo(field == "AuctioneerId" ? changed : PlayerUuid));
    }
    [TestCase("Bidder")]
    [TestCase("BidderId")]
    [TestCase("ProfileId")]
    [TestCase("Amount")]
    [TestCase("Timestamp")]
    public void RejectsChangedBidSnapshotBeforeMutation(string field)
    {
        var snapshot = new PrivacyExport.BidRecord { AuctionUuid = AuctionUuid, Record = new PrivacyExport.BidSnapshot { Id = 9, Uuid = 8, BidderId = 42, Bidder = PlayerUuid, Amount = 100, Timestamp = new DateTime(2026, 10, 2) } };
        var current = new SaveBids { Id = 9, Auction = new SaveAuction { Id = 8, Uuid = AuctionUuid }, BidderId = 42, Bidder = PlayerUuid, Amount = 100, Timestamp = snapshot.Record.Timestamp };
        PrivacyExport.ValidateBid(snapshot, current);
        var property = typeof(SaveBids).GetProperty(field);
        object changed = property.PropertyType == typeof(string) ? "cccccccccccccccccccccccccccccccc" : property.PropertyType == typeof(DateTime) ? current.Timestamp.AddSeconds(1) : property.PropertyType == typeof(long) ? 101L : (object)43;
        property.SetValue(current, changed);
        Assert.Throws<InvalidDataException>(() => PrivacyExport.ValidateBid(snapshot, current));
    }
    [Test]
    public void RejectsChangedBidAuctionMapping()
    {
        var snapshot = new PrivacyExport.BidRecord { AuctionUuid = AuctionUuid, Record = new PrivacyExport.BidSnapshot { Id = 9, Uuid = 8, BidderId = 42, Bidder = PlayerUuid } };
        var current = new SaveBids { Id = 9, Auction = new SaveAuction { Id = 7, Uuid = AuctionUuid }, BidderId = 42, Bidder = PlayerUuid };
        Assert.Throws<InvalidDataException>(() => PrivacyExport.ValidateBid(snapshot, current));
    }

}
