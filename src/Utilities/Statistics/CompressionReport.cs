// Copyright (C) Microsoft Corporation. All Rights Reserved.

#nullable enable

namespace FastDownload.Utilities;

public record class CompressionReport
{
    public required long RawFileSizeBytes { get; init; }

    public required double RawFileSizeMB { get; init; }

    public required long CompressedFileSizeBytes { get; init; }

    public required double CompressedFileSizeMB { get; init; }

    public required double CompressionRatio { get; init; }
}
