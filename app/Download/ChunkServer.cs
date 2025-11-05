// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Buffers;
using System.Diagnostics.ContractsLight;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using BuildXL.Utilities;
using BuildXL.Utilities.Core.Tracing;
using FastDownload.Shared;
using FastDownload.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NLog;
using static FastDownload.Utilities.HttpUtilities;

namespace FastDownload.Download;

/// <summary>
/// ASP.NET Core server which serves in-memory chunks from <see cref="ChunkHost"/>.
/// </summary>
internal class ChunkServer : IAsyncDisposable
{
    private readonly ChunkHost Host;

    public bool TrackMemory { get; init; } = false;

    public bool UseHttp2 { get; }

    private Statistics Statistics;

    private static readonly Logger Logger = LogManager.GetLogger(nameof(ChunkServer));

    public const string CompressionEncodingHeaderName = "Compression-Encoding";
    public const string PendingShutdownHeaderName = "Pending-Shutdown";
    public const string ChecksumQueryKey = "checksum";
    public const int ServerVersion = 1;

    private const string UriSuffix = "fastdownload/blob";

    public TimeSpan PendingShutdownDelay { get; init; }

    public bool IsPendingShutdown => _pendingShutdown != null;
    private CancellationTokenSource? _pendingShutdown;

    public Uri EndpointUri { get; }

    private WebApplication? _application;

    public ChunkServer(ChunkHost host, Statistics? statistics = default, bool useHttp2 = false)
    {
        Host = host;
        Statistics = statistics ?? new();
        UseHttp2 = useHttp2;
        EndpointUri = new Uri($"{host.MachineInfo.Uri}{UriSuffix}?{QueryParamKey.checksum}={host.MachineInfo.FileChecksum}&{QueryParamKey.version}={ServerVersion}&{QueryParamKey.http2}={useHttp2.AsNumberString()}");
        Host.MachineInfo.Uri = EndpointUri;

        if (Host.ProxySemaphore is not null)
        {
            ProxyChain = new ProxyChainManager(Host.ProxySemaphore, Host.MachineInfo)
            {
                Statistics = Statistics
            };
        }
    }

    public ProxyChainManager? ProxyChain { get; }

    public string? CertHash => Host.MachineInfo.CertHash;

