// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Utilities;

/// <summary>
/// Arguments for <see cref="HttpUtilities.RangeDownloadAsync"/>
/// </summary>
internal record RangeDownloadArguments
{
    public string? Etag { get; init; }

    public ProxyRangeDownloadArguments? ProxyArguments { get; init; }
}