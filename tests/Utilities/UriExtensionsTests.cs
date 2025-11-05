// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Utilities;

namespace FastDownload.Tests.Utilities
{
    [TestClass]
    public class UriExtensionsTests
    {
        [TestMethod]
        [DataRow("https://somestorageacct.blob.core.windows.net/container/blob.bin", true)]
        [DataRow("https://somestorageacct.blob.core.windows.net/container/blob.bin?somequery=1", false)]
        [DataRow("https://somestorageacct.blob.core.windows.net/container/blob.bin?sp=r&st=2024-11-21T00:43:36Z&se=2024-11-21T08:43:36Z&skoid=e8587b08-8644-463f-a2e2-c139a0fffd28&sktid=72f988bf-86f1-41af-91ab-2d7cd011db47&skt=2024-11-21T00:43:36Z&ske=2024-11-21T08:43:36Z&sks=b&skv=2022-11-02&spr=https&sv=2022-11-02&sr=b&sig=ThisIsASignature", false)]
        [DataRow("https://somestorageacct.azurefd.net/container/blob.bin", true)]
        [DataRow("https://somestorageacct.azurefd.net/container/blob.bin?somequery=1", false)]
        [DataRow("https://somestorageacct.azurefd.net/container/blob.bin?sp=r&st=2024-11-21T00:43:36Z&se=2024-11-21T08:43:36Z&skoid=e8587b08-8644-463f-a2e2-c139a0fffd28&sktid=72f988bf-86f1-41af-91ab-2d7cd011db47&skt=2024-11-21T00:43:36Z&ske=2024-11-21T08:43:36Z&sks=b&skv=2022-11-02&spr=https&sv=2022-11-02&sr=b&sig=ThisIsASignature", false)]
        public void UseAzureCredentials(string url, bool useAzureCredentials)
        {
            var uri = new Uri(url);
            Assert.AreEqual(useAzureCredentials, uri.UseAzureCredentials());
        }

        [TestMethod]
        [DataRow("https://somestorageacct.blob.core.windows.net/container/blob.bin", "https://somestorageacct.blob.core.windows.net/container/blob.bin")]
        [DataRow("https://somestorageacct.blob.core.windows.net/container/blob.bin?somequery=1", "https://somestorageacct.blob.core.windows.net/container/blob.bin")]
        [DataRow("https://somestorageacct.blob.core.windows.net/container/blob.bin?sp=r&st=2024-11-21T00:43:36Z&se=2024-11-21T08:43:36Z&skoid=e8587b08-8644-463f-a2e2-c139a0fffd28&sktid=72f988bf-86f1-41af-91ab-2d7cd011db47&skt=2024-11-21T00:43:36Z&ske=2024-11-21T08:43:36Z&sks=b&skv=2022-11-02&spr=https&sv=2022-11-02&sr=b&sig=ThisIsASignature", "https://somestorageacct.blob.core.windows.net/container/blob.bin")]
        public void Scrub(string original, string scrubbed)
        {
            var originalUri = new Uri(original);
            var originalUriScrubbed = originalUri.Scrub();
            var scrubbedUri = new Uri(scrubbed);
            Assert.AreEqual(scrubbedUri.AbsoluteUri, originalUriScrubbed.Uri.AbsoluteUri);
        }
    }
}
