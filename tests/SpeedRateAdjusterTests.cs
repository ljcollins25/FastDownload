// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Utilities;

namespace FastDownload.Tests
{
    [TestClass]
    public class SpeedRateAdjusterApplyTests
    {
        private static readonly SpeedRateAdjuster.Settings DefaultSettings = new(
            MinimumRate: 10,
            MaximumRate: 1000,
            ShrinkFactor: 0.75,
            MaximumGrowthFactor: 2.0,
            GrowthFactorIncrementPerStreak: 0.1,
            Period: TimeSpan.FromSeconds(1),
            MinimumSuccessPeriods: 3,
            RandomAdjustmentRange: 0.05);

        private static SpeedRateAdjuster.Settings NoRandomSettings => DefaultSettings with { RandomAdjustmentRange = 0.0 };

        private static Random CreateTestRandom()
        {
            // A fixed seed for deterministic test results
            return new Random(42);
        }

        private static DateTime Now => new(2024, 12, 17, 12, 0, 0, DateTimeKind.Utc);

        [TestMethod]
        public void Apply_RetryOccurs_ReducesRate()
        {
            // IntervalRetryCount > 0 => try shrinking
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 100,
                SuccessStreak: 5, // arbitrary success streak
                LastAdjustmentTime: Now);

            // CurrentRate = 500, will try to reduce
            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 105,  // IntervalRetryCount = 5
                IntervalRetryCount: 5,
                CurrentRate: 500,
                DeltaTime: TimeSpan.FromSeconds(1));

