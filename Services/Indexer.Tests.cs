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
}