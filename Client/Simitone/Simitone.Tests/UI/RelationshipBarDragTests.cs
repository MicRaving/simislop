using Simitone.Client.UI.Panels.LiveSubpanels;
using System;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// The relationship bar drag: turning a position along a bar into a relationship value, and
    /// putting the value back where the engine will keep it.
    ///
    /// <para>
    /// Retail's signature relationship interaction is grab the bar and pull it, and the panel
    /// had no mouse handling at all - the two bars were read-only pictures of the stored value.
    /// This covers the arithmetic and the write, which is the part that can be wrong in a way
    /// that still looks like a working drag: a value that runs past its end, a write that
    /// silently does nothing, or a write into a vector the engine has not padded yet.
    /// </para>
    ///
    /// <para>
    /// Pure static code: no graphics device, no content, no statics. The pixel-to-fraction step
    /// that the panel does from the pointer position lives in the panel, because it needs a
    /// mouse; everything downstream of it is here.
    /// </para>
    /// </summary>
    public sealed class RelationshipBarDragTests
    {
        // ---- the range the engine stores ---------------------------------------------------

        [Fact]
        public void TheRangeIsTheOnesTheEngineClampsTo()
        {
            // VMRelationship clamps a set relationship to -100..100 before storing it, so a drag
            // that produced a wider range would let the player set a value the very next script
            // to write that relationship would silently squash - and the bar would jump back.
            Assert.Equal((short)-100, RelationshipBarDrag.MinValue);
            Assert.Equal((short)100, RelationshipBarDrag.MaxValue);
        }

        [Theory]
        [InlineData((short)-100)]
        [InlineData((short)-1)]
        [InlineData((short)0)]
        [InlineData((short)1)]
        [InlineData((short)100)]
        public void AnInRangeValueIsLeftAlone(short value)
        {
            Assert.Equal(value, RelationshipBarDrag.Clamp(value));
        }

        [Theory]
        [InlineData((short)101)]
        [InlineData((short)300)]
        [InlineData(short.MaxValue)]
        public void AValueAboveTheTopIsPinnedToIt(short value)
        {
            Assert.Equal(RelationshipBarDrag.MaxValue, RelationshipBarDrag.Clamp(value));
        }

        [Theory]
        [InlineData((short)-101)]
        [InlineData((short)-300)]
        [InlineData(short.MinValue)]
        public void AValueBelowTheBottomIsPinnedToIt(short value)
        {
            Assert.Equal(RelationshipBarDrag.MinValue, RelationshipBarDrag.Clamp(value));
        }

        // ---- position to value -------------------------------------------------------------

        [Fact]
        public void TheLeftEdgeOfTheBarIsTheBottomOfTheRange()
        {
            Assert.Equal(RelationshipBarDrag.MinValue, RelationshipBarDrag.ValueForFraction(0f));
        }

        [Fact]
        public void TheRightEdgeOfTheBarIsTheTopOfTheRange()
        {
            Assert.Equal(RelationshipBarDrag.MaxValue, RelationshipBarDrag.ValueForFraction(1f));
        }

        [Fact]
        public void TheMiddleOfTheBarIsNeutral()
        {
            // The half-way point of a -100..100 range is exactly 0, which is the value a fresh
            // relationship starts at. A player who drags a bar to the middle has wiped the
            // relationship, and that has to be what the bar shows.
            Assert.Equal((short)0, RelationshipBarDrag.ValueForFraction(0.5f));
        }

        [Theory]
        [InlineData(-5f, RelationshipBarDrag.MinValue)]
        [InlineData(0f, RelationshipBarDrag.MinValue)]
        [InlineData(2f, RelationshipBarDrag.MaxValue)]
        [InlineData(1f, RelationshipBarDrag.MaxValue)]
        public void APointerOffEitherEndPinsTheValueToThatEnd(float fraction, short expected)
        {
            // Clamped, not wrapped. A wrap would throw a pointer dragged off the left edge to
            // the opposite extreme, which for a relationship is the difference between a sim
            // who dislikes you and a sim who adores you.
            Assert.Equal(expected, RelationshipBarDrag.ValueForFraction(fraction));
        }

        [Fact]
        public void ANotANumberFractionReadsAsNeutral()
        {
            // A degenerate bar width can produce one, and Math.Round of NaN is NaN, which does
            // not fit in a short. Neutral is the answer that writes something defensible.
            Assert.Equal((short)0, RelationshipBarDrag.ValueForFraction(float.NaN));
        }

        [Fact]
        public void TheMappingIsEvenAcrossTheBar()
        {
            // Ten steps along the bar, each worth 20 points. Anything other than an even spread
            // means one end of the bar is finer than the other, and a player aiming for "as
            // friendly as possible" would not get it.
            for (int i = 0; i <= 10; i++)
            {
                Assert.Equal((short)(-100 + (i * 20)), RelationshipBarDrag.ValueForFraction(i / 10f));
            }
        }

        [Fact]
        public void AQuarterOfTheBarIsAHundredBelowNeutral()
        {
            Assert.Equal((short)-50, RelationshipBarDrag.ValueForFraction(0.25f));
        }

        [Fact]
        public void ThreeQuartersOfTheBarIsAHundredAboveNeutral()
        {
            Assert.Equal((short)50, RelationshipBarDrag.ValueForFraction(0.75f));
        }

        // ---- value back to position ---------------------------------------------------------

        [Theory]
        [InlineData((short)-100, 0f)]
        [InlineData((short)0, 0.5f)]
        [InlineData((short)100, 1f)]
        public void AValueReadsBackAtThePositionThatWouldProduceIt(short value, float expected)
        {
            Assert.Equal(expected, RelationshipBarDrag.FractionForValue(value), 4);
        }

        [Fact]
        public void AValueOutsideTheRangeStillReadsBackAsAnEndOfTheBar()
        {
            // Person data is a raw short; a value the drag clamp would never produce still has
            // to draw somewhere sane rather than off the end of the bar.
            Assert.Equal(0f, RelationshipBarDrag.FractionForValue((short)-4000), 4);
            Assert.Equal(1f, RelationshipBarDrag.FractionForValue(4000), 4);
        }

        [Fact]
        public void EveryValueOnTheBarRoundTrips()
        {
            // The bar is drawn from FractionForValue and dragged into ValueForFraction, so if
            // the two disagree the bar drifts under the pointer: grab it at a point, move the
            // pointer nowhere, and the value changes.
            for (short value = RelationshipBarDrag.MinValue; value <= RelationshipBarDrag.MaxValue; value += 5)
            {
                var again = RelationshipBarDrag.ValueForFraction(RelationshipBarDrag.FractionForValue(value));
                Assert.True(Math.Abs(again - value) <= 1,
                    "value " + value + " came back as " + again);
            }
        }

        // ---- writing the value back ---------------------------------------------------------

        [Fact]
        public void ApplyWritesTheValueIntoTheVector()
        {
            var vector = new List<short> { 10, 0, 0 };

            Assert.True(RelationshipBarDrag.Apply(vector, 0, -75));
            Assert.Equal(new short[] { -75, 0, 0 }, vector);
        }

        [Fact]
        public void ApplyReportsNoChangeWhenTheValueIsAlreadyThere()
        {
            // The panel only invalidates and the engine only hears about a change when this
            // says so; a drag that reports a change every frame it does not move would be a
            // per-frame write of the same number for as long as the button is held.
            var vector = new List<short> { 10, 0, 0 };

            Assert.False(RelationshipBarDrag.Apply(vector, 0, 10));
            Assert.Equal(new short[] { 10, 0, 0 }, vector);
        }

        [Fact]
        public void ApplyClampsBeforeItWrites()
        {
            // The engine clamps too, but only when a script writes the relationship. A drag
            // that stored an out-of-range value would leave the bar showing something the next
            // script to touch that relationship would discard.
            var vector = new List<short> { 0, 0, 0 };

            Assert.True(RelationshipBarDrag.Apply(vector, 0, 5000));
            Assert.Equal(RelationshipBarDrag.MaxValue, vector[0]);
        }

        [Fact]
        public void ApplyPadsAVectorThatIsTooShortToHoldTheSlot()
        {
            // VMRelationship pads a relationship vector out to the slot it is writing, and a
            // drag needs the same: a relationship written by a build that stopped at the first
            // entry has no slot 1 or 2 to assign, and a drag that quietly did nothing would be
            // the least explicable thing the panel could do.
            var vector = new List<short> { 40 };

            Assert.True(RelationshipBarDrag.Apply(vector, 2, 30));
            Assert.Equal(new short[] { 40, 0, 30 }, vector);
        }

        [Fact]
        public void ApplyRefusesANullVector()
        {
            Assert.False(RelationshipBarDrag.Apply(null, 0, 50));
        }

        [Fact]
        public void ApplyRefusesANegativeSlot()
        {
            // A negative index is not a value the panel can ever ask for, and
            // List<short>'s indexer would throw on it.
            var vector = new List<short> { 0 };
            Assert.False(RelationshipBarDrag.Apply(vector, -1, 50));
        }

        [Fact]
        public void ApplyReportsNoChangeOnAnAlreadyClampedValue()
        {
            // Clamping happens before the comparison, so a drag pinned at the end of the bar
            // stops writing rather than writing 100 over 100 for every pixel past the end.
            var vector = new List<short> { 0, 0, 0 };
            RelationshipBarDrag.Apply(vector, 0, 400);

            Assert.False(RelationshipBarDrag.Apply(vector, 0, 4000));
        }

        [Fact]
        public void EachBarEditsItsOwnSlot()
        {
            // The two bars are the relationship and the friendship, and a drag on one must not
            // move the other. The tile reads [0] for the top bar and [2] for the bottom one.
            var vector = new List<short> { 10, 0, 20 };

            RelationshipBarDrag.Apply(vector, 0, -30);
            Assert.Equal(new short[] { -30, 0, 20 }, vector);

            RelationshipBarDrag.Apply(vector, 2, 60);
            Assert.Equal(new short[] { -30, 0, 60 }, vector);
        }
    }
}
