// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Collections.Concurrent;
using System.Diagnostics.ContractsLight;
using BuildXL.Utilities.Collections;
using FastDownload.Shared;
using FastDownload.Utilities;

namespace FastDownload.Upload;

/// <summary>
/// Information about sparse files
/// </summary>
/// <param name="OccupiedBytes">the actual number of bytes occupied by data regions in sparse file</param>
/// <param name="WrittenBytes">the number of bytes to be written for sparse file</param>
/// <param name="DownloadBytes">the number of uncompressed bytes downloaded (this includes padding bytes for partially filled download blocks)</param>
/// <param name="RegionCount">the number of sparse data regions (if sparse aware)</param>
/// <param name="Mode">the sparse handling mode</param>
internal record SparseInfo(long OccupiedBytes, long WrittenBytes, long DownloadBytes, long RegionCount, SparseHandlingMode Mode);

internal record ChunkingScheme(
    long RawFileSize,
    long RawBlockSize,
    IReadOnlyList<ChunkMapping> Chunks,
    SparseInfo? SparseInfo = null)
{
    public static ChunkingScheme FromFileStream(SparseHandlingMode sparseHandling, FileStream inputStream, long blockSize)
    {
        var fileSize = inputStream.Length;

        // We will produce blocks of the specified size, but align them to the unbuffered IO alignment of the file. The
        // reasoning behind this is that the downloading side will also read the file in chunks of the specified size,
        // and it's less work if the chunks are aligned to the unbuffered IO alignment of the file. Of course, the
        // remote host may not be aligned equivalently, but we can't control that (i.e., in those cases, they will have
        // to do the more involved thing of dealing with this difference).
        var alignment = inputStream.ComputeUnbufferedIOAlignment();
        blockSize = (long)Maths.AlignTo((ulong)blockSize, alignment);

        if (sparseHandling != SparseHandlingMode.None)
        {
            return CreateForSparseAware(inputStream, fileSize, alignment, blockSize, sparseHandling);
        }
        else
        {
            // We will not be using sparse aware chunking, so we can just use the file size as the raw file size.
            return Create(fileSize, alignment, blockSize);
        }
    }

    public static ChunkingScheme Create(long fileSize, uint alignment, long blockSize)
    {
        var numBlocks = fileSize / blockSize;
        if (fileSize % blockSize != 0)
        {
            numBlocks++;
        }

        var chunks = Chunk.Split(fileSize, numBlocks, (uint)blockSize, alignment).Select(c => new ChunkMapping(c)).ToList();
        Contract.Assert(chunks.Count == (int)numBlocks);

        return new ChunkingScheme(fileSize, blockSize, chunks);
    }

    /// <summary>
    /// Split written data regions into along block aligned boundaries and expanding to match file system alignment
    /// </summary>
    public static IEnumerable<Chunk> GetWrittenBlockRegions(IEnumerable<Chunk> dataRegions, long blockSize, long alignment, long fileSize)
    {
        var chunksByStart = new ConcurrentDictionary<long, BoxRef<Chunk>>();
        var fullFileChunk = new Chunk(0, fileSize);

        // Ensure data regions are aligned to file byte alignment
        // Also ensure in order based on start
        var orderedAlignedDataRegions = dataRegions.Select(d => d.AlignTo(alignment)).OrderBy(d => d.Start);

        foreach (var dataRegion in orderedAlignedDataRegions)
        {
            // Iterate all the blocks which this data region intersects
            var blockExtent = dataRegion.AlignTo(blockSize);
            for (long start = blockExtent.Start; start < blockExtent.End; start += blockSize)
            {
                var currentBlock = Chunk.FromStartAndLength(start, blockSize);

                // Intersect with current block
                var intersection = currentBlock.Intersect(dataRegion)!.Value;

                var chunkBox = chunksByStart.GetOrAdd(start, static (k, realStart) => Chunk.FromStartAndLength(realStart, 0), intersection.Start);

                // Extend the written portion of the block to include the data region
                chunkBox.Value = chunkBox.Value.Union(intersection);
            }
        }

        return chunksByStart.OrderBy(e => e.Key).Select(b => b.Value.Value.Intersect(fullFileChunk)!.Value);
    }

    /// <summary>
    /// Compact written blocks smaller than <paramref name="blockSize"/> into download blocks of <paramref name="blockSize"/> or less.
    /// This create virtual chunks which are written to multiple physical blocks
    /// </summary>
    public static List<ChunkMapping> GroupIntoDownloadChunks(IEnumerable<Chunk> writtenChunks, long blockSize)
    {
        var result = new List<ChunkMapping>();

        long currentChunkSize = 0;
        var chunkBuffer = new List<Chunk>();

        long virtualChunkOffset = 0;

        void flush()
        {
            if (chunkBuffer.Count != 0)
            {
                var offset = result.Count * blockSize;

                // Create a virtual chunk whose data maps to the constituent sparse regions
                var virtualChunkLength = chunkBuffer.Sum(c => c.Length);
                Contract.Assert(virtualChunkLength == currentChunkSize);
                Contract.Assert(virtualChunkLength <= blockSize);
                var info = new ChunkMapping(Chunk.FromStartAndLength(virtualChunkOffset, virtualChunkLength), chunkBuffer.ToArray());
                result.Add(info);
                virtualChunkOffset += blockSize;
            }

            chunkBuffer.Clear();
            currentChunkSize = 0;
        }

        foreach (var writtenChunk in writtenChunks)
        {
            // Limit to block size
            if ((currentChunkSize + writtenChunk.Length) > blockSize)
            {
                flush();
            }

            chunkBuffer.Add(writtenChunk);
            currentChunkSize += writtenChunk.Length;
        }

        flush();

        return result;
    }

    /// <summary>
    /// Creates a sparse-aware chunking scheme using the given <paramref name="mode"/> to determine how
    /// data regions in the file are mapped into chunks.
    /// </summary>
    public static ChunkingScheme CreateForSparseAware(FileStream inputStream, long fileSize, long alignment, long blockSize, SparseHandlingMode mode)
    {
        var dataRegions = inputStream.GetDataRegions();
        var actualSize = dataRegions.Sum(r => r.End - r.Start);

        var writtenChunks = GetWrittenBlockRegions(
            dataRegions,
            blockSize: blockSize,
            // When using skip holes mode, align to block size so that full blocks are written
            alignment: mode == SparseHandlingMode.SkipHoles ? blockSize : alignment,
            fileSize: fileSize);

        var downloadChunks = mode == SparseHandlingMode.SkipHoles
            ? writtenChunks.Select(c => new ChunkMapping(c)).ToList()
            : GroupIntoDownloadChunks(writtenChunks, blockSize);

        var writtenLength = downloadChunks.SelectMany(info => info.SparseRegions ?? [info.Chunk]).Sum(chunk => chunk.Length);
        var downloadLength = downloadChunks.Sum(info => info.Chunk.Length);

        Contract.Assert(writtenLength >= actualSize);
        Contract.Assert(downloadLength >= writtenLength);

        return new ChunkingScheme(
            fileSize,
            blockSize,
            downloadChunks,
            new SparseInfo(
                OccupiedBytes: actualSize,
                WrittenBytes: writtenLength,
                DownloadBytes: downloadLength,
                RegionCount: dataRegions.Count,
                Mode: mode));
    }
}
