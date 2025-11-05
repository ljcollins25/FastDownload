// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Collections.Concurrent;
using System.Diagnostics.ContractsLight;
using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Azure.Core;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Core.Tracing;
using BuildXL.Utilities.ParallelAlgorithms;
using FastDownload.Shared;
using FastDownload.Utilities;
using NLog;
using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Retry;
using Polly.Timeout;

#nullable enable

namespace FastDownload.Download;

/// <summary>
/// Orchestrates download and writes for <see cref="DownloadArguments"/>.
///
/// 1. Compute chunks and store in <see cref="DownloadQueue"/>.
/// 2. Initiate download and writer threads using <see cref="DownloadWorkers"/> and <see cref="WriteWorkers"/> to manage concurrency. An item
///    in these queues corresponds to a download or writer thread respectively.
/// 3. Each download thread pulls chunks from <see cref="DownloadQueue"/> and downloads the byte range into a pooled buffer from <see cref="BufferPool"/>.
///    The number of outstanding buffers is bounded to twice the available number of download workers. (see <see cref="GetBuffer"/>)
///    Buffer is added to <see cref="WriteQueue"/> and item is added to <see cref="PendingWriteChannel"/> to notify writers that buffer is available
///    for writing. (see <see cref="QueueWriteAsync"/>)
/// 4. Each writer takes ordered chunks from <see cref="WriteQueue"/>. Potentially pulling multiple contiguous chunks if available up to a configured
///    threshold (<see cref="DownloadArguments.MaxContiguousBuffers"/>). (see <see cref="GetWritesAsync"/>) Ordering of chunks also allows for scenario where
///    we write file sequentially from a single thread.
/// </summary>
internal sealed class GlobalState
{
    private static readonly Logger Logger = LogManager.GetLogger(nameof(GlobalState));

    public Statistics Statistics { get; init; }

    public required HttpClientSettings HttpClientSettings { get; init; }

    public Input Input { get; }

    public SpeedRateLimiter? DownloadRateLimiter { get; init; }

    public SpeedRateAdjuster? DownloadRateAdjuster { get; init; }

    public AzureBlobSemaphore? BlobDownloadSemaphore { get; init; }

    public CancellationToken CancellationToken { get; }

    public CancellationTokenSource CancellationTokenSource { get; }

    public AsyncRetryPolicy ChunkRetryPolicy { get; }

    public ConcurrentQueue<DownloadItem> DownloadQueue { get; } = new();

    public ActionBlockSlim<DownloadWorkerState> DownloadWorkers { get; }

    public Channel<DecompressionItem> DecompressionQueue { get; }

    public ActionBlockSlim<DecompressionWorkerState> DecompressionWorkers { get; }

    /// <summary>
    /// Priority queue which returns chunk in order
    /// </summary>
    public PriorityQueue<WriteItem, long> WriteQueue { get; } = new();

    public ActionBlockSlim<WriteWorkerState> WriteWorkers { get; }

    /// <summary>
    /// Pool of buffers for storing downloaded chunks
    /// </summary>
    private TrackingObjectPool<AlignedManagedBuffer> BufferPool { get; }

    /// <summary>
    /// Channel representing available buffers for writing. We don't store the buffers here because
    /// buffers need to be returned in sequential order so we store them in <see cref="WriteQueue"/> instead.
    /// </summary>
    private Channel<Unit> PendingWriteChannel { get; }

    public AlignedManagedBuffer? WriteStressSharedBuffer { get; }

    internal ChunkHost? ChunkHost { get; set; }

    internal ChunkServer? ChunkServer { get; set; }

    public MerkleHasher MerkleHasher { get; } = new MerkleHasher();

