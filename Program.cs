using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using hypixel;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace Coflnet.Sky.Indexer
{
    public class Program
    {
        static string apiKey = SimplerConfig.Config.Instance["apiKey"];
        public static void Main(string[] args)
        {
            // migrations
            //hypixel.Program.GetDBToDesiredState();
            ItemDetails.Instance.LoadFromDB();
            Task.Run(hypixel.Program.MakeSureRedisIsInitialized);

            Console.WriteLine("booting db dependend stuff");
            var bazaar = new dev.BazaarIndexer();
            hypixel.Program.RunIsolatedForever(bazaar.ProcessBazaarQueue, "bazaar queue");
            hypixel.Indexer.LoadFromDB();
            hypixel.Program.RunIsolatedForever(async () =>
            {
                await hypixel.Indexer.ProcessQueue();
                await hypixel.Indexer.LastHourIndex();

            }, "An error occured while indexing");
            hypixel.Program.RunIsolatedForever(Numberer.NumberUsers, "Error occured while userIndexing");
            //NameUpdater.Run();
            Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromMinutes(3));
                await ItemPrices.Instance.BackfillPrices();
            }).ConfigureAwait(false);

            /*try
            {
                hypixel.Program.CleanDB();
            }
            catch (Exception e)
            {
                Console.WriteLine($"Cleaning failed {e.Message}");
            }*/
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
