using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Indexer;

public class ActiveAuctionIndexService : IHostedService
{
    private const int RebuildBatchSize = 1000;
    private const int SyncBatchSize = 1000;
    private readonly ILogger<ActiveAuctionIndexService> logger;

    public ActiveAuctionIndexService(ILogger<ActiveAuctionIndexService> logger)
    {
        this.logger = logger;
    }

    public bool IsEnabled => ActiveAuctionsContext.IsConfigured;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!IsEnabled)
        {
            logger.LogInformation("Active auction lookup is disabled because ActiveAuctionsDBConnection is not configured");
            return;
        }

        await RebuildFromPrimaryDatabase(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public async Task SyncAuctions(IEnumerable<long> auctionUids, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        var uidList = auctionUids?.Where(uid => uid > 0).Distinct().ToArray() ?? [];
        if (uidList.Length == 0)
            return;

        foreach (var chunk in Batch(uidList, SyncBatchSize))
        {
            try
            {
                await SyncAuctionChunk(chunk, cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to sync {count} auctions into the active auction lookup", chunk.Count);
            }
        }
    }

    public async Task RemoveAuctions(IEnumerable<long> auctionUids, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled)
            return;

        var uidList = auctionUids?.Where(uid => uid > 0).Distinct().ToArray() ?? [];
        if (uidList.Length == 0)
            return;

        foreach (var chunk in Batch(uidList, SyncBatchSize))
        {
            try
            {
                await using var activeContext = new ActiveAuctionsContext();
                var existing = await ActiveQuery(activeContext)
                    .Where(auction => chunk.Contains(auction.UId))
                    .ToListAsync(cancellationToken);
                if (existing.Count == 0)
                    continue;

                activeContext.Auctions.RemoveRange(existing);
                await activeContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Failed to remove {count} auctions from the active auction lookup", chunk.Count);
            }
        }
    }

    private async Task RebuildFromPrimaryDatabase(CancellationToken cancellationToken)
    {
        try
        {
            logger.LogInformation("Rebuilding active auction lookup");
            await using (var activeContext = new ActiveAuctionsContext())
            {
                await activeContext.Database.EnsureCreatedAsync(cancellationToken);
                await ClearActiveAuctionTables(activeContext, cancellationToken);
            }

            var copied = 0;
            var lastId = 0;
            var now = DateTime.UtcNow;

            while (!cancellationToken.IsCancellationRequested)
            {
                await using var sourceContext = new HypixelContext();
                var auctions = await sourceContext.Auctions
                    .AsNoTracking()
                    .Where(auction => auction.Id > lastId && auction.End > now)
                    .OrderBy(auction => auction.Id)
                    .Take(RebuildBatchSize)
                    .Include(auction => auction.NBTLookup)
                    .Include(auction => auction.Enchantments)
                    .AsSplitQuery()
                    .ToListAsync(cancellationToken);

                if (auctions.Count == 0)
                    break;

                lastId = auctions[^1].Id;
                await AddActiveAuctions(auctions, cancellationToken);
                copied += auctions.Count;
            }

            logger.LogInformation("Rebuilt active auction lookup with {count} active auctions", copied);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Failed to rebuild active auction lookup");
        }
    }

    private async Task SyncAuctionChunk(IReadOnlyCollection<long> auctionUids, CancellationToken cancellationToken)
    {
        var now = DateTime.UtcNow;
        await using var sourceContext = new HypixelContext();
        var sourceAuctions = await sourceContext.Auctions
            .AsNoTracking()
            .Where(auction => auctionUids.Contains(auction.UId) && auction.End > now)
            .Include(auction => auction.NBTLookup)
            .Include(auction => auction.Enchantments)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        await using var activeContext = new ActiveAuctionsContext();
        var existing = await ActiveQuery(activeContext)
            .Where(auction => auctionUids.Contains(auction.UId))
            .ToListAsync(cancellationToken);

        if (existing.Count > 0)
        {
            activeContext.Auctions.RemoveRange(existing);
            await activeContext.SaveChangesAsync(cancellationToken);
        }

        if (sourceAuctions.Count == 0)
            return;

        activeContext.Auctions.AddRange(sourceAuctions.Select(CloneForActiveLookup));
        await activeContext.SaveChangesAsync(cancellationToken);
    }

    private async Task AddActiveAuctions(IReadOnlyCollection<SaveAuction> auctions, CancellationToken cancellationToken)
    {
        if (auctions.Count == 0)
            return;

        await using var activeContext = new ActiveAuctionsContext();
        activeContext.Auctions.AddRange(auctions.Select(CloneForActiveLookup));
        await activeContext.SaveChangesAsync(cancellationToken);
    }

    private static IQueryable<SaveAuction> ActiveQuery(ActiveAuctionsContext context)
    {
        return context.Auctions
            .Include(auction => auction.NBTLookup)
            .Include(auction => auction.Enchantments)
            .AsSplitQuery();
    }

    private static async Task ClearActiveAuctionTables(ActiveAuctionsContext context, CancellationToken cancellationToken)
    {
        await context.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 0", cancellationToken);
        try
        {
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE `NBTLookups`", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE `Enchantment`", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE `Bids`", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE `UuId`", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE `NbtData`", cancellationToken);
            await context.Database.ExecuteSqlRawAsync("TRUNCATE TABLE `Auctions`", cancellationToken);
        }
        finally
        {
            await context.Database.ExecuteSqlRawAsync("SET FOREIGN_KEY_CHECKS = 1", cancellationToken);
        }
    }

    private static SaveAuction CloneForActiveLookup(SaveAuction source)
    {
        return new SaveAuction
        {
            Uuid = source.Uuid,
            Claimed = source.Claimed,
            Count = source.Count,
            StartingBid = source.StartingBid,
            Tag = source.Tag ?? string.Empty,
            ItemName = source.ItemName,
            Start = source.Start,
            End = source.End,
            AuctioneerId = source.AuctioneerId,
            ProfileId = source.ProfileId,
            HighestBidAmount = source.HighestBidAmount,
            AnvilUses = source.AnvilUses,
            ItemCreatedAt = source.ItemCreatedAt,
            Reforge = source.Reforge,
            Category = source.Category,
            Tier = source.Tier,
            Bin = source.Bin,
            SellerId = source.SellerId,
            ItemId = source.ItemId,
            UId = source.UId,
            NBTLookup = source.NBTLookup?
                .GroupBy(lookup => lookup.KeyId)
                .Select(group => new NBTLookup { KeyId = group.Key, Value = group.First().Value })
                .ToArray() ?? [],
            Enchantments = source.Enchantments?
                .Select(enchantment => new Enchantment
                {
                    Type = enchantment.Type,
                    Level = enchantment.Level,
                    ItemType = enchantment.ItemType
                })
                .ToList() ?? []
        };
    }

    private static IEnumerable<IReadOnlyCollection<T>> Batch<T>(IReadOnlyCollection<T> values, int size)
    {
        for (var index = 0; index < values.Count; index += size)
        {
            yield return values.Skip(index).Take(size).ToArray();
        }
    }
}