    public GlobalState(Input input, Statistics statistics, CancellationToken cancellationToken)
    {
        Statistics = statistics;
        Input = input;
        CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        CancellationToken = CancellationTokenSource.Token;

        ChunkRetryPolicy = CreateHttpRetryPolicy();

        BufferPool = new(
            () =>
            {
                uint size = input.ChunkSize;
                return AllocateBuffer(size);
            },
            w => { });
        if (input.Arguments.IsWriteStress)
        {
            WriteStressSharedBuffer = BufferPool.GetInstance().Instance;
            WriteStressSharedBuffer.Memory.Span.Fill(1);
        }

        var arguments = input.Arguments;
        if (arguments.GlobalDownloadSemaphoreFolderUri is { } semaphoreUri)
        {
            BlobDownloadSemaphore = new AzureBlobSemaphore(semaphoreUri, CancellationToken)
            {
                Statistics = statistics,
                MaxSlots = arguments.MaxConcurrentDownloaders ?? -1,
                CleanupRatio = arguments.SemaphoreCleanupRatio,
                KeepAliveTime = TimeSpan.FromSeconds(Math.Max(1, arguments.SemaphoreKeepAliveSeconds)),
                RecheckInterval = TimeSpan.FromSeconds(Math.Max(1, arguments.SemaphoreRecheckSeconds)),
            };
        }

        DownloadWorkers = ActionBlockSlim.CreateWithAsyncAction<DownloadWorkerState>(
            new ActionBlockSlimConfiguration(
                (int)input.MaximumDownloadConcurrency,
                UseLongRunningTasks: true),
            static state => DownloadWorkerState.RunAsync(state),
            CancellationToken);

        var maximumOutstandingBuffersPerDecompressor = 2;
        var decompressionCapacity = Math.Max((int)input.MaximumDownloadConcurrency * 4, (int)(maximumOutstandingBuffersPerDecompressor * input.MaximumDecompressionConcurrency));

        DecompressionQueue = Channel.CreateBounded<DecompressionItem>(new BoundedChannelOptions(capacity: decompressionCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
            SingleReader = false,
            SingleWriter = false,
        });

        DecompressionWorkers = ActionBlockSlim.CreateWithAsyncAction<DecompressionWorkerState>(
            new ActionBlockSlimConfiguration(
                (int)input.MaximumDecompressionConcurrency,
                UseLongRunningTasks: true),
            static state => DecompressionWorkerState.RunAsync(state),
            CancellationToken);

        var maximumOutstandingWritesPerWriter = 2 * Math.Max(1, input.Arguments.MaxContiguousBuffers);
        if (input.Arguments.MaximumOutstandingWritesPerWriter >= 1)
        {
            maximumOutstandingWritesPerWriter = input.Arguments.MaximumOutstandingWritesPerWriter;
        }

        int pendingWriteCapacity = (int)(maximumOutstandingWritesPerWriter * input.MaximumWriteConcurrency);
        PendingWriteChannel = Channel.CreateBounded<Unit>(new BoundedChannelOptions(capacity: pendingWriteCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = false,
        });

        Logger.ForInfoEvent()
            .Message(
                "DecompressionQueue.Capacity: {decompressionCapacity}, PendingWriteChannel.Capacity: {PendingWriteCapacity}",
                decompressionCapacity,
                pendingWriteCapacity)
            .Log();

        WriteWorkers = ActionBlockSlim.CreateWithAsyncAction<WriteWorkerState>(
            new ActionBlockSlimConfiguration(
                (int)input.MaximumWriteConcurrency,
                UseLongRunningTasks: true),
            static state => WriteWorkerState.RunAsync(state),
            CancellationToken);

        if (input.Arguments.MaximumDownloadMbps > 0)
        {
            var period = TimeSpan.FromSeconds(1);
            DownloadRateLimiter = new SpeedRateLimiter(input.Arguments.MaximumDownloadMbps, period);
            DownloadRateAdjuster = new SpeedRateAdjuster(
                DownloadRateLimiter,
                Statistics,
                new SpeedRateAdjuster.Settings(
                    MinimumRate: input.Arguments.MinimumDownloadMbps,
                    MaximumRate: input.Arguments.MaximumDownloadMbps,
                    Period: period));
        }
    }

    private AlignedManagedBuffer AllocateBuffer(uint size)
    {
        using var scope = Statistics.TrackDuration(TimeCounters.AllocateBufferTime);
        return AlignedManagedBuffer.Allocate(size, Input.Alignment, useNative: Globals.UseNativeBuffers);
    }

