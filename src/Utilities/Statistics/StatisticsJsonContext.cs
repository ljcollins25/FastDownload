// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json.Serialization;

#nullable enable

namespace FastDownload.Utilities;

public record Description(double Avg, double P50, double P99, double Min, double Max);

public record QueueStatus(int Downloads, int Decompressions, int Writes);

public record ChunkCounters(long Started, long Completed, long Failed, long Succeeded, long Cancelled);

public record StatisticsSnapshot(
    QueueStatus Queues,
    Dictionary<Counters, double> Instantaneous,
    Dictionary<Counters, Description> Rates,
    ChunkCounters Downloads,
    ChunkCounters Decompression,
    ChunkCounters Write);

/// <summary>
/// This class is required to generate the JSON serialization code for the <see cref="Statistics"/> class. The
/// generated code is placed in the same namespace as this class, and the reason it's needed is that Native AOT
/// doesn't support reflection, so we can't use the built-in System.Text.Json source generator.
///
/// This class is also used in the <see cref="PerformanceCountersCollector"/>.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = false, NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals, Converters = [typeof(CounterJsonConverter)])]
[JsonSerializable(typeof(CompressionReport))]
[JsonSerializable(typeof(Dictionary<string, Counter>))]
[JsonSerializable(typeof(Dictionary<string, double>))]
[JsonSerializable(typeof(StatisticsSnapshot))]
public partial class StatisticsJsonContext : JsonSerializerContext
{
}
