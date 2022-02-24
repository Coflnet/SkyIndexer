using System.Threading.Tasks;
using hypixel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using RestSharp;

namespace Coflnet.Sky.Indexer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class PlayerController : ControllerBase
    {
        private readonly ILogger<PlayerController> _logger;

        public PlayerController(ILogger<PlayerController> logger)
        {
            _logger = logger;
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
    }
}
