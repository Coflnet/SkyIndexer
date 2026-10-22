using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core.Archive;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.Indexer.Archive;

/// <summary>Dictionary-backed <see cref="IAuctionBlobStore"/> that stores the real compressed wire format.</summary>
internal class InMemoryAuctionBlobStore : IAuctionBlobStore
{
    public readonly Dictionary<string, byte[]> Objects = new();
    public readonly List<string> Events;
    /// <summary>When set, uncached block reads flip one item byte, like a corrupted upload.</summary>
    public bool CorruptReadBack;

    public InMemoryAuctionBlobStore(List<string> events = null) => Events = events ?? new List<string>();

    public bool IsEnabled => true;

    public Task<ArchivedAuctionBlock> ReadBlockAsync(string tag, int month, int part, bool uncached = false, CancellationToken cancellationToken = default)
    {
        Events.Add($"read:{tag}:{month}:{part}:{(uncached ? "uncached" : "cached")}");
        var block = Get<ArchivedAuctionBlock>(AuctionBlobKeys.Block(tag, month, part));
        if (block != null && CorruptReadBack && block.ItemBytes.Length > 0 && block.ItemBytes[0]?.Length > 0)
            block.ItemBytes[0][0] ^= 0xFF;
        return Task.FromResult(block);
    }

    public Task WriteBlockAsync(ArchivedAuctionBlock block, CancellationToken cancellationToken = default)
    {
        Events.Add($"write:{block.Tag}:{block.Month}:{block.Part}");
        Put(AuctionBlobKeys.Block(block.Tag, block.Month, block.Part), block);
        return Task.CompletedTask;
    }

    public Task<bool> BlockExistsAsync(string tag, int month, int part, CancellationToken cancellationToken = default)
        => Task.FromResult(Objects.ContainsKey(AuctionBlobKeys.Block(tag, month, part)));

    public Task<ArchivedTagIndex> ReadIndexAsync(string tag, CancellationToken cancellationToken = default)
        => Task.FromResult(Get<ArchivedTagIndex>(AuctionBlobKeys.Index(tag)));

    public Task WriteIndexAsync(ArchivedTagIndex index, CancellationToken cancellationToken = default)
    {
        Events.Add($"tag_index:{index.Tag}");
        Put(AuctionBlobKeys.Index(index.Tag), index);
        return Task.CompletedTask;
    }

    private void Put<T>(string key, T value)
    {
        using var ms = new MemoryStream();
        ArchiveBlobCodec.Write(ms, value);
        Objects[key] = ms.ToArray();
    }

    private T Get<T>(string key) where T : class
    {
        if (!Objects.TryGetValue(key, out var bytes))
            return null;
        using var ms = new MemoryStream(bytes);
        return ArchiveBlobCodec.ReadAsync<T>(ms).GetAwaiter().GetResult();
    }
}

internal static class ArchiveTestConfig
{
    public static IConfiguration Build(params (string key, string value)[] values)
    {
        var dict = new Dictionary<string, string>
        {
            ["ARCHIVE:ENABLED"] = "true",
            ["ARCHIVE:OLDEST_MONTH"] = "2026-08",
            ["ARCHIVE:PAGE_PAUSE_MS"] = "0",
        };
        foreach (var (key, value) in values)
            dict[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(dict).Build();
    }
}

/// <summary>Collects log messages (message text plus formatted values) for assertions.</summary>
internal class ListLogger<T> : Microsoft.Extensions.Logging.ILogger<T>
{
    public readonly List<(Microsoft.Extensions.Logging.LogLevel level, string message)> Entries = new();
    public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;
    public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        => Entries.Add((logLevel, formatter(state, exception) + (exception == null ? "" : " EX " + exception)));
}
