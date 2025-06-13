using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Indexer.Controllers;

[ApiController]
[Route("[controller]")]
public class StatsController : ControllerBase
{
    private ItemDetails itemDetails { get; }

    public StatsController(ItemDetails itemDetails)
    {
        this.itemDetails = itemDetails;
    }
    [HttpGet]
    public async Task<LinkedInStats> GetStats()
    {
        using var context = new HypixelContext();
        return new LinkedInStats
        {
            TotalUsers = await context.Players.Select(s=>s.Id).CountAsync(),
            TotalAuctions = await context.Auctions.MaxAsync(s => s.Id),
            TotalDistinctItems = itemDetails.TagLookup.Count
        };
    }


    public class LinkedInStats
    {
        public int TotalUsers { get; set; }
        public int TotalAuctions { get; set; }
        public int TotalDistinctItems { get; set; }
    }
}