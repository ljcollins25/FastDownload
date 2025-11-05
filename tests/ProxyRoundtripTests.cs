// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Cache.ContentStore.Hashing;
using FastDownload.Cli;
using FastDownload.Download;
using FastDownload.Tests.Azurite;
using FastDownload.Tests.Roundtrip;
using FastDownload.Tests.Utilities;
using FastDownload.Upload;

namespace FastDownload.Tests
{
    [TestClass]
    public class ProxyRoundtripTests : TestOutputTestBase
    {
        [TestMethod]
        [DataRow(0.5)]
        [DataRow(1.0)]
        [DataRow(2.0)]
        [DataRow(5.3)]
        [DataRow(25)]
        [Timeout(60_000)]
        public async Task SuccessUncompressibleRoundtripTestAsync(double blockSizeMultiple)
        {
            var test = new StorageRoundtripTest(
                TestContext,
                new UncompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new SuccessBehavior(), new Uncompressible(), new NonSparseBehavior()));
            await test.ExecuteRoundtripTestAsync();
        }

        [TestMethod]
        [DataRow(0.5)]
        [DataRow(1.0)]
        [DataRow(2.0)]
        [DataRow(5.3)]
        [DataRow(25)]
        [Timeout(60_000)]
        public async Task SuccessCompressibleRoundtripTestAsync(double blockSizeMultiple)
        {
            var test = new StorageRoundtripTest(
                TestContext,
                new HighlyCompressibleContentGenerator(blockSizeMultiple),
                Compound.Sequential(new SuccessBehavior(), new NonSparseBehavior()));
            await test.ExecuteRoundtripTestAsync();
        }

        private sealed class StorageRoundtripTest(
            TestContext testContext,
            IContentGenerator contentGenerator,
            IRoundtripBehavior behavior,
            int machineCount = 3)
            : RoundtripTest(contentGenerator, behavior)
        {
            public Uri ContentUri { get; set; }
            public Uri ProxyChainUri { get; set; }

            public DownloadOutput[] Results { get; } = new DownloadOutput[machineCount];

            public override async Task ExecuteRoundtripTestAsync(HashType hashType = HashType.SHA256, uint blockSize = 1048576)
            {
                using var storage = await AzuriteStorageProcess.CreateAndStartAsync(testContext);
                var container = storage.GetContainer("testcontainer");
                await container.CreateIfNotExistsAsync();

                ContentUri = container.GetBlobClient("test.blob").Uri;
                ProxyChainUri = container.GetBlobClient("proxychain").Uri;

                await base.ExecuteRoundtripTestAsync(hashType, blockSize);
            }

            protected override Task<ReturnCode> ExecuteUploadAsync(UploadArguments upload)
            {
                upload.Uri = ContentUri;
                return base.ExecuteUploadAsync(upload);
            }

            protected override async Task<DownloadOutput> ExecuteDownloadWithServiceAsync(UploadArguments upload, DownloadArguments download)
            {
                download.Uri = ContentUri;

                await Parallel.ForAsync(0, machineCount, async (index, token) =>
                {
                    var download_Inner = download with
                    {
                        Statistics = new(),
                        Port = (ushort)PortExtensions.GetNextAvailablePort(),
                        GlobalDownloadSemaphoreFolderUri = ProxyChainUri,
                        ProxyBufferSizeMb = 100,
                        SemaphoreRecheckSeconds = 5,
                        SemaphoreKeepAliveSeconds = 30,
                        Path = $"{download.Path}.{index}.bin"
                    };

                    var result = await ExecuteDownloadAsync(upload, download_Inner);
                    Results[index] = result;

                    var stats = download_Inner.Statistics;
                    // Console.WriteLine($"Download job {index} statistics: {stats.GetStatisticsReport(indented: true)}");
                    await AfterDownloadAsync(result);
                });

                return Results[0];
            }
        }
    }
}
