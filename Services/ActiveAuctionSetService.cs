using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Confluent.Kafka;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Prometheus;

namespace Coflnet.Sky.Indexer;

/// <summary>
/// Owns the in-memory set of all currently active auctions, keeps it up to date from the indexer feed
/// and serves it over <c>/active/*</c>.
/// </summary>
public class ActiveAuctionSetService : IHostedService
{
    private const int MaxReconcilePerCall = 5000;
    private static readonly TimeSpan LoadWindow = TimeSpan.FromDays(15);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(60);

    private static readonly Gauge CountGauge = Metrics.CreateGauge("sky_indexer_active_set_count", "Auctions in the active set");
    private static readonly Gauge ReadyGauge = Metrics.CreateGauge("sky_indexer_active_set_ready", "1 once the initial load finished");
    private static readonly Gauge LoadSeconds = Metrics.CreateGauge("sky_indexer_active_set_load_seconds", "Duration of the initial load");
    private static readonly Counter Upserts = Metrics.CreateCounter("sky_indexer_active_set_upserts_total", "Auctions upserted into the active set", "source");
    private static readonly Counter Removed = Metrics.CreateCounter("sky_indexer_active_set_removed_total", "Auctions removed from the active set", "reason");
    internal static readonly Counter SnapshotRebuilds = Metrics.CreateCounter("sky_indexer_active_set_snapshot_rebuilds_total", "Snapshot rebuilds of the active set");
    private static readonly Histogram RebuildSeconds = Metrics.CreateHistogram("sky_indexer_active_set_rebuild_seconds", "Duration of an active set snapshot rebuild",
        new HistogramConfiguration { Buckets = Histogram.ExponentialBuckets(0.005, 2, 12) });
    private static readonly Counter ReconcileFetched = Metrics.CreateCounter("sky_indexer_active_set_reconcile_fetched_total", "Auctions fetched from the db because the summary listed them but the set did not");

    private readonly ActiveAuctionDbLoader loader;
    private readonly ItemDetails itemDetails;
    private readonly ILogger<ActiveAuctionSetService> logger;
    private readonly IConfiguration config;
    private readonly CancellationTokenSource stopping = new();
    private DateTime lastReconcile = DateTime.MinValue;
    private readonly List<Task> background = new();

    public ActiveAuctionSet Set { get; }

    /// <summary>Number of copies made for upserts, unchanged re-sightings must not add to it</summary>
    internal long ClonesMade => Interlocked.Read(ref clonesMade);
    private long clonesMade;

    public ActiveAuctionSetService(ActiveAuctionDbLoader loader, ItemDetails itemDetails, ILogger<ActiveAuctionSetService> logger, IConfiguration config = null, ActiveAuctionSet set = null)
    {
        this.loader = loader;
        this.itemDetails = itemDetails;
        this.logger = logger;
        this.config = config;
        Set = set ?? new ActiveAuctionSet();
        Set.OnRemoved += (_, reason) => Removed.WithLabels(reason.ToString()).Inc();
        Set.OnSnapshotRebuilt += duration =>
        {
            SnapshotRebuilds.Inc();
            RebuildSeconds.Observe(duration.TotalSeconds);
        };
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var token = stopping.Token;
        background.Add(Task.Run(() => LoadAsync(token)));
        background.Add(Task.Run(() => Every(TimeSpan.FromSeconds(10), () => { Set.ExpireEnded(DateTime.UtcNow); CountGauge.Set(Set.Count); }, token)));
        background.Add(Task.Run(() => Every(TimeSpan.FromSeconds(2), () => Set.RebuildSnapshotIfDirty(), token)));
        background.Add(Task.Run(() => Every(TimeSpan.FromSeconds(60), () => Set.PurgeTombstones(DateTime.UtcNow), token)));
        if (config != null && !string.IsNullOrEmpty(config["TOPICS:MISSING_AUCTION"]))
            background.Add(Task.Run(() => ConsumeCancelled(token)));
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        stopping.Cancel();
        return Task.CompletedTask;
    }

