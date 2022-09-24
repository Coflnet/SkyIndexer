using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Indexer.Controllers;

[ApiController]
[Route("[controller]")]
public class UserController : ControllerBase
{
    private readonly ILogger<UserController> _logger;
    private SemaphoreSlim creationLock = new(1);

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
}