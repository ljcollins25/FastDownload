// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json.Serialization;
using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Utilities;

namespace FastDownload.Shared.Manifest.V0
{
    internal sealed record Block
    {
        /// <summary>
        /// Algorithm used to compress the block.
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter<CompressionAlgorithm>))]
        public required CompressionAlgorithm Compression { get; init; }

        /// <summary>
        /// Hash of the decompressed block. Used to verify that the block was downloaded and decompressed correctly.
        /// </summary>
        [JsonConverter(typeof(ContentHashJsonConverter))]
        public required ContentHash RawHash { get; init; }

        /// <summary>
        /// Hash of the decompressed block. Used to verify that the block was downloaded and decompressed correctly.
        /// </summary>
        [JsonConverter(typeof(ContentHashJsonConverter))]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public ContentHash? CompressedHash { get; init; }

        /// <summary>
        /// Slice of the output file where the decompressed block should be written.
        /// </summary>
        public required Slice RawSlice { get; init; }

        /// <summary>
        /// Slice of the file where the compressed block is located.
        /// </summary>
        public required Slice CompressedSlice { get; init; }

        /// <summary>
        /// Sparse regions which are mapped into the current block as contiguous segments
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public Slice[]? SparseRegions { get; init; }
    }
}
