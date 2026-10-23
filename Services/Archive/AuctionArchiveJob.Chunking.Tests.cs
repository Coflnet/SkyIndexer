using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

public class AuctionArchiveJobChunkingTests
{
    [Test]
    public void ChildQueryBatchSize_StaysBelowMariaDbIndexDiveLimit()
    {
        // Production EXPLAIN (MariaDB 10.11): eq_range_index_dive_limit = 200. From 200 IN values on, the optimizer
        // uses statistics and full-scans IX_UuId_SaveAuctionId (all NULL, 583M rows) -> command timeout.
        Assert.That(AuctionArchiveJob.ChildQueryBatchSize, Is.LessThan(200));
        Assert.That(AuctionArchiveJob.ChildQueryBatchSize, Is.GreaterThan(0));
    }

    [Test]
    public void Chunk_SplitsAPageIntoBatchesBelowTheDiveLimit_PreservingOrderAndCount()
    {
        var ids = Enumerable.Range(1, 2000).ToList();
        var chunks = AuctionArchiveJob.Chunk(ids, AuctionArchiveJob.ChildQueryBatchSize).ToList();

        Assert.That(chunks, Has.Count.EqualTo(20));
        Assert.That(chunks.All(c => c.Count <= AuctionArchiveJob.ChildQueryBatchSize));
        Assert.That(chunks.SelectMany(c => c), Is.EqualTo(ids));

        Assert.That(AuctionArchiveJob.Chunk(new List<int>(), AuctionArchiveJob.ChildQueryBatchSize), Is.Empty);

        var odd = AuctionArchiveJob.Chunk(Enumerable.Range(1, 101).ToList(), AuctionArchiveJob.ChildQueryBatchSize).ToList();
        Assert.That(odd.Select(c => c.Count), Is.EqualTo(new[] { 100, 1 }));
    }
}
