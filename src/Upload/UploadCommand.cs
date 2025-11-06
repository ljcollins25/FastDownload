// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Buffers;
using System.Diagnostics.ContractsLight;
using Azure.Core;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Specialized;
using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core.Tasks;
using FastDownload.Shared;
using FastDownload.Shared.Manifest;
using FastDownload.Shared.Manifest.V0;
using FastDownload.Utilities;
using NLog;

namespace FastDownload.Upload;

internal static class UploadCommand
{
    private static readonly Logger Logger = LogManager.GetLogger(nameof(UploadCommand));

    private record Partial(ChunkMapping RawMapping, BlockInfo BlockInfo, CompressionResult CompressionResult)
        : BlockUploadData(RawMapping, CompressionResult)
    {
        public long Offset { get; set; } = long.MaxValue;
    }

    internal static async Task ExecuteAsync(UploadArguments arguments, Statistics statistics, CancellationTokenSource internalCancellationSource, CancellationToken cancellationToken)
    {
        // Just to use it and get rid of errors
        internalCancellationSource.Token.ThrowIfCancellationRequested();

        if (arguments.Raw)
        {
            arguments.CompressionLevel = System.IO.Compression.CompressionLevel.NoCompression;
            arguments.SparseHandling = SparseHandlingMode.None;
        }

        if (arguments.CheckOnly)
        {
            arguments.CheckManifestUri = arguments.Uri;

            // Overload target uri to write to null.
            arguments.Uri = new Uri("null:");
        }

        // Fetch upload configuration (where are we uploading to?)
        // Report progress
        // Handle cancellation

        ChunkingScheme chunking;
        IUploadContentProvider contentProvider;
        if (arguments.RandomFileSize is long randomFileSize)
        {
            chunking = ChunkingScheme.Create(fileSize: randomFileSize, alignment: (uint)Environment.SystemPageSize, blockSize: arguments.BlockSize);

            contentProvider = new NullUploadContentProvider(arguments.Path);
        }
        else
        {
            var fileInfo = new FileInfo(arguments.Path);
            if (!fileInfo.Exists)
            {
                throw new FileNotFoundException($"File '{arguments.Path}' does not exist.");
            }
            using (var fileStream = fileInfo.OpenRead())
            {
                chunking = ChunkingScheme.FromFileStream(arguments.SparseHandling, fileStream, arguments.BlockSize);

                // This is given by the Azure Storage Blob service. They don't allow more than 50,000 blocks in a block blob.
                if (chunking.Chunks.Count > 50_000)
                {
                    throw new InvalidOperationException($"The file is too large to upload. The maximum number of blocks is 50,000. The file has {chunking.Chunks.Count} blocks.");
                }
            }

            contentProvider = new FileUploadContentProvider(fileInfo);
        }

        Logger.ForInfoEvent()
            .Message(
                "Uploading {FileName} to {Uri} with block size {BlockSize} and compression {Compression}. Raw file size: {RawFileSize}, Size on disk: {OccupiedBytes}, NumChunks: {NumChunks}, NumDataRegions: {NumRegions}, SparseWrittenBytes: {SparseWrittenBytes}, SparseDownloadBytes: {SparseDownloadBytes}",
                contentProvider.FullName,
                arguments.Uri.Scrub().ToString(),
                arguments.BlockSize,
                arguments.Compression,
                chunking.RawFileSize,
                chunking.SparseInfo?.OccupiedBytes,
                chunking.Chunks.Count,
                chunking.SparseInfo?.RegionCount,
                chunking.SparseInfo?.WrittenBytes,
                chunking.SparseInfo?.DownloadBytes)
            .Log();

        async ValueTask<IUploadMechanism> getUploadMechanism()
        {
            if (arguments.Uri.IsFile)
            {
                // Write to a file instead. Used mainly for testing.
                return new TargetFileUploader(arguments.Uri.LocalPath, overwrite: arguments.Overwrite);
            }
            else if (arguments.Uri.Scheme == "null")
            {
                return new NullUploadMechanism();
            }
            else
            {
                // Upload to Azure Storage.

                BlockBlobClient client;
                var options = new BlobClientOptions()
                {
                    Retry =
                    {
                        MaxRetries = 20,
                        Mode = RetryMode.Exponential,
                        Delay = TimeSpan.FromSeconds(2),
                        MaxDelay = TimeSpan.FromSeconds(16),
                        NetworkTimeout = TimeSpan.FromSeconds(60),
                    },
                };

                options.Diagnostics.ApplicationId = VersionInfo.GenerateApplicationId();

                if (arguments.Uri.UseAzureCredentials())
                {
                    // NOTE: the constructor for BlockBlobClient here is different from the other branch. This constructor
                    // won't take a null credential.
                    client = new BlockBlobClient(arguments.Uri, new ManagedIdentityCredential(), options);
                }
                else
                {
                    client = new BlockBlobClient(arguments.Uri, options: options);
                }

                return await AzureStorageUploader.CreateAsync(client, arguments.Overwrite, cancellationToken);
            }
        }

        var uploadMechanism = await getUploadMechanism();
        try
        {
            if (arguments.CheckManifestUri != null)
            {
                uploadMechanism = await VerifyingUploadMechanism.CreateAsync(uploadMechanism, arguments.CheckManifestUri, cancellationToken);
            }

            await UploadFileAsync(arguments, statistics, contentProvider, chunking, uploadMechanism, cancellationToken);
        }
        catch
        {
            try
            {
                // We pass in a CancellationToken.None here because we're already in a failed state, either because
                // we actually failed or because we were cancelled. In the latter case, we want to make sure the
                // operation runs.
                await uploadMechanism.DeleteAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Logger.ForErrorEvent()
                    .Message(
                        "Failed to delete the output {Uri}",
                        arguments.Uri.Scrub().ToString())
                    .Exception(ex)
                    .Log();
            }

            throw;
        }
        finally
        {
            await uploadMechanism.DisposeAsync();
        }

        Logger.ForInfoEvent()
            .Message(
                "Upload of {FileName} to {Uri} completed successfully.",
                contentProvider.FullName,
                arguments.Uri.Scrub().ToString())
            .Log();

        statistics.LogCompressionReport();
    }

