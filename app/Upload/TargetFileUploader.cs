// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Upload
{
    internal class TargetFileUploader : IUploadMechanism, IAsyncDisposable
    {
        private readonly FileStream _fileStream;
        private long _offset = 0;

        public TargetFileUploader(string path, bool overwrite)
        {
            var mode = overwrite ? FileMode.Create : FileMode.CreateNew;
            _fileStream = new FileStream(path, mode, FileAccess.Write, FileShare.None);
        }

        public Task DeleteAsync(CancellationToken cancellationToken)
        {
            File.Delete(_fileStream.Name);
            return Task.CompletedTask;
        }

        public async Task<BlockInfo> UploadBlockAsync(BlockUploadData? blockData, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            var offset = Interlocked.Add(ref _offset, content.Length);
            offset -= content.Length;

            await RandomAccess.WriteAsync(_fileStream.SafeFileHandle, content, offset, cancellationToken);
            return new BlockInfo(Id: string.Empty, offset, content.Length);
        }

        public async Task CommitAsync(IEnumerable<BlockInfo> blockIds, CancellationToken cancellationToken)
        {
            await _fileStream.FlushAsync();
        }

        public async ValueTask DisposeAsync()
        {
            await _fileStream.DisposeAsync();
        }
    }
}
