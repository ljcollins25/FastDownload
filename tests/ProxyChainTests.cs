// Copyright (C) Microsoft Corporation. All Rights Reserved.

using DotNext.Collections.Generic;
using FastDownload.Download;
using FastDownload.Tests.Azurite;
using FastDownload.Tests.Utilities;
using FastDownload.Utilities;
using Shouldly;

namespace FastDownload.Tests
{
    [TestClass]
    public class ProxyChainTests : TestOutputTestBase
    {
        [TestMethod]
        public async Task TestMachineRegistration()
        {
            int machineCount = 2;
            int zoneCount = 5;
            using var storage = await AzuriteStorageProcess.CreateAndStartAsync(TestContext);

            var checksum = Guid.NewGuid().ToString();

            var container = storage.GetContainer("testcontainer");
            await container.CreateIfNotExistsAsync();

            var timerQueue = new TestTimerQueue(TestContext);

            var machines = Enumerable.Range(0, machineCount).Select(i =>
            {
                var uri = new Uri($"http://localhost:{3100 + i}");
                string zone = (i % zoneCount).ToString();
                var harness = new SemaphoreHarness(container.Uri, timerQueue, maxSlots: -1, i, default);
                var manager = new ProxyChainManager(harness.Semaphore, new ProxyNodeEntry(uri)
                {
                    FileChecksum = checksum,
                    Zone = zone
                });

                return (harness, manager, uri, zone);
            }).ToList();

            // Start the proxy chain managers
            await Parallel.ForEachAsync(machines, async (item, token) =>
            {
                await item.manager.StartAsync(token);
                await item.manager.ExecuteTask;
            });

            // Wait for the proxy chain managers to complete iteration
            await timerQueue.Advance(SemaphoreHarness.RecheckInterval);

            for (int i = 0; i < machineCount; i++)
            {
                var activeMachines = machines.Where(m => m.manager.IsActive);

                // Wait for the proxy chain managers to complete iteration
                TestLogger.Debug($"{i}: Advancing timer from {timerQueue.CurrentTime}");
                await timerQueue.Advance(SemaphoreHarness.RecheckInterval);
                TestLogger.Debug($"{i}: Advanced timer to {timerQueue.CurrentTime}");

                foreach (var machine in activeMachines)
                {
                    TestLogger.Debug($"{i}: ActiveMachine: {machine.uri}, Zone: {machine.zone}, Predecessor: {machine.manager.PredecessorUri}");
                }

                var urisByPredecessor = activeMachines.ToLookup(m => m.manager.PredecessorUri);
                urisByPredecessor[null].Count().ShouldBe(1, "Only one machine should have no predecessor");

                urisByPredecessor.ForEach(g => g.Count().ShouldBeLessThanOrEqualTo(2, "A machine should have at most 2 successors (one in zone and one out of zone)"));

                TestLogger.Debug($"{i}: Marking unavailable: {machines[i].uri}, Predecessor: {machines[i].manager.PredecessorUri}");
                if (machines[i] is { } m && m.manager.PredecessorUri is Uri predecessorUri)
                {
                    m.manager.MarkUnavailable(predecessorUri);

                    // Marking unavailable should set predecessor uri to null
                    m.manager.PredecessorUri.ShouldBe(null);

                    await timerQueue.Advance(SemaphoreHarness.RecheckInterval);

                    // Marking unavailable should prevent using the same predecessor after chain is updated
                    m.manager.PredecessorUri.ShouldNotBe(predecessorUri);
                }

                TestLogger.Debug($"{i}: Stopping: {machines[i].uri}, Predecessor: {machines[i].manager.PredecessorUri}");
                await machines[i].manager.StopAsync(CancellationToken.None);

                await machines[i].harness.Semaphore.DisposeAsync();
            }
        }
    }
}
