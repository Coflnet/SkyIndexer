using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Indexer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class SupplyController : ControllerBase
    {
        private readonly ILogger<UserController> _logger;
        private ActiveAhStateService ahStateService;

        public SupplyController(ILogger<UserController> logger, ActiveAhStateService ahStateService)
        {
            _logger = logger;
            this.ahStateService = ahStateService;
        }

        [Route("low")]
        [HttpGet]
        public IEnumerable<KeyValuePair<string, short>> LastUpdateLow()
        {
            return ahStateService.LastUpdate.ItemCount.Where(k=>k.Value < 20).ToList();
        }

        [Route("")]
        [HttpGet]
        public System.Collections.Concurrent.ConcurrentDictionary<string, short> LastUpdate()
        {
            return ahStateService.LastUpdate.ItemCount;
        }
    }
}
