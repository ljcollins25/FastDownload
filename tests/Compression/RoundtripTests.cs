// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using System.IO.Compression;
using System.Net;
using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Cli;
using FastDownload.Download;
using FastDownload.Shared.Manifest.V0;
using FastDownload.Tests.Utilities;
using FastDownload.Tests.Utilities.HttpServer;
using FastDownload.Upload;
using FastDownload.Utilities;
using Shouldly;

namespace FastDownload.Tests.Roundtrip
{
    [TestClass]
    public class RoundtripTests : TestOutputTestBase
    {
        [TestMethod]
        public async Task SuccessEmptyRoundtripTestAsync()
        {
            var test = new RoundtripTest(
                new EmptyContentGenerator(),
                Compound.Sequential(new SuccessBehavior(), new NonSparseBehavior()));
            await test.ExecuteRoundtripTestAsync();
        }

        [TestMethod]
        [DataRow(0.3)]
        [DataRow(0.5)]
        [DataRow(1.0)]
        [DataRow(1.1)]
        [DataRow(2.0)]
        [DataRow(5.3)]
        [DataRow(128)]
        [DataRow(0.3, true)]
        [DataRow(0.5, true)]
        [DataRow(1.0, true)]
        [DataRow(1.1, true)]
        [DataRow(2.0, true)]
        [DataRow(5.3, true)]
        [DataRow(128, true)]
        public async Task SuccessUncompressibleRoundtripTestAsync(double blockSizeMultiple, bool sparse = false)
        {
            var test = new RoundtripTest(
                SparseContentContentGeneratorWrapper.Wrap(new UncompressibleContentGenerator(blockSizeMultiple), sparse, out var behavior),
                Compound.Sequential(new SuccessBehavior(), new Uncompressible(sparse: sparse), behavior));
            await test.ExecuteRoundtripTestAsync();
        }

        [TestMethod]
        [DataRow(0.3)]
        [DataRow(0.5)]
        [DataRow(1.0)]
        [DataRow(1.1)]
        [DataRow(2.0)]
        [DataRow(5.3)]
        [DataRow(128)]
        [DataRow(0.3, true)]
        [DataRow(0.5, true)]
        [DataRow(1.0, true)]
        [DataRow(1.1, true)]
        [DataRow(2.0, true)]
        [DataRow(5.3, true)]
        [DataRow(128, true)]
        public async Task SuccessCompressibleRoundtripTestAsync(double blockSizeMultiple, bool sparse = false)
        {
            var test = new RoundtripTest(
                SparseContentContentGeneratorWrapper.Wrap(new HighlyCompressibleContentGenerator(blockSizeMultiple), sparse, out var behavior),
                Compound.Sequential(
                    new SuccessBehavior(),
                    new Compressible(),
                    behavior));
            await test.ExecuteRoundtripTestAsync();
        }

        // Comment out Ignore and add DataRow with arguments to run.
        [Ignore("Mainly for testing specific aspects manually, doesn't need to be run all the time to test functionality.")]
        [TestMethod]
        public async Task RealDataSparseFileTest(string path, string regionsFilePath, bool contiguousRegions, long maxLength = 100L << 30 /* 100 gb */)
        {
            var basePath = path + ".rtoutput";

            var generator = new FileSourceSparseContentGenerator(path, regionsFilePath, contiguousRegions, maxLength);
            var test = new RoundtripTest(
                generator,
                Compound.Sequential(
                    new SuccessBehavior(),
                    new Compressible(),
                    new SparseBehavior(),
                    new ConfigureBehavior(configureDownload: download =>
                    {
                        // Don't throttle download
                        download.MaximumDownloadMbps = 0;
                    })))
            {
                CachedBasePath = basePath,
                FolderName = "RealDataSparseFileTest",
                MinExecutionTimeoutMinutes = 15
            };
            await test.ExecuteRoundtripTestAsync(HashType.Murmur, blockSize: 128 << 20);
        }

