// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Shared;
using FastDownload.Utilities;

namespace FastDownload.Download
{
    /// <summary>
    /// This record represents a piece that needs to be downloaded, decompressed, and written to disk. The information
    /// in it is common to all stages of the download process.
    /// </summary>
    internal record WorkItem(
        int ChunkIndex,
        Chunk Source,
        Chunk Destination,
        ContentHash? RawHash,
        CompressionAlgorithm Compression,
        ContentHash? CompressedHash,
        IReadOnlyList<Chunk>? SparseRegions,
        IReadOnlyList<WorkItem>? Destinations = null)
    {
        /// <summary>
        /// Gets the number of bytes written for the work item
        /// </summary>
        public long GetWrittenLength()
        {
            if (SparseRegions != null)
            {
                return SparseRegions.Sum(c => c.Length);
            }
            else if (Destinations != null)
            {
                return Destinations.Sum(d => d.Destination.Length);
            }
            else
            {
                return Destination.Length;
            }
        }
    }

    /// <summary>
    /// This represents a piece that needs to be downloaded. Used to pass information to
    /// <see cref="DownloadWorkerState"/>.
    /// </summary>
    internal record DownloadItem(WorkItem Work)
    {
        public ProxyNodeEntry? Predecessor;
        public int TryCount;
    }

    /// <summary>
    /// This represents a piece that needs to be either decompressed or have it's hash verified (when not using
    /// compression). Used to pass information to <see cref="DecompressionWorkerState"/>
    /// </summary>
    /// <remarks>
    /// The buffer that gets passed here should be returned to the pool after the work is done.
    /// </remarks>
    internal record DecompressionItem(WorkItem Work, AlignedManagedBuffer Buffer);

    /// <summary>
    /// This represents a piece that needs to be written to disk. Used to pass information to
    /// <see cref="WriteWorkerState"/>
    /// </summary>
    /// <remarks>
    /// The buffer that gets passed here should be returned to the pool after the work is done.
    /// </remarks>
    internal record WriteItem(WorkItem Work, ReadOnlyMemory<byte> Buffer, RefCountHandle<AlignedManagedBuffer> BufferHandle)
    {
        public WriteItem(WorkItem work, AlignedManagedBuffer buffer)
            : this(work, buffer.ReadOnlyMemory, buffer.RefCountHandle!)
        {
        }
    }

}