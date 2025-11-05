// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Tests.Utilities.HttpServer
{
    public class HttpMemoryFileHandler : BaseHttpFileHandler
    {
        private readonly ReadOnlyMemory<byte> _content;

        public HttpMemoryFileHandler(ReadOnlyMemory<byte> content, IRateLimiter? limiter = null, IChaos? chaos = null)
            : base(limiter, chaos)
        {
            if (content.Length == 0)
            {
                throw new ArgumentException("Content cannot be empty.", nameof(content));
            }

            _content = content;
        }

        public override ValueTask DisposeAsync()
        {
            return ValueTask.CompletedTask;
        }

        protected override long FetchContentLength()
        {
            return _content.Length;
        }

        protected override async Task WriteContentAsync(Stream output, long start, long length)
        {
            var slice = _content.Slice((int)start, (int)length);
            await output.WriteAsync(slice);
        }
    }
}
