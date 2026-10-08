using System.Linq;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Indexer;

/// <summary>Thin forwarder to <see cref="PlayerOptOut"/> in the core library.</summary>
internal static class PermanentAnonymization
{
    internal static string[] LegacyPlayerUuids => PlayerOptOut.LegacyPlayerUuids;
    internal static string[] PlayerUuids => PlayerOptOut.PlayerUuids;
    internal const string AnonymousUuidPrefix = PlayerOptOut.AnonymousUuidPrefix;
    internal static bool IsAnonymousUuid(string uuid) => PlayerOptOut.IsAnonymousUuid(uuid);
    internal static bool IsProtectedPlayer(string uuid) => PlayerOptOut.IsOptedOut(uuid);
    internal static void Apply(SaveAuction auction) => PlayerOptOut.Mask(auction);
    internal static void Anonymize(SaveAuction auction) => PlayerOptOut.Anonymize(auction);
    internal static void Anonymize(SaveBids bid) => PlayerOptOut.Anonymize(bid);

    /// <summary>
    /// Read time scrub of an already loaded auction. Returns true if anything changed.
    /// With a context the removed CoopMembers/ClaimedBids rows are deleted (removing them from the list would only null the foreign key).
    /// </summary>
    internal static bool MaskLoaded(SaveAuction auction, HypixelContext db = null)
    {
        string State() => string.Join('|', auction.AuctioneerId, auction.SellerId, auction.ProfileId,
            string.Join(',', auction.Bids?.Select(b => b.Bidder + b.BidderId + b.ProfileId) ?? []),
            auction.CoopMembers?.Count, auction.ClaimedBids?.Count);
        var before = State();
        var dropped = (auction.CoopMembers ?? []).Concat(auction.ClaimedBids ?? []).Where(m => PlayerOptOut.IsOptedOut(m.value)).ToList();
        PlayerOptOut.Mask(auction);
        if (db != null && dropped.Count > 0)
            db.RemoveRange(dropped);
        return State() != before;
    }
}
