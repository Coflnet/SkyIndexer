using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace Coflnet.Sky.Indexer.Archive
{
    /// <summary>One part an item produced for a tag: the part number inside the tag-month and its SHA-256.</summary>
    public record CheckpointPart(int Part, string Sha256);

    /// <summary>What one item contributed to one tag-month (deltas of <c>TagMonthRun</c>).</summary>
    public record CheckpointTagDelta(List<CheckpointPart> Parts, int Rows, int SoldRows, int IndexRows, int IndexBidderRows, long BidsWithoutBidder);

    /// <summary>Finished ItemId-month: per tag the delta it added to the month's runs, empty for items without rows.</summary>
    public record ItemMonthCheckpoint(DateTime ArchivedAt, Dictionary<string, CheckpointTagDelta> Tags);

    /// <summary>
    /// Restart-progress of the archive job per ItemId-month. Implementations never throw: a failure only means a (partial) re-walk.
    /// </summary>
    public interface IArchiveItemCheckpoints
    {
        /// <summary>All finished items of the month by ItemId; empty when none or on failure.</summary>
        Task<IReadOnlyDictionary<int, ItemMonthCheckpoint>> LoadAsync(int month, CancellationToken cancellationToken);
        /// <summary>Record a finished item and refresh the TTL of the month.</summary>
        Task SaveAsync(int month, int itemId, ItemMonthCheckpoint checkpoint, TimeSpan ttl, CancellationToken cancellationToken);
        /// <summary>Forget the month (after its sentinel was written).</summary>
        Task ClearAsync(int month, CancellationToken cancellationToken);
    }

    /// <summary>No checkpoints (used when no store is configured).</summary>
    public sealed class NoArchiveItemCheckpoints : IArchiveItemCheckpoints
    {
        public Task<IReadOnlyDictionary<int, ItemMonthCheckpoint>> LoadAsync(int month, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyDictionary<int, ItemMonthCheckpoint>>(new Dictionary<int, ItemMonthCheckpoint>());
        public Task SaveAsync(int month, int itemId, ItemMonthCheckpoint checkpoint, TimeSpan ttl, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ClearAsync(int month, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    /// <summary>
    /// Redis hash per month (<c>sky-indexer:archive:itemmonths:{yyyyMM}</c>), field = ItemId, value = JSON of <see cref="ItemMonthCheckpoint"/>.
    /// Every Redis call is guarded: errors log a warning and count <c>sky_indexer_archive_checkpoint_errors</c>.
    /// </summary>
    public class RedisArchiveItemCheckpoints : IArchiveItemCheckpoints
    {
        private static readonly Prometheus.Counter checkpointErrors = Prometheus.Metrics.CreateCounter("sky_indexer_archive_checkpoint_errors", "Redis failures while reading or writing archive item checkpoints");
        private readonly IConnectionMultiplexer redis;
        private readonly ILogger<RedisArchiveItemCheckpoints> logger;

        public RedisArchiveItemCheckpoints(IConnectionMultiplexer redis, ILogger<RedisArchiveItemCheckpoints> logger)
        {
            this.redis = redis;
            this.logger = logger;
        }

        /// <summary>Total Redis failures so far (<c>sky_indexer_archive_checkpoint_errors</c>).</summary>
        public static double ErrorCount => checkpointErrors.Value;

        /// <summary>Redis key of a month's hash.</summary>
        public static string Key(int month) => $"sky-indexer:archive:itemmonths:{month}";

        /// <inheritdoc/>
        public async Task<IReadOnlyDictionary<int, ItemMonthCheckpoint>> LoadAsync(int month, CancellationToken cancellationToken)
        {
            var result = new Dictionary<int, ItemMonthCheckpoint>();
            try
            {
                foreach (var (field, value) in await HashGetAllAsync(Key(month)).ConfigureAwait(false))
                {
                    var checkpoint = JsonSerializer.Deserialize<ItemMonthCheckpoint>(value);
                    if (checkpoint?.Tags == null || !int.TryParse(field, out var itemId))
                        throw new FormatException($"Invalid checkpoint field {field}");
                    result[itemId] = checkpoint;
                }
            }
            catch (Exception ex)
            {
                Failed(ex, "load", month);
                return new Dictionary<int, ItemMonthCheckpoint>();
            }
            return result;
        }

        /// <inheritdoc/>
        public async Task SaveAsync(int month, int itemId, ItemMonthCheckpoint checkpoint, TimeSpan ttl, CancellationToken cancellationToken)
        {
            try
            {
                var key = Key(month);
                await HashSetWithExpiryAsync(key, itemId.ToString(), JsonSerializer.Serialize(checkpoint), ttl).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Failed(ex, "save", month);
            }
        }

        /// <inheritdoc/>
        public async Task ClearAsync(int month, CancellationToken cancellationToken)
        {
            try
            {
                await DeleteAsync(Key(month)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Failed(ex, "clear", month);
            }
        }

        private void Failed(Exception ex, string action, int month)
        {
            checkpointErrors.Inc();
            logger.LogWarning(ex, "Archive checkpoint {Action} for {Month} failed; continuing without it", action, month);
        }

        // ---------------------------------------------------------------- Redis seams

        protected virtual async Task<IReadOnlyList<(string field, string value)>> HashGetAllAsync(string key)
            => (await redis.GetDatabase().HashGetAllAsync(key).ConfigureAwait(false)).Select(e => (e.Name.ToString(), e.Value.ToString())).ToList();

        /// <summary>HSET and EXPIRE in one transaction, so the hash can never live without its TTL.</summary>
        protected virtual async Task HashSetWithExpiryAsync(string key, string field, string value, TimeSpan ttl)
        {
            var transaction = redis.GetDatabase().CreateTransaction();
            var set = transaction.HashSetAsync(key, field, value);
            var expire = transaction.KeyExpireAsync(key, ttl);
            if (!await transaction.ExecuteAsync().ConfigureAwait(false))
                throw new InvalidOperationException("Redis transaction for the archive checkpoint was not committed");
            await Task.WhenAll(set, expire).ConfigureAwait(false);
        }

        protected virtual Task DeleteAsync(string key) => redis.GetDatabase().KeyDeleteAsync(key);
    }
}
