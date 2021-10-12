using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using OpenTracing.Util;

namespace Coflnet.Sky.Indexer
{
    public class ActiveAhStateService : BackgroundService
    {
        IConfiguration config;
        Queue<AhStateSumary> RecentUpdates = new Queue<AhStateSumary>();

        public ActiveAhStateService(IConfiguration config)
        {
            this.config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            using (var context = new hypixel.HypixelContext())
            {
                using var spancontext = GlobalTracer.Instance.BuildSpan("LoadActive").StartActive();
                try
                {
                    RecentUpdates.Enqueue(new AhStateSumary()
                    {
                        ActiveAuctions = new System.Collections.Concurrent.ConcurrentDictionary<long, long>(
                            context.Auctions.Where(a => a.Id > context.Auctions.Max(auc => auc.Id) - 1000000 && a.End > DateTime.Now).Select(a => a.UId)
                            .ToDictionary(a => a)
                    )
                    });
                }
                catch (Exception e)
                {
                    dev.Logger.Instance.Error(e, "loading active auctionids");
                }
            }
            while (!stoppingToken.IsCancellationRequested)
                try
                {
                    await Kafka.KafkaConsumer.Consume<AhStateSumary>(hypixel.Program.KafkaHost, config["TOPICS:AH_SUMARY"], async sum =>
                    {
                        Console.WriteLine("\n-->Consumed update sumary " + sum.Time);
                        using var spancontext = GlobalTracer.Instance.BuildSpan("AhSumaryUpdate").StartActive();
                        if (sum.Time < DateTime.Now - TimeSpan.FromMinutes(5))
                            return;
                        RecentUpdates.Enqueue(sum);

                        if (RecentUpdates.Min(r => r.Time) > DateTime.Now - TimeSpan.FromMinutes(3))
                            return;
                        List<long> missing = FindInactiveAuctions();
                        await UpdateInactiveAuctions(missing, sum.ActiveAuctions);

                    }, stoppingToken);
                }
                catch (Exception e)
                {
                    dev.Logger.Instance.Error(e, "processing inactive auctions");
                }

        }

        private List<long> FindInactiveAuctions()
        {
            var oldest = RecentUpdates.Dequeue();
            var mostRecent = RecentUpdates.OrderByDescending(u => u.Time).Take(3).ToList();
            List<long> missing = new List<long>();
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

        private static async Task UpdateInactiveAuctions(List<long> missing, System.Collections.Concurrent.ConcurrentDictionary<long, long> activeAuctions)
        {
            using (var context = new hypixel.HypixelContext())
            {
                try
                {

                    var toUpdate = await context.Auctions.Where(a => missing.Contains(a.UId) && a.End > DateTime.Now).ToListAsync();
                    foreach (var item in toUpdate)
                    {
                        Console.WriteLine("inactive auction " + item.Uuid);
                        item.End = DateTime.Now;
                        context.Update(item);
                    }
                    await context.SaveChangesAsync();
                    var denominator = 6;
                    var toCheck = activeAuctions.Where(a => a.Key % denominator == DateTime.Now.Minute % denominator).Select(a => a.Key).ToList();
                    var foundActiveAgain = await context.Auctions.Where(a => toCheck.Contains(a.UId) && a.End < DateTime.Now).ToListAsync();
                    foreach (var item in foundActiveAgain)
                    {
                        item.End = DateTime.Now + TimeSpan.FromMinutes(10);
                        context.Update(item);
                        Console.WriteLine("reactivated " + item.Uuid);
                    }
                    await context.SaveChangesAsync();
                    Console.WriteLine(foundActiveAgain.FirstOrDefault()?.Uuid + " found ended active " + foundActiveAgain.Count);
                }
                catch (Exception e)
                {
                    dev.Logger.Instance.Error(e, "updating inactive auctions");
                }
            }
        }
    }
}
