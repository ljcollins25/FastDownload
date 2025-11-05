// Copyright (C) Microsoft Corporation. All Rights Reserved.

#nullable enable

using FastDownload.Shared.Manifest.V0;

namespace FastDownload.Download;

/// <summary>
/// Represents all information required for the download operation to do what it needs to do.
/// </summary>
internal sealed record Input(
    DownloadArguments Arguments,
    string? ETag,
    long RawSize,
    long ArchiveSize,
    uint Alignment,
    uint ChunkSize,
    long Chunks,
    uint MaximumDownloadConcurrency,
    uint MaximumDecompressionConcurrency,
    uint MaximumWriteConcurrency,
    Manifest? Manifest,
    IReadOnlyList<WorkItem> Items)
{
    public long TotalDownloadSize => Items.Sum(i => (long?)i.Source.Length) ?? 0;
}

