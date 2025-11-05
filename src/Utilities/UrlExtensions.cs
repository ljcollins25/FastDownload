// Copyright (C) Microsoft Corporation. All Rights Reserved.

using Azure.Storage.Blobs;

namespace FastDownload.Utilities
{
    internal static class UrlExtensions
    {
        public static bool UseAzureCredentials(this Uri uri)
        {
            if (uri.Host.Contains(".blob.core.windows.net") || uri.Host.Contains(".azurefd.net"))
            {
                if (string.IsNullOrEmpty(uri.Query))
                {
                    return true;
                }

                return false;
            }
            else
            {
                return false;
            }
        }

        public static UriBuilder Scrub(this Uri uri)
        {
            var scrubbedUri = new UriBuilder(uri)
            {
                Query = null
            };

            return scrubbedUri;
        }

        public static Uri Combine(this Uri folder, string path)
        {
            var builder = new BlobUriBuilder(folder, trimBlobNameSlashes: true);
            builder.BlobName += $"/{path}";
            return builder.ToUri();
        }
    }
}