// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.IO.Compression;
using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Cli;
using FastDownload.Utilities;
using NLog;

namespace FastDownload.Upload;

internal sealed record UploadArguments : ArgumentsBase
{
    protected override Logger Logger { get; } = LogManager.GetLogger(nameof(UploadArguments));

    public required string Path;

    public required Uri Uri;

    public required long BlockSize;

    public string? ManifestPath;

    public required CompressionAlgorithm Compression;

    public required CompressionLevel CompressionLevel;

    public required HashType UncompressedHashType;

    public required HashType CompressedHashType;

    public required bool Overwrite;

    /// <summary>
    /// Specifying this indicates that the file should be uploaded uncompressed with no manifest footer to
    /// exactly match the uploaded file bytes
    /// </summary>
    public bool Raw;

    /// <summary>
    /// The size of random file to upload
    /// </summary>
    public long? RandomFileSize;

    public int MaximumUploadConcurrency;

    public SparseHandlingMode SparseHandling;

    public bool SparseAware => SparseHandling != SparseHandlingMode.None;

    public Uri? CheckManifestUri;

    public bool CheckOnly;

    protected override async Task RunCoreAsync(Statistics statistics, CancellationTokenSource internalCancellationSource, CancellationToken internalCancellationToken)
    {
        await UploadCommand.ExecuteAsync(this, statistics, internalCancellationSource, internalCancellationToken);
    }

    protected override ReturnCode OnUnhandledException(Exception ex)
    {
        Logger.ForFatalEvent()
            .Message(
                "Failed to upload {Path} into {Uri}",
                Path,
                Uri.Scrub().ToString())
            .Exception(ex)
            .Log();

        return ReturnCode.UnhandledException;
    }

    protected override ReturnCode OnTimeout()
    {
        Logger.ForFatalEvent()
            .Message(
                "Failed to upload {Path} into {Uri} because the file upload timed out after {ExecutionTimeout}",
                Path,
                Uri.Scrub().ToString(),
                ExecutionTimeout)
            .Log();

        return ReturnCode.Timeout;
    }

    protected override ReturnCode OnControlC()
    {
        Logger.ForFatalEvent()
            .Message(
                "Failed to upload {Path} into {Uri} because the upload was cancelled by the user via Ctrl+C",
                Path,
                Uri.Scrub().ToString())
            .Log();

        return ReturnCode.Cancelled;
    }
}
