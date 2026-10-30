using System;
using System.Diagnostics;

namespace Coflnet.Sky.Indexer.Archive
{
    /// <summary>
    /// Timing instrumentation of the auction archive job: the <c>sky_indexer_archive_stage_seconds{stage}</c> histogram, two gauges for
    /// the index write gate and the walked items, and the <see cref="StageScope"/> helper that also opens a child span of the item's root span.
    /// </summary>
    public static class ArchiveTelemetry
    {
        /// <summary>Stage names of <c>sky_indexer_archive_stage_seconds</c> (also the span names).</summary>
        public static class Stages
        {
            public const string PageQuery = "page_query";
            public const string ChildQuery = "child_query";
            public const string PartFlush = "part_flush";
            public const string PartSerialize = "part_serialize";
            public const string PartUpload = "part_upload";
            public const string PartVerify = "part_verify";
            public const string TagId = "tag_id";
            public const string PlayerLookup = "player_lookup";
            public const string IndexGateWait = "index_gate_wait";
            public const string IndexWrite = "index_write";
            public const string CheckpointSave = "checkpoint_save";
            public const string Item = "item";
            public const string Finalise = "finalise";
        }

        /// <summary>Name of the root span of one item-month.</summary>
        public const string ItemSpanName = "ArchiveItem";

        private static readonly Prometheus.Histogram stageSeconds = Prometheus.Metrics.CreateHistogram(
            "sky_indexer_archive_stage_seconds", "Seconds spent per stage of the auction archive job",
            new Prometheus.HistogramConfiguration
            {
                LabelNames = new[] { "stage" },
                Buckets = Prometheus.Histogram.ExponentialBuckets(0.005, 2, 18), // 5 ms .. ~655 s
            });

        /// <summary>Items currently waiting at the index write gate.</summary>
        internal static readonly Prometheus.Gauge IndexWriteWaiting = Prometheus.Metrics.CreateGauge("sky_indexer_archive_index_write_waiting", "Items currently waiting at the index write gate");
        /// <summary>Item-months currently being walked.</summary>
        internal static readonly Prometheus.Gauge ItemsInFlight = Prometheus.Metrics.CreateGauge("sky_indexer_archive_items_in_flight", "Item-months currently being walked by the archive job");

        /// <summary>Number of observations of a stage so far (tests).</summary>
        internal static long StageCount(string stage) => stageSeconds.WithLabels(stage).Count;

        /// <summary>Sum of the observed seconds of a stage so far (tests).</summary>
        internal static double StageSum(string stage) => stageSeconds.WithLabels(stage).Sum;

        /// <summary>
        /// Start timing a stage. With a <paramref name="root"/> span (null when the item is not sampled) a child span of the same name is
        /// opened under it, always with the root as explicit parent so ambient activities never interfere.
        /// </summary>
        internal static StageScope Start(string stage, ActivitySource source = null, Activity root = null)
        {
            Activity span = null;
            if (root != null && source != null)
                span = source.StartActivity(stage, ActivityKind.Internal, root.Context);
            return new StageScope(stageSeconds.WithLabels(stage), span);
        }

        /// <summary>Observes the elapsed seconds and ends the span when disposed.</summary>
        public readonly struct StageScope : IDisposable
        {
            private readonly Prometheus.Histogram.Child histogram;
            private readonly long started;
            private readonly Activity span;

            internal StageScope(Prometheus.Histogram.Child histogram, Activity span)
            {
                this.histogram = histogram;
                this.span = span;
                started = Stopwatch.GetTimestamp();
            }

            /// <summary>The span of this stage, null when not sampled.</summary>
            public Activity Span => span;

            /// <inheritdoc/>
            public void Dispose()
            {
                histogram?.Observe(Stopwatch.GetElapsedTime(started).TotalSeconds);
                span?.Dispose();
            }
        }
    }
}
