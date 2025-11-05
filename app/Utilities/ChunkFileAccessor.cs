// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using FastDownload.Shared;
using FastDownload.Upload;

namespace FastDownload.Utilities;

/// <summary>
/// Provides access to a file stream for reading chunks.
/// </summary>
internal class ChunkFileAccessor(Stream stream)
{
    public static ChunkFileAccessor Create(FileStream stream, ChunkingScheme chunkingScheme)
    {
        if (chunkingScheme.SparseInfo?.Mode == SparseHandlingMode.Compact)
        {
            return new SparseChunkFileAccessor(stream, chunkingScheme.RawBlockSize, chunkingScheme.Chunks);
        }
        else
        {
            return new ChunkFileAccessor(stream);
        }
    }

    public virtual async ValueTask ReadChunkAsync(long offset, Memory<byte> buffer, CancellationToken token)
    {
        stream.Seek(offset, SeekOrigin.Begin);

        await stream.ReadExactlyAsync(buffer, token);
    }

    public virtual double GetPercent(long offset) => ((int)((100000 * offset) / stream.Length)) / 1000.0;
}

/// <summary>
/// Provides access to a file stream for reading virtual chunks composed of sparse physical chunks.
/// </summary>
internal class SparseChunkFileAccessor(FileStream stream, long blockSize, IReadOnlyList<ChunkMapping> chunks) : ChunkFileAccessor(stream)
{
    public override async ValueTask ReadChunkAsync(long offset, Memory<byte> buffer, CancellationToken token)
    {
        Contract.Assert(offset % blockSize == 0, $"Offset {offset} is not aligned to block size {blockSize}");
        var chunk = chunks[(int)(offset / blockSize)];
        if (chunk.SparseRegions is { } sparseRegions)
        {
            var remainingBuffer = buffer;

            foreach (var region in sparseRegions)
            {
                await base.ReadChunkAsync(region.Start, remainingBuffer.Slice(0, (int)region.Length), token);

                remainingBuffer = remainingBuffer.Slice((int)region.Length);
            }

            // When writing sparse chunks, we need to clear buffer
            // to avoid including leftover data
            remainingBuffer.Span.Clear();
        }
        else
        {
            await base.ReadChunkAsync(offset, buffer, token);
        }
    }

    public override double GetPercent(long offset) => ((int)((100000 * (offset / blockSize)) / chunks.Count)) / 1000.0;
}