    public AlignedManagedBuffer GetBuffer(uint? compressedSize = null)
    {
        using var _ = Statistics.TrackDuration(TimeCounters.GetBufferTime);

        if (WriteStressSharedBuffer != null)
        {
            return WriteStressSharedBuffer;
        }

        AlignedManagedBuffer instance;

        Contract.Check(!(compressedSize > Input.ChunkSize))?.Assert($"Requested compressed size ({compressedSize}) should not be greater than chunk size ({Input.ChunkSize})");

        if (!Input.Arguments.PoolCompressedBuffers && compressedSize != null)
        {
            // For buffers smaller than chunks (i.e. compressed buffers), don't use buffer pool with
            // oversized buffers
            instance = AllocateBuffer(compressedSize.Value);
            instance.ResetRefCount(() =>
            {
                using (Statistics.TrackDuration(TimeCounters.BufferCleanupTime))
                {
                    instance.Dispose();
                }
            });
        }
        else
        {
            instance = BufferPool.GetOrCreateInstance();
            instance.ResetRefCount(onCleanup: () =>
            {
                // When ref count reaches 0, the buffer is returned to the pool
                BufferPool.PutInstance(instance);
            });
        }

        return instance;
    }

    public async ValueTask QueueDecompressionAsync(DecompressionItem item, CancellationToken cancellationToken)
    {
        using var timer = Statistics.TrackDuration(TimeCounters.QueueDecompressionTime);
        await DecompressionQueue.Writer.WriteAsync(item, cancellationToken);
        Statistics.Increment(Counters.BytesDecompressionQueued, item.Work.Source.Length);
    }

    public async ValueTask QueueWriteAsync(WriteItem sourceItem, CancellationToken cancellationToken)
    {
        List<WriteItem> writeItems = ProcessWriteItem(sourceItem);

        using var timer = Statistics.TrackDuration(TimeCounters.QueueWriteWaitTime);
        long bytesWriteQueued = 0;

        lock (WriteQueue)
        {
            foreach (var item in writeItems)
            {
                WriteQueue.Enqueue(item, item.Work.Destination.Start);
                bytesWriteQueued += item.Work.Destination.Length;
            }
        }

        for (int i = 0; i < writeItems.Count; i++)
        {
            await PendingWriteChannel.Writer.WriteAsync(Unit.Void, cancellationToken);
        }

        Statistics.Increment(Counters.BytesWriteQueued, bytesWriteQueued);
    }

    private SemaphoreSlim _processWriteItemLock = TaskUtilities.CreateMutex();

    private List<WriteItem> ProcessWriteItem(WriteItem sourceItem)
    {
        var writeItems = new List<WriteItem>();

        using var _ = _processWriteItemLock.AcquireSemaphore();

        using var timer = Statistics.TrackDuration(TimeCounters.PrepareWriteWaitTime);

        if (sourceItem.Work.RawHash is not null)
        {
            MerkleHasher.Block sourceBlock = sourceItem;
            var writeWorkItems = sourceItem.Work.Destinations ?? [sourceItem.Work];
            MerkleHasher.AddMany(writeWorkItems.Select(d => (MerkleHasher.Block)d));
        }

        Chunk? trimmedRegion = null;

        if (Input.Arguments.SkipZeroRegions)
        {
            trimmedRegion = sourceItem.Buffer.Span.GetTrimRegion().AlignTo(Input.Alignment);
            if (trimmedRegion.Value.Length == 0)
            {
                // The whole buffer is filled with zeros. Skip it.
                sourceItem.BufferHandle.Dispose();
                return writeItems;
            }
        }

        if (sourceItem.Work.Destinations is { } destinationWorkItems)
        {
            foreach (var destinationWork in destinationWorkItems)
            {
                Contract.Assert(sourceItem.BufferHandle.TryReference());

                writeItems.Add(sourceItem with
                {
                    Work = destinationWork
                });
            }

            sourceItem.BufferHandle.Dispose();
        }
        else if (sourceItem.Work.SparseRegions is { } sparseRegions)
        {
            // Map sparse regions into target writes
            int bufferOffset = 0;
            foreach (var region in sparseRegions)
            {
                Contract.Assert(region.Start.IsAlignedOrZero(Input.Alignment), $"Start of sparse region {region} must be aligned");
                Contract.Assert(region.End.IsAlignedOrZero(Input.Alignment) || region.End == Input.RawSize, $"End of sparse region {region} must be aligned");

                var alignedLength = (int)region.AlignTo(Input.Alignment).Length;

                Contract.Assert(sourceItem.BufferHandle.TryReference());

                writeItems.Add(new WriteItem(
                    Work: sourceItem.Work with
                    {
                        // Update target write region
                        Destination = region,
                    },
                    // Extract the portion of buffer for this sparse region
                    Buffer: sourceItem.Buffer.Slice(bufferOffset, alignedLength),
                    BufferHandle: sourceItem.BufferHandle));

                bufferOffset += alignedLength;
            }

            sourceItem.BufferHandle.Dispose();
        }
        else
        {
            writeItems.Add(sourceItem);
        }

        // Check if there are leading and trailing zeros (requires a 25% savings from removing leading and trailing bytes)
        // as otherwise it might be preferable to just write the whole region since there's a greater likelihood of writing
        // muliple regions in a batch
        if (trimmedRegion?.Length < (Input.ChunkSize * 0.75))
        {
            for (int i = writeItems.Count - 1; i >= 0; i--)
            {
                var writeItem = writeItems[i];
                var destination = writeItem.Work.Destination;
                var targetBlock = destination.AlignTo(Input.ChunkSize);
                var targetRegion = trimmedRegion.Value.Shift(targetBlock.Start);
                if (targetRegion.Intersect(destination) is { } updatedDestination)
                {
                    writeItems[i] = writeItem with
                    {
                        Work = writeItem.Work with
                        {
                            Destination = updatedDestination
                        },

                        Buffer = writeItem.Buffer.Slice((int)(updatedDestination.Start - destination.Start), (int)updatedDestination.Length)
                    };
                }
                else
                {
                    writeItem.BufferHandle.Dispose();
                    writeItems.RemoveAt(i);
                }
            }
        }

        return writeItems;
    }

