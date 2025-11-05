// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Collections.Immutable;
using System.Diagnostics.ContractsLight;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using BuildXL.Utilities;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Core.Tracing;
using FastDownload.Download;
using NLog;

namespace FastDownload.Utilities;

/// <summary>
/// Attempts to reserve a slot using azure blob storage.
///
/// The core algorithm writes a unique blob under the specified folder in blob storage. The creation time of the blob is used to order the blob in the list
/// of blobs and the top <see cref="MaxSlots"/> blobs are considered to have acquired the slot. Blobs are explitly deleted when done. Otherwise, the blob expires
/// automatically after <see cref="KeepAliveTime"/> if not deleted due to abrupt termination.
///
/// Note, there are two levels of cancellation in this type. Call-level cancellation used by a particular call to WaitAsync
/// and instance-level cancellation which happens on Dispose. Call-level cancellation just causes the current call to
/// cancel, but shouldn't affect the ongoing slot acquisition and keep alive. Instance-level cancellation will terminate
/// slot acquisition and keep alive.
/// </summary>
public class AzureBlobSemaphore(
    Uri folderSasUrl,
    CancellationToken token,
    IReadOnlyDictionary<string, string>? additionalMetadata = null) : IAsyncDisposable
{
    private static readonly OperationLoggingAdapter OperationLogger = new OperationLoggingAdapter(nameof(AzureBlobSemaphore));

    private static ILogger Logger => OperationLogger.Logger;

    private readonly OperationContext _context = new OperationContext(new(OperationLogger), token);

    public string MachineName { get; set; } = Environment.MachineName;

    public bool Debug { get; set; }

    /// <summary>
    /// The maximum number of concurrent slots
    /// </summary>
    public required int MaxSlots { get; init; }

    /// <summary>
    /// The amount of time since last update before a blob slot is considered expired
    /// </summary>
    public required TimeSpan KeepAliveTime { get; init; }

    /// <summary>
    /// How often to recheck for avaiable slots
    /// </summary>
    public required TimeSpan RecheckInterval { get; init; }

    /// <summary>
    /// Timer implementation for allowing tests to simulate delays for <see cref="RecheckInterval"/>
    /// </summary>
    public AsyncTimer RecheckTimer { get; init; } = AsyncTimer.Default;

    /// <summary>
    /// Timer implementation for allowing tests to simulate delays for <see cref="KeepAliveTime"/>
    /// </summary>
    public AsyncTimer KeepAliveTimer { get; init; } = AsyncTimer.Default;

    private readonly SemaphoreSlim _blobModificationMutex = TaskUtilities.CreateMutex();
    private readonly AsyncBarrier _recheckBarrier = new();

    /// <summary>
    /// Double between 0 and 1 which expresses how often expired blobs should be deleted
    /// </summary>
    public required double CleanupRatio { get; init; }

    internal Statistics? Statistics { get; init; }

    private StopwatchSlim _startTimer = StopwatchSlim.Start();

    public bool IsAcquired { get; private set; }

    private Task _blobKeepAliveTask = Task.CompletedTask;
    private Task _checkBlobSlotStatusTask = Task.CompletedTask;
    private Task<Result<bool>>? _getSlotTask = null;
    private Task<bool>? _initializeTask = null;

    private ImmutableList<Action<SemaphoreListResult>> _recheckHandlers = ImmutableList<Action<SemaphoreListResult>>.Empty;

    private CancellationTokenSource _disposeCts = CancellationTokenSource.CreateLinkedTokenSource(token);

    private ServerClock _serverClock = null!;
    private BlockBlobClient _blob = null!;
    private SemaphoreEntry _blobEntry;
    private string? lastSnapshot = null;

    public Guid Id { get; } = Guid.NewGuid();

    private readonly BlobContainerClient _container = CreateBlobClient(folderSasUrl).GetParentBlobContainerClient();
    private readonly string _folderPrefix = (new BlobUriBuilder(folderSasUrl, trimBlobNameSlashes: true).BlobName + "/").TrimStart('/');

    public async Task<bool> SlotBlobExistsAsync()
    {
        await InitializeAsync();

        var r = await _blob.ExistsAsync();
        return r.Value;
    }

    private static BlockBlobClient CreateBlobClient(Uri uri)
    {
        if (uri.UseAzureCredentials())
        {
            return new BlockBlobClient(uri, new ManagedIdentityCredential());
        }
        else
        {
            return new BlockBlobClient(uri);
        }
    }

    public async Task InitializeAsync()
    {
        await Atomic.RunOnceAsync(ref _initializeTask, 0, async _ =>
        {
            _disposeCts.Token.Register(() =>
            {
                IsAcquired = false;
            });

            DateTimeOffset? serverTimestamp = await GetServerTimestampAsync();

            _serverClock = new ServerClock(serverTimestamp ?? DateTimeOffset.UtcNow, KeepAliveTimer);

            // The blob is name starts with an estimated server timestamp so that lexicographic order generally will match or closely match the order of blobs by creation time.
            // That said, we can't rely on this so we have to enumerate all the blobs and order by creation time. This ensures that it is impossible to
            // add a blob which skips in line. An optimization to avoid processing all entries is to stop enumerating once the lexographic name indicates a time
            // greater than the current blob's creation time.
            _blob = CreateBlobClient(folderSasUrl.Combine($"{_serverClock.Now.ToSortableFileNameString()}-{MachineName}.{Id}"));

            // Create the metadata
            _blobEntry = new SemaphoreEntry(
                new Dictionary<string, string>(additionalMetadata ?? ImmutableDictionary<string, string>.Empty),
                new(_blob.Name, LastModified: default, StoreEffectiveTimes: true))
            {
                Timeout = KeepAliveTime,
                Id = Id,
            };

            // Create the blob. This reserves a spot in the queue of waiters for the semaphore
            await _context.PerformOperationAsync(
                OperationLogger,
                async () =>
                {
                    var response = await _blob.UploadAsync(new MemoryStream(), cancellationToken: _disposeCts.Token);
                    await UpdateSnapshotAsync();

                    return Result.Success(response.Value.LastModified);
                },
                extraEndMessage: r => $"Folder={folderSasUrl.Scrub()} BlobSlotName={_blob?.Name}, LastModified={_blobEntry.LastModifiedTime:o}, ServerTimestamp={r.GetValueOrDefault():o}, ServerClock.Now={_serverClock.Now:o}",
                caller: "CreateSlotBlob")
                .ThrowIfFailureAsync();

            // Start a task to keep the blob alive so that it doesn't expire
            _blobKeepAliveTask = Task.Run(async () =>
            {
                using (KeepAliveTimer)
                {
                    // Keep blob alive by metadata periodically
                    // Update interval needs to be sufficient less than the
                    // KeepAliveTime so we use KeepAliveTime / 3.
                    while (!_disposeCts.IsCancellationRequested)
                    {
                        await KeepAliveTimer.Delay(KeepAliveTime.Multiply(0.33), _disposeCts.Token);

                        if (!_disposeCts.IsCancellationRequested)
                        {
                            await TouchAsync(TouchReason.KeepAlive);
                        }
                    }
                }
            });

            _checkBlobSlotStatusTask = Task.Run(async () =>
            {
                await _recheckBarrier.WaitAsync(_disposeCts.Token);

                using (RecheckTimer)
                {
                    while (!_disposeCts.IsCancellationRequested)
                    {
                        var recheckBarrierTask = _recheckBarrier.WaitAsync(_disposeCts.Token);

                        await CheckBlobSlotStatusAsync();

                        // The test timer expects every iteration to block until the delay completes
                        // or the token is triggered. So we create a separate token to cancel when
                        // the barrier is triggered rather than waiting on the timer.
                        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(_disposeCts.Token);

                        await Task.WhenAny(recheckBarrierTask, RecheckTimer.Delay(RecheckInterval, delayCts.Token))
                            .Unwrap();
                        delayCts.Cancel();
                    }
                }
            });

            return true;
        });
    }

    public async Task WaitAsync(CancellationToken token)
    {
        await InitializeAsync();

        // Cancellation here is not passed to AcquireSlotCoreAsync because
        // cancellation of this should just unblock the caller but not terminate
        // acquisition of the semaphore. Acquisition of the semaphore can only be
        // terminated by disposing the object.
        await TaskUtilities.AwaitWithCancellationAsync(
            Atomic.RunOnceAsync(// Action to get slot should only be run once.
                ref _getSlotTask,
                data: 0,
                _ => _context.PerformOperationAsync(
                    OperationLogger, async () =>
                    {
                        Statistics?.Increment(TimeCounters.SemaphoreStartWaitTime, _startTimer.Elapsed);
                        var waitWatch = StopwatchSlim.Start();
                        using var waitTimer = Statistics?.TrackDuration(TimeCounters.SemaphoreWaitTime);
                        var result = await AcquireSlotCoreAsync();
                        IsAcquired = result;
                        Logger.Info($"Semaphore acquire result={result}, WaitTime={waitWatch.Elapsed}, ServerTime={_serverClock.Now:o}, CurrentTime={DateTimeOffset.UtcNow:o}");
                        return Result.Success(result);
                    },
                    traceOperationStarted: true,
                    extraStartMessage: $"Folder={folderSasUrl.Scrub()}",
                    extraEndMessage: r => $"Folder={folderSasUrl.Scrub()} BlobSlotName={_blob?.Name}, Acquired={r.GetValueOrDefault()}")),
            cancellationToken: token);
    }

    private async Task<bool> AcquireSlotCoreAsync()
    {
        var waitWatch = StopwatchSlim.Start();
        // Mark in order to enqueue it
        await TouchAsync(TouchReason.Enqueue, entry =>
        {
            entry.Enqueued = true;
        });

        if (MaxSlots <= 0)
        {
            return true;
        }

        var isAcquiredTask = TaskSourceSlim.Create<bool>();

        using var _ = WatchActiveBlobs(result =>
        {
            var activeBlobs = result.ActiveBlobs;
            var index = activeBlobs
                .Where(e => e.Enqueued)
                .OrderBy(e => e.EnqueueTime)
                .IndexOfWhere(e => e.Id == Id);

            if (Debug)
            {
                Logger.Info(string.Join(
                    "\n",
                    [
                        "Enqueued blobs:",
                        ..activeBlobs.Select(a => $"{a.Data.Name} EnqueueTime={a.EnqueueTime:o}")
                    ]));
            }

            Contract.Assert(index >= 0);

            if (index < MaxSlots)
            {
                isAcquiredTask.TrySetResult(true);
            }
        });

        var result = await isAcquiredTask.Task;
        return result;
    }

    public async Task<IDisposable> StartProxyAsync(ProxyNodeEntry proxyData, Action<SemaphoreListResult> handler)
    {
        // Mark in order to enqueue it
        await TouchAsync(TouchReason.EnqueueProxy, entry =>
        {
            entry.EnqueuedProxy = true;
            proxyData.ApplyTo(entry);
        });

        var tcs = TaskSourceSlim.Create<UnitValue>();
        var result = WatchActiveBlobs(e =>
        {
            handler.Invoke(e);
            tcs.TrySetResult(UnitValue.Unit);
        });

        await tcs.Task;
        return result;
    }

    private async Task<DateTimeOffset?> GetServerTimestampAsync()
    {
        try
        {
            var response = await _container.GetPropertiesAsync(cancellationToken: _disposeCts.Token);
            return response.GetRawResponse()?.Headers.Date;
        }
        catch (RequestFailedException ex)
        {
            return ex.GetRawResponse()?.Headers.Date;
        }
    }

#pragma warning disable IDE0079 // Remove unnecessary suppression
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Performance", "CA1859:Use concrete types when possible for improved performance")]
#pragma warning restore IDE0079 // Remove unnecessary suppression
    private IDisposable WatchActiveBlobs(Action<SemaphoreListResult> handler)
    {
        var disposable = new DisposeAction<bool>(true, _ =>
        {
            Atomic.OptimisticUpdate(ref _recheckHandlers, r => r.Remove(handler));
        });

        Atomic.OptimisticUpdate(ref _recheckHandlers, r => r.Add(handler));

        // Activate the recheck loop
        _recheckBarrier.Reset();

        return disposable;
    }

    public async Task CheckBlobSlotStatusAsync()
    {
        var handlers = _recheckHandlers;
        if (handlers.Count == 0)
        {
            return;
        }

        var result = await _context.PerformOperationAsync(
            OperationLogger,
            async () =>
            {
                // Enumerate MaxSlots active blobs to
                // see if this instance's blob is amongst them
                var activeBlobs = await GetActiveBlobsAsync(AsyncOut.Var<int>(out var expiredCount))
                    .ToListAsync();

                var index = activeBlobs.IndexOfWhere(item => item.Id == Id);

                Statistics?.Increment(Counters.SemaphoreCheckStatusCalls);
                if (Statistics?.Read(Counters.SemaphoreCheckStatusCalls) == 1)
                {
                    Statistics?.Increment(Counters.SemaphoreInitialSlotIndex, index);
                }

                Contract.Check(index >= 0)?.Assert($"Active={activeBlobs.Count}, Expired={expiredCount.Value}");

                return Result.Success((index, activeBlobs, expiredCount));
            },
            caller: "CheckBlobSlotStatus",
            extraEndMessage: r => $"BlobName={_blob.Name}, {r.ThenOrDefault(v => $"BlobSlotIndex={v.index}, Active={v.activeBlobs.Count}, Expired={v.expiredCount.Value}, MaxSlots={MaxSlots}")}")
            .AsAsync(r => new SemaphoreListResult(r.index, r.activeBlobs));

        if (result.TryGetValue(out var r))
        {
            foreach (var handler in handlers)
            {
                handler.Invoke(r);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _context.PerformOperationAsync(
            OperationLogger,
            async () =>
            {
                _disposeCts.Cancel();

                var getSlotTask = Interlocked.Exchange(ref _getSlotTask, Task.FromResult(Result.Success(false)));

                if (getSlotTask != null)
                {
                    await getSlotTask.IgnoreErrorsAndReturnCompletion();
                }

                if (_blobKeepAliveTask != null)
                {
                    await _blobKeepAliveTask.IgnoreErrorsAndReturnCompletion();
                }

                if (_checkBlobSlotStatusTask != null)
                {
                    await _checkBlobSlotStatusTask.IgnoreErrorsAndReturnCompletion();
                }

                if (_blob != null)
                {
                    // Delete the blob once the operation completes
                    await _blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: token);
                }

                IsAcquired = false;

                return Result.Success(Unit.Void);
            },
            extraEndMessage: _ => $"Folder={folderSasUrl.Scrub()} BlobSlotName={_blob?.Name}")
            .IgnoreFailure();

        _disposeCts.Dispose();
    }

    private async IAsyncEnumerable<SemaphoreEntry> GetActiveBlobsAsync(AsyncOut<int> expiredCount)
    {
        async IAsyncEnumerable<(DateTimeOffset? ResponseDate, BlobItem BlobItem)> getBlobItems()
        {
            var pages = _blob.GetParentBlobContainerClient().GetBlobsAsync(BlobTraits.Metadata, BlobStates.Snapshots, prefix: _folderPrefix, cancellationToken: _disposeCts.Token);
            await foreach (var page in pages.AsPages())
            {
                // Use the date from the response to compare to blob expiry
                var responseDate = page.GetRawResponse().Headers.Date + RecheckTimer.VirtualTimeOffset;
                foreach (var blobItem in page.Values)
                {
                    if (string.IsNullOrEmpty(blobItem.Snapshot))
                    {
                        continue;
                    }

                    yield return (responseDate, blobItem);
                }
            }
        }

        var allItems = await getBlobItems().ToListAsync();

        var latestBlobSnapshots = allItems.GroupBy(b => b.BlobItem.Name).Select(g => g.MaxBy(b => SemaphoreEntry.GetLastModifiedTime(b.BlobItem)));

        foreach (var (responseDate, blobItem) in latestBlobSnapshots)
        {
            // Use the date from the response to compare to blob expiry
            var entry = new SemaphoreEntry(blobItem.Metadata, blobItem);
            var touchTime = entry.LastModifiedTime;

            var expiresAt = touchTime + entry.Timeout;
            if (entry.Timeout == default || expiresAt < responseDate)
            {
                expiredCount.Value++;
                bool isSelf = entry.Id == Id;
                Logger.Debug($"Expired blob: Name={blobItem.Name}, TouchTime={entry.LastModifiedTime}, ExpiresAt={expiresAt}, Timeout={entry.Timeout}, IsSelf={isSelf}");

                // Conditionally delete expired blob at random. This ensures there is low contention on delete
                // when multiple semaphore waiters observe the expired blob.
                if (Random.Shared.NextDouble() < CleanupRatio)
                {
                    await _context.PerformOperationAsync(
                        OperationLogger,
                        async () =>
                        {
                            var blobToDelete = _container.GetBlobClient(blobItem.Name);
                            var response = await blobToDelete.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, cancellationToken: _disposeCts.Token);
                            return Result.Success(Unit.Void);
                        },
                        extraEndMessage: r => $"Blob='{blobItem.Name}' TouchTime='{touchTime:o}' Timeout='{entry.Timeout}'",
                        caller: "DeleteExpiredBlob");
                }

                if (!isSelf)
                {
                    // Skip expired blobs
                    continue;
                }
            }

            yield return entry;
        }
    }

    private enum TouchReason
    {
        KeepAlive,
        Enqueue,
        EnqueueProxy
    }

    private async Task TouchAsync(TouchReason reason, Action<SemaphoreEntry>? updateEntry = null)
    {
        await InitializeAsync();

        using var scope = await _blobModificationMutex.AcquireAsync(_disposeCts.Token);

        updateEntry?.Invoke(_blobEntry);

        _blobEntry.UpdateCount++;
        if (KeepAliveTimer.VirtualTimeOffset != default)
        {
            _blobEntry.TouchTime = _serverClock.Now;
        }

        await _context.PerformOperationAsync(
            OperationLogger,
            async () =>
            {
                await UpdateSnapshotAsync();

                // Query EnqueueTime and ProxyEnqueueTime to value is updated and stored
                Analysis.IgnoreArgument(_blobEntry.EnqueueTime);
                Analysis.IgnoreArgument(_blobEntry.EnqueueProxyTime);

                return Result.Success(Unit.Void);
            },
            extraEndMessage: r => $"Name={_blob.Name}, LastModified={_blobEntry.LastModifiedTime:o}, UpdateCount={_blobEntry.UpdateCount} Reason={reason}");
    }

    private async Task UpdateSnapshotAsync()
    {
        var response = await _blob.CreateSnapshotAsync(_blobEntry.Metadata, cancellationToken: _disposeCts.Token);
        if (lastSnapshot != null)
        {
            var r = await _blob.WithSnapshot(lastSnapshot).DeleteIfExistsAsync(cancellationToken: _disposeCts.Token);
            Analysis.IgnoreArgument(r);
        }

        lastSnapshot = response.Value.Snapshot;
        _blobEntry.Data = _blobEntry.Data with
        {
            LastModified = SemaphoreEntry.GetLastModifiedTime(response.Value)
        };
    }

    private record ServerClock(DateTimeOffset StartTime, AsyncTimer timer)
    {
        private readonly StopwatchSlim _sw = StopwatchSlim.Start();
        public DateTimeOffset Now => StartTime + _sw.Elapsed + timer.VirtualTimeOffset;
    }
}

public record struct SemaphoreListResult(int SlotIndex, List<SemaphoreEntry> ActiveBlobs);
