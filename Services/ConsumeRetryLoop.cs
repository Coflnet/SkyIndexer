using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Coflnet.Sky.Indexer;

/// <summary>
/// Exponential backoff for a Kafka consume loop: starts at 2s, doubles up to 60s,
/// and goes back to 2s once <see cref="Reset"/> is called after a successfully processed batch.
/// </summary>
public class ConsumeBackoff
{
    public static readonly TimeSpan Initial = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(60);

    private TimeSpan _next = Initial;

    public TimeSpan Next()
    {
        var current = _next;
        _next = TimeSpan.FromTicks(Math.Min(_next.Ticks * 2, Max.Ticks));
        return current;
    }

    public void Reset() => _next = Initial;
}

/// <summary>
/// Keeps a consume call alive across failures (startup preflight, assignment timeout,
/// failed TLS recovery, ...). Only cancellation ends the loop (quietly); a consume call that
/// returns without shutdown is treated as a failure, so intake never stops silently.
/// </summary>
public static class ConsumeRetryLoop
{
    public const string EndedMessage = "Kafka consume ended without shutdown";

    /// <summary>
    /// Runs <paramref name="consumeOnce"/> in the retry loop and logs every failure with the next delay.
    /// </summary>
    public static Task RunAsync(string name, Func<Task> consumeOnce, ConsumeBackoff backoff, ILogger logger, CancellationToken ct)
    {
        return RunAsync(consumeOnce, backoff, (ex, wait) => LogFailure(logger, name, ex, wait), Task.Delay, ct);
    }

    private static void LogFailure(ILogger logger, string name, Exception ex, TimeSpan wait)
    {
        if (ex.Message == EndedMessage)
            logger.LogWarning("Kafka consumer {Name} returned without shutdown, restarting in {Delay}", name, wait);
        else
            logger.LogError(ex, "Kafka consumer {Name} failed, restarting in {Delay}", name, wait);
    }

    public static async Task RunAsync(
        Func<Task> consumeOnce,
        ConsumeBackoff backoff,
        Action<Exception, TimeSpan> onFailure,
        Func<TimeSpan, CancellationToken, Task> delay,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var failure = await TryConsumeAsync(consumeOnce);
            if (ct.IsCancellationRequested)
                return;
            var wait = backoff.Next();
            onFailure(failure ?? new InvalidOperationException(EndedMessage), wait);
            if (!await TryDelayAsync(delay, wait, ct))
                return;
        }
    }

    /// <summary>
    /// Wraps a batch handler so the backoff restarts at 2s once a batch was processed.
    /// </summary>
    public static Func<IEnumerable<T>, Task> ResetAfterBatch<T>(ConsumeBackoff backoff, Func<IEnumerable<T>, Task> handler)
    {
        return async batch =>
        {
            await handler(batch);
            backoff.Reset();
        };
    }

    private static async Task<Exception> TryConsumeAsync(Func<Task> consumeOnce)
    {
        try
        {
            await consumeOnce();
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    private static async Task<bool> TryDelayAsync(Func<TimeSpan, CancellationToken, Task> delay, TimeSpan wait, CancellationToken ct)
    {
        try
        {
            await delay(wait, ct);
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
    }
}
