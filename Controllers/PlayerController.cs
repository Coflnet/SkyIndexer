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
            if (uuid == null || uuid.Length != 32)
                return null;

            NameUpdater.UpdateUUid(uuid, name);
            return new Player(uuid) { Name = name };

        }
        [HttpGet]
        public async Task<IEnumerable<Player>> GetPlayersToUpdate(int count = 10)
        {
            return await nameUpdateService.GetPlayersToUpdate(count);
        }
    }
}
