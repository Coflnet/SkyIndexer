using System;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;

namespace Coflnet.Sky.Indexer.Infrastructure;

internal static class DatabaseReadiness
{
    internal static async Task CheckAsync(HypixelContext database, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            await database.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
        }
        catch (Exception)
        {
            throw new InvalidOperationException("The required database readiness check failed.");
        }
    }
}
