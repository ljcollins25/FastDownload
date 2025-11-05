// Copyright (C) Microsoft Corporation. All Rights Reserved.

#nullable enable

using System.Diagnostics.ContractsLight;
using System.Net;
using FastDownload.Shared;
using FastDownload.Shared.Manifest;
using FastDownload.Shared.Manifest.V0;
using FastDownload.Utilities;
using NLog;
using Polly;
using Polly.Timeout;

namespace FastDownload.Download;

#pragma warning disable CA1859

internal readonly record struct RemoteFileMetadata(long FileSizeBytes, string? ETag, Manifest? Manifest)
{
    private static readonly Logger Logger = LogManager.GetLogger(nameof(RemoteFileMetadata));

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromMinutes(5);

    private static readonly IAsyncPolicy RetryPolicy = CreateRetryPolicyAsync();

    private static IAsyncPolicy CreateRetryPolicyAsync()
    {
        var retryPolicy = Policy
            .Handle<HttpRequestException>(exception =>
            {
                // Don't retry on authentication errors.
                if (exception.HttpRequestError is HttpRequestError.UserAuthenticationError)
                {
                    return false;
                }

                return
                    // Don't retry permanent errors.
                    exception.StatusCode is not HttpStatusCode.NotFound
                    and not HttpStatusCode.Forbidden
                    and not HttpStatusCode.Unauthorized
                    // Don't retry on rate limiting. This likely means there's some form of ongoing incident and we'll
                    // make it worse if we start downloading.
                    and not HttpStatusCode.TooManyRequests
                    and not HttpStatusCode.ServiceUnavailable
                    and not HttpStatusCode.InternalServerError
                    ;
            })
            .WaitAndRetryAsync(
                retryCount: 5,
                sleepDurationProvider: retryAttempt => TimeSpan.FromSeconds(2),
                onRetry: (exception, timeSpan, retryCount, context) =>
                {
                    Logger.ForErrorEvent()
                        .Message(
                            "Attempt {RetryCount} failed. Retrying in {RetryDelay}",
                            retryCount,
                            timeSpan)
                        .Exception(exception)
                        .Log();
                });

        return retryPolicy;
    }

    public static async Task<RemoteFileMetadata> FetchAsync(Uri uri, HttpClientSettings httpClientSettings, CancellationToken cancellationToken)
    {
        try
        {
            // This is an extremely long timeout relative to the operation we're doing (a few HTTP requests with
            // very low response payload size). If it takes this long to do this, something is very wrong and we
            // should just fail the download.
            var timeoutPolicy = Policy.TimeoutAsync(
                FetchTimeout,
                // All code underneath obeys cancellation tokens, so we don't need to use a Pesimistic strategy.
                TimeoutStrategy.Optimistic);

            return await timeoutPolicy.ExecuteAsync(
                async (cancellationToken) =>
                {
                    return await FetchCoreAsync(uri, httpClientSettings, cancellationToken);
                }, cancellationToken);
        }
        catch (Exception ex)
        {
            Logger.ForErrorEvent()
                .Message("Failed to fetch remote file metadata from {Uri}", uri.Scrub().ToString())
                .Exception(ex)
                .Log();

            throw;
        }
    }

    private static async Task<RemoteFileMetadata> FetchCoreAsync(Uri uri, HttpClientSettings httpClientSettings, CancellationToken cancellationToken)
    {
        // Create a temporary HTTP client just for this stage. This isn't a good practice, but it's OK since this
        // isn't the high performance part of the program.
        using var httpClient = httpClientSettings.CreateHttpClient();

        // Each of these individual operations are wrapped in a retry policy at HTTP communication level, and they will
        // throw if anything goes wrong. If any of these fail, there's absolutely nothing we can do to recover from it.
        var remoteFileMetadata = await GetRemoteFileMetadataAsync(uri, httpClient, cancellationToken);
        var manifestMetadata = await TryGetManifestMetadataAsync(httpClient, uri, remoteFileMetadata, cancellationToken);
        if (manifestMetadata is null)
        {
            return remoteFileMetadata;
        }

        var manifest = await GetManifestAsync(httpClient, uri, manifestMetadata, remoteFileMetadata.ETag, cancellationToken);
        return remoteFileMetadata with { Manifest = manifest };
    }

    private static async Task<RemoteFileMetadata> GetRemoteFileMetadataAsync(Uri uri, HttpClient httpClient, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await RetryPolicy.ExecuteAsync(
                async (cancellationToken) =>
                {
                    // Do a 1 byte range request. In any compliant HTTP service, this is guaranteed to return a
                    // Content-Range header that looks like bytes 0-1/<file size>.
                    //
                    // WARNING: We've done this using HEAD requests in the past. That's not a standard way of operating
                    // and so it may fail when trying to download from some HTTP implementations (ex: Azure CDN). We
                    // depend on Range requests for the rest of the download, so if this doesn't work the program has
                    // no chance of working anyways.
                    var range = new Chunk(0, 1);
                    var response = await httpClient.RangeDownloadAsync(uri, range, completionOption: HttpCompletionOption.ResponseHeadersRead, cancellationToken: cancellationToken);
                    response.EnsureSuccessStatusCode();

                    return response;
                }, cancellationToken);

            var contentRange = response.Content.Headers.ContentRange ?? throw new InvalidOperationException($"Content-Range header not found in GET response from URI {uri.Scrub()}");
            var fileSizeBytes = contentRange.Length ?? throw new InvalidOperationException($"Content-Range header missing Length in GET response from URI {uri.Scrub()}");
            var etag = response.Headers.ETag?.Tag;

            return new RemoteFileMetadata(fileSizeBytes, etag, Manifest: null);
        }
        catch (HttpRequestException e)
        {
            throw new InvalidOperationException("Failed to send HEAD request to input URI", e);
        }
    }

    private static async Task<Footer?> TryGetManifestMetadataAsync(HttpClient httpClient, Uri uri, RemoteFileMetadata remoteFileMetadata, CancellationToken cancellationToken)
    {
        if (remoteFileMetadata.FileSizeBytes < MagicSequence.FixedBinaryLength + Footer.FixedBinaryLength)
        {
            return null;
        }

        var magicSequenceChunk = MagicSequence.Location(remoteFileMetadata.FileSizeBytes);
        magicSequenceChunk.Validate(remoteFileMetadata.FileSizeBytes);

        var metadataChunk = Footer.Location(remoteFileMetadata.FileSizeBytes);
        metadataChunk.Validate(remoteFileMetadata.FileSizeBytes);

        var requestChunk = new Chunk(metadataChunk.Start, magicSequenceChunk.End);
        requestChunk.Validate(remoteFileMetadata.FileSizeBytes);
        Contract.Assert(requestChunk.Length == Footer.FixedBinaryLength + MagicSequence.FixedBinaryLength);

        var requestBytes = await RetryPolicy.ExecuteAsync(
            async (cancellationToken) =>
            {
                return await httpClient.RangeDownloadBytesAsync(
                    uri,
                    requestChunk,
                    etag: remoteFileMetadata.ETag,
                    cancellationToken: cancellationToken);
            }, cancellationToken);

        var sequenceBytes = requestBytes.AsSpan().Slice(Footer.FixedBinaryLength);
        if (!MagicSequence.Matches(sequenceBytes))
        {
            return null;
        }

        var metadataBytes = requestBytes.AsSpan().Slice(0, Footer.FixedBinaryLength);
        var metadata = Footer.FromSpan(metadataBytes);
        metadata.Validate(remoteFileMetadata.FileSizeBytes);

        return metadata;
    }

    private static async Task<Manifest> GetManifestAsync(HttpClient httpClient, Uri uri, Footer metadata, string? etag, CancellationToken cancellationToken)
    {
        var manifestBytes = await RetryPolicy.ExecuteAsync(
            async (cancellationToken) =>
            {
                return await httpClient.RangeDownloadBytesAsync(
                    uri,
                    metadata.Slice,
                    etag: etag,
                    cancellationToken: cancellationToken);
            }, cancellationToken);

        using var manifestStream = new MemoryStream(manifestBytes);
        return await metadata.ReadManifestAsync(manifestStream, cancellationToken);
    }
}
