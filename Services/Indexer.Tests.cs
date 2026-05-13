using NUnit.Framework;
using Coflnet.Sky.Core;
using System.Collections.Generic;

namespace Coflnet.Sky.Indexer;
public class IndexerTests
{
    [Test]
    public void Test1()
    {
        var activeAuction = new SaveAuction() {HighestBidAmount = 100};
        var dbAuction = new SaveAuction();
        Indexer.UpdateHighestBid(activeAuction, dbAuction);
        Assert.That(dbAuction.HighestBidAmount, Is.EqualTo(100));
    }
    [Test]
    public void GetFromBid()
    {
        var activeAuction = new SaveAuction() {Bids = new () {new SaveBids() {Amount = 100}}};
        var dbAuction = new SaveAuction();
        Indexer.UpdateHighestBid(activeAuction, dbAuction);
        Assert.That(dbAuction.HighestBidAmount, Is.EqualTo(100));
    }

    [Test]
    public void DeduplicateNbtLookupsKeepsFirstValuePerKey()
    {
        var deduplicated = Indexer.DeduplicateNbtLookups(new[]
        {
            new NBTLookup(229, 1),
            new NBTLookup(229, 2),
            new NBTLookup(42, 3),
            new NBTLookup(42, 4)
        });

        Assert.That(deduplicated, Has.Length.EqualTo(2));
        Assert.Multiple(() =>
        {
            Assert.That(deduplicated[0].KeyId, Is.EqualTo(229));
            Assert.That(deduplicated[0].Value, Is.EqualTo(1));
            Assert.That(deduplicated[1].KeyId, Is.EqualTo(42));
            Assert.That(deduplicated[1].Value, Is.EqualTo(3));
        });
    }
}