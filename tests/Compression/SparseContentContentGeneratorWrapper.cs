// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Download;
using FastDownload.Upload;

namespace FastDownload.Tests.Roundtrip
{
    internal sealed class SparseContentContentGeneratorWrapper(IContentGenerator inner) : ISparseContentGenerator
    {
        public static ISparseContentGenerator Wrap(IContentGenerator inner, bool sparse, out IDownloadBehavior behavior)
        {
            behavior = sparse
                ? new SparseBehavior()
                : new NonSparseBehavior();

            return sparse
                ? new SparseContentContentGeneratorWrapper(inner)
                : inner;
        }

        public IEnumerable<(long Offset, ReadOnlyMemory<byte> Content)> GenerateContentRegions(UploadArguments upload, DownloadArguments download)
        {
            var content = inner.GenerateContent(upload, download);
            var random = new Random(Seed: 12345);

            var remaining = content.AsMemory();
            long lastRegionEndOffset = 0;
            while (remaining.Length > 0)
            {
                var length = Math.Min(remaining.Length, random.Next(1, Math.Max(1, content.Length / 4)));

                var offset = lastRegionEndOffset + random.NextInt64(1, upload.BlockSize * 4);

                yield return (offset, remaining.Slice(0, length));

                remaining = remaining.Slice(length);
                lastRegionEndOffset = offset + length;
            }
        }
    }
}
