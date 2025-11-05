// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Collections.Specialized;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.ContractsLight;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Web;
using Azure.Core;
using Azure.Identity;
using FastDownload.Shared;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using Newtonsoft.Json.Linq;
using NLog;
using RangeItemHeaderValue = Microsoft.Net.Http.Headers.RangeItemHeaderValue;

namespace FastDownload.Utilities;

public record HttpClientSettings(AccessToken? AccessToken)
{
    /// <summary>
    /// The Storage SDK version to use.
    ///
    /// See: https://learn.microsoft.com/en-us/rest/api/storageservices/versioning-for-the-azure-storage-services
    /// </summary>
    /// <remarks>
    /// 2024-11-04 is the first API version that supports Bearer token authentication.
    /// </remarks>
    private const string StorageApiVersion = "2024-11-04";

    /// <summary>
    /// Header name to specify API version.
    /// </summary>
    private const string StorageApiVersionHeader = "x-ms-version";

    public HttpClient CreateHttpClient()
    {
        // Specify custom certificate validation for use when talking to peer machines.
        // Default certificate validation is still evaluated, and the handler passes along the
        // result when not talking to a peer. Peer is specified by attaching the expected cert hash
        // to the http request object's options.
        var handler = new HttpClientHandler()
        {
            ServerCertificateCustomValidationCallback = VerifyCertificate
        };

        // This is all technically very wrong, because:
        // 1. HttpClient should be created once and shared to prevent port exhaustion issues
        // 2. HttpClient should be created with SocketsHandler to avoid DNS resolution caching issues
        // This binary is meant to run (along) for a short time and then exit, so we're not going to worry about it.
        var client = new HttpClient(handler)
        {
            // Prevent HTTP version weirdness from happening during the requests. Storage only supports HTTP/1.1.
            // See: https://learn.microsoft.com/en-us/rest/api/storageservices/http-version-support
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionExact,
            DefaultRequestVersion = HttpVersion.Version11,

            // Disable HTTP timeouts inside of the HttpClient. We want to control timeouts ourselves.
            Timeout = Timeout.InfiniteTimeSpan,
            // TODO: set server-side timeouts https://learn.microsoft.com/en-us/rest/api/storageservices/setting-timeouts-for-blob-service-operations?view=rest-storageservices-datalakestoragegen2-2019-12-12
            // TODO: x-ms-client-request-id
        };

        // Ensure all requests are identified as coming from this tool in case it's required for debugging
        client.DefaultRequestHeaders.UserAgent.Add(VersionInfo.GenerateProductInfoHeader());
        client.DefaultRequestHeaders.Add(StorageApiVersionHeader, StorageApiVersion);

        if (AccessToken is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", AccessToken.Value.Token);
        }

        return client;
    }

    public static async Task<HttpClientSettings> CreateAsync(Uri uri, Logger logger, CancellationToken cancellationToken)
    {
        // If it's Azure Storage and if it doesn't have a SAS token in the URI, we'll try to fetch an access token
        // using DefaultAzureCredential.
        AccessToken? accessToken = null;
        if (uri.UseAzureCredentials())
        {
            logger.ForInfoEvent()
                .Message("Azure Storage URI without a SAS token detected. Fetching Azure Storage access token...")
                .Log();

            accessToken = await HttpUtilities.GetAccessTokenAsync(cancellationToken);

            logger.ForInfoEvent()
                .Message("Azure Storage access token fetched successfully with expiration at {Expiration}", accessToken.Value.ExpiresOn)
                .Log();
        }

        return new HttpClientSettings(accessToken);
    }

    private bool VerifyCertificate(HttpRequestMessage message, X509Certificate2? certificate, X509Chain? chain, SslPolicyErrors errors)
    {
        // For proxy requests, the expected cert hash is set on request options.
        if (certificate != null && message.Options.TryGetValue(HttpUtilities.ExpectedCertHashKey, out var expectedCertHash))
        {
            if (expectedCertHash == HttpUtilities.MatchAllCertHash)
            {
                return true;
            }

            var actualCertHash = certificate.ComputeCertHash();
            return actualCertHash == expectedCertHash;
        }

        // If certificate passes default checks, just return true.
        if (errors == SslPolicyErrors.None)
        {
            return true;
        }

        return false;
    }
}

internal static class HttpUtilities
{
    /// <summary>
    /// Header name to specify a correlation ID for the request.
    /// </summary>
    private const string StorageCorrelationIdHeader = "x-ms-client-request-id";

    public const string MatchAllCertHash = "*";

    public enum QueryParamKey
    {
        checksum,
        version,
        action,
        http2
    }

    private static readonly TokenCredential Credential = new ManagedIdentityCredential();

    private static readonly TokenRequestContext StorageRequestContext = new TokenRequestContext(["https://storage.azure.com/.default"]);

    public static readonly HttpRequestOptionsKey<string> ExpectedCertHashKey = new HttpRequestOptionsKey<string>(nameof(ExpectedCertHashKey));

