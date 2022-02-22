using System.Threading.Tasks;
using hypixel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

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
        public Task<Player> UpdatName(string uuid, string name = null)
        {
            if(uuid == null || uuid.Length != 32)
                return null;
            return PlayerService.Instance.UpdatePlayerName(uuid);
        }
    }
}
