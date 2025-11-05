// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Net;
using System.Text;

namespace FastDownload.Tests.Utilities.HttpServer
{
    public interface IChaos
    {
        Task<bool> MaybeChaosAsync(HttpListenerContext context);
    }

    public interface IRateLimiter
    {
        bool TryAcquire(long bytes);
    }

    public abstract class BaseHttpFileHandler : IHttpHandler
    {
        private readonly IRateLimiter? _limiter;

        private readonly IChaos? _chaos;

        protected BaseHttpFileHandler(IRateLimiter? limiter, IChaos? chaos)
        {
            _limiter = limiter;
            _chaos = chaos;
        }

        public abstract ValueTask DisposeAsync();

        protected abstract long FetchContentLength();

        protected abstract Task WriteContentAsync(Stream output, long start, long length);

        public async Task HandleRequestAsync(HttpListenerContext context)
        {
            if (_chaos != null && await _chaos.MaybeChaosAsync(context))
            {
                return;
            }

            var response = context.Response;

            try
            {
                var fileLength = FetchContentLength();
                var method = context.Request.HttpMethod.ToUpperInvariant();

                if (method == "GET")
                {
                    if (context.Request.Headers["Range"] is string rangeHeader)
                    {
                        if (!rangeHeader.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase))
                        {
                            response.StatusCode = 416; // Range Not Satisfiable
                            response.OutputStream.Close();
                            return;
                        }

                        var rangeParts = rangeHeader.Substring(6).Split('-');
                        if (!long.TryParse(rangeParts[0], out long start) || start < 0 || start >= fileLength)
                        {
                            response.StatusCode = 416; // Range Not Satisfiable
                            response.OutputStream.Close();
                            return;
                        }

                        long end = (rangeParts.Length > 1 && long.TryParse(rangeParts[1], out long parsedEnd))
                            ? Math.Min(parsedEnd, fileLength - 1)
                            : fileLength - 1;

                        if (end < start)
                        {
                            response.StatusCode = 416; // Range Not Satisfiable
                            response.OutputStream.Close();
                            return;
                        }

                        long length = end - start + 1;

                        // Check rate limiter
                        if (_limiter != null && !_limiter.TryAcquire(length))
                        {
                            response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                            var errorBytes = Encoding.UTF8.GetBytes("Rate limit exceeded.");
                            response.ContentLength64 = errorBytes.Length;
                            await response.OutputStream.WriteAsync(errorBytes);
                            response.OutputStream.Close();
                            return;
                        }

                        // Serve partial content
                        response.StatusCode = 206; // Partial Content
                        response.ContentType = "application/octet-stream";
                        response.ContentLength64 = length;
                        response.Headers["Content-Range"] = $"bytes {start}-{end}/{fileLength}";

                        await WriteContentAsync(response.OutputStream, start, length);
                        response.OutputStream.Close();
                    }
                    else
                    {
                        // No range: serve the entire content
                        // Check rate limiter
                        if (_limiter != null && !_limiter.TryAcquire(fileLength))
                        {
                            response.StatusCode = (int)HttpStatusCode.ServiceUnavailable;
                            var errorBytes = Encoding.UTF8.GetBytes("Rate limit exceeded.");
                            response.ContentLength64 = errorBytes.Length;
                            await response.OutputStream.WriteAsync(errorBytes);
                            response.OutputStream.Close();
                            return;
                        }

                        response.ContentType = "application/octet-stream";
                        response.ContentLength64 = fileLength;
                        await WriteContentAsync(response.OutputStream, 0, fileLength);
                        response.OutputStream.Close();
                    }
                }
                else
                {
                    throw new InvalidOperationException($"Unsupported method {method}");
                }
            }
            catch (Exception ex)
            {
                response.StatusCode = 500; // Internal Server Error
                var errorBytes = Encoding.UTF8.GetBytes($"Error: {ex.Message}");
                response.ContentLength64 = errorBytes.Length;
                await response.OutputStream.WriteAsync(errorBytes);
                response.OutputStream.Close();
            }
        }
    }
}
