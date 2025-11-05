// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Utilities.Core.Tasks;
using FastDownload.Download;
using FastDownload.Shared;
using FastDownload.Tests.Azurite;
using FastDownload.Upload;
using FastDownload.Utilities;
using Shouldly;
using static FastDownload.Utilities.FileUtilities;

namespace FastDownload.Tests
{
    [TestClass]
    public class ChunkServerTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        [DataRow("http", true)]
        [DataRow("http", false)]
        [DataRow("https", true)]
        [DataRow("https", false)]
        [Timeout(60_000)]
        public async Task BasicTest(string scheme, bool useHttp2)
        {
            uint chunkSize = 1 << 20;
            using var host = new TestChunkHost(Path.Combine(TestContext.TestRunDirectory, "test.bin"), chunkSize: chunkSize, fileSize: (1 << 21) + 443)
            {
                MachineInfo = new ProxyNodeEntry(new Uri($"{scheme}://localhost:{PortExtensions.GetNextAvailablePort()}"))
                {
                    FileChecksum = Guid.NewGuid().ToString(),
                    Zone = "1"
                }
            };

            await using var chunkServer = new ChunkServer(host, useHttp2: useHttp2)
            {
                TrackMemory = true
            };

            await chunkServer.StartAsync();

            var chunk0Data = new byte[chunkSize];
            var chunk1Data = new byte[chunkSize];

            Random.Shared.NextBytes(chunk0Data);
            Random.Shared.NextBytes(chunk1Data);

            for (int i = 0; i < 2; i++)
            {
                var getChunk0 = host.QueryChunkAsync(chunkServer, 0);
                var getChunk1 = host.QueryChunkAsync(chunkServer, 1);

                if (i == 0)
                {
                    getChunk1.IsCompleted.ShouldBeFalse();

                    host.AddChunk(1, stream => stream.Write(chunk1Data));
                }

                var chunk1 = await getChunk1;
                chunk1.AsSpan().SequenceEqual(chunk1Data).ShouldBeTrue();
                chunk1.Length.ShouldBe((int)chunkSize);

                if (i == 0)
                {
                    getChunk0.IsCompleted.ShouldBeFalse();

                    host.AddChunk(0, stream => stream.Write(chunk0Data));
                }

                var chunk0 = await getChunk0;
                chunk0.AsSpan().SequenceEqual(chunk0Data).ShouldBeTrue();
                chunk0.Length.ShouldBe((int)chunkSize);
            }
        }

        private static List<WorkItem> GetWork(ChunkingScheme scheme)
        {
            return scheme.Chunks.Select((c, index) =>
            {
                return new WorkItem(index, c.Chunk, c.Chunk, null, CompressionAlgorithm.None, CompressedHash: null, c.SparseRegions);
            })
            .ToList();
        }

        internal sealed class TestChunkHost(string filePath, uint chunkSize, long fileSize)
            : ChunkHost(filePath, fileSize, GetWork(ChunkingScheme.Create(fileSize, 2, chunkSize)), chunkSize, 100 * chunkSize), IDisposable
        {
            public HttpClient Client { get; } = new HttpClientSettings(default).CreateHttpClient();

            public uint Alignment { get; } = 1 << 12;

            public async Task<byte[]> QueryChunkAsync(ChunkServer server, int chunkIndex, CompressionAlgorithm encoding = CompressionAlgorithm.None)
            {
                var client = Client;
                var chunkStart = chunkIndex * ChunkSize;
                var chunk = new Chunk(chunkStart, chunkStart + GetChunkLength(chunkStart, FileSize, ChunkSize));
                var args = new RangeDownloadArguments()
                {
                    ProxyArguments = new ProxyRangeDownloadArguments()
                    {
                        ChunkIndex = chunkIndex,
                        CompressionEncoding = encoding,
                        DestinationChunk = chunk,
                        Requestor = MachineInfo.Uri.ToString(),
                        ExpectedCertificateCertHash = server.CertHash
                    }
                };

                var response = await client.RangeDownloadAsync(server.EndpointUri, chunk, args);
                response.EnsureSuccessStatusCode();

                return await response.Content.ReadAsByteArrayAsync();
            }

            public void AddChunk(long chunkIndex, Action<Stream> writeBytes, CompressionAlgorithm encoding = CompressionAlgorithm.None)
            {
                var chunkStart = chunkIndex * ChunkSize;
                var buffer = AlignedManagedBuffer.Allocate(ChunkSize, Alignment);
                buffer.ResetRefCount(buffer.Dispose);

                var memoryStream = buffer.CreateMemoryStream();

                writeBytes(memoryStream);

                PutBuffer(Entries[chunkIndex].WorkItem, buffer, encoding);
            }
        }

    }
}
