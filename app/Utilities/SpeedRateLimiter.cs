// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using BuildXL.Utilities.Core.Tasks;
using BuildXL.Utilities.Core.Tracing;

namespace FastDownload.Utilities
{
    public class SpeedRateLimiter : IAsyncDisposable, ISpeedRateLimiter
    {
        private readonly TimeSpan _period;
        private long _refillPerPeriod;
        private long _allowance;

        private readonly CancellationTokenSource _stopSource = new CancellationTokenSource();
        private readonly Task _backgroundTask;
        private AsyncBarrier _barrier = new();

        public TimeSpan Period => _period;

        public long RefillPerPeriod => Interlocked.Read(ref _refillPerPeriod);

        public SpeedRateLimiter(long refillPerPeriod, TimeSpan? period = null)
        {
            Contract.Requires(refillPerPeriod > 0);
            Contract.Requires(period == null || period > TimeSpan.Zero);

            _period = period ?? TimeSpan.FromSeconds(1);
            _refillPerPeriod = refillPerPeriod;

            // We start with a full allowance. This means the first thread that tries to consume will be allowed to do
            // so and might lead to slightly higher consumption than the rate limit for the first couple of seconds.
            _allowance = _refillPerPeriod;
            _backgroundTask = Task.Run(() => BackgroundRefillAsync(_stopSource.Token));
        }

        public void AdjustRefill(long target)
        {
            Contract.Requires(target > 0);
            Interlocked.Exchange(ref _refillPerPeriod, target);
        }

        public bool TryAcquire(long permits)
        {
            while (true)
            {
                var allowance = Interlocked.Read(ref _allowance);
                var next = allowance - permits;
                if (next >= 0)
                {
                    // This thread would be allowed to consume. Verify no one changed the allowance in the meantime and
                    // update it.
                    if (Interlocked.CompareExchange(ref _allowance, next, allowance) == allowance)
                    {
                        // Successfully updated the allowance. This thread is allowed to consume.
                        return true;
                    }
                    else
                    {
                        // Someone else changed the allowance in the meantime. Retry.
                        continue;
                    }
                }
                else
                {
                    return false;
                }
            }
        }

        public async Task AcquireAsync(long permits, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            while (!TryAcquire(permits))
            {
                // Wait until the allowance is refreshed.
                await _barrier.WaitAsync(cancellationToken);
            }
        }

        private async Task BackgroundRefillAsync(CancellationToken cancellationToken)
        {
            try
            {
                await BackgroundRefillCoreAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // We ignore this exception as it's expected when the token is cancelled.
            }
            finally
            {
                _barrier.Dispose();
            }
        }

        private async Task BackgroundRefillCoreAsync(CancellationToken cancellationToken)
        {
            StopwatchSlim stopwatch = StopwatchSlim.Start();
            while (!cancellationToken.IsCancellationRequested)
            {
                // Compute the refill based on the time since the last refill. This might lead to slightly higher rate
                // than the limit if the task doesn't run quickly enough, but it's expected because it lets us ensure
                // we're always as close to the limit as we can.
                var elapsed = stopwatch.Elapsed;
                var refill = (long)Math.Ceiling(elapsed.TotalSeconds * RefillPerPeriod);

                // REMARK: Order matters here. It's important that we reset the allowance before we allow threads
                // to consume again. This prevents spurious loops in other threads.
                Interlocked.Add(ref _allowance, refill);

                _barrier.Reset();

                stopwatch.ElapsedAndReset();
                await Task.Delay(_period, cancellationToken);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _stopSource.Cancel();

            try
            {
                await _backgroundTask;
            }
            finally
            {
                _barrier.Dispose();
                _stopSource.Dispose();
            }
        }
    }
}
