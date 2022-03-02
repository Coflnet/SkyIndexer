using System;
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

        /// <summary>
        /// Adds a user to the database
        /// </summary>
        /// <param name="user"></param>
        /// <returns></returns>
        [Route("")]
        [HttpPost]
        public GoogleUser CreateUser([FromBody] GoogleUser user)
        {
            return UserService.Instance.GetOrCreateUser(user.GoogleId, user.Email);
        }
    }
}
