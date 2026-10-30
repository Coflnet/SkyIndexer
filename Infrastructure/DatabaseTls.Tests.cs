using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Coflnet.Sky.Core;
using Coflnet.Sky.Core.Archive;
using Coflnet.Sky.Indexer.Archive;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using MySqlConnector;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Infrastructure;

[NonParallelizable]
public class DatabaseTlsTests
{
    private Func<string, MySqlConnection> previousFactory;
    private IConfiguration previousPrimary, previousArchive;
    private static IConfiguration Configuration(Dictionary<string, string> values) => new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    private static IConfiguration Previous(Type type) => (IConfiguration)type.GetField("_config", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);

    [SetUp]
    public void SaveConfiguration()
    {
        previousFactory = HypixelContext.ConnectionFactory;
        previousPrimary = Previous(typeof(HypixelContext));
        previousArchive = Previous(typeof(ArchiveContext));
        var config = Configuration(new() { ["DBConnection"] = "Server=primary;Database=fixture", ["DBVersion"] = "10.3" });
        HypixelContext.SetConfiguration(config);
        ArchiveContext.SetConfiguration(config);
        HypixelContext.ConnectionFactory = null;
    }

    [TearDown]
    public void RestoreConfiguration()
    {
        HypixelContext.ConnectionFactory = previousFactory;
        HypixelContext.SetConfiguration(previousPrimary);
        ArchiveContext.SetConfiguration(previousArchive);
    }

    private static DatabaseTls Tls() => new(Configuration(new()
    {
        ["DB:TLS:CA_CERT_PATH"] = "missing-roots", ["DB:TLS:CERT_PATH"] = "missing-cert", ["DB:TLS:KEY_PATH"] = "missing-key"
    }));

    [TestCase(0u, 300u)]
    [TestCase(40u, 40u)]
    [TestCase(900u, 300u)]
    public void FactoryPreservesAuthenticationAndFailoverButRequiresStrictPhysicalTls(uint lifetime, uint expected)
    {
        using var connection = Tls().CreateConnection($"Server=db-a,db-b;Port=3307;User ID=fixture;Password=synthetic;Database=fixture;MaximumPoolSize=23;ConnectionTimeout=90;ConnectionLifeTime={lifetime};SslMode=None;CertificateFile=old.pfx;CertificatePassword=synthetic;CertificateThumbprint=old;CertificateStoreLocation=CurrentUser;SslCa=old-ca;SslCert=old-cert;SslKey=old-key");
        var options = new MySqlConnectionStringBuilder(connection.ConnectionString);
        Assert.Multiple(() =>
        {
            Assert.That(options.Server, Is.EqualTo("db-a,db-b"));
            Assert.That(options.Port, Is.EqualTo(3307));
            Assert.That(options.UserID, Is.EqualTo("fixture"));
            Assert.That(options.Password, Is.EqualTo("synthetic"));
            Assert.That(options.Database, Is.EqualTo("fixture"));
            Assert.That(options.MaximumPoolSize, Is.EqualTo(23));
            Assert.That(options.ConnectionTimeout, Is.EqualTo(10));
            Assert.That(options.ConnectionLifeTime, Is.EqualTo(expected));
            Assert.That(options.SslMode, Is.EqualTo(MySqlSslMode.Required));
            Assert.That(options.SslCa + options.SslCert + options.SslKey + options.CertificateFile + options.CertificatePassword + options.CertificateThumbprint, Is.Empty);
            Assert.That(options.CertificateStoreLocation, Is.EqualTo(MySqlCertificateStoreLocation.None));
            Assert.That(connection.ProvideClientCertificatesCallback, Is.Not.Null);
            Assert.That(connection.RemoteCertificateValidationCallback, Is.Not.Null);
            Assert.That(connection.RemoteCertificateValidationCallback(new object(), null, null, SslPolicyErrors.RemoteCertificateNameMismatch), Is.False);
        });
        Assert.Throws<FileNotFoundException>(() => connection.ProvideClientCertificatesCallback(new()));
    }

