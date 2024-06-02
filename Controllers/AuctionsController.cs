using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
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
    HypixelContext db;
    public AuctionsController(ILogger<AuctionsController> logger, HypixelContext db, ConcurrentQueue<AuctionResult> endedQueue)
    {
        _logger = logger;
        this.db = db;
        this.endedQueue = endedQueue;
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
        auction.NBTLookup = NBT.CreateLookup(auction);
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
        var playerId = await db.Players.Where(p => p.UuId == playerUuid).Select(p => p.Id).FirstOrDefaultAsync();
        var auctions = await db.Auctions.Where(a => a.SellerId == playerId).ToListAsync();
        foreach (var auction in auctions)
        {
            auction.SellerId = 0;
            auction.AuctioneerId = "00000000-0000-0000-0000-0000000000" + Random.Shared.Next(1, 254).ToString("X2").PadLeft(2, '0');
            db.Update(auction);
        }
        var bids = await db.Bids.Where(b => b.BidderId == playerId).ToListAsync();
        foreach (var bid in bids)
        {
            bid.BidderId = 0;
            bid.Bidder = "00000000-0000-0000-0000-0000000000" + Random.Shared.Next(1, 254).ToString("X2").PadLeft(2, '0');
            db.Update(bid);
        }
        return (await db.SaveChangesAsync(), auctions.Count, bids.Count);

    }
}

