using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Coflnet.Sky.Core;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Prometheus;
using System.Collections.Generic;
using fNbt.Tags;

namespace Coflnet.Sky.Indexer
{
    public class Numberer : BackgroundService
    {
        private readonly ActivitySource activitySource;
        private readonly ILogger<Numberer> logger;
        readonly Gauge bidsWithoutId = Metrics.CreateGauge("sky_indexer_bids_without_id", "Number of Bids that don't yet have a player id");
        readonly Gauge auctionsWithoutId = Metrics.CreateGauge("sky_indexer_auctions_without_id", "Number of Auctions that don't yet have a player id");
        readonly Counter auctionsNumbered = Metrics.CreateCounter("sky_indexer_auctions_numbered", "Number of Auctions that have been numbered");
        readonly Counter bidsNumbered = Metrics.CreateCounter("sky_indexer_bids_numbered", "Number of Bids that have been numbered");
        readonly Counter playersNumbered = Metrics.CreateCounter("sky_indexer_players_numbered", "Number of Players that have been numbered");
        readonly Counter doublePlayersReset = Metrics.CreateCounter("sky_indexer_double_players_reset", "Number of Players that have been reset");

        public Numberer(ActivitySource activitySource, ILogger<Numberer> logger)
        {
            this.activitySource = activitySource;
            this.logger = logger;
        }