    private static async Task UploadFileAsync<T>(UploadArguments arguments, Statistics statistics, IUploadContentProvider contentProvider, ChunkingScheme chunking, T uploadMechanism, CancellationToken cancellationToken)
        where T : IUploadMechanism
    {
        using var uploadSemaphore = new SemaphoreSlim(arguments.MaximumUploadConcurrency, arguments.MaximumUploadConcurrency);
        var tasks = chunking.Chunks.Select(async boundary =>
        {
            using var guard = await uploadSemaphore.AcquireAsync(cancellationToken);

            await using var accessor = arguments.RandomFileSize != null
                ? ChunkFileAccessor.CreateRandomContent((long)arguments.RandomFileSize.Value)
                : ChunkFileAccessor.Create(contentProvider.OpenReadStream(), chunking);
            return await UploadBoundaryAsync(arguments, statistics, uploadMechanism, accessor, boundary, cancellationToken);
        });
        var partials = await Task.WhenAll(tasks);

        var metadataBlockInfo = await UploadManifestAsync(arguments, chunking, uploadMechanism, chunking.RawFileSize, chunking.RawBlockSize, partials, cancellationToken);
        await uploadMechanism.CommitAsync(partials.Select(p => p.BlockInfo).Concat([metadataBlockInfo]), cancellationToken);
    }

    private static async Task<BlockInfo> UploadManifestAsync(
        UploadArguments arguments,
        ChunkingScheme chunking,
        IUploadMechanism uploadMechanism,
        long rawSize,
        long rawBlockSize,
        Partial[] partials,
        CancellationToken cancellationToken)
    {
        var (manifest, manifestOffset) = CreateManifestFromPartials(rawSize, rawBlockSize, partials.ToList(), chunking);

        BlockInfo metadataBlockInfo;
        await using (var manifestStream = new MemoryStream())
        {
            await manifest.WriteFooterAsync(manifestStream, manifestOffset, cancellationToken);

            var manifestBytes = manifestStream.GetBuffer()
                .AsMemory()
                .Slice(0, (int)manifestStream.Length);
            metadataBlockInfo = await uploadMechanism.UploadBlockAsync(null, manifestBytes, cancellationToken);
        }

        if (arguments.ManifestPath is not null)
        {
            File.WriteAllText(arguments.ManifestPath, manifest.ToJsonString());
        }

        if (metadataBlockInfo.Offset is not null && metadataBlockInfo.Offset != manifestOffset)
        {
            throw new InvalidOperationException($"The metadata block offset is {metadataBlockInfo.Offset}, but it should be {manifestOffset}");
        }

        return metadataBlockInfo;
    }

