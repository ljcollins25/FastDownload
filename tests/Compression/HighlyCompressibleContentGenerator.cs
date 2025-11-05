// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using FastDownload.Download;
using FastDownload.Upload;

namespace FastDownload.Tests.Roundtrip
{
    internal sealed class HighlyCompressibleContentGenerator : IContentGenerator
    {
        private readonly double _blockSizeMultiple;

        public HighlyCompressibleContentGenerator(double blockSizeMultiple)
        {
            Contract.Assert(blockSizeMultiple > 0);
            _blockSizeMultiple = blockSizeMultiple;
        }

        public byte[] GenerateContent(UploadArguments upload, DownloadArguments download)
        {
            uint contentLength = (uint)Math.Ceiling(_blockSizeMultiple * upload.BlockSize);
            byte[] content = new byte[contentLength];
            for (int i = 0; i < content.Length; i++)
            {
                content[i] = (byte)(i % 256);
            }

            return content;
        }
    }
}
