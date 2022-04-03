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
        static string apiKey = SimplerConfig.Config.Instance["apiKey"];
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
                await ItemPrices.Instance.BackfillPrices();
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

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
