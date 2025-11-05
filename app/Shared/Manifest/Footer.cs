// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;

namespace FastDownload.Shared.Manifest
{
    /// <summary>
    /// Represents the metadata of a manifest. This is the first thing that's read from a file that will be processed.
    /// </summary>
    internal sealed record Footer
    {
        public static readonly int FixedBinaryLength = Slice.FixedBinaryLength + ManifestVersionExtensions.FixedBinaryLength;

        /// <summary>
        /// Version of the manifest.
        /// </summary>
        public required ManifestVersion Version { get; init; }

        /// <summary>
        /// Slice of the file where the manifest is located.
        /// </summary>
        public required Slice Slice { get; init; }

        public static Footer Create(V0.Manifest manifest, Slice slice)
        {
            return new Footer
            {
                Version = manifest.Version,
                Slice = slice,
            };
        }

        public static Footer FromSpan(ReadOnlySpan<byte> data)
        {
            if (data.Length != FixedBinaryLength)
            {
                throw new ArgumentOutOfRangeException(nameof(data), $"Invalid {nameof(Footer)} size. Found {data.Length} when {FixedBinaryLength} was expected");
            }

            var version = ManifestVersionExtensions.FromSpan(data.Slice(0, ManifestVersionExtensions.FixedBinaryLength));
            var slice = Slice.FromSpan(data.Slice(ManifestVersionExtensions.FixedBinaryLength, Slice.FixedBinaryLength));

            return new Footer { Version = version, Slice = slice };
        }

        public static void ToSpan(Footer footer, Span<byte> data)
        {
            if (data.Length != FixedBinaryLength)
            {
                throw new ArgumentOutOfRangeException(nameof(data), $"Invalid {nameof(Footer)} size. Found {data.Length} when {FixedBinaryLength} was expected");
            }

            footer.Version.ToSpan(data.Slice(0, ManifestVersionExtensions.FixedBinaryLength));
            footer.Slice.ToSpan(data.Slice(ManifestVersionExtensions.FixedBinaryLength, Slice.FixedBinaryLength));
        }

        public async Task<V0.Manifest> ReadManifestAsync(Stream stream, CancellationToken cancellationToken)
        {
            switch (Version)
            {
                case ManifestVersion.V0:
                    return await V0.Manifest.ReadAsync(stream, cancellationToken);
                default:
                    throw new InvalidDataException($"Found unknown {nameof(ManifestVersion)} {Version} inside of {nameof(Footer)}");
            }
        }

        public async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
        {
            var buffer = new byte[FixedBinaryLength];
            ToSpan(this, buffer);
            await stream.WriteAsync(buffer, cancellationToken);
        }

        public static Chunk Location(long fileSizeBytes)
        {
            var magicSequence = MagicSequence.Location(fileSizeBytes);
            var chunk = new Chunk(magicSequence.Start - FixedBinaryLength, magicSequence.Start);
            Contract.Assert(chunk.Length == FixedBinaryLength);
            return chunk;
        }

        public void Validate(long fileSizeBytes)
        {
            if (Slice.Offset >= fileSizeBytes)
            {
                throw new InvalidDataException("Invalid offset");
            }

            if (Slice.Length > fileSizeBytes - Slice.Offset)
            {
                throw new InvalidDataException("Invalid length");
            }
        }
    }
}
