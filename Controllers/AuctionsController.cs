using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Coflnet.Sky.Indexer.Controllers;

[ApiController]
[Route("[controller]")]
public class AuctionsController : ControllerBase
{
    private readonly ILogger<AuctionsController> _logger;
    private ConcurrentQueue<AuctionResult> endedQueue;
    private NBT nbt;
    HypixelContext db;
    private Indexer indexer;
    public AuctionsController(ILogger<AuctionsController> logger, HypixelContext db, ConcurrentQueue<AuctionResult> endedQueue, Indexer indexer, NBT nbt)
    {
        _logger = logger;
        this.db = db;
        this.endedQueue = endedQueue;
        this.indexer = indexer;
        this.nbt = nbt;
    }

    /// <summary>
    /// Adds a user to the database
    /// </summary>
    /// <param name="user"></param>
    /// <returns></returns>
    [Route("{uuid}")]
    [HttpPost]
    public async Task Reindex(string uuid)
    {
        var uid = AuctionService.Instance.GetId(uuid);
        var auction = await db.Auctions.Where(a => a.UId == uid).Include(a => a.NbtData).Include(a => a.NBTLookup).FirstOrDefaultAsync();
        Console.WriteLine(JsonConvert.SerializeObject(auction.NBTLookup, Formatting.Indented));
        auction.NBTLookup = nbt.CreateLookup(auction);
        Console.WriteLine(JsonConvert.SerializeObject(auction.NBTLookup, Formatting.Indented));
        db.Update(auction);
        await db.SaveChangesAsync();
    }

    [Route("reindex")]
    [HttpPost]
    public async Task<int> Reindex(int amount, int offset)
    {
        var max = db.Auctions.Max(d => d.Id);
        var auctions = await db.Auctions.Include(a => a.Bids).Where(a => a.Id > max - amount - offset && a.Id < max - offset && a.Bin && a.Bids.Count > 0 && a.Bids.First().Timestamp != a.End).ToListAsync();
        foreach (var auction in auctions)
        {
            if (auction.End.RoundDown(TimeSpan.FromMinutes(1)) == auction.Bids.First().Timestamp.RoundDown(TimeSpan.FromMinutes(1)))
                continue;
            auction.End = auction.Bids.First().Timestamp;
            db.Update(auction);
        }
        return await db.SaveChangesAsync();
    }

    /// <summary>
    /// Returns recently added auctions previw 
    /// </summary>
    /// <returns></returns>
    [Route("ended")]
    [HttpGet]
    [ProducesResponseType(typeof(List<AuctionResult>), 200)]
    public async Task<string> GetEnded()
    {
        return JsonConvert.SerializeObject(endedQueue.ToArray());
    }

    [Route("anonymize/{playerUuid}")]
    [HttpDelete]
    public async Task<(int, int, int)> Anonymize(string playerUuid, string email)
    {
        var user = await UserService.Instance.GetUserByEmail(email);
        if (user == null)
            return (0, 0, 0);
        var playerId = (await PlayerSearch.Instance.GetPlayerAsync(playerUuid)).Id;
        var auctions = await db.Auctions.Where(a => a.SellerId == playerId).ToListAsync();
        foreach (var auction in auctions)
        {
            auction.SellerId = 0;
            auction.AuctioneerId = Random.Shared.Next(1, 254).ToString("X2").PadLeft(32, '0');
            db.Update(auction);
        }
        var bids = await db.Bids.Where(b => b.BidderId == playerId).ToListAsync();
        foreach (var bid in bids)
        {
            bid.BidderId = 0;
            bid.Bidder = Random.Shared.Next(1, 254).ToString("X2").PadLeft(32, '0');
            db.Update(bid);
        }
        return (await db.SaveChangesAsync(), auctions.Count, bids.Count);

    }

    MessagePack.MessagePackSerializerOptions options = MessagePack.MessagePackSerializerOptions.Standard.WithResolver(MessagePack.Resolvers.ContractlessStandardResolver.Instance).WithCompression(MessagePack.MessagePackCompression.Lz4BlockArray);

    [Route("export")]
    [HttpGet]
    public async Task<string> Export(DateTime start, DateTime end)
    {
        var auctions = await db.Auctions.Include(a => a.Bids).Include(a => a.NbtData).Include(a => a.Enchantments).Where(a => a.End > start && a.End < end && a.Id > db.Auctions.Max(au => au.Id) - 5_000_000).ToListAsync();
        // set count in header 
        Response.Headers["X-Total-Count"] = auctions.Count.ToString();
        return Convert.ToBase64String(MessagePack.MessagePackSerializer.Serialize(auctions, options));
    }

    [Route("import")]
    [HttpPost]
    public async Task<int> Import([FromBody] string data)
    {
        var auctions = MessagePack.MessagePackSerializer.Deserialize<List<SaveAuction>>(Convert.FromBase64String(data), options);
        foreach (var item in Batch(auctions, 50))
        {
            await indexer.ToDb(item);
        }
        return auctions.Count;
    }

    public static IEnumerable<IEnumerable<T>> Batch<T>(IEnumerable<T> values, int size)
    {
        List<T> valueList = values.ToList();
        for (int i = 0; i < valueList.Count; i += size)
        {
            yield return valueList.GetRange(i, Math.Min(size, valueList.Count - i));
        }
    }
}

