using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Indexer.Controllers;
[ApiController]
[Route("[controller]")]
public class PlayerController : ControllerBase
{
    private readonly ILogger<PlayerController> _logger;
    private readonly NameUpdateService nameUpdateService;
    private WhipedTracker whipedTracker;

    public PlayerController(ILogger<PlayerController> logger, NameUpdateService nameUpdateService, WhipedTracker whipedTracker)
    {
        _logger = logger;
        this.nameUpdateService = nameUpdateService;
        this.whipedTracker = whipedTracker;
    }

    [Route("{uuid}")]
    [HttpPatch]
    public async Task<Player> UpdatName(string uuid, string name = null)
    {
        if (uuid == null || uuid.Length != 32)
            return null;

        if (PermanentAnonymization.IsProtectedPlayer(uuid))
            name = null;

        NameUpdater.UpdateUUid(uuid, name);
        return new Player(uuid) { Name = name };

    }
    [HttpGet]
    public async Task<IEnumerable<Player>> GetPlayersToUpdate(int count = 10)
    {
        return await nameUpdateService.GetPlayersToUpdate(count);
    }

    /// <summary>
    /// Persists an opt-out of processing and refreshes the in-memory set of this (single) indexer instance immediately; other services pick it up with their next hourly refresh
    /// </summary>
    [Route("optout/{uuid}")]
    [HttpPost]
    public IActionResult OptOut(string uuid)
    {
        if (!PrivacyExport.CanonicalUuid(uuid))
            return BadRequest("uuid must be 32 lowercase hex characters without dashes");
        using var context = new HypixelContext();
        PlayerOptOut.Add(context, uuid);
        return Ok();
    }

    [Route("{uuid}/{profile}/whiped")]
    [HttpPatch]
    public async Task WhipedProfile(string uuid, string profile)
    {
        await whipedTracker.AddWhipedProfile(uuid, profile);
    }
}
