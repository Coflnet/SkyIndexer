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
}
