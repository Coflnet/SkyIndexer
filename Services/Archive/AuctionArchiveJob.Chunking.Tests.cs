using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

public class AuctionArchiveJobChunkingTests
{
    private static int Configured(string value = null)
    {
        var d = new System.Collections.Generic.Dictionary<string, string>();
        if (value != null) d["ARCHIVE:CHILD_QUERY_BATCH_SIZE"] = value;
        return ArchiveOptions.From(Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions.AddInMemoryCollection(new Microsoft.Extensions.Configuration.ConfigurationBuilder(), d).Build()).ChildQueryBatchSize;
    }

    [Test]
    public void Chunk_UsesTheConfiguredBatchSize()
    {
        var ids = Enumerable.Range(1, 2000).ToList();
        Assert.That(AuctionArchiveJob.Chunk(ids, Configured("250")).Count(), Is.EqualTo(8));
        Assert.That(AuctionArchiveJob.Chunk(ids, Configured("0")).Count(), Is.EqualTo(2000));
    }

    [Test]
    public void ChildQueryBatchSize_DefaultStaysBelowMariaDbIndexDiveLimit()
    {
        // Production EXPLAIN (MariaDB 10.11): eq_range_index_dive_limit = 200. From 200 IN values on, the optimizer
        // uses statistics and full-scans IX_UuId_SaveAuctionId (all NULL, 583M rows) -> command timeout.
        Assert.That(Configured(), Is.LessThan(200));
        Assert.That(Configured(), Is.GreaterThan(0));
    }

    [Test]
    public void Chunk_SplitsAPageIntoBatchesBelowTheDiveLimit_PreservingOrderAndCount()
    {
        var ids = Enumerable.Range(1, 2000).ToList();
        var chunks = AuctionArchiveJob.Chunk(ids, Configured()).ToList();

        Assert.That(chunks, Has.Count.EqualTo(20));
        Assert.That(chunks.All(c => c.Count <= Configured()));
        Assert.That(chunks.SelectMany(c => c), Is.EqualTo(ids));

        Assert.That(AuctionArchiveJob.Chunk(new List<int>(), Configured()), Is.Empty);

        var odd = AuctionArchiveJob.Chunk(Enumerable.Range(1, 101).ToList(), Configured()).ToList();
        Assert.That(odd.Select(c => c.Count), Is.EqualTo(new[] { 100, 1 }));
    }
}
