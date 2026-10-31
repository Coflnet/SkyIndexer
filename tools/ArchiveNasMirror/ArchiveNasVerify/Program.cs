using System.Globalization;
using System.Text.Json;
using Coflnet.Sky.Core.Archive;
using Coflnet.Sky.Indexer.Tools.ArchiveNasVerify;

const string usage = @"ArchiveNasVerify --root <mirror dir> [--out <dir>] [--month yyyy-MM[,yyyy-MM]] [--tag TAG[,TAG]] [--mariadb-env <file>] [--parallel N]
  Decodes every block of the mirror, recomputes Sha256() and compares with the tag index (PartSha256, Parts, Rows).
  Writes verify-<month>.json for every month and receipt-<month>.json for every fully verified month into --out
  (default: <root>/_verify-<UTC timestamp>). --mariadb-env: file with DBConnection=<read-only connection string>.
  Exit code: 0 all verified, 1 verification failed, 2 usage/error.";

try
{
    var opt = new VerifyOptions();
    string? mariaEnv = null;
    string? out_ = null;
    for (var i = 0; i < args.Length; i++)
    {
        string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException("missing value for " + args[i]);
        switch (args[i])
        {
            case "--root": opt.Root = Path.GetFullPath(Next()); break;
            case "--out": out_ = Path.GetFullPath(Next()); break;
            case "--month":
                foreach (var m in Next().Split(',', StringSplitOptions.RemoveEmptyEntries)) opt.Months.Add(ArchiveMonths.Parse(m));
                break;
            case "--tag":
                foreach (var t in Next().Split(',', StringSplitOptions.RemoveEmptyEntries)) opt.Tags.Add(t);
                break;
            case "--mariadb-env": mariaEnv = Next(); break;
            case "--parallel": opt.Parallelism = int.Parse(Next(), CultureInfo.InvariantCulture); break;
            case "-h": case "--help": Console.WriteLine(usage); return 0;
            default: throw new ArgumentException("unknown argument " + args[i]);
        }
    }
    if (opt.Root.Length == 0)
        throw new ArgumentException("--root is required");
    opt.OutDir = out_ ?? Path.Combine(opt.Root, "_verify-" + DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture));
    IMariaDbSource? maria = mariaEnv == null ? null : MariaDbSource.FromEnvFile(mariaEnv);

    Console.Error.WriteLine($"verifying {opt.Root} -> {opt.OutDir}");
    var run = await Verifier.RunAsync(opt, maria, m => Console.Error.WriteLine(m));
    File.WriteAllText(Path.Combine(opt.OutDir, "summary.json"), JsonSerializer.Serialize(new
    {
        ok = run.Ok,
        months = run.Months.Select(m => new { m.Month, m.Ok, m.Tags, m.Parts, m.Rows, issues = m.Issues.Count, warnings = m.Warnings.Count }),
        globalIssues = run.GlobalIssues,
    }, Json.Options));
    foreach (var g in run.GlobalIssues) Console.WriteLine("ISSUE " + g);
    foreach (var m in run.Months)
    {
        Console.WriteLine($"{m.Month} {(m.Ok ? "OK  " : "FAIL")} tags={m.Tags} parts={m.Parts} rows={m.Rows} issues={m.Issues.Count} warnings={m.Warnings.Count}");
        foreach (var issue in m.Issues.Take(20)) Console.WriteLine("  ISSUE " + issue);
    }
    return run.Ok ? 0 : 1;
}
catch (Exception ex) when (ex is ArgumentException or FormatException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine("error: " + ex.Message);
    Console.Error.WriteLine(usage);
    return 2;
}
