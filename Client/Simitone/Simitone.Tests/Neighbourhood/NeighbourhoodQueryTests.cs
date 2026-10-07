/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// §11's <c>NeighbourhoodQueryTests</c>: the spatial substrate a live neighbourhood asks
    /// questions of.
    ///
    /// <para>
    /// The fixture is hand-written rather than generated, and deliberately so. A
    /// <see cref="NeighbourhoodQuery"/> is the thing that decides what a sim can walk on and
    /// which lots it can reach, so its answers have to be readable off a piece of paper: a
    /// sixteen-tile map with a named road plus, a named water patch and lots at known
    /// coordinates, where the expected <c>IsRoad</c>/<c>IsWater</c>/<c>IsLotInterior</c> for any
    /// tile is something you can say out loud. A generated map would be a better test of the
    /// generator - which <c>NeighbourhoodMapGeneratorTests</c> already is - and a worse test of
    /// the query.
    /// </para>
    ///
    /// <para>
    /// What the fixture is, precisely. On a 16x16 grid the roads are a <b>plus</b>: a column down
    /// <c>x = 8</c> and a row across <c>y = 8</c>, 31 tiles, one connected piece. The lots sit in
    /// the four quadrants around it, each a 2x2 square flush against the plus so that a door on the
    /// lot's own border edge is already on the road - which is what
    /// <see cref="RoadGraph"/>'s own anchors do, and therefore what the derived path will find.
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     Lots 11, 12, 13, 14 - one per quadrant, all 2x2, all with a house and a door on the
    ///     plus, so all four are mutually reachable.
    ///   </description></item>
    ///   <item><description>
    ///     Lot 15 - 2x2 in the north-west corner, far from any road, and bulldozed
    ///     (<c>HasHouse == false</c>). The bulldozed case: no driveway, no entrance, no
    ///     neighbours, which is the one rule a query inherits from
    ///     <see cref="RoadGraph"/> rather than inventing.
    ///   </description></item>
    ///   <item><description>
    ///     A 2x2 water patch at <c>(12, 12)</c> and a <see cref="NeighbourhoodTerrain.Nothing"/>
    ///     tile at <c>(0, 15)</c>, so "water blocks" and "no terrain does not" are each pinned
    ///     against a real tile rather than argued in a comment.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>Why the entrances are almost always supplied rather than derived.</b> A derived
    /// anchor's coordinates are the road graph's business; asserting them here would be a test of
    /// <see cref="RoadGraph"/> wearing this class's name, and <c>RoadGraphTests</c> already has
    /// them. The one derived-path test that does exist asserts the thing a query can actually be
    /// blamed for - that deriving them a second time produces the graph's own answers rather than
    /// a parallel opinion - and does it by comparing against
    /// <see cref="RoadGraph.BuildNetwork"/> instead of against literals.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodQueryTests
    {
        /// <summary>The fixture grid's side length.</summary>
        private const int Size = 16;

        private const int RoadColumn = 8;
        private const int RoadRow = 8;

        // The four corner lots, each 2x2 and each flush against the plus on one edge.
        private const int NorthWestX = 6;
        private const int NorthWestY = 6;
        private const int NorthEastX = 9;
        private const int NorthEastY = 1;
        private const int SouthWestX = 6;
        private const int SouthWestY = 10;
        private const int SouthEastX = 9;
        private const int SouthEastY = 13;

        // The bulldozed plot, in the corner of the north-west quadrant, nowhere near a road.
        private const int PlotX = 1;
        private const int PlotY = 1;

        private const int WaterX = 12;
        private const int WaterY = 12;

        // ---------------------------------------------------------------- the fixture

        /// <summary>
        /// A 16x16 map with the road plus, a water patch, one no-terrain tile and grass
        /// everywhere else.
        /// </summary>
        private static NeighbourhoodMap FixtureMap()
        {
            var map = NeighbourhoodMap.Create(Size);
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var index = map.Index(x, y);
                    if (IsRoadPlus(x, y))
                    {
                        //The mask has to agree with the neighbours' occupancy, exactly as
                        //RoadGraph.WriteMasks writes it - otherwise "IsRoad reads the low nibble"
                        //would be tested against nibbles that mean nothing.
                        byte mask = RoadMask.None;
                        if (IsRoadPlus(x + 1, y)) mask |= RoadMask.East;
                        if (IsRoadPlus(x, y + 1)) mask |= RoadMask.South;
                        if (IsRoadPlus(x - 1, y)) mask |= RoadMask.West;
                        if (IsRoadPlus(x, y - 1)) mask |= RoadMask.North;
                        map.RoadData[index] = RoadMask.IsTurnOrJunction(mask)
                            ? (byte)(mask | (mask << 4))
                            : mask;
                    }
                    if (x >= WaterX && x < WaterX + 2 && y >= WaterY && y < WaterY + 2)
                    {
                        map.TerrainType[index] = NeighbourhoodTerrain.Water;
                        map.TerrainTypeColorData[index] = NeighbourhoodTerrain.ColorFor(
                            NeighbourhoodTerrain.Water);
                    }
                    else if (x == 0 && y == 15)
                    {
                        map.TerrainType[index] = NeighbourhoodTerrain.Nothing;
                    }
                }
            }
            return map;
        }

        private static bool IsRoadPlus(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Size && y < Size
                && (x == RoadColumn || y == RoadRow);
        }

        /// <summary>The fixture's five lots.</summary>
        private static NeighbourhoodLayout FixtureLayout()
        {
            return Layout(
                Lot(11, NorthWestX, NorthWestY, NeighbourhoodLotSide.East),
                Lot(12, SouthWestX, SouthWestY, NeighbourhoodLotSide.East),
                Lot(13, NorthEastX, NorthEastY, NeighbourhoodLotSide.West),
                Lot(14, SouthEastX, SouthEastY, NeighbourhoodLotSide.West),
                Lot(15, PlotX, PlotY, NeighbourhoodLotSide.East, false));
        }

        /// <summary>One 2x2 lot record with everything but the interesting fields defaulted.</summary>
        private static NeighbourhoodLot Lot(
            short number, int x, int y, NeighbourhoodLotSide doorSide, bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Size = 2,
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

        private static NeighbourhoodLayout Layout(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(0, 4242, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                Size, lots);
        }

        /// <summary>
        /// The four doors, one per lot with a house, each on the plus one tile out from the lot's
        /// own border and facing the way its driveway would run. Lot 15 is deliberately absent: a
        /// plot with no house has no driveway.
        /// </summary>
        private static List<NeighbourhoodEntrance> CornerEntrances()
        {
            return new List<NeighbourhoodEntrance>
            {
                new NeighbourhoodEntrance(11, RoadColumn, NorthWestY, 1, 0, NeighbourhoodDoorSide.East),
                new NeighbourhoodEntrance(12, RoadColumn, SouthWestY, 1, 0, NeighbourhoodDoorSide.East),
                new NeighbourhoodEntrance(13, RoadColumn, NorthEastY, -1, 0, NeighbourhoodDoorSide.West),
                new NeighbourhoodEntrance(14, RoadColumn, SouthEastY, -1, 0, NeighbourhoodDoorSide.West),
            };
        }

        private static NeighbourhoodQuery Query(
            NeighbourhoodMap map, NeighbourhoodLayout layout,
            IReadOnlyList<NeighbourhoodEntrance> entrances)
        {
            return new NeighbourhoodQuery(layout, map, entrances);
        }

        private static NeighbourhoodQuery FixtureQuery()
        {
            return Query(FixtureMap(), FixtureLayout(), CornerEntrances());
        }

        /// <summary>
        /// A throwaway map for a graph that is only being built to supply entrances.
        /// <see cref="RoadGraph.Build"/> rasterises, so a graph must never be built on the
        /// fixture map - it would overwrite the very road bytes these tests are about.
        /// </summary>
        private static NeighbourhoodMap ScratchMap()
        {
            return NeighbourhoodMap.Create(Size);
        }

        // ---------------------------------------------------------------- classification

        [Fact]
        public void BoundsAreTheMapAndNothingElse()
        {
            var query = FixtureQuery();

            Assert.True(query.IsInBounds(0, 0));
            Assert.True(query.IsInBounds(15, 15));
            Assert.True(query.IsInBounds(RoadColumn, RoadRow));

            Assert.False(query.IsInBounds(Size, 0));
            Assert.False(query.IsInBounds(0, Size));
            Assert.False(query.IsInBounds(Size, Size));
            Assert.False(query.IsInBounds(-1, 0));
            Assert.False(query.IsInBounds(0, -1));
            Assert.False(query.IsInBounds(-1, -1));
        }

        [Fact]
        public void TileQueriesAnswerOutsideTheMapRatherThanThrowing()
        {
            //"Is this tile in the world" is the question these members are asked, so an
            //out-of-bounds coordinate is a normal answer, not a programming error. Every
            //tile-keyed member has to agree, and the extreme values are what catch a bounds
            //check written as a subtraction or a cast.
            var query = FixtureQuery();

            foreach (var pair in new[]
            {
                new[] { -1, -1 },
                new[] { Size, Size },
                new[] { int.MinValue, int.MinValue },
                new[] { int.MaxValue, 0 },
                new[] { 0, int.MaxValue },
            })
            {
                var x = pair[0];
                var y = pair[1];
                Assert.False(query.IsInBounds(x, y));
                Assert.False(query.IsRoad(x, y));
                Assert.False(query.IsWater(x, y));
                Assert.False(query.IsLotInterior(x, y));
                Assert.Null(query.LotAt(x, y));
                Assert.True(query.IsBlocked(x, y));
                Assert.Equal(NeighbourhoodQuery.NoRoadComponent, query.RoadComponentAt(x, y));
            }
        }

        [Fact]
        public void TheRoadPlusIsRoadAndOnlyThePlusIs()
        {
            var query = FixtureQuery();

            for (int i = 0; i < Size; i++)
            {
                Assert.True(query.IsRoad(RoadColumn, i), "(" + RoadColumn + "," + i + ")");
                Assert.True(query.IsRoad(i, RoadRow), "(" + i + "," + RoadRow + ")");
            }

            Assert.False(query.IsRoad(RoadColumn - 1, RoadColumn - 1));
            Assert.False(query.IsRoad(RoadColumn + 1, RoadRow + 1));
            Assert.False(query.IsRoad(0, 0));
            Assert.False(query.IsRoad(15, 15));
            Assert.False(query.IsRoad(PlotX, PlotY));
        }

        [Fact]
        public void IsRoadReadsTheMapsOwnLowNibble()
        {
            //The query's road answer is the map format's, not a second opinion: for every tile
            //in the map, IsRoad agrees with the low nibble. This is the property that lets a
            //renderer and a pathfinder trust each other.
            var map = FixtureMap();
            var query = Query(map, FixtureLayout(), CornerEntrances());

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var nibble = map.RoadMaskAt(map.Index(x, y));
                    Assert.Equal(nibble != RoadMask.None, query.IsRoad(x, y));
                }
            }
        }

        [Fact]
        public void AClearedRoadNibbleReadsAsBareGround()
        {
            //The degenerate case the type remarks name. The nibble <em>is</em> the adjacency mask,
            //so a road tile with no road neighbour has nibble 0 - which the format spells "no road
            //here". The map therefore cannot express that tile, and the query reports bare ground
            //for it. Pinned because it is a decision rather than an accident: the alternative -
            //treating the tile as road from some other signal - would make IsRoad disagree with
            //the map the renderer is drawing.
            var map = FixtureMap();
            map.RoadData[map.Index(RoadColumn, RoadColumn + 4)] = RoadMask.None;

            var query = Query(map, FixtureLayout(), CornerEntrances());

            Assert.False(query.IsRoad(RoadColumn, RoadColumn + 4));
            Assert.Equal(NeighbourhoodQuery.NoRoadComponent,
                query.RoadComponentAt(RoadColumn, RoadColumn + 4));
        }

        [Fact]
        public void WaterIsWhateverTheTerrainTypeSaysAndNothingElse()
        {
            var map = FixtureMap();
            var query = Query(map, FixtureLayout(), CornerEntrances());

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var expected = map.TerrainType[map.Index(x, y)] == NeighbourhoodTerrain.Water;
                    Assert.Equal(expected, query.IsWater(x, y));
                }
            }

            Assert.True(query.IsWater(WaterX, WaterY));
            Assert.True(query.IsWater(WaterX + 1, WaterY + 1));
            Assert.False(query.IsWater(WaterX + 2, WaterY));
            Assert.False(query.IsWater(WaterX, WaterY - 1));

            //water and road are independent: a ford crossing a street is both, and a query that
            //conflated them would refuse the crossing without saying why
            Assert.False(query.IsWater(RoadColumn, RoadRow));
            Assert.True(query.IsRoad(RoadColumn, RoadRow));
        }

        [Fact]
        public void ALotInteriorIsItsWholeRectangleAndOnlyThat()
        {
            var query = FixtureQuery();

            //lot 11 is the 2x2 at (6,6): inclusive corners, half-open edges
            Assert.True(query.IsLotInterior(NorthWestX, NorthWestY));
            Assert.True(query.IsLotInterior(NorthWestX + 1, NorthWestY + 1));
            Assert.True(query.IsLotInterior(NorthWestX + 1, NorthWestY));
            Assert.True(query.IsLotInterior(NorthWestX, NorthWestY + 1));

            //just outside, on all four sides - including the side the road is on
            Assert.False(query.IsLotInterior(NorthWestX - 1, NorthWestY));
            Assert.False(query.IsLotInterior(NorthWestX + 2, NorthWestY));
            Assert.False(query.IsLotInterior(NorthWestX, NorthWestY - 1));
            Assert.False(query.IsLotInterior(NorthWestX, NorthWestY + 2));
            Assert.False(query.IsLotInterior(RoadColumn, NorthWestY));
        }

        [Fact]
        public void LotAtNamesTheOwningLotAndNullForEverythingElse()
        {
            var query = FixtureQuery();

            Assert.Equal(11, query.LotAt(NorthWestX, NorthWestY).Number);
            Assert.Equal(12, query.LotAt(SouthWestX, SouthWestY).Number);
            Assert.Equal(13, query.LotAt(NorthEastX, NorthEastY).Number);
            Assert.Equal(14, query.LotAt(SouthEastX, SouthEastY).Number);
            Assert.Equal(15, query.LotAt(PlotX, PlotY).Number);

            //connective ground: the margin around a lot, the road, open ground, the water
            Assert.Null(query.LotAt(NorthWestX - 1, NorthWestY));
            Assert.Null(query.LotAt(RoadColumn, RoadRow));
            Assert.Null(query.LotAt(0, 0));
            Assert.Null(query.LotAt(WaterX, WaterY));
        }

        [Fact]
        public void LotAtIsTheSameRectangleIsLotInteriorReports()
        {
            //The two are the same fact reached two ways, so a raster bug that made one disagree
            //with the other would be invisible to any test that only used one. Checked over the
            //whole grid, because a mixed-up index is exactly the bug that hides behind a lucky
            //coordinate.
            var query = FixtureQuery();

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    var lot = query.LotAt(x, y);
                    Assert.Equal(query.IsLotInterior(x, y), lot != null);
                    if (lot != null)
                    {
                        Assert.True(lot.WorldX <= x && x < lot.WorldX + lot.Size,
                            "(" + x + "," + y + ") is not inside lot " + lot.Number + " in x");
                        Assert.True(lot.WorldY <= y && y < lot.WorldY + lot.Size,
                            "(" + x + "," + y + ") is not inside lot " + lot.Number + " in y");
                    }
                }
            }
        }

        [Fact]
        public void TryGetLotMatchesTheLayoutsOwnAnswer()
        {
            var query = FixtureQuery();

            NeighbourhoodLot lot;
            Assert.True(query.TryGetLot(13, out lot));
            Assert.NotNull(lot);
            Assert.Equal(13, lot.Number);

            Assert.False(query.TryGetLot(99, out lot));
            Assert.Null(lot);
            Assert.False(query.TryGetLot(0, out lot));
            Assert.False(query.TryGetLot(-1, out lot));
            Assert.False(query.TryGetLot(int.MinValue, out lot));
            Assert.False(query.TryGetLot(int.MaxValue, out lot));
        }

        [Fact]
        public void BlockedIsOutOfBoundsWaterOrALot()
        {
            //The three cases, stated as the three cases. "Lot wall" from out here means the tiles
            //a lot owns: the road graph refuses to put road on them, so the rectangle boundary
            //is exactly the thing that stops a walker.
            var query = FixtureQuery();

            //water
            Assert.True(query.IsBlocked(WaterX, WaterY));
            Assert.True(query.IsBlocked(WaterX + 1, WaterY + 1));
            //lot interior
            Assert.True(query.IsBlocked(NorthWestX, NorthWestY));
            Assert.True(query.IsBlocked(SouthEastX, SouthEastY));
            //out of bounds
            Assert.True(query.IsBlocked(Size, 4));
            Assert.True(query.IsBlocked(-1, 4));

            //walkable: open ground, margin, and road
            Assert.False(query.IsBlocked(0, 0));
            Assert.False(query.IsBlocked(NorthWestX - 1, NorthWestY));
            Assert.False(query.IsBlocked(RoadColumn, RoadRow));
            Assert.False(query.IsBlocked(4, RoadRow));
        }

        [Fact]
        public void ANothingTileIsNotBlocked()
        {
            //"Emit no quad here" is a hole in an outline, not an obstacle, and the generated
            //diamond's unused corners sit outside the walkable world rather than behind a wall. A
            //generated map never contains one, so nothing in the pipeline can contradict this; it
            //is pinned because the alternative - treating a hole as a wall - would turn a
            //rendering decision into a routing one.
            var map = FixtureMap();
            var query = Query(map, FixtureLayout(), CornerEntrances());

            Assert.Equal(NeighbourhoodTerrain.Nothing, map.TerrainType[map.Index(0, 15)]);
            Assert.False(query.IsWater(0, 15));
            Assert.False(query.IsLotInterior(0, 15));
            Assert.False(query.IsBlocked(0, 15));
        }

        [Fact]
        public void TheLowestLotNumberWinsAnOverlappingTile()
        {
            //Two lots overlapping is not something the packer produces, but a hand-written
            //sidecar can say it, and "which lot owns this tile" must then not depend on which lot
            //the query happened to visit first.
            var layout = Layout(
                Lot(21, 1, 1, NeighbourhoodLotSide.East),
                Lot(20, 1, 1, NeighbourhoodLotSide.East));
            var query = Query(FixtureMap(), layout, CornerEntrances());

            Assert.Equal(20, query.LotAt(1, 1).Number);
            Assert.Equal(20, query.LotAt(2, 2).Number);
            //both interiors are still interiors: an overlap is not a gap
            Assert.True(query.IsLotInterior(1, 1));
            Assert.True(query.IsBlocked(1, 1));
        }

        [Fact]
        public void TileCensusIsConsistentWithTheFixture()
        {
            //Every member read over the whole grid at once. The numbers are stated rather than
            //computed, so a change to the fixture is a failing assertion somebody can read rather
            //than a test that quietly asserts its own arithmetic.
            var map = FixtureMap();
            var query = Query(map, FixtureLayout(), CornerEntrances());

            var road = 0;
            var water = 0;
            var lot = 0;
            var blocked = 0;
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    if (query.IsRoad(x, y)) road++;
                    if (query.IsWater(x, y)) water++;
                    if (query.IsLotInterior(x, y)) lot++;
                    if (query.IsBlocked(x, y)) blocked++;
                }
            }

            Assert.Equal(31, road);      //16 down + 16 across - the shared centre
            Assert.Equal(4, water);      //the 2x2 patch
            Assert.Equal(20, lot);       //five 2x2 lots
            Assert.Equal(24, blocked);    //water + lots; the fixture keeps them disjoint
            Assert.Equal(Size * Size, query.TileCount);
        }

        [Fact]
        public void AMapWithNoRoadsHasNoRoadComponent()
        {
            //Zero components is not an error and must not be reported as one: a neighbourhood
            //with no lots has no road, and the invariant RoadGraph checks is about not being
            //split, not about having any.
            var query = new NeighbourhoodQuery(
                Layout(), NeighbourhoodMap.Create(Size), new List<NeighbourhoodEntrance>());

            Assert.Equal(0, query.RoadComponentCount);
            Assert.Equal(NeighbourhoodQuery.NoRoadComponent, query.RoadComponentAt(4, 4));
            Assert.Empty(query.Entrances);
            Assert.Empty(query.NeighboursOf(11));
        }

        [Fact]
        public void AQueryNeedsALayoutAndAMap()
        {
            var map = FixtureMap();
            var layout = FixtureLayout();
            var entrances = CornerEntrances();

            Assert.Throws<ArgumentNullException>(
                () => new NeighbourhoodQuery(null, map, entrances));
            Assert.Throws<ArgumentNullException>(
                () => new NeighbourhoodQuery(layout, null, entrances));
        }

        [Fact]
        public void TheFixtureRoadsAreOneComponent()
        {
            var query = FixtureQuery();

            Assert.Equal(1, query.RoadComponentCount);
            Assert.Equal(0, query.RoadComponentAt(RoadColumn, 0));
            Assert.Equal(
                query.RoadComponentAt(RoadColumn, 0),
                query.RoadComponentAt(15, RoadRow));
            Assert.Equal(
                query.RoadComponentAt(RoadColumn, 0),
                query.RoadComponentAt(0, RoadRow));
            Assert.Equal(NeighbourhoodQuery.NoRoadComponent, query.RoadComponentAt(0, 0));
        }

        // ---------------------------------------------------------------- entrances

        [Fact]
        public void ASuppliedEntranceTableIsTakenVerbatim()
        {
            var query = Query(FixtureMap(), FixtureLayout(), CornerEntrances());

            Assert.Equal(4, query.Entrances.Count);

            var eleven = query.EntranceFor(11);
            Assert.NotNull(eleven);
            Assert.Equal(11, eleven.LotNumber);
            Assert.Equal(RoadColumn, eleven.X);
            Assert.Equal(NorthWestY, eleven.Y);
            Assert.Equal(1, eleven.FacingX);
            Assert.Equal(0, eleven.FacingY);

            var thirteen = query.EntranceFor(13);
            Assert.NotNull(thirteen);
            Assert.Equal(RoadColumn, thirteen.X);
            Assert.Equal(NorthEastY, thirteen.Y);
            Assert.Equal(-1, thirteen.FacingX);
            Assert.Equal(0, thirteen.FacingY);
        }

        [Fact]
        public void EveryDoorInTheTableSitsOnRoadAndOutsideItsLot()
        {
            //The table is authoritative, so this is a statement about the fixture rather than
            //about the query - but it is the precondition the rest of these tests lean on, and a
            //fixture that drifted out of it would make a later failure mean nothing.
            var map = FixtureMap();
            var layout = FixtureLayout();
            var query = Query(map, layout, CornerEntrances());

            foreach (var entrance in query.Entrances)
            {
                Assert.True(query.IsRoad(entrance.X, entrance.Y), entrance.ToString());
                Assert.False(query.IsLotInterior(entrance.X, entrance.Y), entrance.ToString());

                NeighbourhoodLot lot;
                Assert.True(query.TryGetLot(entrance.LotNumber, out lot));
                var inwardX = entrance.X - entrance.FacingX;
                var inwardY = entrance.Y - entrance.FacingY;
                Assert.True(
                    lot.WorldX <= inwardX && inwardX < lot.WorldX + lot.Size
                    && lot.WorldY <= inwardY && inwardY < lot.WorldY + lot.Size,
                    entrance + " does not sit one tile out from lot " + lot.Number);
            }
        }

        [Fact]
        public void TheEntranceListIsOrderedByLotNumberNotByTheOrderItWasGiven()
        {
            //Two sources describing the same doors must compare equal element for element, or a
            //persisted table and a regenerated one would look like different neighbourhoods.
            var forwards = CornerEntrances();
            var backwards = CornerEntrances();
            backwards.Reverse();

            var a = Query(FixtureMap(), FixtureLayout(), forwards);
            var b = Query(FixtureMap(), FixtureLayout(), backwards);

            Assert.Equal(
                a.Entrances.Select(e => e.LotNumber).ToArray(),
                b.Entrances.Select(e => e.LotNumber).ToArray());
            Assert.Equal(new[] { 11, 12, 13, 14 }, a.Entrances.Select(e => e.LotNumber).ToArray());
        }

        [Fact]
        public void TheFirstEntranceForALotWins()
        {
            //A table that names a lot twice resolves to the first mention, so the answer is a
            //function of the list rather than of a hash table's iteration order.
            var query = Query(FixtureMap(), FixtureLayout(), new List<NeighbourhoodEntrance>
            {
                new NeighbourhoodEntrance(11, RoadColumn, 2, 1, 0),
                new NeighbourhoodEntrance(11, 2, 2, 0, 1),
            });

            Assert.Single(query.Entrances);
            Assert.Equal(RoadColumn, query.EntranceFor(11).X);
            Assert.Equal(2, query.EntranceFor(11).Y);
        }

        [Fact]
        public void EntranceForIsNullForAnythingTheTableDoesNotName()
        {
            var query = FixtureQuery();

            //a plot with no driveway, a lot that is not in the layout, and a number that is not a
            //lot at all - all three are "no entrance", and none of them may throw
            Assert.Null(query.EntranceFor(15));
            Assert.Null(query.EntranceFor(99));
            Assert.Null(query.EntranceFor(0));
            Assert.Null(query.EntranceFor(-1));
            Assert.Null(query.EntranceFor(int.MinValue));
        }

        [Fact]
        public void ASoleEntranceIsEnoughToJoinTheNetwork()
        {
            //One door on the plus is all it takes, and the rest of the plus is still one piece:
            //the query reports the network, not the lot.
            var query = Query(FixtureMap(), FixtureLayout(), new List<NeighbourhoodEntrance>
            {
                new NeighbourhoodEntrance(11, RoadColumn, 3, 1, 0),
            });

            Assert.Single(query.Entrances);
            Assert.Equal(1, query.RoadComponentCount);
            Assert.Equal(
                query.RoadComponentAt(RoadColumn, 3),
                query.RoadComponentAt(RoadRow, 1));
        }

        [Fact]
        public void DerivedEntrancesAreTheRoadGraphsOwn()
        {
            //The derived path runs RoadGraph.BuildNetwork, so this is deliberately not a test of
            //where an anchor lands - that is RoadGraph's business, and RoadGraphTests has it. It
            //is a test of the thing a query can be blamed for: that deriving the entrances a
            //second time produces the graph's answers and not a parallel opinion.
            var layout = FixtureLayout();
            var network = RoadGraph.BuildNetwork(NeighbourhoodMapAdapter.InputFor(layout));

            var query = new NeighbourhoodQuery(layout, FixtureMap());

            Assert.NotEmpty(network.Entrances);
            Assert.Equal(network.Entrances.Count, query.Entrances.Count);
            foreach (var expected in network.Entrances)
            {
                var actual = query.EntranceFor(expected.LotNumber);
                Assert.NotNull(actual);
                Assert.Equal(expected.X, actual.X);
                Assert.Equal(expected.Y, actual.Y);
                Assert.Equal(expected.FacingX, actual.FacingX);
                Assert.Equal(expected.FacingY, actual.FacingY);
            }
        }

        [Fact]
        public void AGraphSuppliesTheEntrancesWhenOneIsGiven()
        {
            //The second constructor exists so a caller that has just built the graph does not pay
            //to build it again. The two must be indistinguishable from outside, or "which
            //constructor did you use" becomes a question every caller has to remember the answer
            //to.
            var layout = FixtureLayout();
            var network = RoadGraph.Build(NeighbourhoodMapAdapter.InputFor(layout), ScratchMap());

            var derived = new NeighbourhoodQuery(layout, FixtureMap());
            var fromGraph = new NeighbourhoodQuery(layout, FixtureMap(), network);

            Assert.Equal(derived.Entrances.Count, fromGraph.Entrances.Count);
            foreach (var expected in derived.Entrances)
            {
                var actual = fromGraph.EntranceFor(expected.LotNumber);
                Assert.NotNull(actual);
                Assert.Equal(expected.X, actual.X);
                Assert.Equal(expected.Y, actual.Y);
                Assert.Equal(expected.FacingX, actual.FacingX);
                Assert.Equal(expected.FacingY, actual.FacingY);
            }
        }

        [Fact]
        public void AGraphsRoadsAreNotReadThroughTheQuery()
        {
            //The graph contributes entrances and nothing else. A query whose road answer depended
            //on which constructor was used would be two answers wearing one type, and a pathfinder
            //that trusted it would be routing over ground the renderer never drew.
            var layout = FixtureLayout();
            var network = RoadGraph.Build(NeighbourhoodMapAdapter.InputFor(layout), ScratchMap());

            var fixture = FixtureMap();
            var fromGraph = new NeighbourhoodQuery(layout, fixture, network);
            var withTable = new NeighbourhoodQuery(layout, fixture, CornerEntrances());

            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    Assert.Equal(withTable.IsRoad(x, y), fromGraph.IsRoad(x, y));
                }
            }
            Assert.Equal(1, fromGraph.RoadComponentCount);
        }

        [Fact]
        public void AStoredLotEntranceOverridesTheDerivedAnchor()
        {
            //A lot that states its own entrance is stating a fact, so it wins over the derived
            //anchor - which is what makes a sidecar with stored entrances authoritative, and what
            //lets a later scope write a door down without re-deriving anything.
            var stated = new NeighbourhoodLot
            {
                Number = 11,
                Size = 2,
                WorldX = NorthWestX,
                WorldY = NorthWestY,
                DoorSide = NeighbourhoodLotSide.East,
                HasHouse = true,
                Entrance = new NeighbourhoodLotEntrance(
                    RoadColumn, NorthWestY - 2, (int)NeighbourhoodDoorSide.East),
            };

            //One lot on its own, so the comparison is between two answers about the same door and
            //nothing else can be the difference. The layout with no stated entrance derives the
            //road graph's anchor; the one with a stated entrance reports what the record says.
            var derived = new NeighbourhoodQuery(
                Layout(Lot(11, NorthWestX, NorthWestY, NeighbourhoodLotSide.East)), FixtureMap());
            var fromLot = new NeighbourhoodQuery(Layout(stated), FixtureMap());

            Assert.Single(derived.Entrances);
            Assert.Single(fromLot.Entrances);
            Assert.Equal(11, derived.Entrances[0].LotNumber);
            Assert.Equal(11, fromLot.Entrances[0].LotNumber);

            //the derived anchor is on the lot's own door side, one tile out; the stated one is
            //two tiles along the road and nowhere near it
            Assert.Equal(RoadColumn, derived.EntranceFor(11).X);
            Assert.Equal(RoadColumn, fromLot.EntranceFor(11).X);
            Assert.NotEqual(derived.EntranceFor(11).Y, fromLot.EntranceFor(11).Y);
            Assert.Equal(NorthWestY - 2, fromLot.EntranceFor(11).Y);
            Assert.Equal(1, fromLot.EntranceFor(11).FacingX);
            Assert.Equal(0, fromLot.EntranceFor(11).FacingY);
        }

        // ---------------------------------------------------------------- neighbours

        [Fact]
        public void LotsOnTheSameRoadAreEachOthersNeighbours()
        {
            var query = FixtureQuery();

            Assert.Equal(new[] { 12, 13, 14 }, query.NeighboursOf(11));
            Assert.Equal(new[] { 11, 13, 14 }, query.NeighboursOf(12));
            Assert.Equal(new[] { 11, 12, 14 }, query.NeighboursOf(13));
            Assert.Equal(new[] { 11, 12, 13 }, query.NeighboursOf(14));
        }

        [Fact]
        public void ALotWithNoEntranceHasNoNeighbours()
        {
            //Lot 15 is bulldozed: no house, so no driveway, so no door and nothing reachable from
            //one. It is in the layout, it is on the map, and it is a neighbour of nobody.
            var query = FixtureQuery();

            Assert.Null(query.EntranceFor(15));
            Assert.Empty(query.NeighboursOf(15));
            foreach (var lot in new[] { 11, 12, 13, 14 })
            {
                Assert.DoesNotContain(15, query.NeighboursOf(lot));
            }
        }

        [Fact]
        public void TheNeighbourRelationIsSymmetric()
        {
            //The property that makes a route findable in both directions. It is not free: the
            //query flood-fills once and groups lots by component, so a lot on a smaller piece
            //than the one it was asked about would be the easiest way to get this wrong.
            var query = FixtureQuery();
            var numbers = query.Lots.Select(l => (int)l.Number).ToArray();
            Assert.Equal(5, numbers.Length);

            foreach (var a in numbers)
            {
                foreach (var b in numbers)
                {
                    if (a == b) continue;
                    Assert.Equal(
                        query.NeighboursOf(a).Contains(b),
                        query.NeighboursOf(b).Contains(a));
                }
            }
        }

        [Fact]
        public void ALotIsNeverItsOwnNeighbour()
        {
            var query = FixtureQuery();

            foreach (var lot in query.Lots)
            {
                Assert.DoesNotContain(lot.Number, query.NeighboursOf(lot.Number));
            }
        }

        [Fact]
        public void NeighbourListsAreAscendingAndDistinct()
        {
            var query = FixtureQuery();

            foreach (var lot in query.Lots)
            {
                var asArray = query.NeighboursOf(lot.Number).ToArray();
                Assert.Equal(asArray.OrderBy(n => n).ToArray(), asArray);
                Assert.Equal(asArray.Distinct().ToArray(), asArray);
            }
        }

        [Fact]
        public void NeighbourListsAreTheSameListEveryTime()
        {
            //The list is built at construction and handed out read-only, so a caller may hold it
            //across frames. What is pinned here is that the contents do not drift under a caller
            //that keeps a reference, and that a query with no state is not being asked into a
            //different answer.
            var query = FixtureQuery();
            var first = query.NeighboursOf(11);
            var second = query.NeighboursOf(11);

            Assert.Equal(first.ToArray(), second.ToArray());
            Assert.Same(first, second);
        }

        [Fact]
        public void AQueryBuiltTwiceAnswersIdentically()
        {
            //Determinism, the property the whole generated world rests on: the same layout and
            //the same map must give the same answers in the same order, with nothing depending on
            //hash order or on which constructor was used.
            var map = FixtureMap();
            var layout = FixtureLayout();

            var a = Query(map, layout, CornerEntrances());
            var b = Query(map, layout, CornerEntrances());

            Assert.Equal(a.Describe(), b.Describe());
            Assert.Equal(a.RoadComponentCount, b.RoadComponentCount);
            foreach (var lot in layout.Lots)
            {
                Assert.Equal(
                    a.NeighboursOf(lot.Number).ToArray(),
                    b.NeighboursOf(lot.Number).ToArray());

                var ea = a.EntranceFor(lot.Number);
                var eb = b.EntranceFor(lot.Number);
                if (ea == null)
                {
                    Assert.Null(eb);
                }
                else
                {
                    Assert.Equal(ea.X, eb.X);
                    Assert.Equal(ea.Y, eb.Y);
                    Assert.Equal(ea.FacingX, eb.FacingX);
                    Assert.Equal(ea.FacingY, eb.FacingY);
                }
            }
        }

        [Fact]
        public void AskingTwiceChangesNothing()
        {
            //A query with no state cannot be talked into a different answer. Cheap to assert, and
            //it is the property that lets a renderer, a pathfinder and a test share one instance.
            var query = FixtureQuery();
            var before = query.Describe();

            for (int i = 0; i < 3; i++)
            {
                query.IsRoad(RoadColumn, RoadRow);
                query.IsWater(WaterX, WaterY);
                query.LotAt(NorthWestX, NorthWestY);
                query.IsBlocked(0, 0);
                query.EntranceFor(11);
                query.NeighboursOf(11);
                query.RoadComponentAt(RoadColumn, RoadRow);
                NeighbourhoodLot ignored;
                query.TryGetLot(11, out ignored);
            }

            Assert.Equal(before, query.Describe());
        }

        [Fact]
        public void NeighboursOfAnUnknownLotIsEmpty()
        {
            var query = FixtureQuery();

            Assert.Empty(query.NeighboursOf(99));
            Assert.Empty(query.NeighboursOf(0));
            Assert.Empty(query.NeighboursOf(-1));
            Assert.Empty(query.NeighboursOf(int.MinValue));
            Assert.Empty(query.NeighboursOf(int.MaxValue));
        }

        [Fact]
        public void TwoRoadsThatDoNotTouchGiveTwoSeparateNeighbourGroups()
        {
            //The split-network case, which is what the connectivity repair and
            //RoadGraph.VerifyConnected are about. A query that reported every lot as every other
            //lot's neighbour would make a split network look connected, so this pins that
            //reachability means reachability.
            var map = FixtureMap();

            //A second road, down x = 5 and only over y 12..15, so it cannot touch the plus: the
            //column is at x = 8 and the row ends at y = 8.
            for (int y = 12; y < Size; y++)
            {
                map.RoadData[map.Index(5, y)] = RoadMask.South;
            }

            var query = Query(map, FixtureLayout(), new List<NeighbourhoodEntrance>
            {
                new NeighbourhoodEntrance(11, RoadColumn, NorthWestY, 1, 0),
                new NeighbourhoodEntrance(12, 5, SouthWestY, 1, 0),
            });

            Assert.Equal(2, query.RoadComponentCount);
            Assert.NotEqual(
                query.RoadComponentAt(RoadColumn, NorthWestY),
                query.RoadComponentAt(5, SouthWestY));
            Assert.Empty(query.NeighboursOf(11));
            Assert.Empty(query.NeighboursOf(12));
        }

        [Fact]
        public void AnEntranceOnBareGroundPutsItsLotOnNoNetwork()
        {
            //The lot is still reported, with its door: the caller is told where the door is, and
            //the door simply is not on the road. The lot on the network keeps only the lots that
            //are on it with it.
            var query = Query(FixtureMap(), FixtureLayout(), new List<NeighbourhoodEntrance>
            {
                new NeighbourhoodEntrance(11, RoadColumn, NorthWestY, 1, 0),
                new NeighbourhoodEntrance(13, RoadColumn, NorthEastY, -1, 0),
                new NeighbourhoodEntrance(12, 0, 0, 0, -1),   //open ground in the far corner
            });

            var twelve = query.EntranceFor(12);
            Assert.NotNull(twelve);
            Assert.Equal(NeighbourhoodQuery.NoRoadComponent,
                query.RoadComponentAt(twelve.X, twelve.Y));

            Assert.Equal(new[] { 13 }, query.NeighboursOf(11));
            Assert.Equal(new[] { 11 }, query.NeighboursOf(13));
            Assert.Empty(query.NeighboursOf(12));
        }

        [Fact]
        public void AnEntranceOutsideTheMapIsAnEntranceWithNoRoute()
        {
            //A sidecar written by a build that used different units, or a lot packed past the
            //edge of a smaller map, produces this. It must be neither silently dropped nor a
            //crash.
            var query = Query(FixtureMap(), FixtureLayout(), new List<NeighbourhoodEntrance>
            {
                new NeighbourhoodEntrance(11, 500, 500, 1, 0),
            });

            Assert.Single(query.Entrances);
            Assert.Equal(500, query.EntranceFor(11).X);
            Assert.Empty(query.NeighboursOf(11));
        }

        // ---------------------------------------------------------------- the report

        [Fact]
        public void TheSummaryLineNamesTheGridTheLotsAndTheNetwork()
        {
            //Not a formatting nicety: this is the class's ToString, and it is what a generation
            //report quotes. Pinning it means a change to the wording shows up as a failing
            //assertion rather than as a diff nobody reads.
            Assert.Equal(
                "query 16x16, 5 lot(s), 4 entrance(s), 1 road component(s)",
                FixtureQuery().ToString());
        }

        [Fact]
        public void DescribeCoversTheGridTheCensusAndEveryLot()
        {
            var text = FixtureQuery().Describe();

            //the header and the grid
            Assert.Contains("neighbourhood query 16x16", text);
            Assert.Contains("256 tile(s)", text);

            //the census, so a reader can see at a glance that the roads did not eat the lots
            Assert.Contains("31 road", text);
            Assert.Contains("4 water", text);
            Assert.Contains("20 lot interior", text);

            //the network
            Assert.Contains("1 component(s)", text);

            //the lots
            Assert.Contains("lots: 5 (", text);
            Assert.Contains("4 with a house", text);
            Assert.Contains("1 bulldozed plot(s)", text);
            Assert.Contains("entrances: 4 of 4 lot(s) with a house", text);

            //every lot's neighbour list, so a log line alone is enough to debug a route
            Assert.Contains("lot 11 -> [12, 13, 14]", text);
            Assert.Contains("lot 15 -> []", text);
        }

        [Fact]
        public void DescribeNamesTheLotsThatCannotReachAnything()
        {
            //The only line in the report that is usually about a bug, so it is the line the
            //report exists for. Lot 15 is bulldozed and legitimately has no door; lot 14 here is
            //stranded by accident, which is the case worth reading.
            var text = Query(FixtureMap(), FixtureLayout(), new List<NeighbourhoodEntrance>
            {
                new NeighbourhoodEntrance(11, RoadColumn, NorthWestY, 1, 0),
                new NeighbourhoodEntrance(12, RoadColumn, SouthWestY, 1, 0),
                new NeighbourhoodEntrance(13, RoadColumn, NorthEastY, -1, 0),
                new NeighbourhoodEntrance(14, 0, 0, 0, -1),   //deliberately off the network
            }).Describe();

            Assert.Contains("lot 14 -> []", text);
            Assert.Contains("no route from lot(s): 14, 15", text);
        }

        [Fact]
        public void DescribeIsDeterministic()
        {
            var a = Query(FixtureMap(), FixtureLayout(), CornerEntrances()).Describe();
            var b = Query(FixtureMap(), FixtureLayout(), CornerEntrances()).Describe();

            Assert.Equal(a, b);
        }

        // ---------------------------------------------------------------- ILotEntrance

        [Fact]
        public void LotEntranceEqualsTheGraphsOwnAnswerFieldForField()
        {
            var source = new NeighbourhoodEntrance(11, 6, 4, 1, 0, NeighbourhoodDoorSide.East);
            var promoted = LotEntrance.From(source);

            Assert.Equal(11, promoted.LotNumber);
            Assert.Equal(6, promoted.X);
            Assert.Equal(4, promoted.Y);
            Assert.Equal(1, promoted.FacingX);
            Assert.Equal(0, promoted.FacingY);
            //the side the graph recorded has no counterpart on the interface, and dropping it is
            //the point: an interface that carried it would expose a field nothing consumes
            Assert.True(promoted.IsSet);
        }

        [Fact]
        public void LotEntranceComparesByValue()
        {
            var a = new LotEntrance(11, 6, 4, 1, 0);
            var b = new LotEntrance(11, 6, 4, 1, 0);
            var different = new LotEntrance(11, 6, 5, 1, 0);

            Assert.Equal(a, b);
            Assert.True(a == b);
            Assert.False(a != b);
            Assert.Equal(a.GetHashCode(), b.GetHashCode());
            Assert.True(a.Equals((object)b));

            Assert.NotEqual(a, different);
            Assert.True(a != different);
            Assert.False(a.Equals((object)"lot 11"));
        }

        [Fact]
        public void LotEntranceIsReadableOnOneLine()
        {
            //The same wording NeighbourhoodEntrance uses, so the two can be read against each
            //other in a log that has both.
            Assert.Equal("lot 11 door at (6,4) facing (1,0)", new LotEntrance(11, 6, 4, 1, 0).ToString());
        }

        [Fact]
        public void TheDefaultLotEntranceNamesNoLot()
        {
            //default is all zeros, and 0 is not a lot number - so IsSet is false rather than the
            //struct reporting a door at tile (0,0) on lot 0.
            var empty = default(LotEntrance);

            Assert.False(empty.IsSet);
            Assert.Equal(0, empty.LotNumber);
            Assert.Equal(0, empty.X);
            Assert.Equal(0, empty.Y);
        }

        [Fact]
        public void ADoorAtTheOriginIsStillADoor()
        {
            //IsSet is about the lot number, not about "some field is non-zero": (0,0) is a real
            //tile, and a lot whose door is there is a lot with a door.
            Assert.True(new LotEntrance(11, 0, 0, 0, -1).IsSet);
        }

        [Fact]
        public void ALotsOwnEntrancePromotesThroughItsStoredSide()
        {
            //The layout records a side index, the interface records an outward normal, and the
            //conversion is the one NeighbourhoodDoorSide exists for. Getting it wrong would point
            //an arriving sim away from the house instead of at it.
            var lot = Lot(11, NorthWestX, NorthWestY, NeighbourhoodLotSide.East);

            lot.Entrance = new NeighbourhoodLotEntrance(6, 4, (int)NeighbourhoodDoorSide.East);
            var east = LotEntrance.From(lot);
            Assert.Equal(11, east.LotNumber);
            Assert.Equal(6, east.X);
            Assert.Equal(4, east.Y);
            Assert.Equal(1, east.FacingX);
            Assert.Equal(0, east.FacingY);

            lot.Entrance = new NeighbourhoodLotEntrance(6, 0, (int)NeighbourhoodDoorSide.North);
            var north = LotEntrance.From(lot);
            Assert.Equal(0, north.FacingX);
            Assert.Equal(-1, north.FacingY);

            lot.Entrance = new NeighbourhoodLotEntrance(6, 4, (int)NeighbourhoodDoorSide.West);
            Assert.Equal(-1, LotEntrance.From(lot).FacingX);
            Assert.Equal(0, LotEntrance.From(lot).FacingY);
        }

        [Fact]
        public void AStoredFacingOutsideTheFourSidesFoldsToNorth()
        {
            //A hand-edited sidecar must produce a working neighbourhood, not an index crash - and
            //not a cast that turns 256 into "east". Clamping the int before it becomes a byte is
            //the whole point of the helper.
            Assert.Equal(NeighbourhoodDoorSide.North, LotEntrance.ClampFacing(-1));
            Assert.Equal(NeighbourhoodDoorSide.North, LotEntrance.ClampFacing(4));
            Assert.Equal(NeighbourhoodDoorSide.North, LotEntrance.ClampFacing(256));
            Assert.Equal(NeighbourhoodDoorSide.North, LotEntrance.ClampFacing(int.MaxValue));
            Assert.Equal(NeighbourhoodDoorSide.East, LotEntrance.ClampFacing(1));
            Assert.Equal(NeighbourhoodDoorSide.West, LotEntrance.ClampFacing(3));

            var lot = Lot(11, NorthWestX, NorthWestY, NeighbourhoodLotSide.East);
            lot.Entrance = new NeighbourhoodLotEntrance(6, 4, 256);

            var promoted = LotEntrance.From(lot);
            Assert.Equal(0, promoted.FacingX);
            Assert.Equal(-1, promoted.FacingY);
        }

        [Fact]
        public void PromotingALotWithNoEntranceFailsLoudly()
        {
            //A missing entrance is a normal state - it is null in every Part A sidecar - so the
            //non-throwing form is the one most callers want. The throwing one exists for a caller
            //that has already established there is an entrance, and its message has to name the
            //lot.
            var lot = Lot(11, NorthWestX, NorthWestY, NeighbourhoodLotSide.East);
            Assert.Null(lot.Entrance);

            var exception = Assert.Throws<InvalidOperationException>(() => LotEntrance.From(lot));
            Assert.Contains("lot 11", exception.Message);

            LotEntrance promoted;
            Assert.False(LotEntrance.TryFrom(lot, out promoted));
            Assert.False(promoted.IsSet);
        }

        [Fact]
        public void PromotingANullLotFailsRatherThanReturningAZeroDoor()
        {
            LotEntrance promoted;
            Assert.False(LotEntrance.TryFrom(null, out promoted));
            Assert.False(promoted.IsSet);
            Assert.Throws<ArgumentNullException>(() => LotEntrance.From(null));
        }
    }
}
