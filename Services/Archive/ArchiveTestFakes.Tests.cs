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
internal class InMemoryAuctionBlobStore : IAuctionBlobStore, IArchiveMetaStore
{
    public readonly Dictionary<string, byte[]> Objects = new();
    public readonly List<string> Events;
    /// <summary>When set, uncached block reads flip one item byte, like a corrupted upload.</summary>
    public bool CorruptReadBack;

    /// <summary>Version per conditionally written key (tag indexes, meta objects); the ETag is <c>"v{n}"</c>.</summary>
    private readonly Dictionary<string, int> versions = new();
    /// <summary>Runs once before the next conditional meta write is checked (another writer in between).</summary>
    public Func<string, Task> BeforeMetaWrite;
    /// <summary>Runs once before the next index write is checked, e.g. another writer updating the same tag index in between.</summary>
    public Func<ArchivedTagIndex, Task> BeforeIndexWrite;
    public int IndexConflicts;

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
    {
        var key = AuctionBlobKeys.Index(tag);
        var index = Get<ArchivedTagIndex>(key);
        if (index != null)
            index.ETag = ETagOf(key);
        return Task.FromResult(index);
    }

    /// <summary>Conditional like the S3 store: If-Match the read ETag, If-None-Match * for a new index.</summary>
    public async Task WriteIndexAsync(ArchivedTagIndex index, CancellationToken cancellationToken = default)
    {
        var hook = BeforeIndexWrite;
        BeforeIndexWrite = null;
        if (hook != null)
            await hook(index);
        var key = AuctionBlobKeys.Index(index.Tag);
        CheckCondition(key, index.ETag, index.Tag, $"tag_index_conflict:{index.Tag}");
        Events.Add($"tag_index:{index.Tag}");
        index.ETag = PutVersioned(key, index);
    }

    public Task<T> ReadMetaAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
        => Task.FromResult(Get<T>(AuctionBlobKeys.Meta(key)));

    public Task<(T Value, string ETag)> ReadMetaVersionedAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        var full = AuctionBlobKeys.Meta(key);
        var value = Get<T>(full);
        return Task.FromResult((value, value == null ? null : ETagOf(full)));
    }

    public Task WriteMetaAsync<T>(string key, T value, CancellationToken cancellationToken = default) where T : class
    {
        Events.Add("meta:" + key);
        PutVersioned(AuctionBlobKeys.Meta(key), value);
        return Task.CompletedTask;
    }

    public async Task<string> WriteMetaConditionalAsync<T>(string key, T value, string etag, CancellationToken cancellationToken = default) where T : class
    {
        var full = AuctionBlobKeys.Meta(key);
        var hook = BeforeMetaWrite;
        BeforeMetaWrite = null;
        if (hook != null)
            await hook(full);
        CheckCondition(full, etag, full, "meta_conflict:" + key);
        Events.Add("meta:" + key);
        return PutVersioned(full, value);
    }

    public Task<IReadOnlyList<string>> ListMetaKeysAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var full = AuctionBlobKeys.Meta(prefix);
        IReadOnlyList<string> keys = Objects.Keys.Where(k => k.StartsWith(full, StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        return Task.FromResult(keys);
    }

    private void CheckCondition(string key, string etag, string name, string conflictEvent)
    {
        if (etag == ETagOf(key))
            return;
        IndexConflicts++;
        Events.Add(conflictEvent);
        throw new ArchiveIndexConflictException(name);
    }

    private string PutVersioned<T>(string key, T value)
    {
        Put(key, value);
        versions[key] = versions.GetValueOrDefault(key) + 1;
        return ETagOf(key);
    }

    private string ETagOf(string key) => Objects.ContainsKey(key) && versions.TryGetValue(key, out var v) ? $"\"v{v}\"" : null;

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