    public static ValueTask<AccessToken> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        return Credential.GetTokenAsync(StorageRequestContext, cancellationToken);
    }

    public static string ComputeCertHash(this X509Certificate certificate)
    {
        return certificate.GetCertHashString(HashAlgorithmName.SHA256);
    }

    public static StringValues GetQueryValue(this HttpRequest request, QueryParamKey key)
    {
        return request.Query[key.ToString()];
    }

    public static bool TryGetQueryValue(this HttpRequest request, QueryParamKey key, out StringValues value)
    {
        return request.Query.TryGetValue(key.ToString(), out value);
    }

    public static async Task<bool> SyncAsync(this HttpClient client, ProxyNodeEntry proxy, ProxyEventAction action)
    {
        var uri = new RequestUriBuilder();
        uri.Reset(proxy.Uri);
        uri.AppendQuery(nameof(QueryParamKey.action), action.ToString());

        var request = new HttpRequestMessage(HttpMethod.Get, uri.ToUri());

        request.ApplyProxySettings(certHash: proxy.CertHash);

        var response = await client.SendAsync(request);
        return response.IsSuccessStatusCode;
    }

    public static NameValueCollection ParseQueryString(this Uri uri)
    {
        return HttpUtility.ParseQueryString(uri.Query);
    }

    public static void ApplyProxySettings(this HttpRequestMessage request, string? certHash)
    {
        if (request.RequestUri?.ParseQueryString()[nameof(QueryParamKey.http2)] == true.AsNumberString())
        {
            request.Version = new Version(2, 0);
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        if (certHash is { } expectedThumprint)
        {
            request.Options.Set(ExpectedCertHashKey, expectedThumprint);
        }
    }

    public static bool TryGetSingleValue(this HttpHeaders headers, string name, [NotNullWhen(true)] out string? value)
    {
        if (headers.TryGetValues(name, out var values) && values.Any())
        {
            value = values.First();
            return true;
        }
        else
        {
            value = null;
            return false;
        }
    }

    public static async Task<string?> GetAzureZoneAsync()
    {
        try
        {
            string metadataUrl = "http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01";
            using (HttpClient client = new HttpClient())
            {
                client.DefaultRequestHeaders.Add("Metadata", "true");

                HttpResponseMessage response = await client.GetAsync(metadataUrl);
                response.EnsureSuccessStatusCode();

                string responseBody = await response.Content.ReadAsStringAsync();
                JObject metadata = JObject.Parse(responseBody);

                var zone = metadata["zone"]?.ToString();
                return zone;
            }
        }
        catch
        {
            return null;
        }
    }

    public static long? GetLength(this RangeItemHeaderValue range)
    {
        return (range.To - range.From) + 1;
    }

    public static async Task<byte[]> RangeDownloadBytesAsync(
        this HttpClient httpClient,
        Uri uri,
        Chunk chunk,
        string? etag = null,
        CancellationToken cancellationToken = default)
    {
        var args = new RangeDownloadArguments()
        {
            Etag = etag
        };

        using var response = await httpClient.RangeDownloadAsync(uri, chunk, args, HttpCompletionOption.ResponseContentRead, cancellationToken);

        // It's important that we do this, upper layers depend on HttpRequestException being thrown if the request fails.
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        Contract.Assert(body.Length == chunk.Length, $"Downloaded chunk length {body.Length} doesn't match requested chunk length {chunk.Length}");

        return body;
    }

    public static Task<HttpResponseMessage> RangeDownloadAsync(
        this HttpClient httpClient,
        Uri uri,
        Chunk chunk,
        RangeDownloadArguments? arguments = null,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        return httpClient.RangeDownloadAsync(
            uri,
            chunk.Start,
            // REMARK: chunk.End is exclusive, so we need to subtract 1 to get the correct range as it's used by the
            // HTTP Range header.
            chunk.End - 1,
            arguments,
            completionOption,
            cancellationToken);
    }

    public static async Task<HttpResponseMessage> RangeDownloadAsync(
        this HttpClient httpClient,
        Uri uri,
        long? from = null,
        long? to = null,
        RangeDownloadArguments? arguments = null,
        HttpCompletionOption completionOption = HttpCompletionOption.ResponseContentRead,
        CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);

        if (from is not null || to is not null)
        {
            Contract.Requires(from < to, "Invalid byte range specified");
            request.Headers.Range = new RangeHeaderValue(from, to);
        }

        var etag = arguments?.Etag;

        if (arguments?.ProxyArguments is { } proxyArgs)
        {
            proxyArgs.ApplyTo(request);
        }

        if (!string.IsNullOrEmpty(etag))
        {
            // Ensure we're always reading from the same file as we initially checked.
            //
            // Servers may not provide a valid ETag we can match against, in which case
            // we just don't use it.
            //
            // This is non-ideal because we have no way to ensure we're reading the
            // same file as we originally were (or it might even be modified while we're
            // reading it), but in practice shouldn't be a huge deal for the use-cases
            // this tool is meant to address.
            request.Headers.IfMatch.Add(new EntityTagHeaderValue(etag));
        }

        string clientRequestId = $"{Globals.CorrelationId}/{Globals.SourceCorrelationId ?? "Unknown"}/{etag ?? "Unknown"}/{from ?? -1}/{to ?? -1}/{Guid.NewGuid()}";
        request.Headers.Add(StorageCorrelationIdHeader, clientRequestId);

        // WARNING: do not catch any exceptions here, let the caller handle them. This is assumed in the code, and
        // changing it could break retry policies.
        var response = await httpClient.SendAsync(request, completionOption, cancellationToken);
        Contract.Assert(!response.IsSuccessStatusCode || response.Content.Headers.ContentLength is not null, "Content-Length header missing in GET response from input URI");
        Contract.Assert(!response.IsSuccessStatusCode || from is null || to is null || response.Content.Headers.ContentLength == to - from + 1, $"Content-Length header ({response.Content.Headers.ContentLength}) doesn't match requested chunk length (chunk [{from}, {to}], length {to - from + 1})");

        return response;
    }
}
