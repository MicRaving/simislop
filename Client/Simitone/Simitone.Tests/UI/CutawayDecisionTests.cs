using System.Collections.Generic;
using FSO.LotView;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the three cutaway decisions in <see cref="CutawayDecision"/>, lifted out of
    /// <c>UILotControl.UpdateCutaway</c>.
    ///
    /// The dynamic cutaway is a cache: the expensive <c>GenerateRoomCut</c> pass only reruns when
    /// the floor, the cutaway rotation or the hovered-room set actually changed
    /// (<see cref="CutawayDecision.ShouldRegenerate"/>), and the computed array is only pushed to
    /// the blueprint when the full regenerate happened or the small mouse rectangle actually cut
    /// something (<see cref="CutawayDecision.ShouldPushCut"/>).
    ///
    /// The invariant worth pinning: the room comparison is a <em>set</em> comparison. Two hovered
    /// room sets of the same size but different contents describe a different cutaway, and
    /// treating them as equal leaves a stale wall cut on screen until the player happens to move
    /// the mouse far enough to change the count.
    ///
    /// The mouse rectangle is a fixed 5x5 box centred on the mouse tile, shifted two tiles
    /// against the current cutaway direction (<see cref="VMArchitectureTools.CutCheckDir"/>) so it
    /// peeks past the wall the player is facing. The tests assert that offset relationship rather
    /// than the literal per-rotation coordinates, so a change to CutCheckDir shows up as a failing
    /// offset rather than four silently-wrong magic numbers.
    ///
    /// All of this is pure: HashSet comparison, Rectangle arithmetic and a static int[][] lookup.
    /// No graphics device, no content, no audio, no VM.
    /// </summary>
    public sealed class CutawayDecisionTests
    {
        private static HashSet<uint> Rooms(params uint[] ids)
        {
            return new HashSet<uint>(ids);
        }

        // ---- ShouldRegenerate ---------------------------------------------------------------

        [Fact]
        public void ShouldRegenerate_IsFalse_WhenFloorRotationAndRoomSetAllMatch()
        {
            Assert.False(CutawayDecision.ShouldRegenerate(
                Rooms(1, 2, 3), Rooms(1, 2, 3),
                2, 2,
                WorldRotation.BottomRight, WorldRotation.BottomRight));
        }

        [Fact]
        public void ShouldRegenerate_IsTrue_WhenTheFloorChanges()
        {
            Assert.True(CutawayDecision.ShouldRegenerate(
                Rooms(1, 2, 3), Rooms(1, 2, 3),
                3, 2,                          // current level 3, cache built for 2
                WorldRotation.BottomRight, WorldRotation.BottomRight));
        }

        [Fact]
        public void ShouldRegenerate_IsTrue_WhenTheRotationChanges()
        {
            Assert.True(CutawayDecision.ShouldRegenerate(
                Rooms(1, 2, 3), Rooms(1, 2, 3),
                2, 2,
                WorldRotation.TopLeft, WorldRotation.BottomRight));
        }

        [Fact]
        public void ShouldRegenerate_IsTrue_WhenTheRoomSetGrows()
        {
            Assert.True(CutawayDecision.ShouldRegenerate(
                Rooms(1, 2, 3), Rooms(1, 2),
                2, 2,
                WorldRotation.BottomRight, WorldRotation.BottomRight));
        }

        [Fact]
        public void ShouldRegenerate_IsTrue_WhenTheRoomSetShrinks()
        {
            Assert.True(CutawayDecision.ShouldRegenerate(
                Rooms(1, 2), Rooms(1, 2, 3),
                2, 2,
                WorldRotation.BottomRight, WorldRotation.BottomRight));
        }

        [Fact]
        public void ShouldRegenerate_IsTrue_WhenRoomSetsHaveTheSameCountButDifferentContents()
        {
            // the regression this guards: a count comparison would call these equal and leave a
            // stale wall cut behind.
            Assert.True(CutawayDecision.ShouldRegenerate(
                Rooms(1, 2, 3), Rooms(4, 5, 6),
                2, 2,
                WorldRotation.BottomRight, WorldRotation.BottomRight));
        }

        [Fact]
        public void ShouldRegenerate_IsTrue_WhenOneRoomIsReplacedInsideTheSet()
        {
            Assert.True(CutawayDecision.ShouldRegenerate(
                Rooms(1, 2, 3), Rooms(1, 2, 4),
                2, 2,
                WorldRotation.BottomRight, WorldRotation.BottomRight));
        }

        [Fact]
        public void ShouldRegenerate_IsFalse_RegardlessOfInsertionOrder()
        {
            // SetEquals is order-insensitive, and the hovered rooms arrive from a List that
            // rotates, so order must not count as a change.
            Assert.False(CutawayDecision.ShouldRegenerate(
                Rooms(3, 1, 2), Rooms(1, 2, 3),
                2, 2,
                WorldRotation.TopRight, WorldRotation.TopRight));
        }

        [Fact]
        public void ShouldRegenerate_IsTrue_WhenTheCacheWasNeverBuiltForThisFloor()
        {
            // UILotControl.LastFloor starts at -1 as its "nothing cached yet" sentinel.
            Assert.True(CutawayDecision.ShouldRegenerate(
                Rooms(1), Rooms(uint.MaxValue),
                0, -1,
                WorldRotation.TopLeft, WorldRotation.TopLeft));
        }

        [Fact]
        public void ShouldRegenerate_IsFalse_ForTwoEmptyRoomSets()
        {
            // SoftwareKeyboard mode can legitimately hover nothing at all.
            Assert.False(CutawayDecision.ShouldRegenerate(
                Rooms(), Rooms(),
                1, 1,
                WorldRotation.BottomLeft, WorldRotation.BottomLeft));
        }

        [Fact]
        public void ShouldRegenerate_IsTrue_ForAnEmptyRoomSetAgainstANonEmptyCache()
        {
            Assert.True(CutawayDecision.ShouldRegenerate(
                Rooms(), Rooms(7),
                1, 1,
                WorldRotation.BottomLeft, WorldRotation.BottomLeft));
        }

        // ---- ComputeMouseCutRect ------------------------------------------------------------

        [Theory]
        [InlineData(WorldRotation.TopLeft)]
        [InlineData(WorldRotation.TopRight)]
        [InlineData(WorldRotation.BottomRight)]
        [InlineData(WorldRotation.BottomLeft)]
        public void ComputeMouseCutRect_IsAFiveByFiveBoxCentredOnTheMouseTileAndShiftedAgainstCutCheckDir(
            WorldRotation cutRotation)
        {
            const int tileX = 10;
            const int tileY = 12;

            var rect = CutawayDecision.ComputeMouseCutRect(tileX, tileY, cutRotation);

            // the box itself never changes size or shape
            Assert.Equal(5, rect.Width);
            Assert.Equal(5, rect.Height);

            // base box: 5x5 around the mouse tile, computed exactly as the original inline code
            // did - (int)(tile - 2.5) truncates toward zero, so for positive tiles this is tile-3.
            int baseX = (int)(tileX - 2.5);
            int baseY = (int)(tileY - 2.5);
            Assert.Equal(tileX - 3, baseX);
            Assert.Equal(tileY - 3, baseY);

            int[] dir = VMArchitectureTools.CutCheckDir[(int)cutRotation];
            Assert.Equal(baseX - dir[0] * 2, rect.X);
            Assert.Equal(baseY - dir[1] * 2, rect.Y);
        }

        [Fact]
        public void ComputeMouseCutRect_SnapsTheHalfTileCentre_DroppingTheLowerTileNotTheUpper()
        {
            // (int)(x - 2.5) truncates toward zero, so 10 -> 7.5 -> 7. If this ever switched to
            // Math.Floor the whole cutaway would sit one tile north-west of the cursor.
            var rect = CutawayDecision.ComputeMouseCutRect(10, 12, WorldRotation.TopLeft);

            // CutCheckDir[TopLeft] == {-1,-1}, so the box moves +2 on both axes.
            Assert.Equal(9, rect.X);
            Assert.Equal(11, rect.Y);
            Assert.Equal(new Rectangle(9, 11, 5, 5), rect);
        }

        [Fact]
        public void ComputeMouseCutRect_ShiftsAwayFromTheWallThePlayerIsFacing()
        {
            // CutCheckDir points at the wall being cut, so subtracting it moves the box to the
            // far side of the wall. TopLeft(-1,-1) and BottomLeft(1,-1) therefore share a Y
            // offset and differ in X; the same holds for the right-hand pair.
            var topLeft = CutawayDecision.ComputeMouseCutRect(10, 12, WorldRotation.TopLeft);
            var topRight = CutawayDecision.ComputeMouseCutRect(10, 12, WorldRotation.TopRight);
            var bottomRight = CutawayDecision.ComputeMouseCutRect(10, 12, WorldRotation.BottomRight);
            var bottomLeft = CutawayDecision.ComputeMouseCutRect(10, 12, WorldRotation.BottomLeft);

            Assert.Equal(9, topLeft.X);
            Assert.Equal(11, topLeft.Y);

            Assert.Equal(9, topRight.X);
            Assert.Equal(7, topRight.Y);

            Assert.Equal(5, bottomRight.X);
            Assert.Equal(7, bottomRight.Y);

            Assert.Equal(5, bottomLeft.X);
            Assert.Equal(11, bottomLeft.Y);

            // the box always covers the same 5x5 span whichever way it moved
            foreach (var rect in new[] { topLeft, topRight, bottomRight, bottomLeft })
            {
                Assert.Equal(5, rect.Width);
                Assert.Equal(5, rect.Height);
            }
        }

        [Fact]
        public void ComputeMouseCutRect_MovesTheBoxByExactlyFourTilesBetweenOppositeRotations()
        {
            // TopLeft and BottomRight are diagonal opposites, so their CutCheckDir entries are
            // exact negatives and the two rects are offset by 4 on both axes.
            var topLeft = CutawayDecision.ComputeMouseCutRect(10, 12, WorldRotation.TopLeft);
            var bottomRight = CutawayDecision.ComputeMouseCutRect(10, 12, WorldRotation.BottomRight);

            Assert.Equal(4, topLeft.X - bottomRight.X);
            Assert.Equal(4, topLeft.Y - bottomRight.Y);
        }

        [Fact]
        public void ComputeMouseCutRect_IsStable_ForTheSameInputs_SoTheCallerCanCompareRectsForChange()
        {
            // UpdateCutaway only re-applies the rectangle when newCut != MouseCutRect, so an
            // unstable rect would re-cut the architecture every single frame.
            var first = CutawayDecision.ComputeMouseCutRect(37, 44, WorldRotation.BottomRight);
            var second = CutawayDecision.ComputeMouseCutRect(37, 44, WorldRotation.BottomRight);

            Assert.Equal(first, second);
        }

        [Fact]
        public void ComputeMouseCutRect_MovesTheBoxWithTheMouse()
        {
            var a = CutawayDecision.ComputeMouseCutRect(10, 12, WorldRotation.BottomRight);
            var b = CutawayDecision.ComputeMouseCutRect(11, 12, WorldRotation.BottomRight);

            Assert.Equal(a.X + 1, b.X);
            Assert.Equal(a.Y, b.Y);
        }

        // ---- ShouldPushCut -----------------------------------------------------------------

        [Theory]
        [InlineData(2, false, false)]  // regenerated: always pushed
        [InlineData(2, false, true)]
        [InlineData(2, true, false)]
        [InlineData(2, true, true)]
        [InlineData(1, true, false)]   // rect cut that changed something: pushed
        [InlineData(1, false, true)]  // revert of a previous notable rect cut: pushed
        [InlineData(1, true, true)]
        public void ShouldPushCut_IsTrue_ForARegenerateOrANotableChange(int recut, bool notableChange, bool lastRectCutNotable)
        {
            Assert.True(CutawayDecision.ShouldPushCut(recut, notableChange, lastRectCutNotable));
        }

        [Theory]
        [InlineData(0, false, false)]
        [InlineData(1, false, false)]
        public void ShouldPushCut_IsFalse_ForASilentRectangleCutWithNoPendingRevert(int recut, bool notableChange, bool lastRectCutNotable)
        {
            // nothing changed, so the blueprint keeps the cut array it already had - this is what
            // avoids a WALL_CUT_CHANGED flag (and a blueprint rewrite) on every idle frame.
            Assert.False(CutawayDecision.ShouldPushCut(recut, notableChange, lastRectCutNotable));
        }

        [Fact]
        public void ShouldPushCut_TreatsAnyRecutAboveTwoAsARegenerate()
        {
            Assert.True(CutawayDecision.ShouldPushCut(3, false, false));
            Assert.True(CutawayDecision.ShouldPushCut(int.MaxValue, false, false));
        }
    }
}
