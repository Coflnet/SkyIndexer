using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace Coflnet.Sky.Indexer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            // migrations
            using (var context = new HypixelContext())
            {
                context.Database.Migrate();
            }
            ItemDetails.Instance.LoadFromDB();
            Task.Run(Coflnet.Sky.Core.Program.MakeSureRedisIsInitialized);

            Console.WriteLine("booting db dependend stuff");

            Indexer.LoadFromDB();
            Coflnet.Sky.Core.Program.RunIsolatedForever(async () =>
            {
                await Indexer.ProcessQueue(System.Threading.CancellationToken.None);
            }, "An error occured while indexing");
            Coflnet.Sky.Core.Program.RunIsolatedForever(Numberer.NumberUsers, "Error occured while userIndexing");
            //NameUpdater.Run();
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(3));
                try
                {
                    await ItemPrices.Instance.BackfillPrices();

                }
                catch (Exception e)
                {
                    dev.Logger.Instance.Error(e, "Item Backfill failed");
                }
            }).ConfigureAwait(false);
            NameUpdater.Run();

            Coflnet.Sky.Core.Program.RunIsolatedForever(async () =>
            {
                if (System.Net.Dns.GetHostName().Contains("ekwav"))
                {
                    await Task.Delay(TimeSpan.FromMinutes(3));
                    return;
                }
                while (true)
                {
                    using var context = new HypixelContext();
                    var pulls = await context.BazaarPull
                            .Where(p => p.Id > 3437504)
                            .Include(p => p.Products).ThenInclude(p => p.SellSummary)
                            .Include(p => p.Products).ThenInclude(p => p.BuySummery)
                            .Include(p => p.Products).ThenInclude(p => p.QuickStatus)
                            .Take(5).ToListAsync();
                    if (pulls.Count == 0)
                        throw new TaskCanceledException();
                    context.RemoveRange(pulls);
                    foreach (var pull in pulls)
                    {
                        var products = pull.Products;
                        MarkAllForDeletion(context, products);
                    }
                    var x = await context.SaveChangesAsync();
                    Console.WriteLine($"removed {pulls.FirstOrDefault()?.Products.FirstOrDefault().Id} " + x);
                    
                    var productsWithNoPull = await context.BazaarPrices.Where(p => p.PullInstance == null).Include(p => p.SellSummary).Include(p => p.BuySummery).Include(p => p.QuickStatus).Take(500).ToListAsync();
                    MarkAllForDeletion(context, productsWithNoPull);
                    var y = await context.SaveChangesAsync();
                    Console.WriteLine($"removed {productsWithNoPull.FirstOrDefault().Id} " + y);
                }
            }, "Bazaar delete failed");

            /*try
            {
                Coflnet.Sky.Core.Program.CleanDB();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Cleaning failed {e.Message}");
            }*/
            CreateHostBuilder(args).Build().Run();
        }

        private static void MarkAllForDeletion(HypixelContext context, List<dev.ProductInfo> products)
        {
            context.RemoveRange(products);
            context.RemoveRange(products.SelectMany(p => p.SellSummary));
            context.RemoveRange(products.SelectMany(p => p.BuySummery));
            context.RemoveRange(products.Select(p => p.QuickStatus));
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
