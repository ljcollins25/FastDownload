// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json;
using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Shared.Manifest;
using FastDownload.Shared.Manifest.V0;
using FastDownload.Utilities;

namespace FastDownload.Tests;

[TestClass]
public class SerializationTests
{
    [TestMethod]
    public void TestLocation()
    {
        var contentSize = 1234;

        var fileSize = contentSize + MagicSequence.FixedBinaryLength + Footer.FixedBinaryLength;

        var magicSequenceChunk = MagicSequence.Location(fileSize);
        magicSequenceChunk.Validate(fileSize);
        Assert.AreEqual(magicSequenceChunk.End, fileSize);
        Assert.AreEqual(magicSequenceChunk.Length, MagicSequence.FixedBinaryLength);

        var metadataChunk = Footer.Location(fileSize);
        metadataChunk.Validate(fileSize);
        Assert.AreEqual(magicSequenceChunk.Start, metadataChunk.End);
        Assert.AreEqual(metadataChunk.Length, Footer.FixedBinaryLength);
    }

    [TestMethod]
    public void TestManifestVersionSerialization()
    {
        // Serialize and deserialize each possible version and verify that works
        foreach (var version in Enum.GetValues<ManifestVersion>())
        {
            var buffer = new Span<byte>([255]);
            version.ToSpan(buffer);
            Assert.AreEqual(0, buffer[0]);
            var deserialized = ManifestVersionExtensions.FromSpan(buffer);
            Assert.AreEqual(version, deserialized);
        }

        // Trying to deserialize from an empty buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ManifestVersionExtensions.FromSpan(default(Span<byte>)));

        // Trying to deserialize from larger buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ManifestVersionExtensions.FromSpan(new Span<byte>([255, 255])));

        // Trying to deserialize a non-existing version should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ManifestVersionExtensions.FromSpan(new Span<byte>([255])));

        // Trying to serialize into an empty buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ManifestVersionExtensions.ToSpan(ManifestVersion.V0, default));

        // Trying to serialize into a larger buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => ManifestVersionExtensions.ToSpan(ManifestVersion.V0, new Span<byte>([255, 255])));
    }

    [TestMethod]
    public void TestSliceSerialization()
    {
        var buffer = new Span<byte>(new byte[Slice.FixedBinaryLength]);
        var instance = new Slice
        {
            Offset = 32,
            Length = 3123
        };

        instance.ToSpan(buffer);
        var deserialized = Slice.FromSpan(buffer);
        Assert.AreEqual(instance.Offset, deserialized.Offset);
        Assert.AreEqual(instance.Length, deserialized.Length);
        Assert.AreEqual(instance, deserialized);

        // Trying to deserialize from an empty buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Slice.FromSpan(default(Span<byte>)));

        // Trying to deserialize from smaller buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Slice.FromSpan(new Span<byte>(new byte[Slice.FixedBinaryLength - 1])));

        // Trying to deserialize from larger buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Slice.FromSpan(new Span<byte>(new byte[Slice.FixedBinaryLength + 1])));

        // Trying to serialize into an empty buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => instance.ToSpan(default));

        // Trying to serialize into a smaller buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => instance.ToSpan(new Span<byte>(new byte[Slice.FixedBinaryLength - 1])));

        // Trying to serialize into a larger buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => instance.ToSpan(new Span<byte>(new byte[Slice.FixedBinaryLength + 1])));
    }

    [TestMethod]
    public void TestMetadataSerialization()
    {
        var instance = new Footer
        {
            Version = ManifestVersion.V0,
            Slice = new Slice
            {
                Offset = 0,
                Length = 16
            },
        };

        var buffer = new Span<byte>(new byte[Footer.FixedBinaryLength]);
        Footer.ToSpan(instance, buffer);
        var deserialized = Footer.FromSpan(buffer);
        Assert.AreEqual(instance, deserialized);

        // Trying to deserialize from an empty buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Footer.FromSpan(default(Span<byte>)));

        // Trying to deserialize from smaller buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Footer.FromSpan(new Span<byte>(new byte[Footer.FixedBinaryLength - 1])));

        // Trying to deserialize from larger buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Footer.FromSpan(new Span<byte>(new byte[Footer.FixedBinaryLength + 1])));

        // Trying to serialize into an empty buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Footer.ToSpan(instance, default));

        // Trying to serialize into a smaller buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Footer.ToSpan(instance, new Span<byte>(new byte[Footer.FixedBinaryLength - 1])));

        // Trying to serialize into a larger buffer should throw
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => Footer.ToSpan(instance, new Span<byte>(new byte[Footer.FixedBinaryLength + 1])));
    }

    [TestMethod]
    public void TestMagicNumber()
    {
        // Ensure the magic number is valid
        Assert.IsTrue(MagicSequence.Matches(MagicSequence.Bytes.Span));

        // Ensure an empty buffer is invalid
        Assert.IsFalse(MagicSequence.Matches(new byte[MagicSequence.FixedBinaryLength]));

        // Copy the magic number and change the first byte, ensure it's invalid
        var buffer = new byte[MagicSequence.FixedBinaryLength];
        MagicSequence.Bytes.Span.Slice(1).CopyTo(buffer);
        Assert.IsFalse(MagicSequence.Matches(buffer));
    }

    [TestMethod]
    public void TestManifestV0Serialization()
    {
        var contentHasher = HashInfoLookup.GetContentHasher(HashType.SHA256);

        List<Block> blocks = [
                new Block
                {
                    Compression = CompressionAlgorithm.GZip,
                    RawHash = ContentHash.Random(),
                    CompressedHash = null,
                    RawSlice = new Slice
                    {
                        Offset = 0,
                        Length = 16
                    },
                    CompressedSlice = new Slice
                    {
                        Offset = 0,
                        Length = 16
                    },
                },
            ];

        var manifest = new Manifest
        {
            Producer = null,
            CorrelationId = "correlation-id",
            RawSize = 16,
            RawBlockSize = 16,
            Hash = blocks.ComputeBlockHash(contentHasher),
            Blocks = blocks
        };

        // Serialize and deserialize the manifest using JSON
        var json = JsonSerializer.Serialize(manifest);
        var deserialized = JsonSerializer.Deserialize<Manifest>(json);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual(manifest.RawSize, deserialized.RawSize);
        Assert.AreEqual(manifest.Blocks.Count, deserialized.Blocks.Count);
        Assert.AreEqual(manifest.Hash, deserialized.Hash);
        foreach (var (block, deserializedBlock) in manifest.Blocks.Zip(deserialized.Blocks))
        {
            Assert.AreEqual(block.Compression, deserializedBlock.Compression);
            Assert.AreEqual(block.RawHash, deserializedBlock.RawHash);
            Assert.AreEqual(block.CompressedSlice, deserializedBlock.CompressedSlice);
            Assert.AreEqual(block.RawSlice, deserializedBlock.RawSlice);
        }
    }
}
