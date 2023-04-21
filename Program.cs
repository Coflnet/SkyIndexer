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
                // if you setup from scratch you may want to use EnsureCreated because for unkown reasons migrations don't want to create an empty db
                // context.Database.EnsureCreated();
                context.Database.Migrate();
            }
            ItemDetails.Instance.LoadFromDB();
            Task.Run(Coflnet.Sky.Core.Program.MakeSureRedisIsInitialized);

            Console.WriteLine("booting db dependend stuff");

            Indexer.LoadFromDB();
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
