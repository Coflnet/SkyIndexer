using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core.Archive;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Indexer.Archive;

/// <summary>
/// Write protection for months the retention worker has (partly) deleted from MariaDB. Once <c>archived_months.deleted_at</c> is set or
/// <c>deleted_rows &gt; 0</c> (tag row, or the month sentinel <c>tag_id = 0</c>), the R2 blobs and the index rows are the only copy of the
/// month, so no writer may build a new version of it from the (now incomplete) MariaDB rows: the archive job would overwrite complete parts
/// with partial ones. Rewrites of EXISTING parts by <see cref="ArchivePlayerErasure"/> stay allowed (privacy); they never create parts.
/// All reads are primary key lookups and are re-run after a statement timeout (the primary is latency bound while the archive job writes).
/// </summary>
internal static class ArchiveDeletedMonthGuard
{
    /// <summary>Writes refused because the month is (partly) deleted, by writer (<c>archive_job</c>, <c>scylla_import</c>).</summary>
    public static readonly Prometheus.Counter Refused = Prometheus.Metrics.CreateCounter(
        "sky_indexer_archive_refused_deleted_month", "Archive writes refused because the tag-month or month has deleted_at/deleted_rows set",
        new Prometheus.CounterConfiguration { LabelNames = new[] { "writer" } });

    /// <summary>Tag ids never change once assigned, so a found id is cached for the process lifetime.</summary>
    private static readonly ConcurrentDictionary<string, ushort> TagIds = new(StringComparer.Ordinal);

    /// <summary>True when the row marks deleted data (<c>deleted_at</c> set or <c>deleted_rows &gt; 0</c>).</summary>
    public static bool IsDeleted(ArchivedMonth row) => row != null && (row.DeletedAt != null || row.DeletedRows > 0);

    /// <summary>The month sentinel is marked deleted.</summary>
    public static Task<bool> IsMonthDeletedAsync(int month, CancellationToken cancellationToken)
        => RetryAsync(() => ReadIsDeletedAsync(new ushort[] { 0 }, month, cancellationToken), AuctionArchiveJob.PrimaryWriteRetryDelays, cancellationToken);

    /// <summary>The month sentinel or the row of the tag-month is marked deleted (primary key lookups of <c>(0, month)</c> and <c>(tagId, month)</c>).</summary>
    public static Task<bool> IsTagMonthDeletedAsync(string tag, int month, CancellationToken cancellationToken)
        => RetryAsync(() => ReadTagMonthDeletedAsync(tag, month, cancellationToken), AuctionArchiveJob.PrimaryWriteRetryDelays, cancellationToken);

    private static async Task<bool> ReadTagMonthDeletedAsync(string tag, int month, CancellationToken cancellationToken)
    {
        var tagId = await FindTagIdAsync(tag, cancellationToken).ConfigureAwait(false);
        // a tag without an auction_tags row has no archived_months row either, only the sentinel can mark its month
        var ids = tagId == null ? new ushort[] { 0 } : new ushort[] { 0, tagId.Value };
        return await ReadIsDeletedAsync(ids, month, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ushort?> FindTagIdAsync(string tag, CancellationToken cancellationToken)
    {
        if (TagIds.TryGetValue(tag, out var cached))
            return cached;
        using var db = new ArchiveContext();
        var id = await TagIdQuery(db, tag).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (id != null)
            TagIds[tag] = id.Value;
        return id;
    }

    private static async Task<bool> ReadIsDeletedAsync(ushort[] tagIds, int month, CancellationToken cancellationToken)
    {
        using var db = new ArchiveContext();
        return await DeletedRowsQuery(db, tagIds, ArchiveMonths.ToIndex(month)).AnyAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The deleted rows among the <c>(tag_id, month)</c> primary key points: <c>tag_id IN (...) AND month = ?</c>.</summary>
    internal static IQueryable<ArchivedMonth> DeletedRowsQuery(ArchiveContext db, ushort[] tagIds, ushort monthIndex)
        => db.ArchivedMonths.AsNoTracking().Where(m => tagIds.Contains(m.TagId) && m.Month == monthIndex && (m.DeletedAt != null || m.DeletedRows > 0));

    /// <summary>The unique-index lookup of a tag's id: <c>auction_tags WHERE tag = ?</c>.</summary>
    internal static IQueryable<ushort?> TagIdQuery(ArchiveContext db, string tag)
        => db.AuctionTags.AsNoTracking().Where(t => t.Tag == tag).Select(t => (ushort?)t.Id);

    /// <summary>Runs a read, re-running it after <paramref name="delays"/> while it fails with a statement timeout, deadlock or lock wait timeout.</summary>
    internal static async Task<T> RetryAsync<T>(Func<Task<T>> read, TimeSpan[] delays, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await read().ConfigureAwait(false);
            }
            catch (Exception ex) when (AuctionArchiveJob.IsRetryableWriteError(ex) && !cancellationToken.IsCancellationRequested && attempt < delays.Length)
            {
                await Task.Delay(delays[attempt], cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
