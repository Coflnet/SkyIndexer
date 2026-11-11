using Coflnet.Sky.Core.Archive;
using Microsoft.Extensions.Configuration;

namespace Coflnet.Sky.Indexer.Archive
{
    /// <summary>
    /// <c>ARCHIVE:*</c> settings of the auction archive job and the retention worker (defaults as in appsettings.json).
    /// </summary>
    public record ArchiveOptions
    {
        public bool Enabled { get; init; }
        public int StartDelaySeconds { get; init; } = 600;
        public int WindowYears { get; init; } = 3;
        public int ClosedMarginDays { get; init; } = 14;
        public int MaxRowsPerPart { get; init; } = 50000;
        public int PageSize { get; init; } = 2000;
        /// <summary>Hard upper bound of <see cref="ChildQueryBatchSize"/>: MariaDB's <c>eq_range_index_dive_limit</c> is 200 and an IN list of that many values
        /// made the optimizer scan the all-NULL <c>UuId.SaveAuctionId</c> index (583M rows).</summary>
        public const int MaxChildQueryBatchSize = 199;
        /// <summary>Auction ids per child-table IN list (5 statements per chunk), clamped to 1..<see cref="MaxChildQueryBatchSize"/>.</summary>
        public int ChildQueryBatchSize { get; init; } = 180;
        /// <summary>The unclamped <c>CHILD_QUERY_BATCH_SIZE</c> as configured (the job warns when it differs from <see cref="ChildQueryBatchSize"/>).</summary>
        public int RequestedChildQueryBatchSize { get; init; } = 180;
        /// <summary>Child-table queries of one page that run at the same time (each on its own connection to the read replica).</summary>
        public int ChildQueryParallelism { get; init; } = 6;
        /// <summary>ItemIds of a month archived at the same time. Replica connections used are about <c>ItemParallelism * (ChildQueryParallelism + 1)</c>
        /// (MySqlConnector's default MaxPoolSize is 100).</summary>
        public int ItemParallelism { get; init; } = 4;
        /// <summary>Index bulk inserts (locations, bidders, sellers) running on the primary at the same time, across all parallel items.</summary>
        public int IndexWriteParallelism { get; init; } = 1;
        /// <summary>Command timeout in seconds of the primary writes (the default of 30 s is too short for a busy disk).</summary>
        public int IndexWriteTimeoutSeconds { get; init; } = 300;
        public int PagePauseMs { get; init; } = 0;
        /// <summary>Pause after an ItemId-month that had rows; each visit is one keyset page query on (ItemId, End).</summary>
        public int TagPauseMs { get; init; } = 0;
        public int MonthPauseMs { get; init; } = 60000;
        public int IdlePauseMinutes { get; init; } = 60;
        /// <summary>Minutes between scans of the Scylla month manifests SkyAuctions hands over (<see cref="ScyllaArchiveImportWorker"/>).</summary>
        public int ImportPollMinutes { get; init; } = 5;
        /// <summary>Hours the Redis item-month checkpoints live (refreshed on every item); expired means a full re-walk of the month.</summary>
        public int CheckpointTtlHours { get; init; } = 48;
        public bool DeleteMariaDb { get; init; }
        /// <summary>Hours a month that the retention worker aborted is left alone (persisted in the sentinel note, survives restarts); 0 retries on every run.</summary>
        public int AbortBackoffHours { get; init; } = 24;
        public int MaxDeleteMonthsPerDay { get; init; } = 1;
        /// <summary>A month whose delete removed fewer auctions than this does not count toward <see cref="MaxDeleteMonthsPerDay"/> (2019-2020 months are mostly in Scylla, tiny in MariaDB).</summary>
        public long SmallMonthRows { get; init; } = 100_000;
        public int DeleteBatchSize { get; init; } = 5000;
        public int DeletePauseMs { get; init; } = 500;
        public double MaxBinlogGb { get; init; } = 100;
        public long MaxHistoryListLength { get; init; } = 1_000_000;
        public bool RequireHealthChecks { get; init; } = true;
        /// <summary>
        /// The replica gate (<c>DBReadOnlyConnection</c> replica: both replication threads running and lag at most <see cref="MaxReplicaLagSeconds"/>) is
        /// evaluated before the month gates and before every batch, independent of <see cref="RequireHealthChecks"/>. Default true, fails closed when the status can not be read.
        /// </summary>
        public bool RequireReplicaHealthy { get; init; } = true;
        /// <summary>Retention pauses while the replica (<c>DBReadOnlyConnection</c>) is further behind than this.</summary>
        public int MaxReplicaLagSeconds { get; init; } = 300;
        /// <summary>A month is deleted from MariaDB only when <c>auctions/v2/_meta/backup/{yyyy-MM}.json</c> covers its current R2 state, see <see cref="Coflnet.Sky.Core.Archive.BackupReceipt"/>.</summary>
        public bool RequireBackupReceipt { get; init; } = true;
        /// <summary>A month is deleted only once its sentinel has <c>scylla_at</c> (Scylla sweep and import finished).</summary>
        public bool RequireScyllaImport { get; init; } = true;
        /// <summary>
        /// <c>yyyyMM</c> parsed from <c>OLDEST_MONTH</c> (<c>yyyy-MM</c>). SkyBlock launched on 2019-06-11, so no auction
        /// ends before 2019-06; every empty month still costs one page query per ItemId. Raise this default as the
        /// backfill completes months, so a fresh pass does not re-walk them.
        /// </summary>
        public int OldestMonth { get; init; } = 201906;

