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
    public class UserController : ControllerBase
    {
        private readonly ILogger<UserController> _logger;

        public UserController(ILogger<UserController> logger)
        {
            _logger = logger;
        }


        [Route("")]
        [HttpPost]
        public GoogleUser LastUpdate([FromBody] GoogleUser user)
        {
            return UserService.Instance.GetOrCreateUser(user.GoogleId, user.Email);
        }
    }
}
