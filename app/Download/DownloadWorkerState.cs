// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Core.Tracing;
using FastDownload.Utilities;
using NLog;
using static FastDownload.Download.DownloadArguments;

namespace FastDownload.Download;

internal record struct DownloadWorkerState : IDisposable
{
    private static readonly Logger Logger = LogManager.GetLogger(nameof(DownloadWorkerState));

    public required int WorkerId { get; init; }

    public required HttpClient HttpClient { get; init; }

    public required GlobalState GlobalState { get; init; }

    public static DownloadWorkerState Create(GlobalState state, int workerId)
    {
        return new DownloadWorkerState
        {
            HttpClient = state.HttpClientSettings.CreateHttpClient(),
            GlobalState = state,
            WorkerId = workerId
        };
    }

    public void Dispose()
    {
        HttpClient.Dispose();
    }

    internal static async Task RunAsync(DownloadWorkerState state)
    {
        using var _ = state;

        var globalState = state.GlobalState;
        var workerId = state.WorkerId;

        var input = state.GlobalState.Input;
        var cancellationToken = globalState.CancellationToken;

        Logger.ForTraceEvent()
            .Message(
                "[{WorkerId}] {Component} started",
                workerId,
                nameof(DownloadWorkerState))
            .Log();

        try
        {
            while (globalState.DownloadQueue.TryDequeue(out var item))
            {
                var chunk = item.Work.Source;
                Logger.ForTraceEvent()
                    .Message(
                        "[{WorkerId}] Downloading {ChunkId}/{Chunks} {Chunk}",
                        workerId,
                        item.Work.ChunkIndex,
                        input.Chunks,
                        chunk)
                    .Log();

                cancellationToken.ThrowIfCancellationRequested();
                await DownloadChunkAsync(state, item);

                Logger.ForTraceEvent()
                    .Message(
                        "[{WorkerId}] Downloaded {ChunkId}/{Chunks} {Chunk}",
                        workerId,
                        item.Work.ChunkIndex,
                        input.Chunks,
                        chunk)
                    .Log();
            }

            Logger.ForTraceEvent()
                .Message(
                    "[{WorkerId}] {Component} exiting with success",
                    workerId,
                    nameof(DownloadWorkerState))
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
                        nameof(DownloadWorkerState))
                    .Log();
            }
            else
            {
                Logger.ForFatalEvent()
                    .Message(
                        "[{WorkerId}] {Component} exiting with failure",
                        workerId,
                        nameof(DownloadWorkerState))
                    .Exception(ex)
                    .Log();
            }

