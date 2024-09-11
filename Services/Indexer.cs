using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet;
using Confluent.Kafka;
using dev;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json;
using Coflnet.Sky.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Coflnet.Sky.Indexer
{
    public class Indexer : BackgroundService
    {
        private const int MAX_QUEUE_SIZE = 10000;
        private const int AUCTION_CHUNK_SIZE = 1000;
        private static bool abort;
        private static bool minimumOutput;
        public static int IndexedAmount => count;

        public static readonly string MissingAuctionsTopic = SimplerConfig.Config.Instance["TOPICS:MISSING_AUCTION"];
        public static readonly string SoldAuctionTopic = SimplerConfig.Config.Instance["TOPICS:SOLD_AUCTION"];
        public static readonly string NewAuctionsTopic = SimplerConfig.Config.Instance["TOPICS:NEW_AUCTION"];
        public static readonly string AuctionEndedTopic = SimplerConfig.Config.Instance["TOPICS:AUCTION_ENDED"];
        public static readonly string NewBidTopic = SimplerConfig.Config.Instance["TOPICS:NEW_BID"];

        private static int count;
        public static DateTime LastFinish { get; internal set; }

        private ConcurrentQueue<AuctionResult> endedAuctionsQueue;
        private IConfiguration config;

        public Indexer(IConfiguration config, ConcurrentQueue<AuctionResult> endedAuctionsQueue)
        {
            this.config = config;
            this.endedAuctionsQueue = endedAuctionsQueue;
        }

        static Prometheus.Counter insertCount = Prometheus.Metrics.CreateCounter("sky_indexer_auction_insert", "Tracks the count of inserted auctions");
        static Prometheus.Counter indexCount = Prometheus.Metrics.CreateCounter("sky_indexer_auction_consume", "Tracks the count of consumed auctions");
        protected override async Task ExecuteAsync(CancellationToken stopToken)
        {
            var tokenSource = new CancellationTokenSource();
            stopToken.Register(() => tokenSource.Cancel());
            Console.WriteLine("Starting consuming updates");
            try
            {
                await Kafka.KafkaConsumer.ConsumeBatch<SaveAuction>(
                    config,
                    new string[] { NewBidTopic, AuctionEndedTopic, NewAuctionsTopic, SoldAuctionTopic, MissingAuctionsTopic },
                    ToDb,
                    tokenSource.Token,
                    "sky-indexer",
                    300
                    );
            }
            catch (Exception e)
            {
                dev.Logger.Instance.Error(e, $"consuming failed {e.GetType().Name}");
            }
            // cancel all as they will be restarted
            tokenSource.Cancel();
            throw new Exception("Kafka consumer failed");

        }


        public static int highestPlayerId = 1;

        public async Task ToDb(IEnumerable<SaveAuction> auctions)
        {
            auctions = auctions.GroupBy(a => a.UId).Select(g => g.OrderByDescending(a => a.Bids?.Count).First()).ToList();
            lock (nameof(highestPlayerId))
            {
                if (highestPlayerId == 1)
                    LoadFromDB();
            }
            if (auctions.All(a => a.End < DateTime.UtcNow.AddDays(-4)))
            {
                return;
            }

            foreach (var item in auctions)
            {
                if (item.End > DateTime.UtcNow)
                    continue;
                endedAuctionsQueue.Enqueue(new AuctionResult(item));
                if (endedAuctionsQueue.Count > 40)
                    endedAuctionsQueue.TryDequeue(out var _);
            }

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    using (var context = new HypixelContext())
                    {
                        // start isolated transaction
                        using var transaction = await context.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
                        Dictionary<string, SaveAuction> inDb = await GetExistingAuctions(auctions, context);

                        var comparer = new BidComparer();

                        foreach (var auction in auctions)
                        {
                            ProcessAuction(context, inDb, comparer, auction);
                        }

                        var count = await context.SaveChangesAsync();
                        insertCount.Inc(count);
                        indexCount.Inc(auctions.Count());
                        LastFinish = DateTime.Now;
                        transaction.Commit();
                        return;
                    }
                }
                catch (Exception e)
                {
                    dev.Logger.Instance.Error("failed save once, retrying");
                    if (i > 0)
                        dev.Logger.Instance.Error(e, "Trying to index batch of " + auctions.Count());
                    await Task.Delay(500);
                    if (i == 3)
                    {
                        if (auctions.Count() == 1)
                            dev.Logger.Instance.Error("Could not save this auction\n" + JsonConvert.SerializeObject(auctions, Formatting.Indented));
                        else
                        {
                            await ToDb(auctions.Take(auctions.Count() / 2));
                            await ToDb(auctions.Skip(auctions.Count() / 2));
                            return;
                        }
                    }
                    if (i >= 4)
                        throw;
                }
            }
        }

        private static void ProcessAuction(HypixelContext context, Dictionary<string, SaveAuction> inDb, BidComparer comparer, SaveAuction auction)
        {
            try
            {
                var id = auction.Uuid;

                if (inDb.TryGetValue(id, out SaveAuction dbauction))
                {
                    UpdateAuction(context, comparer, auction, dbauction);
                }
                else
                {
                    if (auction.AuctioneerId == null)
                    {
                        Logger.Instance.Error($"auction removed bevore in db " + auction.Uuid);
                        return;
                    }
                    try
                    {
                        if (auction.NBTLookup == null || auction.NBTLookup.Count() == 0)
                            auction.NBTLookup = NBT.CreateLookup(auction);
                    }
                    catch (Exception e)
                    {
                        Logger.Instance.Error($"Error on CreateLookup: {e.Message} \n{e.StackTrace} \n{JSON.Stringify(auction.NbtData.Data)}");
                        throw;
                    }
                    if (auction.HighestBidAmount == 0 && auction.Bids.Count > 0)
                    {
                        Logger.Instance.Info($"Fixing highest bid amount for {auction.Uuid}");
                    }
                    auction.HighestBidAmount = auction.Bids.Select(b => b.Amount).DefaultIfEmpty(auction.HighestBidAmount).Max();
                    // remove dashes if present
                    if (auction.Uuid.Contains('-'))
                        auction.Uuid = auction.Uuid.Replace("-", "");
                    context.Auctions.Add(auction);

                }

                count++;
                if (!minimumOutput && count % 50 == 0)
                    Console.Write($"\r         Indexed: {count} NameRequests: {Sky.Core.Program.RequestsSinceStart}");

            }
            catch (Exception e)
            {
                Logger.Instance.Error($"Error {e.Message} on {auction.ItemName} {auction.Uuid} from {auction.AuctioneerId}");
                Logger.Instance.Error(e.StackTrace);
            }
        }

        private static void UpdateAuction(HypixelContext context, BidComparer comparer, SaveAuction auction, SaveAuction dbauction)
        {
            string sum = GetComparisonKey(dbauction);
            foreach (var bid in auction.Bids)
            {
                bid.Auction = dbauction;
                if (!dbauction.Bids.Contains(bid, comparer))
                {
                    context.Bids.Add(bid);
                }
            }
            UpdateHighestBid(auction, dbauction);

            if (auction.AuctioneerId == null)
            {
                // an ended auction
                dbauction.End = auction.End;
                if (dbauction.End > DateTime.UtcNow)
                    dbauction.End = DateTime.UtcNow;
                context.Auctions.Update(dbauction);
                return;
            }
            if (auction.Bin)
            {
                dbauction.Bin = true;
            }
            if (dbauction.ItemName == null)
                dbauction.ItemName = auction.ItemName;
            if (dbauction.ProfileId == null)
                dbauction.ProfileId = auction.ProfileId;
            if (dbauction.Start == default(DateTime))
                dbauction.Start = auction.Start;
            if (dbauction.End > auction.End || dbauction.End == default(DateTime))
                dbauction.End = auction.End;
            if (dbauction.Category == Category.UNKNOWN)
                dbauction.Category = auction.Category;
            if (dbauction.StartingBid == 0 && auction.StartingBid != 0)
                dbauction.StartingBid = auction.StartingBid;

            if (sum != GetComparisonKey(dbauction))
                // update
                context.Auctions.Update(dbauction);
        }

        private static string GetComparisonKey(SaveAuction dbauction)
        {
            return "" + dbauction.HighestBidAmount + dbauction.Bids.Count + dbauction.StartingBid + dbauction.Start + dbauction.ProfileId + dbauction.AuctioneerId + dbauction.End + dbauction.Category + dbauction.ItemName;
        }

        internal static void UpdateHighestBid(SaveAuction auction, SaveAuction dbauction)
        {
            var highestBid = auction.HighestBidAmount;
            // special case sometimes highest bid is not set
            if (auction.Bids.Count > 0)
                highestBid = auction.Bids.Max(b => b.Amount);
            dbauction.HighestBidAmount = Math.Max(highestBid, dbauction.HighestBidAmount);
        }

        private static async Task<Dictionary<string, SaveAuction>> GetExistingAuctions(IEnumerable<SaveAuction> auctions, HypixelContext context)
        {
            // preload
            return (await context.Auctions.Where(a => auctions.Select(oa => oa.UId)
                .Contains(a.UId)).Include(a => a.Bids).ToListAsync())
                .ToDictionary(a => a.Uuid);
        }

        private static void DeleteDir(string path)
        {
            if (!Directory.Exists(path))
            {
                // nothing to do
                return;
            }

            System.IO.DirectoryInfo di = new DirectoryInfo(path);

            foreach (FileInfo file in di.GetFiles())
            {
                file.Delete();
            }
            foreach (DirectoryInfo dir in di.GetDirectories())
            {
                dir.Delete(true);
            }
            Directory.Delete(path);
        }

        internal static void MiniumOutput()
        {
            minimumOutput = true;
        }


        public static void LoadFromDB()
        {
            using (var context = new HypixelContext())
            {
                if (context.Players.Any())
                    highestPlayerId = context.Players.Max(p => p.Id) + 1;
            }
        }
    }

    class AuctionDeserializer : IDeserializer<SaveAuction>
    {
        public static AuctionDeserializer Instance = new AuctionDeserializer();
        public SaveAuction Deserialize(ReadOnlySpan<byte> data, bool isNull, SerializationContext context)
        {
            return MessagePack.MessagePackSerializer.Deserialize<SaveAuction>(data.ToArray());
        }
    }
}