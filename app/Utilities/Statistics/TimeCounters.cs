// Copyright (C) Microsoft Corporation. All Rights Reserved.

#nullable enable

namespace FastDownload.Utilities;

internal enum TimeCounters
{
    TotalExecutionTime,
    QueueWriteWaitTime,
    PrepareWriteWaitTime,
    WriteWorkerTime,
    QueueDecompressionTime,
    ChunkDownloadTime,
    ChunkDownloadExclusiveTime,
    ChunkWriteTime,
    ChunkDecompressionTime,
    SemaphoreWaitTime,
    ChunkHashingTime,
    CompressedChunkHashingTime,
    ChunkDecompressionExclusiveTime,
    ServerAwaitDataTime,
    ServerAwaitWriteTime,
    ServerResponseStartTime,
    ServerResponseWriteTime,
    AllocateTime,
    PendingShutdownOverhangTime,
    BufferCleanupTime,
    GetBufferTime,
    AllocateBufferTime,
    ServerStartTime,
    ServerStopTime,
    ProgressReporterDownloadWallClockTime,
    ProgressReporterTotalWallClockTime,
    DownloadWallClockTime,
    TotalWallClockTime,
    SemaphoreStartWaitTime,
}