using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Confluent.Kafka;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;
using OpenTracing.Util;
using RestSharp;

namespace Coflnet.Sky.Indexer
{
    public class ActiveAhStateService : BackgroundService
    {
        IConfiguration config;
        Queue<AhStateSumary> RecentUpdates = new Queue<AhStateSumary>();

        public AhStateSumary LastUpdate => RecentUpdates.LastOrDefault();


        public ActiveAhStateService(IConfiguration config)
        {
            this.config = config;
        }


        private static ProducerConfig producerConfig = new ProducerConfig
        {
            BootstrapServers = SimplerConfig.Config.Instance["KAFKA_HOST"],
            LingerMs = 100,
        };

        private async Task LoadActiveFromDb()
        {
            using (var context = new HypixelContext())
            {
                using var spancontext = GlobalTracer.Instance.BuildSpan("LoadActive").StartActive();
                try
                {
                    var activeAuctions = new System.Collections.Concurrent.ConcurrentDictionary<long, long>(
                            await context.Auctions.Where(a => a.Id > context.Auctions.Max(auc => auc.Id) - 2500000 && a.End > Now)
                            .Select(a => a.UId)
                            .ToDictionaryAsync(a => a));
                    await ProcessSummary(new AhStateSumary()
                    {
                        ActiveAuctions = activeAuctions,
                        Time = Now
                    });
                    Console.WriteLine("loaded all active auctionids " + activeAuctions.Count);

                    RequestCheck(await context.Auctions.Where(a => a.Id > context.Auctions.Max(auc => auc.Id) - 25)
                                .ToListAsync());
                }
                catch (Exception e)
                {
                    dev.Logger.Instance.Error(e, "loading active auctionids");
                }
            }
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await LoadActiveFromDb();

            await Task.Run(async () =>
            {

                while (!stoppingToken.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
                    await LoadActiveFromDb();
                    KeepQueueSizeInCheck();
                }
            });

            while (!stoppingToken.IsCancellationRequested)
                try
                {
                    await Kafka.KafkaConsumer.Consume<AhStateSumary>(Core.Program.KafkaHost, config["TOPICS:AH_SUMARY"], async sum =>
                    {
                        Console.WriteLine($"\n-->Consumed update sumary {sum.Time} {sum.ActiveAuctions.Count}");
                        using var spancontext = GlobalTracer.Instance.BuildSpan("AhSumaryUpdate").StartActive();
                        await ProcessSummary(sum);

                    }, stoppingToken);
                }
                catch (Exception e)
                {
                    dev.Logger.Instance.Error(e, "processing inactive auctions");
                }

        }

        private async Task ProcessSummary(AhStateSumary sum)
        {
            if (sum.Time < Now - TimeSpan.FromMinutes(5))
                return;
            RecentUpdates.Enqueue(sum);
            using (var context = new HypixelContext())
            {
                await ReactiveFalsyDeactivated(sum, context);
            }

            if (RecentUpdates.Min(r => r.Time) > Now - TimeSpan.FromMinutes(5))
                return;
            if (RecentUpdates.Count < 6)
                return;

            List<long> missing = FindInactiveAuctions();
            if (missing.Count == 0)
                return;
            await UpdateInactiveAuctions(missing);
            KeepQueueSizeInCheck();
        }

        private void KeepQueueSizeInCheck()
        {
            if (RecentUpdates.Peek().Time < Now - TimeSpan.FromMinutes(10))
                RecentUpdates.Dequeue();
        }

        private List<long> FindInactiveAuctions()
        {
            if (RecentUpdates.Where(u => u.Time > Now - TimeSpan.FromMinutes(4.4)).Count() < 4)
                return new List<long>(); // not enough context

            var oldest = RecentUpdates.Dequeue();
            var mostRecent = RecentUpdates.Where(u => u.Time > Now - TimeSpan.FromMinutes(5)).ToList();
            List<long> missing = new List<long>();
            Console.WriteLine($"Checking parts {mostRecent.Count()} with time {mostRecent.First().Time} avg part counts: {string.Join(',', mostRecent.GroupBy(m => m.Part).Select(m => m.Average(d => d.ActiveAuctions.Count())))} ");
            foreach (var item in oldest.ActiveAuctions.Keys)
            {
                var exists = false;
                foreach (var recent in mostRecent)
                {
                    if (recent.ActiveAuctions.ContainsKey(item))
                    {
                        exists = true;
                        break;
                    }
                }
                if (!exists)
                {
                    missing.Add(item);
                }
            }
            Console.WriteLine("Missing summary");
            Console.WriteLine("Total missing " + missing.Count);
            Console.WriteLine("First missing " + missing.FirstOrDefault());
            Console.WriteLine("oldest count " + oldest.ActiveAuctions.Count);

            return missing;
        }

