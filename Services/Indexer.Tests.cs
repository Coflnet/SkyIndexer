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

    [TestCase("Ekwav", "Ekwav")]
    [TestCase("abc_123", "abc_123")]
    [TestCase("abcdefghijklmnop", "abcdefghijklmnop")]
    [TestCase("  Ekwav\r\n", "Ekwav")]
    [TestCase("", null)]
    [TestCase("abcdefghijklmnopq", null)]
    [TestCase("rate limit exceeded", null)]
    [TestCase("{\"error\":\"blocked\"}", null)]
    [TestCase("invalid-name", null)]
    [TestCase("Ekwav!", null)]
    [TestCase(null, null)]
    public void NormalizeMinecraftUsernameRejectsInvalidProviderResponses(string response, string expected)
    {
        Assert.That(Coflnet.Sky.Core.Program.NormalizeMinecraftUsername(response), Is.EqualTo(expected));
    }

    [Test]
    public void ProtectedPlayerIsRemovedFromIncomingAuctionData()
    {
        var auction = new SaveAuction
        {
            AuctioneerId = PermanentAnonymization.PlayerUuid,
            SellerId = 42,
            Bids = [new SaveBids { Bidder = PermanentAnonymization.PlayerUuid, BidderId = 42 }]
        };

        PermanentAnonymization.Apply(auction);

        Assert.Multiple(() =>
        {
            Assert.That(auction.AuctioneerId, Is.Not.EqualTo(PermanentAnonymization.PlayerUuid));
            Assert.That(auction.AuctioneerId, Has.Length.EqualTo(32));
            Assert.That(auction.SellerId, Is.Zero);
            Assert.That(auction.Bids[0].Bidder, Is.Not.EqualTo(PermanentAnonymization.PlayerUuid));
            Assert.That(auction.Bids[0].Bidder, Has.Length.EqualTo(32));
            Assert.That(auction.Bids[0].BidderId, Is.Zero);
        });
    }

    [TestCase("f3c19fb53ea940f3921e90faab8e2b30")]
    [TestCase("F3C19FB5-3EA9-40F3-921E-90FAAB8E2B30")]
    public void ProtectedPlayerUuidMatchingIsFormatIndependent(string uuid)
    {
        Assert.That(PermanentAnonymization.IsProtectedPlayer(uuid), Is.True);
    }
}
