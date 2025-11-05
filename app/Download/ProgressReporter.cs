// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json;
using BuildXL.Cache.ContentStore.UtilitiesCore.Sketching;
using BuildXL.Utilities.Core.Tracing;
using FastDownload.Utilities;
using NLog;
using Statistics = FastDownload.Utilities.Statistics;

namespace FastDownload.Download
{
    internal class ProgressReporter : IAsyncDisposable
    {
        private static readonly Logger Logger = LogManager.GetLogger(nameof(ProgressReporter));

        private readonly Statistics _statistics;
        private readonly GlobalState _globalState;
        private readonly TimeSpan _reportFrequency;
        private readonly Input _input;

        private readonly CancellationTokenSource _stopSource = new();
        private Task _backgroundTask = Task.CompletedTask;

        private ProgressReporter(Statistics statistics, TimeSpan reportFrequency, Input input, GlobalState globalState)
        {
            _statistics = statistics;
            _reportFrequency = reportFrequency;
            _input = input;
            _globalState = globalState;
        }

        public static ProgressReporter Start(GlobalState globalState)
        {
            var reporter = new ProgressReporter(globalState.Statistics, TimeSpan.FromSeconds(globalState.Input.Arguments.ProgressIntervalSeconds), globalState.Input, globalState);
            reporter.Start(globalState.CancellationToken);
            return reporter;
        }

        private void Start(CancellationToken cancellationToken)
        {
            _backgroundTask = Task.Run(async () => await BackgroundTaskAsync(cancellationToken), cancellationToken);
        }

        public async Task StopAsync()
        {
            if (!_stopSource.IsCancellationRequested)
            {
                await _stopSource.CancelAsync();
            }

            await _backgroundTask;
        }

