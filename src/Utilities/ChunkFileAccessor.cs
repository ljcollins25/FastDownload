// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using FastDownload.Shared;
using FastDownload.Upload;

namespace FastDownload.Utilities;

/// <summary>
/// Provides access to a file stream for reading chunks.
/// </summary>
internal class ChunkFileAccessor(Stream stream) : IAsyncDisposable
{
    public static ChunkFileAccessor Create(Stream stream, ChunkingScheme chunkingScheme)
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

    public static ChunkFileAccessor CreateRandomContent(long length)
    {
        return new RandomContentChunkFileAccessor(length);
    }

    public virtual async ValueTask ReadChunkAsync(long offset, Memory<byte> buffer, CancellationToken token)
    {
        stream.Seek(offset, SeekOrigin.Begin);

        await stream.ReadExactlyAsync(buffer, token);
    }

    public virtual long Length => stream.Length;

    public virtual double GetPercent(long offset) => ((int)((100000 * offset) / Length)) / 1000.0;

    public ValueTask DisposeAsync()
    {
        return stream.DisposeAsync();
    }
}

internal class RandomContentChunkFileAccessor(long length) : ChunkFileAccessor(Stream.Null)
{
    public Random random = new Random();

    public override ValueTask ReadChunkAsync(long offset, Memory<byte> buffer, CancellationToken token)
    {
        random.NextBytes(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override long Length => length;
}

/// <summary>
/// Provides access to a file stream for reading virtual chunks composed of sparse physical chunks.
/// </summary>
internal class SparseChunkFileAccessor(Stream stream, long blockSize, IReadOnlyList<ChunkMapping> chunks) : ChunkFileAccessor(stream)
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