            throw;
        }
    }

    private static async Task DownloadChunkAsync(DownloadWorkerState state, DownloadItem item)
    {
        var globalState = state.GlobalState;
        var statistics = globalState.Statistics;

        var input = state.GlobalState.Input;
        var cancellationToken = globalState.CancellationToken;
        var sourceChunk = item.Work.Source;

        var buffer = globalState.WriteStressSharedBuffer ?? globalState.GetBuffer((uint)sourceChunk.Length);

        var stopwatch = StopwatchSlim.Start();

        statistics.Increment(Counters.ChunksDownloadStarted);
        try
        {
            Contract.Assert(sourceChunk.Length <= buffer.Size, $"Received chunk {sourceChunk} exceeds buffer size {buffer.Size}");

            if (input.Arguments.SimulationMode == SimulationModes.None ||
                input.Arguments.SimulationMode == SimulationModes.DownloadStress)
            {
                await DownloadChunkIntoBufferAsync(state, item, buffer);
            }

            if (input.Arguments.SimulationMode != SimulationModes.DownloadStress)
            {
                if (item.Work.RawHash is not null)
                {
                    // When the hash is present, we need to verify that the downloaded chunk matches the hash. All
                    // compressed chunks MUST have a hash, so those cases will also utilize this branch.
                    await globalState.QueueDecompressionAsync(new DecompressionItem(item.Work, buffer), cancellationToken);
                }
                else
                {
                    // All other cases can be straight up written to disk, and that's what we'll do.
                    await globalState.QueueWriteAsync(new WriteItem(item.Work, buffer), cancellationToken);
                }

                buffer = null;
            }

            statistics.Increment(Counters.ChunksDownloadSucceeded);
        }
        catch (Exception ex)
        {
            globalState.ChunkHost?.CancelChunk(item.Work);

            if (ex is OperationCanceledException)
            {
                // Cancellation triggers logging at higher layers, so we don't log the exception here.
                statistics.Increment(Counters.ChunksDownloadCancelled);
            }
            else
            {
                statistics.Increment(Counters.ChunksDownloadFailed);

                Logger.ForFatalEvent()
                    .Message(
                        "[{WorkerId}] Permanently failed to download chunk #{ChunkIndex} {Chunk}",
                        state.WorkerId,
                        item.Work.ChunkIndex,
                        sourceChunk)
                    .Exception(ex)
                    .Log();

                throw;
            }

            // If at this point weve got an exception, there's absolutely no point in continuing, as we have already
            // retried as much as we're willing to.
            throw;
        }
        finally
        {
            // buffer is null if buffer is passed to write queue
            if (buffer != null)
            {
                buffer.Release();
            }

            statistics.Increment(TimeCounters.ChunkDownloadTime, stopwatch.Elapsed);
            statistics.Increment(Counters.ChunksDownloadCompleted);
        }
    }

    private static async Task DownloadChunkIntoBufferAsync(DownloadWorkerState state, DownloadItem item, AlignedManagedBuffer buffer)
    {
        var chunk = item.Work.Source;
        var httpClient = state.HttpClient;
        var globalState = state.GlobalState;
        var statistics = globalState.Statistics;
        var input = globalState.Input;
        var cancellationToken = globalState.CancellationToken;

        // The MemoryStream is only used to copy the response into the buffer, because we don't have a way to copy
        // the response directly into the buffer without having a Stream as an in-between.
        using var bufferStream = buffer.CreateMemoryStream();

        var predecessor = globalState.ChunkServer?.ProxyChain?.PredecessorEntry;
        var contextData = new Dictionary<string, object>
        {
            { Constants.WorkItem, item.Work },
        };

        if (predecessor != null)
        {
            contextData[Constants.Predecessor] = predecessor.Uri.GetLocation();
        }

        async ValueTask acquireSemaphoreAsync()
        {
            if (predecessor == null && globalState.BlobDownloadSemaphore is { } semaphore)
            {
                await semaphore.WaitAsync(cancellationToken);
            }
        }

        // Wait for blob download semaphore if specified.
        await acquireSemaphoreAsync();

        int tryIndex = -1;

        await globalState.PerformDownloadAsync(
            async (context, cancellationToken) =>
            {
                tryIndex++;

                // Don't use predecessor uri for retries
                // Also disable predecessor if blob semaphore has been acquired because otherwise
                // there can be deadlocks where B (acquired semaphore) depends on A (requesting semaphore with no more slots)
                if (tryIndex > 0 || globalState.BlobDownloadSemaphore?.IsAcquired == true)
                {
                    predecessor = null;
                }

                if (predecessor == null)
                {
                    context.Remove(Constants.Predecessor);
                }

                if (predecessor == null && globalState.DownloadRateLimiter is not null)
                {
                    // Call again here, so that retries which go to storage acquire the blob semaphore
                    await acquireSemaphoreAsync();

                    await globalState.DownloadRateLimiter.AcquireAsync((long)Math.Ceiling(chunk.Length.AsMb()), cancellationToken);
                }

                // When downloading from predecessor apply a timeout for the download operation.
                var downloadIterationTimeout = predecessor != null
                    ? TimeSpan.FromSeconds(input.Arguments.ProxyDownloadTimeoutSeconds)
                    : Timeout.InfiniteTimeSpan;

                await TaskUtilities.WithTimeoutAsync(
                    token => runDownloadInsideTimeoutAsync(token)
                        .WithResultAsync(Unit.Void),
                    downloadIterationTimeout,
                    cancellationToken);

                async Task runDownloadInsideTimeoutAsync(CancellationToken cancellationToken)
                {
                    using var timer = statistics.TrackDuration(TimeCounters.ChunkDownloadExclusiveTime);

                    state.GlobalState.Statistics.Increment(Counters.ChunkDownloadAttempts);

                    // Clean the context in case anything here throws. This stops us from misleading the retry policy.
                    context.Remove("Retry-After");

                    var requestArguments = new RangeDownloadArguments()
                    {
                        Etag = input.ETag,
                        ProxyArguments = predecessor == null ? null :
                            new ProxyRangeDownloadArguments()
                            {
                                ChunkIndex = item.Work.ChunkIndex,
                                ExpectedCertificateCertHash = predecessor.CertHash,
                                Requestor = globalState.ChunkServer!.ProxyChain!.Identity,
                                CompressionEncoding = item.Work.Compression,
                                DestinationChunk = item.Work.Destination
                            }
                    };

                    using var response = await httpClient.RangeDownloadAsync(predecessor?.Uri ?? input.Arguments.Uri, chunk, requestArguments, HttpCompletionOption.ResponseHeadersRead, cancellationToken: cancellationToken);

                    // We add information about the response to the context, so that we can use it in the retry policy.
                    if (response.Headers.RetryAfter != null)
                    {
                        Logger.ForInfoEvent()
                            .Message(
                                "[{WorkerId}] Retry-After header received: {RetryAfter}",
                                state.WorkerId,
                                response.Headers.RetryAfter)
                            .Log();

                        // We set this to UTC-based timestamps to ensure that we're not affected by timezones and our
                        // calculations are uniform.
                        var retryAfterDelta = TimeSpan.Zero;
                        if (response.Headers.RetryAfter.Delta != null)
                        {
                            retryAfterDelta = response.Headers.RetryAfter.Delta.Value;
                        }
                        else if (response.Headers.RetryAfter.Date != null)
                        {
                            retryAfterDelta = response.Headers.RetryAfter.Date.Value.UtcDateTime - DateTime.UtcNow;
                        }

                        if (retryAfterDelta > TimeSpan.Zero)
                        {
                            // This is a protective measure to ensure that we don't wait for too long. If the server sends
                            // something like this, we've already failed, we just haven't noticed.
                            if (retryAfterDelta >= input.Arguments.ChunkDownloadTimeout)
                            {
                                throw new InvalidOperationException($"Retry-After header value is too large. Received {retryAfterDelta}");
                            }

                            context["Retry-After"] = retryAfterDelta;
                        }
                    }

                    string? pendingShutdown = null;
                    if (predecessor != null
                        && (!response.IsSuccessStatusCode
                            || response.Headers.TryGetSingleValue(ChunkServer.PendingShutdownHeaderName, out pendingShutdown)))
                    {
                        statistics.Increment(pendingShutdown != null
                            ? Counters.PredecessorPendingShutdowns
                            : Counters.PredecessorDownloadFailures);

                        // BadRequest indicates a data mismatch with the server. Disable calling this predecessor.
                        globalState.ChunkServer?.ProxyChain?.MarkUnavailable(predecessor.Uri);
                    }

                    // The below code will throw HttpRequestException if the status code is not a success code. That will
                    // trigger the retry policy.
                    response.EnsureSuccessStatusCode();

                    var length = response.Content.Headers.ContentLength ?? throw new InvalidOperationException("Content-Length header missing in GET response from input URI");
                    Contract.Assert(length == chunk.Length, $"Content-Length header ({length}) doesn't match requested chunk {chunk}");

                    // We need the response to be copied into the buffer, but the only way the buffer can be written to is
                    // through the stream because that's what response.Content supports.
                    bufferStream.Seek(0, SeekOrigin.Begin);
                    bufferStream.SetLength(0);

                    var compressedHash = input.Arguments.SkipCompressedHashCheck ? null : item.Work.CompressedHash;
                    using (var hashingStream = compressedHash?.HashType.CreateWriteHashingStream(bufferStream, chunk.Length))
                    {
                        var targetStream = hashingStream ?? bufferStream;

                        // WARNING: the code below can throw an exception if there's issues with the HTTP connection, because
                        // we're reading the response body into a buffer while we're receiving it. If it throws, we'll trigger
                        // the retry policy.
                        await response.Content.CopyToAsync(targetStream, cancellationToken);

                        var computedCompressedHash = hashingStream?.GetContentHash();
                        statistics.Increment(TimeCounters.CompressedChunkHashingTime, hashingStream?.TimeSpentHashing ?? default);
                        if (computedCompressedHash != compressedHash)
                        {
                            // Surface error as HttpRequestException to allow retry.
                            throw new HttpRequestException(HttpRequestError.InvalidResponse, $"Mismatch content hash Actual: '{computedCompressedHash}'. Expected: '{item.Work.CompressedHash}'");
                        }
                    }

                    state.GlobalState.Statistics.Increment(Counters.BytesDownloaded, chunk.Length);
                    Contract.Assert(bufferStream.Length == length, "Reading response body into buffer yielded a content length that doesn't match Content-Length header");

                    if (predecessor != null)
                    {
                        statistics.Increment(Counters.PredecessorChunks);
                        statistics.Increment(Counters.PredecessorBytesDownloaded, chunk.Length);
                        if (response.TrailingHeaders.TryGetSingleValue(ChunkServer.PendingShutdownHeaderName, out pendingShutdown))
                        {
                            statistics.Increment(Counters.PredecessorPendingShutdownsTrailing);
                            globalState.ChunkServer?.ProxyChain?.MarkUnavailable(predecessor.Uri);
                        }
                    }
                    else
                    {
                        statistics.Increment(Counters.StorageChunks);
                        statistics.Increment(Counters.StorageBytesDownloaded, chunk.Length);
                    }

                    globalState.ChunkHost?.PutBuffer(work: item.Work, buffer, item.Work.Compression, isSourceChunk: true);
                }
            },
            contextData,
            cancellationToken);
    }
}
