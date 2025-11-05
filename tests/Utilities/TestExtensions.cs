// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Cache.ContentStore.Hashing;
using BuildXL.Cache.ContentStore.Interfaces.Extensions;
using BuildXL.Utilities;
using FastDownload.Utilities;
using Shouldly;

namespace FastDownload.Tests.Utilities
{
    public static class TestExtensions
    {
        public static async Task WriteAsync(
            string path,
            IEnumerable<(long Offset, ReadOnlyMemory<byte> Content)> contentRegions,
            bool sparse,
            AsyncOut<long>? writtenLength = null)
        {
            int contentRegionCount = 0;
            writtenLength ??= new();
            await using (var outputStream = File.OpenWrite(path))
            {
                if (sparse)
                {
                    WindowsNativeMethods.SetSparseFlag(outputStream, true).ShouldBe(true);
                }

                foreach (var region in contentRegions)
                {
                    contentRegionCount++;
                    outputStream.Seek(region.Offset, SeekOrigin.Begin);

                    Interlocked.Add(ref writtenLength.Value, region.Content.Length);
                    await outputStream.WriteAsync(region.Content);
                }
            }

            if (sparse && contentRegionCount > 1)
            {
                await using var readStream = File.OpenRead(path);
                var regions = WindowsNativeMethods.GetDataRegions(readStream);
                regions.Count.ShouldBeGreaterThan(1);
            }
        }

        public static async Task<ContentHash?> TryHashFileAsync(string path, HashType hashType)
        {
            if (!File.Exists(path))
            {
                return null;
            }

            var contentHasher = HashInfoLookup.GetContentHasher(hashType);
            await using (var stream = File.OpenRead(path))
            await using (var hashingStream = contentHasher.CreateReadHashingStream(stream))
            {
                var buffer = new byte[Math.Min(stream.Length, 128 << 20)];
                await stream.CopyToWithFullBufferAsync(Stream.Null, buffer);
                return await hashingStream.GetContentHashAsync();
            }
        }

        public static void ForEach<T>(this IEnumerable<T> items, Action<T, int> action)
        {
            int index = 0;
            foreach (var item in items)
            {
                action(item, index++);
            }
        }

        public static byte? ByteOrDefault(this Memory<byte> memory, int index) => index < memory.Length ? memory.Span[index] : null;

        public static string DisplayByteAt(this Memory<byte> memory, int index) => memory.ByteOrDefault(index) is byte b ? $"{b:x2}" : "missing";
    }
}