    public async IAsyncEnumerable<(Chunk Chunk, IReadOnlyList<WriteItem> Buffers)> GetWritesAsync([EnumeratorCancellation] CancellationToken token)
    {
        int maxContiguousBuffers = Math.Max(1, Input.Arguments.MaxContiguousBuffers);
        var items = new List<WriteItem>();

        void releaseBuffers()
        {
            if (items.Count == 0)
            {
                return;
            }

            foreach (var item in items)
            {
                item.BufferHandle.Release();
            }

            items = new List<WriteItem>();
        }

        try
        {
            await foreach (var ignored in PendingWriteChannel.Reader.ReadAllAsync(token))
            {
                Analysis.IgnoreArgument(ignored);

                long length = 0;
                long end = 0;

                lock (WriteQueue)
                {
                    while (items.Count < maxContiguousBuffers && WriteQueue.TryDequeue(out var writeItem, out var chunkStart))
                    {
                        Contract.Assert(items.Count == 0 || chunkStart == end);

                        end = Math.Max(end, writeItem.Work.Destination.End);
                        length += writeItem.Work.Destination.Length;
                        items.Add(writeItem);

                        if (!WriteQueue.TryPeek(out _, out var nextChunk) || nextChunk != end)
                        {
                            break;
                        }
                    }
                }

                if (items.Count > 0)
                {
                    yield return (new Chunk(Start: end - length, End: end), items);

                    releaseBuffers();
                }
            }
        }
        finally
        {
            releaseBuffers();
        }
    }

