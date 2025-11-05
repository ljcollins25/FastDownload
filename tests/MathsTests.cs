// Copyright (C) Microsoft Corporation. All Rights Reserved.

using FastDownload.Utilities;

namespace FastDownload.Tests
{
    [TestClass]
    public class MathsTests
    {
        [TestMethod]
        public void GreatestCommonDivisor_PositiveNumbers()
        {
            Assert.AreEqual(6, Maths.GreatestCommonDivisor(54, 24));
        }

        [TestMethod]
        public void GreatestCommonDivisor_OneZero()
        {
            Assert.AreEqual(10, Maths.GreatestCommonDivisor(10, 0));
        }

        [TestMethod]
        public void GreatestCommonDivisor_BothZeros()
        {
            Assert.AreEqual(0, Maths.GreatestCommonDivisor(0, 0));
        }

        [TestMethod]
        public void GreatestCommonDivisor_NegativeNumbers()
        {
            Assert.AreEqual(-4, Maths.GreatestCommonDivisor(-8, -12));
        }

        [TestMethod]
        public void LowestCommonMultiple_PositiveNumbers()
        {
            Assert.AreEqual(72, Maths.LowestCommonMultiple(24, 18));
        }

        [TestMethod]
        public void LowestCommonMultiple_WithZero()
        {
            Assert.AreEqual(0, Maths.LowestCommonMultiple(0, 10));
        }

        [TestMethod]
        public void LowestCommonMultiple_NegativeNumbers()
        {
            Assert.AreEqual(-36, Maths.LowestCommonMultiple(-9, -12));
        }

        [TestMethod]
        public void AlignTo_NoRemainder()
        {
            Assert.AreEqual(16u, Maths.AlignTo(16u, 4u));
        }

        [TestMethod]
        public void AlignTo_NeedsAlignment()
        {
            Assert.AreEqual(32u, Maths.AlignTo(30u, 16u));
        }

        [TestMethod]
        public void AlignTo_AlignmentIsOne()
        {
            Assert.AreEqual(7u, Maths.AlignTo(7u, 1u));
        }

        [TestMethod]
        public void AlignTo_ValueIsZero()
        {
            Assert.AreEqual(0u, Maths.AlignTo(0u, 8u));
        }
    }
}
