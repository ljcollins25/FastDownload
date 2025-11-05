// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.IO.Compression;
using K4os.Compression.LZ4;
using K4os.Compression.LZ4.Streams;

namespace FastDownload.Utilities
{
    /// <summary>
    /// Represents the compression options for content.
    /// </summary>
    public enum CompressionAlgorithm
    {
        /// <summary>
        /// No compression.
        /// </summary>
        None,

        /// <summary>
        /// GZip compression.
        /// </summary>
        GZip,

        /// <summary>
        /// Deflate compression.
        /// </summary>
        Deflate,

        /// <summary>
        /// Brotli compression.
        /// </summary>
        Brotli,

        /// <summary>
        /// LZ4 compression
        /// </summary>
        Lz4,

        /// <summary>
        /// Zstd compression
        /// </summary>
        Zstd
    }

    public static class CompressionAlgorithmExtensions
    {
        public static Stream CreateCompressionStream(this CompressionAlgorithm compression, Stream stream, CompressionLevel compressionLevel, bool leaveOpen)
        {
            return compression switch
            {
                CompressionAlgorithm.None => stream,
                CompressionAlgorithm.GZip => new GZipStream(stream, compressionLevel, leaveOpen),
                CompressionAlgorithm.Deflate => new DeflateStream(stream, compressionLevel, leaveOpen),
                CompressionAlgorithm.Brotli => new BrotliStream(stream, compressionLevel, leaveOpen),
                CompressionAlgorithm.Lz4 => LZ4Stream.Encode(stream, AsLZ4Level(compressionLevel), leaveOpen: leaveOpen),
                CompressionAlgorithm.Zstd => new ZstdSharp.CompressionStream(stream, AsZstdLevel(compressionLevel), leaveOpen: leaveOpen),
                _ => throw new ArgumentOutOfRangeException(nameof(compression)),
            };
        }

        private static LZ4Level AsLZ4Level(CompressionLevel compressionLevel)
        {
            return compressionLevel switch
            {
                CompressionLevel.NoCompression => throw new ArgumentOutOfRangeException(nameof(compressionLevel)),
                CompressionLevel.Fastest => LZ4Level.L00_FAST,  // Fast compression
                CompressionLevel.Optimal => LZ4Level.L07_HC,
                CompressionLevel.SmallestSize => LZ4Level.L12_MAX, // Higher compression
                _ => LZ4Level.L07_HC
            };
        }

        private static int AsZstdLevel(CompressionLevel compressionLevel)
        {
            return compressionLevel switch
            {
                CompressionLevel.NoCompression => throw new ArgumentOutOfRangeException(nameof(compressionLevel)),
                CompressionLevel.Fastest => -7,  // Fast compression
                CompressionLevel.Optimal => 9,
                CompressionLevel.SmallestSize => ZstdSharp.Compressor.MaxCompressionLevel,  // Highest compression
                _ => ZstdSharp.Compressor.DefaultCompressionLevel
            };
        }

        public static Stream CreateDecompressionStream(this CompressionAlgorithm compression, Stream stream, bool leaveOpen)
        {
            return compression switch
            {
                CompressionAlgorithm.None => stream,
                CompressionAlgorithm.GZip => new GZipStream(stream, CompressionMode.Decompress, leaveOpen),
                CompressionAlgorithm.Deflate => new DeflateStream(stream, CompressionMode.Decompress, leaveOpen),
                CompressionAlgorithm.Brotli => new BrotliStream(stream, CompressionMode.Decompress, leaveOpen),
                CompressionAlgorithm.Lz4 => LZ4Stream.Decode(stream, leaveOpen: leaveOpen),
                CompressionAlgorithm.Zstd => new ZstdSharp.DecompressionStream(stream, leaveOpen: leaveOpen),
                _ => throw new ArgumentOutOfRangeException(nameof(compression)),
            };
        }
    }
}