    public async Task RunAsync()
    {
        await Task.Yield();

        using var _1 = Statistics.TrackDuration(TimeCounters.TotalWallClockTime);

        try
        {
            // WARNING: Order matters here! We need to enqueue work items before creating the workers. If we create
            // them in parallel, the workers might start before the work items are enqueued, notice there's no work
            // items, and exit.
            EnqueueWorkItems();

            // Creating the download and write workers is done in parallel
            var downloadWorkers = CreateDownloadWorkersAsync();

            var decompressionWorkers = CreateDecompressionWorkersAsync();
            var writeWorkers = CreateWriteWorkersAsync();

            // Dispose of the BlobDownloadSemaphore here so that we unblock other
            // downloaders as soon as possible without waiting for decompression and writing to complete.
            await using (BlobDownloadSemaphore)
            using (Statistics.TrackDuration(TimeCounters.DownloadWallClockTime))
            {
                await downloadWorkers;

                // Prepare for shutdown to notify clients that shutdown is imminent
                // and to move on to other servers
                ChunkServer?.PrepareForShutdown();
            }

            DecompressionQueue.Writer.Complete();
            await decompressionWorkers;
            PendingWriteChannel.Writer.Complete();
            await writeWorkers;

            if (Input.Manifest is not null)
            {
                var contentHasher = HashInfoLookup.GetContentHasher(Input.Manifest.Hash.HashType);
                var downloadHash = MerkleHasher.Compute(contentHasher);

                Logger.ForInfoEvent()
                    .Message("Manifest has hash {ManifestHash}. Download has hash {DownloadHash}", Input.Manifest.Hash, downloadHash)
                    .Log();

                if (downloadHash != Input.Manifest.Hash)
                {
                    throw new InvalidOperationException($"Content hash mismatch for manifest. Expected: {Input.Manifest.Hash}, Actual: {downloadHash}");
                }
            }
        }
        finally
        {
            // Ensure all the outstanding operations are completed/canceled. Namely, if an exception is thrown.
            DecompressionQueue.Writer.TryComplete();
            PendingWriteChannel.Writer.TryComplete();
            CancellationTokenSource.Cancel();

            if (ChunkServer != null)
            {
                await ChunkServer.DisposeAsync();
            }

            ChunkHost?.Dispose();

            int outstandingBuffers = 0;
            using (Statistics.TrackDuration(TimeCounters.BufferCleanupTime))
            {
                foreach (var item in BufferPool.AllItems)
                {
                    if (item.RefCountHandle?.Value != null)
                    {
                        // Handle has value so it has active references
                        outstandingBuffers++;
                    }

                    if (Input.Arguments.ReleaseBuffers)
                    {
                        item.Dispose();
                    }
                }
            }

            Statistics.Increment(Counters.BuffersAllocated, BufferPool.AllocatedCount);
            Statistics.Increment(Counters.BuffersOutstanding, outstandingBuffers);

            if (DownloadRateLimiter != null)
            {
                await DownloadRateLimiter.DisposeAsync();
            }

            if (DownloadRateAdjuster != null)
            {
                await DownloadRateAdjuster.DisposeAsync();
            }
        }
    }

    private async Task CreateWriteWorkersAsync()
    {
        await Task.Yield();

        var writeWorkerStates = Enumerable
            .Range(0, (int)Input.MaximumWriteConcurrency)
            .Select(i => WriteWorkerState.Create(this, i));

        await WriteWorkers.PostAllAndComplete(writeWorkerStates);
    }

    private async Task CreateDecompressionWorkersAsync()
    {
        await Task.Yield();

        var decompressionWorkerStates = Enumerable
            .Range(0, (int)Input.MaximumDecompressionConcurrency)
            .Select(i => DecompressionWorkerState.Create(this, i));

        await DecompressionWorkers.PostAllAndComplete(decompressionWorkerStates);
    }

    private async Task CreateDownloadWorkersAsync()
    {
        await Task.Yield();

        var downloadWorkerStates = Enumerable
            .Range(0, (int)Input.MaximumDownloadConcurrency)
            .Select(i => DownloadWorkerState.Create(this, i));

        await DownloadWorkers.PostAllAndComplete(downloadWorkerStates);
    }

    private void EnqueueWorkItems()
    {
        foreach (var item in Input.Items)
        {
            DownloadQueue.Enqueue(new DownloadItem(item));
        }
    }

