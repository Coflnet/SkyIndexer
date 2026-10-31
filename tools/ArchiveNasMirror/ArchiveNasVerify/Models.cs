using System.Text.Json;
using System.Text.Json.Serialization;

namespace Coflnet.Sky.Indexer.Tools.ArchiveNasVerify;

public sealed class VerifyOptions
{
    /// <summary>Mirror directory (contains <c>auctions/v2</c> and the <c>_manifest-*.tsv</c> files).</summary>
    public string Root { get; set; } = "";
    /// <summary>Directory receiving verify-*.json / receipt-*.json.</summary>
    public string OutDir { get; set; } = "";
    /// <summary>Only these months (yyyyMM); empty = every month found in the tag indexes.</summary>
    public HashSet<int> Months { get; set; } = new();
    /// <summary>Only these tags; empty = all.</summary>
    public HashSet<string> Tags { get; set; } = new(StringComparer.Ordinal);
    public int Parallelism { get; set; } = Environment.ProcessorCount;
    public DateTime? Now { get; set; }
}

public sealed record MariaMonthRow(string Tag, int Month, int Parts, long Rows, string Sha256, DateTime? DeletedAt);

public interface IMariaDbSource
{
    /// <summary>All non-sentinel archived_months rows keyed by (tag, yyyyMM).</summary>
    Task<IReadOnlyDictionary<(string Tag, int Month), MariaMonthRow>> LoadAsync(CancellationToken ct);
}

public sealed record ManifestEntry(string Key, long Size, string Etag, string Md5, string Status);

public sealed class ObjectRef
{
    public string Key { get; set; } = "";
    public string Etag { get; set; } = "";
    public long Size { get; set; }
}

public sealed class TagMonthResult
{
    public string Tag { get; set; } = "";
    public int Month { get; set; }
    public int Parts { get; set; }
    public long Rows { get; set; }
    public List<string> PartSha256 { get; set; } = new();
    public string CombinedSha256 { get; set; } = "";
    public List<ObjectRef> Objects { get; set; } = new();
    public List<string> Issues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public bool? MariaDbMatches { get; set; }
}

public sealed class ReceiptTag
{
    public string Tag { get; set; } = "";
    public int Parts { get; set; }
    public long Rows { get; set; }
    public List<string> PartSha256 { get; set; } = new();
    public List<ObjectRef> Objects { get; set; } = new();
}

/// <summary>Schema is fixed by the consumer (version 1); <c>snapshot</c> is filled in by snapshot.sh.</summary>
public sealed class Receipt
{
    public int Version { get; set; } = 1;
    public string Month { get; set; } = "";
    public string Snapshot { get; set; } = "";
    public string CreatedAt { get; set; } = "";
    public List<ReceiptTag> Tags { get; set; } = new();
}

public sealed class MonthReport
{
    public string Month { get; set; } = "";
    public bool Ok { get; set; }
    public string VerifiedAt { get; set; } = "";
    public bool MariaDbChecked { get; set; }
    public int Tags { get; set; }
    public int Parts { get; set; }
    public long Rows { get; set; }
    public List<string> Issues { get; set; } = new();
    public List<string> Warnings { get; set; } = new();
    public List<TagReportRow> TagResults { get; set; } = new();
}

public sealed class TagReportRow
{
    public string Tag { get; set; } = "";
    public bool Ok { get; set; }
    public int Parts { get; set; }
    public long Rows { get; set; }
    public string CombinedSha256 { get; set; } = "";
    public bool? MariaDbMatches { get; set; }
}

public sealed class RunResult
{
    public List<MonthReport> Months { get; set; } = new();
    public List<string> GlobalIssues { get; set; } = new();
    public bool Ok => GlobalIssues.Count == 0 && Months.All(m => m.Ok);
}

public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
