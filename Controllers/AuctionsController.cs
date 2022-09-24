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
        Console.WriteLine(JsonConvert.SerializeObject(auction.NBTLookup,Formatting.Indented));
        auction.NBTLookup = NBT.CreateLookup(auction);
        Console.WriteLine(JsonConvert.SerializeObject(auction.NBTLookup,Formatting.Indented));
        db.Update(auction);
        await db.SaveChangesAsync();
    }
}

