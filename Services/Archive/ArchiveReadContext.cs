using System.Text.RegularExpressions;
using Coflnet.Sky.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.Indexer.Archive
{
    /// <summary>
    /// Opens the <see cref="HypixelContext"/> the archive job READS auctions with: <c>DBReadOnlyConnection</c> (read replica)
    /// when configured and non-blank, otherwise the default <c>DBConnection</c>. Writes (<c>ArchiveContext</c>) and the
    /// retention worker stay on the primary. Operator note: set <c>DBReadOnlyConnection</c> in the indexer's OpenBao secret
    /// (kv/sky/sky-indexer) and restart; raise <c>ARCHIVE__CHILD_QUERY_BATCH_SIZE</c> only after <c>ANALYZE TABLE UuId</c>
    /// on the primary (<c>IX_UuId_SaveAuctionId</c> reports cardinality 2, large IN lists then full-scan).
    /// </summary>
    public static class ArchiveReadContext
    {
        /// <summary>True when a non-blank <c>DBReadOnlyConnection</c> is configured.</summary>
        public static bool UsesReadReplica(IConfiguration config) => !string.IsNullOrWhiteSpace(config["DBReadOnlyConnection"]);

        /// <summary>Open a context for reads.</summary>
        public static HypixelContext Open(IConfiguration config)
        {
            if (!UsesReadReplica(config))
                return new HypixelContext();
            var version = config["DBVersion"];
            if (string.IsNullOrWhiteSpace(version))
                version = "10.3";
            var options = new DbContextOptionsBuilder<HypixelContext>()
                .UseMySql(config["DBReadOnlyConnection"], new MariaDbServerVersion(version), o => o.CommandTimeout(60).MaxBatchSize(100))
                .Options;
            return new HypixelContext(options);
        }

        /// <summary>Log text naming the read source: only the <c>server=</c>/<c>port=</c> parts, never user or password.</summary>
        public static string ReadConnectionDescription(IConfiguration config)
        {
            if (!UsesReadReplica(config))
                return "DBConnection (no DBReadOnlyConnection configured)";
            var conn = config["DBReadOnlyConnection"];
            return $"DBReadOnlyConnection (server={Part(conn, "server|host|data source")}, port={Part(conn, "port")})";
        }

        private static string Part(string conn, string keys)
        {
            var m = Regex.Match(conn, $@"(?:^|;)\s*(?:{keys})\s*=\s*([^;]*)", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value.Trim() : "default";
        }
    }
}