    private async Task Every(TimeSpan interval, Action action, CancellationToken token)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(token))
            {
                try { action(); }
                catch (Exception e) { logger.LogError(e, "active set maintenance failed"); }
            }
        }
        catch (OperationCanceledException) { }
    }

    public async Task LoadAsync(CancellationToken ct)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var now = DateTime.UtcNow;
            logger.LogInformation("loading active auction set");
            var lower = await loader.FindLowerBoundIdAsync(now - LoadWindow, ct);
            await foreach (var auction in loader.LoadActiveAsync(lower, now, ct))
            {
                EnsureItemId(auction);
                if (Set.Upsert(auction, "db"))
                    Upserts.WithLabels("db").Inc();
            }
            Set.MarkReady();
            Set.RebuildSnapshotIfDirty();
            ReadyGauge.Set(1);
            CountGauge.Set(Set.Count);
            LoadSeconds.Set(watch.Elapsed.TotalSeconds);
            logger.LogInformation("active set loaded {count} auctions from id {lower} in {seconds}s", Set.Count, lower, watch.Elapsed.TotalSeconds);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            logger.LogError(e, "loading the active auction set failed");
        }
    }

    private void EnsureItemId(SaveAuction auction)
    {
        if (auction.ItemId != 0 || itemDetails == null || string.IsNullOrEmpty(auction.Tag))
            return;
        try
        {
            auction.ItemId = itemDetails.GetItemIdForTag(auction.Tag, false);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "could not resolve item id for {tag}", auction.Tag);
        }
    }

    /// <summary>
    /// Adds or refreshes auctions. Works on copies, the passed objects stay untouched (EF still needs them).
    /// The cheap freshness/change check runs before copying.
    /// </summary>
    public void UpsertActive(IEnumerable<SaveAuction> auctions, string source)
    {
        if (auctions == null)
            return;
        var now = DateTime.UtcNow;
        foreach (var auction in auctions)
        {
            if (auction == null || auction.End <= now)
                continue;
            try
            {
                if (!Set.WouldAccept(auction))
                    continue; // unchanged re-sighting, not worth a copy
                Interlocked.Increment(ref clonesMade);
                var copy = CloneForActiveSet(auction);
                EnsureItemId(copy);
                if (Set.Upsert(copy, source))
                    Upserts.WithLabels(source).Inc();
            }
            catch (Exception e)
            {
                logger.LogError(e, "failed to add {uid} to the active set", auction.UId);
            }
        }
        CountGauge.Set(Set.Count);
    }

    public void RemoveActive(IEnumerable<long> uids, RemoveReason reason)
    {
        if (uids == null)
            return;
        foreach (var uid in uids)
            Set.Remove(uid, reason);
        CountGauge.Set(Set.Count);
    }

    /// <summary>
    /// Loads auctions the summary lists as active but the set does not know (at most 5000 per call).
    /// </summary>
    public async Task ReconcileWithSummaryAsync(IReadOnlyCollection<long> summaryUids, CancellationToken ct)
    {
        if (summaryUids == null || summaryUids.Count == 0 || !Set.IsReady)
            return;
        var missing = summaryUids.Where(uid => !Set.TryGet(uid, out _)).Take(MaxReconcilePerCall).ToList();
        if (missing.Count == 0)
            return;
        var loaded = await loader.LoadByUidsAsync(missing, DateTime.UtcNow, ct);
        ReconcileFetched.Inc(loaded.Count);
        foreach (var auction in loaded)
        {
            EnsureItemId(auction);
            if (Set.Upsert(auction, "reconcile"))
                Upserts.WithLabels("reconcile").Inc();
        }
        CountGauge.Set(Set.Count);
    }

    /// <summary>
    /// Rate limited wrapper used by the summary consumer. Returns false when skipped.
    /// </summary>
    public async Task<bool> TryReconcileWithSummaryAsync(IReadOnlyCollection<long> summaryUids, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (now - lastReconcile < ReconcileInterval)
            return false;
        lastReconcile = now;
        await ReconcileWithSummaryAsync(summaryUids, ct);
        return true;
    }

    public IReadOnlyCollection<long> ActiveUidsForSeller(string auctioneerId)
        => Set.BySeller(auctioneerId).Select(a => a.UId).ToArray();

    /// <summary>
    /// Ends cancelled auctions in the db (if still in the future) and removes them from the set.
    /// </summary>
    public async Task MarkCancelled(IEnumerable<SaveAuction> auctions)
    {
        var uids = auctions.Where(a => a != null).Select(a => a.UId).Where(u => u != 0).Distinct().ToList();
        if (uids.Count == 0)
            return;
        try
        {
            using var context = new HypixelContext();
            var now = DateTime.UtcNow;
            var toUpdate = await context.Auctions.Where(a => uids.Contains(a.UId) && a.End > now).ToListAsync();
            foreach (var item in toUpdate)
            {
                item.End = now;
                context.Update(item);
            }
            if (toUpdate.Count > 0)
                await context.SaveChangesAsync();
        }
        catch (Exception e)
        {
            logger.LogError(e, "failed to end {count} cancelled auctions in the db", uids.Count);
        }
        RemoveActive(uids, RemoveReason.Cancelled);
    }

    private async Task ConsumeCancelled(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Kafka.KafkaConsumer.ConsumeBatch<SaveAuction>(config, config["TOPICS:MISSING_AUCTION"], MarkCancelled,
                    token, "sky-indexer-cancelled", 100, AutoOffsetReset.Latest);
            }
            catch (OperationCanceledException) { }
            catch (Exception e)
            {
                logger.LogError(e, "cancelled auction consumer failed, restarting");
                try { await Task.Delay(TimeSpan.FromSeconds(10), token); } catch (OperationCanceledException) { }
            }
        }
    }

    /// <summary>
    /// Copy that can be normalised by the set without affecting the original object (which EF may still track).
    /// </summary>
    public static SaveAuction CloneForActiveSet(SaveAuction source)
    {
        var copy = new SaveAuction(source)
        {
            Tag = source.Tag ?? string.Empty,
            Bids = source.Bids?.Select(b => new SaveBids
            {
                AuctionId = b.AuctionId,
                Bidder = b.Bidder,
                ProfileId = b.ProfileId,
                Amount = b.Amount,
                Timestamp = b.Timestamp,
                BidderId = b.BidderId
            }).ToList() ?? new List<SaveBids>(),
            Enchantments = source.Enchantments?.Select(e => new Enchantment
            {
                Type = e.Type,
                Level = e.Level,
                ItemType = e.ItemType
            }).ToList() ?? new List<Enchantment>(),
            NBTLookup = null,
            CoopMembers = null,
            ClaimedBids = null
        };
        var flat = source.FlatenedNBT;
        copy.FlatenedNBT = flat == null ? null : new Dictionary<string, string>(flat);
        copy.Context = source.Context == null ? null : new Dictionary<string, string>(source.Context);
        return copy;
    }
}
