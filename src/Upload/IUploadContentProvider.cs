// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Upload
{
    internal interface IUploadContentProvider
    {
        string FullName { get; }

        Stream OpenReadStream();
    }

    internal class FileUploadContentProvider(FileInfo fileInfo) : IUploadContentProvider
    {
        public string FullName => fileInfo.FullName;

        public Stream OpenReadStream() => fileInfo.OpenRead();
    }

    internal class NullUploadContentProvider(string name) : IUploadContentProvider
    {
        public string FullName => name;

        public Stream OpenReadStream()
        {
            throw new NotSupportedException();
        }
    }
}
