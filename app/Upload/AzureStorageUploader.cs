// Copyright (C) Microsoft Corporation. All Rights Reserved.

using Azure.Storage;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using FastDownload.Utilities;

namespace FastDownload.Upload
{
    internal class AzureStorageUploader : IUploadMechanism
    {
        private readonly BlockBlobClient _blockClient;

        private AzureStorageUploader(BlockBlobClient blockClient)
        {
            _blockClient = blockClient;
        }

        public static async Task<AzureStorageUploader> CreateAsync(BlockBlobClient blockClient, bool overwrite, CancellationToken cancellationToken)
        {
            var uploader = new AzureStorageUploader(blockClient);

            if (overwrite)
            {
                await uploader.DeleteAsync(cancellationToken);
            }
            else if (await uploader.ExistsAsync(cancellationToken))
            {
                throw new InvalidOperationException("Blob already exists");
            }

            return uploader;
        }

        private async Task<bool> ExistsAsync(CancellationToken cancellationToken)
        {
            return await _blockClient.ExistsAsync(cancellationToken: cancellationToken);
        }

        public async Task DeleteAsync(CancellationToken cancellationToken)
        {
            await _blockClient.DeleteIfExistsAsync(cancellationToken: cancellationToken);
        }

        public async Task<BlockInfo> UploadBlockAsync(BlockUploadData? blockData, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            var blockInfo = BlockInfo.Random(offset: null, length: -1);

            using var stream = new ReadOnlyMemoryStreamAdapter(content);
            await _blockClient.StageBlockAsync(
                blockInfo.Id,
                stream,
                options: new BlockBlobStageBlockOptions()
                {
                    TransferValidation = new UploadTransferValidationOptions()
                    {
                        ChecksumAlgorithm = StorageChecksumAlgorithm.None,
                    },
                },
                cancellationToken: cancellationToken);

            return blockInfo with { Length = content.Length };
        }

        public async Task CommitAsync(IEnumerable<BlockInfo> blockIds, CancellationToken cancellationToken)
        {
            var commitOrdereredIds = blockIds.ToList();

            var ids = commitOrdereredIds.ToDictionary(o => o.Id, o => o);
            var blocks = await _blockClient.GetBlockListAsync(BlockListTypes.All, cancellationToken: cancellationToken);

            if (blocks.Value.CommittedBlocks.Any())
            {
                throw new InvalidOperationException("Block list should have been empty, but found at least one block");
            }

            var committable = new HashSet<string>(capacity: ids.Count);
            foreach (var block in blocks.Value.UncommittedBlocks)
            {
                if (!ids.TryGetValue(block.Name, out var blockInfo))
                {
                    throw new InvalidOperationException($"Block {blockInfo} is committed, but not found in the list of blocks to commit");
                }

                if (blockInfo.Length != block.SizeLong)
                {
                    throw new InvalidOperationException($"Block {blockInfo} has a different size than expected {block.Size}");
                }

                ids.Remove(block.Name);
                committable.Add(block.Name);
            }

            if (ids.Count > 0)
            {
                throw new InvalidOperationException($"Block list mismatch: {ids.Count} blocks exist in the list of blocks to commit, but haven't been uploaded");
            }

            // It's very important to keep the order of the blocks as they are passed into us as the caller has
            // potentially made assumptions as to where the data ends up in the final file.
            var commit = commitOrdereredIds
                .Where(id => committable.Contains(id.Id))
                .Select(id => id.Id)
                .ToList();

            await _blockClient.CommitBlockListAsync(
                commit,
                options: new CommitBlockListOptions()
                {
                },
                cancellationToken: cancellationToken);
        }
    }
}
