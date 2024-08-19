using System.Collections.Concurrent;
using Coflnet.Kafka;
using Coflnet.Sky.Core;
using Coflnet.Sky.Indexer.Controllers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.OpenApi.Models;
using Prometheus;
using StackExchange.Redis;

namespace Coflnet.Sky.Indexer
{
    public class Startup
    {
        public Startup(IConfiguration configuration)
        {
            Configuration = configuration;
        }

        public IConfiguration Configuration { get; }

        // This method gets called by the runtime. Use this method to add services to the container.
        public void ConfigureServices(IServiceCollection services)
        {

            services.AddControllers();
            services.AddSwaggerGen(c =>
            {
                c.SwaggerDoc("v1", new OpenApiInfo { Title = "SkyIndexer", Version = "v1" });
            });
            services.AddJaeger(Configuration);

            services.AddDbContext<HypixelContext>();
            services.AddSingleton<ActiveAhStateService>();
            services.AddHostedService<ActiveAhStateService>(a => a.GetRequiredService<ActiveAhStateService>());

            var redisOptions = ConfigurationOptions.Parse(Configuration["REDIS_HOST"]);
            services.AddSingleton<IConnectionMultiplexer>(provider => ConnectionMultiplexer.Connect(redisOptions));
            services.AddSingleton<NameUpdateService>();
            services.AddHostedService<Numberer>();
            services.AddSingleton<WhipedTracker>();
            services.AddSingleton<Indexer>();
            services.AddHostedService<Indexer>(di=>di.GetRequiredService<Indexer>());
            services.AddSingleton<ConcurrentQueue<AuctionResult>>();
            services.AddSingleton<Kafka.KafkaCreator>();
        }

        // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
        public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
        {
            app.UseDeveloperExceptionPage();
            app.UseSwagger();
            app.UseSwaggerUI(c =>
            {
                c.SwaggerEndpoint("/swagger/v1/swagger.json", "SkyIndexer v1");
                c.RoutePrefix = "api";
            });

            app.UseRouting();

            app.UseAuthorization();

            app.UseEndpoints(endpoints =>
            {
                endpoints.MapMetrics();
                endpoints.MapControllers();
            });
        }
    }
}
