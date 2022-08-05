using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using Microsoft.Extensions.DependencyInjection;
using System.Threading.Tasks;
using System.Collections.Generic;
using Coflnet.Sky.Core;
using System;

namespace Coflnet.Sky.Indexer;

public class NameUpdateService
{
    private readonly int Offset = -1;

    private readonly ILogger<NameUpdateService> _logger;
    private readonly IConnectionMultiplexer redis;
    private IServiceScopeFactory scopeFactory;

    public NameUpdateService(ILogger<NameUpdateService> logger, IConnectionMultiplexer redis, IServiceScopeFactory scopeFactory)
    {
        _logger = logger;
        this.redis = redis;
        this.scopeFactory = scopeFactory;
    }

    internal Task<IEnumerable<Player>> GetPlayersToUpdate(int count)
    {
        throw new NotImplementedException();
    }
}