        [TestMethod]
        [DataRow(128, CompressionAlgorithm.Brotli, CompressionLevel.Fastest)]
        [DataRow(128, CompressionAlgorithm.Brotli, CompressionLevel.Optimal)]
        [DataRow(128, CompressionAlgorithm.Lz4, CompressionLevel.Fastest)]
        [DataRow(128, CompressionAlgorithm.Lz4, CompressionLevel.Optimal)]
        [DataRow(128, CompressionAlgorithm.Zstd, CompressionLevel.Fastest)]
        [DataRow(128, CompressionAlgorithm.Zstd, CompressionLevel.Optimal)]
        public async Task SuccessCompressibleRoundtripAlgorithmTestAsync(double blockSizeMultiple, CompressionAlgorithm compression, CompressionLevel level)
        {
            var test = new RoundtripTest(
                new HighlyCompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new SuccessBehavior(), new Compressible(), new NonSparseBehavior()))
            {
                CompressionAlgorithm = compression,
                CompressionLevel = level
            };

            await test.ExecuteRoundtripTestAsync();
        }

        internal sealed class NonVerboseBehavior : IRoundtripBehavior
        {
            public Task BeforeAsync(DownloadArguments arguments)
            {
                arguments.Verbose = false;
                return Task.CompletedTask;
            }

            public Task BeforeAsync(UploadArguments arguments)
            {
                arguments.Verbose = false;
                return Task.CompletedTask;
            }
        }