        private async Task BackgroundTaskAsync(CancellationToken cancellationToken)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _stopSource.Token);
            try
            {
                await ReportProgressCoreAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        private static readonly List<Counters> SamplingCounters = new List<Counters>() {
                Counters.BytesDownloaded,
                Counters.BytesDecompressionQueued,
                Counters.BytesDecompressed,
                Counters.BytesWriteQueued,
                Counters.BytesWritten,
            };

        internal readonly record struct Sample(TimeSpan TimeStampUtc, long Value)
        {
            public static Sample Zero => new Sample(TimeSpan.Zero, 0);

            public override string ToString()
            {
                return Value.ToString();
            }
        }

        private Dictionary<Counters, Sample> _previous = new(capacity: SamplingCounters.Count);

        private readonly Dictionary<Counters, DDSketch> _mbps = SamplingCounters.ToDictionary(counter => counter, counter => new DDSketch());

        private StatisticsSnapshot LogSnapshot(StopwatchSlim stopwatch, Statistics s)
        {
            var queues = new QueueStatus(
                _globalState.DownloadQueue.Count,
                _globalState.DecompressionQueue.Reader.Count,
                _globalState.WriteQueue.Count);

            var downloads = new ChunkCounters(
                s.Read(Counters.ChunksDownloadStarted),
                s.Read(Counters.ChunksDownloadCompleted),
                s.Read(Counters.ChunksDownloadFailed),
                s.Read(Counters.ChunksDownloadSucceeded),
                s.Read(Counters.ChunksDownloadCancelled));

            var decompression = new ChunkCounters(
                s.Read(Counters.ChunksDecompressionStarted),
                s.Read(Counters.ChunksDecompressionCompleted),
                s.Read(Counters.ChunksDecompressionFailed),
                s.Read(Counters.ChunksDecompressionSucceeded),
                s.Read(Counters.ChunksDecompressionCancelled));

            var write = new ChunkCounters(
                s.Read(Counters.ChunksWriteStarted),
                s.Read(Counters.ChunksWriteCompleted),
                s.Read(Counters.ChunksWriteFailed),
                s.Read(Counters.ChunksWriteSucceeded),
                s.Read(Counters.ChunksWriteCancelled));

            var now = stopwatch.Elapsed;
            var samples = new Dictionary<Counters, Sample>(capacity: SamplingCounters.Count);
            foreach (var counter in SamplingCounters)
            {
                samples[counter] = new Sample(now, s.Read(counter));
            }

            var instantaneous = new Dictionary<Counters, double>(capacity: SamplingCounters.Count);
            foreach (var counter in SamplingCounters)
            {
                var sample = samples[counter];
                var previousSample = _previous.TryGetValue(counter, out var previous) ? previous : Sample.Zero;
                var elapsedSeconds = (sample.TimeStampUtc - previousSample.TimeStampUtc).TotalSeconds;
                var growthMB = (sample.Value - previousSample.Value).AsMb();
                var growthMBps = elapsedSeconds > 0.001 ? growthMB / elapsedSeconds : 0;

                if (growthMBps <= 0)
                {
                    Logger.ForTraceEvent()
                        .Message(
                            "Ignoring growth rate of {GrowthMBps} MB/s for {Counter} due to small value growth of {GrowthMB} MB in {ElapsedSeconds} seconds.",
                            growthMBps,
                            counter,
                            growthMB,
                            elapsedSeconds)
                        .Log();

                    continue;
                }

                instantaneous[counter] = growthMBps;
                _mbps[counter].Insert(growthMBps);
            }

            _previous = samples;

            var rates = new Dictionary<Counters, Description>(capacity: SamplingCounters.Count);
            foreach (var counter in SamplingCounters)
            {
                var sketch = _mbps[counter];
                if (sketch.Count == 0)
                {
                    Logger.ForTraceEvent()
                        .Message("Ignoring empty sketch for {Counter}", counter)
                        .Log();

                    continue;
                }

                rates[counter] = new Description(
                    Avg: sketch.Average,
                    P50: sketch.Quantile(0.5),
                    P99: sketch.Quantile(0.99),
                    Min: sketch.Min,
                    Max: sketch.Max);
            }

            var snapshot = new StatisticsSnapshot(
                Instantaneous: instantaneous,
                Queues: queues,
                Downloads: downloads,
                Decompression: decompression,
                Write: write,
                Rates: rates);

            Logger.ForInfoEvent()
                .Message("Progress Snapshot")
                .Property("Report", JsonSerializer.Serialize(snapshot, typeof(StatisticsSnapshot), StatisticsJsonContext.Default))
                .Log();

            return snapshot;
        }

        private async Task ReportProgressCoreAsync(CancellationToken cancellationToken)
        {
            var totalDownloadSize = _input.TotalDownloadSize;
            var totalWriteSize = _input.Items.Sum(w => w.GetWrittenLength());

            var stopwatch = StopwatchSlim.Start();
            var lastStamps = new (long Record, TimeSpan Elapsed, long Total)[(int)Counters.Max];

            TimeSpan? totalTime = null;
            TimeSpan? downloadTime = null;

            bool logProgressAndCheckDone(Counters counter, long totalBytes, TimeSpan? elapsedOverride = null)
            {
                ref var lastStamp = ref lastStamps[(int)counter];
                var currentBytes = _statistics.Read(counter);

                var elapsed = elapsedOverride ?? stopwatch.Elapsed;
                var deltaBytes = currentBytes - lastStamp.Record;
                var deltaElapsed = elapsed - lastStamp.Elapsed;
                lastStamp.Record = currentBytes;
                lastStamp.Elapsed = elapsed;
                lastStamp.Total = totalBytes;

                if (currentBytes >= totalBytes && totalBytes >= 0 && elapsedOverride == null)
                {
                    return true;
                }

                // We need a few seconds worth of data before we start reporting download speeds. This is because
                // the time elapsed is very small at the start and can cause the speed to be reported arbitrarily
                // high.
                if (currentBytes > 0 && elapsed.TotalSeconds > 2)
                {
                    var writtenMb = currentBytes.AsMb();
                    var mbps = writtenMb / elapsed.TotalSeconds;
                    var intervalMbps = deltaElapsed == default ? 0 : deltaBytes.AsMb() / deltaElapsed.TotalSeconds;
                    var percentComplete = totalBytes < 0 ? 0 : Math.Round(currentBytes * 100.0 / totalBytes, 2);

                    var remainingTime = TimeSpan.Zero;
                    try
                    {
                        if (totalBytes >= 0)
                        {
                            remainingTime = TimeSpan.FromSeconds((totalBytes - currentBytes).AsMb() / Math.Max(0.0000001, mbps));
                        }
                    }
                    catch (OverflowException)
                    {
                        // When the download is extremely fast relative to the file size, the division can overflow.
                    }

                    Logger.ForInfoEvent()
                        .Message(
                            "{Counter} {CurrentBytes} bytes ({CurrentMb} MB, {PercentComplete}%) of {TotalBytes} bytes ({TotalMb} MB) @ ~{Mbps} MB/s. IntervalSpeed: (~{IntervalMbps} MB/s) Elapsed: {Elapsed}. Remaining: {RemainingTime}",
                            counter,
                            currentBytes,
                            currentBytes.AsMb().Truncate(3),
                            percentComplete.Truncate(2),
                            totalBytes,
                            totalBytes.AsMb().Truncate(3),
                            mbps.Truncate(3),
                            intervalMbps.Truncate(3),
                            elapsed,
                            remainingTime)
                        .Log();
                }

                return false;
            }

            while (!cancellationToken.IsCancellationRequested)
            {
                LogSnapshot(stopwatch, _statistics);

                logProgressAndCheckDone(Counters.ServerRequestedBytes, _statistics.Read(Counters.ServerRequestedBytes) + 1);
                logProgressAndCheckDone(Counters.ServerSendBytes, _statistics.Read(Counters.ServerRequestedBytes) + 1);

                if (_statistics.Read(Counters.ChunksDownloadCompleted) < _input.Items.Count)
                {
                    logProgressAndCheckDone(Counters.BytesDownloaded, totalDownloadSize);
                    logProgressAndCheckDone(Counters.StorageBytesDownloaded, totalDownloadSize);
                    logProgressAndCheckDone(Counters.PredecessorBytesDownloaded, totalDownloadSize);
                }
                else
                {
                    downloadTime ??= stopwatch.Elapsed;
                }

                logProgressAndCheckDone(Counters.BytesHashMatch, totalWriteSize);
                logProgressAndCheckDone(Counters.BytesWriteQueued, totalWriteSize);
                if (logProgressAndCheckDone(Counters.BytesWritten, totalWriteSize))
                {
                    totalTime = stopwatch.Elapsed;
                }

                try
                {
                    await Task.Delay(_reportFrequency, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            totalTime ??= stopwatch.ElapsedAndReset();
            downloadTime ??= totalTime;
            {
                void logFinalProgress(Counters counter)
                {
                    var lastStamp = lastStamps[(int)counter];
                    logProgressAndCheckDone(counter, lastStamp.Total, lastStamp.Elapsed);
                }

                double getMbps(double totalMb, TimeSpan time)
                {
                    return time == TimeSpan.Zero ? -1 : totalMb / time.TotalSeconds;
                }

                logFinalProgress(Counters.BytesDownloaded);

                var writtenBytes = _statistics.Read(Counters.BytesWritten);
                var downloadedMb = _statistics.Read(Counters.BytesDownloaded).AsMb();

                var writtenMb = writtenBytes.AsMb();
                var downloadSpeedMbps = getMbps(downloadedMb, downloadTime.Value);
                var totalSpeedMbps = getMbps(writtenMb, totalTime.Value);

                _statistics.Increment(TimeCounters.ProgressReporterDownloadWallClockTime, downloadTime.Value);
                _statistics.Increment(TimeCounters.ProgressReporterTotalWallClockTime, totalTime.Value);
                _statistics.Increment(Counters.DownloadSpeedMbps, (long)downloadSpeedMbps);
                _statistics.Increment(Counters.TotalSpeedMbps, (long)totalSpeedMbps);

                Logger.ForInfoEvent()
                    .Message(
                        "Total Downloaded {WrittenBytes} bytes ({WrittenMb} MB) in {DownloadTime} @ ~{DownloadSpeedMbps} MB/s",
                        writtenBytes,
                        writtenMb.Truncate(3),
                        totalTime,
                        totalSpeedMbps.Truncate(3))
                    .Log();
            }

        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            _stopSource.Dispose();
        }
    }
}