    private WebApplication CreateApp()
    {
        ThreadPoolHelper.ConfigureWorkerThreadPools(Environment.ProcessorCount, 16);

        var builder = WebApplication.CreateBuilder();

        var certificate = CreateSelfSignedCertificate(EndpointUri.Host);
        Host.MachineInfo.CertHash = certificate.ComputeCertHash();

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Limits.MaxResponseBufferSize = Math.Max(1 << 16, Host.ChunkSize / 16);
            if (UseHttp2)
            {
                options.Limits.Http2.MaxFrameSize = 1 << 20;
            }

            options.ListenAnyIP(EndpointUri.Port, l =>
            {
                if (TrackMemory)
                {
                    l.Use(c =>
                    {
                        return context =>
                        {
                            context.Features.Set<IMemoryPoolFeature>(TrackingMemoryPoolFeature.Instance);
                            return c(context);
                        };
                    });
                }

                l.Protocols = UseHttp2 ? HttpProtocols.Http2 : HttpProtocols.Http1;
                if (EndpointUri.Scheme == "https")
                {
                    l.UseHttps(certificate);
                }
            });
        });

        // Don't log informational messages
        if (!Globals.IsTest)
        {
            ConfigureLogging(builder);
        }

        // Initialize the proxy chain manager to track predecessor
        if (ProxyChain is not null)
        {
            builder.Services.AddHostedService(_ => ProxyChain);
        }

        var app = builder.Build();

        // Main handler for REST queries
        app.MapGet($"/{UriSuffix}", async (HttpContext context) =>
        {
            Statistics.Increment(Counters.ServerRequests);

            var response = context.Response;
            var request = context.Request;
            bool succeeded = false;

            int? statusCode = null;

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, app.Lifetime.ApplicationStopping);
            var token = cts.Token;
            try
            {
                var checksum = request.GetQueryValue(QueryParamKey.checksum);
                if (checksum != Host.MachineInfo.FileChecksum)
                {
                    return;
                }

                var stopwatch = StopwatchSlim.Start();
                var headers = request.GetTypedHeaders();
                var requestedChunk = Chunk.FromRange(headers.Range!);
                var arguments = ProxyRangeDownloadArguments.Get(headers);

                Statistics.Increment(Counters.ServerRequestedBytes, requestedChunk.Length);

                void log(string action, string arg = "", bool force = false, TimeCounters? counter = null)
                {
                    var duration = stopwatch.ElapsedAndReset();
                    if (counter is not null)
                    {
                        Statistics.Increment(counter.Value, duration);
                    }

                    if (force || Globals.DebugProxy || duration.TotalMilliseconds > 1000)
                    {
                        Logger.Info($"{action} chunk: {requestedChunk} index={arguments.ChunkIndex} requestor={arguments.Requestor}, duration={duration}, {arg}");
                    }
                }

                using var registration = token.Register(() =>
                {
                    log("Aborted", force: true);
                });

                var entry = Host.GetChunkEntry(chunkIndex: arguments.ChunkIndex);

                Contract.Assert(entry.DestinationChunk == arguments.DestinationChunk);

                log("Started request", $"compression={arguments.CompressionEncoding}");

                // Wait for chunk download to complete and server content if still buffered in memory.
                using var chunk = await entry.TryGetChunkDataAsync(token);
                log("Awaited in-memory data of", $"foundData={chunk != null}", counter: TimeCounters.ServerAwaitDataTime);

                bool addedPendingShutdown = false;
                if (IsPendingShutdown)
                {
                    response.Headers[PendingShutdownHeaderName] = bool.TrueString;
                    addedPendingShutdown = true;
                }

                if (response.SupportsTrailers())
                {
                    response.DeclareTrailer(PendingShutdownHeaderName);
                }

                bool isCompressed = false;
                void addBytes(long count, bool finishChunk = true, bool isBuffer = true)
                {
                    if (finishChunk)
                    {
                        Statistics.Increment(Counters.ServerSendMemoryChunks);
                        Statistics.Increment(isCompressed
                            ? Counters.ServerSendMemoryCompressedChunks
                            : Counters.ServerSendMemoryUncompressedChunks);
                    }

                    Statistics.Increment(Counters.ServerSendBytes, count);
                }

                if (chunk?.Encoding == arguments.CompressionEncoding)
                {
                    isCompressed = chunk.Encoding != CompressionAlgorithm.None;
                    response.StatusCode = StatusCodes.Status206PartialContent;

                    // Found in-memory chunk with matching compression
                    // so return the chunk compressed with header indicating the data is not compressed
                    response.Headers[CompressionEncodingHeaderName] = chunk!.Encoding.ToString();
                    response.ContentLength = chunk.Bytes.Length;

                    await response.StartAsync(token);

                    log($"Sending in-memory bytes for", $"length={chunk.Bytes.Length}, isCompressed={isCompressed}", counter: TimeCounters.ServerResponseStartTime);

                    var writer = response.BodyWriter;
                    var bytes = chunk.Bytes;

                    // Split larger buffer writing to response stream as that yield
                    // modestly better performance results. Likely because ASP.NET core buffers
                    // the entire content unless FlushAsync is called
                    var writeLength = Math.Max((int)Host.ChunkSize / 8, 1 << 16);
                    for (int start = 0; start < chunk.Bytes.Length; start += writeLength)
                    {
                        writeLength = Math.Min(writeLength, bytes.Length - start);
                        var target = writer.GetMemory(writeLength);
                        var source = bytes.Slice(start, writeLength);
                        source.CopyTo(target);
                        writer.Advance(writeLength);

                        await writer.FlushAsync(token);
                        addBytes(writeLength, finishChunk: false);
                    }

                    addBytes(0, finishChunk: true);

                    log($"Sent in-memory bytes for", $"length={chunk.Bytes.Length}, isCompressed={isCompressed}", counter: TimeCounters.ServerResponseWriteTime);
                    succeeded = true;
                }
                else
                {
                    statusCode = StatusCodes.Status404NotFound;
                }

                if (succeeded && !addedPendingShutdown && IsPendingShutdown && response.SupportsTrailers())
                {
                    response.AppendTrailer(PendingShutdownHeaderName, bool.TrueString);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.Error($"Error {ex}");
            }
            finally
            {
                if (!succeeded)
                {
                    Statistics.Increment(Counters.ServerBadRequests);
                    // No range specified. Return bad request
                    if (!response.HasStarted)
                    {
                        response.StatusCode = statusCode ?? StatusCodes.Status400BadRequest;
                    }
                }
            }
        });

        return app;
    }

    private static void ConfigureLogging(WebApplicationBuilder builder) => builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);

    /// <summary>
    /// Prepare the server for shutdown by notifying clients in response headers. Also,
    /// set timer to signal abort of any outstanding requests.
    /// </summary>
    public void PrepareForShutdown()
    {
        Logger.ForInfoEvent()
            .Message(
                "Pending shutdown for server at '{EndpointUri}' in zone '{Zone}' with checksum '{Checksum}'.",
                EndpointUri,
                Host.MachineInfo.Zone,
                Host.MachineInfo.FileChecksum)
            .Log();

        var delaySeconds = PendingShutdownDelay.TotalSeconds;
        if (delaySeconds >= 0)
        {
            _pendingShutdown = new();
            _pendingShutdown.CancelAfter(PendingShutdownDelay);
        }
        else if (Globals.DebugProxy)
        {
            // Specifying negative delay indicates to wait for Console.ReadLine. This is only available
            // in DebugProxy mode.
            Console.ReadLine();
        }
    }

    /// <summary>
    /// Create a self-signed certificate for secure sharing of data between nodes
    /// </summary>
    public static X509Certificate2 CreateSelfSignedCertificate(string subjectName)
    {
        using (ECDsa key = ECDsa.Create())
        {
            var certificateRequest = new CertificateRequest(
                new X500DistinguishedName($"CN={subjectName}"),
                key,
                HashAlgorithmName.SHA256);

            var certificate = certificateRequest.CreateSelfSigned(
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(2));

            var result = new X509Certificate2(certificate.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.PersistKeySet | X509KeyStorageFlags.Exportable);
            return result;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_application is { } application)
        {
            _application = null;

            using var scope = Statistics.TrackDuration(TimeCounters.ServerStopTime);

            Logger.ForInfoEvent()
                .Message(
                    "Stopping server at '{EndpointUri}' in zone '{Zone}' with checksum '{Checksum}'.",
                    EndpointUri,
                    Host.MachineInfo.Zone,
                    Host.MachineInfo.FileChecksum)
                .Log();

            using (Statistics.TrackDuration(TimeCounters.PendingShutdownOverhangTime))
            {
                // Wait for pending shutdown
                if (_pendingShutdown != null && !_pendingShutdown.IsCancellationRequested)
                {
                    await _pendingShutdown.Token.GetCompletionTask();
                }
            }

            await application.StopAsync();

            Logger.ForInfoEvent()
                .Message(
                    "Stopped server at '{EndpointUri}' in zone '{Zone}' with checksum '{Checksum}' in {ServerStartTime} ms.",
                    EndpointUri,
                    Host.MachineInfo.Zone,
                    Host.MachineInfo.FileChecksum,
                    ((long)scope.Elapsed.TotalMilliseconds))
                .Log();
        }
    }

    public async ValueTask StartAsync()
    {
        using var scope = Statistics.TrackDuration(TimeCounters.ServerStartTime);

        Logger.ForInfoEvent()
            .Message(
                "Starting server at '{EndpointUri}' in zone '{Zone}' with checksum '{Checksum}'.",
                EndpointUri,
                Host.MachineInfo.Zone,
                Host.MachineInfo.FileChecksum)
            .Log();

        _application ??= CreateApp();

        await _application.StartAsync();

        Logger.ForInfoEvent()
            .Message(
                "Started server at '{EndpointUri}' in zone '{Zone}' with checksum '{Checksum}' in {ServerStartTime} ms.",
                EndpointUri,
                Host.MachineInfo.Zone,
                Host.MachineInfo.FileChecksum,
                ((long)scope.Elapsed.TotalMilliseconds))
            .Log();
    }
}

