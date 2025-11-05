// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text;
using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Download;
using FastDownload.Shared;

namespace FastDownload.Utilities
{
    internal class MerkleHasher
    {
        public readonly record struct Block(int Id, ContentHash ContentHash, Chunk Destination, CompressionAlgorithm Compression)
        {
            public static implicit operator Block(WriteItem writeItem)
            {
                return writeItem.Work;
            }

            public static implicit operator Block(WorkItem work)
            {
                return new Block(work.ChunkIndex, work.RawHash!.Value, work.Destination, work.Compression);
            }
        }

        private readonly PriorityQueue<Block, int> _queue = new();

        public void AddMany(IEnumerable<Block> items)
        {
            lock (_queue)
            {
                foreach (var item in items)
                {
                    _queue.Enqueue(item, item.Id);
                }
            }
        }

        public ContentHash Compute(IContentHasher hasher)
        {
            lock (_queue)
            {
                using var hashingStream = hasher.CreateWriteHashingStream(new StreamWithLength(Stream.Null, 0), parallelHashingFileSizeBoundary: -1);
                using var binaryWriter = new BinaryWriter(hashingStream, encoding: Encoding.UTF8, leaveOpen: false);
                var expectedId = 0;

                binaryWriter.Write(_queue.Count);
                while (_queue.Count > 0)
                {
                    var next = _queue.Dequeue();

                    // When this algorithm runs, we expect to have a contiguous sequence of blocks starting from 0 all
                    // the way to the last block. If we find a block with an ID that's not the expected one, it means
                    // there's a gap in the input, which should never happen.
                    if (next.Id != expectedId)
                    {
                        throw new InvalidOperationException($"Expected block with ID {expectedId}, got {next.Id}. This means there's a missing block in the input.");
                    }
                    else
                    {
                        expectedId++;
                    }

                    binaryWriter.Write(next.Id);
                    next.ContentHash.Serialize(binaryWriter);
                    binaryWriter.Write(next.Destination.Start);
                    binaryWriter.Write(next.Destination.Length);
                    binaryWriter.Write((byte)next.Compression);
                }

                binaryWriter.Flush();
                return hashingStream.GetContentHash();
            }
        }
    }
}
