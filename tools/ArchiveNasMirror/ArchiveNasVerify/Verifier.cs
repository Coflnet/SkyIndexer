using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Coflnet.Sky.Core.Archive;

namespace Coflnet.Sky.Indexer.Tools.ArchiveNasVerify;

public static class Verifier
{
    public const string ManifestGlob = "_manifest-*.tsv";

    /// <summary>Merged manifest of the mirror, oldest first so newer manifests win. Reads the 4 column (key,size,etag,status) and 5 column (+md5) formats.</summary>
    public static Dictionary<string, ManifestEntry> LoadManifest(string root)
    {
        var result = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var file in Directory.GetFiles(root, ManifestGlob).OrderBy(f => f, StringComparer.Ordinal))
        {
            foreach (var line in File.ReadLines(file))
            {
                var c = line.Split('\t');
                if (c.Length < 4 || c[0] == "key" || !long.TryParse(c[1], NumberStyles.None, CultureInfo.InvariantCulture, out var size))
                    continue;
                result[c[0]] = c.Length >= 5 ? new ManifestEntry(c[0], size, c[2], c[3], c[4]) : new ManifestEntry(c[0], size, c[2], "", c[3]);
            }
        }
        return result;
    }

    public static async Task<RunResult> RunAsync(VerifyOptions options, IMariaDbSource? maria, Action<string>? progress = null, CancellationToken ct = default)
    {
        var run = new RunResult();
        var v2 = Path.Combine(options.Root, "auctions", "v2");
        if (!Directory.Exists(v2))
        {
            run.GlobalIssues.Add($"{v2} does not exist");
            return run;
        }
        var manifest = LoadManifest(options.Root);
        if (manifest.Count == 0)
            run.GlobalIssues.Add("no _manifest-*.tsv found in mirror root (run mirror.py first)");

        var (results, globalIssues) = await VerifyAllTagDirsAsync(options, manifest, v2, progress, ct).ConfigureAwait(false);
        run.GlobalIssues.AddRange(globalIssues.OrderBy(i => i, StringComparer.Ordinal));

        var db = maria == null ? null : await maria.LoadAsync(ct).ConfigureAwait(false);
        var now = options.Now ?? DateTime.UtcNow;
        foreach (var month in MonthsToReport(options, results, db))
        {
            var monthResults = results.Where(r => r.Month == month).OrderBy(r => r.Tag, StringComparer.Ordinal).ToList();
            var report = BuildMonthReport(options, month, monthResults, db, now);
            run.Months.Add(report);
            WriteOutputs(options, report, monthResults, now);
        }
        return run;
    }

    private static async Task<(ConcurrentBag<TagMonthResult> Results, ConcurrentBag<string> GlobalIssues)> VerifyAllTagDirsAsync(
        VerifyOptions options, Dictionary<string, ManifestEntry> manifest, string v2, Action<string>? progress, CancellationToken ct)
    {
        var tagDirs = Directory.GetDirectories(v2).Select(Path.GetFileName).Where(n => n != null && n != "_meta").Select(n => n!)
            .OrderBy(n => n, StringComparer.Ordinal).ToList();
        var results = new ConcurrentBag<TagMonthResult>();
        var globalIssues = new ConcurrentBag<string>();
        var done = 0;
        await Parallel.ForEachAsync(tagDirs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, options.Parallelism), CancellationToken = ct }, async (dir, token) =>
        {
            foreach (var r in await VerifyTagDirAsync(options, manifest, dir, globalIssues, token).ConfigureAwait(false))
                results.Add(r);
            var n = Interlocked.Increment(ref done);
            if (n % 25 == 0)
                progress?.Invoke($"verified {n}/{tagDirs.Count} tag directories");
        }).ConfigureAwait(false);
        return (results, globalIssues);
    }

    /// <summary>Months found in the mirror, plus (with MariaDB, inside the requested filter) months that MariaDB archived but the mirror lacks.</summary>
    private static List<int> MonthsToReport(VerifyOptions options, IEnumerable<TagMonthResult> results, IReadOnlyDictionary<(string Tag, int Month), MariaMonthRow>? db)
    {
        var months = results.Select(r => r.Month).Distinct().ToList();
        if (db != null)
            months.AddRange(db.Keys.Select(k => k.Month).Distinct().Where(m => !months.Contains(m) && (options.Months.Count == 0 || options.Months.Contains(m))));
        months.Sort();
        return months;
    }

    private static MonthReport BuildMonthReport(VerifyOptions options, int month, List<TagMonthResult> monthResults,
        IReadOnlyDictionary<(string Tag, int Month), MariaMonthRow>? db, DateTime now)
    {
        var report = new MonthReport { Month = ArchiveMonths.Format(month), VerifiedAt = now.ToString("O", CultureInfo.InvariantCulture), MariaDbChecked = db != null };
        if (db != null)
            ReconcileWithMariaDb(options, month, monthResults, db, report);
        foreach (var r in monthResults)
        {
            report.Issues.AddRange(r.Issues.Select(i => $"{r.Tag}: {i}"));
            report.Warnings.AddRange(r.Warnings.Select(w => $"{r.Tag}: {w}"));
            report.TagResults.Add(new TagReportRow { Tag = r.Tag, Ok = r.Issues.Count == 0, Parts = r.Parts, Rows = r.Rows, CombinedSha256 = r.CombinedSha256, MariaDbMatches = r.MariaDbMatches });
        }
        report.Tags = monthResults.Count;
        report.Parts = monthResults.Sum(r => r.Parts);
        report.Rows = monthResults.Sum(r => r.Rows);
        report.Ok = report.Issues.Count == 0 && monthResults.Count > 0;
        if (monthResults.Count == 0 && report.Issues.Count == 0)
            report.Issues.Add("no tag of this month found in the mirror");
        return report;
    }

    /// <summary>Compares each mirrored tag-month with its archived_months row and reports rows MariaDB archived that the mirror lacks.</summary>
    private static void ReconcileWithMariaDb(VerifyOptions options, int month, List<TagMonthResult> monthResults,
        IReadOnlyDictionary<(string Tag, int Month), MariaMonthRow> db, MonthReport report)
    {
        foreach (var r in monthResults)
            CompareWithMariaDbRow(r, db.GetValueOrDefault((r.Tag, month)));
        var inMirror = monthResults.Select(r => r.Tag).ToHashSet(StringComparer.Ordinal);
        var missing = db.Keys.Where(k => k.Month == month && !inMirror.Contains(k.Tag) && (options.Tags.Count == 0 || options.Tags.Contains(k.Tag)))
            .OrderBy(k => k.Tag, StringComparer.Ordinal);
        foreach (var key in missing)
            report.Issues.Add($"{key.Tag}: archived in MariaDB but missing from the mirror");
    }

    private static void CompareWithMariaDbRow(TagMonthResult r, MariaMonthRow? row)
    {
        if (row == null)
        {
            r.MariaDbMatches = false;
            r.Issues.Add("no archived_months row in MariaDB");
            return;
        }
        var before = r.Issues.Count;
        if (row.Sha256 != r.CombinedSha256)
            r.Issues.Add($"combined sha256 {r.CombinedSha256} != MariaDB {row.Sha256}");
        if (row.Rows != r.Rows)
            r.Issues.Add($"rows {r.Rows} != MariaDB {row.Rows}");
        if (row.Parts != r.Parts)
            r.Issues.Add($"parts {r.Parts} != MariaDB {row.Parts}");
        r.MariaDbMatches = r.Issues.Count == before;
    }

    private static void WriteOutputs(VerifyOptions options, MonthReport report, List<TagMonthResult> monthResults, DateTime now)
    {
        Directory.CreateDirectory(options.OutDir);
        File.WriteAllText(Path.Combine(options.OutDir, $"verify-{report.Month}.json"), JsonSerializer.Serialize(report, Json.Options));
        var receiptPath = Path.Combine(options.OutDir, $"receipt-{report.Month}.json");
        if (!report.Ok)
        {
            // a receipt is a proof: never leave a stale one next to a failed verification
            if (File.Exists(receiptPath))
                File.Delete(receiptPath);
            return;
        }
        var receipt = new Receipt
        {
            Month = report.Month,
            CreatedAt = now.ToString("O", CultureInfo.InvariantCulture),
            Tags = monthResults.Select(r => new ReceiptTag { Tag = r.Tag, Parts = r.Parts, Rows = r.Rows, PartSha256 = r.PartSha256, Objects = r.Objects }).ToList(),
        };
        File.WriteAllText(receiptPath, JsonSerializer.Serialize(receipt, Json.Options));
    }

    private static string LocalPath(string root, string key) => Path.Combine(root, Path.Combine(key.Split('/')));

    private static async Task<List<TagMonthResult>> VerifyTagDirAsync(VerifyOptions options, Dictionary<string, ManifestEntry> manifest, string dirName, ConcurrentBag<string> globalIssues, CancellationToken ct)
    {
        var output = new List<TagMonthResult>();
        var indexPath = Path.Combine(options.Root, "auctions", "v2", dirName, "index.msgpack.br");
        if (!File.Exists(indexPath))
        {
            if (options.Tags.Count == 0 || options.Tags.Contains(Uri.UnescapeDataString(dirName)))
                globalIssues.Add($"auctions/v2/{dirName}: no index.msgpack.br");
            return output;
        }
        ArchivedTagIndex index;
        try
        {
            await using var fs = File.OpenRead(indexPath);
            index = await ArchiveBlobCodec.ReadAsync<ArchivedTagIndex>(fs, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            globalIssues.Add($"auctions/v2/{dirName}/index.msgpack.br: undecodable ({ex.GetType().Name})");
            return output;
        }
        if (options.Tags.Count > 0 && !options.Tags.Contains(index.Tag ?? ""))
            return output;
        if (string.IsNullOrEmpty(index.Tag) || AuctionBlobKeys.Index(index.Tag) != $"auctions/v2/{dirName}/index.msgpack.br")
        {
            globalIssues.Add($"auctions/v2/{dirName}: index tag '{index.Tag}' does not belong to this directory");
            return output;
        }
        foreach (var entry in index.Months ?? new List<ArchivedMonthEntry>())
        {
            if (options.Months.Count > 0 && !options.Months.Contains(entry.Month))
                continue;
            output.Add(await VerifyMonthAsync(options, manifest, index.Tag, entry, ct).ConfigureAwait(false));
        }
        return output;
    }

    private static async Task<TagMonthResult> VerifyMonthAsync(VerifyOptions options, Dictionary<string, ManifestEntry> manifest, string tag, ArchivedMonthEntry entry, CancellationToken ct)
    {
        var res = new TagMonthResult { Tag = tag, Month = entry.Month, Parts = entry.Parts, Rows = entry.Rows };
        var expected = entry.PartSha256 ?? new List<string>();
        if (expected.Count != entry.Parts)
            res.Issues.Add($"index lists {expected.Count} part hashes but Parts={entry.Parts}");
        if (entry.Parts <= 0)
            res.Issues.Add("index entry has no parts");

        long rows = 0, sold = 0;
        for (var part = 0; part < entry.Parts; part++)
        {
            ct.ThrowIfCancellationRequested();
            var counts = await VerifyPartAsync(options, manifest, res, entry, expected, part, ct).ConfigureAwait(false);
            rows += counts.Rows;
            sold += counts.Sold;
        }
        WarnAboutExtraParts(options, res, entry);
        FinishMonth(res, entry, rows, sold);
        return res;
    }

    /// <summary>Checks one part file against the manifest and the index; returns the decoded row counts (0 when it could not be read).</summary>
    private static async Task<(long Rows, long Sold)> VerifyPartAsync(VerifyOptions options, Dictionary<string, ManifestEntry> manifest,
        TagMonthResult res, ArchivedMonthEntry entry, List<string> expected, int part, CancellationToken ct)
    {
        var key = AuctionBlobKeys.Block(res.Tag, entry.Month, part);
        var path = LocalPath(options.Root, key);
        if (!File.Exists(path))
        {
            res.Issues.Add($"part {part} missing ({key})");
            return (0, 0);
        }
        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
        }
        catch (IOException ex)
        {
            res.Issues.Add($"part {part} unreadable ({ex.GetType().Name})");
            return (0, 0);
        }
        CheckPartAgainstManifest(res, manifest, key, part, bytes);
        return await DecodePartAsync(res, entry, expected, part, bytes, ct).ConfigureAwait(false);
    }

    private static void CheckPartAgainstManifest(TagMonthResult res, Dictionary<string, ManifestEntry> manifest, string key, int part, byte[] bytes)
    {
        if (!manifest.TryGetValue(key, out var m))
        {
            res.Issues.Add($"part {part} is not in the mirror manifest");
            return;
        }
        var md5 = Convert.ToHexString(MD5.HashData(bytes)).ToLowerInvariant();
        if (m.Status is "gone" or "failed" || m.Status.StartsWith("failed", StringComparison.Ordinal))
            res.Issues.Add($"part {part} has manifest status {m.Status}");
        if (m.Size != bytes.Length)
            res.Issues.Add($"part {part} size {bytes.Length} != manifest {m.Size}");
        if (!m.Etag.Contains('-') && !string.Equals(m.Etag, md5, StringComparison.OrdinalIgnoreCase))
            res.Issues.Add($"part {part} md5 {md5} != etag {m.Etag}");
        if (m.Md5.Length > 0 && !string.Equals(m.Md5, md5, StringComparison.OrdinalIgnoreCase))
            res.Issues.Add($"part {part} md5 differs from manifest md5 (file changed after mirroring)");
        res.Objects.Add(new ObjectRef { Key = key, Etag = m.Etag, Size = m.Size });
    }

    private static async Task<(long Rows, long Sold)> DecodePartAsync(TagMonthResult res, ArchivedMonthEntry entry, List<string> expected, int part, byte[] bytes, CancellationToken ct)
    {
        try
        {
            using var ms = new MemoryStream(bytes, writable: false);
            var block = await ArchiveBlobCodec.ReadAsync<ArchivedAuctionBlock>(ms, ct).ConfigureAwait(false);
            if (block.Tag != res.Tag || block.Month != entry.Month || block.Part != part)
                res.Issues.Add($"part {part} header says {block.Tag}/{block.Month}/{block.Part}");
            var hash = block.Sha256();
            res.PartSha256.Add(hash);
            if (part < expected.Count && !string.Equals(expected[part], hash, StringComparison.OrdinalIgnoreCase))
                res.Issues.Add($"part {part} sha256 {hash} != index {expected[part]}");
            return (block.RowCount, block.IsSold?.Count(s => s) ?? 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            res.PartSha256.Add("");
            res.Issues.Add($"part {part} cannot be decoded ({ex.GetType().Name})");
            return (0, 0);
        }
    }

    /// <summary>Parts on disk beyond the index entry (not fatal: an interrupted rewrite may leave them in R2).</summary>
    private static void WarnAboutExtraParts(VerifyOptions options, TagMonthResult res, ArchivedMonthEntry entry)
    {
        var monthDir = Path.GetDirectoryName(LocalPath(options.Root, AuctionBlobKeys.Block(res.Tag, entry.Month, 0)))!;
        if (!Directory.Exists(monthDir))
            return;
        var extra = Directory.GetFiles(monthDir, "*.msgpack.br").Count(f => int.TryParse(Path.GetFileName(f).Split('.')[0], out var p) && p >= entry.Parts);
        if (extra > 0)
            res.Warnings.Add($"{extra} part file(s) beyond Parts={entry.Parts}");
    }

    private static void FinishMonth(TagMonthResult res, ArchivedMonthEntry entry, long rows, long sold)
    {
        var allDecoded = res.PartSha256.Count > 0 && res.PartSha256.All(h => h.Length > 0);
        if (res.Issues.Count == 0 || res.PartSha256.Count == entry.Parts && allDecoded)
        {
            if (rows != entry.Rows)
                res.Issues.Add($"decoded rows {rows} != index Rows {entry.Rows}");
            if (sold != entry.SoldRows)
                res.Warnings.Add($"decoded sold rows {sold} != index SoldRows {entry.SoldRows}");
        }
        res.CombinedSha256 = allDecoded ? ArchivedAuctionBlock.CombinedSha256(res.PartSha256) : "";
        res.Objects = res.Objects.OrderBy(o => o.Key, StringComparer.Ordinal).ToList();
    }
}
