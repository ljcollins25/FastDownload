// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Buffers;
using FastDownload.Shared;
using Microsoft.Win32.SafeHandles;

namespace FastDownload.Tests.Utilities.HttpServer
{
    internal delegate void OnBeforeWriteContent(Chunk chunk, Memory<byte> content);

    public class HttpDiskFileHandler : BaseHttpFileHandler
    {
        private readonly SafeFileHandle _handle;
        private readonly long _length;
        public int BlockSize { get; set; } = 8096;

        internal OnBeforeWriteContent OnBeforeWriteContent;

        public HttpDiskFileHandler(string filePath, IRateLimiter? limiter = null, IChaos? chaos = null)
            : base(limiter, chaos)
        {
            ArgumentNullException.ThrowIfNull(filePath);

            if (!File.Exists(filePath))
            {
                throw new FileNotFoundException("File not found.", filePath);
            }

            _handle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.Asynchronous | FileOptions.RandomAccess);
            _length = RandomAccess.GetLength(_handle);
        }

        public override ValueTask DisposeAsync()
        {
            _handle.Dispose();
            return ValueTask.CompletedTask;
        }

        protected override long FetchContentLength()
        {
            return _length;
        }

        protected override async Task WriteContentAsync(Stream output, long start, long length)
        {
            var pool = ArrayPool<byte>.Shared;
            var buffer = pool.Rent(BlockSize);
            try
            {
                long remaining = length;
                long position = start;

                while (remaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, remaining);
                    int bytesRead = await RandomAccess.ReadAsync(_handle, buffer.AsMemory(0, toRead), position);
                    if (bytesRead <= 0)
                    {
                        break;
                    }

                    Memory<byte> content = buffer.AsMemory(0, bytesRead);
                    OnBeforeWriteContent?.Invoke(new Chunk(start, start + length), content);
                    await output.WriteAsync(content);
                    position += bytesRead;
                    remaining -= bytesRead;
                }

                if (remaining > 0)
                {
                    throw new IOException("Failed to read the entire file.");
                }
            }
            finally
            {
                pool.Return(buffer);
            }
        }
    }
}