    private AsyncRetryPolicy CreateHttpRetryPolicy()
    {
        // This policy is based on the Azure SDK retry policy. We do retry 5 times instead of 3 as recommended, because
        // failing to download a chunk is a critical failure that'll fail the entire download.
        // See: https://azure.github.io/azure-sdk/general_azurecore.html#retry-policy
        var medianDelay = TimeSpan.FromSeconds(0.8);
        var delay = Backoff.DecorrelatedJitterBackoffV2(
            medianFirstRetryDelay: medianDelay,
            retryCount: 5,
            // This is explicitly disabled to prevent DDoS-like behavior where all clients retry at the same time
            fastFirst: false);

        var httpRetryPolicy =
            Policy
            .Handle<HttpRequestException>(exception =>
            {
                // Exceptions thrown when using a proxy should always retry
                if (exception is ProxyHttpRetryException)
                {
                    return true;
                }

                // If there was any HTTP issue, retry. This is basically protocol and network errors, but doesn't
                // include status codes that would be unsuccessful in the HTTP protocol.
                if (exception.HttpRequestError != HttpRequestError.Unknown)
                {
                    return true;
                }

                // The 500 range is always retried, as per the Azure Blob Storage retry policy guidance.
                // See: https://learn.microsoft.com/en-us/azure/storage/blobs/storage-performance-checklist#timeout-and-server-busy-errors
                if (exception.StatusCode is not null and >= HttpStatusCode.InternalServerError)
                {
                    return true;
                }

                // Do not retry any response with a status code between [300, 500).
                //
                // The 400 range is part of the Azure SDK retry policy guidance, while the 300 range is because we
                // don't obey anything in the 300 range.
                // See: https://azure.github.io/azure-sdk/general_azurecore.html#retry-policy
                if (exception.StatusCode is not null and >= HttpStatusCode.Ambiguous and < HttpStatusCode.InternalServerError)
                {
                    return false;
                }

                // Retry on network errors.
                if (exception.InnerException is SocketException or IOException or ProtocolViolationException)
                {
                    return true;
                }

                // Ignore everything else.
                return false;
            })
            .WaitAndRetryAsync(
                retryCount: delay.Count(),
                sleepDurationProvider: (int retryAttempt, Context context) =>
                {
                    if (context.ContainsKey(Constants.Predecessor))
                    {
                        // When download from predecessor, immediately retry from storage.
                        return TimeSpan.Zero;
                    }

                    var waitTime = delay.ElementAt(retryAttempt - 1);

                    if (context.TryGetValue("Retry-After", out var retryAfterObj))
                    {
                        var retryAfterHeader = (TimeSpan)retryAfterObj;
                        var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * retryAfterHeader.TotalMilliseconds * 0.2);
                        waitTime = retryAfterHeader + jitter;
                    }

                    waitTime = waitTime.Min(TimeSpan.FromSeconds(60));
                    return waitTime;
                },
                onRetryAsync: (Exception exception, TimeSpan timespan, int attempt, Context context) =>
                {
                    if (exception is ProxyHttpRetryException p && p.InnerException is not null)
                    {
                        exception = p.InnerException;
                    }

                    Statistics.Increment(Counters.ChunkDownloadHttpRetry);

                    // Defensive handling of unknown chunk start/end for logging
                    var workItem = context.GetValueOrDefault(Constants.WorkItem) as WorkItem;
                    var predecessor = context.GetValueOrDefault(Constants.Predecessor) as string;
                    var timer = context.GetValueOrDefault(Constants.StartWatch) as StopwatchSlim?;

                    var chunkIndex = workItem?.ChunkIndex ?? -1;
                    var chunkStart = workItem?.Source.Start ?? -1;
                    var chunkEnd = workItem?.Source.End ?? -1;
                    var elapsed = timer?.Elapsed ?? TimeSpan.FromSeconds(-1);

                    // This should never happen, but still checking to ensure we fail gracefully
                    if (exception is null)
                    {
                        Statistics.Increment($"RetryUnknownError");

                        Logger.ForErrorEvent()
                            .Message(
                                "Retrying chunk download due to an unknown error. Attempt=[{Attempt}] ChunkIndex=[{ChunkIndex}] ChunkStart=[{ChunkStart}] ChunkEnd=[{ChunkEnd}] Elapsed=[{Elapsed}] Predecessor=[{Predecessor}]",
                                attempt,
                                chunkIndex,
                                chunkStart,
                                chunkEnd,
                                elapsed,
                                predecessor)
                            .Log();

                        return Task.CompletedTask;
                    }

                    if (exception is HttpRequestException httpRequestException)
                    {
                        var statusCode = httpRequestException.StatusCode;

                        Statistics.Increment($"Retry{statusCode}");

                        Logger.ForErrorEvent()
                            .Message(
                                "Retrying chunk download due to an HTTP failure. Attempt=[{Attempt}] ChunkIndex=[{ChunkIndex}] ChunkStart=[{ChunkStart}] ChunkEnd=[{ChunkEnd}] Elapsed=[{Elapsed}] StatusCode=[{StatusCode}] Predecessor=[{Predecessor}]",
                                attempt,
                                chunkIndex,
                                chunkStart,
                                chunkEnd,
                                elapsed,
                                statusCode,
                                predecessor)
                            .Exception(exception)
                            .Log();

                        return Task.CompletedTask;
                    }
                    else
                    {
                        Statistics.Increment($"RetryException");

                        Logger.ForErrorEvent()
                            .Message(
                                "Retrying chunk download due to an exception. Attempt=[{Attempt}] ChunkIndex=[{ChunkIndex}] ChunkStart=[{ChunkStart}] ChunkEnd=[{ChunkEnd}] Elapsed=[{Elapsed}] Predecessor=[{Predecessor}]",
                                attempt,
                                chunkIndex,
                                chunkStart,
                                elapsed,
                                chunkEnd,
                                predecessor)
                            .Exception(exception)
                            .Log();

                        return Task.CompletedTask;
                    }
                });

