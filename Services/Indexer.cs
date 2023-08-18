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
        public static int QueueCount => auctionsQueue.Count;

        public static readonly string MissingAuctionsTopic = SimplerConfig.Config.Instance["TOPICS:MISSING_AUCTION"];
        public static readonly string SoldAuctionTopic = SimplerConfig.Config.Instance["TOPICS:SOLD_AUCTION"];
        public static readonly string NewAuctionsTopic = SimplerConfig.Config.Instance["TOPICS:NEW_AUCTION"];
        public static readonly string AuctionEndedTopic = SimplerConfig.Config.Instance["TOPICS:AUCTION_ENDED"];
        public static readonly string NewBidTopic = SimplerConfig.Config.Instance["TOPICS:NEW_BID"];

        private static int count;
        public static DateTime LastFinish { get; internal set; }

        private static ConcurrentQueue<SaveAuction> auctionsQueue = new ConcurrentQueue<SaveAuction>();
        private IConfiguration config;

        public Indexer(IConfiguration config)
        {
            this.config = config;
        }

        static Prometheus.Counter insertCount = Prometheus.Metrics.CreateCounter("sky_indexer_auction_insert", "Tracks the count of inserted auctions");



        public static async Task LastHourIndex()
        {

            DateTime indexStart;
            string targetTmp, pullPath;
            VariableSetup(out indexStart, out targetTmp, out pullPath);
            //DeleteDir(targetTmp);
            if (!Directory.Exists(pullPath) && !Directory.Exists(targetTmp))
            {
                // update first
                if (!Sky.Core.Program.FullServerMode)
                    Console.WriteLine("nothing to build indexes from, run again with option u first");
                return;
            }
            // only copy the pull path if there is no temp work path yet
            if (!Directory.Exists(targetTmp))
                Directory.Move(pullPath, targetTmp);
            else
                Console.WriteLine("Resuming work");

            try
            {
                Console.WriteLine("working");

                var work = PullData();
                var earlybreak = 100;
                foreach (var item in work)
                {
                    await ToDb(item);
                    if (earlybreak-- <= 0)
                        break;
                }

                Console.WriteLine($"Indexing done, Indexed: {count} NameRequests: {Sky.Core.Program.RequestsSinceStart}");

                if (!abort)
                    // successful made this index save the startTime
                    FileController.SaveAs("lastIndex", indexStart);
            }
            catch (System.AggregateException e)
            {
                // oh no an error occured
                Logger.Instance.Error($"An error occured while indexing, abording: {e.Message} {e.StackTrace}");
                return;
                //FileController.DeleteFolder("auctionpull");

                //Directory.Move(FileController.GetAbsolutePath("awork"),FileController.GetAbsolutePath("auctionpull"));

            }
            LastFinish = DateTime.Now;

            DeleteDir(targetTmp);
        }

        protected override async Task ExecuteAsync(CancellationToken stopToken)
        {
            var tokenSource = new CancellationTokenSource();
            stopToken.Register(() => tokenSource.Cancel());
            Console.WriteLine("Starting consuming updates");
            try
            {
                await Coflnet.Kafka.KafkaConsumer.ConsumeBatch<SaveAuction>(
                    config,
                    new string[] { NewBidTopic, AuctionEndedTopic, NewAuctionsTopic, SoldAuctionTopic, MissingAuctionsTopic },
                    ToDb,
                    tokenSource.Token,
                    "sky-indexer",
                    200
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

        private static void VariableSetup(out DateTime indexStart, out string targetTmp, out string pullPath)
        {
            indexStart = DateTime.Now;
            if (!Sky.Core.Program.FullServerMode)
                Console.WriteLine($"{indexStart}");
            var lastIndexStart = new DateTime(2020, 4, 25);
            if (FileController.Exists("lastIndex"))
                lastIndexStart = FileController.LoadAs<DateTime>("lastIndex");
            lastIndexStart = lastIndexStart - new TimeSpan(0, 20, 0);
            targetTmp = FileController.GetAbsolutePath("awork");
            pullPath = FileController.GetAbsolutePath("apull");
        }

        static IEnumerable<List<SaveAuction>> PullData()
        {
            var path = "awork";
            foreach (var item in FileController.FileNames("*", path))
            {
                if (abort)
                {
                    Console.WriteLine("Stopped indexer");
                    yield break;
                }
                var fullPath = $"{path}/{item}";
                List<SaveAuction> data = null;
                try
                {
                    data = FileController.LoadAs<List<SaveAuction>>(fullPath);
                }
                catch (Exception)
                {
                    Console.WriteLine("could not load downloaded auction-buffer");
                    FileController.Move(fullPath, "correupted/" + fullPath);
                }
                if (data != null)
                {
                    yield return data;
                    FileController.Delete(fullPath);
                }
            }
        }

        public static int highestPlayerId = 1;

        private static async Task ToDb(IEnumerable<SaveAuction> auctions)
        {
            auctions = auctions.GroupBy(a => a.UId).Select(g => g.OrderByDescending(a => a.Bids?.Count).First()).ToList();
            lock (nameof(highestPlayerId))
            {
                if (highestPlayerId == 1)
                    LoadFromDB();
            }

            for (int i = 0; i < 5; i++)
            {
                try
                {
                    using (var context = new HypixelContext())
                    {
                        Dictionary<string, SaveAuction> inDb = await GetExistingAuctions(auctions, context);

                        var comparer = new BidComparer();

                        foreach (var auction in auctions)
                        {
                            ProcessAuction(context, inDb, comparer, auction);
                        }

                        var count = await context.SaveChangesAsync();
                        insertCount.Inc(count);
                        LastFinish = DateTime.Now;
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
                        throw e;
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
                        throw e;
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

            // update
            context.Auctions.Update(dbauction);
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