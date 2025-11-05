// Copyright (C) Microsoft Corporation. All Rights Reserved.

using System.Diagnostics.ContractsLight;
using NLog;

namespace FastDownload.Utilities
{
    internal class SpeedRateAdjuster : IAsyncDisposable
    {
        public record Settings(
            long MinimumRate,
            long MaximumRate,
            double ShrinkFactor = 0.75,
            double MaximumGrowthFactor = 2.0,
            double GrowthFactorIncrementPerStreak = 0.1,
            TimeSpan Period = default,
            int MinimumSuccessPeriods = 3,
            double RandomAdjustmentRange = 0.05,
            double GrowthTimeSpreadSeconds = 1.5);

        internal readonly record struct State(
            long PreviousRetryCount,
            int SuccessStreak,
            DateTime LastAdjustmentTime);

        internal readonly record struct Intermediate(
            long TotalRetryCount,
            long IntervalRetryCount,
            long CurrentRate,
            TimeSpan DeltaTime);

        protected readonly Logger Logger = LogManager.GetLogger(nameof(SpeedRateAdjuster));

        private readonly CancellationTokenSource _stopSource = new();
        private readonly Task? _backgroundTask;

        private readonly Settings _settings;
        private readonly Statistics _statistics;
        private readonly Random _random = new();
        private readonly ISpeedRateLimiter _limiter;

        private bool _disposed = false;

        private State _state;

        public SpeedRateAdjuster(ISpeedRateLimiter limiter, Statistics statistics, Settings settings, bool runBackgroundLoop = true)
        {
            if (settings.Period == default)
            {
                settings = settings with { Period = TimeSpan.FromSeconds(1) };
            }

            Contract.Requires(settings.Period > TimeSpan.Zero);
            Contract.Requires(settings.MinimumRate > 0);
            Contract.Requires(settings.MaximumRate > 0);
            Contract.Requires(settings.MinimumRate <= settings.MaximumRate);
            Contract.Requires(settings.ShrinkFactor > 0.0 && settings.ShrinkFactor < 1.0);
            Contract.Requires(settings.MaximumGrowthFactor > 1.0);
            Contract.Requires(settings.GrowthFactorIncrementPerStreak > 0.0);
            Contract.Requires(settings.MinimumSuccessPeriods >= 0);
            Contract.Requires(settings.RandomAdjustmentRange >= 0.0 && settings.RandomAdjustmentRange <= 1.0);

            _settings = settings;
            _statistics = statistics;
            _limiter = limiter;

            var initialRetryCount = _statistics.Read("RetryServiceUnavailable") ?? 0;
            _state = new State(
                PreviousRetryCount: initialRetryCount,
                SuccessStreak: 0,
                LastAdjustmentTime: DateTime.UtcNow);

            if (runBackgroundLoop)
            {
                _backgroundTask = Task.Run(() => BackgroundTaskAsync(_stopSource.Token));
            }
        }

