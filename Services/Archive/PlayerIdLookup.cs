using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Indexer.Archive
{
    /// <summary>
    /// Bulk <c>Players.UuId</c> to <c>Players.Id</c> resolution shared by the Scylla import, the retention seller pass and the retention batches: sorted, distinct
    /// uuids in chunks of at most <see cref="ChunkSize"/> primary key lookups (MariaDB stops index dives at 200 IN values).
    /// </summary>
    internal static class PlayerIdLookup
    {
        /// <summary>Uuids per <c>Players</c> lookup.</summary>
        public const int ChunkSize = 100;

        /// <summary>Resolve <paramref name="uuids"/> through <paramref name="chunkLookup"/>; unknown uuids are absent from the result.</summary>
        public static async Task<Dictionary<string, int>> ResolveAsync(IEnumerable<string> uuids,
            Func<IReadOnlyList<string>, CancellationToken, Task<IReadOnlyDictionary<string, int>>> chunkLookup, CancellationToken cancellationToken)
        {
            var sorted = uuids.Distinct(StringComparer.Ordinal).OrderBy(u => u, StringComparer.Ordinal).ToList();
            var ids = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var offset = 0; offset < sorted.Count; offset += ChunkSize)
            {
                var chunk = sorted.GetRange(offset, Math.Min(ChunkSize, sorted.Count - offset));
                foreach (var (uuid, id) in await chunkLookup(chunk, cancellationToken).ConfigureAwait(false))
                    ids[uuid] = id;
            }
            return ids;
        }

        /// <summary>The production chunk lookup: <c>Players</c> by <c>UuId</c> (32-char hex).</summary>
        public static async Task<IReadOnlyDictionary<string, int>> QueryPlayersAsync(IReadOnlyList<string> uuids, CancellationToken cancellationToken)
        {
            using var db = new HypixelContext();
            var list = uuids.ToList();
            var rows = await db.Players.AsNoTracking().Where(p => list.Contains(p.UuId)).Select(p => new { p.UuId, p.Id }).ToListAsync(cancellationToken).ConfigureAwait(false);
            var result = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var r in rows)
                result[r.UuId] = r.Id;
            return result;
        }
    }
}
