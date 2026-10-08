using System.Linq;
using Coflnet.Sky.Core;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer;

[NonParallelizable]
public class PermanentAnonymizationTests
{
    private const string Other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static string Target => PermanentAnonymization.LegacyPlayerUuids[0];

    [SetUp]
    public void ResetMemory() => PlayerOptOut.ResetMemory();

    [Test]
    public void LegacyDefaultsAreProtectedBeforeLoadAndUuidFormatDoesNotMatter()
    {
        Assert.That(PermanentAnonymization.IsProtectedPlayer("F3C19FB5-3EA9-40F3-921E-90FAAB8E2B30"), Is.True);
        Assert.That(PermanentAnonymization.IsProtectedPlayer(Other), Is.False);
        Assert.That(PermanentAnonymization.IsProtectedPlayer(null), Is.False);
        Assert.That(PermanentAnonymization.PlayerUuids, Is.EquivalentTo(PermanentAnonymization.LegacyPlayerUuids));
    }

    [Test]
    public void IncomingDataClearsProtectedProfileAndAffiliationsButKeepsOtherPlayers()
    {
        var auction = new SaveAuction
        {
            AuctioneerId = Target, SellerId = 42, ProfileId = Target,
            Bids = [new SaveBids { Bidder = Target, BidderId = 42, ProfileId = Target },
                    new SaveBids { Bidder = Other, BidderId = 43, ProfileId = Other }],
            CoopMembers = [new UuId(Target), new UuId(Other)],
            ClaimedBids = [new UuId(Target), new UuId(Other)]
        };
        PermanentAnonymization.Apply(auction);
        Assert.Multiple(() =>
        {
            Assert.That(auction.SellerId, Is.Zero);
            Assert.That(auction.AuctioneerId, Is.Not.EqualTo(Target));
            Assert.That(PermanentAnonymization.IsAnonymousUuid(auction.AuctioneerId), Is.True);
            Assert.That(auction.ProfileId, Is.Null);
            Assert.That(auction.Bids[0].BidderId, Is.Zero);
            Assert.That(auction.Bids[0].ProfileId, Is.Null);
            Assert.That(auction.Bids[1].Bidder, Is.EqualTo(Other));
            Assert.That(auction.Bids[1].ProfileId, Is.EqualTo(Other));
            Assert.That(auction.CoopMembers, Has.Count.EqualTo(1));
            Assert.That(auction.CoopMembers[0].value, Is.EqualTo(Other));
            Assert.That(auction.ClaimedBids, Has.Count.EqualTo(1));
            Assert.That(auction.ClaimedBids[0].value, Is.EqualTo(Other));
        });
    }

    [Test]
    public void MaskLoadedRemovesOptedOutCoopClaimedAndProfileAndReportsChange()
    {
        var auction = new SaveAuction
        {
            AuctioneerId = Other, ProfileId = Target,
            Bids = [new SaveBids { Bidder = Other, ProfileId = Target }],
            CoopMembers = [new UuId(Target), new UuId(Other)],
            ClaimedBids = [new UuId(Target)]
        };
        Assert.That(PermanentAnonymization.MaskLoaded(auction), Is.True);
        Assert.That(auction.ProfileId, Is.Null);
        Assert.That(auction.Bids[0].ProfileId, Is.Null);
        Assert.That(auction.CoopMembers.Select(m => m.value), Is.EqualTo(new[] { Other }));
        Assert.That(auction.ClaimedBids, Is.Empty);
        Assert.That(auction.AuctioneerId, Is.EqualTo(Other));
    }

    [Test]
    public void MaskLoadedLeavesCleanAuctionUntouchedAndReportsNoChange()
    {
        var auction = new SaveAuction
        {
            AuctioneerId = Other, ProfileId = Other,
            Bids = [new SaveBids { Bidder = Other, ProfileId = Other }],
            CoopMembers = [new UuId(Other)], ClaimedBids = [new UuId(Other)]
        };
        Assert.That(PermanentAnonymization.MaskLoaded(auction), Is.False);
        Assert.That(auction.CoopMembers, Has.Count.EqualTo(1));
        Assert.That(auction.ClaimedBids, Has.Count.EqualTo(1));
        Assert.That(PermanentAnonymization.MaskLoaded(new SaveAuction { AuctioneerId = Other }), Is.False);
    }
}
