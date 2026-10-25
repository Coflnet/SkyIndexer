using System.Collections.Generic;
using Coflnet.Sky.Core;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Tests;

public class IndexerCancelledStubsTests
{
    [Test]
    public void DropsCancellationStubsAndKeepsRealAuctions()
    {
        var real = new SaveAuction { Uuid = "fbc87f87b71346e1ba5516649ae1cce4", UId = -303852753978692069, AuctioneerId = "a", Tag = "PET_SLUG" };
        var stub = new SaveAuction { Uuid = null, UId = -303852753978692069, AuctioneerId = "a", Context = new Dictionary<string, string> { ["cancelled"] = "#cofl" } };
        var stubWithUuid = new SaveAuction { Uuid = "fbc87f87b71346e1ba5516649ae1cce4", UId = -303852753978692069, AuctioneerId = "a", Context = new Dictionary<string, string> { ["cancelled"] = "pre-api" } };
        var noUuid = new SaveAuction { Uuid = "", UId = 5, AuctioneerId = "b" };
        var noUid = new SaveAuction { Uuid = "c", UId = 0, AuctioneerId = "b" };

        var kept = Indexer.DropCancelledStubs(new[] { real, stub, stubWithUuid, noUuid, noUid, null });

        Assert.That(kept, Is.EquivalentTo(new[] { real }));
    }
}