        private async Task UpdateInactiveAuctions(List<long> missing)
        {
            using (var context = new HypixelContext())
            {
                try
                {

                    var toUpdate = await context.Auctions.Where(a => missing.Contains(a.UId) && a.End > Now).ToListAsync();

                    if (toUpdate.Count > 100)
                    {
                        Console.WriteLine($"to many went inactive {toUpdate.Count}, dropping");
                        toUpdate = toUpdate.Take(50).ToList();
                    }
                    foreach (var item in toUpdate)
                    {
                        if (item.UId % 5 == 0)
                            Console.WriteLine("inactive auction " + item.Uuid);
                        item.End = Now;
                        context.Update(item);
                    }
                    var sumarised = toUpdate.GroupBy(b => b.AuctioneerId).Select(b => b.First()).ToList();
                    Console.WriteLine("deactivated " + toUpdate.Count());
                    Console.WriteLine("from sellers: " + sumarised.Count());
                    await context.SaveChangesAsync();
                    RequestCheck(toUpdate);
                }
                catch (Exception e)
                {
                    dev.Logger.Instance.Error(e, "updating inactive auctions");
                }
            }
        }

        private static async Task ReactiveFalsyDeactivated(AhStateSumary sumary, HypixelContext context)
        {
            var activeAuctions = sumary.ActiveAuctions;
            var denominator = 1;
            var minUtcTicks = (sumary.Time + TimeSpan.FromMinutes(1.5)).Ticks;
            var toCheck = activeAuctions.Where(a => a.Value > minUtcTicks && a.Key % denominator == Now.Minute % denominator).Select(a => a.Key).ToList();
            var almostEnded = activeAuctions.Where(a => a.Value < minUtcTicks);
            Console.WriteLine($"No need to reactivate almost expired {almostEnded.Count()} {almostEnded.FirstOrDefault().Key}");
            // maximimum time considered in the past to require reactivation (sells can take up to 2 minutes to be saved)
            var maxExpiry = Now - TimeSpan.FromMinutes(1.5);
            var foundActiveAgain = await context.Auctions.Where(a => toCheck.Contains(a.UId) && a.End < maxExpiry).ToListAsync();
            foreach (var item in foundActiveAgain)
            {
                var ticks = activeAuctions.GetValueOrDefault(item.UId);
                item.End = ticks > 2 ? new DateTime(ticks, DateTimeKind.Utc) : Now + TimeSpan.FromMinutes(10);
                context.Update(item);
                Console.WriteLine($"reactivated {item.Uuid}  {item.End}");
            }
            await context.SaveChangesAsync();
            Console.WriteLine(foundActiveAgain.FirstOrDefault()?.Uuid + " found ended active " + foundActiveAgain.Count);
        }

        private static DateTime Now => DateTime.UtcNow;


        private void RequestCheck(List<SaveAuction> sumarised)
        {
            using (var p = new ProducerBuilder<string, SaveAuction>(producerConfig).SetValueSerializer(SerializerFactory.GetSerializer<SaveAuction>()).Build())
            {
                foreach (var item in sumarised)
                {
                    try
                    {

                        p.Produce(config["TOPICS:AUCTION_CHECK"], new Message<string, SaveAuction> { Value = item, Key = $"{item.UId.ToString()}{(item.Bids == null ? "null" : item.Bids.Count)}{item.End}" }, r =>
                        {
                            if (r.Error.IsError || r.TopicPartitionOffset.Offset % 1000 == 10)
                                Console.WriteLine(!r.Error.IsError ?
                                    $"Delivered {r.Topic} {r.Offset} " :
                                    $"\nDelivery Error {r.Topic}: {r.Error.Reason}");
                        });
                    }
                    catch (Exception e)
                    {
                        dev.Logger.Instance.Error(e, "Sending auction " + JsonConvert.SerializeObject(item));
                    }
                }
                p.Flush(TimeSpan.FromSeconds(10));
                Console.WriteLine("sent to check: " + sumarised.Count());
            }
        }
    }
}
