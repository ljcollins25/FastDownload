// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Buffers.Binary;

namespace FastDownload.Shared.Manifest
{
    /// <summary>
    /// Represents a slice of a file
    /// </summary>
    /// <remarks>
    /// The <see cref="Slice"/> differs from a <see cref="Chunk"/> in that it's used as part of the serialization and
    /// deserialization logic. Any changes to this type MUST remain backwards compatible.
    /// </remarks>
    internal sealed record Slice : IComparable<Slice>
    {
        public static readonly int FixedBinaryLength = 16;

        /// <summary>
        /// Offset of the manifest in bytes
        /// </summary>
        public required long Offset { get; init; }

        /// <summary>
        /// Size of the manifest in bytes
        /// </summary>
        public required long Length { get; init; }

        public static Slice FromSpan(ReadOnlySpan<byte> data)
        {
            if (data.Length != FixedBinaryLength)
            {
                throw new ArgumentOutOfRangeException(nameof(data), "Invalid slice size");
            }

            var offset = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(0, 8));
            var length = BinaryPrimitives.ReadInt64LittleEndian(data.Slice(8, 8));

            return new Slice { Offset = offset, Length = length };
        }

        public void ToSpan(Span<byte> data)
        {
            if (data.Length != FixedBinaryLength)
            {
                throw new ArgumentOutOfRangeException(nameof(data), "Invalid slice size");
            }

            BinaryPrimitives.WriteInt64LittleEndian(data.Slice(0, 8), Offset);
            BinaryPrimitives.WriteInt64LittleEndian(data.Slice(8, 8), Length);
        }

        public int CompareTo(Slice? other)
        {
            if (other is null)
            {
                return 1;
            }

            var offsetComparison = Offset.CompareTo(other.Offset);
            if (offsetComparison != 0)
            {
                return offsetComparison;
            }

            return Length.CompareTo(other.Length);
        }

        public Chunk ToChunk() => new Chunk(Offset, Offset + Length);

        public static implicit operator Chunk(Slice slice)
        {
            return slice.ToChunk();
        }

        public static implicit operator Slice(Chunk chunk)
        {
            return new Slice
            {
                Offset = chunk.Start,
                Length = chunk.Length,
            };
        }
    }
}
