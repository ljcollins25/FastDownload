// Copyright (C) Microsoft Corporation. All Rights Reserved.

using Azure.Storage.Blobs.Specialized;
using FastDownload.Tests.Azurite;
using Shouldly;

namespace FastDownload.Tests
{
    [TestClass]
    public class StorageTests
    {
        public TestContext TestContext { get; set; }

        [TestMethod]
        public async Task Basic()
        {
            using var storage = await AzuriteStorageProcess.CreateAndStartAsync(TestContext);

            var container = storage.GetContainer("testcontainer");
            await container.CreateIfNotExistsAsync();

            var blob = container.GetBlockBlobClient("hello.txt");

            using var ms = new MemoryStream();
            using var writer = new StreamWriter(ms);
            var text = "Hello world";
            writer.Write(text);
            writer.Flush();

            ms.Position = 0;
            await blob.UploadAsync(ms);

            var client = new HttpClient();
            var downloadedText = await client.GetStringAsync(blob.Uri);

            downloadedText.ShouldBe(text);
        }
    }
}
