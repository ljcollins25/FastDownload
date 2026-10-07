// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Cli;
using FastDownload.Download;
using FastDownload.Tests.Utilities;
using FastDownload.Upload;

namespace FastDownload.Tests.Roundtrip
{
    /// <summary>
    /// Regression tests for --skip-zero-regions combined with every sparse handling mode (notably Compact, where one block's
    /// buffer is the concatenation of several sparse regions) and --dedude-download-chunks.
    /// The content has data regions that straddle block boundaries, zero runs (leading, trailing and inside regions),
    /// regions consisting only of zeros, holes, and a region at the very end of the file.
    /// </summary>
    [TestClass]
    public class SkipZeroSparseRoundtripTests : TestOutputTestBase
    {
        private const int Page = 4096;

        internal sealed class ZeroPatternGenerator(int pageCount) : ISparseContentGenerator
        {
            public IEnumerable<(long Offset, ReadOnlyMemory<byte> Content)> GenerateContentRegions(UploadArguments upload, DownloadArguments download)
            {
                long block = upload.BlockSize;
                var random = new Random(4242);
                byte[] Data(long pages)
                {
                    var b = new byte[pages * Page];
                    random.NextBytes(b);
                    for (int i = 0; i < b.Length; i++) { if (b[i] == 0) { b[i] = 1; } }
                    return b;
                }

                byte[] DataWithZeros(long pages, long zeroStart, long zeroPages)
                {
                    var b = Data(pages);
                    Array.Clear(b, (int)(zeroStart * Page), (int)(zeroPages * Page));
                    return b;
                }

                long pagesPerBlock = block / Page;

                // block 0: leading zeros (region starts with 3 zero pages), data, trailing zeros (region ends with 2 zero pages)
                yield return (0, DataWithZeros(10, 0, 3).AsMemory().Slice(0, 8 * Page).ToArray().Concat(new byte[2 * Page]).ToArray());
                // straddles the block 0 / block 1 boundary
                yield return (block - 2 * Page, Data(4));
                // block 1: zero run inside a data region, followed by data
                yield return (block + (pagesPerBlock / 4) * Page, DataWithZeros(14, 0, 10));
                // block 2: region of only zeros (allocated, but all zero)
                yield return (2 * block, new byte[block]);
                // block 3: two small regions
                yield return (3 * block + 7L * Page, Data(1));
                // (20 pages is past the end of a 64 KiB block and would fill the block 4 hole)
                yield return (3 * block + Math.Min(20L, pagesPerBlock - 2) * Page, Data(1));
                // block 4: hole. block 5: whole block of data
                yield return (5 * block, Data(pagesPerBlock));
                // starts mid block 6 and straddles into block 7
                yield return (6 * block + block / 2, Data(pagesPerBlock / 2 + 3));
                // block 8: data, zero page, data in one region
                yield return (8 * block + (pagesPerBlock / 3) * Page, DataWithZeros(3, 1, 1));
                // single data pages in blocks 9 and 10 (not at the start of their block) and a final region with trailing zeros:
                // with Compact these regions share one buffer whose trimmed range only covers the first of them
                yield return (9 * block + 5L * Page, Data(1));
                yield return (10 * block + 5L * Page, Data(1));
                yield return (12 * block - 5L * Page, Data(1).Concat(new byte[4 * Page]).ToArray());
            }
        }

        /// <summary>Compares the downloaded file with the generated input byte by byte (page granular, for a readable failure).</summary>
        internal sealed class ByteCompareBehavior : IRoundtripBehavior
        {
            public Task AfterRoundtripAsync(RoundtripOutput result)
            {
                var expected = File.ReadAllBytes(result.Upload.Arguments.Path);
                var actual = File.ReadAllBytes(result.Download.Arguments.Path);
                Assert.AreEqual(expected.Length, actual.Length, "length");
                for (int i = 0; i < expected.Length; i += Page)
                {
                    int len = Math.Min(Page, expected.Length - i);
                    if (!expected.AsSpan(i, len).SequenceEqual(actual.AsSpan(i, len)))
                    {
                        Assert.Fail($"Downloaded file differs from input in page {i / Page} (offset {i})");
                    }
                }

                return Task.CompletedTask;
            }
        }

