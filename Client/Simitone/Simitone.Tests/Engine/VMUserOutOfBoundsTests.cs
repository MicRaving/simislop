using FSO.LotView.Model;
using FSO.SimAntics;
using Microsoft.Xna.Framework;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMContext.IsUserOutOfBounds(LotTilePos, int, int, int, bool[], Rectangle)"/> -
    /// whether the user may place something at a tile.
    ///
    /// <para>
    /// The bug was three inverted early returns. The fine-plane branch of
    /// <c>IsUserOutOfBounds</c> answered "in bounds" for exactly the tiles that are out of bounds -
    /// past the lot edge, or on a level with no buildable floor - and only its <c>else</c> branch
    /// had the comparisons the right way round. The non-fine branch of the same method, three
    /// lines below, got all three right, which is what makes it a transcription slip rather than
    /// a decision.
    /// </para>
    ///
    /// <para>
    /// It mattered because <c>VMEntity.IndividualUserMovable</c> gates pickup on exactly this
    /// question - <c>if (context.IsUserOutOfBounds(Position)) return
    /// CantBePickedupOutOfBounds</c> - so with the check inverted, an object pushed off the edge of
    /// a lot that has a fine buildable area could still be picked up and moved.
    /// </para>
    ///
    /// <para>
    /// The rule is a pure function of the lot's numbers, so it is tested as one: no lot, no
    /// content manager, no graphics device. The interesting cases are the boundaries - one tile
    /// inside the edge, one outside; ground level, the ground below it, the top buildable floor and
    /// the floor above it - because a comparison that is off by one at the edge is exactly the
    /// kind of thing that only shows up as a sim standing in the wall.
    /// </para>
    /// </summary>
    public sealed class VMUserOutOfBoundsTests
    {
        /// <summary>A 32x32 lot with three buildable floors.</summary>
        private const int Width = 32;
        private const int Height = 32;
        private const int BuildableFloors = 3;

        /// <summary>
        /// A lot on which the user may build everywhere. A <c>FineBuildableArea</c> entry is
        /// <b>true where the user may build</b> - it comes from the house file's bit 32 and the
        /// check reads <c>!fine[i]</c> - so "everywhere" is all trues, not a fresh <c>bool[]</c>.
        /// </summary>
        private static bool[] FullyBuildable()
        {
            var fine = new bool[Width * Height];
            for (int i = 0; i < fine.Length; i++) fine[i] = true;
            return fine;
        }

        /// <summary>
        /// A position in <b>tiles</b>. <c>LotTilePos</c> stores x and y in sixteenths of a tile and
        /// exposes <c>TileX</c>/<c>TileY</c> as the shifted-down values, so a raw 32 is tile 2 -
        /// which is how a "one tile off the edge" test quietly becomes a "well inside" test.
        /// </summary>
        private static LotTilePos Tile(int tileX, int tileY, int level)
            => new LotTilePos((short)(tileX * 16), (short)(tileY * 16), (sbyte)level);

        private static bool OutOfBounds(LotTilePos pos, bool[] fine = null)
            => VMContext.IsUserOutOfBounds(pos, Width, Height, BuildableFloors, fine ?? FullyBuildable(),
                new Rectangle(0, 0, Width, Height));

        // ---- the inversion ----------------------------------------------------------------

        [Fact]
        public void ABuildableTileIsNotOutOfBounds()
        {
            Assert.False(OutOfBounds(Tile(0, 0, 1)));
            Assert.False(OutOfBounds(Tile(16, 16, 2)));
        }

        [Fact]
        public void ATileOffTheWestOrNorthEdgeIsOutOfBounds()
        {
            // The first of the three inverted returns: TileX/TileY below zero answered "in bounds".
            Assert.True(OutOfBounds(Tile(-1, 0, 1)));
            Assert.True(OutOfBounds(Tile(0, -1, 1)));
            Assert.True(OutOfBounds(Tile(-1, -1, 1)));
        }

        [Fact]
        public void ATileOffTheEastOrSouthEdgeIsOutOfBounds()
        {
            // The same return, other side of the comparison: TileX/TileY at or past the lot size.
            Assert.True(OutOfBounds(Tile(Width, 0, 1)));
            Assert.True(OutOfBounds(Tile(0, Height, 1)));
            Assert.True(OutOfBounds(Tile(Width, Height, 1)));
        }

        [Fact]
        public void TheLastTileOnTheLotIsStillOnIt()
        {
            // The boundary the inverted return got wrong in the other direction too: `>=` has to
            // reject the tile AT Width, not the one before it.
            Assert.False(OutOfBounds(Tile(Width - 1, Height - 1, 1)));
            Assert.True(OutOfBounds(Tile(Width, Height - 1, 1)));
            Assert.True(OutOfBounds(Tile(Width - 1, Height, 1)));
        }

        [Fact]
        public void ALevelWithNoBuildableFloorIsOutOfBounds()
        {
            // The third inverted return: Level below 1, and above BuildableFloors.
            Assert.True(OutOfBounds(Tile(0, 0, 0)));
            Assert.True(OutOfBounds(Tile(0, 0, -1)));
            Assert.True(OutOfBounds(Tile(0, 0, BuildableFloors + 1)));
        }

        [Fact]
        public void TheGroundAndTheTopBuildableFloorAreTheInclusiveEnds()
        {
            Assert.False(OutOfBounds(Tile(5, 5, 1)));
            Assert.False(OutOfBounds(Tile(5, 5, BuildableFloors)));
        }

        // ---- the fine plane is still what decides the interior ---------------------------

        [Fact]
        public void ATileInsideTheLotThatTheFinePlaneExcludesIsOutOfBounds()
        {
            // The branch that was already right, and the reason the fix is not "always false": a
            // lot with a hole in its buildable area must still refuse that hole.
            var fine = FullyBuildable();
            fine[4 + 9 * Width] = false;

            Assert.True(OutOfBounds(Tile(4, 9, 1), fine));
            Assert.False(OutOfBounds(Tile(4, 9, 1)));
        }

        [Fact]
        public void AFinePlaneShorterThanTheLotCountsAsNothingBuildable()
        {
            // One index further out of bounds than the width/height test covers. The answer is the
            // one a missing plane gives, rather than an IndexOutOfRangeException on load.
            var fine = new bool[4];

            Assert.True(OutOfBounds(Tile(0, 0, 1), fine));
            Assert.True(OutOfBounds(Tile(3, 0, 1), fine));
        }

        [Fact]
        public void ABuildableRectangularAreaIsTheFallbackWhenThereIsNoFinePlane()
        {
            // The other branch of the original method, unchanged - and the one that was already
            // right, which is the evidence the fine branch's inversions were a slip.
            var area = new Rectangle(8, 8, 16, 16);

            Assert.False(VMContext.IsUserOutOfBounds(Tile(8, 8, 1), Width, Height, BuildableFloors, null, area));
            Assert.False(VMContext.IsUserOutOfBounds(Tile(23, 23, 1), Width, Height, BuildableFloors, null, area));
            Assert.True(VMContext.IsUserOutOfBounds(Tile(7, 8, 1), Width, Height, BuildableFloors, null, area));
            Assert.True(VMContext.IsUserOutOfBounds(Tile(24, 23, 1), Width, Height, BuildableFloors, null, area));
            Assert.True(VMContext.IsUserOutOfBounds(Tile(8, 8, 0), Width, Height, BuildableFloors, null, area));
            Assert.True(VMContext.IsUserOutOfBounds(Tile(8, 8, BuildableFloors + 1), Width, Height, BuildableFloors, null, area));
        }

        [Fact]
        public void TheFinePlaneTakesPrecedenceWhenBothArePresent()
        {
            // A lot has both; asking the rectangle question would answer "in bounds" for the whole
            // lot, including the parts the fine plane excludes.
            var fine = FullyBuildable();
            var area = new Rectangle(0, 0, Width, Height);

            Assert.False(VMContext.IsUserOutOfBounds(Tile(2, 2, 1), Width, Height, BuildableFloors, fine, area));
            fine[2 + 2 * Width] = false;
            Assert.True(VMContext.IsUserOutOfBounds(Tile(2, 2, 1), Width, Height, BuildableFloors, fine, area));
        }

        [Fact]
        public void TheTwoBranchesAgreeAboutTheEdgesTheyBothCover()
        {
            // A cheap cross-check: for the tiles that are inside both the lot and the rectangle,
            // the two branches must not disagree about anything but the fine plane. This is what
            // makes the fine branch's inversions a slip rather than a different rule.
            var area = new Rectangle(0, 0, Width, Height);
            var fine = FullyBuildable();

            for (int x = -1; x <= Width; x++)
            {
                for (int y = -1; y <= Height; y++)
                {
                    var pos = Tile(x, y, 1);
                    Assert.Equal(VMContext.IsUserOutOfBounds(pos, Width, Height, BuildableFloors, null, area),
                                 VMContext.IsUserOutOfBounds(pos, Width, Height, BuildableFloors, fine, area));
                }
            }
        }
    }
}