internal class TrackingMemoryPoolFeature : MemoryPool<byte>, IMemoryPoolFeature
{
    public static TrackingMemoryPoolFeature Instance { get; } = new();

    MemoryPool<byte> IMemoryPoolFeature.MemoryPool => this;

    public override int MaxBufferSize => Shared.MaxBufferSize;

    public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
    {
        var result = new WrapperOwner(this, Shared.Rent(minBufferSize));
        Update(result.Memory.Length, requested: minBufferSize);
        return result;
    }

    private record struct Counter() { public long Current; public long Total; }

    private Counter _sizeCounter = default;
    private Counter _rentalCounter = default;
    private Counter _requestedCounter = default;

    private void Update(long update, bool returning = false, long requested = 0)
    {
        if (!returning)
        {
            Interlocked.Add(ref _requestedCounter.Total, requested);
            Interlocked.Add(ref _sizeCounter.Total, update);
            Interlocked.Add(ref _rentalCounter.Current, 1);
            Interlocked.Add(ref _rentalCounter.Total, 1);
        }
        else
        {
            Interlocked.Add(ref _rentalCounter.Current, -1);
        }

        Interlocked.Add(ref _sizeCounter.Current, update);

        Console.WriteLine($"Size: {_sizeCounter}, Requested: {_requestedCounter}, Rentals: {_rentalCounter}");
    }

    protected override void Dispose(bool disposing) => Shared.Dispose();

    private class WrapperOwner(TrackingMemoryPoolFeature feature, IMemoryOwner<byte> inner) : IMemoryOwner<byte>
    {
        public Memory<byte> Memory => inner.Memory;

        public void Dispose()
        {
            feature.Update(-Memory.Length, true);
            inner.Dispose();
        }
    }
}
