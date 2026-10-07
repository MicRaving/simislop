using System.Collections.Generic;
using FSO.LotView.Components;
using FSO.LotView.Model;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests the "this tile cannot emit anything, step over it" mask that the wall sprite loop uses
    /// on real (synthetic) <see cref="Blueprint"/> data, without needing a graphics device.
    ///
    /// The optimisation rests on that mask being a <em>superset</em> of everything the old
    /// unconditional walk could have produced a sprite for. The loop emits exactly two things:
    ///
    ///   * wall segments, for a tile whose <c>WallTile.Segments != 0</c>, and
    ///   * a junction cap, for a tile that has junction flags on it.
    ///
    /// A junction flag only ever arrives by a wall tile OR-ing its bits into its own slot and into
    /// off+1, off+width and off+1+width. So the mask must contain every wall tile and every one of
    /// those three targets, and - for the optimisation to be worth anything - must not contain much
    /// else. Both halves are asserted here, against layouts with corners, diagonals and map edges in
    /// them, because getting the second half wrong is how a "faster" loop silently drops a junction.
    ///
    /// The expected set is written out here from the draw side of the pre-pass rather than by asking
    /// the component for it, so a change to where the pre-pass puts its junction bits fails the
    /// test instead of silently agreeing with itself.
    /// </summary>
    public class WallEmitMaskTests
    {
        /// <summary>A small lot with a wall, and the row-major offsets it implies.</summary>
        private sealed class Lot
        {
            public readonly int Size;
            public readonly Blueprint Blueprint;
            public readonly int Total;

            public Lot(int size)
            {
                Size = size;
                Total = size * size;
                Blueprint = new Blueprint(size, size);
            }

            /// <summary>Mirrors what <c>VMArchitecture.SetWall</c> does to the wall array and its index.</summary>
            public void SetWall(int level, int x, int y, WallTile wall)
            {
                var off = y * Size + x;
                var walls = Blueprint.Walls[level - 1];
                var at = Blueprint.WallsAt[level - 1];
                at.Remove(off);
                if (wall.Segments != 0)
                {
                    walls[off] = wall;
                    at.Add(off);
                }
                else
                {
                    walls[off] = new WallTile();
                }
            }

            public bool[] Mask(int level = 1, bool cutaway = true)
            {
                return Blueprint.WallComp.BuildEmitMask((sbyte)level, cutaway);
            }

            /// <summary>What the loop needs, derived from the draw side of the pre-pass.</summary>
            public bool[] Expected(int level)
            {
                var walls = Blueprint.Walls[level - 1];
                var at = Blueprint.WallsAt[level - 1];
                var expected = new bool[Total];
                foreach (var off in at)
                {
                    //a wall tile draws its own segments...
                    expected[off] = true;
                    //...and pushes a junction into each of these three slots
                    if (off + 1 < Total) expected[off + 1] = true;
                    if (off + Size < Total) expected[off + Size] = true;
                    if (off + Size + 1 < Total) expected[off + Size + 1] = true;
                }
                //belt and braces: nothing that carries a wall may sit outside the set
                for (var off = 0; off < Total; off++)
                {
                    if (walls[off].Segments != 0) expected[off] = true;
                }
                return expected;
            }
        }

        private static WallTile Wall(WallSegments segments, ushort style = 1)
        {
            return new WallTile
            {
                Segments = segments,
                TopLeftPattern = 1,
                TopRightPattern = 2,
                BottomLeftPattern = 3,
                BottomRightPattern = 4,
                TopLeftStyle = style,
                TopRightStyle = style
            };
        }

        private static int Area(bool[] mask)
        {
            var count = 0;
            foreach (var b in mask) if (b) count++;
            return count;
        }

        private static void AssertSuperset(Lot lot, int level = 1)
        {
            var mask = lot.Mask(level);
            var expected = lot.Expected(level);
            Assert.Equal(expected.Length, mask.Length);
            for (var off = 0; off < mask.Length; off++)
            {
                Assert.True(mask[off] || !expected[off],
                    $"offset {off} (x {off % lot.Size}, y {off / lot.Size}) can emit but the mask lets the loop step over it");
            }
        }

        [Fact]
        public void AnEmptyLotEmitsNothing()
        {
            var lot = new Lot(24);
            Assert.Equal(0, Area(lot.Mask()));
        }

        [Fact]
        public void ASingleWallMarksItselfAndItsThreeNeighbours()
        {
            var lot = new Lot(24);
            lot.SetWall(1, 10, 10, Wall(WallSegments.TopLeft | WallSegments.TopRight));

            var mask = lot.Mask();
            var off = 10 * 24 + 10;
            Assert.True(mask[off]);          // itself
            Assert.True(mask[off + 1]);      // right neighbour, gets the TopLeft junction
            Assert.True(mask[off + 24]);     // below, gets the TopRight junction
            Assert.True(mask[off + 24 + 1]);
            Assert.False(mask[off - 1]);
            Assert.False(mask[off - 24]);
            Assert.Equal(4, Area(mask));

            AssertSuperset(lot);
        }

        [Fact]
        public void AWallOnTheRightEdgeWrapsItsJunctionOntoTheNextRow()
        {
            // off+1 for the last column is the first column of the next row, and that is exactly the
            // slot the draw loop reads when it reaches that tile - the wrap is how the junction
            // reaches across the row, so the mask has to follow it.
            var lot = new Lot(24);
            lot.SetWall(1, 23, 5, Wall(WallSegments.TopLeft));

            var mask = lot.Mask();
            var off = 5 * 24 + 23;
            Assert.True(mask[off]);        // (23, 5), itself
            Assert.True(mask[off + 1]);    // (0, 6), the wrapped neighbour
            Assert.True(mask[off + 24]);   // (23, 6)
            Assert.True(mask[off + 25]);   // (0, 7)
            Assert.Equal(4, Area(mask));

            AssertSuperset(lot);
        }

        [Fact]
        public void AWallOnTheBottomRowDropsTheSlotsThatFallOffTheMap()
        {
            var lot = new Lot(24);
            lot.SetWall(1, 5, 23, Wall(WallSegments.TopLeft | WallSegments.TopRight));

            var mask = lot.Mask();
            var off = 23 * 24 + 5;
            Assert.True(mask[off]);
            Assert.True(mask[off + 1]);
            Assert.Equal(2, Area(mask));     // off+24 and off+25 are past the end of the array

            AssertSuperset(lot);
        }

        [Fact]
        public void DiagonalWallsAreMarkedLikeAnyOther()
        {
            var lot = new Lot(24);
            lot.SetWall(1, 3, 3, Wall(WallSegments.HorizontalDiag));
            lot.SetWall(1, 4, 4, Wall(WallSegments.VerticalDiag));

            var mask = lot.Mask();
            var a = 3 * 24 + 3;
            var b = 4 * 24 + 4;
            Assert.True(mask[a]);
            Assert.True(mask[b]);
            // a vertical diag pushes its DiagTop one row down and one column right
            Assert.True(mask[b + 24 + 1]);

            AssertSuperset(lot);
        }

        [Fact]
        public void CornersShareTheirJunctionSlots()
        {
            var lot = new Lot(24);
            for (var i = 0; i < 8; i++)
            {
                lot.SetWall(1, 4 + i, 4, Wall(WallSegments.TopLeft | WallSegments.TopRight));
                lot.SetWall(1, 4, 4 + i, Wall(WallSegments.TopLeft | WallSegments.TopRight));
            }

            var mask = lot.Mask();
            // 8 tiles of run plus 8 tiles of column, meeting at (4,4). Each run dilates to a
            // two-tile band, so the shape is rows 4-5 across columns 4-12 unioned with columns 4-5
            // down rows 4-12: 18 + 18 - 4 shared.
            Assert.Equal(32, Area(mask));
            for (var i = 0; i < 9; i++)
            {
                Assert.True(mask[4 * 24 + 4 + i]);
                Assert.True(mask[5 * 24 + 4 + i]);
                Assert.True(mask[(4 + i) * 24 + 4]);
                Assert.True(mask[(4 + i) * 24 + 5]);
            }
            Assert.False(mask[0]);
            Assert.False(mask[23 * 24 + 23]);

            AssertSuperset(lot);
        }

        [Fact]
        public void ARoomOutlineIsMarkedAroundItsCornersToo()
        {
            var lot = new Lot(24);
            for (var i = 0; i <= 5; i++)
            {
                lot.SetWall(1, 4 + i, 4, Wall(WallSegments.TopLeft | WallSegments.TopRight));
                lot.SetWall(1, 4 + i, 9, Wall(WallSegments.TopLeft | WallSegments.TopRight));
                lot.SetWall(1, 4, 4 + i, Wall(WallSegments.TopLeft | WallSegments.TopRight));
                lot.SetWall(1, 9, 4 + i, Wall(WallSegments.TopLeft | WallSegments.TopRight));
            }

            AssertSuperset(lot);
        }

        [Fact]
        public void ARealisticLotLeavesMostTilesSkippable()
        {
            // Four 8x8 room outlines on a 64x64 lot: 112 wall tiles in 4096. This is the number that
            // says whether the step-over is worth anything, so it is asserted tightly - the mask
            // cannot be more than about twice the wall count, because every wall tile adds at most
            // three slots and neighbours share them.
            var lot = new Lot(64);
            for (var ry = 0; ry < 2; ry++)
            {
                for (var rx = 0; rx < 2; rx++)
                {
                    var x0 = 4 + rx * 28;
                    var y0 = 4 + ry * 28;
                    for (var i = 0; i < 8; i++)
                    {
                        lot.SetWall(1, x0 + i, y0, Wall(WallSegments.TopLeft | WallSegments.TopRight));
                        lot.SetWall(1, x0 + i, y0 + 7, Wall(WallSegments.TopLeft | WallSegments.TopRight));
                        lot.SetWall(1, x0, y0 + i, Wall(WallSegments.TopLeft | WallSegments.TopRight));
                        lot.SetWall(1, x0 + 7, y0 + i, Wall(WallSegments.TopLeft | WallSegments.TopRight));
                    }
                }
            }

            var mask = lot.Mask();
            AssertSuperset(lot);

            var wallTiles = lot.Blueprint.WallsAt[0].Count;
            Assert.Equal(4 * 28, wallTiles);            // 4 rooms, 28 tiles of 8x8 outline each
            var area = Area(mask);
            Assert.True(area >= wallTiles);
            Assert.True(area <= wallTiles * 2, $"mask covered {area} tiles for {wallTiles} walls");
            Assert.True(area < lot.Total / 8, $"mask covered {area} of {lot.Total} tiles");
        }

        [Fact]
        public void DeletingAWallShrinksTheMaskAgain()
        {
            var lot = new Lot(24);
            lot.SetWall(1, 10, 10, Wall(WallSegments.TopLeft | WallSegments.TopRight));
            Assert.Equal(4, Area(lot.Mask()));

            lot.SetWall(1, 10, 10, new WallTile());
            Assert.Equal(0, Area(lot.Mask()));
            AssertSuperset(lot);
        }

        [Fact]
        public void EveryStoreyGetsItsOwnMask()
        {
            var lot = new Lot(24);
            lot.SetWall(1, 2, 2, Wall(WallSegments.TopLeft));
            lot.SetWall(3, 20, 20, Wall(WallSegments.TopLeft));

            var ground = lot.Mask(1);
            var second = lot.Mask(2);
            var third = lot.Mask(3);

            Assert.True(ground[2 * 24 + 2]);
            Assert.False(ground[20 * 24 + 20]);
            Assert.True(third[20 * 24 + 20]);
            Assert.False(third[2 * 24 + 2]);
            Assert.Equal(0, Area(second));

            AssertSuperset(lot, 1);
            AssertSuperset(lot, 2);
            AssertSuperset(lot, 3);
        }

        [Fact]
        public void TheMaskDoesNotDependOnTheCutaway()
        {
            // The mask is built from WallsAt, not from the cut, so whether the storey is the cut one
            // cannot move it. If this ever fails, the mask has picked up a cutaway dependency and
            // would be wrong for the storeys that are not cut.
            var lot = new Lot(24);
            for (var i = 0; i < 6; i++)
            {
                lot.SetWall(1, 4 + i, 4, Wall(WallSegments.TopLeft | WallSegments.TopRight));
            }

            var top = lot.Mask(1, true);
            var below = lot.Mask(1, false);
            Assert.Equal(Area(top), Area(below));
            for (var off = 0; off < top.Length; off++) Assert.Equal(top[off], below[off]);
        }

        [Fact]
        public void StyledAndDoorWallsAreStillMarked()
        {
            // The mask is deliberately not clever about style: a tile is either in it or it is not,
            // and anything carrying a segment is in it.
            var lot = new Lot(24);
            lot.SetWall(1, 6, 6, Wall(WallSegments.TopLeft, style: 5));
            var doorWall = Wall(WallSegments.TopLeft | WallSegments.TopRight);
            doorWall.TopLeftDoor = true;
            lot.SetWall(1, 7, 6, doorWall);

            var mask = lot.Mask();
            Assert.True(mask[6 * 24 + 6]);
            Assert.True(mask[6 * 24 + 7]);
            AssertSuperset(lot);
        }

        [Fact]
        public void APatternOnlyChangeDoesNotGrowTheMask()
        {
            // Re-styling a wall is a data change the recache has to see, but it does not move the
            // set of tiles that can emit - the region arithmetic handles that, not this mask.
            var lot = new Lot(24);
            lot.SetWall(1, 8, 8, Wall(WallSegments.TopLeft, style: 1));
            var before = Area(lot.Mask());

            lot.SetWall(1, 8, 8, Wall(WallSegments.TopLeft, style: 9));
            Assert.Equal(before, Area(lot.Mask()));
            AssertSuperset(lot);
        }
    }
}
