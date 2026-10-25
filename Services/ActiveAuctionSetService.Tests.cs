using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Indexer.Controllers;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer;

public class ActiveAuctionSetServiceTests
{
    private class FakeLoader : ActiveAuctionDbLoader
    {
        public List<SaveAuction> Stored = new();
        public List<long[]> UidQueries = new();

        protected override Task<int> MaxIdAsync(CancellationToken ct) => Task.FromResult(10);
        protected override Task<(int Id, DateTime Start)?> ProbeAsync(int fromId, CancellationToken ct)
            => Task.FromResult<(int, DateTime)?>((fromId, DateTime.UtcNow.AddDays(-30)));

        protected override async IAsyncEnumerable<SaveAuction> QueryChunkAsync(int from, int to, DateTime now, [EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            foreach (var a in Stored)
                yield return Copy(a);
        }

        protected override Task<List<SaveAuction>> QueryByUidsAsync(long[] uids, DateTime now, CancellationToken ct)
        {
            UidQueries.Add(uids);
            return Task.FromResult(Stored.Where(a => uids.Contains(a.UId)).Select(Copy).ToList());
        }

        private static SaveAuction Copy(SaveAuction a) => ActiveAuctionSetService.CloneForActiveSet(a);
    }

    private FakeLoader loader;
    private ActiveAuctionSetService service;

    private static SaveAuction Auction(long uid, string seller = "seller", string tag = "TAG", int hoursLeft = 1)
        => new()
        {
            UId = uid,
            Uuid = uid.ToString("x32"),
            Tag = tag,
            AuctioneerId = seller,
            Start = DateTime.UtcNow.AddHours(-1),
            End = DateTime.UtcNow.AddHours(hoursLeft),
            Context = new() { ["k"] = "v" },
            Bids = new(),
            Enchantments = new()
        };

    [SetUp]
    public void Setup()
    {
        loader = new FakeLoader();
        var details = new ItemDetails(null);
        details.TagLookup["TAG"] = 42;
        service = new ActiveAuctionSetService(loader, details, NullLogger<ActiveAuctionSetService>.Instance);
    }

    [Test]
    public async Task LoadMarksReadyAndSetsItemId()
    {
        loader.Stored.Add(Auction(1));
        loader.Stored.Add(Auction(2));
        await service.LoadAsync(CancellationToken.None);
        Assert.That(service.Set.IsReady);
        Assert.That(service.Set.Count, Is.EqualTo(2));
        service.Set.TryGet(1, out var a);
        Assert.That(a.ItemId, Is.EqualTo(42));
    }

    [Test]
    public void UpsertActiveClonesAndSkipsEnded()
    {
        var original = Auction(5);
        var ended = Auction(6, hoursLeft: -1);
        service.UpsertActive(new[] { original, ended }, "test");
        Assert.That(service.Set.Count, Is.EqualTo(1));
        Assert.That(original.Context, Is.Not.Null, "the original must not be normalised");
        service.Set.TryGet(5, out var stored);
        Assert.That(stored, Is.Not.SameAs(original));
        Assert.That(stored.Context, Is.Null);
        Assert.That(stored.ItemId, Is.EqualTo(42));
    }

    [Test]
    public void RemoveActiveRemoves()
    {
        service.UpsertActive(new[] { Auction(1), Auction(2) }, "test");
        service.RemoveActive(new long[] { 1 }, RemoveReason.Sold);
        Assert.That(service.Set.TryGet(1, out _), Is.False);
        Assert.That(service.Set.TryGet(2, out _), Is.True);
    }

    [Test]
    public async Task ReconcileFetchesOnlyMissing()
    {
        service.Set.MarkReady();
        service.UpsertActive(new[] { Auction(1) }, "test");
        loader.Stored.Add(Auction(1));
        loader.Stored.Add(Auction(2));
        await service.ReconcileWithSummaryAsync(new long[] { 1, 2 }, CancellationToken.None);
        Assert.That(loader.UidQueries.SelectMany(q => q).ToArray(), Is.EqualTo(new long[] { 2 }));
        Assert.That(service.Set.TryGet(2, out _), Is.True);
    }

    [Test]
    public async Task ReconcileCapsAt5000()
    {
        service.Set.MarkReady();
        await service.ReconcileWithSummaryAsync(Enumerable.Range(1, 7000).Select(i => (long)i).ToList(), CancellationToken.None);
        Assert.That(loader.UidQueries.Sum(q => q.Length), Is.EqualTo(5000));
    }

    [Test]
    public async Task MarkCancelledRemovesFromSet()
    {
        var a = Auction(9);
        service.UpsertActive(new[] { a }, "test");
        await service.MarkCancelled(new[] { a });
        Assert.That(service.Set.TryGet(9, out _), Is.False);
    }

    [Test]
    public void ActiveUidsForSeller()
    {
        service.UpsertActive(new[] { Auction(1, "a"), Auction(2, "a"), Auction(3, "b") }, "test");
        service.Set.RebuildSnapshotIfDirty();
        Assert.That(service.ActiveUidsForSeller("a").OrderBy(u => u).ToArray(), Is.EqualTo(new long[] { 1, 2 }));
        Assert.That(service.ActiveUidsForSeller("none"), Is.Empty);
    }

    private ActiveController Controller(out MemoryStream body)
    {
        body = new MemoryStream();
        var controller = new ActiveController(service) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() } };
        controller.Response.Body = body;
        return controller;
    }

    [Test]
    public async Task ControllerStreamsAuctionsRoundTrip()
    {
        service.UpsertActive(Enumerable.Range(1, 2500).Select(i => Auction(i)).ToList(), "test");
        service.Set.MarkReady();
        var controller = Controller(out var body);
        await controller.GetAuctions();
        Assert.That(controller.Response.Headers["X-Total-Count"].ToString(), Is.EqualTo("2500"));
        Assert.That(controller.Response.ContentType, Is.EqualTo("application/x-msgpack"));
        body.Position = 0;
        var back = new List<SaveAuction>();
        await foreach (var a in ActiveAuctionSetSerializer.DeserializeAsync(body, CancellationToken.None))
            back.Add(a);
        Assert.That(back.Count, Is.EqualTo(2500));
        Assert.That(back[0].ItemId, Is.EqualTo(42));
    }

    [Test]
    public async Task ControllerReturns503WhenNotReady()
    {
        var result = await Controller(out _).GetAuctions();
        Assert.That(((ObjectResult)result).StatusCode, Is.EqualTo(503));
        Assert.That(((ObjectResult)Controller(out _).GetSellerUids("x")).StatusCode, Is.EqualTo(503));
    }

    [Test]
    public void ControllerSellerUids()
    {
        service.UpsertActive(new[] { Auction(1, "a"), Auction(2, "b") }, "test");
        service.Set.MarkReady();
        service.Set.RebuildSnapshotIfDirty();
        var result = (OkObjectResult)Controller(out _).GetSellerUids("a");
        Assert.That((long[])result.Value, Is.EqualTo(new long[] { 1 }));
    }

    [Test]
    public async Task ControllerSingleAuction404()
    {
        var result = await Controller(out _).GetAuction(123);
        Assert.That(result, Is.TypeOf<NotFoundResult>());
    }
}
