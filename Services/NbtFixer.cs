using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Coflnet.Sky.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Newtonsoft.Json;

namespace Coflnet.Sky.Indexer;
public class NbtFixer : BackgroundService
{
    private IConfiguration config;

    public NbtFixer(IConfiguration config)
    {
        this.config = config;
    }
    protected override async Task ExecuteAsync(CancellationToken stopToken)
    {
        var tokenSource = new CancellationTokenSource();
        stopToken.Register(() => tokenSource.Cancel());
        Console.WriteLine("Starting consuming updates");
        try
        {
            await Kafka.KafkaConsumer.ConsumeBatch<SaveAuction>(
                config,
                new string[] { Indexer.NewAuctionsTopic },
                ToDb,
                tokenSource.Token,
                "sky-indexer-nbtfix",
                50
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

    private async Task ToDb(IEnumerable<SaveAuction> auctions)
    {
        // check start is on 2024-08-07
        if (auctions.All(a => a.Start < new DateTime(2024, 8, 6)))
        {
            return;
        }
        auctions = auctions.GroupBy(a => a.UId).Select(g => g.OrderByDescending(a => a.Bids?.Count).First()).ToList();
        var uidLookup = auctions.Select(oa => oa.UId).ToHashSet();
        using var context = new HypixelContext();
        var indDb = await context.Auctions.Where(ai => context.Auctions.Where(a => auctions.Select(oa => oa.UId)
            .Contains(a.UId)).Select(a => a.NbtDataId).Contains(ai.NbtDataId)).Include(a => a.NbtData).ToListAsync();
        foreach (var group in indDb.GroupBy(a => a.NbtDataId).Where(g => g.Count() > 1))
        {
            var matching = group.Where(a => uidLookup.Contains(a.UId)).First();
            var actual = auctions.Where(a => a.UId == matching.UId).First();
            Console.WriteLine($"nbtId dupplicate: {matching.Id} {matching.Uuid} {matching.NbtDataId} \n{JsonConvert.SerializeObject(matching.NbtData)}\n{JsonConvert.SerializeObject(actual.NbtData)}");
            matching.NbtData = actual.NbtData;
            context.Auctions.Update(matching);
            // context.Add(matching.NbtData);
            var changeCount = await context.SaveChangesAsync();
            Console.WriteLine($"changed {changeCount} on {matching.Id}");
        }
        Console.WriteLine($"checked dupplicate nbtdataId {indDb.Count} - {indDb.FirstOrDefault()?.Id}");
    }
}
