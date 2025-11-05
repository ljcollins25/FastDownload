// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Utilities;

namespace FastDownload.Shared.Manifest
{
    internal static class ManifestExtensions
    {
        public static ContentHash ComputeBlockHash(this IEnumerable<V0.Block> blocks, IContentHasher contentHasher)
        {
            var hasher = new MerkleHasher();

            hasher.AddMany(
                blocks
                    .Select((block, index) => new MerkleHasher.Block(index, block.RawHash, block.RawSlice, block.Compression)));

            return hasher.Compute(contentHasher);
        }
    }
}
