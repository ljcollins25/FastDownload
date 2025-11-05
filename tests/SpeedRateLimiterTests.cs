// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Utilities;

namespace FastDownload.Tests
{
    [TestClass]
    public class SpeedRateLimiterTests
    {
        [TestMethod]
        public async Task AcquireAsync_ShouldAllowConsumption_WhenAllowanceIsSufficient()
        {
            await using var rateLimiter = new SpeedRateLimiter(10); // 10 permits per second

            // Attempt to acquire less than the available allowance
            await rateLimiter.AcquireAsync(5, CancellationToken.None);

            // If no exception is thrown, then the acquisition succeeded
            Assert.IsTrue(true);
        }

        [TestMethod]
        public async Task AcquireAsync_ShouldBlock_WhenAllowanceIsInsufficient()
        {
            await using var rateLimiter = new SpeedRateLimiter(5); // 5 permits per second

            // Consume all allowance
            await rateLimiter.AcquireAsync(5, CancellationToken.None);

            var acquireTask = rateLimiter.AcquireAsync(5, CancellationToken.None);
            // This should still be waiting as allowance was insufficient
            Assert.IsFalse(acquireTask.IsCompleted);
        }

        [TestMethod]
        public async Task BackgroundRefill_ShouldIncreaseAllowance_PerSecond()
        {
            await using var rateLimiter = new SpeedRateLimiter(10); // 10 permits per second

            // Consume initial allowance
            await rateLimiter.AcquireAsync(10, CancellationToken.None);

            // Wait for more than one second to allow refill
            await Task.Delay(1100);

            // Attempt to acquire again; should succeed if refill worked
            await rateLimiter.AcquireAsync(5, CancellationToken.None);
            Assert.IsTrue(true);
        }

        [TestMethod]
        public async Task AcquireAsync_ShouldThrowCancellation_WhenTokenIsCancelled()
        {
            await using var rateLimiter = new SpeedRateLimiter(1); // 1 permit per second
            var cancellationTokenSource = new CancellationTokenSource();

            // Consume the initial allowance
            await rateLimiter.AcquireAsync(1, CancellationToken.None);

            // Cancel token before calling acquire
            cancellationTokenSource.Cancel();

            await Assert.ThrowsExactlyAsync<OperationCanceledException>(async () =>
            {
                await rateLimiter.AcquireAsync(1, cancellationTokenSource.Token);
            });
        }

        [TestMethod]
        public async Task AcquireAsync_ShouldAllowConcurrentRequests()
        {
            await using var rateLimiter = new SpeedRateLimiter(50); // High allowance for concurrency test

            // Create tasks to acquire permits simultaneously
            var tasks = new Task[5];
            for (int i = 0; i < 5; i++)
            {
                tasks[i] = rateLimiter.AcquireAsync(10, CancellationToken.None);
            }

            // All tasks should complete without blocking due to sufficient allowance
            await Task.WhenAll(tasks);
        }
    }
}
