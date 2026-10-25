using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using MessagePack;
using Microsoft.AspNetCore.Mvc;

namespace Coflnet.Sky.Indexer.Controllers;

/// <summary>
/// Serves the in-memory set of all currently active auctions
/// </summary>
[ApiController]
[Route("[controller]")]
public class ActiveController : ControllerBase
{
    private const string MsgPack = "application/x-msgpack";
    private readonly ActiveAuctionSetService service;

    public ActiveController(ActiveAuctionSetService service)
    {
        this.service = service;
    }

    private IActionResult NotReady() => StatusCode(503, new { error = "not_ready" });

    /// <summary>
    /// All active auctions as length prefixed lz4 msgpack chunks (see ActiveAuctionSetSerializer)
    /// </summary>
    [HttpGet("auctions")]
    [Produces(MsgPack)]
    [ProducesResponseType(200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetAuctions()
    {
        var set = service.Set;
        if (!set.IsReady)
            return NotReady();
        var snapshot = set.GetFreshSnapshot();
        Response.ContentType = MsgPack;
        Response.Headers["X-Total-Count"] = snapshot.Count.ToString();
        Response.Headers["X-Snapshot-Version"] = snapshot.Version.ToString();
        Response.Headers["X-Ready"] = "true";
        await ActiveAuctionSetSerializer.SerializeAsync(Response.Body, snapshot.All, HttpContext.RequestAborted);
        return new EmptyResult();
    }

    /// <summary>
    /// Map of uid to end ticks for all active auctions
    /// </summary>
    [HttpGet("uids")]
    [Produces(MsgPack)]
    [ProducesResponseType(200)]
    [ProducesResponseType(503)]
    public async Task<IActionResult> GetUids()
    {
        if (!service.Set.IsReady)
            return NotReady();
        var uids = service.Set.UidEndTicks();
        Response.ContentType = MsgPack;
        Response.Headers["X-Total-Count"] = uids.Count.ToString();
        Response.Headers["X-Ready"] = "true";
        var bytes = MessagePackSerializer.Serialize(uids, ActiveAuctionSetSerializer.Options, HttpContext.RequestAborted);
        await Response.Body.WriteAsync(bytes, HttpContext.RequestAborted);
        return new EmptyResult();
    }

    /// <summary>
    /// A single active auction
    /// </summary>
    [HttpGet("auctions/{uid:long}")]
    [Produces(MsgPack)]
    [ProducesResponseType(200)]
    [ProducesResponseType(404)]
    public async Task<IActionResult> GetAuction(long uid)
    {
        if (!service.Set.TryGet(uid, out var auction))
            return NotFound();
        var record = new ActiveAuctionRecord { Auction = auction, ItemId = auction.ItemId };
        Response.ContentType = MsgPack;
        var bytes = MessagePackSerializer.Serialize(record, ActiveAuctionSetSerializer.Options, HttpContext.RequestAborted);
        await Response.Body.WriteAsync(bytes, HttpContext.RequestAborted);
        return new EmptyResult();
    }

    /// <summary>
    /// Uids of the active auctions of a seller
    /// </summary>
    [HttpGet("seller/{auctioneerId}/uids")]
    [ProducesResponseType(typeof(long[]), 200)]
    [ProducesResponseType(503)]
    public IActionResult GetSellerUids(string auctioneerId)
    {
        if (!service.Set.IsReady)
            return NotReady();
        return Ok(service.ActiveUidsForSeller(auctioneerId).ToArray());
    }

    /// <summary>
    /// State of the set
    /// </summary>
    [HttpGet("stats")]
    [ProducesResponseType(typeof(ActiveSetStats), 200)]
    public ActiveSetStats GetStats()
    {
        var set = service.Set;
        var snapshot = set.Snapshot;
        return new ActiveSetStats
        {
            Count = set.Count,
            Ready = set.IsReady,
            SnapshotAt = snapshot.BuiltAt,
            Version = snapshot.Version,
            LastChange = set.LastChange
        };
    }
}

public class ActiveSetStats
{
    public int Count { get; set; }
    public bool Ready { get; set; }
    public System.DateTime SnapshotAt { get; set; }
    public long Version { get; set; }
    public System.DateTime LastChange { get; set; }
}