    private static async Task<Partial> UploadBoundaryAsync(
        UploadArguments arguments,
        Statistics statistics,
        IUploadMechanism uploadMechanism,
        ChunkFileAccessor fileAccessor,
        ChunkMapping chunkMapping,
        CancellationToken cancellationToken)
    {
        var boundary = chunkMapping.Chunk;
        statistics.Increment(Counters.ChunksUploadStarted);

        try
        {
            Logger.ForTraceEvent()
                .Message(
                    "Uploading {Boundary} [{Percent}]",
                    boundary,
                    fileAccessor.GetPercent(boundary.Start))
                .Log();

            // We have to buffer the entire decompressed block in memory:
            // 1. Compression APIs need for the compressing part to be a write-only stream, so we can't compress while
            //    reading from a file.
            // 2. We _could_ use a buffer to read from the file and write to the compression stream, but that would
            //    mean we just loose control of the buffering we're doing here. It's better to read large chunks from
            //    the file and compress them in memory.

            var rawContentArray = ArrayPool<byte>.Shared.Rent((int)boundary.Length);
            using var rawDisposeArray = new DisposeAction<byte[]>(rawContentArray, static (array) => ArrayPool<byte>.Shared.Return(array, clearArray: false));
            var rawContent = rawContentArray.AsMemory().Slice(0, (int)boundary.Length);

            await fileAccessor.ReadChunkAsync(boundary.Start, rawContent, cancellationToken);
            statistics.Increment(Counters.BytesRead, boundary.Length);

            CompressionResult? compressionInfo = null;
            BlockInfo? uploadInfo = null;
            var compression = arguments.Compression;
            if (compression != CompressionAlgorithm.None)
            {
                // We have to buffer the entire compressed block in memory:
                // 1. Azure Blob Storage's APIs require that the Content-Length header be set to the length of the data
                //    when uploading. Naturally, we don't know this upfront because we're compressing it.
                // 2. Compression APIs don't allow us to get the length of the compressed data without actually compressing.
                // 3. No, Azure Storage does not support multipart HTTP requests either.
                var compressedContentArray = ArrayPool<byte>.Shared.Rent((int)boundary.Length);
                using var compressedDisposeArray = new DisposeAction<byte[]>(compressedContentArray, static (array) => ArrayPool<byte>.Shared.Return(array, clearArray: false));

                // WARNING: The length of the array may be larger than the length of the chunk because of how the
                // array pool operates. This means we could potentially have some extra space at the end of the array.
                //
                // We allow the compressor to write up to 64 bytes more than the chunk length, because we're hashing
                // the raw data as we compress, and so we're saving some compute by letting it run a bit longer. It's
                // unlikely the compressor will go too far above the chunk length, but by allowing for it we save time.
                long maximumOverhead = 0;
                if (compressedContentArray.Length > boundary.Length)
                {
                    maximumOverhead = Math.Max(Math.Min(compressedContentArray.Length - boundary.Length, 64), 0);
                }

                var maximumCompressedLength = boundary.Length + maximumOverhead;
                Contract.Assert(maximumCompressedLength <= compressedContentArray.Length);

                // Create a non-resizable MemoryStream to write the compressed data to. We need to set the length to 0
                // because the constructor will initialize to the complete length, which would lead to assuming the
                // data is uncompressible.
                await using var compressedStream = new MemoryStream(
                    compressedContentArray,
                    0,
                    (int)maximumCompressedLength,
                    writable: true);
                compressedStream.SetLength(0);
                try
                {
                    Contract.Assert(compressedStream.Length == 0);
                    compressionInfo = await compressedStream.WriteCompressedAsync(
                        contents: rawContent,
                        uncompressedHashType: arguments.UncompressedHashType,
                        compressedHashType: arguments.CompressedHashType,
                        algorithm: arguments.Compression,
                        level: arguments.CompressionLevel,
                        cancellationToken: cancellationToken);

                    if (compressedStream.Length >= boundary.Length)
                    {
                        // Compressed data is larger than the raw data, but it fits within the allocated array. This
                        // means it's not worth compressing, so we'll just upload the raw data.
                        compressionInfo = null;
                    }
                }
                catch (NotSupportedException)
                {
                    // Compressed data is larger than the raw data, and it goes over the capacity of the allocated
                    // array. When this happens, the compressor will try to write more data than the underlying
                    // MemoryStream can support, and that will cause MemoryStream to throw.
                    //
                    // This should happen infrequently, as it means that compression is useless for the given block.
                    // When it does happen, at this point we'll have lost any progress towards computing the block's
                    // hash, which means we need to re-compute it as we upload the block below.
                    compressionInfo = null;
                }

                if (compressionInfo != null)
                {
                    var compressedContent = compressedContentArray.AsMemory().Slice(0, (int)compressedStream.Length);
                    uploadInfo = await uploadMechanism.UploadBlockAsync(new(chunkMapping, compressionInfo), compressedContent, cancellationToken);
                    Contract.Assert(uploadInfo.Length == compressedStream.Length);
                }
            }

            if (compressionInfo == null)
            {
                compression = CompressionAlgorithm.None;

                compressionInfo = await Stream.Null.WriteCompressedAsync(
                        contents: rawContent,
                        uncompressedHashType: arguments.UncompressedHashType,
                        compressedHashType: arguments.CompressedHashType,
                        algorithm: compression,
                        level: arguments.CompressionLevel,
                        cancellationToken: cancellationToken);

                uploadInfo = await uploadMechanism.UploadBlockAsync(new(chunkMapping, compressionInfo), rawContent, cancellationToken);

                Contract.Assert(uploadInfo.Length == rawContent.Length);
            }

            Contract.Assert(compressionInfo is not null);
            Contract.Assert(uploadInfo is not null);

            if (arguments.Compression != CompressionAlgorithm.None && compression == CompressionAlgorithm.None)
            {
                // This only happens when compression above doesn't work.
                statistics.Increment(Counters.BytesIncompressible, rawContent.Length);
            }

            statistics.Increment(Counters.BytesUploaded, uploadInfo.Length);
            long savings = rawContent.Length - uploadInfo.Length;
            statistics.Increment(Counters.BytesSaved, savings);
            statistics.Increment(Counters.ChunksUploadSucceeded);

            Logger.ForTraceEvent()
                .Message(
                    "Uploaded {Boundary} into {UploadInfo} using compression algorithm {Compression}. Saved {SavingsBytes} bytes. RawHash={RawHash} SparseRegions (Count={SparseRegionCount}, Extent={SparseRegionExtent})",
                    boundary,
                    uploadInfo,
                    compression,
                    savings,
                    compressionInfo.RawContentHash,
                    chunkMapping.SparseRegions?.Length ?? -1,
                    chunkMapping.SparseRegions?.Aggregate((c1, c2) => c1.Union(c2)))
                .Log();

            return new Partial(
                chunkMapping,
                uploadInfo,
                compressionInfo);
        }
        catch (Exception ex)
        {
            if (ex is OperationCanceledException || ex is TaskCanceledException)
            {
                statistics.Increment(Counters.ChunksUploadCancelled);
            }
            else
            {
                statistics.Increment(Counters.ChunksUploadFailed);

                Logger.ForErrorEvent()
                    .Message(
                        "Failed to upload {Boundary}",
                        boundary)
                    .Exception(ex)
                    .Log();

                throw;
            }

            throw;
        }
        finally
        {
            statistics.Increment(Counters.ChunksUploadCompleted);
        }
    }

