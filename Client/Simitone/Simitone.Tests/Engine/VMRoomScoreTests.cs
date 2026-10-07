using FSO.SimAntics;
using System;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMRoomScore"/> - the score a light group is given, and where its outside-ness
    /// comes from.
    ///
    /// <para>
    /// The bug was in <c>VMContext.RefreshRoomScore</c>: the walk over the group's support rooms
    /// reused the caller's own <c>info</c> variable as its loop variable, so by the time the
    /// method asked "is this room outside" to pick the area scale and the penalty, <c>info</c>
    /// pointed at the <em>last</em> support room rather than the base room. The score is written
    /// to the light object the group shares, so a wrong one is visible in every room of the group,
    /// and room score is what drives light placement and the "dark room" behaviours.
    /// </para>
    ///
    /// <para>
    /// Its sibling <c>RefreshLighting</c> does the same walk and uses a separate <c>info2</c>,
    /// which is what makes the original a bug rather than a choice - the two methods were written
    /// from the same template and one of them kept it. Both now call the one core below, so the
    /// arithmetic has a single copy and the question of which room is outside has left the loop.
    /// </para>
    ///
    /// <para>
    /// The property under test is the one the bug broke: <b>outside-ness is a property of the
    /// group, not of a room in it</b>. Area genuinely is a sum over the group - every room
    /// contributes - but a single one of the rooms being outdoors must not change the answer.
    /// </para>
    /// </summary>
    public sealed class VMRoomScoreTests
    {
        /// <summary>
        /// The formula the room score has always been, restated from the constants rather than
        /// copied from the implementation: the divisor is applied to the <em>area</em>, and the
        /// impact is divided by that. Written out so a wrong expectation is a wrong expectation
        /// about the rule and not a mistyped division.
        /// </summary>
        private static short Expected(bool isOutside, int area, int roomImpact)
        {
            var scale = Math.Max(1, area / VMRoomScore.InsideAreaDivisor);
            if (isOutside) scale = VMRoomScore.OutsideAreaDivisor;

            var score = (short)(roomImpact / scale) - (isOutside ? VMRoomScore.OutsidePenalty : VMRoomScore.InsidePenalty);
            return (short)Math.Min(VMRoomScore.Max, Math.Max(VMRoomScore.Min, score));
        }

        // ---- the formula ------------------------------------------------------------------

        [Theory]
        [InlineData(false, 12, 120)]
        [InlineData(false, 120, 600)]
        [InlineData(false, 600, 600)]
        [InlineData(true, 120, 600)]
        [InlineData(true, 600, 600)]
        [InlineData(false, 0, 30)]
        [InlineData(true, 0, 30)]
        public void TheScoreIsTheRuleItHasAlwaysBeen(bool isOutside, int area, int roomImpact)
        {
            // Valid behaviour has to be identical after the fix; this is the whole rule, checked
            // across the divisor boundary, the floor and the outdoor case.
            Assert.Equal(Expected(isOutside, area, roomImpact), VMRoomScore.Compute(isOutside, area, roomImpact));
        }

        // ---- the two things that must not be confused ------------------------------------

        [Fact]
        public void AnOutdoorGroupIsScaledAndPenalisedAsAnOutdoorGroup()
        {
            // Same totals, different group: the outside case divides by 30 instead of
            // area/12, and takes 15 off instead of 10.
            var inside = VMRoomScore.Compute(isOutside: false, area: 1200, roomImpact: 1200);
            var outside = VMRoomScore.Compute(isOutside: true, area: 1200, roomImpact: 1200);

            Assert.Equal((short)2, inside);       // 1200 / 100 - 10
            Assert.Equal((short)25, outside);     // 1200 /  30 - 15
            Assert.NotEqual(inside, outside);
        }

        [Fact]
        public void TheOutsideFlagIsTheGroupsAndNotAnyRoomsInIt()
        {
            // The bug, stated as a property. The old method read IsOutside off whichever room the
            // loop happened to leave in the variable, so the score of a group whose LAST support
            // room happened to be outdoor was computed as an outdoor group - and written to every
            // room's light. The core takes one flag, and it is the base room's: nothing a support
            // room is can change the answer.
            var asTheBaseRoomSays = VMRoomScore.Compute(isOutside: false, area: 120, roomImpact: 600);
            var asTheLastSupportRoomSaid = VMRoomScore.Compute(isOutside: true, area: 120, roomImpact: 600);

            Assert.Equal((short)50, asTheBaseRoomSays);   // 600 / 10 - 10
            Assert.Equal((short)5, asTheLastSupportRoomSaid); // 600 / 30 - 15
            Assert.NotEqual(asTheBaseRoomSays, asTheLastSupportRoomSaid);
        }

        // ---- area is a sum over the whole group ------------------------------------------

        [Fact]
        public void TheAreaScaleFloorsAtOne()
        {
            // Math.Max(1, area/12f): a small room is not divided to nothing, which would round
            // every impact away. 12 is exactly the divisor, so the floor and the division agree
            // there; 1 and 0 are strictly below it.
            Assert.Equal((short)20, VMRoomScore.Compute(false, area: 12, roomImpact: 30));
            Assert.Equal((short)20, VMRoomScore.Compute(false, area: 1, roomImpact: 30));
            Assert.Equal((short)20, VMRoomScore.Compute(false, area: 0, roomImpact: 30));
        }

        [Fact]
        public void AnEmptyGroupStillTakesThePenalty()
        {
            // Zero area and zero impact is a room with nothing lit in it, which is the common case
            // this method exists to express - not a division by zero.
            Assert.Equal((short)-10, VMRoomScore.Compute(false, 0, 0));
            Assert.Equal((short)-15, VMRoomScore.Compute(true, 0, 0));
        }

        [Fact]
        public void RoomImpactAccumulatesAcrossTheGroup()
        {
            // Two rooms contributing 60 each is the same total as one room contributing 120, and
            // the score is over the group's area, not each room's own.
            Assert.Equal(
                VMRoomScore.Compute(false, area: 120, roomImpact: 60 + 60),
                VMRoomScore.Compute(false, area: 120, roomImpact: 120));
        }

        [Fact]
        public void MoreAreaInTheSameGroupMeansALessBrilliantRoom()
        {
            // The other half of "area is a sum over the group": the same lights in twice the area
            // score half as high, because the room score is an average over the group. The fixed
            // penalty is not part of the average, which is why this is not exactly half.
            var small = VMRoomScore.Compute(false, area: 120, roomImpact: 480);   // 480/10 - 10
            var large = VMRoomScore.Compute(false, area: 240, roomImpact: 480);   // 480/20 - 10

            Assert.Equal((short)38, small);
            Assert.Equal((short)14, large);
            Assert.True(large < small);
        }

        // ---- the clamp ---------------------------------------------------------------------

        [Fact]
        public void AVeryBrightRoomIsClampedAtAHundredAndAVeryDarkOneAtMinusAHundred()
        {
            // Room score is a -100..100 word that light placement reads, so it cannot be allowed to
            // run away in either direction.
            Assert.Equal((short)VMRoomScore.Max, VMRoomScore.Compute(false, 12, 200));
            Assert.Equal((short)VMRoomScore.Min, VMRoomScore.Compute(false, 12, -200));
        }

        [Fact]
        public void AScoreInsideTheRangeIsUntouched()
        {
            // The clamp is not the rule - a mid-range score is the score.
            Assert.Equal((short)42, VMRoomScore.Compute(false, 120, 520));
            Assert.Equal((short)-42, VMRoomScore.Compute(false, 120, -320));
        }

        [Fact]
        public void ThePenaltiesAndDivisorsAreTheOnesTheEngineHasAlwaysUsed()
        {
            // Pinned so a "tidier" constant cannot quietly rescale every room in the game.
            Assert.Equal(12f, VMRoomScore.InsideAreaDivisor);
            Assert.Equal(30f, VMRoomScore.OutsideAreaDivisor);
            Assert.Equal(10, VMRoomScore.InsidePenalty);
            Assert.Equal(15, VMRoomScore.OutsidePenalty);
            Assert.Equal(-100, VMRoomScore.Min);
            Assert.Equal(100, VMRoomScore.Max);
        }
    }
}