        return httpRetryPolicy;
    }

    private long _waitUntil = 0;

    internal async Task PerformDownloadAsync(Func<Context, CancellationToken, Task> action, Dictionary<string, object> contextData, CancellationToken cancellationToken)
    {
        // This policy will timeout the entire download if a single chunk takes too long to download. The policy is
        // meant to fail fast when storage is being slow for some reason.
        var timeoutPolicy = Policy.TimeoutAsync(
            Input.Arguments.ChunkDownloadTimeout,
            // This code is written in a way that it can handle the timeout gracefully, so we don't need to use the
            // pessimistic timeout strategy.
            TimeoutStrategy.Optimistic);

        // We set this up here so that this time isn't counted against the total chunk download timeout.
        await WaitUntilRetryAfterAsync(cancellationToken);

        bool UpdateRetryAfterOnException(Context context)
        {
            if (context.TryGetValue("Retry-After", out var retryAfterObj))
            {
                var retryAfter = (TimeSpan)retryAfterObj;
                var waitUntil = DateTime.UtcNow + retryAfter;
                Interlocked.Exchange(ref _waitUntil, waitUntil.Ticks);
            }

            return false;
        }

        string? predecessor = null;

        try
        {
            await timeoutPolicy.ExecuteAsync(
            async (cancellationToken) =>
            {
                await ChunkRetryPolicy.ExecuteAsync(
                    async (context, cancellationToken) =>
                    {
                        try
                        {
                            context[Constants.StartWatch] = StopwatchSlim.Start();
                            await action(context, cancellationToken);

                            // Any successful download stops the waitUntil timer.
                            Interlocked.Exchange(ref _waitUntil, DateTime.MinValue.Ticks);
                        }
                        catch (HttpRequestException ex) when (UpdateRetryAfterOnException(context) || context.TryGetValue(Constants.Predecessor, out predecessor))
                        {
                            throw new ProxyHttpRetryException(predecessor!, ex);
                        }
                        catch (Exception ex) when (context.TryGetValue(Constants.Predecessor, out predecessor))
                        {
                            throw new ProxyHttpRetryException(predecessor, ex);
                        }
                    }, contextData, cancellationToken);
            }, cancellationToken);
        }
        catch (TimeoutRejectedException)
        {
            Statistics.Increment(Counters.ChunkDownloadTimeout);
            throw;
        }
    }

    private async Task WaitUntilRetryAfterAsync(CancellationToken cancellationToken)
    {
        // If any thread has observed a Retry-After, we'll make all of them wait for that duration + some jitter before
        // retrying.
        var waitUntilTicks = Interlocked.Read(ref _waitUntil);
        var waitUntil = new DateTime(ticks: waitUntilTicks, kind: DateTimeKind.Utc);
        var delay = waitUntil - DateTime.UtcNow;
        var jitter = TimeSpan.FromMilliseconds(Random.Shared.NextDouble() * delay.TotalMilliseconds * 0.2);
        var waitTime = delay + jitter;
        if (waitTime > TimeSpan.Zero)
        {
            Logger.ForTraceEvent()
                .Message(
                    "Waiting for Retry-After delay. WaitTime=[{WaitTime}]",
                    waitTime)
                .Log();

            await Task.Delay(waitTime, cancellationToken);
        }
    }

    private class ProxyHttpRetryException(string predecessor, Exception innerException) : HttpRequestException(innerException.Message, innerException)
    {
        public string Predecessor { get; } = predecessor;
    }
}
