using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Globalization;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Coflnet.Security.OpenBao;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.Indexer;

// SQL-only operation run manually by an internal operator: the archive is a bounded, checksummed prerequisite that is rechecked against the live DB, not an authorization signature.
internal sealed class PrivacyExport
{
    internal string Sha256;
    internal string Uuid;
    internal Player Player;
    internal List<SaveAuction> Auctions;
    internal List<BidRecord> Bids;
    internal List<Relation> Relations;
    internal sealed class BidRecord { public BidSnapshot Record { get; set; } public string AuctionUuid { get; set; } }
    internal sealed class BidSnapshot : SaveBids { public int Uuid { get; set; } }
    internal sealed class Relation { public string RelationType; public int AuctionId; public string AuctionUuid; public string PlayerUuid; }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true, Converters = { new SqlDateTimeConverter(), new SqlBooleanConverter() } };
    private sealed class SqlBooleanConverter : JsonConverter<bool>
    {
        public override bool Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TokenType is JsonTokenType.True or JsonTokenType.False) return reader.GetBoolean();
            if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var value) && value is 0 or 1) return value == 1;
            throw new InvalidDataException("Invalid SQL boolean.");
        }
        public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
    }
    private sealed class SqlDateTimeConverter : JsonConverter<DateTime>
    {
        public override DateTime Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
        {
            if (reader.TryGetDateTime(out var date)) return date;
            if (DateTime.TryParseExact(reader.GetString(), "yyyy-MM-dd HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return date;
            throw new InvalidDataException("Invalid SQL timestamp.");
        }
        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options) => writer.WriteStringValue(value);
    }
    private static void Require(bool valid, string error) { if (!valid) throw new InvalidDataException(error); }
    internal static bool CanonicalUuid(string value) => value != null && value.Length == 32 && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static PrivacyExport Read(string path)
    {
        using var file = File.OpenRead(path);
        var result = new PrivacyExport { Sha256 = Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant() };
        file.Position = 0;
        using var zip = new ZipArchive(file, ZipArchiveMode.Read);
        Require(zip.Entries.Count is > 0 and <= 10000, "Invalid archive entry count.");
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        string prefix = null;
        long total = 0;
        foreach (var entry in zip.Entries)
        {
            var parts = entry.FullName.Split('/');
            Require(parts.Length >= 2 && parts.All(p => p.Length > 0 && p != "." && p != ".." && !p.Contains('\\') && !p.Contains(':')),
                "Archive paths must be files below one folder, without traversal.");
            prefix ??= parts[0];
            Require(prefix == parts[0] && (entry.ExternalAttributes >> 16 & 0xf000) != 0xa000, "Multiple folders or symbolic links are forbidden.");
            total = checked(total + entry.Length);
            Require(total <= 512L * 1024 * 1024 && entry.Length <= 128L * 1024 * 1024, "Archive exceeds uncompressed size limits.");
            var logical = string.Join('/', parts.Skip(1));
            Require(!files.ContainsKey(logical), "Duplicate archive path.");
            using var stream = entry.Open();
            using var data = new MemoryStream();
            var buffer = new byte[81920];
            int count;
            while ((count = stream.Read(buffer)) > 0)
            {
                Require(data.Length + count <= entry.Length, "Archive length mismatch.");
                data.Write(buffer, 0, count);
            }
            Require(data.Length == entry.Length, "Archive length mismatch.");
            files.Add(logical, data.ToArray());
        }
        Require(files.ContainsKey("SHA256SUMS"), "Missing SHA256SUMS.");
        var listed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in Text(files["SHA256SUMS"]).Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            Require(line.Length > 66 && line[64] == ' ' && line[65] == ' ', "Invalid SHA256SUMS line.");
            var name = line[66..].TrimEnd('\r');
            Require(name != "SHA256SUMS" && listed.Add(name) && files.ContainsKey(name), "Invalid checksum file list.");
            Require(string.Equals(line[..64], Convert.ToHexString(SHA256.HashData(files[name])), StringComparison.OrdinalIgnoreCase), "Checksum mismatch.");
        }
        Require(listed.Count == files.Count - 1, "Every archive file must be checksummed exactly once.");
        foreach (var name in new[] { "format.json", "identity.json", "sql/player.jsonl", "sql/auctions.jsonl", "sql/bids.jsonl", "sql/auction-target-relations.jsonl" })
            Require(files.ContainsKey(name), "Missing required file: " + name);
        using var format = Parse(files["format.json"]);
        result.Uuid = format.RootElement.GetProperty("playerUuid").GetString();
        Require(format.RootElement.GetProperty("schema").GetString() == "coflnet-player-export/v1" && CanonicalUuid(result.Uuid), "Unsupported export format or UUID.");
        Require(DateTimeOffset.TryParseExact(format.RootElement.GetProperty("createdAtUtc").GetString(), ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:sszzz", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var created) && created.Offset == TimeSpan.Zero, "Export time must be UTC.");
        using var identity = Parse(files["identity.json"]);
        Require(identity.RootElement.GetProperty("minecraftUuid").GetString() == result.Uuid, "Export identity mismatch.");
        var players = Rows<Player>(files["sql/player.jsonl"]);
        Require(players.Count == 1 && players[0].Id > 0 && players[0].UuId == result.Uuid, "Export must identify exactly one SQL player.");
        result.Player = players[0];
        result.Auctions = Rows<SaveAuction>(files["sql/auctions.jsonl"]);
        result.Bids = Rows<BidRecord>(files["sql/bids.jsonl"]);
        result.Relations = new();
        foreach (var line in Lines(files["sql/auction-target-relations.jsonl"]))
        {
            using var doc = Parse(Encoding.UTF8.GetBytes(line));
            result.Relations.Add(new Relation { RelationType = doc.RootElement.GetProperty("relation").GetString(), AuctionUuid = doc.RootElement.GetProperty("auctionUuid").GetString(), PlayerUuid = doc.RootElement.GetProperty("playerUuid").GetString() });
        }
        Require(result.Auctions.All(a => a.Id > 0 && CanonicalUuid(a.Uuid)) && result.Auctions.Select(a => a.Id).Distinct().Count() == result.Auctions.Count && result.Auctions.Select(a => a.Uuid).Distinct().Count() == result.Auctions.Count, "Invalid or duplicate auction IDs.");
        Require(result.Auctions.All(a => a.SellerId == result.Player.Id && a.AuctioneerId == result.Uuid), "Auction ownership mismatch.");
        Require(result.Bids.All(b => b.Record != null && b.Record.Id > 0 && b.Record.Uuid > 0 && b.Record.BidderId == result.Player.Id && b.Record.Bidder == result.Uuid && CanonicalUuid(b.AuctionUuid)) && result.Bids.Select(b => b.Record.Id).Distinct().Count() == result.Bids.Count, "Invalid, duplicate or unrelated bids.");
        Require(result.Relations.All(r => r.PlayerUuid == result.Uuid && CanonicalUuid(r.AuctionUuid) && r.RelationType is "claimed_bid" or "coop_member") && result.Relations.Select(Key).Distinct().Count() == result.Relations.Count, "Invalid or duplicate target relations.");
        var auctionMap = new Dictionary<string, int>(StringComparer.Ordinal);
        var inverseMap = new Dictionary<int, string>();
        foreach (var pair in result.Auctions.Select(a => (a.Uuid, a.Id)).Concat(result.Bids.Select(b => (b.AuctionUuid, b.Record.Uuid))))
        {
            Require(!auctionMap.TryGetValue(pair.Item1, out var known) || known == pair.Item2, "Conflicting auction mapping.");
            Require(!inverseMap.TryGetValue(pair.Item2, out var knownUuid) || knownUuid == pair.Item1, "Conflicting auction mapping.");
            inverseMap[pair.Item2] = pair.Item1;
            auctionMap[pair.Item1] = pair.Item2;
        }
        foreach (var relation in result.Relations)
        {
            Require(auctionMap.TryGetValue(relation.AuctionUuid, out var id), "Relation outside exported auction/bid scope.");
            relation.AuctionId = id;
        }
        file.Position = 0;
        Require(result.Sha256 == Convert.ToHexString(SHA256.HashData(file)).ToLowerInvariant(), "Archive changed while being verified.");
        return result;
    }
    private static string Text(byte[] data) => new UTF8Encoding(false, true).GetString(data);
    private static IEnumerable<string> Lines(byte[] data) => Text(data).Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(s => !string.IsNullOrWhiteSpace(s));
    private static List<T> Rows<T>(byte[] data) => Lines(data).Select(s =>
    {
        using var doc = Parse(Encoding.UTF8.GetBytes(s));
        var row = doc.RootElement;
        if (typeof(T) == typeof(BidRecord)) row = row.GetProperty("record");
        var required = typeof(T) == typeof(SaveAuction) ? "Id,Uuid,SellerId,AuctioneerId,ProfileId,Start,End,HighestBidAmount,StartingBid,UId"
            : typeof(T) == typeof(BidRecord) ? "Id,Uuid,BidderId,Bidder,ProfileId,Amount,Timestamp" : "Id,UuId,Name,UpdatedAt";
        var names = row.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Require(required.Split(',').All(names.Contains), "Incomplete raw SQL snapshot.");
        return doc.RootElement.Deserialize<T>(JsonOptions);
    }).ToList();
    private static JsonDocument Parse(byte[] data)
    {
        var document = JsonDocument.Parse(data);
        try { ValidateProperties(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
    }
    private static void ValidateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) { Require(names.Add(property.Name), "Duplicate JSON property."); ValidateProperties(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) ValidateProperties(child);
    }
    private static string Key(Relation r) => r.RelationType + ":" + r.AuctionUuid;

    internal static void ValidateAuction(SaveAuction snapshot, SaveAuction current)
    {
        Require(current != null
            && current.Id == snapshot.Id
            && current.Uuid == snapshot.Uuid
            && current.UId == snapshot.UId
            && current.SellerId == snapshot.SellerId
            && current.AuctioneerId == snapshot.AuctioneerId
            && current.ProfileId == snapshot.ProfileId
            && current.Start == snapshot.Start
            && current.End == snapshot.End
            && current.HighestBidAmount == snapshot.HighestBidAmount
            && current.StartingBid == snapshot.StartingBid
            && current.Claimed == snapshot.Claimed
            && current.Count == snapshot.Count
            && current.Tag == snapshot.Tag
            && current.ItemName == snapshot.ItemName
            && current.AnvilUses == snapshot.AnvilUses
            && current.NbtDataId == snapshot.NbtDataId
            && current.ItemCreatedAt == snapshot.ItemCreatedAt
            && current.Reforge == snapshot.Reforge
            && current.Category == snapshot.Category
            && current.Tier == snapshot.Tier
            && current.Bin == snapshot.Bin
            && current.ItemId == snapshot.ItemId, "Auction changed or missing; create a fresh export.");
    }
    internal static void ValidateBid(BidRecord snapshot, SaveBids current)
    {
        var b = snapshot.Record;
        Require(current != null
            && current.Id == b.Id
            && current.Auction?.Id == b.Uuid
            && current.Auction.Uuid == snapshot.AuctionUuid
            && current.BidderId == b.BidderId
            && current.Bidder == b.Bidder
            && current.ProfileId == b.ProfileId
            && current.Amount == b.Amount
            && current.Timestamp == b.Timestamp, "Bid changed or missing; create a fresh export.");
    }
    /// <returns>number of unlinked user accounts (0 for a check)</returns>
    internal int Execute(HypixelContext db, bool apply)
    {
        using var transaction = db.Database.BeginTransaction(IsolationLevel.Serializable);
        Require(PlayerOptOut.Exists(db, Uuid), "Player is not opted out yet. Call POST Player/optout/{uuid} on the running indexer first so ingestion stops writing this player.");
        var player = db.Players.SingleOrDefault(p => p.UuId == Uuid);
        Require(player != null && player.Id == Player.Id, "SQL player changed; create a fresh export.");
        // Broad identity queries are read-only completeness checks. Mutations below use only verified archive IDs.
        var owned = db.Auctions.Where(a => a.SellerId == Player.Id).Select(a => a.Id).ToHashSet();
        Require(owned.SetEquals(Auctions.Where(a => a.SellerId == Player.Id).Select(a => a.Id)), "Seller scope changed; create a fresh export.");
        var targetBids = db.Bids.Where(b => b.BidderId == Player.Id).Select(b => b.Id).ToHashSet();
        Require(targetBids.SetEquals(Bids.Select(b => b.Record.Id)), "Bid scope changed; create a fresh export.");
        var ids = Auctions.Select(a => a.Id).ToArray();
        var auctions = db.Auctions.Where(a => ids.Contains(a.Id)).ToDictionary(a => a.Id);
        foreach (var snapshot in Auctions)
        {
            auctions.TryGetValue(snapshot.Id, out var current);
            ValidateAuction(snapshot, current);
        }
        var bidIds = Bids.Select(b => b.Record.Id).ToArray();
        var bids = db.Bids.Where(b => bidIds.Contains(b.Id)).Include(b => b.Auction).ToDictionary(b => b.Id);
        foreach (var snapshot in Bids)
        {
            bids.TryGetValue(snapshot.Record.Id, out var current);
            ValidateBid(snapshot, current);
        }
        var relationIds = Relations.Select(r => r.AuctionId).Concat(ids).Concat(bids.Values.Select(b => b.Auction.Id)).Distinct().ToArray();
        var related = db.Auctions.Where(a => relationIds.Contains(a.Id)).Include(a => a.CoopMembers).Include(a => a.ClaimedBids).ToList();
        foreach (var relation in Relations)
            Require(related.Any(a => a.Id == relation.AuctionId && a.Uuid == relation.AuctionUuid), "Relation auction mapping changed or missing.");
        var relationKeys = related.SelectMany(a => a.CoopMembers.Where(p => p.value == Uuid).Select(p => "coop_member:" + a.Uuid)
            .Concat(a.ClaimedBids.Where(p => p.value == Uuid).Select(p => "claimed_bid:" + a.Uuid))).ToList();
        Require(relationKeys.Count == relationKeys.Distinct().Count() && relationKeys.ToHashSet().SetEquals(Relations.Select(Key)), "Relation scope changed; create a fresh export.");
        var relationRows = Relations.Select(relation =>
        {
            var auction = related.Single(a => a.Id == relation.AuctionId);
            var row = (relation.RelationType == "coop_member" ? auction.CoopMembers : auction.ClaimedBids).Single(p => p.value == Uuid);
            var otherForeignKey = relation.RelationType == "coop_member" ? "SaveAuctionId" : "SaveAuctionId1";
            Require(db.Entry(row).Property(otherForeignKey).CurrentValue == null, "Shared relation row requires a fresh, complete export.");
            return row;
        }).ToList();
        if (!apply) return 0;
        foreach (var snapshot in Auctions.Where(a => a.SellerId == Player.Id))
        { var auction = auctions[snapshot.Id]; PermanentAnonymization.Anonymize(auction); }
        foreach (var bid in bids.Values) { PermanentAnonymization.Anonymize(bid); }
        db.RemoveRange(relationRows);
        // profile ids equal to the player's uuid on the rows loaded above
        foreach (var auction in related.Concat(auctions.Values))
            if (auction.ProfileId == Uuid) auction.ProfileId = null;
        foreach (var bid in bids.Values)
            if (bid.ProfileId == Uuid) bid.ProfileId = null;
        // unlink the account; the user row, its email and AgreementAcceptances are kept on purpose (compliance)
        var users = db.Users.Where(u => u.MinecraftUuid == Uuid).ToList();
        foreach (var user in users) user.MinecraftUuid = null;
        db.Players.Remove(player);
        db.SaveChanges();
        transaction.Commit();
        return users.Count;
    }
    private static void ConfigureDatabase()
    {
        var config = new ConfigurationBuilder().AddJsonFile("appsettings.json", true).AddEnvironmentVariables().AddOpenBaoFromEnvironment().Build();
        HypixelContext.SetConfiguration(config);
    }
    internal static int Run(string[] args)
    {
        try
        {
            Require(args.Length == 3 && args[1] is "preview" or "check" or "erase", "Usage: privacy preview|check|erase EXPORT.zip");
            var apply = args[1] == "erase";
            var plan = Read(args[2]);
            var unlinked = 0;
            if (args[1] != "preview")
            {
                ConfigureDatabase();
                using var db = new HypixelContext();
                unlinked = plan.Execute(db, apply);
            }
            Console.WriteLine($"SQL-only {args[1]}: {plan.Auctions.Count(a => a.SellerId == plan.Player.Id)} owned auctions, {plan.Bids.Count} target bids, {plan.Relations.Count} target relations; archive SHA-256 {plan.Sha256}. {(apply ? $"Players row deleted, {unlinked} user account(s) unlinked (account row and agreement acceptances kept)." : "No changes written.")} Scope: numbered sellers/bidders and relations on known auction IDs only; unnumbered identities and cross-service erasure are outside this command.");
            return 0;
        }
        catch (Exception error)
        {
            // SQL errors can contain sensitive parameters: do not print exception bodies.
            Console.Error.WriteLine(error is InvalidDataException ? error.Message : "Privacy command failed; no successful completion. Check archive format and database configuration.");
            return 1;
        }
    }
}
