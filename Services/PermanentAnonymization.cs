using System;
using System.Linq;
using Coflnet.Sky.Core;

namespace Coflnet.Sky.Indexer;

internal static class PermanentAnonymization
{
    internal const string PlayerUuid = "f3c19fb53ea940f3921e90faab8e2b30";

    internal static bool IsProtectedPlayer(string uuid)
    {
        return string.Equals(uuid?.Replace("-", ""), PlayerUuid, StringComparison.OrdinalIgnoreCase);
    }

    internal static void Apply(SaveAuction auction)
    {
        if (IsProtectedPlayer(auction.AuctioneerId))
            Anonymize(auction);

        foreach (var bid in auction.Bids?.Where(bid => IsProtectedPlayer(bid.Bidder)) ?? [])
            Anonymize(bid);
    }

    internal static void Anonymize(SaveAuction auction)
    {
        auction.SellerId = 0;
        auction.AuctioneerId = AnonymousUuid();
    }

    internal static void Anonymize(SaveBids bid)
    {
        bid.BidderId = 0;
        bid.Bidder = AnonymousUuid();
    }

    private static string AnonymousUuid()
    {
        return Random.Shared.Next(1, 254).ToString("X2").PadLeft(32, '0');
    }
}
