using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

public class ArchiveOptionsTests
{
    private static IConfiguration Config(params (string key, string value)[] values)
    {
        var dict = new Dictionary<string, string>();
        foreach (var (key, value) in values)
            dict["ARCHIVE:" + key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }

    [Test]
    public void Defaults_StartAtSkyBlockLaunchMonth_NotJanuary2019()
    {
        // Every month before 2019-06 is empty and would cost one page query per ItemId (~18 min for 4,771 items).
        var o = ArchiveOptions.From(Config());
        Assert.That(o.OldestMonth, Is.EqualTo(201906));
    }

    [Test]
    public void Defaults_TagPauseIsShort()
    {
        // 1,000 ms per ItemId-month with rows made a full backfill take days in pauses alone.
        var o = ArchiveOptions.From(Config());
        Assert.That(o.TagPauseMs, Is.EqualTo(100));
        Assert.That(o.PagePauseMs, Is.EqualTo(200));
    }

    [Test]
    public void ShippedAppsettings_MatchTheCodeDefaults()
    {
        // appsettings.json wins over the code defaults; the first rollout of the 2019-06 default was silently
        // overridden by the "2019-01" / 1000 ms values still shipped in the file.
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        string path = null;
        for (; dir != null && path == null; dir = dir.Parent)
        {
            var candidate = System.IO.Path.Combine(dir.FullName, "appsettings.json");
            if (System.IO.File.Exists(candidate) && System.IO.File.Exists(System.IO.Path.Combine(dir.FullName, "SkyIndexer.csproj")))
                path = candidate;
        }
        Assert.That(path, Is.Not.Null, "appsettings.json next to SkyIndexer.csproj not found");
        var shipped = ArchiveOptions.From(new ConfigurationBuilder().AddJsonFile(path).Build());
        var defaults = new ArchiveOptions();
        Assert.That(shipped.OldestMonth, Is.EqualTo(defaults.OldestMonth));
        Assert.That(shipped.TagPauseMs, Is.EqualTo(defaults.TagPauseMs));
        Assert.That(shipped.PagePauseMs, Is.EqualTo(defaults.PagePauseMs));
        Assert.That(shipped.StartDelaySeconds, Is.EqualTo(defaults.StartDelaySeconds));
        Assert.That(shipped.MaxRowsPerPart, Is.EqualTo(defaults.MaxRowsPerPart));
        Assert.That(shipped.ImportPollMinutes, Is.EqualTo(defaults.ImportPollMinutes));
        Assert.That(defaults.ImportPollMinutes, Is.EqualTo(5));
    }

    [Test]
    public void From_OldestMonthOverride_IsParsed()
    {
        var o = ArchiveOptions.From(Config(("OLDEST_MONTH", "2021-03")));
        Assert.That(o.OldestMonth, Is.EqualTo(202103));
    }

    [Test]
    public void From_PauseOverrides_AreApplied_AndInvalidValuesFallBack()
    {
        var o = ArchiveOptions.From(Config(("TAG_PAUSE_MS", "250"), ("PAGE_PAUSE_MS", "not-a-number")));
        Assert.That(o.TagPauseMs, Is.EqualTo(250));
        Assert.That(o.PagePauseMs, Is.EqualTo(200));
    }

    [Test]
    public void From_ChildQueryBatchSize_DefaultOverrideAndMinimum()
    {
        Assert.That(ArchiveOptions.From(Config()).ChildQueryBatchSize, Is.EqualTo(100));
        Assert.That(ArchiveOptions.From(Config(("CHILD_QUERY_BATCH_SIZE", "150"))).ChildQueryBatchSize, Is.EqualTo(150));
        Assert.That(ArchiveOptions.From(Config(("CHILD_QUERY_BATCH_SIZE", "0"))).ChildQueryBatchSize, Is.EqualTo(1));
    }
}