        [Ignore("Mainly for testing specific aspects manually, doesn't need to be run all the time to test functionality.")]
        [TestMethod]
        [DataRow(512)]
        public async Task LongRunningTestAsync(double blockSizeMultiple)
        {
            var test = new RoundtripTest(
                new HighlyCompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new SuccessBehavior(), new Compressible(), new NonSparseBehavior(), new NonVerboseBehavior()));
            await test.ExecuteRoundtripTestAsync();
        }

        [TestMethod]
        public async Task UncompressibleHashMismatchTestAsync()
        {
            var test = new RoundtripTest(
                new UncompressibleContentGenerator(4),
                Compound.Sequential(new NonSparseBehavior(), new HashMismatch()));
            await test.ExecuteRoundtripTestAsync();
        }

        [TestMethod]
        public async Task HighlyCompressibleHashMismatchTestAsync()
        {
            var test = new RoundtripTest(
                new HighlyCompressibleContentGenerator(4),
                Compound.Sequential(new NonSparseBehavior(), new HashMismatch()));
            await test.ExecuteRoundtripTestAsync();
        }

        [TestMethod]
        public async Task CompressedHashBackwardCompatibility()
        {
            var test = new RoundtripTest(
                new HighlyCompressibleContentGenerator(4),
                Compound.Sequential(new SuccessBehavior(), new NonSparseBehavior()))
            {
                // Set compressed hash type to Unknown so that it is not populated
                // to simulate old VHDs without compressed hash
                CompressedHashType = HashType.Unknown,
                VerifyManifest = manifestText =>
                {
                    // Verify manifest does not contain compressed hash entries matching old behavior
                    manifestText.ShouldNotContain(nameof(Block.CompressedHash), Case.Insensitive);
                }
            };

            await test.ExecuteRoundtripTestAsync();
        }

        [TestMethod]
        [DataRow(true)]
        [DataRow(false)]
        public async Task TransientHashMismatchTestAsync(bool compressible)
        {
            int blocks = 1;
            var test = new RoundtripTest(
                compressible ? new HighlyCompressibleContentGenerator(blocks) : new UncompressibleContentGenerator(blocks),
                Compound.Sequential(new SuccessBehavior(), new NonSparseBehavior(), new DownloadMustRetryBehavior(null)));

            int retryCount = 1;
            test.OnBeforeWriteContent += (range, content) =>
            {
                // Ensure requested range contains byte 2 to avoid initial metadata/manifest requests.
                if (range.Start <= 2 && range.End > 2)
                {
                    if (Interlocked.Decrement(ref retryCount) >= 0)
                    {
                        content.Span[0] = (byte)((content.Span[0] / 4) + 7);
                    }
                }
            };

            await test.ExecuteRoundtripTestAsync();
        }

        internal sealed class ThrottlingTest : RoundtripTest
        {
            private readonly IRateLimiter _rateLimiter;

            public ThrottlingTest(IContentGenerator contentGenerator, IRoundtripBehavior behavior, SpeedRateLimiter speedRateLimiter)
                : base(contentGenerator, behavior)
            {
                _rateLimiter = new Adapter(speedRateLimiter);
            }

            protected override Dictionary<string, IHttpHandler> SetupHttpServerHandlers(UploadArguments upload)
            {
                return new Dictionary<string, IHttpHandler>
                {
                    [SelectHttpHandler()] = new HttpDiskFileHandler(upload.Uri.LocalPath, _rateLimiter),
                };
            }

            private sealed class Adapter : IRateLimiter
            {
                private readonly SpeedRateLimiter _speedRateLimiter;

                public Adapter(SpeedRateLimiter speedRateLimiter)
                {
                    _speedRateLimiter = speedRateLimiter;
                }

                public bool TryAcquire(long bytes)
                {
                    return _speedRateLimiter.TryAcquire(bytes);
                }
            }
        }

        // TODO: successful test

        [TestMethod]
        [DataRow(16)]
        public async Task FailureThrottledRoundtripTestAsync(double blockSizeMultiple)
        {
            // This test throttles downloads to a point that they are bound to fail.
            var blockSize = 1 * 1024 * 1024;
            var speedRateLimiter = new SpeedRateLimiter(refillPerPeriod: (int)Math.Ceiling(0.5 * blockSize), period: TimeSpan.FromSeconds(1));
            var test = new ThrottlingTest(
                new UncompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new NonSparseBehavior(), new DownloadFailureBehavior(), new DownloadMustRetryBehavior(HttpStatusCode.ServiceUnavailable)),
                speedRateLimiter);
            await test.ExecuteRoundtripTestAsync(blockSize: (uint)blockSize);
        }

        internal sealed class ChaosTest : RoundtripTest
        {
            private readonly IChaos _chaos;

            public ChaosTest(IContentGenerator contentGenerator, IRoundtripBehavior behavior, IChaos chaos)
                : base(contentGenerator, behavior)
            {
                _chaos = chaos;
            }

            protected override Dictionary<string, IHttpHandler> SetupHttpServerHandlers(UploadArguments upload)
            {
                return new Dictionary<string, IHttpHandler>
                {
                    [SelectHttpHandler()] = new HttpDiskFileHandler(upload.Uri.LocalPath, chaos: _chaos),
                };
            }
        }

        internal sealed class StatusCodeChaosGenerator : IChaos
        {
            private readonly double _probability;
            private readonly HttpStatusCode _status;
            private int _skip;
            private int _guarantee;

            public StatusCodeChaosGenerator(double probability, HttpStatusCode status, int skip = 0, int guarantee = 0)
            {
                Contract.Requires(probability > 0.0 && probability <= 1.0);
                Contract.Requires(skip >= 0);
                _probability = probability;
                _status = status;
                _skip = skip;
                _guarantee = guarantee;
            }

            public Task<bool> MaybeChaosAsync(HttpListenerContext context)
            {
                if (_skip > 0)
                {
                    var r = Interlocked.Decrement(ref _skip);

                    if (r > 0)
                    {
                        return Task.FromResult(false);
                    }
                }

                if (_guarantee > 0)
                {
                    var r = Interlocked.Decrement(ref _guarantee);
                    if (r > 0)
                    {
                        return respond(context);
                    }
                }

                var coin = Random.Shared.NextDouble();
                if (coin > _probability)
                {
                    return Task.FromResult(false);
                }

                return respond(context);

                Task<bool> respond(HttpListenerContext context)
                {
                    context.Response.StatusCode = (int)_status;
                    context.Response.Close();
                    return Task.FromResult(true);
                }
            }
        }

        [TestMethod]
        [DataRow(256)]
        public async Task RetryOnServiceUnavailableTestAsync(double blockSizeMultiple)
        {
            var blockSize = 1 * 1024 * 1024;
            var test = new ChaosTest(
                new UncompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new NonSparseBehavior(), new SuccessBehavior(), new DownloadMustRetryBehavior(HttpStatusCode.ServiceUnavailable)),
                new StatusCodeChaosGenerator(
                    probability: 0.1,
                    HttpStatusCode.ServiceUnavailable,
                    // The first 4 requests are part of loading the file's metadata. These aren't retried on ServiceUnavailable on purpose.
                    skip: 4,
                    guarantee: 1));
            await test.ExecuteRoundtripTestAsync(blockSize: (uint)blockSize);
        }

        [TestMethod]
        [DataRow(64)]
        public async Task AbortOnNotFoundTestAsync(double blockSizeMultiple)
        {
            var blockSize = 1 * 1024 * 1024;
            var test = new ChaosTest(
                new UncompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new DownloadFailureBehavior(ReturnCode.NotFound)),
                new StatusCodeChaosGenerator(probability: 1.0, HttpStatusCode.NotFound));
            await test.ExecuteRoundtripTestAsync(blockSize: (uint)blockSize);
        }

        [TestMethod]
        [DataRow(64)]
        public async Task AbortOn400TestAsync(double blockSizeMultiple)
        {
            var blockSize = 1 * 1024 * 1024;
            var test = new ChaosTest(
                new UncompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new DownloadFailureBehavior(ReturnCode.Throttled)),
                new StatusCodeChaosGenerator(probability: 1.0, HttpStatusCode.TooManyRequests));
            await test.ExecuteRoundtripTestAsync(blockSize: (uint)blockSize);
        }

        internal sealed class ShortDownloadExecutionTimeoutBehavior : IDownloadBehavior
        {
            public Task BeforeAsync(DownloadArguments arguments)
            {
                arguments.ExecutionTimeoutMinutes = 0.0001;
                return Task.CompletedTask;
            }

            public Task AfterAsync(DownloadOutput result)
            {
                Assert.AreEqual(ReturnCode.Timeout, result.ReturnCode);
                return Task.CompletedTask;
            }
        }

        [TestMethod]
        [DataRow(16)]
        public async Task TimeoutExceptionTestAsync(double blockSizeMultiple)
        {
            var blockSize = 1 * 1024 * 1024;
            var test = new RoundtripTest(
                new UncompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new NonSparseBehavior(), new ShortDownloadExecutionTimeoutBehavior()));
            await test.ExecuteRoundtripTestAsync(blockSize: (uint)blockSize);
        }

        internal sealed class ShortDownloadChunkTimeoutBehavior : IDownloadBehavior
        {
            public Task BeforeAsync(DownloadArguments arguments)
            {
                arguments.ChunkDownloadTimeoutMinutes = 0.0001;
                return Task.CompletedTask;
            }

            public Task AfterAsync(DownloadOutput result)
            {
                Assert.AreEqual(ReturnCode.Timeout, result.ReturnCode);
                Assert.IsTrue(result.Statistics.Read(Counters.ChunkDownloadTimeout) > 0);
                return Task.CompletedTask;
            }
        }

        [TestMethod]
        [DataRow(16)]
        public async Task ChunkTimeoutExceptionTestAsync(double blockSizeMultiple)
        {
            var blockSize = 1 * 1024 * 1024;
            var test = new RoundtripTest(
                new UncompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new NonSparseBehavior(), new ShortDownloadChunkTimeoutBehavior()));
            await test.ExecuteRoundtripTestAsync(blockSize: (uint)blockSize);
        }
    }
}
