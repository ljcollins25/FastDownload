// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Utilities.Core.Tasks;
using FastDownload.Shared;
using FastDownload.Utilities;

namespace FastDownload.Download;

/// <summary>
/// Represents a reference to bytes for a chunk
/// </summary>
public interface IChunkDataHandle : IDisposable
{
    public ReadOnlyMemory<byte> Bytes { get; }

    public Stream Stream { get; }

    public CompressionAlgorithm Encoding { get; }

    int ByteLength { get; }
}

/// <summary>
/// An entry for a chunk which track availability of data and file write completion
/// </summary>
internal interface IChunkEntry
{
    ValueTask<IChunkDataHandle?> TryGetChunkDataAsync(CancellationToken token);

    Chunk DestinationChunk { get; }
}

internal class ChunkHost : IDisposable
{
    private ChunkDataHandle[] _bufferChunks;
    private int _bufferReleaseChunkCursor = 0;
    private int _bufferChunkCursor = -1;
    private int _isDisposed = 0;

    private long _currentBufferSize = 0;

    public long MaxBufferSize { get; }

    public int BufferCount => _bufferChunks.Length;

    public string FilePath { get; }
    public long FileSize { get; }
    public uint ChunkSize { get; }
    public required ProxyNodeEntry MachineInfo { get; init; }

    public AzureBlobSemaphore? ProxySemaphore { get; init; }

    public Statistics? Statistics { get; init; }

    public GlobalState? GlobalState { get; init; }

    protected ChunkEntry[] Entries { get; }

    public ChunkHost(string filePath, long fileSize, IEnumerable<WorkItem> workItems, uint chunkSize, long maxBufferSize)
    {
        FilePath = filePath;
        MaxBufferSize = maxBufferSize;
        FileSize = fileSize;
        ChunkSize = chunkSize;
        Entries = workItems.OrderBy(w => w.ChunkIndex).Select(w => new ChunkEntry(this, w)).ToArray();
        _bufferChunks = new ChunkDataHandle[Entries.Length * 2];
    }

    public void PutBuffer(WorkItem work, AlignedManagedBuffer buffer, CompressionAlgorithm encoding = CompressionAlgorithm.None, bool isSourceChunk = false)
    {
        var chunk = isSourceChunk ? work.Source : work.Destination;
        var chunkData = new ChunkDataHandle(buffer.RefCountHandle!, (int)chunk.Length, encoding, (uint)buffer.Memory.Length);
        var entry = Entries[work.ChunkIndex];
        entry.CompressedData = chunkData;

        if (isSourceChunk && _bufferChunks.Length > 0 && chunkData.TryReference())
        {
            Statistics?.Increment(Counters.CacheableChunks);

            var bufferIndex = Interlocked.Increment(ref _bufferChunkCursor);
            Interlocked.Add(ref _currentBufferSize, chunkData.AllocatedSize);
            Statistics?.Increment(Counters.CurrentBufferCacheSize, chunkData.AllocatedSize);
            _bufferChunks[bufferIndex] = chunkData;

            while (_currentBufferSize > MaxBufferSize)
            {
                var releaseCursor = Math.Min(_bufferReleaseChunkCursor, bufferIndex);

                var releasedHandle = Interlocked.Exchange(ref _bufferChunks[releaseCursor]!, null);
                if (releasedHandle != null)
                {
                    Interlocked.Add(ref _currentBufferSize, -releasedHandle.AllocatedSize);
                    Interlocked.Increment(ref _bufferReleaseChunkCursor);
                    Statistics?.Increment(Counters.ReleasedCacheableChunks);
                    Statistics?.Increment(Counters.CurrentBufferCacheSize, -releasedHandle.AllocatedSize);
                    releasedHandle.Dispose();
                }

                if (releaseCursor == bufferIndex)
                {
                    break;
                }
            }
        }
        else
        {
            Statistics?.Increment(Counters.UncacheableChunks);
        }

        entry.ActiveHandleSet.TrySetResult(true);
    }

    public IChunkEntry GetChunkEntry(int chunkIndex)
    {
        return Entries[chunkIndex];
    }

    public void CancelChunk(WorkItem work)
    {
        var entry = Entries[work.ChunkIndex];
        entry.ActiveHandleSet.TrySetResult(false);
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _isDisposed, 1, 0) != 0)
        {
            return;
        }

        foreach (var entry in Entries)
        {
            entry.ActiveHandleSet.TrySetCanceled();
        }

        for (int i = 0; i < _bufferChunks.Length; i++)
        {
            Interlocked.Exchange(ref _bufferChunks[i]!, null)?.Dispose();
        }
    }

    protected record ChunkEntry(ChunkHost Host, WorkItem WorkItem) : IChunkEntry
    {
        public TaskSourceSlim<bool> ActiveHandleSet = TaskSourceSlim.Create<bool>();
        public ChunkDataHandle? CompressedData;

        public Chunk DestinationChunk => WorkItem.Destination;

        public async ValueTask<IChunkDataHandle?> TryGetChunkDataAsync(CancellationToken token)
        {
            if (!await ActiveHandleSet.Task.WithCancellationAsync(token))
            {
                return null;
            }

            return TryReferenceAndGetData();
        }

        public ChunkDataHandle? TryReferenceAndGetData()
        {
            return CompressedData?.TryReferenceAndGet();
        }
    }

    protected record ChunkDataHandle(RefCountHandle<AlignedManagedBuffer> BufferHandle, int ByteLength, CompressionAlgorithm Encoding, uint AllocatedSize)
        : IChunkDataHandle
    {
        public ReadOnlyMemory<byte> Bytes { get; } = BufferHandle.Value!.ReadOnlyMemory.Slice(0, ByteLength);

        public Stream Stream => BufferHandle.Value!.CreateMemoryStream(writable: false, length: ByteLength);

        public ChunkDataHandle? TryReferenceAndGet()
        {
            return TryReference() ? this : null;
        }

        public bool TryReference()
        {
            return BufferHandle.TryReference();
        }

        public void Dispose()
        {
            BufferHandle.Dispose();
        }
    }
}
