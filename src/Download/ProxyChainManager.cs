// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Collections.Concurrent;
using BuildXL.Cache.ContentStore.Interfaces.Results;
using BuildXL.Cache.ContentStore.Tracing;
using BuildXL.Cache.ContentStore.Tracing.Internal;
using FastDownload.Utilities;
using Microsoft.Extensions.Hosting;
using NLog;
using LogManager = NLog.LogManager;

namespace FastDownload.Download;

/// <summary>
/// Tracks the chain of proxy peers using metadata on the azure blob semaphore. The semaphore orders peers by their proxy enqueue time (<see cref="SemaphoreEntry.EnqueueProxyTime"/>)
/// and the current node is capable of downloading from predecessors in the queue.
/// </summary>
/// <param name="semaphore">the semaphore to use to track the peer proxy queue</param>
/// <param name="proxyData">data about the current node</param>
public class ProxyChainManager(AzureBlobSemaphore semaphore, ProxyNodeEntry proxyData) : BackgroundService
{
    private static readonly OperationLoggingAdapter OperationLogger = new OperationLoggingAdapter(nameof(ProxyChainManager));

    private static ILogger Logger => OperationLogger.Logger;

    public ProxyNodeEntry? PredecessorEntry
    {
        get => LinearChain
            ? _immediatePredecessorEntry.Value
            : _distantPredecessorEntry.Value ?? _immediatePredecessorEntry.Value;
    }

    internal Statistics? Statistics { get; init; }

    public Uri? PredecessorUri => PredecessorEntry?.Uri;

    public bool IsActive { get; private set; }

    public bool LinearChain { get; set; } = true;

    public string Identity { get; } = proxyData.Uri.GetLocation();

    /// <summary>
    /// The set of machines marked unavailable due to failures in downloads or
    /// explicit signal that server is pending shutdown
    /// </summary>
    private ConcurrentDictionary<Uri, int> _unavailableMachines = new();

    private object _lock = new object();
    private EntryHolder _distantPredecessorEntry = new("Distant Predecessor");
    private EntryHolder _immediatePredecessorEntry = new EntryHolder("Immediate Predecessor");

    private class EntryHolder(string Name)
    {
        private ProxyNodeEntry? _value;
        public ProxyNodeEntry? Value
        {
            get => _value;
            set
            {
                var oldValue = _value;
                _value = value;
                if (value?.Id != oldValue?.Id)
                {
                    Logger.ForInfoEvent()
                    .Message(
                        Name + " changed from '{OldPrecessor}' to '{NewPredecessor}'",
                        oldValue?.Uri,
                        value?.Uri)
                    .Log();
                }
            }
        }

        public void OnUnavailable(Uri uri)
        {
            if (Value?.Uri == uri)
            {
                Value = null;
            }
        }
    }

    /// <summary>
    /// Marks the given uri as unavailable so that subsequent downloads will no longer
    /// choose this peer.
    /// </summary>
    public void MarkUnavailable(Uri uri)
    {
        lock (_lock)
        {
            _unavailableMachines.TryAdd(uri, 0);
            _distantPredecessorEntry.OnUnavailable(uri);
            _immediatePredecessorEntry.OnUnavailable(uri);
        }
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        Logger.Info($"Registering: {proxyData}");

        await base.StartAsync(cancellationToken);

        // Await the execute task which is populated by this point so that application startup doesn't complete until
        // the proxy peer entry is registered
        await ExecuteTask!;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IsActive = true;
        int iteration = -1;
        var context = new OperationContext(new(OperationLogger), stoppingToken);
        var disposable = await semaphore.StartProxyAsync(proxyData, result =>
        {
            Interlocked.Increment(ref iteration);
            context.PerformOperation<Result<(SemaphoreEntry? Entry, int? Index)>>(
                OperationLogger,
                () =>
                {
                    bool isUnavailable(SemaphoreEntry e) => !e.EnqueuedProxy || _unavailableMachines.ContainsKey(e.MachineUri);

                    var proxies = result.ActiveBlobs
                        .Where(e => e.EnqueuedProxy && proxyData.FileChecksum == e.FileChecksum && !isUnavailable(e))
                        .OrderBy(e => e.EnqueueProxyTime)
                        .TakeWhile(e => e.Id != semaphore.Id)
                        .ToList();

                    if (Globals.DebugProxy)
                    {
                        var matchId = semaphore.Id;
                        Logger.Info($"Active blobs (i={iteration}) [{result.ActiveBlobs.Count}]:\n{result.ActiveBlobs.Select(e => $"{e.ToProxyString(matchId)} Available={!isUnavailable(e)}").Join("\n")}");
                        Logger.Info($"Proxies (i={iteration}, u={_unavailableMachines.Count}) [{proxies.Count}]:\n{proxies.Select(e => e.ToProxyString()).Join("\n")}");
                    }

                    try
                    {
                        if (!string.IsNullOrEmpty(proxyData.Zone) && proxies.Any(p => p.Zone == proxyData.Zone))
                        {
                            // TODO: Attempt to limit cross zone transfers. As it stands, the zone metadata is never populated
                            // because in current use cases, the azure metadata rest api returns empty for the zone field.
                        }

                        if (proxies.Count > 0)
                        {
                            int index = proxies.Count - 1;
                            var selectedProxy = proxies[index];
                            _immediatePredecessorEntry.Value = ProxyNodeEntry.From(selectedProxy);
                            if (iteration == 0)
                            {
                                // Only set distant predecessor once on initial proxy determination
                                _distantPredecessorEntry.Value = ProxyNodeEntry.From(proxies[index / 2]);
                            }

                            return (selectedProxy, index);
                        }
                        else
                        {
                            _immediatePredecessorEntry.Value = null;
                            _distantPredecessorEntry.Value = null;
                        }

                        return (default, default);
                    }
                    finally
                    {
                        if (iteration == 0)
                        {
                            Statistics?.Increment(Counters.ProxyInitialIndex, proxies.Count + 1);
                        }
                    }
                },
                caller: "UpdatePredecessorEntry",
                messageFactory: r => $"Predecessor={r.GetValueOrDefault().Entry?.MachineUri.GetLocation()}, PredecessorIndex={r.GetValueOrDefault().Index ?? -1}, Unavailable={_unavailableMachines.Count}");
        });

        stoppingToken.Register(() =>
        {
            disposable.Dispose();
            IsActive = false;
        });
    }
}
