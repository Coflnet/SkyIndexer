using Microsoft.EntityFrameworkCore;
using System.Collections.Generic;
using Microsoft.Extensions.Configuration;
using NUnit.Framework;

namespace Coflnet.Sky.Indexer.Archive;

public class ArchiveReadContextTests
{
    private const string Replica = "server=replica.example;port=3307;user=ro_user;password=s3cret;database=skyblock";

    private static IConfiguration Config(params (string, string)[] values)
    {
        var d = new Dictionary<string, string>();
        foreach (var (k, v) in values) d[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(d).Build();
    }

    [Test]
    public void ReadOnlyConnectionIsUsedWhenConfigured()
    {
        var config = Config(("DBConnection", "server=primary;port=3306;user=rw;password=x"), ("DBReadOnlyConnection", Replica));
        Assert.That(ArchiveReadContext.UsesReadReplica(config), Is.True);
        using var db = ArchiveReadContext.Open(config);
        // the driver normalizes the string (key casing), so compare the settings, not the text
        var conn = db.Database.GetConnectionString();
        Assert.That(conn, Does.Contain("replica.example").And.Contain("3307").And.Contain("ro_user").And.Not.Contain("primary"));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void FallsBackToDbConnectionWhenReadOnlyIsBlank(string value)
    {
        var config = Config(("DBConnection", "server=primary;port=3306"), ("DBReadOnlyConnection", value));
        Assert.That(ArchiveReadContext.UsesReadReplica(config), Is.False);
        Assert.That(ArchiveReadContext.ReadConnectionDescription(config), Does.Contain("no DBReadOnlyConnection configured"));
        Assert.That(ArchiveReadContext.UsesReadReplica(Config()), Is.False);
    }

    [Test]
    public void DescriptionShowsServerAndPortOnly()
    {
        var text = ArchiveReadContext.ReadConnectionDescription(Config(("DBReadOnlyConnection", Replica)));
        Assert.That(text, Does.Contain("server=replica.example"));
        Assert.That(text, Does.Contain("port=3307"));
        Assert.That(text, Does.Not.Contain("s3cret"));
        Assert.That(text, Does.Not.Contain("ro_user"));
        Assert.That(text, Does.Not.Contain("password"));
        Assert.That(text, Does.Not.Contain("skyblock"));
    }

    [Test]
    public void DescriptionCannotMistakeQuotedPasswordPartsForEndpoints()
    {
        var text = ArchiveReadContext.ReadConnectionDescription(Config(("DBReadOnlyConnection",
            "Password=\";server=synthetic-secret;port=9999\";Server=replica.example;Port=3307")));
        Assert.That(text, Does.Contain("server=replica.example").And.Contain("port=3307"));
        Assert.That(text, Does.Not.Contain("synthetic-secret").And.Not.Contain("9999"));
    }
}
