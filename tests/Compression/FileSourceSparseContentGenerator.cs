// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Text.Json;
using FastDownload.Download;
using FastDownload.Shared.Manifest;
using FastDownload.Upload;
using FastDownload.Utilities;

namespace FastDownload.Tests.Roundtrip
{
    /// <summary>
    /// Generates sparse content regions for tests
    /// </summary>
    internal sealed class FileSourceSparseContentGenerator(string path, string regionsFilePath, bool contiguousRegions, long maxLength = long.MaxValue) : ISparseContentGenerator
    {
        public Slice[] Regions { get; } = JsonSerializer.Deserialize(File.ReadAllText(regionsFilePath), Shared.Manifest.V0.SourceGenerationContext.Default.SliceArray)!;

        public IEnumerable<(long Offset, ReadOnlyMemory<byte> Content)> GenerateContentRegions(UploadArguments upload, DownloadArguments download)
        {
            long readBytes = 0;
            using var fileStream = File.OpenRead(path);
            var maxResultFileLength = Math.Min(maxLength, fileStream.Length);
            byte[] buffer = new byte[128 << 20];
            var alignment = fileStream.ComputeUnbufferedIOAlignment();

            foreach (var region in Regions)
            {
                if (region.Offset >= maxResultFileLength)
                {
                    yield break;
                }

                // If regions are layed out in file contiguously, start where we left off.
                var baseOffset = contiguousRegions ? readBytes : region.Offset;
                fileStream.Position = baseOffset;

                for (long i = 0; i < region.Length; i += buffer.Length)
                {
                    var offset = baseOffset + i;
                    var length = (int)Math.Min(maxResultFileLength - offset, Math.Min(buffer.Length, region.Length - i));
                    if (length == 0)
                    {
                        yield break;
                    }

                    fileStream.ReadExactly(buffer, 0, length);
                    readBytes += length;
                    maxResultFileLength -= length;
                    yield return (offset, buffer.AsMemory(0, length));
                }
            }
        }
    }
}
