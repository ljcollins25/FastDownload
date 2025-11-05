// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using System.Threading.Channels;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Utilities.Core.Tracing;
using FastDownload.Utilities;
using NLog;

namespace FastDownload.Download;

internal record struct DecompressionWorkerState
{
    private static readonly Logger Logger = LogManager.GetLogger(nameof(DecompressionWorkerState));

    public required int WorkerId { get; init; }

    public required GlobalState GlobalState { get; init; }

    public static DecompressionWorkerState Create(GlobalState state, int workerId)
    {
        return new DecompressionWorkerState
        {
            WorkerId = workerId,
            GlobalState = state,
        };
    }

    internal static async Task RunAsync(DecompressionWorkerState state)
    {
        var globalState = state.GlobalState;

        var input = state.GlobalState.Input;
        var cancellationToken = globalState.CancellationToken;

        try
        {
            Logger.ForTraceEvent()
                .Message(
                    "[{WorkerId}] {Component} started",
                    state.WorkerId,
                    nameof(DecompressionWorkerState))
                .Log();

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                DecompressionItem item;
                try
                {
                    item = await globalState.DecompressionQueue.Reader.ReadAsync(cancellationToken);
                }
                catch (ChannelClosedException)
                {
                    break;
                }

                await DecompressAsync(state, item);
            }

            Logger.ForTraceEvent()
                .Message(
                    "[{WorkerId}] {Component} exiting with success",
                    state.WorkerId,
                    nameof(DecompressionWorkerState))
                .Log();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                Logger.ForTraceEvent()
                    .Message(
                        "[{WorkerId}] {Component} exiting with cancellation",
                        state.WorkerId,
                        nameof(DecompressionWorkerState))
                    .Log();
            }
            else
            {
                Logger.ForFatalEvent()
                    .Message(
                        "[{WorkerId}] {Component} exiting with failure",
                        state.WorkerId,
                        nameof(DecompressionWorkerState))
                    .Exception(ex)
                    .Log();
            }

