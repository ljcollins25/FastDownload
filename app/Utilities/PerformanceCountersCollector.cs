// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json;
using BuildXL.Utilities;
using BuildXL.Utilities.ConfigurationHelpers;
using NLog;

namespace FastDownload.Utilities;

internal static class PerformanceCountersCollector
{
    private static readonly Logger Logger = LogManager.GetLogger(nameof(PerformanceCountersCollector));

    private static readonly TimeSpan CollectionFrequency = TimeSpan.FromSeconds(1);

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        // Ensure everything hereafter runs in a separate task.
        await Task.Yield();

        // It's very important to catch all exceptions here, otherwise we'll propagate upwards and we could fail the
        // entire application because of a failure to collect machine metrics.
        try
        {
            await ReportPerformanceMeasurementsCoreAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Logger.ForInfoEvent()
                .Message("Performance measurements collection was cancelled.")
                .Log();
        }
        catch (Exception ex)
        {
            Logger.ForErrorEvent()
                .Message("An error occurred while collecting performance measurements.")
                .Exception(ex)
                .Log();
        }
    }

    private static async Task ReportPerformanceMeasurementsCoreAsync(CancellationToken cancellationToken)
    {
        using var collector = new PerformanceCollector(CollectionFrequency);
        using var aggregator = collector.CreateAggregator();
        var drives = collector.GetDrives().ToList();
        Logger.ForInfoEvent()
            .Message(
                "Performance measurements will be collected every {CollectionFrequencySeconds} seconds. Available drives are: {Drives}",
                CollectionFrequency.TotalSeconds,
                string.Join(", ", drives))
            .Log();

        while (!cancellationToken.IsCancellationRequested)
        {
            var report = CreateReport(drives, aggregator);
            var reportString = JsonSerializer.Serialize(report, typeof(Dictionary<string, double>), StatisticsJsonContext.Default);

            Logger.ForInfoEvent()
                .Message("Performance Counters {ReportMessage}", Globals.FlatLogLayout ? reportString : string.Empty)
                .Property("Report", reportString)
                .Log();

            try
            {
                await Task.Delay(CollectionFrequency, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private delegate void ThreadPoolQuery(out int workerCount, out int completionPortCount);

    private static Dictionary<string, double> CreateReport(List<string> drives, PerformanceCollector.Aggregator aggregator)
    {
        var perfInfo = aggregator.ComputeMachinePerfInfo(ensureSample: true);

        var report = new Dictionary<string, double>(capacity: 20 + drives.Count)
                {
                    { "NetworkReceivedMBps", perfInfo.MachineKbitsPerSecReceived / 8.0 / 1024 },
                    { "NetworkSentMBps", perfInfo.MachineKbitsPerSecSent / 8.0 / 1024 },
                    { "CpuUsagePercentage", perfInfo.CpuUsagePercentage },
                    { "ProcessCpuPercentage", perfInfo.ProcessCpuPercentage },
                    { "CpuQueueLength", perfInfo.CpuQueueLength },
                    { "ProcessWorkingSetMB", perfInfo.ProcessWorkingSetMB },
                    { "Threads", perfInfo.Threads },
                };

        perfInfo.AvailableRamMb.ApplyIfNotNull(v => report["AvailableRamMb"] = v);
        perfInfo.EffectiveAvailableRamMb.ApplyIfNotNull(v => report["EffectiveAvailableRamMb"] = v);
        perfInfo.EffectiveRamUsagePercentage.ApplyIfNotNull(v => report["EffectiveRamUsagePercentage"] = v);
        perfInfo.TotalRamMb.ApplyIfNotNull(v => report["TotalRamMb"] = v);

        var driveId = 0;
        foreach (var (drive, statistics) in drives.Zip(aggregator.DiskStats))
        {
            // All disk performance metrics are obtained every 100ns. For example, ReadTime is the total time spent
            // on reads in 100ns units. That's precisely why we don't report them, the collection frequency is too
            // high relative to the rate of change of the metrics.
            // See: https://stackoverflow.com/questions/9258705/disk-performance-structs-readtime-and-writetime-members
            report[$"DriveQueueLength{drive}"] = statistics.QueueDepth.Latest;
            driveId++;
        }

        return report;
    }
}
