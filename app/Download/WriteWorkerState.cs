// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core.Tracing;
using FastDownload.Utilities;
using Microsoft.Win32.SafeHandles;
using NLog;

#nullable enable

namespace FastDownload.Download;

/// <summary>
/// All state required by a download worker.
/// </summary>
/// <remarks>
/// Used mainly to ensure that all resources are disposed of correctly when needed.
/// </remarks>
internal sealed record WriteWorkerState : IDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger(nameof(WriteWorkerState));

    public required int WorkerId { get; init; }

    public required FileStream Stream { get; init; }

    public required SafeFileHandle Handle { get; init; }

    public required GlobalState GlobalState { get; init; }

    public static WriteWorkerState Create(GlobalState state, int workerId)
    {
        // In the code below, it is important that we create our own HttpClient and FileStream instances. This is
        // because these types although they are thread safe, they are for some reason slower when using them that way.

        var stream = FileUtilities.OpenUnbufferedFileStream(state.Input.Arguments.Path, FileMode.Open, writeThrough: state.Input.Arguments.WriteThrough, async: state.Input.Arguments.Async);

        return new WriteWorkerState
        {
            WorkerId = workerId,
            Stream = stream,
            Handle = stream.SafeFileHandle,
            GlobalState = state,
        };
    }

    public void Dispose()
    {
        Handle.Dispose();
        Stream.Dispose();
    }

    internal static async Task RunAsync(WriteWorkerState state)
    {
        using var _ = state;

        var globalState = state.GlobalState;
        var statistics = globalState.Statistics;
        var arguments = globalState.Input.Arguments;
        var cancellationToken = globalState.CancellationToken;
        var workerId = state.WorkerId;

        var handle = state.Handle;

        Logger.ForTraceEvent()
            .Message(
                "[{WorkerId}] {Component} started",
                workerId,
                nameof(WriteWorkerState))
            .Log();

        StopwatchSlim workerTime = StopwatchSlim.Start();

        try
        {
            // Initialize verification state if VerificationExpectedContentPath is specified.
            using var verificationBuffer = arguments.VerificationExpectedContentPath?.Then(_ => globalState.GetBuffer().RefCountHandle);
            using var verificationStream = arguments.VerificationExpectedContentPath?.Then(File.OpenRead);
            var verificationStreamLength = verificationStream?.Length ?? -1;
            var verificationHandle = verificationStream?.SafeFileHandle;

            await foreach (var (chunk, items) in globalState.GetWritesAsync(cancellationToken).WithSyncOrAsync(isAsync: arguments.Async))
            {
                try
                {
                    Logger.ForTraceEvent()
                        .Message(
                            "[{WorkerId}] Writing {Count} chunks between {Chunk}",
                            workerId,
                            items.Count,
                            chunk)
                        .Log();

                    statistics.Increment(Counters.ChunksWriteStarted, items.Count);

                    cancellationToken.ThrowIfCancellationRequested();

                    var stopwatch = StopwatchSlim.Start();

                    var buffers = items.SelectList(static item => item.Buffer);

                    // Verification mode: verify content on disk at VerificationExpectedContentPath matches content which is being written
                    if (verificationStream != null && verificationBuffer != null)
                    {
                        verificationStream.Position = chunk.Start;
                        foreach (var buffer in buffers)
                        {
                            var startPosition = verificationStream.Position;
                            var length = (int)Math.Min(buffer.Length, verificationStreamLength - startPosition);
                            var verificationMemory = verificationBuffer!.Value!.Memory[0..length];

                            await verificationStream.ReadExactlyAsync(verificationMemory, cancellationToken);

                            // Verify that read content matches the content being written by checking the
                            // common prefix length is equal to the total length
                            var equivalentSpanLength = verificationMemory.Span.CommonPrefixLength(buffer.Span);

                            if (equivalentSpanLength != length)
                            {
                                var differenceByte = startPosition + equivalentSpanLength;
                                var chunkIndex = differenceByte / globalState.Input.ChunkSize;
                                var chunkOffset = differenceByte - (chunkIndex * globalState.Input.ChunkSize);

                                Logger.Warn($"[{workerId}] Write verification for chunk {chunk} failed. Written bytes from verification file at byte {differenceByte} (Chunk {chunkIndex} byte {chunkOffset}) Write byte value ({buffer.Span[equivalentSpanLength]:x2}) != ({verificationMemory.Span[equivalentSpanLength]:x2}) Verification byte value");
                            }
                        }
                    }

                    if (arguments.SkipWriteContent)
                    {
                        // Do not write content
                    }
                    else if (arguments.Async)
                    {
                        await RandomAccess.WriteAsync(
                            handle,
                            buffers,
                            fileOffset: chunk.Start,
                            cancellationToken);
                    }
                    else
                    {
                        RandomAccess.Write(
                            handle,
                            buffers,
                            fileOffset: chunk.Start);
                    }

                    statistics.Increment(TimeCounters.ChunkWriteTime, stopwatch.Elapsed);
                    statistics.Increment(Counters.BytesWritten, chunk.Length);
                    statistics.Increment(Counters.ChunksWriteSucceeded, items.Count);

                    Logger.ForTraceEvent()
                        .Message(
                            "[{WorkerId}] Wrote {Count} chunks between {Chunk} in {WriteTime}",
                            workerId,
                            items.Count,
                            chunk,
                            stopwatch.Elapsed)
                        .Log();
                }
                catch (Exception ex)
                {
                    if (ex is OperationCanceledException)
                    {
                        // Cancellation triggers logging at higher layers, so we don't log the exception here.
                        statistics.Increment(Counters.ChunksWriteCancelled, items.Count);
                    }
                    else
                    {
                        statistics.Increment(Counters.ChunksWriteFailed, items.Count);

                        Logger.ForFatalEvent()
                            .Message(
                                "[{WorkerId}] Writing {Count} chunks between {Chunk} failed",
                                workerId,
                                items.Count,
                                chunk)
                            .Exception(ex)
                            .Log();
                    }

                    throw;
                }
                finally
                {
                    statistics.Increment(Counters.ChunksWriteCompleted, items.Count);
                }
            }

            Logger.ForTraceEvent()
                .Message(
                    "[{WorkerId}] {Component} exiting with success",
                    workerId,
                    nameof(WriteWorkerState))
                .Log();
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException)
            {
                Logger.ForTraceEvent()
                    .Message(
                        "[{WorkerId}] {Component} exiting with cancellation",
                        workerId,
                        nameof(WriteWorkerState))
                    .Log();
            }
            else
            {
                Logger.ForFatalEvent()
                    .Message(
                        "[{WorkerId}] {Component} exiting with failure",
                        workerId,
                        nameof(WriteWorkerState))
                    .Exception(ex)
                    .Log();
            }

            throw;
        }
        finally
        {
            statistics.Increment(TimeCounters.WriteWorkerTime, workerTime.Elapsed);
        }
    }
}