        [TestMethod]
        [DataRow(SparseHandlingMode.None, false, false)]
        [DataRow(SparseHandlingMode.None, true, false)]
        [DataRow(SparseHandlingMode.None, false, true)]
        [DataRow(SparseHandlingMode.None, true, true)]
        [DataRow(SparseHandlingMode.SkipHoles, false, false)]
        [DataRow(SparseHandlingMode.SkipHoles, true, false)]
        [DataRow(SparseHandlingMode.SkipHoles, false, true)]
        [DataRow(SparseHandlingMode.SkipHoles, true, true)]
        [DataRow(SparseHandlingMode.Compact, false, false)]
        [DataRow(SparseHandlingMode.Compact, true, false)]
        [DataRow(SparseHandlingMode.Compact, false, true)]
        [DataRow(SparseHandlingMode.Compact, true, true)]
        public async Task SkipZeroRegionsRoundtripAsync(SparseHandlingMode mode, bool skipZeroRegions, bool dedupDownloadChunks)
        {
            foreach (uint blockSize in new uint[] { 64 * 1024, 256 * 1024, 1024 * 1024 })
            {
                var test = new RoundtripTest(
                    new ZeroPatternGenerator(0),
                    Compound.Sequential(
                        new SuccessBehavior(),
                        new ByteCompareBehavior(),
                        new ConfigureBehavior(
                            configureUpload: upload => upload.SparseHandling = mode,
                            configureDownload: download =>
                            {
                                download.SkipZeroRegions = skipZeroRegions;
                                download.DedupeDownloadChunks = dedupDownloadChunks;
                            })));

                // The roundtrip test hashes the downloaded file and compares it with the hash of the generated input
                await test.ExecuteRoundtripTestAsync(blockSize: blockSize);
            }
        }

        /// <summary>A fixed-VHD-like image: data, a large zero middle, and a 512 byte footer so the file length is not a multiple of 4 KiB.</summary>
        internal sealed class VhdFooterGenerator : ISparseContentGenerator
        {
            public IEnumerable<(long Offset, ReadOnlyMemory<byte> Content)> GenerateContentRegions(UploadArguments upload, DownloadArguments download)
            {
                long block = upload.BlockSize;
                var random = new Random(77);
                byte[] Data(int bytes) { var b = new byte[bytes]; random.NextBytes(b); for (int i = 0; i < b.Length; i++) { if (b[i] == 0) { b[i] = 1; } } return b; }
                yield return (0, Data(3 * Page));
                yield return (3 * block + 9L * Page, Data(2 * Page));
                // footer: 512 bytes at an offset that is a multiple of 512 but not of 4096
                yield return (7 * block + 512 * 5, Data(512));
            }
        }

        /// <summary>Many 64 KiB granular regions (NTFS allocation unit), some with zero tails, spread over a few hundred MiB, ending with a 512 byte footer.</summary>
        internal sealed class DenseVhdLikeGenerator : ISparseContentGenerator
        {
            public IEnumerable<(long Offset, ReadOnlyMemory<byte> Content)> GenerateContentRegions(UploadArguments upload, DownloadArguments download)
            {
                var random = new Random(99);
                const int unit = 64 * 1024;
                long offset = 0;
                for (int i = 0; i < 700; i++)
                {
                    var b = new byte[unit * (1 + random.Next(4))];
                    random.NextBytes(b);
                    for (int j = 0; j < b.Length; j++) { if (b[j] == 0) { b[j] = 1; } }
                    if (i % 3 == 0) { Array.Clear(b, b.Length / 2, b.Length / 2); }
                    if (i % 7 == 0) { Array.Clear(b, 0, unit); }
                    yield return (offset, b);
                    offset += b.Length + unit * random.Next(0, 40);
                }

                var footer = new byte[512];
                random.NextBytes(footer);
                yield return (offset + 512, footer);
            }
        }

        [TestMethod]
        [DataRow(SparseHandlingMode.Compact, 128u << 20)]
        [DataRow(SparseHandlingMode.Compact, 8u << 20)]
        [DataRow(SparseHandlingMode.SkipHoles, 128u << 20)]
        public async Task DenseVhdLikeSkipZeroRoundtripAsync(SparseHandlingMode mode, uint blockSize)
        {
            var test = new RoundtripTest(
                new DenseVhdLikeGenerator(),
                Compound.Sequential(
                    new SuccessBehavior(),
                    new ByteCompareBehavior(),
                    new ConfigureBehavior(
                        configureUpload: upload => upload.SparseHandling = mode,
                        configureDownload: download => download.SkipZeroRegions = true)));
            await test.ExecuteRoundtripTestAsync(blockSize: blockSize);
        }

        [TestMethod]
        [DataRow(SparseHandlingMode.SkipHoles)]
        [DataRow(SparseHandlingMode.Compact)]
        public async Task UnalignedTailSkipZeroRoundtripAsync(SparseHandlingMode mode)
        {
            foreach (uint blockSize in new uint[] { 64 * 1024, 1024 * 1024 })
            {
                var test = new RoundtripTest(
                    new VhdFooterGenerator(),
                    Compound.Sequential(
                        new SuccessBehavior(),
                        new ByteCompareBehavior(),
                        new ConfigureBehavior(
                            configureUpload: upload => upload.SparseHandling = mode,
                            configureDownload: download => download.SkipZeroRegions = true)));
                await test.ExecuteRoundtripTestAsync(blockSize: blockSize);
            }
        }
    }
}