        private async Task BackgroundTaskAsync(CancellationToken cancellationToken)
        {
            await Task.Yield();
            Logger.Trace("Starting background rate adjustment loop");

            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    // Either of these can throw an OperationCanceledException if the token is cancelled, so we catch
                    // and break.
                    await Task.Delay(_settings.Period, cancellationToken);
                    await TryAdjustLimitCoreAsync(isBackground: true, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            Logger.Trace("Exiting background rate adjustment loop");
        }

        public Task TryAdjustLimitAsync(CancellationToken cancellationToken = default)
        {
            return TryAdjustLimitCoreAsync(isBackground: false, cancellationToken);
        }

        public async Task TryAdjustLimitCoreAsync(bool isBackground, CancellationToken cancellationToken = default)
        {
            if (isBackground)
            {
                _stopSource.Token.ThrowIfCancellationRequested();
            }
            else
            {
                // Disposable synchronizes with background task so
                // we don't throw an object disposed exception there. Only direct
                // callers should see object disposed exception
                ObjectDisposedException.ThrowIf(_disposed, this);
            }

            var now = DateTime.UtcNow;
            var deltaTime = now - _state.LastAdjustmentTime;

            var totalRetryCount = _statistics.Read("RetryServiceUnavailable") ?? _state.PreviousRetryCount;
            var currentRate = _limiter.RefillPerPeriod;

            var intermediate = new Intermediate(
                TotalRetryCount: totalRetryCount,
                IntervalRetryCount: totalRetryCount - _state.PreviousRetryCount,
                CurrentRate: currentRate,
                DeltaTime: deltaTime);

            var (nextStableState, targetRate) = Apply(_state, intermediate, _settings, _random, now);

            _state = nextStableState;

            if (targetRate is not null)
            {
                // If we're decreasing the rate, we'll to that immediately and without wait, because we'd only do that
                // if it's necessary to prevent issues. If we're increasing the rate, we'll add some jitter to the
                // adjustment to avoid all clients increasing their speeds at the same time. This means we'll adjust in
                // aggregate slightly slower.
                TimeSpan jitter = TimeSpan.Zero;
                if (targetRate > currentRate)
                {
                    jitter = TimeSpan.FromSeconds(_random.NextDouble() * _settings.GrowthTimeSpreadSeconds);

                    if (jitter > TimeSpan.Zero)
                    {
                        await Task.Delay(jitter, cancellationToken);
                    }
                }

                Logger.Info($"Adjusting target speed rate from {currentRate} MB to {targetRate.Value} MB (Period: {_limiter.Period}, Retry Count: {totalRetryCount - _state.PreviousRetryCount}, Success Streak: {nextStableState.SuccessStreak}, Jitter: {jitter.TotalMilliseconds}ms)");
                _limiter.AdjustRefill(targetRate.Value);
            }
            else
            {
                Logger.Trace($"Target speed rate remains at {currentRate} MB (Period: {_limiter.Period}, Retry Count: {totalRetryCount - _state.PreviousRetryCount}, Success Streak: {nextStableState.SuccessStreak})");
            }
        }

        internal static (State Next, long? TargetRate) Apply(
            State state,
            Intermediate intermediate,
            Settings settings,
            Random random,
            DateTime now)
        {
            int successStreak = intermediate.IntervalRetryCount > 0 ? 0 : state.SuccessStreak + 1;
            bool increaseAllowed = successStreak >= settings.MinimumSuccessPeriods && intermediate.IntervalRetryCount == 0;

            double randomDeviation = (random.NextDouble() * 2.0 - 1.0) * settings.RandomAdjustmentRange;
            double randomFactor = 1.0 + randomDeviation;

            double timeScale = intermediate.DeltaTime.TotalSeconds;
            double shrinkPerDelta = Math.Pow(settings.ShrinkFactor, timeScale);

            int successStreakForGrowth = 0;
            if (successStreak >= settings.MinimumSuccessPeriods)
            {
                successStreakForGrowth = successStreak - settings.MinimumSuccessPeriods + 1;
            }

            double growthIncrement = settings.GrowthFactorIncrementPerStreak * successStreakForGrowth * timeScale;
            double maxGrowthFactor = settings.MaximumGrowthFactor;
            double growthFactor = Math.Min(maxGrowthFactor, 1.0 + growthIncrement);

            long? targetRate = null;

            if (intermediate.IntervalRetryCount > 0)
            {
                var reducedRate = (long)Math.Ceiling(intermediate.CurrentRate * shrinkPerDelta * randomFactor);
                reducedRate = Math.Max(reducedRate, settings.MinimumRate);
                if (reducedRate < intermediate.CurrentRate)
                {
                    targetRate = reducedRate;
                }
            }
            else if (increaseAllowed)
            {
                var increasedRate = (long)Math.Ceiling(intermediate.CurrentRate * growthFactor * randomFactor);
                increasedRate = Math.Min(increasedRate, settings.MaximumRate);
                if (increasedRate > intermediate.CurrentRate)
                {
                    targetRate = increasedRate;
                }
            }

            var next = new State(
                PreviousRetryCount: intermediate.TotalRetryCount,
                SuccessStreak: successStreak,
                LastAdjustmentTime: now);

            return (next, targetRate);
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _stopSource.Cancel();
                _disposed = true;

                if (_backgroundTask != null)
                {
                    await _backgroundTask;
                }

                _stopSource.Dispose();
            }
            else
            {
                if (_backgroundTask != null)
                {
                    await _backgroundTask;
                }
            }
        }
    }
}
