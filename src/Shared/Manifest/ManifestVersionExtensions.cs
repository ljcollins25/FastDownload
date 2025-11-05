// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Shared.Manifest
{
    public static class ManifestVersionExtensions
    {
        public static readonly int FixedBinaryLength = 1;

        public static ManifestVersion FromSpan(ReadOnlySpan<byte> data)
        {
            if (data.Length != FixedBinaryLength)
            {
                throw new ArgumentOutOfRangeException(nameof(data), "Invalid buffer size");
            }

            // Verify the version is within bounds
            var version = (ManifestVersion)data[0];
            if (!Enum.IsDefined(version))
            {
                throw new ArgumentOutOfRangeException(nameof(data), $"Invalid version value {data[0]} found");
            }

            return version;
        }

        public static void ToSpan(this ManifestVersion version, Span<byte> data)
        {
            if (data.Length != FixedBinaryLength)
            {
                throw new ArgumentOutOfRangeException(nameof(data), "Invalid buffer size");
            }

            data[0] = (byte)version;
        }
    }
}
