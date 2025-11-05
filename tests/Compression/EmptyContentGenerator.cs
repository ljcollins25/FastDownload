// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Download;
using FastDownload.Upload;

namespace FastDownload.Tests.Roundtrip
{
    internal sealed class EmptyContentGenerator : IContentGenerator
    {
        public byte[] GenerateContent(UploadArguments upload, DownloadArguments download)
        {
            return Array.Empty<byte>();
        }
    }
}
