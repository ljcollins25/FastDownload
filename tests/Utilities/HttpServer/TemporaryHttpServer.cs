// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Net;
using BuildXL.Utilities.Core.Tasks;
using FastDownload.Tests.Azurite;
using NLog;

namespace FastDownload.Tests.Utilities.HttpServer
{
    public class TemporaryHttpServer : IAsyncDisposable
    {
        private readonly Logger _logger = LogManager.GetCurrentClassLogger();

        private readonly HttpListener _httpListener;
        private readonly CancellationTokenSource _stopSource = new();
        private readonly Task _serverTask;

        public Uri Url { get; }

        private TemporaryHttpServer(string prefix, Func<HttpListenerContext, Task> handler)
        {
            _httpListener = new HttpListener();
            _httpListener.Prefixes.Add(prefix);

            Url = new Uri(prefix);

            _serverTask = Task.Run(async () =>
            {
                _httpListener.Start();
                _logger.Info($"HTTP server started at {Url}");

                try
                {
                    while (!_stopSource.Token.IsCancellationRequested)
                    {
                        try
                        {

                            var contextTask = _httpListener.GetContextAsync();
                            await TaskUtilities.AwaitWithCancellationAsync(contextTask, _stopSource.Token);
                            await handler(await contextTask);
                        }
                        catch (TaskCanceledException) when (_stopSource.Token.IsCancellationRequested)
                        {
                            // Listener closed, exit gracefully
                        }
                        catch (HttpListenerException) when (_stopSource.Token.IsCancellationRequested)
                        {
                            // Listener closed, exit gracefully
                        }
                        catch (Exception ex)
                        {
                            _logger.Info($"[{Url}] Error handling request: {ex.Message}");
                        }
                    }
                }
                finally
                {
                    _httpListener.Stop();
                    _logger.Info($"HTTP server stopped at {Url}");
                }
            });
        }

        public static TemporaryHttpServer Start(Func<HttpListenerContext, Task> handler)
        {
            const int maximumRandomPortRetries = 10;
            int attempts = 0;

            while (attempts < maximumRandomPortRetries)
            {
                var port = PortExtensions.GetNextAvailablePort();
                var url = $"http://localhost:{port}/";

                try
                {
                    return new TemporaryHttpServer(url, handler);
                }
                catch (HttpListenerException ex) when (ex.ErrorCode == 183)
                {
                    attempts++;
                }
            }

            throw new InvalidOperationException("Failed to bind to an available port after multiple attempts.");
        }

        public async ValueTask DisposeAsync()
        {
            _stopSource.Cancel();
            await _serverTask;
            _logger.Info($"Server stopped at {Url}");
        }
    }
}
