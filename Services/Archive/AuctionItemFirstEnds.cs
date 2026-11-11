using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Indexer.Archive;

/// <summary>
/// The distinct <c>ItemId</c>s of <c>Auctions</c> with the <c>End</c> of their first auction. <c>Auctions</c> has no index on <c>End</c> alone, only
/// <c>IX_Auctions_ItemId_End</c>, so every month long read walks this list and queries one ItemId at a time.
/// </summary>
internal static class AuctionItemFirstEnds
{
    /// <summary><c>SELECT ItemId, MIN(End) ... GROUP BY ItemId</c>: a loose index scan on <c>IX_Auctions_ItemId_End</c>, a few thousand rows.</summary>
    internal static async Task<IReadOnlyDictionary<int, DateTime>> ReadAsync(HypixelContext db, CancellationToken cancellationToken)
    {
        var rows = await Query(db).ToListAsync(cancellationToken).ConfigureAwait(false);
        return rows.ToDictionary(r => r.ItemId, r => r.First);
    }

    /// <summary>The query behind <see cref="ReadAsync"/>, exposed so tests can check its SQL shape.</summary>
    internal static IQueryable<ItemFirstEnd> Query(HypixelContext db)
        => db.Auctions.AsNoTracking().GroupBy(a => a.ItemId).Select(g => new ItemFirstEnd { ItemId = g.Key, First = g.Min(a => a.End) });

    internal sealed class ItemFirstEnd
    {
        public int ItemId { get; set; }
        public DateTime First { get; set; }
    }

    /// <summary>
    /// The ItemIds that can have auctions ending before <paramref name="monthEnd"/> (first auction ends before it), ascending. Throws when
    /// <paramref name="firstEnds"/> is empty: <c>Auctions</c> is never empty, so an empty list means the read went wrong (fail closed).
    /// </summary>
    internal static List<int> ItemsEndingBefore(IReadOnlyDictionary<int, DateTime> firstEnds, DateTime monthEnd)
    {
        if (firstEnds == null || firstEnds.Count == 0)
            throw new InvalidOperationException("the ItemId list of Auctions is empty, no month long read can be trusted");
        return firstEnds.Where(kv => kv.Value < monthEnd).Select(kv => kv.Key).OrderBy(id => id).ToList();
    }
}
