// Copyright (C) Microsoft Corporation. All Rights Reserved.

#nullable enable

namespace FastDownload.Utilities;

public enum Counters
{
    BytesDownloaded,
    BytesDecompressionQueued,
    BytesWriteQueued,
    BytesTrimmed,
    BytesRead,
    BytesIncompressible,
    BytesSaved,
    BytesDecompressed,
    BytesUploaded,
    BytesWritten,
    BytesHashMismatch,
    BytesHashMatch,

    ChunksDownloadStarted,
    ChunksDownloadSucceeded,
    ChunksDownloadFailed,
    ChunksDownloadCancelled,
    ChunksDownloadCompleted,

    ChunkDownloadAttempts,
    ChunkDownloadTimeout,
    ChunkDownloadRetry,
    ChunkDownloadHttpRetry,

    ChunksDecompressionStarted,
    ChunksDecompressionSucceeded,
    ChunksDecompressionFailed,
    ChunksDecompressionCancelled,
    ChunksDecompressionCompleted,

    ChunksUploadStarted,
    ChunksUploadSucceeded,
    ChunksUploadCancelled,
    ChunksUploadFailed,
    ChunksUploadCompleted,

    ChunksWriteStarted,
    ChunksWriteSucceeded,
    ChunksWriteCancelled,
    ChunksWriteFailed,
    ChunksWriteCompleted,

    SemaphoreCheckStatusCalls,
    SemaphoreInitialSlotIndex,

    DownloadFailures,
    PredecessorChunks,
    PredecessorBytesDownloaded,
    StorageChunks,
    StorageBytesDownloaded,
    PredecessorDownloadFailures,
    PredecessorPendingShutdowns,
    PredecessorPendingShutdownsTrailing,

    ServerRequests,
    ServerSendMemoryChunks,
    ServerSendMemoryCompressedChunks,
    ServerSendMemoryUncompressedChunks,
    ServerSendMemoryBytes,
    ServerRequestedBytes,
    ServerSendBytes,
    ServerBadRequests,

    BuffersAllocated,
    BuffersOutstanding,

    ExclusiveDownloadSpeedMbps,
    DownloadSpeedMbps,
    TotalSpeedMbps,

    AllocateCount,
    ProxyInitialIndex,
    UncacheableChunks,
    CacheableChunks,
    ReleasedCacheableChunks,
    CurrentBufferCacheSize,

    // No Counter values should be greater than this value
    Max,
}