            throw;
        }
    }

    private static async Task DecompressAsync(DecompressionWorkerState state, DecompressionItem item)
    {
        Contract.Requires(item.Work.RawHash is not null, "Raw hash must be set for decompression and/or hash verification");

        var globalState = state.GlobalState;
        var statistics = globalState.Statistics;

        var cancellationToken = globalState.CancellationToken;

        var src = item.Work.Source;
        AlignedManagedBuffer? srcBuffer = item.Buffer;

        var dst = item.Work.Destination;
        AlignedManagedBuffer? dstBuffer = null;

        StopwatchSlim stopwatch = StopwatchSlim.Start();

        statistics.Increment(Counters.ChunksDecompressionStarted);

        Logger.ForTraceEvent()
            .Message(
                "[{WorkerId}] Decompressing {ChunkId}/{Chunks} {Source} into {Destination} using algorithm {Compression}",
                state.WorkerId,
                item.Work.ChunkIndex,
                state.GlobalState.Input.Chunks,
                src,
                dst,
                item.Work.Compression)
            .Log();

        try
        {
            if (item.Work.Compression == CompressionAlgorithm.None)
            {
                var hashType = item.Work.RawHash!.Value.HashType;
                var hasher = HashInfoLookup.GetContentHasher(hashType);

                // WARNING: it's important here to specify the length of the buffer, because the buffer may be larger
                // than the actual data, particularly for the very last chunk of the file. This doesn't actually
                // matter for the purposes of the final written file, because we'll write over the length of the file
                // and then truncate, but it matters for the hash.
                var contentHash = hasher.GetContentHash(srcBuffer.Memory.Span.Slice(0, (int)dst.Length));
                if (contentHash != item.Work.RawHash)
                {
                    statistics.Increment(Counters.BytesHashMismatch, dst.Length);
                    throw new InvalidOperationException($"Content hash mismatch for chunk {src}. Expected: {item.Work.RawHash}, Actual: {contentHash}");
                }

                statistics.Increment(Counters.BytesHashMatch, dst.Length);
                statistics.Increment(TimeCounters.ChunkHashingTime, stopwatch.Elapsed);

                await globalState.QueueWriteAsync(new WriteItem(item.Work, srcBuffer), cancellationToken);

                // Set to null, so buffer is not released.
                srcBuffer = null;
            }
            else
            {
                dstBuffer = globalState.GetBuffer();
                Contract.Assert(dst.Length <= dstBuffer.Size, $"Output buffer size {dstBuffer.Size} is less than expected decompression output {dst.Length}. Additional {dst.Length - dstBuffer.Size} bytes needed");

                var hashType = item.Work.RawHash!.Value.HashType;
                var hasher = HashInfoLookup.GetContentHasher(hashType);
                await using (var sourceStream = srcBuffer.CreateMemoryStream(writable: false))
                await using (var decompressionStream = item.Work.Compression.CreateDecompressionStream(sourceStream, leaveOpen: false))
                await using (var hashingStream = hasher.CreateReadHashingStream(decompressionStream.WithLength(length: dst.Length), parallelHashingFileSizeBoundary: -1))
                {
                    // WARNING: we're reading the exact amount of data that we expect to decompress. This is important
                    // because any deviation from this will cause the hashing stream to fail, and is also a signal of
                    // a bug in the download process.
                    await hashingStream.ReadExactlyAsync(dstBuffer.Memory[..(int)dst.Length], cancellationToken);

                    var contentHash = await hashingStream.GetContentHashAsync();
                    statistics.Increment(Counters.BytesDecompressed, dst.Length);

                    if (contentHash != item.Work.RawHash)
                    {
                        statistics.Increment(Counters.BytesHashMismatch, dst.Length);
                        throw new InvalidOperationException($"Content hash mismatch for chunk {src}. Expected: {item.Work.RawHash}, Actual: {contentHash}");
                    }

                    statistics.Increment(Counters.BytesHashMatch, dst.Length);
                    statistics.Increment(TimeCounters.ChunkHashingTime, hashingStream.TimeSpentHashing);
                    statistics.Increment(TimeCounters.ChunkDecompressionExclusiveTime, stopwatch.Elapsed - hashingStream.TimeSpentHashing);
                }

                await globalState.QueueWriteAsync(new WriteItem(item.Work, dstBuffer), cancellationToken);

                // Set to null, so buffer is not released.
                dstBuffer = null;
            }

            statistics.Increment(Counters.ChunksDecompressionSucceeded);

            Logger.ForTraceEvent()
                .Message(
                    "[{WorkerId}] Decompressed {ChunkId}/{Chunks} {Source} into {Destination} using algorithm {Compression}",
                    state.WorkerId,
                    item.Work.ChunkIndex,
                    state.GlobalState.Input.Chunks,
                    src,
                    dst,
                    item.Work.Compression)
                .Log();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                // Cancellation triggers logging at higher layers, so we don't log the exception here.
                statistics.Increment(Counters.ChunksDecompressionCancelled);
            }
            else
            {
                statistics.Increment(Counters.ChunksDecompressionFailed);

                Logger.ForFatalEvent()
                    .Exception(ex)
                    .Message(
                        "[{WorkerId}] Failed to decompress chunk {Source} into {Destination} using algorithm {Compression}",
                        state.WorkerId,
                        src,
                        dst,
                        item.Work.Compression)
                    .Exception(ex)
                    .Log();
            }

            // If at this point weve got an exception, there's absolutely no point in continuing, as we have already
            // retried as much as we're willing to.
            throw;
        }
        finally
        {
            // The buffers are only non-null if they were allocated and not passed onto a different stage of the
            // pipeline. If they were passed on, the responsibility for returning them is with the next stage. If they
            // weren't, it's here.
            srcBuffer?.Release();

            dstBuffer?.Release();

            statistics.Increment(Counters.ChunksDecompressionCompleted);
            statistics.Increment(TimeCounters.ChunkDecompressionTime, stopwatch.Elapsed);
        }
    }
}