        /// <summary>Read <c>ARCHIVE:*</c>; missing or invalid values fall back to the defaults.</summary>
        public static ArchiveOptions From(IConfiguration config)
        {
            var d = new ArchiveOptions();
            var s = config.GetSection("ARCHIVE");
            return new ArchiveOptions
            {
                Enabled = Bool(s["ENABLED"], false),
                StartDelaySeconds = Int(s["START_DELAY_SECONDS"], d.StartDelaySeconds),
                WindowYears = Int(s["WINDOW_YEARS"], d.WindowYears),
                ClosedMarginDays = Int(s["CLOSED_MARGIN_DAYS"], d.ClosedMarginDays),
                MaxRowsPerPart = System.Math.Max(1, Int(s["MAX_ROWS_PER_PART"], d.MaxRowsPerPart)),
                PageSize = System.Math.Max(1, Int(s["PAGE_SIZE"], d.PageSize)),
                ChildQueryBatchSize = System.Math.Clamp(Int(s["CHILD_QUERY_BATCH_SIZE"], d.ChildQueryBatchSize), 1, MaxChildQueryBatchSize),
                RequestedChildQueryBatchSize = Int(s["CHILD_QUERY_BATCH_SIZE"], d.ChildQueryBatchSize),
                ChildQueryParallelism = System.Math.Max(1, Int(s["CHILD_QUERY_PARALLELISM"], d.ChildQueryParallelism)),
                ItemParallelism = System.Math.Max(1, Int(s["ITEM_PARALLELISM"], d.ItemParallelism)),
                IndexWriteParallelism = System.Math.Max(1, Int(s["INDEX_WRITE_PARALLELISM"], d.IndexWriteParallelism)),
                IndexWriteTimeoutSeconds = System.Math.Max(1, Int(s["INDEX_WRITE_TIMEOUT_SECONDS"], d.IndexWriteTimeoutSeconds)),
                PagePauseMs = Int(s["PAGE_PAUSE_MS"], d.PagePauseMs),
                TagPauseMs = Int(s["TAG_PAUSE_MS"], d.TagPauseMs),
                MonthPauseMs = Int(s["MONTH_PAUSE_MS"], d.MonthPauseMs),
                IdlePauseMinutes = Int(s["IDLE_PAUSE_MINUTES"], d.IdlePauseMinutes),
                ImportPollMinutes = System.Math.Max(1, Int(s["IMPORT_POLL_MINUTES"], d.ImportPollMinutes)),
                CheckpointTtlHours = System.Math.Max(1, Int(s["CHECKPOINT_TTL_HOURS"], d.CheckpointTtlHours)),
                DeleteMariaDb = Bool(s["DELETE_MARIADB"], false),
                AbortBackoffHours = System.Math.Max(0, Int(s["ABORT_BACKOFF_HOURS"], d.AbortBackoffHours)),
                MaxDeleteMonthsPerDay = Int(s["MAX_DELETE_MONTHS_PER_DAY"], d.MaxDeleteMonthsPerDay),
                SmallMonthRows = System.Math.Max(0, Int(s["SMALL_MONTH_ROWS"], (int)d.SmallMonthRows)),
                DeleteBatchSize = System.Math.Max(1, Int(s["DELETE_BATCH_SIZE"], d.DeleteBatchSize)),
                DeletePauseMs = Int(s["DELETE_PAUSE_MS"], d.DeletePauseMs),
                MaxBinlogGb = double.TryParse(s["MAX_BINLOG_GB"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : d.MaxBinlogGb,
                MaxHistoryListLength = long.TryParse(s["MAX_HISTORY_LIST_LENGTH"], out var h) ? h : d.MaxHistoryListLength,
                RequireHealthChecks = Bool(s["REQUIRE_HEALTH_CHECKS"], true),
                RequireReplicaHealthy = Bool(s["REQUIRE_REPLICA_HEALTHY"], true),
                MaxReplicaLagSeconds = System.Math.Max(0, Int(s["MAX_REPLICA_LAG_SECONDS"], d.MaxReplicaLagSeconds)),
                RequireBackupReceipt = Bool(s["REQUIRE_BACKUP_RECEIPT"], true),
                RequireScyllaImport = Bool(s["REQUIRE_SCYLLA_IMPORT"], true),
                OldestMonth = string.IsNullOrWhiteSpace(s["OLDEST_MONTH"]) ? d.OldestMonth : ArchiveMonths.Parse(s["OLDEST_MONTH"]),
            };
        }

        private static int Int(string value, int fallback) => int.TryParse(value, out var v) ? v : fallback;
        private static bool Bool(string value, bool fallback) => bool.TryParse(value, out var v) ? v : fallback;
    }
}
