using Coflnet.Security.OpenBao;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;


namespace Coflnet.Sky.Indexer
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var host = CreateHostBuilder(args).Build();
            HypixelContext.SetConfiguration(host.Services.GetRequiredService<IConfiguration>());

            // migrations
            using (var context = new HypixelContext())
            {
                context.Database.Migrate();
            }

            Console.WriteLine("booting db dependend stuff");

            Indexer.LoadFromDB();
            NameUpdater.Run();

            host.Run();
        }

        private static void MarkAllForDeletion(HypixelContext context, List<dev.ProductInfo> products)
        {
            context.RemoveRange(products);
            context.RemoveRange(products.Select(p => p.QuickStatus));
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureAppConfiguration((_, config) => config.AddOpenBaoFromEnvironment())
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                });
    }
}
