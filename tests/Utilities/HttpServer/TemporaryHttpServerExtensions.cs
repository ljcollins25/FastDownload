// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Net;

namespace FastDownload.Tests.Utilities.HttpServer
{
    public interface IHttpHandler : IAsyncDisposable
    {
        Task HandleRequestAsync(HttpListenerContext context);
    }

    public static class TemporaryHttpServerExtensions
    {
        public static async Task WithTemporaryHttpServerAsync(this IDictionary<string, IHttpHandler> handlers, Func<Uri, Task> action)
        {
            try
            {
                await using var server = TemporaryHttpServer.Start(async context =>
                {
                    string path = context.Request.Url!.AbsolutePath.TrimStart('/'); // Normalize path
                    if (!handlers.TryGetValue(path, out var handler))
                    {
                        // Set 404 response directly on the context
                        context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                        context.Response.Close();
                        return;
                    }

                    try
                    {
                        // Invoke the custom handler for the path
                        await handler.HandleRequestAsync(context);
                    }
                    catch (Exception)
                    {
                        // Set 500 response directly on the context
                        context.Response.StatusCode = (int)HttpStatusCode.InternalServerError;
                        context.Response.Close();
                    }
                });

                await action(server.Url);
            }
            finally
            {
                foreach (var handler in handlers.Values)
                {
                    await handler.DisposeAsync();
                }
            }
        }
    }
}
