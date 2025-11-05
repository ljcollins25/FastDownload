// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Utilities.Collections;
using BuildXL.Utilities.Core.Tracing;
using FastDownload.Shared;
using FastDownload.Utilities;
using NLog;
using static FastDownload.Download.DownloadArguments;

namespace FastDownload.Download
{
    internal static class DownloadCommand
    {
        private static readonly Logger Logger = LogManager.GetLogger(nameof(DownloadCommand));

        public static async Task ExecuteAsync(DownloadArguments arguments, Statistics statistics, CancellationTokenSource internalCancellationSource, CancellationToken cancellationToken)
        {
            var maximumDownloadConcurrency = arguments.MaximumDownloadConcurrency;

            var defaultDecompressionConcurrency = (uint)(2 * Environment.ProcessorCount);
            var maximumDecompressionConcurrency = Math.Min(arguments.MaximumDecompressionConcurrency ?? defaultDecompressionConcurrency, defaultDecompressionConcurrency);

            var maximumWriteConcurrency = Math.Max(1, arguments.MaxWriteConcurrency ?? maximumDownloadConcurrency);

            if (arguments.SimulationMode == SimulationModes.WriteStressSequential
                || arguments.SimulationMode == SimulationModes.WriteStressSequentialAsync)
            {
                maximumWriteConcurrency = 1;

                if (arguments.SimulationMode == SimulationModes.WriteStressSequential)
                {
                    arguments.Async = false;
                }
            }

            var httpClientSettings = await HttpClientSettings.CreateAsync(arguments.Uri, Logger, cancellationToken);

            Logger.ForInfoEvent()
                .Message(
                    "Downloading {Uri} into {Path}",
                    arguments.Uri.Scrub().ToString(),
                    arguments.Path)
                .Log();

            var (rawArchiveFileSizeBytes, etag, manifest) = await RemoteFileMetadata.FetchAsync(arguments.Uri, httpClientSettings, cancellationToken);
            long rawFileSizeBytes = rawArchiveFileSizeBytes;
            long archiveSizeBytes = rawArchiveFileSizeBytes;
            long expectedWrittenBytes = rawFileSizeBytes;
            if (manifest is null)
            {
                Logger.ForInfoEvent()
                    .Message(
                        "Downloading file of size {RawFileSizeBytes} ({RawFileSizeBytesMb} MB) without compression",
                        rawFileSizeBytes,
                        rawFileSizeBytes.AsMb())
                    .Log();
            }
            else
            {
                rawFileSizeBytes = manifest.RawSize;
                expectedWrittenBytes = manifest.TotalSparseWrittenBytes ?? manifest.RawSize;

                if (!string.IsNullOrEmpty(manifest.CorrelationId))
                {
                    Globals.SourceCorrelationId = manifest.CorrelationId;
                }

                if (arguments.ManifestPath is not null)
                {
                    File.WriteAllText(arguments.ManifestPath, manifest.ToJsonString());
                }

                Logger.ForInfoEvent()
                    .Message(
                        "Found manifest version {ManifestVersion} produced by program version {ProducerVersion} with Correlation ID {CorrelationId}",
                        manifest.Version,
                        manifest.Producer?.Source ?? "Unknown",
                        manifest.CorrelationId ?? "Unknown")
                    .Log();

                Logger
                    .ForInfoEvent()
                    .Message(
                        "Downloading file of size {ArchiveSizeBytes} ({ArchiveSizeBytesMb} MB) and decompressing into {RawFileSizeBytes} ({RawFileSizeBytesMb} MB). The manifest hash is {ManifestHash}.",
                        archiveSizeBytes,
                        archiveSizeBytes.AsMb(),
                        rawFileSizeBytes,
                        rawFileSizeBytes.AsMb(),
                        manifest.Hash)
                    .Log();
            }

            await using var fileStream = FileUtilities.OpenUnbufferedFileStream(arguments.Path, FileMode.Create, writeThrough: arguments.WriteThrough, async: arguments.Async);
            try
            {
                // Marking the file as sparse ensures that the file is allocated in a sparse manner, which is useful
                // for large files. When we set the length below on a dense file, the OS would write 0s to the entire
                // file on disk, which wastes a lot of time. By marking the file as sparse, the SetLength below comes
                // a metadata operation instead, which is obscenely faster.
                if (arguments.Sparse)
                {
                    FileMarkSparse(fileStream);
                }

                // Setting the file length reserves the space on disk without actually writing anything. It's important we
                // do this first, because it radically improves write performance.
                fileStream.SetLength(rawFileSizeBytes);

                // Compute the alignment for unbuffered IO, and ensure the chunk size is a multiple of the alignment (ignores file alignment on non-Windows OS)
                var alignment = WindowsNativeMethods.ComputeUnbufferedIOAlignment(fileStream);

                uint chunkSize;
                long chunks;
                var workItems = new List<WorkItem>();
                if (manifest is null)
                {
                    // This file isn't an archive, so we'll download it in chunks of the specified size.
                    chunkSize = (uint)Maths.AlignTo(arguments.ChunkSize, alignment);

                    chunks = rawFileSizeBytes / chunkSize;
                    if (rawFileSizeBytes % chunkSize != 0)
                    {
                        chunks++;
                    }

                    Logger
                        .ForInfoEvent()
                        .Message(
                            "Downloading {Chunks} chunks of {ChunkSize} bytes each",
                            chunks,
                            chunkSize)
                        .Log();

                    var id = 0;
                    foreach (var chunk in Chunk.Split(rawFileSizeBytes, chunks, chunkSize, alignment))
                    {
                        workItems.Add(new WorkItem(
                            ChunkIndex: id++,
                            Source: chunk,
                            Destination: chunk,
                            RawHash: null,
                            Compression: CompressionAlgorithm.None,
                            CompressedHash: null,
                            SparseRegions: null));
                    }
                }
                else
                {
                    var blockSize = manifest.RawBlockSize;
                    if (Maths.IsAligned(blockSize, alignment))
                    {
                        // The block size is already aligned to the unbuffered IO alignment, so we can use it as is.
                        //
                        // This should always be the case if the block size is a sufficiently large power of two,
                        // because the alignment is also a power of two.
                        chunkSize = (uint)blockSize;
                        chunks = manifest.Blocks.Count;

                        Logger
                            .ForInfoEvent()
                            .Message(
                                "Downloading {Chunks} blocks of {BlockSize} bytes each",
                                chunks,
                                blockSize)
                            .Log();

                        var id = 0;
                        foreach (var block in manifest.Blocks.OrderBy(b => b.RawSlice.Offset))
                        {
                            workItems.Add(new WorkItem(
                                ChunkIndex: id++,
                                Source: block.CompressedSlice,
                                Destination: block.RawSlice,
                                RawHash: block.RawHash,
                                Compression: block.Compression,
                                CompressedHash: block.CompressedHash,
                                SparseRegions: block.SparseRegions?.SelectArray(s => s.ToChunk())));
                        }

                        if (arguments.DedupeDownloadChunks)
                        {
                            var workItemsLookup = workItems.ToLookup(w => w.RawHash!.Value);
                            workItems = workItemsLookup.Select(g => g.Count() == 1 ? g.First() : g.First() with
                            {
                                Destinations = g.ToArray()
                            }).ToList();
                        }
                    }
                    else
                    {
                        // TODO: this is a huge simplification. We might not want to download in chunks of blockSize,
                        // but we might want to download in chunks of chunkSize into a buffer of size blockSize.
                        throw new InvalidOperationException($"The block size {blockSize} is not aligned to the unbuffered IO alignment {alignment}. Please contact the development team.");
                    }
                }

                if (chunks < maximumDownloadConcurrency)
                {
                    maximumDownloadConcurrency = (uint)chunks;
                }

                Logger.ForInfoEvent()
                    .Message(
                        "Running over up to {DownloadConcurrency} concurrent downloads, {WriteConcurrency} concurrent writes, and {DecompressionConcurrency} concurrent decompressors.",
                        maximumDownloadConcurrency,
                        maximumWriteConcurrency,
                        maximumDecompressionConcurrency)
                    .Log();

                var input = new Input(
                    arguments,
                    etag,
                    rawFileSizeBytes,
                    archiveSizeBytes,
                    alignment,
                    chunkSize,
                    chunks,
                    maximumDownloadConcurrency,
                    maximumDecompressionConcurrency,
                    maximumWriteConcurrency,
                    manifest,
                    workItems);

                var fileId = $"{arguments.Uri.Scrub()}@{etag}";

                string getChecksum()
                {
                    var checksum = manifest?.Hash is { } hash
                        ? $"mh{hash.ToShortString(includeHashType: false)}"
                        : $"fi{Extensions.GetHashString(fileId)}";

                    // Add hash of scope to checksum so that machines only see machines in
                    checksum = $"{checksum}_{Extensions.GetHashString(arguments.ProxyScopeId)}";

                    return checksum;
                }

                var state = new GlobalState(input, statistics, cancellationToken)
                {
                    HttpClientSettings = httpClientSettings,
                };

                var zone = await HttpUtilities.GetAzureZoneAsync();

                using var chunkHost = !arguments.ProxyServerEnabled ? null : new ChunkHost(
                    arguments.Path,
                    rawFileSizeBytes,
                    workItems,
                    chunkSize,
                    maxBufferSize: (arguments.ProxyBufferSizeMb << 20))
                {
                    MachineInfo = new ProxyNodeEntry(new Uri($"{arguments.ServerProtocol}://{Environment.MachineName}:{arguments.Port}"))
                    {
                        FileChecksum = getChecksum(),
                        Zone = zone
                    },
                    GlobalState = state,
                    Statistics = statistics,
                    ProxySemaphore = state.BlobDownloadSemaphore
                };

                await using var server = chunkHost == null ? null : new ChunkServer(chunkHost, arguments.Statistics, arguments.UseHttp2)
                {
                    PendingShutdownDelay = TimeSpan.FromSeconds(arguments.PendingShutdownDelaySeconds),
#pragma warning disable CS8670 // Object or collection initializer implicitly dereferences possibly null member.
                    ProxyChain =
                    {
                        LinearChain = arguments.UseLinearProxyChain
                    }
#pragma warning restore CS8670 // Object or collection initializer implicitly dereferences possibly null member.
                };

                var stopwatch = StopwatchSlim.Start();

                try
                {
                    await using var progressReporter = ProgressReporter.Start(state);

                    if (server != null)
                    {
                        Logger.ForInfoEvent()
                            .Message(
                                "Starting server at '{ServerUri}' FileChecksum={FileChecksum} Id={Id}. BufferCount={BufferCount}",
                                chunkHost!.MachineInfo.Uri,
                                chunkHost!.MachineInfo.FileChecksum,
                                fileId,
                                chunkHost!.BufferCount)
                            .Log();

                        try
                        {
                            await server.StartAsync();
                            state.ChunkHost = chunkHost;
                            state.ChunkServer = server;
                        }
                        catch (Exception ex)
                        {
                            Logger.Error(ex, "Failed to start chunk server");
                        }
                    }

                    await state.RunAsync();

                    // Wait for all workers to finish before closing the file stream. This is important because we need to ensure
                    // everything is written to the file before we close it.
                    var downloadTime = stopwatch.ElapsedAndReset();

                    await progressReporter.StopAsync();

                    var writtenBytes = statistics.Read(Counters.BytesWritten);
                    if (writtenBytes != expectedWrittenBytes)
                    {
                        throw new InvalidOperationException($"The download has exited with successful state, but the number of bytes written ({writtenBytes}) doesn't match the expected number ({expectedWrittenBytes}). Please contact the development team.");
                    }

                    // All writes need to be sector aligned, so the last chunk might write past the end of the file. In such cases,
                    // we need to truncate the file to the correct size.
                    fileStream.SetLength(rawFileSizeBytes);

                    // Ensure the file and it's metadata is flushed to disk. Because we do unbuffered IO, this should only flush
                    // the metadata and not the data itself, which should pretty much be a no-op.
                    //
                    // This is mainly here to ensure the times reported are accurate.
                    Logger
                        .ForInfoEvent()
                        .Message("Flushing file to disk...")
                        .Log();
                    await fileStream.FlushAsync();

                    // Sparse file downloads do not support clearing sparse file flag as
                    // all regions of file are not written
                    if (manifest?.IsSparse != true && !arguments.SkipZeroRegions)
                    {
                        FileEnsureNonSparse(fileStream);
                    }

                    fileStream.Close();

                    var flushTime = stopwatch.ElapsedAndReset();

                    Logger.ForInfoEvent()
                        .Message(
                            "File flushed to {Path} in {FlushTime} ({FlushTimeMs:f2} ms). Total download time: {TotalDownloadTime}.",
                            arguments.Path,
                            flushTime,
                            flushTime.TotalMilliseconds,
                            downloadTime + flushTime)
                        .Log();
                }
                catch (Exception ex)
                {
                    // When there's a failure in any of the logic, we want to cancel the download and let the cleanup code run.
                    // We don't log the exceptions here. The highest layers and the callers to this code should log it.
                    if (ex is OperationCanceledException)
                    {
                        Logger
                            .ForWarnEvent()
                            .Message("Download cancelled. Stopping workers and cleaning up...")
                            .Log();
                    }
                    else
                    {
                        Logger
                            .ForFatalEvent()
                            .Message("Download failed. Cancelling download and cleaning up...")
                            .Log();
                    }

                    internalCancellationSource.Cancel();
                    throw;
                }
            }
            catch (Exception ex)
            {
                // If there's an exception anywhere in the code above, the file won't be closed until after the exception
                // handling code runs. Since we'll be deleting it ourselves, we want it to be closed first.
                // We don't log the exceptions here. The highest layers and the callers to this code should log it.
                fileStream.Close();
                await fileStream.DisposeAsync();

                // Don't delete the file if it already exists. We don't want to accidentally delete user data.
                if (!ex.Message.Contains("already exists."))
                {
                    try
                    {
                        File.Delete(arguments.Path);

                        Logger.ForWarnEvent()
                            .Message("Deleted {Path} because the download failed", arguments.Path)
                            .Log();
                    }
                    catch (Exception deleteException)
                    {
                        Logger.ForErrorEvent()
                            .Message("Failed to delete {Path}. A partial file download may have been left behind", arguments.Path)
                            .Exception(deleteException)
                            .Log();
                    }
                }

                throw;
            }
        }

