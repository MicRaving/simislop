using System;
using FSO.Common.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// <see cref="DirectionUtils.PosMod"/> is the wrap-around guard used by the CAS carousel in
    /// TS1CASScreen / UISelectSkinAlert. Those call sites look like:
    ///
    ///     i = (int)DirectionUtils.PosMod(-BodyPositionLast, 18);
    ///     i = (int)DirectionUtils.PosMod(i, ActiveBodies.Count);
    ///     ... ActiveBodies[i] ...
    ///
    /// i.e. an untrusted (possibly negative) carousel position is turned into a list index
    /// straight away. The property the call sites depend on, and the only thing that keeps the
    /// index inside the list, is that PosMod always lands in [0, m). If that ever stops being
    /// true, the CAS throws IndexOutOfRange mid-frame instead of wrapping.
    ///
    /// DirectionUtils is pure static double math - no graphics, no content, no statics.
    /// </summary>
    public sealed class DirectionUtilsTests
    {
        [Theory]
        [InlineData(0, 18, 0)]
        [InlineData(5, 18, 5)]
        [InlineData(17, 18, 17)]
        [InlineData(18, 18, 0)]     // exactly one lap
        [InlineData(19, 18, 1)]
        [InlineData(36, 18, 0)]     // exactly two laps
        [InlineData(-1, 18, 17)]
        [InlineData(-9, 18, 9)]
        [InlineData(-18, 18, 0)]
        [InlineData(-19, 18, 17)]
        [InlineData(-36, 18, 0)]
        public void PosMod_WrapsIntoZeroThroughM_Exclusive(double x, double m, double expected)
        {
            Assert.Equal(expected, DirectionUtils.PosMod(x, m), 10);
        }

        [Theory]
        [InlineData(18)]
        [InlineData(3)]
        [InlineData(1)]
        public void PosMod_IsAlwaysNonNegative_ForTheRangeOfPositionsTheCarouselCanProduce(int m)
        {
            for (int x = -200; x <= 200; x++)
            {
                var result = DirectionUtils.PosMod(x, m);

                Assert.True(result >= 0, $"PosMod({x}, {m}) = {result} is negative");
                Assert.True(result < m, $"PosMod({x}, {m}) = {result} is not less than {m}");
            }
        }

        [Fact]
        public void PosMod_ResultIsAlwaysSafeToCastToInt_AndIndexWith()
        {
            // this mirrors the CAS guards literally: the double is cast down and used as an index.
            const int count = 7;
            var source = new[] { -19, -8, -1, 0, 1, 6, 7, 8, 19 };

            foreach (var raw in source)
            {
                int i = (int)DirectionUtils.PosMod(raw, count);

                Assert.InRange(i, 0, count - 1);
            }
        }

        [Theory]
        [InlineData(-0.5, 0.5)]
        [InlineData(-0.25, 0.75)]
        [InlineData(0.25, 0.25)]
        [InlineData(1.0, 0.0)]
        [InlineData(2.5, 0.5)]
        public void PosMod_WrapsNormalisedValuesIntoZeroToOne(double x, double expected)
        {
            // TS1CASScreen normalises a fractional carousel position with PosMod(value, 1.0)
            // and then casts it to a float, so the result must land in [0, 1).
            var result = DirectionUtils.PosMod(x, 1.0);

            Assert.Equal(expected, result, 10);
            Assert.InRange(result, 0.0, 0.9999999);
        }

        [Theory]
        [InlineData(0.0, 0.0)]
        [InlineData(Math.PI / 2, Math.PI / 2)]
        [InlineData(-Math.PI / 2, -Math.PI / 2)]
        [InlineData(3 * Math.PI / 2, -Math.PI / 2)]
        [InlineData(-3 * Math.PI / 2, Math.PI / 2)]
        [InlineData(4 * Math.PI, 0.0)]
        public void Normalize_CollapsesAnyRadianDirectionIntoTheHalfTurnAroundZero(double dir, double expected)
        {
            Assert.Equal(expected, DirectionUtils.Normalize(dir), 10);
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(90.0)]
        [InlineData(-90.0)]
        [InlineData(270.0)]
        [InlineData(-270.0)]
        [InlineData(720.0)]
        [InlineData(-720.0)]
        public void NormalizeDegrees_CollapsesAnyDegreeDirectionIntoTheHalfTurnAroundZero(double dir)
        {
            var result = DirectionUtils.NormalizeDegrees(dir);

            Assert.InRange(result, -180.0, 180.0);
        }

        [Fact]
        public void Difference_IsTheShortestSignedArcFromAToB()
        {
            // used by UILotControlTouchHelper while dragging to rotate the lot
            Assert.Equal(0.0, DirectionUtils.Difference(1.0, 1.0), 10);
            Assert.Equal(Math.PI / 2, DirectionUtils.Difference(0.0, Math.PI / 2), 10);
            Assert.Equal(-Math.PI / 2, DirectionUtils.Difference(0.0, -Math.PI / 2), 10);

            // rotating "the long way round" must come back as the short arc, not as ~2*PI.
            // (an exact half turn is deliberately not asserted: PosMod(-PI, 2PI) is exactly PI
            // and the "> PI" fold leaves the sign as +PI - both half turns are one angle.)
            Assert.Equal(-Math.PI / 2, DirectionUtils.Difference(0.0, 3 * Math.PI / 2), 10);
            Assert.Equal(Math.PI / 2, DirectionUtils.Difference(0.0, -3 * Math.PI / 2), 10);
        }
    }
}
