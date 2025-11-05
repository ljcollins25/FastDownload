// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using FastDownload.Download;
using FastDownload.Shared.Manifest.V0;
using FastDownload.Utilities;
using NLog;

namespace FastDownload.Upload
{
    /// <summary>
    /// Verifies the uploading chunks against the provided manifest to ensure the hashes match
    /// </summary>
    internal class VerifyingUploadMechanism : IUploadMechanism, IAsyncDisposable
    {
        private static readonly Logger Logger = LogManager.GetLogger(nameof(VerifyingUploadMechanism));
        private readonly Manifest _manifest;
        private readonly IUploadMechanism _inner;
        private readonly Dictionary<long, Block> _blocksByRawOffset;

        private VerifyingUploadMechanism(Manifest manifest, IUploadMechanism inner)
        {
            _manifest = manifest;
            _inner = inner;
            _blocksByRawOffset = _manifest.Blocks.ToDictionary(b => b.RawSlice.Offset);
        }

        public static async ValueTask<VerifyingUploadMechanism> CreateAsync(IUploadMechanism inner, Uri uri, CancellationToken token)
        {
            Manifest manifest;
            if (uri.IsFile)
            {
                using var fs = File.OpenRead(uri.LocalPath);
                manifest = await Manifest.ReadAsync(fs, token);
            }
            else
            {
                var httpClientSettings = await HttpClientSettings.CreateAsync(uri, Logger, token);

                var (_, _, fetchedManifest) = await RemoteFileMetadata.FetchAsync(uri, httpClientSettings, token);
                if (fetchedManifest != null)
                {
                    Logger.ForErrorEvent()
                        .Message(
                            "Failed to find verification manifest at {Uri}.",
                            uri.Scrub().ToString())
                        .Log();
                }

                Contract.Assert(fetchedManifest != null);
                manifest = fetchedManifest!;
            }

            return new VerifyingUploadMechanism(manifest, inner);
        }

        public Task DeleteAsync(CancellationToken cancellationToken) => _inner.DeleteAsync(cancellationToken);
        public Task CommitAsync(IEnumerable<BlockInfo> blockIds, CancellationToken cancellationToken) => _inner.CommitAsync(blockIds, cancellationToken);

        public Task<BlockInfo> UploadBlockAsync(BlockUploadData? blockData, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            if (blockData is { } bd)
            {
                // This is a real chunk of file. Check against block from manifest
                var verificationBlock = _blocksByRawOffset[bd.RawBoundary.Start];
                var computedBlock = bd.CompressionResult;
                Contract.Assert(verificationBlock.RawHash == computedBlock.RawContentHash, $"{verificationBlock.RawHash} != {computedBlock.RawContentHash} [{bd.RawBoundary}]");
                if (verificationBlock.CompressedHash?.HashType == computedBlock.CompressedContentHash?.HashType)
                {
                    Contract.Assert(verificationBlock.CompressedHash == computedBlock.CompressedContentHash, $"{verificationBlock.CompressedHash} != {computedBlock.CompressedContentHash} [{bd.RawBoundary}]");
                }
            }

            return _inner.UploadBlockAsync(blockData, content, cancellationToken);
        }
    }

}
