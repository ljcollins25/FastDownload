// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Collections.Concurrent;
using System.Text.Json;
using BuildXL.Utilities.Core.Tracing;
using NLog;

#nullable enable

namespace FastDownload.Utilities;

internal sealed class Statistics
{
    private static readonly Logger Logger = LogManager.GetLogger(nameof(Statistics));

    private readonly ConcurrentDictionary<string, Counter> _dynamicCounters = new();
    private readonly ConcurrentDictionary<Counters, Counter> _staticCounters = new();
    private readonly ConcurrentDictionary<TimeCounters, Counter> _timers = new();

    public Statistics()
    {
        foreach (var counter in Enum.GetValues<Counters>())
        {
            _staticCounters.TryAdd(counter, new Counter(isTime: false));
        }

        foreach (var counter in Enum.GetValues<TimeCounters>())
        {
            _timers.TryAdd(counter, new Counter(isTime: true));
        }
    }

    public long? Read(string name)
    {
        if (!_dynamicCounters.TryGetValue(name, out var counter))
        {
            return null;
        }

        return counter.Read();
    }

    public long Read(Counters name)
    {
        return _staticCounters[name].Read();
    }

    public TimeSpan Read(TimeCounters name)
    {
        return _timers[name].ReadTime();
    }

    public void Increment(string name, long value = 1)
    {
        var counter = _dynamicCounters.GetOrAdd(name, k => new Counter(isTime: false));
        counter.Add(value);
    }

    public void Increment(Counters name, long value = 1)
    {
        _staticCounters[name].Add(value);
    }

    public ActivityScope EnterActivity(Counters name)
    {
        Increment(name);
        return new ActivityScope(name, this);
    }

    public TimeScope TrackDuration(TimeCounters name)
    {
        return new TimeScope(name, this);
    }

    public void Increment(TimeCounters name, TimeSpan value)
    {
        _timers[name].Add(value.Ticks);
    }

    public string GetStatisticsReport(bool indented)
    {
        Dictionary<string, double> statistics = GetStatisticsMap();

        var context = StatisticsJsonContext.Default;
        if (indented)
        {
            context = new StatisticsJsonContext(new JsonSerializerOptions(context.Options)
            {
                WriteIndented = true
            });
        }

        return JsonSerializer.Serialize(statistics, typeof(Dictionary<string, double>), context);
    }

    public void AddFromMap(Dictionary<string, double> statistics)
    {
        const string timerSuffix = "Seconds";

        foreach (var (key, value) in statistics)
        {
            if (key.EndsWith(timerSuffix) && Enum.TryParse<TimeCounters>(key[0..(key.Length - timerSuffix.Length)], out var timerCounter))
            {
                Increment(timerCounter, TimeSpan.FromSeconds(value));
            }
            else if (Enum.TryParse<Counters>(key, out var counter))
            {
                Increment(counter, (long)value);
            }
            else
            {
                Increment(key, (long)value);
            }
        }
    }

    public Dictionary<string, double> GetStatisticsMap()
    {
        return _staticCounters
               .Where(e => e.Value.Printable)
               .Select(e => new { Key = e.Key.ToString(), Value = e.Value.Serialize() })
           .Concat(
               _dynamicCounters
                   .Where(e => e.Value.Printable)
                   .Select(e => new { Key = e.Key, Value = e.Value.Serialize() }))
           .Concat(
               _timers
                   .Where(e => e.Value.Printable)
                   .Select(e => new { Key = $"{e.Key}Seconds", Value = e.Value.Serialize() }))
           .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
    }

    public void LogStatisticsReport(bool includeInMessage)
    {
        var report = GetStatisticsReport(indented: includeInMessage);
        Logger.ForInfoEvent()
            .Message("Statistics {Stats}", includeInMessage ? report : string.Empty)
            .Property("Report", report)
            .Log();
    }

    public void LogCompressionReport()
    {
        var rawFileSizeBytes = Read(Counters.BytesRead);
        var uploadedSizeBytes = Read(Counters.BytesUploaded);
        var compressionRatio = (1.0 - (uploadedSizeBytes / (double)rawFileSizeBytes)) * 100.0;
        if (double.IsNaN(compressionRatio))
        {
            compressionRatio = 100.0;
        }

        var report = new CompressionReport
        {
            RawFileSizeBytes = rawFileSizeBytes,
            RawFileSizeMB = rawFileSizeBytes.AsMb(),
            CompressedFileSizeBytes = uploadedSizeBytes,
            CompressedFileSizeMB = uploadedSizeBytes.AsMb(),
            CompressionRatio = compressionRatio
        };

        Logger.ForInfoEvent()
            .Message("Compression Summary")
            .Property("Report", JsonSerializer.Serialize(report, typeof(CompressionReport), StatisticsJsonContext.Default))
            .Log();
    }

    public struct ActivityScope(Counters counter, Statistics statistics) : IDisposable
    {
        public void Dispose()
        {
            statistics.Increment(counter, -1);
        }
    }

    public struct TimeScope(TimeCounters counter, Statistics statistics) : IDisposable
    {
        public StopwatchSlim Stopwatch { get; } = StopwatchSlim.Start();

        public TimeSpan Elapsed => Stopwatch.Elapsed;

        public void Dispose()
        {
            statistics.Increment(counter, Stopwatch.Elapsed);
        }
    }
}
