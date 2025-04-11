using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
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

    [HttpGet("{email}")]
    public async Task<GoogleUser> GetUser(string email)
    {
        using var context = new HypixelContext();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email || u.GoogleId == email)
            ?? throw new CoflnetException("user_not_found", "User not found");
        return user;
    }

    [HttpDelete("{email}")]
    public async Task<string> DeleteUser(string email, string id)
    {
        using var context = new HypixelContext();
        var user = await context.Users.FirstOrDefaultAsync(u => u.Email == email && u.GoogleId == id)
            ?? throw new CoflnetException("user_not_found", "User not found");
        context.Users.Remove(user);
        await context.SaveChangesAsync();
        return user.Id.ToString();
    }
}