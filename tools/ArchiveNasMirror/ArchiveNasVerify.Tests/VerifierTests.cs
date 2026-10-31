using System.Security.Cryptography;
using System.Text.Json;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Coflnet.Sky.Indexer.Tools.ArchiveNasVerify;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Tools.ArchiveNasVerify.Tests;

public class VerifierTests
{
    private const int Month = 202103;
    private string _root = "";
    private string _out = "";

    [SetUp]
    public void SetUp()
    {
        _root = Path.Combine(Path.GetTempPath(), "nasverify-" + Guid.NewGuid().ToString("N"));
        _out = Path.Combine(_root, "_out");
        Directory.CreateDirectory(_root);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    private static SaveAuction Row(string tag, long uid, int day)
    {
        var end = new DateTime(2021, 3, day, 12, 0, 0, DateTimeKind.Unspecified);
        return new SaveAuction
        {
            Id = (int)uid, UId = uid, Uuid = uid.ToString("x32"), Tag = tag, ItemName = "Item " + uid, End = end, Start = end.AddDays(-1),
            AuctioneerId = (1000 + uid).ToString("x32"), StartingBid = 10 + uid, HighestBidAmount = uid % 2 == 0 ? 50 : 0, Count = 1,
            Tier = Tier.RARE, Category = Category.WEAPON, Reforge = ItemReferences.Reforge.None, ItemCreatedAt = new DateTime(2021, 1, 1),
            Bids = new List<SaveBids>(), Enchantments = new List<Enchantment>(), NBTLookup = new List<NBTLookup>(),
        };
    }

    private static string Key(string tag, int part) => AuctionBlobKeys.Block(tag, Month, part);
    private string Path_(string key) => Path.Combine(_root, Path.Combine(key.Split('/')));

    private byte[] Encode<T>(T value)
    {
        using var ms = new MemoryStream();
        ArchiveBlobCodec.Write(ms, value);
        return ms.ToArray();
    }

    private void Put(string key, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path_(key))!);
        File.WriteAllBytes(Path_(key), bytes);
    }

    /// <summary>Writes a tag with <paramref name="partRows"/> rows per part, its index and returns the part blocks.</summary>
    private List<ArchivedAuctionBlock> WriteTag(string tag, params int[] partRows)
    {
        var blocks = new List<ArchivedAuctionBlock>();
        var uid = 1L;
        for (var p = 0; p < partRows.Length; p++)
        {
            var rows = Enumerable.Range(0, partRows[p]).Select(i => Row(tag, uid++, 1 + i)).ToList();
            var block = ArchivedAuctionBlock.FromAuctions(tag, Month, p, ArchivedAuctionBlock.SourceMariaDb, rows);
            blocks.Add(block);
            Put(Key(tag, p), Encode(block));
        }
        var index = new ArchivedTagIndex { Tag = tag };
        index.Upsert(new ArchivedMonthEntry
        {
            Month = Month, Parts = blocks.Count, Rows = partRows.Sum(), SoldRows = blocks.Sum(b => b.IsSold.Count(s => s)),
            PartSha256 = blocks.Select(b => b.Sha256()).ToList(), Source = ArchivedAuctionBlock.SourceMariaDb, VerifiedAt = DateTime.UtcNow,
        });
        Put(AuctionBlobKeys.Index(tag), Encode(index));
        return blocks;
    }

    /// <summary>Manifest as the python mirror writes it (md5 == etag for these single-part objects).</summary>
    private void WriteManifest(params string[] exceptKeys)
    {
        var lines = new List<string> { "key\tsize\tetag\tmd5\tstatus" };
        foreach (var file in Directory.GetFiles(Path.Combine(_root, "auctions"), "*", SearchOption.AllDirectories))
        {
            var key = Path.GetRelativePath(_root, file).Replace('\\', '/');
            if (exceptKeys.Contains(key))
                continue;
            var bytes = File.ReadAllBytes(file);
            var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
            lines.Add($"{key}\t{bytes.Length}\t{md5}\t{md5}\tok");
        }
        File.WriteAllLines(Path.Combine(_root, "_manifest-20261031T000000Z.tsv"), lines);
    }

    private Task<RunResult> Run(IMariaDbSource? maria = null) => Verifier.RunAsync(new VerifyOptions { Root = _root, OutDir = _out, Parallelism = 2 }, maria);

    private sealed class FakeMaria : IMariaDbSource
    {
        public Dictionary<(string, int), MariaMonthRow> Rows { get; } = new();
        public Task<IReadOnlyDictionary<(string Tag, int Month), MariaMonthRow>> LoadAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<(string Tag, int Month), MariaMonthRow>>(Rows);
    }

    [Test]
    public async Task GoodMirrorVerifiesAndWritesReceiptWithExactSchema()
    {
        var blocksA = WriteTag("HYPERION", 3, 2);
        WriteTag("A/B", 1);
        WriteManifest();

        var run = await Run();

        Assert.That(run.Ok, Is.True, string.Join("\n", run.Months.SelectMany(m => m.Issues).Concat(run.GlobalIssues)));
        Assert.That(run.Months.Select(m => m.Month), Is.EqualTo(new[] { "2021-03" }));
        Assert.That(File.Exists(Path.Combine(_out, "verify-2021-03.json")));
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(_out, "receipt-2021-03.json")));
        var root = doc.RootElement;
        Assert.That(root.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "version", "month", "snapshot", "createdAt", "tags" }));
        Assert.That(root.GetProperty("version").GetInt32(), Is.EqualTo(1));
        Assert.That(root.GetProperty("month").GetString(), Is.EqualTo("2021-03"));
        Assert.That(root.GetProperty("snapshot").GetString(), Is.EqualTo(""));
        Assert.That(DateTime.TryParse(root.GetProperty("createdAt").GetString(), out _));
        var tags = root.GetProperty("tags").EnumerateArray().ToList();
        Assert.That(tags.Select(t => t.GetProperty("tag").GetString()), Is.EqualTo(new[] { "A/B", "HYPERION" }), "ordinal order");
        var hyp = tags[1];
        Assert.That(hyp.EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "tag", "parts", "rows", "partSha256", "objects" }));
        Assert.That(hyp.GetProperty("parts").GetInt32(), Is.EqualTo(2));
        Assert.That(hyp.GetProperty("rows").GetInt64(), Is.EqualTo(5));
        Assert.That(hyp.GetProperty("partSha256").EnumerateArray().Select(x => x.GetString()), Is.EqualTo(blocksA.Select(b => b.Sha256())));
        var objects = hyp.GetProperty("objects").EnumerateArray().ToList();
        Assert.That(objects[0].EnumerateObject().Select(p => p.Name), Is.EqualTo(new[] { "key", "etag", "size" }));
        Assert.That(objects[0].GetProperty("key").GetString(), Is.EqualTo("auctions/v2/HYPERION/2021-03/000.msgpack.br"));
        Assert.That(objects[0].GetProperty("size").GetInt64(), Is.EqualTo(new FileInfo(Path_(Key("HYPERION", 0))).Length));
        Assert.That(objects[0].GetProperty("etag").GetString(), Has.Length.EqualTo(32));
        Assert.That(tags[0].GetProperty("objects")[0].GetProperty("key").GetString(), Is.EqualTo("auctions/v2/A%2FB/2021-03/000.msgpack.br"));
    }

    /// <summary>The receipt the verifier writes, stamped with a snapshot like snapshot.sh does, is accepted by the consumer (SkyIndexer's retention gate).</summary>
    [Test]
    public async Task WrittenReceiptIsAcceptedByTheRetentionGateParserOnceStamped()
    {
        var blocks = WriteTag("HYPERION", 3, 2);
        WriteTag("A/B", 1);
        WriteManifest();
        var run = await Run();
        Assert.That(run.Ok, Is.True);
        var text = File.ReadAllText(Path.Combine(_out, "receipt-2021-03.json"));
        Assert.Throws<FormatException>(() => BackupReceipt.Parse(text), "unstamped: no snapshot yet");
        var stamped = JsonSerializer.Serialize(new Dictionary<string, object?>(JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(text)!.ToDictionary(kv => kv.Key, kv => (object?)kv.Value))
            { ["snapshot"] = "auction-archive-20261101T0300Z" });

        var receipt = BackupReceipt.Parse(stamped);

        Assert.That(receipt.Month, Is.EqualTo("2021-03"));
        Assert.That(receipt.Tags.Select(t => t.Tag), Is.EqualTo(new[] { "A/B", "HYPERION" }));
        Assert.That(receipt.Tags[1].PartSha256, Is.EqualTo(blocks.Select(b => b.Sha256())));
        var current = new List<(string, ArchivedMonthEntry?)>();
        foreach (var tag in new[] { "A/B", "HYPERION" })
            current.Add((tag, await ReadIndexEntry(tag)));
        var etags = File.ReadAllLines(Directory.GetFiles(_root, "_manifest-*.tsv")[0]).Skip(1).Select(l => l.Split('\t')).ToDictionary(c => c[0], c => "\"" + c[2] + "\"");
        Assert.That(await receipt.FindProblemAsync(Month, current!, key => Task.FromResult(etags.GetValueOrDefault(key))), Is.Null);
        etags[Key("HYPERION", 1)] = "\"ffffffffffffffffffffffffffffffff\"";
        Assert.That(await receipt.FindProblemAsync(Month, current!, key => Task.FromResult(etags.GetValueOrDefault(key))), Does.Contain("rewritten since the backup"));
    }

    private async Task<ArchivedMonthEntry?> ReadIndexEntry(string tag)
    {
        await using var fs = File.OpenRead(Path_(AuctionBlobKeys.Index(tag)));
        var index = await ArchiveBlobCodec.ReadAsync<ArchivedTagIndex>(fs, CancellationToken.None);
        return index.Find(Month);
    }

    [Test]
    public async Task CorruptedByteIsDetectedAndNoReceiptIsWritten()
    {
        WriteTag("HYPERION", 3);
        WriteManifest();
        var path = Path_(Key("HYPERION", 0));
        var bytes = File.ReadAllBytes(path);
        bytes[bytes.Length / 2] ^= 0xFF; // manifest still has the old md5
        File.WriteAllBytes(path, bytes);

        var run = await Run();

        Assert.That(run.Ok, Is.False);
        Assert.That(run.Months[0].Issues, Has.Some.Contains("md5"));
        Assert.That(File.Exists(Path.Combine(_out, "receipt-2021-03.json")), Is.False);
        Assert.That(File.Exists(Path.Combine(_out, "verify-2021-03.json")), Is.True);
    }

    [Test]
    public async Task ContentCorruptionWithMatchingManifestIsCaughtByPartHash()
    {
        WriteTag("HYPERION", 3);
        // replace the block with a different (valid) block of the same shape and re-mirror it, so the byte checks pass
        var other = ArchivedAuctionBlock.FromAuctions("HYPERION", Month, 0, ArchivedAuctionBlock.SourceMariaDb, new[] { Row("HYPERION", 1, 1), Row("HYPERION", 2, 2), Row("HYPERION", 99, 3) });
        Put(Key("HYPERION", 0), Encode(other));
        WriteManifest();

        var run = await Run();

        Assert.That(run.Ok, Is.False);
        Assert.That(run.Months[0].Issues, Has.Some.Contains("sha256"));
        Assert.That(run.Months[0].Issues, Has.None.Contains("md5"));
    }

    [Test]
    public async Task MissingPartIsReported()
    {
        WriteTag("HYPERION", 2, 2);
        WriteManifest();
        File.Delete(Path_(Key("HYPERION", 1)));

        var run = await Run();

        Assert.That(run.Ok, Is.False);
        Assert.That(run.Months[0].Issues, Has.Some.Contains("part 1 missing"));
        Assert.That(File.Exists(Path.Combine(_out, "receipt-2021-03.json")), Is.False);
    }

    [Test]
    public async Task WrongPartCountInIndexIsReported()
    {
        WriteTag("HYPERION", 2, 2);
        var indexKey = AuctionBlobKeys.Index("HYPERION");
        ArchivedTagIndex index;
        using (var fs = File.OpenRead(Path_(indexKey)))
            index = await ArchiveBlobCodec.ReadAsync<ArchivedTagIndex>(fs);
        index.Months[0].Parts = 3; // claims a third part that does not exist; hash list still has 2
        Put(indexKey, Encode(index));
        WriteManifest();

        var run = await Run();

        Assert.That(run.Ok, Is.False);
        Assert.That(run.Months[0].Issues, Has.Some.Contains("Parts=3"));
        Assert.That(run.Months[0].Issues, Has.Some.Contains("part 2 missing"));
    }

    [Test]
    public async Task ExtraPartOnDiskIsOnlyAWarning()
    {
        WriteTag("HYPERION", 2);
        Put(Key("HYPERION", 1), Encode(ArchivedAuctionBlock.FromAuctions("HYPERION", Month, 1, ArchivedAuctionBlock.SourceMariaDb, new[] { Row("HYPERION", 50, 4) })));
        WriteManifest();

        var run = await Run();

        Assert.That(run.Ok, Is.True);
        Assert.That(run.Months[0].Warnings, Has.Some.Contains("beyond Parts=1"));
    }

    [Test]
    public async Task WrongRowCountInIndexIsReported()
    {
        WriteTag("HYPERION", 3);
        var indexKey = AuctionBlobKeys.Index("HYPERION");
        ArchivedTagIndex index;
        using (var fs = File.OpenRead(Path_(indexKey)))
            index = await ArchiveBlobCodec.ReadAsync<ArchivedTagIndex>(fs);
        index.Months[0].Rows = 4;
        Put(indexKey, Encode(index));
        WriteManifest();

        var run = await Run();

        Assert.That(run.Ok, Is.False);
        Assert.That(run.Months[0].Issues, Has.Some.Contains("decoded rows 3 != index Rows 4"));
    }

    [Test]
    public async Task ObjectMissingFromManifestOrGoneIsReported()
    {
        WriteTag("HYPERION", 2);
        WriteManifest(Key("HYPERION", 0));

        var run = await Run();

        Assert.That(run.Ok, Is.False);
        Assert.That(run.Months[0].Issues, Has.Some.Contains("not in the mirror manifest"));
    }

    [Test]
    public async Task StaleReceiptIsRemovedWhenMonthNowFails()
    {
        WriteTag("HYPERION", 2);
        WriteManifest();
        Assert.That((await Run()).Ok, Is.True);
        Assert.That(File.Exists(Path.Combine(_out, "receipt-2021-03.json")));
        File.Delete(Path_(Key("HYPERION", 0)));

        Assert.That((await Run()).Ok, Is.False);
        Assert.That(File.Exists(Path.Combine(_out, "receipt-2021-03.json")), Is.False);
    }

    [Test]
    public async Task MariaDbCombinedHashAndRowsAreCompared()
    {
        var blocks = WriteTag("HYPERION", 3, 2);
        WriteManifest();
        var maria = new FakeMaria();
        maria.Rows[("HYPERION", Month)] = new MariaMonthRow("HYPERION", Month, 2, 5, ArchivedAuctionBlock.CombinedSha256(blocks.Select(b => b.Sha256()).ToList()), null);

        var good = await Run(maria);
        Assert.That(good.Ok, Is.True, string.Join("\n", good.Months.SelectMany(m => m.Issues)));
        Assert.That(good.Months[0].TagResults[0].MariaDbMatches, Is.True);

        maria.Rows[("HYPERION", Month)] = new MariaMonthRow("HYPERION", Month, 2, 6, new string('a', 64), null);
        var bad = await Run(maria);
        Assert.That(bad.Ok, Is.False);
        Assert.That(bad.Months[0].Issues, Has.Some.Contains("combined sha256"));
        Assert.That(bad.Months[0].Issues, Has.Some.Contains("rows 5 != MariaDB 6"));
        Assert.That(File.Exists(Path.Combine(_out, "receipt-2021-03.json")), Is.False);
    }

    [Test]
    public async Task MariaDbTagMissingFromMirrorFailsTheMonth()
    {
        WriteTag("HYPERION", 1);
        WriteManifest();
        var maria = new FakeMaria();
        maria.Rows[("HYPERION", Month)] = new MariaMonthRow("HYPERION", Month, 1, 1, ArchivedAuctionBlock.CombinedSha256(new[] { WriteAgain() }), null);
        maria.Rows[("GHOST", Month)] = new MariaMonthRow("GHOST", Month, 1, 1, "x", null);

        var run = await Run(maria);

        Assert.That(run.Months[0].Issues, Has.Some.Contains("GHOST: archived in MariaDB but missing from the mirror"));
    }

    private string WriteAgain()
    {
        using var fs = File.OpenRead(Path_(Key("HYPERION", 0)));
        return ArchiveBlobCodec.ReadAsync<ArchivedAuctionBlock>(fs).GetAwaiter().GetResult().Sha256();
    }

    [Test]
    public void MariaDbEnvFileParsesKnownKeyAndBareLine()
    {
        var f = Path.Combine(_root, "m.env");
        File.WriteAllText(f, "# c\nDBConnection=\"server=h;user=u;password=p\"\n");
        Assert.DoesNotThrow(() => MariaDbSource.FromEnvFile(f));
        File.WriteAllText(f, "server=h;user=u;password=p\n");
        Assert.DoesNotThrow(() => MariaDbSource.FromEnvFile(f));
        File.WriteAllText(f, "FOO=bar\nBAZ=1\n");
        var ex = Assert.Throws<InvalidOperationException>(() => MariaDbSource.FromEnvFile(f));
        Assert.That(ex!.Message, Does.Not.Contain("bar"));
    }
}