    private record CreateManifestResult(Manifest Manifest, long Offset);

    private static CreateManifestResult CreateManifestFromPartials(long rawSize, long rawBlockSize, List<Partial> partials, ChunkingScheme chunking)
    {
        // Create the manifest. We sort the partials by the raw boundary so that the manifest is deterministic and
        // blocks are in fetch order.
        //
        // Because the manifest needs to know the offset of each block in the final file, we compute that here and fill
        // out the offset field in the partials, and then proceed to generate the manifest, upload it, and commit.
        partials.Sort((a, b) => a.RawBoundary.CompareTo(b.RawBoundary));

        long offset = 0;
        foreach (var partial in partials)
        {
            if (partial.BlockInfo.Offset is not null)
            {
                // If a single block sets an offset, the assumption is all of them do, as it's a property of the upload
                // mechanism. In such cases, we limit ourselves to the maximum offset as we have no idea what the
                // relative ordering of the blocks is.
                partial.Offset = partial.BlockInfo.Offset.Value;
                offset = Math.Max(offset, partial.Offset + partial.BlockInfo.Length);
            }
            else
            {
                // If no block sets an offset, we assume we can arrange them as we wish. We set things up so they are in
                // order and contiguous.
                partial.Offset = offset;
                offset += partial.BlockInfo.Length;
            }
        }

        var blocks = partials.Select(partial =>
        {
            return new Block()
            {
                RawHash = partial.CompressionResult.RawContentHash,
                CompressedHash = partial.CompressionResult.CompressedContentHash,
                RawSlice = partial.RawBoundary,
                Compression = partial.CompressionResult.CompressionAlgorithm,
                CompressedSlice = new Slice()
                {
                    Offset = partial.Offset,
                    Length = partial.BlockInfo.Length,
                },
                SparseRegions = partial.RawMapping.SparseRegions?.SelectArray<Chunk, Slice>(c => c)
            };
        }).ToList();

        var manifest = new Manifest()
        {
            Producer = Globals.ProductVersion,
            CorrelationId = Globals.CorrelationId.ToString(),
            RawSize = rawSize,
            RawBlockSize = rawBlockSize,
            Hash = blocks.ComputeBlockHash(HashInfoLookup.GetContentHasher(HashType.SHA256)),
            Blocks = blocks,
            IsSparse = chunking.SparseInfo != null,
            TotalSparseWrittenBytes = chunking.SparseInfo?.WrittenBytes
        };

        return new CreateManifestResult(manifest, offset);
    }

}

