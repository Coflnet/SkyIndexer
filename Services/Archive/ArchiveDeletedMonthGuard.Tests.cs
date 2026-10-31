using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core.Archive;
using Microsoft.EntityFrameworkCore;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

public class ArchiveDeletedMonthGuardTests
{
    private static ArchiveContext Context()
        => new(new DbContextOptionsBuilder<ArchiveContext>().UseMySql("server=localhost", new MariaDbServerVersion("10.5.5")).Options);

    private static MySqlConnector.MySqlException Timeout()
    {
        // MySqlConnector only exposes its server-error constructor internally
        var ctor = typeof(MySqlConnector.MySqlException).GetConstructors(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic)
            .First(c => c.GetParameters().Length > 0 && c.GetParameters()[0].ParameterType == typeof(MySqlConnector.MySqlErrorCode));
        var args = ctor.GetParameters().Select((p, i) => i == 0 ? (object)(MySqlConnector.MySqlErrorCode)1969 : p.ParameterType == typeof(string) ? "max_statement_time exceeded" : p.HasDefaultValue ? p.DefaultValue : null).ToArray();
        return (MySqlConnector.MySqlException)ctor.Invoke(args);
    }

    /// <summary>m1: the check hits the (tag_id, month) primary key points, never a join or an OR over the whole month.</summary>
    [Test]
    public void TagMonthCheckIsAPrimaryKeyLookup()
    {
        using var db = Context();
        var sql = ArchiveDeletedMonthGuard.DeletedRowsQuery(db, new ushort[] { 0, 5 }, 1234).ToQueryString();

        Assert.That(sql, Does.Contain("FROM `archived_months`"));
        Assert.That(sql, Does.Contain("`tag_id` IN (0, 5)"));
        Assert.That(sql, Does.Contain("`month` = @monthIndex"));
        Assert.That(sql, Does.Not.Contain("JOIN").And.Not.Contain("auction_tags"), "no subquery on auction_tags per row");
    }

    [Test]
    public void TagIdIsResolvedThroughTheUniqueTagIndex()
    {
        using var db = Context();
        var sql = ArchiveDeletedMonthGuard.TagIdQuery(db, "HYPERION").ToQueryString();
        Assert.That(sql, Does.Contain("FROM `auction_tags`").And.Contain("`tag` = 'HYPERION'"));
    }

    [Test]
    public async Task GuardReadIsReRunAfterAStatementTimeout()
    {
        var calls = 0;
        var result = await ArchiveDeletedMonthGuard.RetryAsync(() => ++calls < 3 ? throw Timeout() : Task.FromResult(true), new[] { TimeSpan.Zero, TimeSpan.Zero }, CancellationToken.None);

        Assert.That(result, Is.True);
        Assert.That(calls, Is.EqualTo(3));
    }

    [Test]
    public void GuardReadFailsClosedWhenTheTimeoutKeepsHappening()
    {
        var calls = 0;
        Assert.ThrowsAsync<MySqlConnector.MySqlException>(() => ArchiveDeletedMonthGuard.RetryAsync<bool>(() => { calls++; throw Timeout(); }, new[] { TimeSpan.Zero }, CancellationToken.None));
        Assert.That(calls, Is.EqualTo(2), "one retry, then the error propagates (the write is not allowed)");
    }

    [Test]
    public void OtherErrorsAreNotRetried()
    {
        var calls = 0;
        Assert.ThrowsAsync<InvalidOperationException>(() => ArchiveDeletedMonthGuard.RetryAsync<bool>(() => { calls++; throw new InvalidOperationException("boom"); }, new[] { TimeSpan.Zero, TimeSpan.Zero }, CancellationToken.None));
        Assert.That(calls, Is.EqualTo(1));
    }
}
