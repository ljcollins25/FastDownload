// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Upload;

/// <summary>
/// Specifies how to handle sparse regions during upload/download.
/// This is used to optimize the upload process by skipping unnecessary data.
/// </summary>
public enum SparseHandlingMode
{
    /// <summary>
    /// Don't preserve sparse regions. Use normal upload/download behavior.
    /// </summary>
    None,

    /// <summary>
    /// Skip uploading chunks which do not intersect with written data regions.
    /// </summary>
    SkipHoles,

    /// <summary>
    /// If written chunks are below block size, compact multiple into a single download chunk (will not exceed block size) and store the information
    /// about the target chunk regions. This allows small chunks to be downloaded as a single batch.
    /// This is useful for scenarios where small chunks are interspersed throughout the file, as it reduces the number of requests and improves download performance.
    /// During download, the downloaded chunk is decompressed and split based on information about the target chunk regions prior to write.
    /// </summary>
    Compact
}
