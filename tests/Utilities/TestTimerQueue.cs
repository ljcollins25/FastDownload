// Copyright (C) Microsoft Corporation. All Rights Reserved.

using BuildXL.Utilities.Core.Tasks;
using FastDownload.Download;
using FastDownload.Utilities;

namespace FastDownload.Tests
{
    public class TestTimerQueue(TestContext context)
    {
        private PriorityQueue<(QueueTimer Timer, CancellationToken Token), TimeSpan> _timerQueue = new();
        private TimeSpan _currentTime = default;
        private SemaphoreSlim _lock { get; } = TaskUtilities.CreateMutex(taken: false);

        public TimeSpan CurrentTime => _currentTime;

        public QueueTimer CreateTimer(string debugName)
        {
            return new(this, debugName);
        }

        public ValueTask Advance(double seconds)
        {
            return Advance(TimeSpan.FromSeconds(seconds));
        }

        public async ValueTask Advance(TimeSpan time)
        {
            using var cts = new CancellationTokenSource();
            var waitForNextTasks = new List<Task<string>>();

            cts.CancelAfter(TimeSpan.FromSeconds(120));
            var targetTime = _currentTime + time;

            while (true)
            {
                waitForNextTasks.Clear();

                using (await _lock.AcquireAsync())
                {
                    TimeSpan? nextTime = null;
                    while (_timerQueue.TryPeek(out _, out var topTime)
                        && topTime <= targetTime
                        && (nextTime ??= topTime) == topTime)
                    {
                        var (topEntry, token) = _timerQueue.Dequeue();

                        waitForNextTasks.Add(Task.WhenAny(
                            topEntry.WaitForNextDelayRequest.WaitAsync(cts.Token).ContinueWith(t => "next"),
                            // , token.GetCompletionTask().ContinueWith(t => "cancelled")
                            topEntry.DisposeSource.Task.ContinueWith(t => "dispose")).Unwrap());

                        topEntry.DelaySemaphore.Reset();
                    }

                    _currentTime = nextTime ?? targetTime;
                    if (waitForNextTasks.Count == 0)
                    {
                        _currentTime = targetTime;
                        return;
                    }
                }

                var results = await Task.WhenAll(waitForNextTasks);
            }
        }

        private async Task Delay(QueueTimer timer, TimeSpan delay, CancellationToken token)
        {
            context.WriteLine($"TimerDelay CurrentTime={_currentTime} Name={timer.DebugName}, Delay={delay}");
            var task = timer.DelaySemaphore.WaitAsync(token);

            using (await _lock.AcquireAsync(token))
            {
                _timerQueue.Enqueue((timer, token), _currentTime + delay);
            }

            await task;
        }

        public class QueueTimer(TestTimerQueue queue, string debugName) : AsyncTimer
        {
            public string DebugName => debugName;

            public override TimeSpan VirtualTimeOffset => queue._currentTime;

            public AsyncBarrier DelaySemaphore { get; } = new();
            public AsyncBarrier WaitForNextDelayRequest { get; } = new();
            public TaskSourceSlim<bool> DisposeSource { get; } = TaskSourceSlim.Create<bool>();
            public TaskSourceSlim<bool> FirstDelayRequest { get; } = TaskSourceSlim.Create<bool>();

            public override Task Delay(TimeSpan delay, CancellationToken token)
            {
                WaitForNextDelayRequest.Reset();
                FirstDelayRequest.TrySetResult(true);
                return queue.Delay(this, delay, token);
            }

            public override void Dispose()
            {
                DisposeSource.TrySetResult(true);
            }
        }
    }
}
