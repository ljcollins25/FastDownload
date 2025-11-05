// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using System.Net;
using FastDownload.Cli;
using FastDownload.Download;
using FastDownload.Shared;
using FastDownload.Tests.Utilities;
using FastDownload.Upload;
using FastDownload.Utilities;
using Shouldly;

namespace FastDownload.Tests.Roundtrip
{
    internal sealed class NonSparseBehavior : IDownloadBehavior
    {
        public async Task AfterAsync(DownloadOutput result)
        {
            var filePath = result.Arguments.Path;

            if (result.ReturnCode == ReturnCode.Success && File.Exists(filePath))
            {
                // The leftover file should never be sparse.
                await using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    Contract.Assert(!fileStream.IsSparse(), $"File {filePath} is marked as Sparse when it shouldn't be");
                }
            }
        }
    }

    internal sealed class SparseBehavior(
        SparseHandlingMode sparseHandling = SparseHandlingMode.Compact) : IRoundtripBehavior
    {
        public Task Configure(UploadArguments arg)
        {
            arg.SparseHandling = sparseHandling;
            return Task.CompletedTask;
        }

        public async Task AfterAsync(DownloadOutput result)
        {
            var filePath = result.Arguments.Path;

            if (result.ReturnCode == ReturnCode.Success && File.Exists(filePath))
            {
                // The leftover file should never be sparse.
                await using (var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read))
                {
                    var chunkCount = fileStream.GetDataRegions().Count;
                    Contract.Assert(fileStream.IsSparse(), $"File {filePath} is not marked as Sparse when it should be");
                }
            }
        }
    }

    internal sealed class DownloadFailureBehavior : IRoundtripBehavior
    {
        private readonly ReturnCode _returnCode;

        public DownloadFailureBehavior(ReturnCode returnCode = ReturnCode.UnhandledException)
        {
            Contract.Requires(returnCode != ReturnCode.Success);

            _returnCode = returnCode;
        }

        public Task AfterAsync(DownloadOutput result)
        {
            Assert.AreEqual(_returnCode, result.ReturnCode);

            // We don't assert that the output doesn't exist because tests are weird and might actually have the same
            // file open in parallel.

            return Task.CompletedTask;
        }
    }

    internal sealed class SuccessBehavior : IRoundtripBehavior
    {
        public Task AfterAsync(UploadOutput result)
        {
            Assert.AreEqual(ReturnCode.Success, result.ReturnCode);
            return Task.CompletedTask;
        }

        public Task AfterAsync(DownloadOutput result)
        {
            Assert.AreEqual(ReturnCode.Success, result.ReturnCode);
            Assert.AreEqual(0, result.Statistics.Read(Counters.BytesHashMismatch));
            Assert.AreEqual(0, result.Statistics.Read(Counters.BuffersOutstanding));
            return Task.CompletedTask;
        }

        public async Task AfterRoundtripAsync(RoundtripOutput result)
        {
            var hash = await TestExtensions.TryHashFileAsync(result.Upload.Arguments.Path, result.Upload.InputHash.HashType);

            hash.ShouldBe(result.Upload.InputHash);

            if (result.Upload.InputHash != result.Download.OutputHash)
            {
                await CompareFilesAsync(result.Upload.Arguments.Path, result.Download.Arguments.Path, result.Upload.Arguments.BlockSize);
            }

            Assert.AreEqual(result.Upload.InputHash, result.Download.OutputHash);

            var (uploadResult, downloadResult) = result;
            var upload = uploadResult.Arguments;
            var download = downloadResult.Arguments;

            Assert.AreEqual(uploadResult.InputHash, downloadResult.OutputHash);

            download.Statistics.Read(Counters.BytesWritten).ShouldBe(upload.Statistics.Read(Counters.BytesRead));
        }

        private static async Task CompareFilesAsync(string inputPath, string downloadPath, long blockSize)
        {
            var inputBytes = new byte[blockSize];
            var downloadBytes = new byte[blockSize];

            using var inputStream = File.OpenRead(inputPath);
            using var downloadStream = File.OpenRead(downloadPath);

            var inputLength = inputStream.Length;
            var downloadLength = downloadStream.Length;

            var blockCount = (long)Math.Max(Chunk.Count(inputLength, blockSize), Chunk.Count(downloadLength, blockSize));
            for (int i = 0; i < blockCount; i++)
            {
                var start = i * blockSize;
                start.ShouldBeLessThan(inputLength, $"Block {i} (Start = {start}) Downloaded file (size = {downloadLength}) has blocks beyond what is present in input file (size = {inputLength})");
                start.ShouldBeLessThan(downloadLength, $"Block {i} (Start = {start}) Downloaded file (size = {downloadLength}) is missing blocks present in input file (size = {inputLength})");

                var inputChunk = new Chunk(start, Math.Min(inputLength, start + blockSize));
                var downloadChunk = new Chunk(start, Math.Min(downloadLength, start + blockSize));

                var readChunk = new Chunk(start, Math.Min(inputChunk.End, downloadChunk.End));

                var inputMemory = inputBytes.AsMemory()[0..(int)readChunk.Length];
                var downloadMemory = downloadBytes.AsMemory()[0..(int)readChunk.Length];

                await inputStream.ReadAtLeastAsync(inputMemory, inputMemory.Length);
                await downloadStream.ReadAtLeastAsync(downloadMemory, downloadMemory.Length);

                var commonLength = inputMemory.Span.CommonPrefixLength(downloadMemory.Span);
                var diffIndex = commonLength;

                var differenceByte = start + commonLength;
                Assert.IsTrue(
                    commonLength == inputChunk.Length && commonLength == downloadChunk.Length,
                    $"Input file differs from downloaded file at byte {differenceByte} (Chunk {i} byte {commonLength}) Input byte ({inputMemory.DisplayByteAt(diffIndex)}) != ({downloadMemory.DisplayByteAt(diffIndex)}) Download byte");
            }
        }
    }

    internal sealed class Uncompressible(bool sparse = false) : IRoundtripBehavior
    {
        public Task AfterAsync(UploadOutput result)
        {
            // Sparse content may compress content between sparse segments
            if (!sparse)
            {
                result.Arguments.Statistics.Read(Counters.BytesIncompressible).ShouldBe(result.InputLength);
            }

            return Task.CompletedTask;
        }

        public Task AfterAsync(DownloadOutput result)
        {
            // Sparse content may compress content between sparse segments
            if (!sparse)
            {
                var statistics = result.Statistics;
                statistics.Read(Counters.BytesDecompressed).ShouldBe(0);
            }

            return Task.CompletedTask;
        }

        public Task AfterRoundtripAsync(RoundtripOutput result)
        {
            var upload = result.Upload.Arguments;
            var download = result.Download.Arguments;
            if (!sparse)
            {
                download.Statistics.Read(Counters.BytesDownloaded).ShouldBeGreaterThanOrEqualTo(upload.Statistics.Read(Counters.BytesRead));
            }

            download.Statistics.Read(Counters.BytesDownloaded).ShouldBeGreaterThanOrEqualTo(result.Upload.InputLength);
            return Task.CompletedTask;
        }
    }

    internal sealed class Compressible : IRoundtripBehavior
    {
        public Task AfterAsync(UploadOutput result)
        {
            var statistics = result.Statistics;
            Assert.AreEqual(statistics.Read(Counters.BytesIncompressible), 0);
            Assert.IsTrue(statistics.Read(Counters.BytesUploaded) < statistics.Read(Counters.BytesRead));
            Assert.IsTrue(statistics.Read(Counters.BytesSaved) > 0);
            Assert.IsTrue(statistics.Read(Counters.BytesSaved) < statistics.Read(Counters.BytesRead));
            return Task.CompletedTask;
        }

        public Task AfterAsync(DownloadOutput result)
        {
            return Task.CompletedTask;
        }

        public Task AfterRoundtripAsync(RoundtripOutput result)
        {
            var statistics = result.Download.Statistics;
            if (result.Upload.Arguments.SparseAware)
            {
                statistics.Read(Counters.BytesWritten).ShouldBeLessThanOrEqualTo(statistics.Read(Counters.BytesDecompressed));
            }
            else
            {
                Assert.AreEqual(statistics.Read(Counters.BytesDecompressed), statistics.Read(Counters.BytesWritten));
            }

            return Task.CompletedTask;
        }
    }

    internal sealed class HashMismatch : IRoundtripBehavior
    {
        public Task AfterRoundtripAsync(RoundtripOutput result)
        {
            // We expect the hashes to be different because the data was modified after upload
            Assert.AreNotEqual(result.Upload.InputHash, result.Download.OutputHash);
            return Task.CompletedTask;
        }

        public Task BeforeAsync(DownloadArguments arguments)
        {
            // Skip the compressed hash check to allow falling back to hash check on decompressed content
            arguments.SkipCompressedHashCheck = true;
            return Task.CompletedTask;
        }

        public async Task AfterAsync(UploadOutput result)
        {
            Assert.AreEqual(result.ReturnCode, ReturnCode.Success);

            var uri = result.Arguments.Uri;
            Contract.Assert(uri.IsFile);

            var path = uri.LocalPath;

            byte[] content = await File.ReadAllBytesAsync(path);
            content[new Random(Seed: 12345).Next(content.Length)]++;
            await File.WriteAllBytesAsync(path, content);
        }

        public Task AfterAsync(DownloadOutput result)
        {
            result.ReturnCode.ShouldBe(ReturnCode.UnhandledException);
            Assert.IsTrue(result.Statistics.Read(Counters.BytesHashMismatch) > 0);
            return Task.CompletedTask;
        }
    }

    internal sealed class DownloadMustRetryBehavior : IDownloadBehavior
    {
        private readonly HttpStatusCode? _statusCode;

        public DownloadMustRetryBehavior(HttpStatusCode? statusCode)
        {
            _statusCode = statusCode;
        }

        public Task BeforeAsync(DownloadArguments arguments)
        {
            arguments.ChunkDownloadTimeoutMinutes = TimeSpan.FromHours(1).TotalMinutes;
            arguments.ExecutionTimeoutMinutes = TimeSpan.FromHours(1).TotalMinutes;
            return Task.CompletedTask;
        }

        public Task AfterAsync(DownloadOutput result)
        {
            result.Statistics.Read(Counters.ChunkDownloadHttpRetry).ShouldBeGreaterThan(0);
            if (_statusCode != null)
            {
                (result.Statistics.Read($"Retry{_statusCode.ToString()}") ?? 0).ShouldBeGreaterThan(0);
            }

            return Task.CompletedTask;
        }
    }

    internal sealed class ConfigureBehavior(
        Action<DownloadArguments>? configureDownload = null,
        Action<UploadArguments>? configureUpload = null) : IRoundtripBehavior
    {
        public Task Configure(DownloadArguments arg)
        {
            configureDownload?.Invoke(arg);
            return Task.CompletedTask;
        }

        public Task Configure(UploadArguments arg)
        {
            configureUpload?.Invoke(arg);
            return Task.CompletedTask;
        }
    }
}
