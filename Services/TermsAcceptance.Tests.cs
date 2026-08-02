using System;
using System.Linq;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer;

public class TermsAcceptanceTests
{
    private static readonly DateTime AcceptedAt = new(2026, 7, 28, 12, 0, 0, DateTimeKind.Utc);
    private const string Hash = "ac8ef2a870fb606c7c8190e982a710dedf061f3238cb64d55e0a456bde6f6039";

    [Test]
    public void ApplyStoresValidatedAcceptance()
    {
        var user = new GoogleUser();

        var changed = user.ApplyTermsAcceptance(new("2026-07-28", Hash.ToUpperInvariant(), AcceptedAt, "web-login"));

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.True);
            Assert.That(user.TermsAcceptedVersion, Is.EqualTo("2026-07-28"));
            Assert.That(user.TermsAcceptedHash, Is.EqualTo(Hash));
            Assert.That(user.TermsAcceptedAtUtc, Is.EqualTo(AcceptedAt));
            Assert.That(user.TermsAcceptanceSource, Is.EqualTo("web-login"));
        });
    }

    [Test]
    public void RepeatingSameAcceptancePreservesOriginalEvidence()
    {
        var user = new GoogleUser();
        user.ApplyTermsAcceptance(new("2026-07-28", Hash, AcceptedAt, "web-login"));

        var changed = user.ApplyTermsAcceptance(new("2026-07-28", Hash, AcceptedAt.AddMinutes(10), "account-page"));

        Assert.Multiple(() =>
        {
            Assert.That(changed, Is.False);
            Assert.That(user.TermsAcceptedAtUtc, Is.EqualTo(AcceptedAt));
            Assert.That(user.TermsAcceptanceSource, Is.EqualTo("web-login"));
        });
    }

    [Test]
    public void OlderDifferentAcceptanceCannotReplaceCurrentEvidence()
    {
        var user = new GoogleUser();
        user.ApplyTermsAcceptance(new("2026-07-28", Hash, AcceptedAt, "web-login"));
        var olderHash = new string('b', 64);

        Assert.Throws<InvalidOperationException>(() =>
            user.ApplyTermsAcceptance(new("2025-01-01", olderHash, AcceptedAt.AddSeconds(-1), "web-login")));
        Assert.That(user.TermsAcceptedVersion, Is.EqualTo("2026-07-28"));
    }

    [TestCase("", Hash, "web-login")]
    [TestCase("2026/07/28", Hash, "web-login")]
    [TestCase("2026-07-28", "not-a-hash", "web-login")]
    [TestCase("2026-07-28", Hash, "web login")]
    public void InvalidEvidenceIsRejected(string version, string hash, string source)
    {
        var user = new GoogleUser();

        Assert.Throws<ArgumentException>(() =>
            user.ApplyTermsAcceptance(new(version, hash, AcceptedAt, source)));
    }

    [Test]
    public void NonUtcTimestampIsRejected()
    {
        var user = new GoogleUser();

        Assert.Throws<ArgumentException>(() =>
            user.ApplyTermsAcceptance(new("2026-07-28", Hash, DateTime.SpecifyKind(AcceptedAt, DateTimeKind.Local), "web-login")));
    }

    [Test]
    public void LedgerRecordIsCanonicalAndTiedToAgreementAndUser()
    {
        var record = new AgreementAcceptanceRecord(
            42, "Data-Export", new("2026-07-28", Hash.ToUpperInvariant(), AcceptedAt, "web-login"));

        Assert.Multiple(() =>
        {
            Assert.That(record.UserId, Is.EqualTo(42));
            Assert.That(record.Agreement, Is.EqualTo("data-export"));
            Assert.That(record.Version, Is.EqualTo("2026-07-28"));
            Assert.That(record.Hash, Is.EqualTo(Hash));
            Assert.That(record.AcceptedAtUtc, Is.EqualTo(AcceptedAt));
            Assert.That(record.Source, Is.EqualTo("web-login"));
        });
    }

    [TestCase("")]
    [TestCase("data export")]
    [TestCase("data/export")]
    public void InvalidAgreementIdentifierIsRejected(string agreement)
    {
        Assert.Throws<ArgumentException>(() => new AgreementAcceptanceRecord(
            42, agreement, new("2026-07-28", Hash, AcceptedAt, "web-login")));
    }

    [Test]
    public void SchemaMigrationIsDiscoverable()
    {
        using var context = new HypixelContext();

        Assert.That(context.Database.GetMigrations(), Does.Contain("20260728000000_terms_acceptance"));
    }

    [Test]
    public void SchemaMigrationCreatesUniqueLedgerAndBackfillsProjection()
    {
        var operations = new termsacceptance().UpOperations;
        var ledger = operations.OfType<CreateTableOperation>().Single(o => o.Name == "AgreementAcceptances");
        var idempotencyIndex = operations.OfType<CreateIndexOperation>()
            .Single(o => o.Name == "IX_AgreementAcceptances_UserId_Agreement_Version_Hash");
        var backfill = operations.OfType<SqlOperation>().Single();

        Assert.Multiple(() =>
        {
            Assert.That(ledger.Columns.Select(c => c.Name), Is.EquivalentTo(new[]
                { "Id", "UserId", "Agreement", "Version", "Hash", "AcceptedAtUtc", "Source" }));
            Assert.That(idempotencyIndex.IsUnique, Is.True);
            Assert.That(idempotencyIndex.Columns, Is.EqualTo(new[] { "UserId", "Agreement", "Version", "Hash" }));
            Assert.That(backfill.Sql, Does.Contain("INSERT INTO `AgreementAcceptances`"));
            Assert.That(backfill.Sql, Does.Contain("'terms'"));
            Assert.That(backfill.Sql, Does.Contain("FROM `Users`"));
        });
    }
}