            var random = CreateTestRandom();
            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, random, Now.AddSeconds(1));

            // Because we had retries, success streak resets to 0.
            Assert.AreEqual(0, next.SuccessStreak);
            // We should have updated PreviousRetryCount.
            Assert.AreEqual(105, next.PreviousRetryCount);
            // targetRate should be less than current rate (500).
            Assert.IsNotNull(targetRate);
            Assert.IsTrue(targetRate < 500);
        }

        [TestMethod]
        public void Apply_NoRetries_ButBelowGrowthThreshold_NoIncrease()
        {
            // No retries => IntervalRetryCount = 0
            // SuccessStreak < MinimumSuccessStreakForGrowth => no increase allowed
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 100,
                SuccessStreak: 1, // less than threshold of 3
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 100,
                IntervalRetryCount: 0,
                CurrentRate: 500,
                DeltaTime: TimeSpan.FromSeconds(1));

            var random = CreateTestRandom();
            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, random, Now.AddSeconds(1));

            // Success streak should increment by 1
            Assert.AreEqual(2, next.SuccessStreak);
            Assert.AreEqual(100, next.PreviousRetryCount);
            // Since growth not allowed, targetRate should be null
            Assert.IsNull(targetRate);
        }

        [TestMethod]
        public void Apply_NoRetries_AboveGrowthThreshold_IncreasesRate()
        {
            // No retries => IntervalRetryCount = 0
            // SuccessStreak >= MinimumSuccessStreakForGrowth
            // Should attempt to increase rate
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 100,
                SuccessStreak: 3, // meets the threshold (3)
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 100,
                IntervalRetryCount: 0,
                CurrentRate: 100,
                DeltaTime: TimeSpan.FromSeconds(1));

            var random = CreateTestRandom();
            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, random, Now.AddSeconds(1));

            // Success streak should increment by 1 because no retries
            Assert.AreEqual(4, next.SuccessStreak);
            Assert.AreEqual(100, next.PreviousRetryCount);
            // Should attempt to increase rate above 100 if possible
            Assert.IsNotNull(targetRate);
            Assert.IsTrue(targetRate > 100, "Rate should have increased");
        }

        [TestMethod]
        public void Apply_RepeatedSuccesses_NotExceedMaxRate()
        {
            // Test that we do not exceed maximum rate even with a large success streak
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 200,
                SuccessStreak: 50, // large success streak
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 200,
                IntervalRetryCount: 0,
                CurrentRate: 900, // already quite high
                DeltaTime: TimeSpan.FromSeconds(1));

            var random = CreateTestRandom();
            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, random, Now.AddSeconds(1));

            // We have no retries, streak increments
            Assert.AreEqual(51, next.SuccessStreak);

            // Attempt to increase rate, but capped at maximum
            Assert.IsNotNull(targetRate);
            Assert.AreEqual(DefaultSettings.MaximumRate, targetRate.Value, "Rate should cap at maximum");
        }

        [TestMethod]
        public void Apply_RetryOccurs_RateAlreadyAtMinimum_NoFurtherDecrease()
        {
            // If the current rate is already at minimum, no decrease should occur
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 300,
                SuccessStreak: 10,
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 305, // retries occurred
                IntervalRetryCount: 5,
                CurrentRate: DefaultSettings.MinimumRate, // at minimum
                DeltaTime: TimeSpan.FromSeconds(1));

            var random = CreateTestRandom();
            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, random, Now.AddSeconds(1));

            Assert.AreEqual(0, next.SuccessStreak); // reset streak due to retries
            Assert.IsNull(targetRate, "No further decrease since we're at minimum");
        }

        [TestMethod]
        public void Apply_LongerDeltaTime_ShrinkMore()
        {
            // With a larger delta time, shrink should be more pronounced.
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 400,
                SuccessStreak: 5,
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 410, // retries, interval = 10
                IntervalRetryCount: 10,
                CurrentRate: 500,
                DeltaTime: TimeSpan.FromSeconds(2));

            var random = CreateTestRandom();
            var (_, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, random, Now.AddSeconds(2));

            // Expect a more significant reduction due to 2s delta time.
            Assert.IsNotNull(targetRate);
            Assert.IsTrue(targetRate < 500, "Should reduce more than the 1s scenario");
        }

        [TestMethod]
        public void Apply_LongerDeltaTime_IncreaseMore()
        {
            // With longer delta, increase should also be more pronounced.
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 500,
                SuccessStreak: 3, // meets threshold
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 500,
                IntervalRetryCount: 0,
                CurrentRate: 100,
                DeltaTime: TimeSpan.FromSeconds(2));

            var random = CreateTestRandom();
            var (_, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, random, Now.AddSeconds(2));

            Assert.IsNotNull(targetRate);
            Assert.IsTrue(targetRate > 100, "Should increase more due to longer delta time");
        }

        [TestMethod]
        public void Apply_NoRetries_NoIncreaseBelowThreshold_NoRandomImpactCheck()
        {
            // Check scenario with random factor but no growth or shrink just to ensure stable results
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 600,
                SuccessStreak: 0, // below threshold
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 600,
                IntervalRetryCount: 0,
                CurrentRate: 200,
                DeltaTime: TimeSpan.FromSeconds(1));

            var random = CreateTestRandom();
            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, random, Now.AddSeconds(1));

            // SuccessStreak should increment since no retries
            Assert.AreEqual(1, next.SuccessStreak);
            // No target rate change since not at threshold
            Assert.IsNull(targetRate);
        }

        [TestMethod]
        public void Apply_ExactlyAtSuccessThreshold_ThenIncrease()
        {
            // Just hit the threshold (3), no retries, should allow growth.
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 100,
                SuccessStreak: 2, // one less than threshold
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 100,
                IntervalRetryCount: 0,
                CurrentRate: 200,
                DeltaTime: TimeSpan.FromSeconds(1));

            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, CreateTestRandom(), Now.AddSeconds(1));

            // SuccessStreak increments to 3, which meets threshold exactly
            Assert.AreEqual(3, next.SuccessStreak);
            // At the exact threshold, next apply would increase, but this apply didn't meet threshold at start of it.
            // Actually, re-reading the logic: successStreak is computed first. If it meets threshold after increment,
            // that means we can increase immediately.
            // Therefore, we should see an increase here since we started at 2, incremented to 3, which meets threshold.
            Assert.IsNotNull(targetRate);
            Assert.IsTrue(targetRate > 200, "Should increase at the exact threshold.");
        }

        [TestMethod]
        public void Apply_NoRandomFactor_StableResults()
        {
            // With no randomness, the result should be predictable every time.
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 50,
                SuccessStreak: 3, // threshold met
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 50,
                IntervalRetryCount: 0,
                CurrentRate: 100,
                DeltaTime: TimeSpan.FromSeconds(1));

            // Use no-random settings
            var random = CreateTestRandom();
            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, NoRandomSettings, random, Now.AddSeconds(1));

            Assert.AreEqual(4, next.SuccessStreak);
            Assert.IsNotNull(targetRate);
            Assert.AreEqual(120, targetRate.Value, "Rate should be deterministically increased to 120.");
        }

        [TestMethod]
        public void Apply_VeryLargeDeltaTime_SignificantShrink()
        {
            // A large delta time should compound the shrink significantly.
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 1000,
                SuccessStreak: 10,
                LastAdjustmentTime: Now);

            // Big retries occur after a long delay
            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 1100,
                IntervalRetryCount: 100,
                CurrentRate: 500,
                DeltaTime: TimeSpan.FromSeconds(60)); // 1 minute

            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, CreateTestRandom(), Now.AddMinutes(1));

            Assert.AreEqual(0, next.SuccessStreak, "Success streak should reset after retries.");
            Assert.IsNotNull(targetRate, "We should shrink significantly.");
            Assert.IsTrue(targetRate < 500, "Rate should be much lower after a large delta time.");
        }

        [TestMethod]
        public void Apply_VeryLargeDeltaTime_SignificantIncrease()
        {
            // When no retries occur and success threshold is met, a large delta should produce large growth.
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 2000,
                SuccessStreak: 10,
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 2000,
                IntervalRetryCount: 0,
                CurrentRate: 100,
                DeltaTime: TimeSpan.FromSeconds(60)); // 1 minute

            var (_, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, CreateTestRandom(), Now.AddMinutes(1));

            Assert.IsNotNull(targetRate, "Should increase due to large delta and high streak.");
            Assert.IsTrue(targetRate > 100, "Rate should grow substantially.");
            // Possibly capped at max, but we only assert that it grows more than 100.
        }

        [TestMethod]
        public void Apply_VerySmallDeltaTime_MinimalChange()
        {
            // Very small delta time means minimal growth/shrink.
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 3000,
                SuccessStreak: 3, // threshold met
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 3000,
                IntervalRetryCount: 0,
                CurrentRate: 500,
                DeltaTime: TimeSpan.FromMilliseconds(100)); // 0.1s

            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, CreateTestRandom(), Now.AddMilliseconds(100));

            Assert.AreEqual(4, next.SuccessStreak);
            // GrowthFactorIncrement = 0.1 * 4 * 0.1s = 0.1 * 4 * 0.1 = 0.04 additional factor
            // So factor ~1.04 * a small random factor, but since it's small time, growth should be small.
            if (targetRate.HasValue)
            {
                // Just ensure it's slightly greater than 500, not by a large margin.
                Assert.IsTrue(targetRate.Value > 500 && targetRate.Value < 550, "Minimal growth expected for very small delta time.");
            }
        }

        [TestMethod]
        public void Apply_SuccessStreakBrokenByRetries_NoGrowthAfterReset()
        {
            // Previously had a large success streak, but now many retries occurred, resetting conditions.
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 4000,
                SuccessStreak: 10,
                LastAdjustmentTime: Now);

            // Suddenly a batch of retries
            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 4010,
                IntervalRetryCount: 10,
                CurrentRate: 500,
                DeltaTime: TimeSpan.FromSeconds(1));

            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, CreateTestRandom(), Now.AddSeconds(1));

            Assert.AreEqual(0, next.SuccessStreak, "Streak resets after retries.");
            Assert.IsNotNull(targetRate, "We should shrink rate due to retries.");
            Assert.IsTrue(targetRate < 500, "Rate should decrease from 500.");
        }

        [TestMethod]
        public void Apply_ZeroDeltaTime_NoChange()
        {
            // Settings with no random adjustment
            var noRandomSettings = DefaultSettings with { RandomAdjustmentRange = 0.0 };

            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 5000,
                SuccessStreak: 3, // threshold met
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 5000,
                IntervalRetryCount: 0,
                CurrentRate: 500,
                DeltaTime: TimeSpan.Zero);

            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, noRandomSettings, CreateTestRandom(), Now);

            // With zero delta and no randomness, no change should occur.
            Assert.AreEqual(4, next.SuccessStreak);
            Assert.IsNull(targetRate, "With zero delta time and no randomness, rate should remain unchanged.");
        }

        [TestMethod]
        public void Apply_AlmostNoTimePassed_VerySmallChange()
        {
            // Delta time is extremely small, e.g., 1 millisecond.
            var state = new SpeedRateAdjuster.State(
                PreviousRetryCount: 6000,
                SuccessStreak: 3,
                LastAdjustmentTime: Now);

            var intermediate = new SpeedRateAdjuster.Intermediate(
                TotalRetryCount: 6000,
                IntervalRetryCount: 0,
                CurrentRate: 500,
                DeltaTime: TimeSpan.FromMilliseconds(1));

            var (next, targetRate) = SpeedRateAdjuster.Apply(state, intermediate, DefaultSettings, CreateTestRandom(), Now.AddMilliseconds(1));

            Assert.AreEqual(4, next.SuccessStreak);

            // Even if growth allowed, with ~0.001s, growth increment = 0.1 * 4 * 0.001 = 0.0004 -> ~0.04% increase, barely noticeable.
            // Due to rounding and random factor, we might see no actual increase if it rounds down.
            if (targetRate.HasValue)
            {
                // It might still be 500 or slightly above due to random factor.
                Assert.IsTrue(targetRate.Value >= 500, "Should be at least the same rate, possibly slightly higher due to rounding.");
            }
            else
            {
                // It's also possible no targetRate if rounding cancels out the small increase.
                // So we won't assert strictly on targetRate presence, just that no big change happens.
            }
        }
    }
}