    [Test]
    public void OptOutKeepsLegacyConnectionAndIncompleteTlsFailsClosed()
    {
        var owner = new DatabaseTls(Configuration(new()));
        owner.Install();
        Assert.That(HypixelContext.ConnectionFactory, Is.Null);
        using var connection = owner.CreateConnection("Server=legacy;SslMode=None;ConnectionTimeout=90");
        var options = new MySqlConnectionStringBuilder(connection.ConnectionString);
        Assert.That(options.SslMode, Is.EqualTo(MySqlSslMode.None));
        Assert.That(options.ConnectionTimeout, Is.EqualTo(90));
        Assert.That(options.ConnectionLifeTime, Is.Zero);
        Assert.That(connection.RemoteCertificateValidationCallback, Is.Null);
        Assert.Throws<InvalidOperationException>(() => new DatabaseTls(Configuration(new() { ["DB:TLS:CA_CERT_PATH"] = "roots" })));
    }

    [TestCase(false)]
    [TestCase(true)]
    public async Task BothDefaultContextsOwnFactoryConnections(bool archive)
    {
        MySqlConnection created = null;
        HypixelContext.ConnectionFactory = raw => created = new MySqlConnection(raw);
        DbContext database = archive ? new ArchiveContext() : new HypixelContext();
        Assert.That(new MySqlConnectionStringBuilder(created.ConnectionString).Server, Is.EqualTo("primary"));
        Assert.That(database.Database.GetDbConnection(), Is.SameAs(created));
        Assert.That(database.Database.GetCommandTimeout(), Is.EqualTo(60));
        if (archive)
            Assert.That(database.GetService<IHistoryRepository>().GetCreateScript(), Does.Contain(ArchiveContext.MigrationsHistoryTable));
        if (archive)
            await database.DisposeAsync();
        else
            database.Dispose();
        Assert.Throws<ObjectDisposedException>(() => created.Open());
    }

    [Test]
    public void ExplicitArchiveReaderUsesReplicaAndOwnsItsConnection()
    {
        Tls().Install();
        var config = Configuration(new() { ["DBReadOnlyConnection"] = "Server=replica;Port=3307;Database=archive;ConnectionTimeout=90" });
        var database = ArchiveReadContext.Open(config);
        var connection = (MySqlConnection)database.Database.GetDbConnection();
        var options = new MySqlConnectionStringBuilder(connection.ConnectionString);
        Assert.That(options.Server, Is.EqualTo("replica"));
        Assert.That(options.Port, Is.EqualTo(3307));
        Assert.That(options.SslMode, Is.EqualTo(MySqlSslMode.Required));
        Assert.That(database.Database.GetCommandTimeout(), Is.EqualTo(60));
        database.Dispose();
        Assert.Throws<ObjectDisposedException>(() => connection.Open());
    }

    [Test]
    public void UnsetFactoryKeepsExplicitProviderAndSensitiveLoggingIsOff()
    {
        using var database = new HypixelContext(new DbContextOptionsBuilder<HypixelContext>()
            .UseMySql("Server=explicit", new MariaDbServerVersion("10.3")).Options);
        Assert.That(new MySqlConnectionStringBuilder(database.Database.GetConnectionString()).Server, Is.EqualTo("explicit"));
        using var defaultDatabase = new HypixelContext();
        Assert.That(defaultDatabase.GetService<IDbContextOptions>().FindExtension<CoreOptionsExtension>().IsSensitiveDataLoggingEnabled, Is.False);
    }

    [Test]
    public void RawOptOutSyncAndAsyncLoadsUseTheInstalledFactory()
    {
        var seen = new List<string>();
        HypixelContext.ConnectionFactory = raw =>
        {
            seen.Add(raw);
            throw new InvalidOperationException("synthetic factory sentinel");
        };
        const string raw = "Server=127.0.0.1;Port=1";
        Assert.Throws<InvalidOperationException>(() => PlayerOptOut.Load(raw));
        Assert.ThrowsAsync<InvalidOperationException>(() => PlayerOptOut.LoadAsync(raw));
        Assert.That(seen, Is.EqualTo(new[] { raw, raw }));
    }

    [Test]
    public void PrimaryReadinessFailureIsBoundedAndGeneric()
    {
        using var database = new HypixelContext(new DbContextOptionsBuilder<HypixelContext>()
            .UseMySql("Server=127.0.0.1;Port=1;User ID=fixture;Password=synthetic-private;ConnectionTimeout=1", new MariaDbServerVersion("10.3")).Options);
        var failure = Assert.ThrowsAsync<InvalidOperationException>(() => DatabaseReadiness.CheckAsync(database, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6)));
        Assert.That(failure.Message, Is.EqualTo("The required database readiness check failed."));
        Assert.That(failure.InnerException, Is.Null);
    }
}