        protected override async Task ExecuteAsync(System.Threading.CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    using var activity = activitySource.StartActivity("Numberer");
                    await NumberUsers();
                }
                catch (Exception e)
                {
                    logger.LogError(e, "Error while numbering");
                }
                await Task.Delay(1000, stoppingToken);
            }
        }
        public async Task NumberUsers()
        {
            Task bidNumberTask = null;
            using (var context = new HypixelContext())
            {
                for (int i = 0; i < 3; i++)
                {
                    var doublePlayersId = await context.Players.Where(p => p.Id > 2_000_000).GroupBy(p => p.Id).Where(p => p.Count() > 1).Select(p => p.Key).FirstOrDefaultAsync();
                    if (doublePlayersId == 0)
                        break;

                    await ResetDoublePlayers(context, doublePlayersId);
                    doublePlayersReset.Inc();
                }

                await context.SaveChangesAsync();
                var unindexedPlayers = await context.Players.Where(p => p.Id == 0).Take(2000).ToListAsync();
                if (unindexedPlayers.Any())
                {
                    Console.Write($"  numbering: {unindexedPlayers.Count()} ");
                    foreach (var player in unindexedPlayers)
                    {
                        player.Id = System.Threading.Interlocked.Increment(ref Indexer.highestPlayerId);
                        context.Players.Update(player);
                        playersNumbered.Inc();
                    }
                    // save all the ids
                    await context.SaveChangesAsync();
                }

                if (unindexedPlayers.Count < 2000)
                {
                    using var activity = activitySource.StartActivity("NumberAuctions");
                    // all players in the db have an id now
                    bidNumberTask = Task.Run(NumberBids);
                    await NumberAuctions(context);

                    auctionsWithoutId.Set(context.Auctions.Count(a => a.SellerId == 0));
                }

                await context.SaveChangesAsync();
                var grandRuneId = ItemDetails.Instance.GetItemIdForTag("ABICASE", true);
                var batch = await context.Auctions.Where(a => a.ItemId == grandRuneId).Include(a => a.NbtData).Take(200).ToListAsync();
                foreach (var auction in batch)
                {
                    var tag = auction.NbtData.Root();
                    tag.Add(new NbtString("id", auction.Tag));
                    auction.Tag = NBT.ItemIdFromExtra(tag);
                    // renumber in next iteration
                    auction.ItemId = 0;
                    auction.SellerId = 0;
                }
                await context.SaveChangesAsync();

            }
            if (bidNumberTask != null)
                await bidNumberTask;

            // give the db a moment to store everything
            await Task.Delay(2000);

        }

        private static async Task ResetDoublePlayers(HypixelContext context, int doublePlayersId)
        {
            if (doublePlayersId % 3 == 0)
                Console.WriteLine($"Found Double player id: {doublePlayersId}, renumbering, highestId: {Indexer.highestPlayerId}");

            foreach (var item in context.Players.Where(p => p.Id == doublePlayersId))
            {
                item.Id = 0;
                context.Update(item);
            }
            foreach (var item in context.Auctions.Where(p => p.SellerId == doublePlayersId))
            {
                item.SellerId = 0;
                context.Update(item);
            }
            foreach (var item in context.Bids.Where(p => p.BidderId == doublePlayersId))
            {
                item.BidderId = 0;
                context.Update(item);
            }
            await context.SaveChangesAsync();
        }

        private async Task NumberAuctions(HypixelContext context)
        {
            var auctionsWithoutSellerId = await context
                                    .Auctions.Where(a => a.SellerId == 0)
                                    .Include(a => a.Enchantments)
                                    .Include(a => a.NBTLookup)
                                    .OrderByDescending(a => a.Id)
                                    .Take(2000).ToListAsync();
            if (auctionsWithoutSellerId.Count() > 0)
                Console.Write(" -#-");

            Dictionary<string, int> playerIdLookup = await BatchLookupPlayerId(context, auctionsWithoutSellerId.Select(a => a.AuctioneerId).Distinct().ToList());
            foreach (var auction in auctionsWithoutSellerId)
            {
                try
                {
                    await NumberAuction(context, auction, playerIdLookup);

                }
                catch (Exception e)
                {
                    Console.WriteLine($"Problem with item {Newtonsoft.Json.JsonConvert.SerializeObject(auction)}");
                    Console.WriteLine($"Error occured while userIndexing: {e.Message} {e.StackTrace}\n {e.InnerException?.Message} {e.InnerException?.StackTrace}");
                }
            }
            await context.SaveChangesAsync();
            auctionsNumbered.Inc(auctionsWithoutSellerId.Count());
        }

        private static async Task NumberAuction(HypixelContext context, SaveAuction auction, Dictionary<string, int> lookup)
        {
            auction.SellerId = await GetOrCreatePlayerId(context, auction.AuctioneerId, lookup);

            if (auction.SellerId == 0)
                // his player has not yet received his number
                return;

            if (auction.ItemId == 0)
            {
                var id = ItemDetails.Instance.GetOrCreateItemIdForAuction(auction, context);
                if (id == 0)
                    dev.Logger.Instance.Error("could not get itemid for " + auction.UId);
                auction.ItemId = id;

                foreach (var enchant in auction.Enchantments)
                {
                    enchant.ItemType = auction.ItemId;
                }
            }
            context.Auctions.Update(auction);
        }

        static readonly int batchSize = 2000;

        public Numberer()
        {
        }

        private async Task NumberBids()
        {
            using var activity = activitySource.StartActivity("NumberBids");
            using var context = new HypixelContext();
            try
            {
                var bidsWithoutSellerId = await context.Bids.Where(a => a.BidderId == 0).Take(batchSize).ToListAsync();
                var uuids = bidsWithoutSellerId.Select(b => b.Bidder).Distinct().ToList();
                Dictionary<string, int> bidderIds = await BatchLookupPlayerId(context, uuids);
                foreach (var bid in bidsWithoutSellerId)
                {

                    bid.BidderId = await GetOrCreatePlayerId(context, bid.Bidder, bidderIds);
                    if (bid.BidderId == 0)
                        // this player has not yet received his number
                        continue;

                    context.Bids.Update(bid);
                }

                await context.SaveChangesAsync();
                bidsWithoutId.Set(await context.Bids.Where(a => a.BidderId == 0).CountAsync());
                bidsNumbered.Inc(bidsWithoutSellerId.Count(b => b.BidderId != 0));
            }
            catch (Exception e)
            {
                Console.WriteLine($"Ran into error on numbering bids {e.Message} {e.StackTrace}");
            }
        }

        private static async Task<Dictionary<string, int>> BatchLookupPlayerId(HypixelContext context, List<string> bidderIdBatch)
        {
            return await context.Players.Where(p => bidderIdBatch.Contains(p.UuId)).ToDictionaryAsync(p => p.UuId, p => p.Id);
        }

        private static async Task<int> GetOrCreatePlayerId(HypixelContext context, string uuid, Dictionary<string, int> lookup = null)
        {
            if (uuid == null)
                return -1;

            if (lookup != null && lookup.TryGetValue(uuid, out int lookupId))
                return lookupId;
            var numeric = long.Parse(uuid.Replace("-","").Substring(0, 15), System.Globalization.NumberStyles.HexNumber);
            if (numeric == 0) // special flag uuid
                return int.Parse(uuid.Split('-').Last(), System.Globalization.NumberStyles.HexNumber);
            var id = await context.Players.Where(p => p.UuId == uuid).Select(p => p.Id).FirstOrDefaultAsync();
            if (id == 0)
            {
                id = Sky.Core.Program.AddPlayer(context, uuid, ref Indexer.highestPlayerId);
                if (id != 0 && id % 10 == 0)
                    Console.WriteLine($"Adding player {id} {uuid} {Indexer.highestPlayerId}");
            }
            return id;
        }
    }
}