// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Tests.Azurite;
using FastDownload.Tests.Utilities;
using FastDownload.Utilities;
using Shouldly;

namespace FastDownload.Tests
{
    [TestClass]
    public class AzureBlobSemaphoreTests : TestOutputTestBase
    {
        [TestMethod]
        public async Task DisposeWorksWithoutWait()
        {
            var semaphore = new AzureBlobSemaphore(new Uri("http://127.0.0.1:49295/devstoreaccount1/testcontainer/testfolder"), default(CancellationToken))
            {
                CleanupRatio = 0,
                MaxSlots = 1,
                KeepAliveTime = TimeSpan.FromSeconds(100),
                RecheckInterval = TimeSpan.FromSeconds(5)
            };

            await semaphore.DisposeAsync();
        }

        [TestMethod]
        [Timeout(30_000)]
        public async Task SimpleWait()
        {
            using var storage = await AzuriteStorageProcess.CreateAndStartAsync(TestContext);

            var container = storage.GetContainer("testcontainer");
            await container.CreateIfNotExistsAsync();

            var semaphore = new AzureBlobSemaphore(container.GetBlobClient("testwait").Uri, default(CancellationToken))
            {
                CleanupRatio = 0,
                MaxSlots = 1,
                KeepAliveTime = TimeSpan.FromSeconds(100),
                RecheckInterval = TimeSpan.FromSeconds(5)
            };

            await semaphore.WaitAsync(CancellationToken.None);
        }

        [TestMethod]
        [Timeout(60_000)]
        public async Task MultiMachineAcquisitionEndToEnd()
        {
            int machineCount = 12;
            int maxSlots = 3;
            using var storage = await AzuriteStorageProcess.CreateAndStartAsync(TestContext);
            using var cts = new CancellationTokenSource();

            var container = storage.GetContainer("testcontainer");
            await container.CreateIfNotExistsAsync();

            var blob = container.GetBlobClient("testfolder");

            var timerQueue = new TestTimerQueue(TestContext);

            var machines = await AsyncEnumerable.Range(0, machineCount).SelectAwait(async i =>
            {
                var m = new SemaphoreHarness(blob.Uri, timerQueue, maxSlots, i, cts.Token);
                await timerQueue.Advance(0.1);

                await m.WaitAsync();
                return m;
            })
            .ToListAsync();

            void orderMachinesAcquiredFirst()
            {
                machines = machines.OrderBy(m => m.Semaphore.IsAcquired ? 0 : 1).ToList();
            }

            int randomSlot() => Random.Shared.Next(0, maxSlots);

            for (int i = 0; i < 3; i++)
            {
                foreach (var useExpiry in new[] { false, true })
                {
                    TestContext.WriteLine($"Iteration {i}. (useExpiry={useExpiry})");

                    orderMachinesAcquiredFirst();

                    machines.Count(s => s.Semaphore.IsAcquired).ShouldBe(maxSlots);
                    machines.ShouldAllBe(s => s.Semaphore.SlotBlobExistsAsync().GetAwaiter().GetResult());

                    var slot = randomSlot();

                    var removed = machines[slot];

                    TestContext.WriteLine($"Releasing machine={removed.Index}. (via expiry={useExpiry})");

                    if (useExpiry)
                    {
                        removed.Semaphore.SlotBlobExistsAsync().Result.ShouldBeTrue("Abrupt termination without disposal should not delete blob");
                        removed.SemaphoreCancellation.Cancel();
                        await removed.Semaphore.WaitAsync(default);
                    }
                    else
                    {
                        // Dispose semaphore for one machine to release the slot
                        await removed.Semaphore.DisposeAsync();
                        removed.Semaphore.SlotBlobExistsAsync().Result.ShouldBeFalse("Releasing semaphore should delete the blob");
                    }

                    machines[slot].Semaphore.IsAcquired.ShouldBeFalse();
                    machines.Count(s => s.Semaphore.IsAcquired).ShouldBe(maxSlots - 1);
                    machines.RemoveAt(slot);

                    if (useExpiry)
                    {
                        // Advance time by keep alive time so that blob slot acquisition expires
                        await timerQueue.Advance(machines[slot].Semaphore.KeepAliveTime);
                    }

                    // Increment the timer to allow machines to detect available slot
                    await timerQueue.Advance(seconds: 10);

                    machines.Count(s => s.Semaphore.IsAcquired).ShouldBe(maxSlots);
                    machines.ShouldAllBe(s => s.Semaphore.SlotBlobExistsAsync().GetAwaiter().GetResult());
                }
            }
        }
    }
}
