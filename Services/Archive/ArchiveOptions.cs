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
        public int MaxDeleteMonthsPerDay { get; init; } = 1;
        public int DeleteBatchSize { get; init; } = 5000;
        public int DeletePauseMs { get; init; } = 500;
        public double MaxBinlogGb { get; init; } = 100;
        public long MaxHistoryListLength { get; init; } = 1_000_000;
        public bool RequireHealthChecks { get; init; } = true;
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
                PagePauseMs = Int(s["PAGE_PAUSE_MS"], d.PagePauseMs),
                TagPauseMs = Int(s["TAG_PAUSE_MS"], d.TagPauseMs),
                MonthPauseMs = Int(s["MONTH_PAUSE_MS"], d.MonthPauseMs),
                IdlePauseMinutes = Int(s["IDLE_PAUSE_MINUTES"], d.IdlePauseMinutes),
                ImportPollMinutes = System.Math.Max(1, Int(s["IMPORT_POLL_MINUTES"], d.ImportPollMinutes)),
                CheckpointTtlHours = System.Math.Max(1, Int(s["CHECKPOINT_TTL_HOURS"], d.CheckpointTtlHours)),
                DeleteMariaDb = Bool(s["DELETE_MARIADB"], false),
                MaxDeleteMonthsPerDay = Int(s["MAX_DELETE_MONTHS_PER_DAY"], d.MaxDeleteMonthsPerDay),
                DeleteBatchSize = System.Math.Max(1, Int(s["DELETE_BATCH_SIZE"], d.DeleteBatchSize)),
                DeletePauseMs = Int(s["DELETE_PAUSE_MS"], d.DeletePauseMs),
                MaxBinlogGb = double.TryParse(s["MAX_BINLOG_GB"], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gb) ? gb : d.MaxBinlogGb,
                MaxHistoryListLength = long.TryParse(s["MAX_HISTORY_LIST_LENGTH"], out var h) ? h : d.MaxHistoryListLength,
                RequireHealthChecks = Bool(s["REQUIRE_HEALTH_CHECKS"], true),
                OldestMonth = string.IsNullOrWhiteSpace(s["OLDEST_MONTH"]) ? d.OldestMonth : ArchiveMonths.Parse(s["OLDEST_MONTH"]),
            };
        }

        private static int Int(string value, int fallback) => int.TryParse(value, out var v) ? v : fallback;
        private static bool Bool(string value, bool fallback) => bool.TryParse(value, out var v) ? v : fallback;
    }
}
