using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using hypixel;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Indexer.Controllers
{
    [ApiController]
    [Route("[controller]")]
    public class ApiController : ControllerBase
    {
        private readonly ILogger<ApiController> _logger;

        public ApiController(ILogger<ApiController> logger)
        {
            _logger = logger;
        }


        [Route("create")]
        [HttpPost]
        public GoogleUser LastUpdate([FromBody] GoogleUser user)
        {
            return UserService.Instance.GetOrCreateUser(user.GoogleId, user.Email);
        }
    }
}
