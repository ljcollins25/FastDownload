// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Net.Http.Headers;
using FastDownload.Shared;
using Microsoft.AspNetCore.Http.Headers;

namespace FastDownload.Utilities;

/// <summary>
/// Defines arguments passed as headers to REST queries to <see cref="Download.ChunkServer"/>.
/// </summary>
internal record ProxyRangeDownloadArguments
{
    public required int ChunkIndex { get; init; }

    public required string? ExpectedCertificateCertHash { get; init; }

    public required string Requestor { get; init; }

    /// <summary>
    /// The expected compression algorithm if compressed content is available
    /// </summary>
    public required CompressionAlgorithm CompressionEncoding { get; init; }

    /// <summary>
    /// The destination chunk in the file requested
    /// </summary>
    public required Chunk DestinationChunk { get; init; }

    /// <summary>
    /// Get the arguments from the request headers
    /// </summary>
    public static ProxyRangeDownloadArguments Get(RequestHeaders headers)
    {
        static Chunk FromRange(RangeHeaderValue range)
        {
            var firstRange = range.Ranges.Single();
            return new Chunk(firstRange.From!.Value, firstRange.To!.Value + 1);
        }

        return new()
        {
            ChunkIndex = headers.Get<int>(nameof(HeaderNames.ChunkIndex)),
            Requestor = headers.Headers[nameof(HeaderNames.Requestor)].ToString(),
            CompressionEncoding = Enum.Parse<CompressionAlgorithm>(headers.Headers[nameof(HeaderNames.CompressionEncoding)].ToString()),
            DestinationChunk = FromRange(headers.Get<RangeHeaderValue>(nameof(HeaderNames.DestinationChunk))!),

            // Not used by server
            ExpectedCertificateCertHash = null,
        };
    }

    /// <summary>
    /// Apply headers to http request for the given arguments
    /// </summary>
    public void SetHeaders(HttpRequestHeaders headers)
    {
        headers.Add(nameof(HeaderNames.ChunkIndex), ChunkIndex.ToString());
        headers.Add(nameof(HeaderNames.Requestor), Requestor);
        headers.Add(nameof(HeaderNames.CompressionEncoding), CompressionEncoding.ToString());
        headers.Add(nameof(HeaderNames.DestinationChunk), new RangeHeaderValue(DestinationChunk.Start, DestinationChunk.End - 1).ToString());
    }

    /// <summary>
    /// Apply headers and settings to http request for the given arguments
    /// </summary>
    public void ApplyTo(HttpRequestMessage request)
    {
        SetHeaders(request.Headers);

        request.ApplyProxySettings(certHash: ExpectedCertificateCertHash);
    }

    private enum HeaderNames
    {
        ChunkIndex,
        Requestor,
        CompressionEncoding,
        DestinationChunk
    }
}

public enum ProxyEventAction
{
    WaitEvent,
    SetEvent
}
