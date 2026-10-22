using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

/// <summary>Minimal in-memory S3 built with <see cref="DispatchProxy"/> (no mocking library in this repo).</summary>
public class FakeS3 : DispatchProxy
{
    public Dictionary<string, byte[]> Objects = new();
    public List<PutObjectRequest> Puts = new();
    public int Gets;

    public static (IAmazonS3 client, FakeS3 state) Create()
    {
        var client = Create<IAmazonS3, FakeS3>();
        return (client, (FakeS3)(object)client);
    }

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        switch (targetMethod.Name)
        {
            case nameof(IAmazonS3.PutObjectAsync) when args[0] is PutObjectRequest put:
                using (var ms = new MemoryStream())
                {
                    put.InputStream.CopyTo(ms);
                    Objects[put.Key] = ms.ToArray();
                }
                Puts.Add(put);
                return Task.FromResult(new PutObjectResponse());
            case nameof(IAmazonS3.GetObjectAsync) when args[0] is GetObjectRequest get:
                Gets++;
                if (!Objects.TryGetValue(get.Key, out var bytes))
                    return Task.FromException<GetObjectResponse>(new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound });
                return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(bytes), ContentLength = bytes.Length });
            case nameof(IAmazonS3.GetObjectMetadataAsync) when args[0] is GetObjectMetadataRequest head:
                if (!Objects.ContainsKey(head.Key))
                    return Task.FromException<GetObjectMetadataResponse>(new AmazonS3Exception("not found") { StatusCode = HttpStatusCode.NotFound });
                return Task.FromResult(new GetObjectMetadataResponse());
            case nameof(IDisposable.Dispose):
                return null;
            default:
                throw new NotSupportedException(targetMethod.Name);
        }
    }
}

public class S3AuctionBlobStoreTests
{
    private const string Endpoint = "https://account.eu.r2.cloudflarestorage.com";

    /// <summary>Regression (SkyBazaar 7654d28): assigning RegionEndpoint after ServiceURL sent requests to s3.auto.amazonaws.com.</summary>
    [Test]
    public void BuildConfigKeepsCustomEndpointWhenRegionIsAlsoSet()
    {
        var config = S3AuctionBlobStore.BuildConfig(Endpoint, "auto", pathStyle: true);
        Assert.That(config.ServiceURL?.TrimEnd('/'), Is.EqualTo(Endpoint));
        Assert.That(config.AuthenticationRegion, Is.EqualTo("auto"));
        Assert.That(config.RegionEndpoint, Is.Null);
        Assert.That(config.ForcePathStyle, Is.True);
        Assert.That(config.DetermineServiceURL(), Does.StartWith(Endpoint));
    }

    [Test]
    public void BuildConfigUsesRegionEndpointWithoutCustomEndpoint()
    {
        var config = S3AuctionBlobStore.BuildConfig(null, "eu-central-1", pathStyle: false);
        Assert.That(config.RegionEndpoint, Is.EqualTo(RegionEndpoint.EUCentral1));
        Assert.That(config.ServiceURL, Is.Null);
    }

    [TestCase("false", "bucket")]
    [TestCase("true", "")]
    public async Task DisabledWithoutCompleteConfig(string enabled, string bucket)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string>
        {
            ["S3:ENABLED"] = enabled, ["S3:BUCKET"] = bucket, ["S3:ACCESS_KEY"] = "x", ["S3:SECRET_KEY"] = "y", ["S3:ENDPOINT"] = Endpoint,
        }).Build();
        using var store = new S3AuctionBlobStore(config, NullLogger<S3AuctionBlobStore>.Instance);
        Assert.That(store.IsEnabled, Is.False);
        Assert.That(await store.ReadBlockAsync("HYPERION", 202609, 0), Is.Null);
    }

    private static ArchivedAuctionBlock Block(int itemBytes = 50)
    {
        var rows = Enumerable.Range(1, 3).Select(i => new SaveAuction
        {
            Uuid = i.ToString("x32"), UId = i, Tag = "HYPERION", End = new DateTime(2026, 9, 1).AddHours(i), Start = new DateTime(2026, 8, 30),
            NbtData = new NbtData { data = Enumerable.Repeat((byte)i, itemBytes).ToArray() },
        });
        return ArchivedAuctionBlock.FromAuctions("HYPERION", 202609, 0, ArchivedAuctionBlock.SourceMariaDb, rows);
    }

    private static (S3AuctionBlobStore store, FakeS3 s3) Store(long cacheBytes = 256L * 1024 * 1024)
    {
        var (client, s3) = FakeS3.Create();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string> { ["S3:CACHE_SIZE_BYTES"] = cacheBytes.ToString() }).Build();
        return (new S3AuctionBlobStore(client, "sky-auctions-archive", config, NullLogger<S3AuctionBlobStore>.Instance), s3);
    }

    [Test]
    public async Task WriteThenUncachedReadRoundTripsUnderTheV2Key()
    {
        var (store, s3) = Store();
        var block = Block();
        await store.WriteBlockAsync(block);
        var put = s3.Puts.Single();
        Assert.That(put.Key, Is.EqualTo("auctions/v2/HYPERION/2026-09/000.msgpack.br"));
        Assert.That(put.ContentType, Is.EqualTo("application/x-msgpack+br"));
        Assert.That(put.DisablePayloadSigning, Is.True);
        Assert.That(put.BucketName, Is.EqualTo("sky-auctions-archive"));
        var read = await store.ReadBlockAsync("HYPERION", 202609, 0, uncached: true);
        Assert.That(block.SameRows(read), Is.True);
        Assert.That(await store.BlockExistsAsync("HYPERION", 202609, 0), Is.True);
        Assert.That(await store.BlockExistsAsync("HYPERION", 202609, 1), Is.False);
    }

    [Test]
    public async Task CachedReadsHitTheCacheAndUncachedReadsBypassIt()
    {
        var (store, s3) = Store();
        await store.WriteBlockAsync(Block());
        await store.ReadBlockAsync("HYPERION", 202609, 0);
        await store.ReadBlockAsync("HYPERION", 202609, 0);
        Assert.That(s3.Gets, Is.EqualTo(1));
        await store.ReadBlockAsync("HYPERION", 202609, 0, uncached: true);
        Assert.That(s3.Gets, Is.EqualTo(2));
    }

    [Test]
    public async Task BlockLargerThanTheCacheBoundIsNotCached()
    {
        var block = Block(itemBytes: 20_000);
        var (store, s3) = Store(cacheBytes: block.EstimatedBytes() / 2);
        await store.WriteBlockAsync(block);
        await store.ReadBlockAsync("HYPERION", 202609, 0);
        await store.ReadBlockAsync("HYPERION", 202609, 0);
        Assert.That(s3.Gets, Is.EqualTo(2), "the decoded size is billed, so the entry never fits");
    }

    [Test]
    public async Task MissingObjectsReadAsNullAndIndexRoundTrips()
    {
        var (store, _) = Store();
        Assert.That(await store.ReadBlockAsync("HYPERION", 202609, 0), Is.Null);
        Assert.That(await store.ReadIndexAsync("HYPERION"), Is.Null);
        await store.WriteIndexAsync(new ArchivedTagIndex { Tag = "HYPERION" }.Upsert(new ArchivedMonthEntry { Month = 202609, Rows = 3 }));
        Assert.That((await store.ReadIndexAsync("HYPERION")).Find(202609).Rows, Is.EqualTo(3));
    }
}
