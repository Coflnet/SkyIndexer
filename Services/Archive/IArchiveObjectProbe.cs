using System.Threading;
using Coflnet.Sky.Core.Archive;
using System.Threading.Tasks;

namespace Coflnet.Sky.Indexer.Archive;

/// <summary>
/// Raw object access the retention worker needs for the backup-receipt gate: the receipt is plain JSON (written by the NAS mirror
/// tool, not Brotli/MessagePack like the archive objects) and the live ETag of a block is read with a HEAD request.
/// Implemented by <see cref="S3AuctionBlobStore"/>.
/// </summary>
public interface IArchiveObjectProbe
{
    /// <summary>The full object body as UTF-8 text by full bucket key; null when it does not exist.</summary>
    Task<string> ReadTextAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>The current ETag (HEAD, as returned, usually quoted) by full bucket key; null when it does not exist.</summary>
    Task<string> GetETagAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// An uncached block read that also returns the ETag of the GET response itself (not of an earlier HEAD), so the caller can prove
    /// that the bytes it decoded are the object it compared. Block null (and ETag null) when the part does not exist.
    /// </summary>
    Task<(ArchivedAuctionBlock Block, string ETag)> ReadBlockWithETagAsync(string tag, int month, int part, CancellationToken cancellationToken = default);
}
