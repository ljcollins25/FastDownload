// Copyright (C) Microsoft Corporation. All Rights Reserved.

namespace FastDownload.Utilities
{
    /// <summary>
    /// A class to allow rate limiting based on a specified amount of tokens per period.
    /// </summary>
    public interface ISpeedRateLimiter
    {
        /// <summary>
        /// The period of time over which the rate is calculated.
        /// </summary>
        public TimeSpan Period { get; }

        /// <summary>
        /// The number of permits that are refilled per period.
        /// </summary>
        public long RefillPerPeriod { get; }

        /// <summary>
        /// Adjust the refill rate to the target.
        /// </summary>
        void AdjustRefill(long target);

        /// <summary>
        /// Try to immediately acquire the specified number of permits.
        /// </summary>
        bool TryAcquire(long permits);

        /// <summary>
        /// Asynchronously acquire the specified number of permits.
        /// </summary>
        Task AcquireAsync(long permits, CancellationToken cancellationToken);
    }
}
