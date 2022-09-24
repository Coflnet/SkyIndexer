using System.Collections.Generic;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RestSharp;
using StackExchange.Redis;

namespace Coflnet.Sky.Indexer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PlayerController : ControllerBase
    {
        private readonly ILogger<PlayerController> _logger;
        private readonly NameUpdateService nameUpdateService;

        public PlayerController(ILogger<PlayerController> logger, NameUpdateService nameUpdateService)
        {
            _logger = logger;
            this.nameUpdateService = nameUpdateService;
        }

        [Route("{uuid}")]
        [HttpPatch]
        public async Task<Player> UpdatName(string uuid, string name = null)
        {
            if(uuid == null || uuid.Length != 32)
                return null;
            var client = new RestClient("https://playerdb.co/api/player/minecraft/");
            var result = await client.ExecuteAsync(new RestRequest(uuid));
            if(result.StatusCode != System.Net.HttpStatusCode.OK)
                throw new CoflnetException("invalid_uuid", "There was no player with the given uuid found");
            return await PlayerService.Instance.UpdatePlayerName(uuid);

        }
        [HttpGet]
        public async Task<IEnumerable<Player>> GetPlayersToUpdate(int count = 10)
        {
            return await nameUpdateService.GetPlayersToUpdate(count);
        }
    }
}
