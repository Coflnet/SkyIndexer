using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Confluent.Kafka;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer;

public class ActiveAhStateServiceTests
{
    /// <summary>Minimal IProducer that only counts Produce/Flush calls</summary>
    public class CountingProducer : DispatchProxy
    {
        public int Produced;
        public int Flushed;
        public bool FailFlush;
        public int Disposed;

        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.Name == "Produce")
                Produced++;
            else if (targetMethod.Name == "Dispose")
                Disposed++;
            else if (targetMethod.Name == "Flush")
            {
                Flushed++;
                if (FailFlush)
                    throw new InvalidOperationException("broken producer");
                return 0;
            }
            return targetMethod.ReturnType.IsValueType && targetMethod.ReturnType != typeof(void)
                ? Activator.CreateInstance(targetMethod.ReturnType)
                : null;
        }
    }

    private static ActiveAhStateService Create(Dictionary<string, string> settings = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings ?? new()).Build();
        return new ActiveAhStateService(config, null, new ActivitySource("test"), null, null);
    }

    private static AhStateSumary Summary(int count, DateTime time, DateTime end)
    {
        var sum = new AhStateSumary { Time = time, Part = 1, PartCount = 2 };
        for (long uid = 1; uid <= count; uid++)
            sum.ActiveAuctions[uid] = end.Ticks;
        return sum;
    }

    [Test]
    public void SampleDenominatorTenOnlySelectsMatchingModulo()
    {
        var service = Create(new() { ["ACTIVE_STATE:REACTIVATE_SAMPLE_DENOMINATOR"] = "10" });
        service.MinuteSource = () => 33; // 33 % 10 == 3
        var ids = service.SelectIdsToCheck(Summary(100, DateTime.UtcNow, DateTime.UtcNow.AddHours(1)));
        Assert.That(ids, Is.EquivalentTo(Enumerable.Range(0, 10).Select(i => (long)(i * 10 + 3))));
    }

    [Test]
    public void DefaultDenominatorIsTenAndOneChecksEverything()
    {
        var sum = Summary(100, DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        Assert.That(Create().SelectIdsToCheck(sum).Count, Is.EqualTo(10));
        var all = Create(new() { ["ACTIVE_STATE:REACTIVATE_SAMPLE_DENOMINATOR"] = "1" });
        Assert.That(all.SelectIdsToCheck(sum).Count, Is.EqualTo(100));
    }

    [Test]
    public void WhipedAndAlmostExpiredAreSkipped()
    {
        var service = Create(new() { ["ACTIVE_STATE:REACTIVATE_SAMPLE_DENOMINATOR"] = "1" });
        service.IsWhiped = uid => uid <= 5;
        var sum = Summary(10, DateTime.UtcNow, DateTime.UtcNow.AddHours(1));
        sum.ActiveAuctions[10] = DateTime.UtcNow.AddSeconds(10).Ticks;
        Assert.That(service.SelectIdsToCheck(sum), Is.EquivalentTo(new long[] { 6, 7, 8, 9 }));
    }

    [Test]
    public async Task ReactivationQueriesOnlySampledIds()
    {
        var service = Create(new() { ["ACTIVE_STATE:REACTIVATE_SAMPLE_DENOMINATOR"] = "10" });
        service.MinuteSource = () => 7;
        List<long> queried = null;
        service.FindActiveAgain = (_, ids, _) =>
        {
            queried = ids;
            return Task.FromResult(new List<SaveAuction>());
        };
        await service.ReactiveFalsyDeactivated(Summary(1000, DateTime.UtcNow, DateTime.UtcNow.AddHours(1)), null);
        Assert.That(queried, Has.Count.EqualTo(100));
        Assert.That(queried.All(id => id % 10 == 7));
    }

    [Test]
    public void ProducerIsCreatedOnceAcrossCalls()
    {
        var service = Create(new() { ["TOPICS:AUCTION_CHECK"] = "check" });
        var created = 0;
        var proxy = DispatchProxy.Create<IProducer<string, SaveAuction>, CountingProducer>();
        service.ProducerFactory = () =>
        {
            created++;
            return proxy;
        };
        var list = new List<SaveAuction> { new() { UId = 1, Uuid = "a" }, new() { UId = 2, Uuid = "b" } };
        service.RequestCheck(list);
        service.RequestCheck(list);
        var counter = (CountingProducer)(object)proxy;
        Assert.That(created, Is.EqualTo(1));
        Assert.That(counter.Produced, Is.EqualTo(4));
        Assert.That(counter.Flushed, Is.EqualTo(2));
    }

    [Test]
    public void NegativeUidsAreSelectedExactlyOnceAndAllAreCovered()
    {
        var service = Create(new() { ["ACTIVE_STATE:REACTIVATE_SAMPLE_DENOMINATOR"] = "10" });
        var sum = new AhStateSumary { Time = DateTime.UtcNow };
        var uids = new long[] { -1, -7, -10, -123456789, long.MinValue, 0, 5, 12, long.MaxValue, -9223372036854775807 };
        foreach (var uid in uids)
            sum.ActiveAuctions[uid] = DateTime.UtcNow.AddHours(1).Ticks;
        var selected = new List<long>();
        for (var minute = 0; minute < 10; minute++)
        {
            service.MinuteSource = () => minute;
            selected.AddRange(service.SelectIdsToCheck(sum));
        }
        Assert.That(selected, Is.EquivalentTo(uids));
    }

    [Test]
    public void ProducerFactoryFailureIsRetriedOnNextCall()
    {
        var service = Create(new() { ["TOPICS:AUCTION_CHECK"] = "check" });
        var attempts = 0;
        var proxy = DispatchProxy.Create<IProducer<string, SaveAuction>, CountingProducer>();
        service.ProducerFactory = () =>
        {
            if (attempts++ == 0)
                throw new InvalidOperationException("kafka down");
            return proxy;
        };
        var list = new List<SaveAuction> { new() { UId = 1, Uuid = "a" } };
        Assert.Throws<InvalidOperationException>(() => service.RequestCheck(list));
        service.RequestCheck(list);
        service.RequestCheck(list);
        Assert.That(attempts, Is.EqualTo(2));
        Assert.That(((CountingProducer)(object)proxy).Produced, Is.EqualTo(2));
    }

    [Test]
    public void BrokenProducerIsDisposedAndRecreated()
    {
        var service = Create(new() { ["TOPICS:AUCTION_CHECK"] = "check" });
        var proxies = new List<IProducer<string, SaveAuction>>();
        service.ProducerFactory = () =>
        {
            var p = DispatchProxy.Create<IProducer<string, SaveAuction>, CountingProducer>();
            ((CountingProducer)(object)p).FailFlush = proxies.Count == 0;
            proxies.Add(p);
            return p;
        };
        var list = new List<SaveAuction> { new() { UId = 1, Uuid = "a" } };
        Assert.Throws<InvalidOperationException>(() => service.RequestCheck(list));
        service.RequestCheck(list);
        Assert.That(proxies, Has.Count.EqualTo(2));
        Assert.That(((CountingProducer)(object)proxies[0]).Disposed, Is.EqualTo(1));
    }
}
