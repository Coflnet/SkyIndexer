using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using RestSharp;
using StackExchange.Redis;

namespace Coflnet.Sky.Indexer;

public class WhipedTracker
{
    IConnectionMultiplexer multiplexer;
    ILogger<WhipedTracker> logger;
    private RestClient profileClient = null;
    private HashSet<(string playerUuid, string profileId)> whipedProfiles = new();
    private HashSet<long> auctionUids = new();
    public WhipedTracker(IConfiguration config, IConnectionMultiplexer multiplexer, ILogger<WhipedTracker> logger)
    {
        profileClient = new RestClient(config["PROFILE_BASE_URL"]);
        this.multiplexer = multiplexer;
        var whipedList = multiplexer.GetDatabase().StringGet("whipedProfiles");
        if (whipedList.HasValue)
        {
            whipedProfiles = System.Text.Json.JsonSerializer.Deserialize<HashSet<(string playerUuid, string profileId)>>(whipedList);
        }
        var profileUuidLookup = whipedProfiles.Select(p => p.profileId).ToHashSet();
        foreach (var (playerUuid, profileId) in whipedProfiles)
        {
            LoadWhipedAuctions(logger, profileUuidLookup, playerUuid, profileId);
        }

        this.logger = logger;
    }

    private HypixelContext LoadWhipedAuctions(ILogger<WhipedTracker> logger, HashSet<string> profileUuidLookup, string playerUuid, string profileId)
    {
        var context = new HypixelContext();
        var endAfter = DateTime.UtcNow.AddDays(-14);
        var activeAuctionsToDeactivate = context.Auctions
                    .Where(a => a.SellerId == context.Players.Where(p => p.UuId == playerUuid).Select(p => p.Id).FirstOrDefault() && a.SellerId != 0 && a.End > endAfter).ToList();
        foreach (var auction in activeAuctionsToDeactivate)
        {
            if (!profileUuidLookup.Contains(auction.ProfileId))
            {
                continue;
            }
            if (auction.End > DateTime.UtcNow)
            {
                auction.End = DateTime.UtcNow;
                context.Auctions.Update(auction);
            }
            auctionUids.Add(auction.UId);
            logger.LogInformation("Deactivating whiped auction " + auction.UId + " for player " + playerUuid);
        }
        if (activeAuctionsToDeactivate.Count > 0)
        {
            context.SaveChanges();
        }
        else
        {
            whipedProfiles.Remove((playerUuid, profileId));
            logger.LogWarning("Removed whiped profile " + profileId + " for player " + playerUuid);
        }

        return context;
    }

    public async Task<bool> AddWhipedProfile(string playerUuid, string profileId)
    {
        if (whipedProfiles.Contains((playerUuid, profileId)))
        {
            return false;
        }
        var request = new RestRequest("api/profile/" + playerUuid + "/hypixel");
        var response = await profileClient.GetAsync(request);
        var profile = JsonConvert.DeserializeObject<ProfileResponse>(response.Content);
        if (profile == null || profile.stats.SkyBlock.Profiles.ContainsKey(Guid.Parse(profileId).ToString("n")))
        {
            logger.LogInformation("Profile " + profileId + " not whiped still found on " + playerUuid);
            return false;
        }
        if (!whipedProfiles.Add((playerUuid, profileId)))
            return false;
        logger.LogInformation("Added whiped profile " + profileId + " for player " + playerUuid);
        multiplexer.GetDatabase().StringSet("whipedProfiles", System.Text.Json.JsonSerializer.Serialize(whipedProfiles));
        LoadWhipedAuctions(logger, whipedProfiles.Select(p => p.profileId).ToHashSet(), playerUuid, profileId);
        return true;
    }

    public class ProfileResponse
    {
        public Stats stats;
    }
    public class Stats
    {
        public SkyBlock SkyBlock;
    }
    public class SkyBlock
    {

        public Dictionary<string, object> Profiles;
    }
    public bool IsWhiped(long auctionUuid)
    {
        return auctionUids.Contains(auctionUuid);
    }
}