        private static bool FileMarkSparse(FileStream fileStream)
        {
            var sparse = WindowsNativeMethods.SetSparseFlag(fileStream, value: true);
            if (sparse)
            {
                Logger.ForInfoEvent()
                    .Message("Marked file as sparse")
                    .Log();
            }
            else
            {
                Logger.ForWarnEvent()
                    .Message("Failed to mark file as sparse. The file will be downloaded as a normal file. There will likely be performance degradation as a result.")
                    .Log();
            }

            return sparse;
        }

        private static void FileEnsureNonSparse(FileStream fileStream)
        {
            // VHDs with sparse flags can't be mounted with Hyper-V, so we need to remove the sparse flag.
            bool succeeded = WindowsNativeMethods.SetSparseFlag(fileStream, value: false);
            if (succeeded)
            {
                Logger.ForInfoEvent()
                    .Message("Unmarked file as sparse")
                    .Log();
            }

            // Verify that the file is not sparse at this point. We'll throw if we fail to ensure the program fails, and
            // the file gets deleted. Downstream components won't work with a sparse file.
            if (WindowsNativeMethods.IsSparse(fileStream))
            {
                Logger.ForErrorEvent()
                    .Message("Failed to unmark file as sparse")
                    .Log();

                throw new InvalidOperationException("Failed to unmark file as sparse.");
            }
        }
    }
}