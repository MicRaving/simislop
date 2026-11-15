/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Content.Model;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// §6 B4's transit lot with a <c>VM</c> behind it, tested on the halves that have no
    /// <c>VM</c> in them: which generated map tiles the transit lot's blueprint covers, what street
    /// it is built out of, where it stands and at what altitude, whether the world may take the
    /// avatar at all, and what happens to the world when the walk ends.
    ///
    /// <para>
    /// <b>Nothing here builds a lot, and that is the design, not a compromise.</b> The bridge half
    /// of this feature - a <c>SubWorldComponent</c>, a <c>VM</c>, an avatar rehydrated from a
    /// marshal, a graphics device - cannot be exercised without a TS1 install and a device, and a
    /// test that needed one would not be a test. So the arithmetic that decides <em>how big and
    /// where and at what height</em> is split out of it as four pure values, and those are what is
    /// asserted: a bug that would put the transit lot on the wrong plot, off the ground, or two tiles
    /// too small to hold the walk is a failure here rather than a screenshot.
    /// </para>
    ///
    /// <para>
    /// <b>The fixture is hand-written, for the same reason
    /// <see cref="NeighbourhoodTransitLotTests"/> writes its own.</b> The thing under test is
    /// "given a walk I can draw on paper, what rectangle of the map does the lot covering it need,
    /// and does the blueprint contain the whole walk". A generated neighbourhood answers with a
    /// route and a shape nobody can check by eye. So the map is the same 24x24 block the pure
    /// transit-lot tests use - a road column down <c>x = 12</c> crossed by a road row across
    /// <c>y = 12</c>, six lots on the arms - and the walk is a named straight run down that column.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodTransitLotVmTests
    {
        // ---- the fixture, matching NeighbourhoodTransitLotTests ---------------------------------

        private const int BlockSize = 24;
        private const int BlockColumn = 12;
        private const int BlockRow = 12;

        private const int NorthWestLot = 11;   // (8,4), door east at (12,6)
        private const int SouthWestLot = 12;   // (8,16), door east at (12,18)
        private const int NorthEastLot = 13;   // (13,1), door west at (12,3)
        private const int SouthEastLot = 14;   // (13,18), door west at (12,19)
        private const int WestRowLot = 15;     // (4,13), door north at (5,12)
        private const int Plot = 16;           // (1,1), bulldozed, no door

        private static bool InBounds(int x, int y, int size)
        {
            return x >= 0 && y >= 0 && x < size && y < size;
        }

        /// <summary>
        /// The road mask writer, copied in shape from <see cref="NeighbourhoodTransitLotTests"/>: a
        /// tile's four bits are its road neighbours' occupancy, and a turn or a junction also gets
        /// the same mask in the high nibble. A fixture whose masks disagree with its own shape
        /// would be testing a different map than the one it claims to be.
        /// </summary>
        private static NeighbourhoodMap RoadMap(int size, Func<int, int, bool> isRoad)
        {
            var map = NeighbourhoodMap.Create(size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (!isRoad(x, y)) continue;
                    byte mask = RoadMask.None;
                    if (isRoad(x + 1, y)) mask |= RoadMask.East;
                    if (isRoad(x, y + 1)) mask |= RoadMask.South;
                    if (isRoad(x - 1, y)) mask |= RoadMask.West;
                    if (isRoad(x, y - 1)) mask |= RoadMask.North;
                    map.RoadData[map.Index(x, y)] = RoadMask.IsTurnOrJunction(mask)
                        ? (byte)(mask | (mask << 4))
                        : mask;
                }
            }
            return map;
        }

        private static NeighbourhoodMap BlockMap()
        {
            return RoadMap(BlockSize, (x, y) => InBounds(x, y, BlockSize)
                && (x == BlockColumn || y == BlockRow));
        }

        private static NeighbourhoodLot Lot(short number, int x, int y,
            NeighbourhoodLotSide doorSide, bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Size = 4,
                WorldX = x,
                WorldY = y,
                DoorSide = doorSide,
                HasHouse = hasHouse,
                TerrainType = TerrainType.GRASS,
                Zone = LotZoning.ZoneResidential,
                MapX = number,
                MapY = number,
            };
        }

        private static NeighbourhoodLayout BlockLayout()
        {
            return new NeighbourhoodLayout(0, 4242, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                BlockSize, new[]
                {
                    Lot(NorthWestLot, 8, 4, NeighbourhoodLotSide.East),
                    Lot(SouthWestLot, 8, 16, NeighbourhoodLotSide.East),
                    Lot(NorthEastLot, 13, 1, NeighbourhoodLotSide.West),
                    Lot(SouthEastLot, 13, 18, NeighbourhoodLotSide.West),
                    Lot(WestRowLot, 4, 13, NeighbourhoodLotSide.North),
                    Lot(Plot, 1, 1, NeighbourhoodLotSide.East, false),
                });
        }

        private static NeighbourhoodQuery BlockQuery()
        {
            var entrances = new List<NeighbourhoodEntrance>
            {
                Door(NorthWestLot, BlockColumn, 6, NeighbourhoodDoorSide.East),
                Door(SouthWestLot, BlockColumn, 18, NeighbourhoodDoorSide.East),
                Door(NorthEastLot, BlockColumn, 3, NeighbourhoodDoorSide.West),
                Door(SouthEastLot, BlockColumn, 19, NeighbourhoodDoorSide.West),
                Door(WestRowLot, 5, BlockRow, NeighbourhoodDoorSide.North),
            };
            return new NeighbourhoodQuery(BlockLayout(), BlockMap(), entrances);
        }

        private static NeighbourhoodEntrance Door(short lot, int x, int y, byte side)
        {
            return new NeighbourhoodEntrance(lot, x, y,
                NeighbourhoodDoorSide.DeltaX(side), NeighbourhoodDoorSide.DeltaY(side), side);
        }

        private static TransitPath StraightWalk()
        {
            //The twelve-tile run down the road column from lot 11's door to lot 12's, which is the
            //commonest walk there is and the one a player sees most often.
            var lot = new NeighbourhoodTransitLot(BlockQuery(),
                ActiveLotKind.Active, NorthWestLot);
            TransitPath path;
            string reason;
            Assert.True(lot.TryPlanPath(NorthWestLot, SouthWestLot, out path, out reason), reason);
            return path;
        }

        // ---- the fixture is what it claims to be ---------------------------------------------

        [Fact]
        public void TheFixtureIsAStraightTwelveTileRunDownTheRoadColumn()
        {
            //If this fails, every rectangle assertion below is testing a different walk than the one
            //the comments describe, so it is worth pinning on its own.
            var path = StraightWalk();

            Assert.True(path.IsValid);
            Assert.Equal(NorthWestLot, path.OriginLot);
            Assert.Equal(SouthWestLot, path.DestinationLot);
            Assert.Equal(12, path.Length);
            Assert.Equal(13, path.Waypoints.Count);
            Assert.Equal(BlockColumn, path.Start.X);
            Assert.Equal(6, path.Start.Y);
            Assert.Equal(BlockColumn, path.End.X);
            Assert.Equal(18, path.End.Y);
            Assert.Equal(NorthWestLot, path.Start.LotNumber);
            Assert.Equal(SouthWestLot, path.End.LotNumber);
        }

        // ---- TransitStreetRegion: which tiles the blueprint has to cover -----------------------

        [Fact]
        public void TheRegionIsTheWalksBoundingBoxPlusTheMargin()
        {
            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(StraightWalk(), BlockSize, BlockSize,
                TransitStreetRegion.DefaultMarginTiles, out region, out reason), reason);

            //The run is one column wide at x = 12 from y = 6 to y = 18. Eight tiles of margin on
            //every side: x 4..20, y 0..26 clipped to the map's 24 rows, so y 0..23.
            Assert.Equal(4, region.MinX);
            Assert.Equal(0, region.MinZ);
            Assert.Equal(21, region.MaxX);
            Assert.Equal(24, region.MaxZ);
            Assert.Equal(17, region.Width);
            Assert.Equal(24, region.Depth);
            Assert.True(region.IsValid);
        }

        [Fact]
        public void ARegionIsSquaredUpSoTheBlueprintContainsTheWholeWalk()
        {
            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(StraightWalk(), BlockSize, BlockSize, 1,
                out region, out reason), reason);

            //One tile of margin: x 11..13 (3 wide), y 5..19 (15 deep). The blueprint is built at the
            //larger dimension and the region sits in its top-left corner, so the side is 15 - or 16,
            //because the helpers need sixteen - rather than 3, which is what stops a tile of the
            //walk falling off the side of the lot.
            Assert.Equal(3, region.Width);
            Assert.Equal(15, region.Depth);
            Assert.Equal(16, region.Side);
        }

        [Fact]
        public void ARegionNeverNarrowerThanTheHelpersCanBuildOn()
        {
            //The TSO terrain helpers need at least a sixteen-tile blueprint, so Side floors there
            //rather than letting a two-tile pavement be a two-tile lot. See
            //SimitoneSurroundingsPlan.MinSubWorldSize for the same floor and the same reason.
            var path = new TransitPath(NorthWestLot, SouthWestLot,
                new[] { new TransitWaypoint(12, 6, NorthWestLot) });

            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(path, BlockSize, BlockSize, 0,
                out region, out reason), reason);

            Assert.Equal(1, region.Width);
            Assert.Equal(1, region.Depth);
            Assert.Equal(TransitStreetRegion.MinSideTiles, region.Side);
            Assert.Equal(SimitoneSurroundingsPlan.MinSubWorldSize, region.Side);
        }

        [Fact]
        public void LocalTilesAreTheRegionsOwnSubtraction()
        {
            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(StraightWalk(), BlockSize, BlockSize,
                TransitStreetRegion.DefaultMarginTiles, out region, out reason), reason);

            //(12,6) is the origin lot's door, eight tiles east and six tiles south of the region's
            //north-west corner - and inside the 24x24 blueprint the region is squared up to.
            Assert.Equal(8, region.LocalX(12));
            Assert.Equal(6, region.LocalZ(6));
            Assert.True(region.Contains(12, 6));
            Assert.True(region.Contains(4, 0));
            Assert.False(region.Contains(21, 0));
            Assert.False(region.Contains(4, 24));
        }

        [Fact]
        public void ARegionRefusesAPathThatIsNotThere()
        {
            TransitStreetRegion region;
            string reason;
            Assert.False(TransitStreetRegion.TryFor(null, BlockSize, BlockSize, 8,
                out region, out reason));
            Assert.NotNull(reason);
            Assert.False(region.IsValid);

            Assert.False(TransitStreetRegion.TryFor(TransitPath.None, BlockSize, BlockSize, 8,
                out region, out reason));
            Assert.NotNull(reason);
        }

        [Fact]
        public void ARegionRefusesAMapWithNoTiles()
        {
            TransitStreetRegion region;
            string reason;
            Assert.False(TransitStreetRegion.TryFor(StraightWalk(), 0, 0, 8,
                out region, out reason));
            Assert.Contains("0x0", reason, StringComparison.Ordinal);
        }

        [Fact]
        public void ARegionRefusesAWaypointOffTheMap()
        {
            //A refusal rather than a clamp: a clamped walk is a walk whose blueprint does not
            //contain the walk, which is the one outcome this type exists to prevent. The planner
            //cannot produce this - it is the corrupt-sidecar case - so it is built by hand, and the
            //path is still a legal four-connected chain so the only thing wrong with it is the tile.
            var path = new TransitPath(NorthWestLot, SouthWestLot,
                new[]
                {
                    new TransitWaypoint(0, 0, NorthWestLot),
                    new TransitWaypoint(0, -1, TransitPath.NoLot),
                });

            TransitStreetRegion region;
            string reason;
            Assert.False(TransitStreetRegion.TryFor(path, BlockSize, BlockSize, 8,
                out region, out reason));
            Assert.Contains("outside", reason, StringComparison.Ordinal);
            Assert.Contains("(0,-1)", reason, StringComparison.Ordinal);
        }

        // ---- TransitStreetTerrain: what the street is built out of -----------------------------

        [Fact]
        public void AStreetRegionOfRoadHasRoadOnEveryEdgeItRunsAlong()
        {
            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(StraightWalk(), BlockSize, BlockSize, 1,
                out region, out reason), reason);

            TransitStreetTerrain terrain;
            Assert.True(TransitStreetTerrain.TryBuild(BlockMap(), region, out terrain, out reason),
                reason);

            Assert.True(terrain.IsValid);
            //The walk runs down the road column at x = 12 and the region is x 11..13 by y 5..19, so
            //the column is one tile in from the west and east edges and crosses the north and south
            //edges outright. All four edges have a street on them, which is the truth about a region
            //built around a road that runs all the way through it - not a guess, and not a probe
            //looking outside the region the way a lot's does.
            Assert.Equal(SimitoneNeighbourTerrain.RoadNorth | SimitoneNeighbourTerrain.RoadSouth
                | SimitoneNeighbourTerrain.RoadWest | SimitoneNeighbourTerrain.RoadEast,
                terrain.Roads & 0x0F);
            Assert.Equal(SimitoneNeighbourTerrain.AdjFlagMyEdge, terrain.Blend.AdjFlags);
        }

        [Fact]
        public void AStreetRegionWithNoRoadInItHasNoRoadByte()
        {
            //The negative case, which is the only way "the probe reads road and not just ground"
            //gets tested as a fact rather than as an absence: the same region, on a map with the
            //streets bulldozed.
            var map = RoadMap(BlockSize, (x, y) => false);

            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(StraightWalk(), BlockSize, BlockSize, 1,
                out region, out reason), reason);

            TransitStreetTerrain terrain;
            Assert.True(TransitStreetTerrain.TryBuild(map, region, out terrain, out reason), reason);

            Assert.Equal(0, terrain.Roads);
            Assert.Equal((byte)0, terrain.Blend.AdjFlags);
        }

        [Fact]
        public void AStreetCornerIsWhereTwoOfItsEdgesBothHaveARoad()
        {
            //The full-margin region is x 4..20 by y 0..23, and the road column crosses the road row
            //inside it at (12,12). The region reaches the map's top and bottom rows, so both of its
            //y edges cross the column and both of its x edges cross the row: a region shaped like
            //this genuinely is a crossroads, and all four corners are real turns.
            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(StraightWalk(), BlockSize, BlockSize,
                TransitStreetRegion.DefaultMarginTiles, out region, out reason), reason);

            TransitStreetTerrain terrain;
            Assert.True(TransitStreetTerrain.TryBuild(BlockMap(), region, out terrain, out reason),
                reason);

            Assert.Equal(SimitoneNeighbourTerrain.RoadNorth | SimitoneNeighbourTerrain.RoadSouth
                | SimitoneNeighbourTerrain.RoadWest | SimitoneNeighbourTerrain.RoadEast,
                terrain.Roads & 0x0F);
            //Two edges meeting is a corner the road turns at, which is the same rule the lot's road
            //byte uses - asserted through the bit, because the corner tilemaps are what read it.
            Assert.NotEqual(0, terrain.Roads & SimitoneNeighbourTerrain.RoadCornerTopLeft);
            Assert.NotEqual(0, terrain.Roads & SimitoneNeighbourTerrain.RoadCornerBottomRight);
        }

        [Fact]
        public void AStreetRegionsBaseLevelIsTheMeanOfItsCentreFourSamples()
        {
            //The whole of the elevation contract in one assertion: a flat map is base level zero,
            //and the arithmetic is SimitoneNeighbourTerrain.BaseLevel's, mean of the centre four
            //times a hundred. A difference of a truncated hundredth is a hundredth of a tile of
            //visible step, so the two types must not each have their own expression.
            var map = BlockMap();
            var elevation = (byte)200;
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = elevation;

            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(StraightWalk(), BlockSize, BlockSize,
                TransitStreetRegion.DefaultMarginTiles, out region, out reason), reason);

            TransitStreetTerrain terrain;
            Assert.True(TransitStreetTerrain.TryBuild(map, region, out terrain, out reason), reason);

            //The byte conversion is the shared one, so the expected height is stated through it
            //rather than as a second hard-coded number that could drift from it.
            var expected = TransitStreetTerrain.HeightByteFor(elevation);
            Assert.Equal(expected, terrain.HeightAt(0, 0));
            Assert.Equal(expected, terrain.HeightAt(3, 3));
            Assert.Equal(expected * 100, terrain.BaseLevel);
        }

        [Fact]
        public void AHeightByteIsAWholeTileOfHeightInTwoAndAQuarterBytes()
        {
            //The vertical contract, stated once: a tile of the generated map's height is twelve
            // elevation units, a height-grid byte is a hundredth of an ARRY(0) unit and an ARRY(0)
            // unit is 5.333 of those, so a byte is 0.0444 of an elevation byte and the byte that
            //holds a whole tile of height is the top of the range. This is the quantisation
            //SimitoneNeighbourTerrain's remarks are about, asserted rather than described.
            Assert.Equal(4, TransitStreetTerrain.HeightByteFor(100));
            Assert.Equal(0, TransitStreetTerrain.HeightByteFor(0));
            //The boundary: 11.25 elevation bytes is half a height byte, so 11 rounds down and 12
            //rounds up, away from zero. The quantisation is the point - a lot's ground is a hundred
            //thousandth of a tile per byte, and a walk that crosses a boundary is a walk that steps.
            Assert.Equal(0, TransitStreetTerrain.HeightByteFor(11));
            Assert.Equal(1, TransitStreetTerrain.HeightByteFor(12));
        }

        [Fact]
        public void AStreetRegionsGridSamplesItsOwnFirstAndLastTile()
        {
            //Integer division, as the lot sampler does, so the outer samples land on the region's
            //own tiles rather than one past them. A region whose far edge were the pavement beside
            //it would tilt the whole lot towards its neighbour.
            var map = BlockMap();
            for (int y = 0; y < BlockSize; y++)
            {
                for (int x = 0; x < BlockSize; x++)
                {
                    map.ElevationData[map.Index(x, y)] = (byte)(x == 0 ? 200 : 0);
                }
            }

            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(StraightWalk(), BlockSize, BlockSize, 1,
                out region, out reason), reason);
            Assert.Equal(11, region.MinX);

            TransitStreetTerrain terrain;
            Assert.True(TransitStreetTerrain.TryBuild(map, region, out terrain, out reason), reason);

            //Grid x 0 is the region's first column (x = 11), which is off the high column entirely,
            //so every sample on it is 0 - and the region does not reach x = 0, which is where the
            //high ground is. The point of the assertion is that the whole grid is inside the region.
            for (int y = 0; y < TransitStreetTerrain.GridSize; y++)
            {
                for (int x = 0; x < TransitStreetTerrain.GridSize; x++)
                {
                    Assert.Equal(0, terrain.HeightAt(x, y));
                }
            }
        }

        [Fact]
        public void AStreetRegionsGroundIsTheMostCommonTerrainInIt()
        {
            var map = BlockMap();
            //Two thirds sand, one third rock, and the modal answer must be sand however the loop
            //walks the region.
            for (int y = 0; y < BlockSize; y++)
            {
                for (int x = 0; x < BlockSize; x++)
                {
                    map.TerrainType[map.Index(x, y)] =
                        (byte)(x % 3 == 0 ? NeighbourhoodTerrain.Rock : NeighbourhoodTerrain.Sand);
                }
            }

            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(StraightWalk(), BlockSize, BlockSize,
                TransitStreetRegion.DefaultMarginTiles, out region, out reason), reason);

            TransitStreetTerrain terrain;
            Assert.True(TransitStreetTerrain.TryBuild(map, region, out terrain, out reason), reason);

            Assert.Equal((int)TerrainType.SAND, (int)terrain.Blend.Base);
            Assert.Equal((int)TerrainType.SAND, (int)terrain.Blend.Blend);
        }

        [Fact]
        public void AStreetRefusesAMapOrAWalkItCannotAnswerFor()
        {
            TransitStreetTerrain terrain;
            string reason;
            Assert.False(TransitStreetTerrain.TryBuild(null, default(TransitStreetRegion),
                out terrain, out reason));
            Assert.NotNull(reason);
            Assert.False(terrain.IsValid);

            Assert.False(TransitStreetTerrain.TryBuild(BlockMap(), default(TransitStreetRegion),
                out terrain, out reason));
            Assert.NotNull(reason);

            //And a region that is not there is a zero road byte rather than a throw, so a caller
            //with a corrupt sidecar gets a flat lot instead of a crash.
            Assert.Equal(0, TransitStreetTerrain.RoadByteFor(BlockMap(),
                default(TransitStreetRegion)));
            Assert.Equal(0, TransitStreetTerrain.RoadByteFor(null, default(TransitStreetRegion)));
        }

        // ---- TransitBlueprintPlan: the whole decision ------------------------------------------

        [Fact]
        public void APlanBuildsASquareLotThatContainsTheWholeWalk()
        {
            var path = StraightWalk();
            TransitBlueprintPlan plan;
            string reason;
            Assert.True(TransitBlueprintPlan.TryCreate(BlockMap(),
                BlockLayout().FindLot(NorthWestLot), path,
                NeighbourhoodTransitLotVm.DefaultMarginTiles, out plan, out reason), reason);

            Assert.True(plan.IsValid);
            Assert.Equal(NorthWestLot, plan.OriginLotNumber);
            Assert.Equal(SouthWestLot, plan.DestinationLot);
            Assert.Equal(17, plan.Region.Width);
            Assert.Equal(24, plan.Region.Depth);
            //Squared up to the larger dimension, so the 17-wide region sits in a 24x24 blueprint
            //rather than a lot three tiles wide with a fifteen-tile walk running down it.
            Assert.Equal(Math.Max(plan.Region.Width, plan.Region.Depth), plan.Side);
            Assert.Equal(24, plan.Side);
            Assert.True(plan.Side >= TransitStreetRegion.MinSideTiles);

            //The claim the margin exists to make, asserted rather than believed.
            Assert.True(plan.ContainsWholeWalk(path));
        }

        [Fact]
        public void APlanStandsWhereTheRegionIsRelativeToTheOriginLot()
        {
            //GlobalPosition is the tile delta from the origin lot's minimum corner to the region's,
            //unsigned - the same rule and the same sign as every surround neighbour, with the street
            //region in place of the neighbour lot. See SubWorldComponent's three engine reads.
            TransitBlueprintPlan plan;
            string reason;
            Assert.True(TransitBlueprintPlan.TryCreate(BlockMap(),
                BlockLayout().FindLot(NorthWestLot), StraightWalk(),
                NeighbourhoodTransitLotVm.DefaultMarginTiles, out plan, out reason), reason);

            //Lot 11 is at (8,4) and the region starts at (4,0), so the transit lot reaches four
            //tiles west and four tiles north of the lot the player left.
            Assert.Equal(8, plan.OriginLot.WorldX);
            Assert.Equal(4, plan.OriginLot.WorldY);
            Assert.Equal(-4, (int)plan.SubWorldOrigin.X);
            Assert.Equal(-4, (int)plan.SubWorldOrigin.Y);
        }

        [Fact]
        public void APlanMapsAWaypointOntoItsOwnLotTile()
        {
            TransitBlueprintPlan plan;
            string reason;
            Assert.True(TransitBlueprintPlan.TryCreate(BlockMap(),
                BlockLayout().FindLot(NorthWestLot), StraightWalk(),
                NeighbourhoodTransitLotVm.DefaultMarginTiles, out plan, out reason), reason);

            //The origin door at map tile (12,6) is at blueprint tile (8,6) - the region's own
            //subtraction, and inside the square the region is built in.
            var start = plan.LocalTileFor(plan.Region.MinX + 8 >= 0
                ? new TransitWaypoint(12, 6, NorthWestLot)
                : new TransitWaypoint(12, 6, NorthWestLot));
            Assert.Equal(8, start.TileX);
            Assert.Equal(6, start.TileY);
            Assert.Equal(SimitoneSurroundingsRestore.SubWorldLevel, start.Level);
            //LotTilePos is in sixteenths of a tile and a sim stands in the middle of its tile: a
            //raw tile count here put every walker one sixteenth of the way along the street.
            Assert.Equal((short)((8 << 4) + 8), start.x);
            Assert.Equal((short)((6 << 4) + 8), start.y);

            //And the destination door, twelve tiles down the run, is twelve tiles down the lot.
            var end = plan.LocalTileFor(new TransitWaypoint(12, 18, SouthWestLot));
            Assert.Equal(8, end.TileX);
            Assert.Equal(18, end.TileY);
        }

        [Fact]
        public void APlanClampsATileOutsideTheRegionIntoTheLot()
        {
            //LocalTileAt is what a follower calls with a position, and a position just off the end
            //of a region is arithmetic away from being out of the blueprint. Clamping rather than
            //throwing: the alternative is a sim written to a negative tile, which the lot view
            //renders as somewhere unpredictable.
            TransitBlueprintPlan plan;
            string reason;
            Assert.True(TransitBlueprintPlan.TryCreate(BlockMap(),
                BlockLayout().FindLot(NorthWestLot), StraightWalk(),
                NeighbourhoodTransitLotVm.DefaultMarginTiles, out plan, out reason), reason);

            var before = plan.LocalTileAt(-5, -5);
            Assert.Equal(0, before.TileX);
            Assert.Equal(0, before.TileY);

            var after = plan.LocalTileAt(500, 500);
            Assert.Equal(plan.Side - 1, after.TileX);
            Assert.Equal(plan.Side - 1, after.TileY);
        }

        [Fact]
        public void APlanFacesTheWayTheLotThePlayerLeftFaces()
        {
            //The landmark row PositionLandmarkObjects lays is scenery, and it lands on the pavement
            //the walk is on only if the street lot faces the way the origin lot does. Lot 11's door
            //is on its east edge, and east is lot direction 3.
            TransitBlueprintPlan plan;
            string reason;
            Assert.True(TransitBlueprintPlan.TryCreate(BlockMap(),
                BlockLayout().FindLot(NorthWestLot), StraightWalk(),
                NeighbourhoodTransitLotVm.DefaultMarginTiles, out plan, out reason), reason);

            Assert.Equal((ushort)SimitoneSurroundingsPlan.LotDirectionFor(
                NeighbourhoodLotSide.East), plan.LotDirection);
            Assert.Equal(3, plan.LotDirection);
        }

        [Fact]
        public void AFlatStreetAndItsFlatParentAreAtTheSameAltitude()
        {
            //The whole of the altitude contract on a flat map: both sides are read out of the same
            //map by the same sampler, so they cancel and the transit lot stands level with the lot
            //the player left. This is the common case and the one the packer's own layouts produce.
            TransitBlueprintPlan plan;
            string reason;
            Assert.True(TransitBlueprintPlan.TryCreate(BlockMap(),
                BlockLayout().FindLot(NorthWestLot), StraightWalk(),
                NeighbourhoodTransitLotVm.DefaultMarginTiles, out plan, out reason), reason);

            Assert.Equal(0, plan.Terrain.BaseLevel);
            Assert.Equal(0, plan.BaseAltitude);
            Assert.Equal(
                SimitoneSurroundingsRestore.BaseAltitudeFor(0, 0), plan.BaseAltitude);
        }

        [Fact]
        public void AStreetAboveItsParentLotHangsByTheDifference()
        {
            //The other direction, and the reason the arithmetic is a subtraction of two samples of
            //the same map rather than a cast: a street one height byte above the origin lot hangs
            //that many hundredths lower, which is what makes the two grounds meet at the kerb.
            var map = BlockMap();
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 40;

            //Lot 11's own rectangle (8,4)-(11,7) stays at 40; everything the street region reads is
            //raised by a byte's worth of height, which is 22.5 elevation bytes.
            for (int y = 0; y < BlockSize; y++)
            {
                for (int x = 0; x < BlockSize; x++)
                {
                    var inLot = x >= 8 && x < 12 && y >= 4 && y < 8;
                    if (!inLot) map.ElevationData[map.Index(x, y)] = (byte)(40 + 23);
                }
            }

            TransitBlueprintPlan plan;
            string reason;
            Assert.True(TransitBlueprintPlan.TryCreate(map,
                BlockLayout().FindLot(NorthWestLot), StraightWalk(),
                NeighbourhoodTransitLotVm.DefaultMarginTiles, out plan, out reason), reason);

            //The region includes tiles of the lot's own rectangle, so the two base levels are close;
            //what matters is that the answer is a real difference and that it is the lot's minus the
            //street's, not the other way round.
            //The region overlaps the origin lot's own footprint, so the two base levels are close
            //rather than far apart - but they are not equal, and the answer is the lot's less the
            //street's rather than the other way round, which is the sign that keeps the transit
            //lot's kerb level with the lot the player left.
            Assert.NotEqual(0, plan.Terrain.BaseLevel);
            Assert.True(plan.BaseAltitude < 0,
                "a street standing above its parent lot hangs below it, not above it");

            SimitoneNeighbourTerrain parentTerrain;
            Assert.True(SimitoneNeighbourTerrain.TryBuild(map,
                BlockLayout().FindLot(NorthWestLot), out parentTerrain, out reason), reason);
            Assert.Equal(SimitoneSurroundingsRestore.BaseAltitudeFor(
                parentTerrain.BaseLevel, plan.Terrain.BaseLevel), plan.BaseAltitude);
        }

        [Fact]
        public void APlanRefusesAWalkOrAParentOrAMapItCannotBuildFrom()
        {
            var layout = BlockLayout();
            TransitBlueprintPlan plan;
            string reason;

            Assert.False(TransitBlueprintPlan.TryCreate(BlockMap(), layout.FindLot(NorthWestLot),
                null, 8, out plan, out reason));
            Assert.NotNull(reason);

            Assert.False(TransitBlueprintPlan.TryCreate(BlockMap(), layout.FindLot(NorthWestLot),
                TransitPath.None, 8, out plan, out reason));
            Assert.NotNull(reason);

            Assert.False(TransitBlueprintPlan.TryCreate(BlockMap(), null, StraightWalk(),
                8, out plan, out reason));
            Assert.Contains("parent lot", reason, StringComparison.Ordinal);

            Assert.False(TransitBlueprintPlan.TryCreate(null, layout.FindLot(NorthWestLot),
                StraightWalk(), 8, out plan, out reason));
            Assert.Contains("no generated map", reason, StringComparison.Ordinal);

            Assert.Null(plan);
        }

        [Fact]
        public void APlanRefusesAWalkTooLongToBeALot()
        {
            //The hard cap. A region wider than MaxSideTiles would need a blueprint larger than any
            //lot view can usefully hold, so it is refused by name and the caller falls back to a
            //switch - which is B3's behaviour and the one the capability promises.
            //The size that matters is the walk's own span, not the map's, so the walk is what grows:
            //a road column longer than the cap with a door at each end. The region is then the
            //column plus twice the margin, which is over MaxSideTiles, and the refusal is the answer.
            //The margin is left at the default because it is the caller's parameter - a caller that
            //asks for a wider margin is asking for a bigger lot, and saying no is the honest answer.
            //The road column is the full height of a map just wider than the cap, so the walk's own
            //vertical span - the column - is over MaxSideTiles and so is the region it produces. A
            //longer map than the walk needs is not the point; the point is that the span of the
            //walk, not the size of the world, is what decides the lot's size.
            var size = TransitStreetRegion.MaxSideTiles + TransitStreetRegion.DefaultMarginTiles * 2
                + 2;
            var map = NeighbourhoodMap.Create(size);
            var column = size / 2;
            for (int y = 0; y < size; y++)
            {
                map.RoadData[map.Index(column, y)] = RoadMask.North | RoadMask.South;
            }
            var layout = new NeighbourhoodLayout(0, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                size, new List<NeighbourhoodLot>
                {
                    Lot(NorthWestLot, column - 8, 0, NeighbourhoodLotSide.East),
                    Lot(SouthWestLot, column - 8, size - 5, NeighbourhoodLotSide.East),
                });
            var waypoints = new List<TransitWaypoint>
            {
                new TransitWaypoint(column, 2, NorthWestLot),
            };
            for (int y = 3; y < size - 4; y++)
            {
                waypoints.Add(new TransitWaypoint(column, y, TransitPath.NoLot));
            }
            waypoints.Add(new TransitWaypoint(column, size - 4, SouthWestLot));
            var path = new TransitPath(NorthWestLot, SouthWestLot, waypoints);

            TransitStreetRegion region;
            string reason;
            Assert.True(TransitStreetRegion.TryFor(path, size, size,
                TransitStreetRegion.DefaultMarginTiles, out region, out reason), reason);
            Assert.True(region.Side > TransitStreetRegion.MaxSideTiles,
                "the walk is " + path.Length + " tiles, so its region should be over the cap");

            TransitBlueprintPlan plan;
            Assert.False(TransitBlueprintPlan.TryCreate(map, layout.FindLot(NorthWestLot), path,
                TransitStreetRegion.DefaultMarginTiles, out plan, out reason));
            Assert.Contains("more than the", reason, StringComparison.Ordinal);
            Assert.Contains(TransitStreetRegion.MaxSideTiles.ToString(
                System.Globalization.CultureInfo.InvariantCulture), reason,
                StringComparison.Ordinal);
            Assert.Null(plan);
        }

        // ---- CanTakeOwnership: the invariant, as a question with no VM in it --------------------

        [Fact]
        public void ATransitLotMayOnlyTakeAWalkThatHasAlreadyBegun()
        {
            //B4 task 5's guard. Before BeginTransit the origin lot still owns the avatar, and
            //building a second VM now would give the sim two homes - which is the one failure the
            //whole design exists to prevent, so the refusal names it.
            var query = BlockQuery();
            var lot = new NeighbourhoodTransitLot(query, ActiveLotKind.Active, NorthWestLot);
            var path = StraightWalk();

            string reason;
            Assert.False(NeighbourhoodTransitLotVm.CanTakeOwnership(lot, out reason));
            Assert.Contains("still owns the avatar", reason, StringComparison.Ordinal);
            Assert.Equal(NorthWestLot, lot.ActiveLotNumber);

            Assert.True(lot.BeginTransit(path, out reason), reason);
            Assert.True(NeighbourhoodTransitLotVm.CanTakeOwnership(lot, out reason), reason);
            Assert.Equal(ActiveLotKind.Transit, lot.Kind);
            Assert.Equal(TransitPath.NoLot, lot.ActiveLotNumber);
            Assert.True(NeighbourhoodTransitLot.IsExactlyOneActiveLot(
                lot.Kind, lot.ActiveLotNumber, out reason), reason);
        }

        [Fact]
        public void ATransitLotRefusesNothingAtAll()
        {
            string reason;
            Assert.False(NeighbourhoodTransitLotVm.CanTakeOwnership(null, out reason));
            Assert.NotNull(reason);
        }

        [Fact]
        public void ATransitLotRefusesAFinishedWalk()
        {
            //Arrival is sticky and CompleteTransit has run, so the world already names the
            //destination and there is no walk in transit. A transit lot built now would be a second
            //owner of a sim that is not there - and the refusal it gets is the first of the ladder,
            //because after a completion the world is back to a lot owning the avatar.
            var lot = new NeighbourhoodTransitLot(BlockQuery(),
                ActiveLotKind.Active, NorthWestLot);
            var path = StraightWalk();
            string reason;
            Assert.True(lot.BeginTransit(path, out reason), reason);
            Assert.True(lot.Advance(path.Length), reason);
            Assert.True(lot.CompleteTransit(out reason), reason);

            Assert.False(NeighbourhoodTransitLotVm.CanTakeOwnership(lot, out reason));
            Assert.Contains("still owns the avatar", reason, StringComparison.Ordinal);
            Assert.Equal(ActiveLotKind.Active, lot.Kind);
            Assert.Equal(SouthWestLot, lot.ActiveLotNumber);
            Assert.True(lot.HasOwner);
        }

        [Fact]
        public void ATransitLotRefusesACancelledWalkBackAtItsOrigin()
        {
            //Cancelled puts the world's bookkeeping back where it was, so the origin lot owns the
            //avatar again and the transit lot is once again not allowed to.
            var lot = new NeighbourhoodTransitLot(BlockQuery(),
                ActiveLotKind.Active, NorthWestLot);
            var path = StraightWalk();
            string reason;
            Assert.True(lot.BeginTransit(path, out reason), reason);
            Assert.True(lot.CancelTransit(out reason), reason);

            Assert.False(NeighbourhoodTransitLotVm.CanTakeOwnership(lot, out reason));
            Assert.Equal(ActiveLotKind.Active, lot.Kind);
            Assert.Equal(NorthWestLot, lot.ActiveLotNumber);
        }

        // ---- TransitHandover: what happens when the walk ends ----------------------------------

        private static TransitHandover Handover(double progress, bool avatarCarried)
        {
            var lot = new NeighbourhoodTransitLot(BlockQuery(),
                ActiveLotKind.Active, NorthWestLot);
            var path = StraightWalk();
            string reason;
            lot.BeginTransit(path, out reason);
            lot.Advance(progress);
            return TransitHandover.For(lot.State, avatarCarried);
        }

        [Fact]
        public void ArrivingHandsTheSimToTheDestinationAndTakesTheStreetDown()
        {
            var handover = Handover(12, avatarCarried: true);

            Assert.True(handover.Allowed);
            Assert.True(handover.IsValid);
            Assert.Null(handover.Reason);
            Assert.Equal(SouthWestLot, handover.Destination);
            Assert.Equal(SouthWestLot, handover.ResultingLot);
            Assert.Equal(ActiveLotKind.Active, handover.ResultingKind);
            Assert.True(handover.ArrivesInLot);
            //Always: a sub-world left on the parent's blueprint holds a light map and an
            //architecture for the rest of the session, and is still drawn.
            Assert.True(handover.DisposesWorld);

            //And the outcome satisfies the invariant, which is the check that makes a permitted
            //handover more than a routing decision.
            Assert.True(handover.PreservesOwner);
            Assert.True(NeighbourhoodTransitLot.IsExactlyOneActiveLot(
                handover.ResultingKind, handover.ResultingLot, out var reason), reason);
        }

        [Fact]
        public void ArrivalIsStickySoAHandoverStaysPermitted()
        {
            //A fixed-step loop that catches up twice, or a transition retried after a failure, must
            //not be able to un-arrive a walk.
            Assert.True(Handover(20, avatarCarried: true).Allowed);
            Assert.True(Handover(1000, avatarCarried: true).Allowed);
        }

        [Fact]
        public void AMidStreetWalkIsNotHandedOver()
        {
            var handover = Handover(6, avatarCarried: true);

            Assert.False(handover.Allowed);
            Assert.NotNull(handover.Reason);
            Assert.Contains("short of lot 12", handover.Reason, StringComparison.Ordinal);
            Assert.Equal(TransitPath.NoLot, handover.Destination);

            //A refusal changes nothing, so the world is still the transit lot's to own and the sim
            //is still on the pavement.
            Assert.Equal(ActiveLotKind.Transit, handover.ResultingKind);
            Assert.False(handover.ArrivesInLot);
            Assert.False(handover.DisposesWorld);
            Assert.True(handover.PreservesOwner);
        }

        [Fact]
        public void AWalkWithNoSimInItIsNotHandedOver()
        {
            //A destination lot with no sim in it is a worse answer than a sim standing on a pavement,
            //so the conservative half wins: the caller cancels and falls back to a switch.
            var handover = Handover(12, avatarCarried: false);

            Assert.False(handover.Allowed);
            Assert.Contains("no sim to give", handover.Reason, StringComparison.Ordinal);
            Assert.True(handover.PreservesOwner);
        }

        [Fact]
        public void NoWalkIsNoHandover()
        {
            var handover = TransitHandover.For(default(TransitState), avatarCarried: true);

            Assert.False(handover.Allowed);
            Assert.Contains("nothing to hand over", handover.Reason, StringComparison.Ordinal);
            Assert.True(handover.PreservesOwner);
        }

        [Fact]
        public void ARefusalIsAlwaysItsOwnPreserving()
        {
            //The property that makes a refusal safe to hand straight back to a caller: it cannot
            //leave the world anywhere, because it does not change it.
            var refused = TransitHandover.Refuse("because");
            Assert.False(refused.Allowed);
            Assert.Equal("because", refused.Reason);
            Assert.True(refused.PreservesOwner);
            Assert.False(refused.DisposesWorld);
            Assert.False(refused.ArrivesInLot);

            //And a refusal with no reason is still a sentence, so a log line is never a blank.
            Assert.NotNull(TransitHandover.Refuse(null).Reason);
        }

        [Fact]
        public void EveryHandoverSaysWhatItIs()
        {
            //The log line is the only thing a player reporting "the walk did nothing" can be given,
            //so it has to name the outcome rather than a type name.
            var allowed = Handover(12, avatarCarried: true);
            Assert.Contains("lot 12", allowed.Describe(), StringComparison.Ordinal);
            Assert.Contains("taken down", allowed.Describe(), StringComparison.Ordinal);
            Assert.Equal(allowed.Describe(), allowed.ToString());

            var refused = Handover(0, avatarCarried: true);
            Assert.StartsWith("no handover:", refused.Describe());
        }

        // ---- the binder's way back: the shape of the seam, checked without a VM -----------------

        /// <summary>
        /// The binder's third member exists and is the same shape in both directions, which is what
        /// makes "a cancelled walk puts the sim back" a claim about a contract rather than about a
        /// particular implementation.
        ///
        /// <para>
        /// <b>Why reflection and not a call.</b> Every member of
        /// <see cref="ITransitAvatarBinder"/> needs a <c>VM</c>: the first two move an entity
        /// between two of them, and the third rebuilds one. So the parameter and return types are
        /// what there is to check without a graphics device, and they are the half that is easy to
        /// get wrong - a seam whose third method took a persist id, or returned nothing, would
        /// leave the caller unable to say whether the sim went home.
        /// </para>
        /// </summary>
        private static System.Reflection.MethodInfo RebindMethod()
        {
            var method = typeof(ITransitAvatarBinder).GetMethod("TryRebind");
            Assert.NotNull(method);
            return method;
        }

        [Fact]
        public void TheBinderHasAWayBackAndBothRealAndDeclaredBindersHaveIt()
        {
            //the seam's third direction. Without it a cancelled walk can only hand a marshal to a
            //caller that has nowhere to put it, which is how the origin lot loses its controlled
            //sim - the failure this is here to stop.
            Assert.NotNull(RebindMethod());

            //and the default binder is the implementation of it, so a caller that supplies nothing
            //gets the way back rather than a NotImplementedException on the cancel path.
            Assert.IsAssignableFrom<ITransitAvatarBinder>(TransitAvatarBinder.Default);
        }

        [Fact]
        public void TheWayBackTakesADestinationLotAndTheStateAndGivesBackAnEntity()
        {
            //(destination VM, the marshal TryUnbind produced, a tile) -> (the entity, the reason).
            //The destination is a parameter rather than something the binder remembers, because the
            //binder is stateless and shared: the origin lot on a cancel and the destination lot on
            //an arrival are two different calls to the same object.
            var rebind = RebindMethod();
            var parameters = rebind.GetParameters();

            Assert.Equal(typeof(VM), parameters[0].ParameterType);
            Assert.Equal(typeof(VMAvatarMarshal), parameters[1].ParameterType);
            Assert.Equal(typeof(LotTilePos), parameters[2].ParameterType);
            Assert.True(parameters[3].IsOut);
            Assert.True(parameters[4].IsOut);
            Assert.Equal(typeof(VMEntity), parameters[3].ParameterType.GetElementType());
            Assert.Equal(typeof(string), parameters[4].ParameterType.GetElementType());
            Assert.Equal(typeof(bool), rebind.ReturnType);
        }

        [Fact]
        public void AWalkCanBeCancelledIntoTheLotItCameFromAndStillEndsAsAPlainCancel()
        {
            //the two shapes of "put the walk down", asked from the outside rather than by running
            //one: the four-argument form takes the lot the sim goes back into and also reports
            //whether it did, and the two-argument form - which is what the pure path and every
            //existing caller use - is still there and still means "hand me the marshal alone".
            //
            //Neither can be called without a VM, so what is asserted is that both overloads exist,
            //that the short one is the long one's own default, and that neither has changed what it
            //returns: a bool that says the walk is over either way.
            var cancel = typeof(NeighbourhoodTransitLotVm).GetMethods()
                .Where(m => m.Name == "TryCancel")
                .ToList();

            var plain = Assert.Single(cancel.FindAll(m => m.GetParameters().Length == 2));
            Assert.Equal(typeof(VMAvatarMarshal), plain.GetParameters()[0].ParameterType
                .GetElementType());
            Assert.Equal(typeof(string), plain.GetParameters()[1].ParameterType.GetElementType());

            var rebinding = Assert.Single(cancel.FindAll(m => m.GetParameters().Length == 4));
            var parameters = rebinding.GetParameters();
            Assert.Equal(typeof(VM), parameters[0].ParameterType);
            Assert.Equal(typeof(VMAvatarMarshal), parameters[1].ParameterType.GetElementType());
            //the new out parameter: without it a caller cannot tell a sim that went home from one
            //that only came back as a marshal, which is the whole difference the cancel log line
            //reports
            Assert.True(parameters[2].IsOut);
            Assert.Equal(typeof(VMEntity), parameters[2].ParameterType.GetElementType());
            Assert.Equal(typeof(string), parameters[3].ParameterType.GetElementType());
            Assert.Equal(typeof(bool), rebinding.ReturnType);
        }

        [Fact]
        public void AHandOverStillGivesTheDestinationAndTheStateAndNothingElse()
        {
            //the arrival direction is unchanged, and this is the assertion that keeps it that way:
            //the walked sim reaches the caller as a marshal, and the caller - not this type - is
            //what carries it into the destination lot through the travel seam. Widening TryHandOver
            //to load the marshal itself would be a second switch path, which is the rule this whole
            //type's remarks are written around.
            var handOver = typeof(NeighbourhoodTransitLotVm).GetMethod("TryHandOver");
            Assert.NotNull(handOver);
            var parameters = handOver.GetParameters();
            Assert.Equal(typeof(int), parameters[0].ParameterType.GetElementType());
            Assert.Equal(typeof(VMAvatarMarshal), parameters[1].ParameterType.GetElementType());
            Assert.Equal(typeof(string), parameters[2].ParameterType.GetElementType());
            Assert.Equal(3, parameters.Length);
        }

        // ---- the descriptions are usable, because they are the diagnostics ----------------------

        [Fact]
        public void EveryPlanAndRegionSaysWhatItIs()
        {
            TransitBlueprintPlan plan;
            string reason;
            Assert.True(TransitBlueprintPlan.TryCreate(BlockMap(),
                BlockLayout().FindLot(NorthWestLot), StraightWalk(),
                NeighbourhoodTransitLotVm.DefaultMarginTiles, out plan, out reason), reason);

            //A log line a person can act on: the size, where it stands, which two lots, and the
            //region and the street under it. Every assertion carries the line itself, so a failure
            //here says what the log would have said rather than only that a substring is missing.
            var described = plan.Describe();
            Assert.Contains("24x24", described, StringComparison.Ordinal);
            Assert.Contains("lot 11 -> lot 12", described, StringComparison.Ordinal);
            Assert.Contains("base alt 0", described, StringComparison.Ordinal);
            Assert.Equal(described, plan.ToString());

            //The default values are the "there is none" answers rather than nulls, so a log line
            //built from one is a sentence and not a crash.
            Assert.Null(default(TransitBlueprintPlan));
            Assert.Equal("no street region", default(TransitStreetRegion).Describe());
            Assert.Equal("no street terrain", default(TransitStreetTerrain).Describe());

            //And the region's own line carries the numbers somebody would check against a
            //screenshot.
            Assert.Contains("map tiles x[4,21)", plan.Region.Describe(), StringComparison.Ordinal);
        }
    }
}
