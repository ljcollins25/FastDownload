// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Utilities;
using FastDownload.Download;
using Shouldly;

namespace FastDownload.Tests
{
    [TestClass]
    public class RefCountHandleTests
    {
        [TestMethod]
        public void ParallelUpdateTest()
        {
            AsyncOut<int> cleanupCount = new();
            var handle = new RefCountHandle<string>("MyValue", () =>
            {
                Interlocked.Increment(ref cleanupCount.Value);
            });

            Parallel.For(0, 1000, i =>
            {
                handle.TryReference().ShouldBeTrue();
                handle.Release();

                handle.TryReference().ShouldBeTrue();
                handle.Dispose();
            });

            handle.ActiveReferences.ShouldBe(1u);
            handle.PendingCleanups.ShouldBe(0);
            cleanupCount.Value.ShouldBe(0);

            handle.Dispose();
            handle.ActiveReferences.ShouldBe(0u);
            cleanupCount.Value.ShouldBe(1);
        }
    }
}
