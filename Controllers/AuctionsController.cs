using System;
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
    HypixelContext db;
    public AuctionsController(ILogger<AuctionsController> logger, HypixelContext db)
    {
        _logger = logger;
        this.db = db;
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
}

