// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.IO.Compression;
using BuildXL.Cache.ContentStore.Hashing;

namespace FastDownload.Utilities;

internal record CompressionResult(ContentHash RawContentHash, ContentHash? CompressedContentHash, CompressionAlgorithm CompressionAlgorithm);

internal static class CompressionExtensions
{
    internal static HashingStream CreateWriteHashingStream(
        this HashType hashType,
        Stream readStream,
        long length)
    {
        var hasher = HashInfoLookup.GetContentHasher(hashType);
        return hasher.CreateWriteHashingStream(new StreamWithLength(readStream, length));
    }

    internal static async Task<CompressionResult> WriteCompressedAsync(
        this Stream writeStream,
        ReadOnlyMemory<byte> contents,
        HashType uncompressedHashType,
        HashType compressedHashType,
        CompressionAlgorithm algorithm,
        CompressionLevel level,
        CancellationToken cancellationToken)
    {
        ContentHash rawContentHash;
        ContentHash? compressedContentHash;

        IAsyncDisposable? createCompressionStream(Stream stream, out Stream compressionStream)
        {
            compressionStream = algorithm.CreateCompressionStream(stream, level, leaveOpen: true);

            // When compression algorithm is None, the stream is returned from CreateCompressionStream.
            // Return null to ensure stream isn't disposed by the calling using statement.
            return stream == compressionStream ? null : compressionStream;
        }

        HashingStream? createWriteHashingStream(HashType hashType, Stream stream)
        {
            return hashType == HashType.Unknown ? null : hashType.CreateWriteHashingStream(stream, contents.Length);
        }

        await using (var compressionHashingStream = createWriteHashingStream(compressedHashType, writeStream))
        {
            await using (createCompressionStream(compressionHashingStream ?? writeStream, out var compressionStream))
            await using (var rawHashingStream = uncompressedHashType.CreateWriteHashingStream(compressionStream, contents.Length))
            {
                await rawHashingStream.WriteAsync(contents, cancellationToken);

                // We don't flush here because the hashing stream will flush the compression stream
                // instead, which might have unintended consequences if it happens before we finish
                // writing the data.
                rawContentHash = await rawHashingStream.GetContentHashAsync();
            }

            compressedContentHash = await compressionHashingStream?.GetContentHashAsync();
        }

        return new(rawContentHash, compressedContentHash, algorithm);
    }
}