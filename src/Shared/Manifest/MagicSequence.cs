// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Buffers.Binary;
using System.Diagnostics.ContractsLight;
using System.Security.Cryptography;
using System.Text;
using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Utilities;

namespace FastDownload.Shared.Manifest
{
    /// <summary>
    /// Represents the signature of a manifest file. Used to verify that a file is correctly formatted before
    /// attempting to read it.
    ///
    /// The signature looks like [RandomBytes]ProductName, and it's placed at the very end of the file.
    ///
    /// The string at the end of the file is intended to be a human-readable string that can be used to quickly
    /// identify a file as being produced by the tool using common tools like cat, etc.
    ///
    /// The random bytes are used to make it very unlikely for a random file to match the magic sequence.
    /// </summary>
    internal static class MagicSequence
    {
        private static byte[] Signature = Encoding.UTF8.GetBytes(Globals.ProductName);
        private static int RandomSequenceLength = 16;
        private static int MagicSequenceLength = RandomSequenceLength + Signature.Length;

        /// <summary>
        /// The magic sequence.
        /// </summary>
        public static ReadOnlyMemory<byte> Bytes { get; } = GenerateMagicSequence();

        /// <summary>
        /// Length of the magic sequence.
        /// </summary>
        public static int FixedBinaryLength => Bytes.Length;

        /// <summary>
        /// Checks if the given span matches the magic sequence.
        /// </summary>
        public static bool Matches(ReadOnlySpan<byte> span)
        {
            return span.Length == Bytes.Length && span.SequenceEqual(Bytes.Span);
        }

        /// <summary>
        /// Generates the magic sequence.
        /// </summary>
        private static byte[] GenerateMagicSequence()
        {
            var sequence = new byte[MagicSequenceLength];

            using (var hmac = new HMACSHA512(Signature))
            {
                var temporary = new byte[sizeof(int)];

                for (var filled = 0; filled < MagicSequenceLength;)
                {
                    BinaryPrimitives.WriteInt32LittleEndian(temporary, filled);
                    var hash = hmac.ComputeHash(temporary);

                    var length = Math.Min(MagicSequenceLength - filled, hash.Length);
                    var source = hash.AsSpan().Slice(0, length);
                    var destination = sequence.AsSpan().Slice(filled, length);
                    source.CopyTo(destination);

                    filled += length;
                }
            }

            // Overwrite the end of the sequence with the Signature
            Signature.CopyTo(sequence, MagicSequenceLength - Signature.Length);

            return sequence;
        }

        public static Chunk Location(long fileSizeBytes)
        {
            var chunk = new Chunk(fileSizeBytes - FixedBinaryLength, fileSizeBytes);
            Contract.Assert(chunk.Length == FixedBinaryLength);
            return chunk;
        }
    }
}
