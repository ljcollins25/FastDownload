// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Net;
using FastDownload.Cli;
using FastDownload.Utilities;
using NLog;

namespace FastDownload.Download;

internal sealed record DownloadArguments : ArgumentsBase
{
    protected override Logger Logger { get; } = LogManager.GetLogger(nameof(DownloadArguments));

    public required Uri Uri;

    public required string Path;

    /// <summary>
    /// Path to content to verify writes against. Normally, the original uploaded content
    /// </summary>
    public string? VerificationExpectedContentPath;

    public string? ManifestPath;

    /// <summary>
    /// Indicates whether deduplicate chunks should only be downloaded once and expanded out to
    /// the necessary write offsets
    /// </summary>
    public bool DedupeDownloadChunks;

    /// <summary>
    /// Indicates whether written chunks should be trimmed of leading and trailing zeroes
    /// and skips any chunks which are all zeroes
    /// </summary>
    public bool SkipZeroRegions;

    public Uri? GlobalDownloadSemaphoreFolderUri;

    public int? MaxConcurrentDownloaders;

    public uint SemaphoreKeepAliveSeconds;

    public uint SemaphoreRecheckSeconds;

    public double SemaphoreCleanupRatio;

    public long MinimumDownloadMbps;

    public long MaximumDownloadMbps;

    public uint MaximumDownloadConcurrency;

    public uint? MaximumDecompressionConcurrency;

    public uint? MaxWriteConcurrency;

    public uint ChunkSize;

    public double ProgressIntervalSeconds;

    public bool WriteThrough;

    public bool Async;

    /// <summary>
    /// Indicates that content should not be written disk.
    /// </summary>
    public bool SkipWriteContent;

    public bool SkipCompressedHashCheck;

    public bool Sparse;

    public double ChunkDownloadTimeoutMinutes;

    public int MaxContiguousBuffers;

    public int MaximumOutstandingWritesPerWriter;

    #region Proxy Arguments

    public HttpProtocol ServerProtocol;

    public bool ProxyServerEnabled => Port != 0 && GlobalDownloadSemaphoreFolderUri is not null;

    public ushort Port;

    public double PendingShutdownDelaySeconds;

    public long ProxyDownloadTimeoutSeconds = 30;

    public long ProxyBufferSizeMb = Constants.TotalStartMemoryGb / 16;

    #endregion Proxy Arguments

    public SimulationModes SimulationMode;

    public bool ReleaseBuffers = true;

    public bool UseHttp2 = false;

    public bool UseLinearProxyChain = true;

    public bool PoolCompressedBuffers = true;

    public string ProxyScopeId = "default";

    public bool IsWriteStress => SimulationMode == SimulationModes.WriteStress ||
        SimulationMode == SimulationModes.WriteStressSequential ||
        SimulationMode == SimulationModes.WriteStressSequentialAsync;

    public TimeSpan ChunkDownloadTimeout => TimeSpan.FromMinutes(ChunkDownloadTimeoutMinutes);

    public enum HttpProtocol
    {
        https,
        http
    }

    public enum SimulationModes
    {
        None,
        WriteStress,
        WriteStressSequential,
        WriteStressSequentialAsync,
        DownloadStress
    }

    protected override async Task RunCoreAsync(Statistics statistics, CancellationTokenSource internalCancellationSource, CancellationToken cancellationToken)
    {
        await DownloadCommand.ExecuteAsync(this, statistics, internalCancellationSource, cancellationToken);
    }

    protected override ReturnCode OnUnhandledException(Exception ex)
    {
        Logger.ForFatalEvent()
            .Message(
                "Failed to download from {Uri} into {Path}",
                Uri.Scrub().ToString(),
                Path)
            .Exception(ex)
            .Log();

        var baseEx = ex.GetBaseException();
        if (baseEx is HttpRequestException httpEx)
        {
            if (httpEx.StatusCode == HttpStatusCode.NotFound)
            {
                return ReturnCode.NotFound;
            }
            else if (httpEx.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return ReturnCode.Throttled;
            }
            else if (httpEx.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                var errorMessage = httpEx.Message;
                // Check for both "ingress" and "egress"
                if (errorMessage.Contains("egress", StringComparison.OrdinalIgnoreCase) ||
                    errorMessage.Contains("ingress", StringComparison.OrdinalIgnoreCase))
                {
                    return ReturnCode.Throttled;
                }
            }
        }

        return ReturnCode.UnhandledException;
    }

    protected override ReturnCode OnTimeout()
    {
        Logger.ForFatalEvent()
            .Message(
                "Failed to download from {Uri} into {Path} because the file download timed out after {Timeout}",
                Uri.Scrub().ToString(),
                Path,
                ExecutionTimeout)
            .Log();

        return ReturnCode.Timeout;
    }

    protected override ReturnCode OnControlC()
    {
        Logger.ForFatalEvent()
            .Message(
                "Failed to download from {Uri} into {Path} because the download was cancelled by the user via Ctrl+C",
                Uri.Scrub().ToString(),
                Path)
            .Log();

        return ReturnCode.Cancelled;
    }
}
