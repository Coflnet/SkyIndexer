using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.McConnect.Api;
using Coflnet.Sky.PlayerState.Client.Api;
using Coflnet.Sky.Settings.Client.Api;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Indexer.Controllers;

[ApiController]
[Route("[controller]")]
public class UserController : ControllerBase
{
    private readonly ILogger<UserController> _logger;
    private readonly IConnectApi connectApi;
    private readonly ISettingsApi settingsApi;
    private readonly IPlayerStateApi playerStateApi;
    private SemaphoreSlim creationLock = new(1);

    public UserController(ILogger<UserController> logger, IConnectApi connectApi, ISettingsApi settingsApi, IPlayerStateApi playerStateApi)
    {
        _logger = logger;
        this.connectApi = connectApi;
        this.settingsApi = settingsApi;
        this.playerStateApi = playerStateApi;
    }

    /// <summary>
    /// Adds a user to the database
    /// </summary>
    /// <param name="user"></param>
    /// <returns></returns>
    [Route("")]
    [HttpPost]
    public async Task<GoogleUser> CreateUser([FromBody] GoogleUser user)
    {
        try
        {
            await creationLock.WaitAsync();
            return UserService.Instance.GetOrCreateUser(user.GoogleId, user.Email);
        }
        finally
        {
            creationLock.Release();
        }
    }

    [HttpGet("{email}")]
    public async Task<GoogleUser> GetUser(string email)
    {
        using var context = new HypixelContext();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email || u.GoogleId == email)
            ?? throw new CoflnetException("user_not_found", "User not found");
        return user;
    }

    [HttpPost("{userId:int}/terms")]
    public Task<GoogleUser> AcceptTerms(int userId, [FromBody] TermsAcceptance acceptance)
    {
        return UserService.Instance.AcceptTerms(userId, acceptance);
    }

    [HttpPost("{userId:int}/agreements/{agreement}")]
    public Task<GoogleUser> AcceptAgreement(int userId, string agreement, [FromBody] TermsAcceptance acceptance)
    {
        return UserService.Instance.AcceptAgreement(userId, agreement, acceptance);
    }

    /// <summary>
    /// Deletes a user and everything the other services stored about them: the settings, the player
    /// state of every connected minecraft account and the account link itself.
    /// </summary>
    /// <param name="email">the email of the user to delete</param>
    /// <param name="id">the google id or the numeric id of the user</param>
    /// <returns>the id of the deleted user</returns>
    [HttpDelete("{email}")]
    public async Task<string> DeleteUser(string email, string id)
    {
        using var context = new HypixelContext();
        var parsedId = int.TryParse(id, out var userId) ? userId : 0;
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email && (u.GoogleId == id || u.Id == parsedId))
            ?? throw new CoflnetException("user_not_found", "User not found");
        // the other services key their data by the numeric id, same as SkyApi passes it to them
        var externalId = user.Id.ToString();

        // the connected accounts have to be read before anything is destroyed, deleting the user in
        // mcconnect is what makes them unrecoverable. Failing anywhere before that leaves the whole
        // deletion retryable.
        var connections = await connectApi.ConnectUserUserIdGetAsync(externalId);
        var purged = new HashSet<string>();
        foreach (var uuid in ConnectedUuids(connections).Append(user.MinecraftUuid))
        {
            if (await DeletePlayerState(uuid))
                purged.Add(uuid);
        }

        // drops the user record and unlinks/unverifies the minecraft accounts, returning the uuids
        // that were verified. Anything not purged above got verified in the meantime, so catch it up.
        var anonymized = await connectApi.ConnectUserUserIdDeleteAsync(externalId) ?? new List<string>();
        foreach (var uuid in anonymized.Where(u => !purged.Contains(u)))
        {
            await DeletePlayerState(uuid);
        }

        await settingsApi.DeleteUserAsync(externalId);

        context.Users.Remove(user);
        await context.SaveChangesAsync();
        _logger.LogInformation("Deleted user {userId} along with {count} minecraft accounts", externalId, purged.Count);
        return externalId;
    }

    private static IEnumerable<string> ConnectedUuids(McConnect.Model.User connections)
    {
        // == true rather than a null coalesce, the generator emits Verified as bool or bool? depending on its version
        return connections?.Accounts?.Where(a => a.Verified == true).Select(a => a.AccountUuid) ?? Enumerable.Empty<string>();
    }

    /// <returns>true if the uuid was valid and state deletion was attempted</returns>
    private async Task<bool> DeletePlayerState(string uuid)
    {
        if (string.IsNullOrEmpty(uuid))
            return false;
        if (!Guid.TryParse(uuid, out var parsed))
        {
            _logger.LogWarning("Could not parse {uuid} as a minecraft uuid, skipping state deletion", uuid);
            return false;
        }
        // the state itself is partitioned by the name the mod reports, everything else by the uuid.
        // Without a known name only the uuid keyed data can be removed.
        var name = (await PlayerSearch.Instance.GetPlayerAsync(uuid))?.Name ?? uuid;
        await playerStateApi.PlayerStatePlayerIdDeleteAsync(name, parsed);
        return true;
    }
}
