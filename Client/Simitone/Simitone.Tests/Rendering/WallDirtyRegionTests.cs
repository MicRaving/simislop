using System;
using System.Collections.Generic;
using FSO.LotView.Model;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for the tile-region arithmetic behind the dirty-region 2D wall recache
    /// (<see cref="WallTileRegion"/>, <see cref="WallDirtyRegionMath"/>, <see cref="WallDirtyRegion"/>
    /// and <see cref="WallDirtyDiff"/>).
    ///
    /// The optimisation replaces "re-walk every tile of the lot" with "rebuild the tiles a change
    /// can reach, replay the rest", so the only thing that has to be exactly right is *which tiles
    /// get rebuilt*. Too tight and a wall keeps a stale rounded end; too wide and the saving
    /// evaporates. The bound is set by how far a wall tile's sprites read:
    /// the thickness/continuation checks at (0,±1), (-1,1), (±1,0) and (1,-1), the diagonal-behind
    /// check at (-1,-1), <c>RotateWall</c>'s style/door lookups at (0,1) and (1,0),
    /// <c>RotateCuts</c>'s cut lookups at (0,1) and (1,0), and the junction bits a wall tile ORs
    /// into off+1, off+width and off+1+width. All of those are one tile away at most, so
    /// <see cref="WallDirtyRegionMath.Halo"/> is 1 - and these tests are what keep it at 1.
    ///
    /// The tests also pin the awkward cases: the halo has to be clipped, not wrapped, at every map
    /// edge and corner, and a change set has to be allowed to give up and demand a full recache
    /// rather than pretend a whole-lot edit is cheap.
    /// </summary>
    public class WallDirtyRegionTests
    {
        // ------------------------------------------------------------------ WallTileRegion basics

        [Fact]
        public void EmptyRegionContainsNothing()
        {
            Assert.True(WallTileRegion.Empty.IsEmpty);
            Assert.Equal(0, WallTileRegion.Empty.Area);
            Assert.False(WallTileRegion.Empty.Contains(0, 0));
            Assert.False(WallTileRegion.Empty.Contains(-1, -1));
            Assert.Equal(WallTileRegion.Empty, new WallTileRegion(3, 3, 0, 5));
            Assert.Equal(WallTileRegion.Empty, new WallTileRegion(3, 3, 5, -2));
        }

        [Fact]
        public void RegionIsHalfOpen()
        {
            var r = new WallTileRegion(2, 3, 4, 5);
            Assert.Equal(2, r.X);
            Assert.Equal(6, r.Right);
            Assert.Equal(3, r.Y);
            Assert.Equal(8, r.Bottom);
            Assert.Equal(20, r.Area);

            Assert.True(r.Contains(2, 3));      // top left corner is inside
            Assert.True(r.Contains(5, 7));      // bottom right pixel is inside
            Assert.False(r.Contains(6, 7));     // one past the right edge
            Assert.False(r.Contains(5, 8));     // one past the bottom edge
            Assert.False(r.Contains(1, 3));     // one before the left edge
            Assert.False(r.Contains(2, 2));     // one before the top edge
        }

        [Fact]
        public void ContainmentOfAnotherRegion()
        {
            var big = new WallTileRegion(0, 0, 10, 10);
            Assert.True(big.Contains(new WallTileRegion(2, 2, 3, 3)));
            Assert.True(big.Contains(WallTileRegion.Empty));
            Assert.False(big.Contains(new WallTileRegion(8, 8, 5, 5)));
            Assert.False(WallTileRegion.Empty.Contains(big));
        }

        [Fact]
        public void UnionIsCommutativeAndAbsorbsEmpty()
        {
            var a = new WallTileRegion(1, 1, 2, 2);
            var b = new WallTileRegion(8, 4, 1, 1);
            var expected = new WallTileRegion(1, 1, 8, 4);
            Assert.Equal(expected, WallDirtyRegionMath.Union(a, b));
            Assert.Equal(expected, WallDirtyRegionMath.Union(b, a));
            Assert.Equal(a, WallDirtyRegionMath.Union(a, WallTileRegion.Empty));
            Assert.Equal(a, WallDirtyRegionMath.Union(WallTileRegion.Empty, a));
        }

        [Fact]
        public void UnionOfOverlappingRegionsDoesNotDoubleCount()
        {
            var a = new WallTileRegion(0, 0, 5, 5);
            var b = new WallTileRegion(3, 3, 5, 5);
            Assert.Equal(new WallTileRegion(0, 0, 8, 8), WallDirtyRegionMath.Union(a, b));
        }

        // ------------------------------------------------------------------ halo

        [Fact]
        public void HaloIsOneTile()
        {
            // If the continuation/thickness lookups ever reach two tiles out, this is the assertion
            // that has to move - and with it the halo itself.
            Assert.Equal(1, WallDirtyRegionMath.Halo);
        }

        [Fact]
        public void SingleInteriorTileGrowsToThreeByThree()
        {
            var r = new WallTileRegion(10, 10, 1, 1);
            Assert.Equal(new WallTileRegion(9, 9, 3, 3), WallDirtyRegionMath.Dilate(r, WallDirtyRegionMath.Halo));
        }

        [Fact]
        public void SingleCornerTileIsClippedNotWrapped()
        {
            // A halo of one around (0,0) must not spill to x == -1 or y == -1; the wall sprite loop
            // only ever visits tiles inside the map, so a region naming anything else is a bug that
            // would only show up as an out-of-range read.
            var r = WallDirtyRegionMath.DilateAndClamp(WallTileRegion.Single(0, 0), 77, 77, WallDirtyRegionMath.Halo);
            Assert.Equal(new WallTileRegion(0, 0, 2, 2), r);
            Assert.True(r.Contains(0, 0));
            Assert.True(r.Contains(1, 1));
            Assert.False(r.Contains(2, 2));
        }

        [Fact]
        public void SingleTileOnEachEdgeAndCornerIsClipped()
        {
            const int w = 77;
            const int h = 77;
            var edges = new (int x, int y)[]
            {
                (0, 0), (0, 1), (1, 0),                  // top left corner and its neighbours
                (w - 1, 0), (0, h - 1), (w - 1, h - 1),  // the other three corners
                (w - 1, 10), (10, h - 1),                // right and bottom edges
            };

            foreach (var (x, y) in edges)
            {
                var r = WallDirtyRegionMath.DilateAndClamp(WallTileRegion.Single(x, y), w, h, WallDirtyRegionMath.Halo);
                Assert.False(r.IsEmpty);
                Assert.True(r.Contains(x, y));
                Assert.True(r.X >= 0 && r.Y >= 0);
                Assert.True(r.Right <= w && r.Bottom <= h);
                // a one-tile halo can never produce more than three tiles per axis
                Assert.True(r.Width <= 3 && r.Height <= 3);
            }
        }

        [Fact]
        public void SingleTileOnANarrowMapLosesTheHaloOnBothSides()
        {
            var r = WallDirtyRegionMath.DilateAndClamp(WallTileRegion.Single(0, 0), 1, 1, WallDirtyRegionMath.Halo);
            Assert.Equal(new WallTileRegion(0, 0, 1, 1), r);
        }

        [Fact]
        public void DiagonalPairKeepsTheHollowCornerOut()
        {
            // Two tiles touching only at a corner. Their halos together cover 4x4, and the two
            // tiles that are only corner-neighbours of both are inside it - which is exactly the set
            // of tiles whose rounded wall ends and junctions can move.
            var a = WallTileRegion.Single(10, 10);
            var b = WallTileRegion.Single(11, 11);
            var united = WallDirtyRegionMath.DilateAndClamp(a, 64, 64, WallDirtyRegionMath.Halo);
            united = WallDirtyRegionMath.Union(united,
                WallDirtyRegionMath.DilateAndClamp(b, 64, 64, WallDirtyRegionMath.Halo));
            Assert.Equal(new WallTileRegion(9, 9, 4, 4), united);
            Assert.True(united.Contains(10, 11));
            Assert.True(united.Contains(11, 10));
            Assert.True(united.Contains(9, 9));
            Assert.True(united.Contains(12, 12));
        }

        [Fact]
        public void DiagonalRunStaysSquareBecauseThatIsTheTruth()
        {
            // A diagonal wall run really does make every tile along it dirty - the bounding box is
            // not a pessimistic artefact here, it is the answer.
            var united = WallTileRegion.Empty;
            for (var i = 0; i < 8; i++)
            {
                united = WallDirtyRegionMath.Union(united,
                    WallDirtyRegionMath.DilateAndClamp(WallTileRegion.Single(20 + i, 20 + i), 64, 64, WallDirtyRegionMath.Halo));
            }
            Assert.Equal(new WallTileRegion(19, 19, 10, 10), united);
        }

        [Fact]
        public void StraightWallRunStaysThreeTilesWide()
        {
            // The case the wall brush actually produces. Dilating the union tile by tile keeps this
            // a three-tile band; dilating the bounding box of the changed tiles would have produced
            // a 22x22 square for the same run, which is the difference between the optimisation
            // working and not.
            var united = WallTileRegion.Empty;
            for (var x = 10; x < 30; x++)
            {
                united = WallDirtyRegionMath.Union(united,
                    WallDirtyRegionMath.DilateAndClamp(WallTileRegion.Single(x, 25), 64, 64, WallDirtyRegionMath.Halo));
            }
            Assert.Equal(new WallTileRegion(9, 24, 22, 3), united);
        }

        [Fact]
        public void StraightWallRunAlongTheTopEdgeIsTwoTilesTall()
        {
            var united = WallTileRegion.Empty;
            for (var x = 0; x < 10; x++)
            {
                united = WallDirtyRegionMath.Union(united,
                    WallDirtyRegionMath.DilateAndClamp(WallTileRegion.Single(x, 0), 64, 64, WallDirtyRegionMath.Halo));
            }
            Assert.Equal(new WallTileRegion(0, 0, 11, 2), united);
        }

        [Fact]
        public void TwoFarApartTilesCoverBothAndEverythingBetween()
        {
            var a = WallDirtyRegionMath.DilateAndClamp(WallTileRegion.Single(2, 2), 64, 64, WallDirtyRegionMath.Halo);
            var b = WallDirtyRegionMath.DilateAndClamp(WallTileRegion.Single(60, 60), 64, 64, WallDirtyRegionMath.Halo);
            var united = WallDirtyRegionMath.Union(a, b);
            Assert.True(united.Contains(2, 2));
            Assert.True(united.Contains(60, 60));
            Assert.True(united.Contains(30, 30));
        }

        [Fact]
        public void ClampDropsARegionEntirelyOutsideTheMap()
        {
            Assert.True(WallDirtyRegionMath.Clamp(new WallTileRegion(-5, -5, 2, 2), 64, 64).IsEmpty);
            Assert.True(WallDirtyRegionMath.Clamp(new WallTileRegion(70, 70, 4, 4), 64, 64).IsEmpty);
            Assert.Equal(new WallTileRegion(60, 60, 4, 4),
                WallDirtyRegionMath.Clamp(new WallTileRegion(60, 60, 10, 10), 64, 64));
        }

        [Fact]
        public void ClampToRenderDomainNeverLeavesTheMap()
        {
            var wide = new WallTileRegion(-3, -3, 200, 200);
            var clamped = WallDirtyRegionMath.ClampToRenderDomain(wide, 64, 64);
            Assert.Equal(WallTileRegion.Full(64, 64), clamped);
        }

        // ------------------------------------------------------------------ full-recache threshold

        [Fact]
        public void SingleTileNeverAsksForAFullRecache()
        {
            var r = WallDirtyRegionMath.DilateAndClamp(WallTileRegion.Single(38, 38), 77, 77, WallDirtyRegionMath.Halo);
            Assert.False(WallDirtyRegionMath.NeedsFullRecache(r, 77, 77));
        }

        [Fact]
        public void WholeMapAsksForAFullRecache()
        {
            var r = WallTileRegion.Full(77, 77);
            Assert.Equal(1d, WallDirtyRegionMath.AreaFraction(r, 77, 77));
            Assert.True(WallDirtyRegionMath.NeedsFullRecache(r, 77, 77));
        }

        [Fact]
        public void ThresholdIsHonouredOnBothSides()
        {
            // 20% of a 100x100 map is 2000 tiles. 39x50 is 19.5% and stays partial; 40x50 is exactly
            // 20% and gives up, because at that point a full pass does less work than replaying.
            Assert.False(WallDirtyRegionMath.NeedsFullRecache(new WallTileRegion(0, 0, 39, 50), 100, 100, 20));
            Assert.True(WallDirtyRegionMath.NeedsFullRecache(new WallTileRegion(0, 0, 40, 50), 100, 100, 20));
            Assert.True(WallDirtyRegionMath.NeedsFullRecache(new WallTileRegion(0, 0, 40, 51), 100, 100, 20));
            // 99.9% still counts as full, and 0.1% still does not
            Assert.True(WallDirtyRegionMath.NeedsFullRecache(new WallTileRegion(0, 0, 100, 100), 100, 100, 20));
            Assert.False(WallDirtyRegionMath.NeedsFullRecache(new WallTileRegion(0, 0, 1, 1), 100, 100, 20));
        }

        [Fact]
        public void EmptyRegionIsNotTreatedAsFull()
        {
            // Nothing changed is not "everything changed" - it is a cheap pass that replays the lot.
            Assert.False(WallDirtyRegionMath.NeedsFullRecache(WallTileRegion.Empty, 100, 100));
            Assert.Equal(0d, WallDirtyRegionMath.AreaFraction(WallTileRegion.Empty, 100, 100));
        }

        [Fact]
        public void TinyMapFractionIsComputedWithoutOverflow()
        {
            Assert.Equal(0.25d, WallDirtyRegionMath.AreaFraction(new WallTileRegion(0, 0, 1, 1), 2, 2), 6);
            Assert.Equal(0d, WallDirtyRegionMath.AreaFraction(new WallTileRegion(0, 0, 1, 1), 0, 0));
        }

        // ------------------------------------------------------------------ per-storey set

        [Fact]
        public void SetStartsEmpty()
        {
            var set = new WallDirtyRegion(5, 77, 77);
            Assert.True(set.IsEmpty);
            Assert.False(set.Full);
            Assert.Equal(5, set.Stories);
            for (var level = 1; level <= 5; level++) Assert.True(set.Level(level).IsEmpty);
        }

        [Fact]
        public void SetKeepsStoreysApart()
        {
            var set = new WallDirtyRegion(5, 77, 77);
            set.AddTile(1, 10, 10);
            set.AddTile(3, 40, 50);

            Assert.Equal(new WallTileRegion(9, 9, 3, 3), set.Level(1));
            Assert.True(set.Level(2).IsEmpty);
            Assert.Equal(new WallTileRegion(39, 49, 3, 3), set.Level(3));
            Assert.True(set.Level(4).IsEmpty);
            Assert.False(set.IsEmpty);
        }

        [Fact]
        public void SetAccumulatesWithinAStorey()
        {
            var set = new WallDirtyRegion(5, 77, 77);
            set.AddTile(1, 10, 10);
            set.AddTile(1, 12, 10);
            // adjacent tiles share their halos, so the band is three tiles tall and five wide
            Assert.Equal(new WallTileRegion(9, 9, 5, 3), set.Level(1));
        }

        [Fact]
        public void AddOffsetMapsRowMajorOffsetsToTiles()
        {
            var set = new WallDirtyRegion(1, 16, 16);
            set.AddOffset(1, 5 * 16 + 7, 16); // row 5, column 7
            var r = set.Level(1);
            Assert.True(r.Contains(7, 5));
            Assert.True(r.Contains(6, 5));
            Assert.True(r.Contains(8, 5));
            Assert.True(r.Contains(7, 4));
            Assert.True(r.Contains(7, 6));
        }

        [Fact]
        public void OutOfRangeStoreysAndTilesAreIgnored()
        {
            var set = new WallDirtyRegion(3, 77, 77);
            set.AddTile(0, 10, 10);
            set.AddTile(4, 10, 10);
            set.AddTile(2, -5, -5);
            set.AddTile(2, 1000, 1000);
            Assert.True(set.IsEmpty);

            // A tile on the very edge of the map is still recorded, just without a halo to give.
            set.AddTile(2, 0, 0);
            Assert.Equal(new WallTileRegion(0, 0, 2, 2), set.Level(2));
        }

        [Fact]
        public void MarkFullSwallowsEverything()
        {
            var set = new WallDirtyRegion(3, 77, 77);
            set.AddTile(1, 10, 10);
            set.MarkFull();
            Assert.True(set.Full);
            Assert.False(set.IsEmpty);
            Assert.True(set.Level(1).IsEmpty);

            set.AddTile(1, 20, 20);
            Assert.True(set.Level(1).IsEmpty); // still full, nothing accumulates
        }

        [Fact]
        public void MarkLevelFullOnlyTouchesThatStorey()
        {
            var set = new WallDirtyRegion(3, 77, 77);
            set.MarkLevelFull(2);
            Assert.False(set.Full);
            Assert.Equal(WallTileRegion.Full(77, 77), set.Level(2));
            Assert.True(set.Level(1).IsEmpty);
            Assert.True(set.Level(3).IsEmpty);
        }

        [Fact]
        public void ResolveFullRecachePromotesALargeStoreyAndNothingElse()
        {
            var small = new WallDirtyRegion(5, 100, 100);
            small.AddTile(1, 50, 50);
            Assert.False(small.ResolveFullRecache());
            Assert.False(small.Full);

            var big = new WallDirtyRegion(5, 100, 100);
            big.AddRegion(2, new WallTileRegion(0, 0, 50, 50));
            Assert.True(big.ResolveFullRecache());
            Assert.True(big.Full);
        }

        [Fact]
        public void WholeMapChangeResolvesToAFullRecache()
        {
            var set = new WallDirtyRegion(5, 32, 32);
            for (var y = 0; y < 32; y++) set.AddRegion(1, new WallTileRegion(0, y, 32, 1));
            Assert.Equal(WallTileRegion.Full(32, 32), set.Level(1));
            Assert.True(set.ResolveFullRecache());
        }

        [Fact]
        public void DilatedAreaSumsTheStoreys()
        {
            var set = new WallDirtyRegion(3, 77, 77);
            set.AddTile(1, 10, 10);
            set.AddTile(3, 10, 10);
            Assert.Equal(18, set.DilatedArea());
        }

        // ------------------------------------------------------------------ diffing before/after

        private static WallTile Wall(WallSegments segments, ushort style, bool door = false)
        {
            return new WallTile
            {
                Segments = segments,
                TopLeftPattern = 1,
                TopRightPattern = 2,
                BottomLeftPattern = 3,
                BottomRightPattern = 4,
                TopLeftStyle = style,
                TopRightStyle = style,
                ObjSetTLStyle = 0,
                ObjSetTRStyle = 0,
                TopLeftDoor = door,
                TopRightDoor = false,
                OccupiedWalls = 0
            };
        }

        [Fact]
        public void SameWallComparesEveryField()
        {
            var a = Wall(WallSegments.TopLeft, 1);
            Assert.True(WallDirtyDiff.SameWall(a, Wall(WallSegments.TopLeft, 1)));
            Assert.False(WallDirtyDiff.SameWall(a, Wall(WallSegments.TopLeft, 2)));
            Assert.False(WallDirtyDiff.SameWall(a, Wall(WallSegments.TopRight, 1)));
            Assert.False(WallDirtyDiff.SameWall(a, Wall(WallSegments.TopLeft, 1, door: true)));

            var b = a;
            b.ObjSetTRStyle = 9;
            Assert.False(WallDirtyDiff.SameWall(a, b));
            b = a;
            b.BottomRightPattern = 44;
            Assert.False(WallDirtyDiff.SameWall(a, b));
            b = a;
            b.OccupiedWalls = WallSegments.TopLeft;
            Assert.False(WallDirtyDiff.SameWall(a, b));
        }

        [Fact]
        public void UnchangedWallsProduceNoRegion()
        {
            const int w = 16;
            var before = new WallTile[w * w];
            for (var i = 0; i < before.Length; i++) before[i] = Wall(WallSegments.TopLeft, 1);
            var after = (WallTile[])before.Clone();

            var set = new WallDirtyRegion(1, w, w);
            Assert.Equal(0, WallDirtyDiff.Walls(before, after, 1, w, set));
            Assert.True(set.IsEmpty);
        }

        [Fact]
        public void OneChangedWallDirtiesItsTileAndHalo()
        {
            const int w = 16;
            var before = new WallTile[w * w];
            var after = (WallTile[])before.Clone();
            after[5 * w + 6] = Wall(WallSegments.TopLeft, 1); // row 5, column 6

            var set = new WallDirtyRegion(1, w, w);
            Assert.Equal(1, WallDirtyDiff.Walls(before, after, 1, w, set));
            // columns 5..7, rows 4..6
            Assert.Equal(new WallTileRegion(5, 4, 3, 3), set.Level(1));
        }

        [Fact]
        public void NeighbouringWallsCollapseIntoOneBand()
        {
            const int w = 16;
            var before = new WallTile[w * w];
            var after = (WallTile[])before.Clone();
            for (var x = 4; x < 8; x++) after[9 * w + x] = Wall(WallSegments.TopLeft, 1); // row 9, columns 4..7

            var set = new WallDirtyRegion(1, w, w);
            Assert.Equal(4, WallDirtyDiff.Walls(before, after, 1, w, set));
            // columns 3..8, rows 8..10 - one three-tile-tall band, not four separate boxes
            Assert.Equal(new WallTileRegion(3, 8, 6, 3), set.Level(1));
        }

        [Fact]
        public void DeletedWallIsCaughtToo()
        {
            // Erasing a wall leaves a default (Segments == 0) tile behind, which is a change just
            // as much as a wall appearing.
            const int w = 16;
            var before = new WallTile[w * w];
            before[3 * w + 3] = Wall(WallSegments.TopLeft, 1);
            var after = (WallTile[])before.Clone();
            after[3 * w + 3] = default;

            var set = new WallDirtyRegion(1, w, w);
            Assert.Equal(1, WallDirtyDiff.Walls(before, after, 1, w, set));
            Assert.Equal(new WallTileRegion(2, 2, 3, 3), set.Level(1));
        }

        [Fact]
        public void AResizedLotAsksForAFullRecache()
        {
            var set = new WallDirtyRegion(1, 16, 16);
            Assert.Equal(-1, WallDirtyDiff.Walls(new WallTile[16 * 16], new WallTile[17 * 17], 1, 16, set));
            Assert.Equal(-1, WallDirtyDiff.Walls(null, new WallTile[4], 1, 16, set));
            Assert.Equal(-1, WallDirtyDiff.Walls(new WallTile[4], null, 1, 16, set));
        }

        [Fact]
        public void RoomMapChangeDirtiesTheTilesItMovedTo()
        {
            const int w = 16;
            var before = new uint[w * w];
            var after = (uint[])before.Clone();
            after[2 * w + 3] = 7;
            after[14 * w + 15] = 9;

            var set = new WallDirtyRegion(1, w, w);
            Assert.Equal(2, WallDirtyDiff.RoomMap(before, after, 1, w, set));
            Assert.True(set.Level(1).Contains(3, 2));
            Assert.True(set.Level(1).Contains(15, 14));
            // the corner halo is clipped
            Assert.Equal(16, set.Level(1).Right);
            Assert.Equal(16, set.Level(1).Bottom);
        }

        [Fact]
        public void AltitudeChangeDirtiesTheGroundStoreyAndTheHaloCarriesItUp()
        {
            const int w = 16;
            var before = new short[w * w];
            var after = (short[])before.Clone();
            after[8 * w + 8] = 64;

            var set = new WallDirtyRegion(5, w, w);
            Assert.Equal(1, WallDirtyDiff.Altitude(before, after, w, set));
            Assert.Equal(new WallTileRegion(7, 7, 3, 3), set.Level(1));
        }

        [Fact]
        public void CutawayChangeDirtiesTheGroundStorey()
        {
            const int w = 16;
            var before = new bool[w * w];
            var after = new bool[w * w];
            for (var y = 6; y <= 8; y++) for (var x = 6; x <= 8; x++) after[y * w + x] = true;

            var set = new WallDirtyRegion(5, w, w);
            Assert.Equal(9, WallDirtyDiff.Cutaway(before, after, w, set));
            Assert.Equal(new WallTileRegion(5, 5, 5, 5), set.Level(1));
        }

        [Fact]
        public void ARealisticSingleWallEditDirtiesNineTilesOnOneStoreyOnly()
        {
            // The shape the optimisation exists for: a 77x77 five storey lot, one wall segment
            // added on the ground floor. Three by three tiles on storey one, nothing on the rest.
            const int w = 77;
            const int h = 77;
            var before = new WallTile[w * h];
            var after = (WallTile[])before.Clone();
            after[40 * w + 38] = Wall(WallSegments.TopLeft, 1);

            var set = new WallDirtyRegion(5, w, h);
            var rooms = new uint[w * h];
            Assert.Equal(1, WallDirtyDiff.Walls(before, after, 1, w, set));
            Assert.Equal(0, WallDirtyDiff.RoomMap(rooms, rooms, 1, w, set));
            Assert.Equal(new WallTileRegion(37, 39, 3, 3), set.Level(1));

            Assert.False(set.ResolveFullRecache());
            Assert.False(set.Full);
            for (var level = 2; level <= 5; level++) Assert.True(set.Level(level).IsEmpty);
            Assert.Equal(9, set.DilatedArea());
        }

        [Fact]
        public void ARoomChangeOnOneStoreyDoesNotDragTheOthersIn()
        {
            // Re-seating a room rewrites the room map for the storey it is on. Only that storey's
            // walls change colour, so only that storey is rebuilt.
            const int w = 32;
            var before = new uint[w * w];
            var after = (uint[])before.Clone();
            for (var i = 0; i < 4; i++) after[i] = 3;

            var set = new WallDirtyRegion(5, w, w);
            Assert.Equal(4, WallDirtyDiff.RoomMap(before, after, 2, w, set));
            // row 0, columns 0..3 -> columns 0..4, rows 0..1
            Assert.Equal(new WallTileRegion(0, 0, 5, 2), set.Level(2));
            Assert.True(set.Level(1).IsEmpty);
        }

        [Fact]
        public void SpreadingManySmallChangesPromotesToAFullRecache()
        {
            // Twenty scattered single-tile edits on a 100x100 lot: the dilated bounding box covers
            // the lot, and the caller must notice rather than rebuild 10000 tiles one at a time.
            const int w = 100;
            var before = new WallTile[w * w];
            var after = (WallTile[])before.Clone();
            for (var i = 0; i < 20; i++) after[(i * 1009) % (w * w)] = Wall(WallSegments.TopLeft, 1);

            var set = new WallDirtyRegion(1, w, w);
            WallDirtyDiff.Walls(before, after, 1, w, set);
            Assert.True(set.ResolveFullRecache());
        }

        [Fact]
        public void DilateAndClampOverAPartCollectionMatchesTheIncrementalResult()
        {
            var parts = new List<WallTileRegion>
            {
                WallTileRegion.Single(3, 3),
                WallTileRegion.Single(9, 3),
                WallTileRegion.Single(6, 8)
            };
            var batched = WallDirtyRegionMath.DilateAndClamp(parts, 32, 32, WallDirtyRegionMath.Halo);

            var incremental = WallTileRegion.Empty;
            foreach (var part in parts)
            {
                incremental = WallDirtyRegionMath.Union(incremental,
                    WallDirtyRegionMath.DilateAndClamp(part, 32, 32, WallDirtyRegionMath.Halo));
            }
            Assert.Equal(incremental, batched);
            Assert.True(WallDirtyRegionMath.DilateAndClamp(null, 32, 32, 1).IsEmpty);
        }
    }
}
