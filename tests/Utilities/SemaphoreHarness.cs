// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Utilities;

namespace FastDownload.Tests
{
    public class SemaphoreHarness
    {
        public static readonly TimeSpan RecheckInterval = TimeSpan.FromSeconds(5);
        public static readonly TimeSpan KeepAliveTime = TimeSpan.FromMinutes(1);

        public SemaphoreHarness(Uri uri, TestTimerQueue timerQueue, int maxSlots, int i, CancellationToken token)
        {
            Index = i;
            SemaphoreCancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
            KeepAliveTimer = timerQueue.CreateTimer($"{i} KeepAlive");
            RecheckTimer = timerQueue.CreateTimer($"{i} Recheck");
            Semaphore = new AzureBlobSemaphore(uri, SemaphoreCancellation.Token)
            {
                RecheckInterval = RecheckInterval,
                KeepAliveTime = KeepAliveTime,
                CleanupRatio = 0.1,
                MaxSlots = maxSlots,
                KeepAliveTimer = KeepAliveTimer,
                RecheckTimer = RecheckTimer,
                Debug = true,
                MachineName = $"M{i:D3}"
            };
        }

        public Task WaitAsync()
        {
            return Task.WhenAny(RecheckTimer.FirstDelayRequest.Task, Semaphore.WaitAsync(default));
        }

        public int Index { get; }
        public CancellationTokenSource SemaphoreCancellation { get; }
        public TestTimerQueue.QueueTimer KeepAliveTimer { get; }
        public TestTimerQueue.QueueTimer RecheckTimer { get; }
        public AzureBlobSemaphore Semaphore { get; }
    }
}
