// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json;
using System.Text.Json.Serialization;
using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Utilities;

namespace FastDownload.Shared.Manifest.V0
{
    /// <summary>
    /// Manifest version 0.
    /// </summary>
    /// <remarks>
    /// The manifest is JSON serialized into output files, so all underlying types MUST be JSON compatible and be added
    /// to <see cref="SourceGenerationContext"/>.
    /// </remarks>
    internal sealed record Manifest
    {
        [JsonIgnore]
        public ManifestVersion Version { get; } = ManifestVersion.V0;

        /// <summary>
        /// Information about the producer of the manifest.
        /// </summary>
        public required VersionInfo? Producer { get; init; }

        /// <summary>
        /// The correlation ID of the run that produced this manifest.
        /// </summary>
        public required string? CorrelationId { get; init; }

        /// <summary>
        /// The absolute file size in bytes.
        /// </summary>
        public required long RawSize { get; init; }

        /// <summary>
        /// The size of the decompressed blocks.
        /// </summary>
        public required long RawBlockSize { get; init; }

        /// <summary>
        /// The compressed blocks that make up the file.
        /// </summary>
        public required List<Block> Blocks { get; init; }

        /// <summary>
        /// The hash to use to verify the <see cref="Manifest"/>'s <see cref="Blocks"/> match.
        /// </summary>
        [JsonConverter(typeof(ContentHashJsonConverter))]
        public required ContentHash Hash { get; init; }

        /// <summary>
        /// Gets whether the file was uploaded sparse-aware
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public bool IsSparse { get; init; }

        /// <summary>
        /// The total number of written bytes if the file is sparse.
        /// </summary>
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
        public long? TotalSparseWrittenBytes { get; init; }

        /// <summary>
        /// Write to stream in JSON format.
        /// </summary>
        public async Task WriteAsync(Stream stream, CancellationToken cancellationToken)
        {
            await JsonSerializer.SerializeAsync(stream, this, SourceGenerationContext.Default.Manifest, cancellationToken);
        }

        public string ToJsonString()
        {
            return JsonSerializer.Serialize(this, SourceGenerationContext.Default.Manifest);
        }

        /// <summary>
        /// Read from stream in JSON format.
        /// </summary>
        public static async Task<Manifest> ReadAsync(Stream stream, CancellationToken cancellationToken)
        {
            var manifest = await JsonSerializer.DeserializeAsync(stream, SourceGenerationContext.Default.Manifest, cancellationToken);
            if (manifest is null)
            {
                throw new InvalidDataException($"Failed to read {nameof(Manifest)} with version {ManifestVersion.V0}");
            }

            return manifest;
        }

        public async Task WriteFooterAsync(Stream stream, long manifestOffset, CancellationToken cancellationToken)
        {
            var startPosition = stream.Position;
            await WriteAsync(stream, cancellationToken);
            var endPosition = stream.Position;

            var metadata = Footer.Create(this, new Slice() { Offset = manifestOffset, Length = endPosition - startPosition });
            await metadata.WriteAsync(stream, cancellationToken);

            await stream.WriteAsync(MagicSequence.Bytes, cancellationToken);
        }
    }
}
