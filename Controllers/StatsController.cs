using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Indexer.Controllers;

[ApiController]
[Route("[controller]")]
public class StatsController : ControllerBase
{
    [HttpGet]
    public async Task<LinkedInStats> GetStats()
    {
        using var context = new HypixelContext();
        return new LinkedInStats
        {
            TotalUsers = await context.Players.MaxAsync(s => s.Id),
            TotalAuctions = await context.Auctions.MaxAsync(s => s.Id),
            TotalDistinctItems = ItemDetails.Instance.TagLookup.Count
        };
    }


    public class LinkedInStats
    {
        public int TotalUsers { get; set; }
        public int TotalAuctions { get; set; }
        public int TotalDistinctItems { get; set; }
    }
}