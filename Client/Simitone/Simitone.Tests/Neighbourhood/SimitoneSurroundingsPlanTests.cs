/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using FSO.SimAntics.Marshals.Hollow;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// B2's pure half: the terrain one neighbouring lot is built out of, and the plan that decides
    /// which neighbours exist. Everything here runs with no <c>VM</c>, no graphics device and no
    /// game content, because that is the point of the split - the decision of <em>which lots are
    /// real</em> is arithmetic over a layout, a map and a store, and the FreeSO bridge that acts on
    /// it (<c>SimitoneSurroundingsRestore</c>) is the half that needs an engine.
    ///
    /// <para>
    /// The generated fixture comes from <see cref="NeighbourhoodTestInput"/> wherever the question
    /// is about how a real packed neighbourhood answers; the hand-written maps are used wherever
    /// the question is about what a particular road byte or elevation byte does, because a
    /// generated map's roads are a fact about the generator rather than an input. That is the same
    /// division <c>NeighbourhoodQueryTests</c> makes and for the same reason.
    /// </para>
    ///
    /// <para>
    /// <b>What is deliberately not here:</b> anything that stands a <c>SubWorldComponent</c> or a
    /// <c>VM</c> up. B2's test list asks for "neighbour selection is deterministic" - below - and
    /// for "a missing neighbour IFF is skipped", which is a property of the bridge's per-neighbour
    /// <c>try</c> and is only observable with a lot view, which this suite has no business owning.
    /// </para>
    ///
    /// <para>
    /// <b>What the bridge's pure halves are tested against.</b> The three numbers B2 exists to get
    /// right are all reachable without a device, so each is a test here rather than a comment: the
    /// tile delta (against <c>SubWorldComponent.SubDraw</c>'s own rule), the base-level difference
    /// that hangs a sub-world at a height (composed through
    /// <see cref="NeighbourhoodCityMeshBuilder"/>'s vertical contract rather than a local divisor),
    /// and the lot direction (against the corner arithmetic
    /// <c>VMContext.GetTSOBuildableArea</c> and
    /// <c>VMLotTerrainRestoreTools.PositionLandmarkObjects</c> actually perform). The
    /// <c>TerrainContainerFor</c> and <c>BuildableSizeIndexFor</c> halves of the bridge are the
    /// other two, and both are asserted from this side.
    /// </para>
    /// </summary>
    public sealed class SimitoneSurroundingsPlanTests
    {
        /// <summary>The hand-written fixture's grid: big enough for a street and two rows of lots.</summary>
        private const int Size = 64;

        /// <summary>The row the fixture's single street runs along, between the two rows of lots.</summary>
        private const int StreetY = 36;

        private const int LotSize = 16;

        // ==================================================================== the fixture

        private static NeighbourhoodMap BlankMap()
        {
            return NeighbourhoodMap.Create(Size);
        }

        private static NeighbourhoodMap FlatMap(byte elevation)
        {
            var map = NeighbourhoodMap.Create(Size);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = elevation;
            return map;
        }

        /// <summary>A map whose only road is a full-width street along one row.</summary>
        private static NeighbourhoodMap StreetMap()
        {
            var map = NeighbourhoodMap.Create(Size);
            for (int x = 0; x < Size; x++) map.RoadData[map.Index(x, StreetY)] = RoadMask.All;
            return map;
        }

        /// <summary>
        /// The fixture's map: the street, on ground every lot shares. Every plan below needs the
        /// street - reachability is answered by whether a lot's door tile is on a road - and a
        /// flat elevation, so a neighbour's ground level can be compared with the active lot's.
        /// </summary>
        private static NeighbourhoodMap NeighbourMap()
        {
            var map = StreetMap();
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;
            return map;
        }

        private static NeighbourhoodLot Lot(
            short number, int x, int y, int size = LotSize,
            NeighbourhoodLotSide door = NeighbourhoodLotSide.North,
            TerrainType terrain = TerrainType.GRASS, bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
                WorldX = x,
                WorldY = y,
                Rotation = (int)door,
                TerrainType = terrain,
                BaseHeight = 4,
                DoorSide = door,
                HouseHash = 0xABCD,
                HasHouse = hasHouse
            };
        }

        private static NeighbourhoodLayout LayoutOf(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(4, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                Size, lots);
        }

        private static SimitoneSurroundingsPlacement PlacementFor(
            NeighbourhoodLayout layout, NeighbourhoodMap map, short lotNumber)
        {
            SimitoneSurroundingsPlacement placement;
            string reason;
            Assert.True(SimitoneSurroundingsPlacement.TryFor(
                layout, map, lotNumber, out placement, out reason), reason);
            return placement;
        }

        private static SimitoneNeighbourTerrain TerrainFor(
            NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            SimitoneNeighbourTerrain terrain;
            string reason;
            Assert.True(SimitoneNeighbourTerrain.TryBuild(map, lot, out terrain, out reason), reason);
            return terrain;
        }

        /// <summary>
        /// Two columns of lots either side of the fixture's street, spaced the way a packer spaces
        /// them - a gap between the columns and a gap between the rows, so the street has somewhere
        /// to go - all with their doors on it and so all mutually reachable.
        /// </summary>
        /// <list type="bullet">
        ///   <item><description>Lot 11 at (4, 4) is the lot a test stands on.</description></item>
        ///   <item><description>Lot 12 is six tiles below it, so inside a radius of eight.</description></item>
        ///   <item><description>Lots 13 and 14 are twenty across: outside eight, inside
        ///     sixty-four.</description></item>
        ///   <item><description>Lot 15 is six below lot 12 and twenty-eight below lot 11.</description></item>
        /// </list>
        private static NeighbourhoodLayout StreetLayout()
        {
            return LayoutOf(
                Lot(11, 4, 4),
                Lot(12, 4, 26),
                Lot(13, 40, 4),
                Lot(14, 40, 26),
                Lot(15, 4, 48));
        }

        private static NeighbourhoodEntrance[] StreetEntrances()
        {
            return new[]
            {
                new NeighbourhoodEntrance(11, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(12, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(13, 48, StreetY, 0, -1),
                new NeighbourhoodEntrance(14, 48, StreetY, 0, -1),
                new NeighbourhoodEntrance(15, 12, StreetY, 0, -1)
            };
        }

        /// <summary>
        /// The fixture's query, with the doors supplied rather than derived. Reachability is stated
        /// here rather than taken from the road graph, because what is under test is the plan's use
        /// of the answer, not how the road graph arrives at it - that is
        /// <c>RoadGraphTests</c>'s. The one test that exercises the derived path uses a generated
        /// map, where the two really are the same network.
        /// </summary>
        private static NeighbourhoodQuery StreetQuery(
            NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            return new NeighbourhoodQuery(layout, map, StreetEntrances());
        }

        /// <summary>The plan over the fixture, for the radius under test.</summary>
        private static SimitoneSurroundingsPlan Plan(
            NeighbourhoodMap map, NeighbourhoodLayout layout, short active, int radius)
        {
            return SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map, active), map, StreetQuery(layout, map),
                layout, null, radius);
        }

        // ============================================================ SimitoneNeighbourTerrain

        [Fact]
        public void TerrainRefusesWhatItCannotAnswerFor()
        {
            // Four refusals, four sentences. The rule the whole type is built on: a neighbour built
            // from a guessed height field is a neighbour in the wrong place, so "no" is a
            // first-class answer and never a wrong one.
            SimitoneNeighbourTerrain terrain;
            string reason;

            Assert.False(SimitoneNeighbourTerrain.TryBuild(
                null, Lot(11, 8, 8), out terrain, out reason));
            Assert.Contains("map", reason);

            Assert.False(SimitoneNeighbourTerrain.TryBuild(
                FlatMap(48), null, out terrain, out reason));
            Assert.Contains("lot", reason);

            // A lot with no footprint has no height field to read, and the message names the lot.
            var map = FlatMap(48);
            Assert.False(SimitoneNeighbourTerrain.TryBuild(
                map, Lot(77, 8, 8, size: 0), out terrain, out reason));
            Assert.Contains("77", reason);

            // And the default value is not a terrain: it has no grid, and every accessor on it
            // answers safely rather than dereferencing nothing.
            Assert.False(terrain.IsValid);
            Assert.Equal(0, (int)terrain.HeightAt(0, 0));
            Assert.Equal(0, terrain.BaseLevel);
        }

        [Fact]
        public void AFlatMapGivesAFlatSixteenSampleGrid()
        {
            // The case most of a generated neighbourhood is in, and the one that has to be exactly
            // right: a lot whose ground is all one elevation is a lot with a flat terrain, not a lot
            // with sixteen slightly different ones.
            var map = FlatMap(48);
            var terrain = TerrainFor(map, Lot(11, 8, 8));

            Assert.True(terrain.IsValid);
            Assert.Equal(SimitoneNeighbourTerrain.GridSamples, terrain.Heights.Count);
            var expected = SimitoneNeighbourTerrain.HeightByteFor(48);
            for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
            {
                for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                {
                    Assert.Equal(expected, terrain.HeightAt(x, y));
                }
            }

            // Off-grid reads are a loop's mistake, not a crash.
            Assert.Equal(0, (int)terrain.HeightAt(-1, 0));
            Assert.Equal(0, (int)terrain.HeightAt(0, 4));
        }

        [Fact]
        public void TheGridIsSampledOverTheLotsOwnFootprint()
        {
            // Two lots on one map at different heights, and each lot's grid has to be entirely its
            // own height. A grid that bled across the property line is how a neighbour ends up
            // tilted towards the lot next door, and it is invisible until two lots are compared.
            var map = FlatMap(48);
            var low = Lot(11, 4, 4);
            var high = Lot(12, 4, 44);
            for (int y = high.WorldY; y < high.WorldY + high.Size; y++)
            {
                for (int x = high.WorldX; x < high.WorldX + high.Size; x++)
                {
                    map.ElevationData[map.Index(x, y)] = 200;
                }
            }

            var lowTerrain = TerrainFor(map, low);
            var highTerrain = TerrainFor(map, high);

            Assert.NotEqual(lowTerrain.BaseLevel, highTerrain.BaseLevel);
            for (int i = 0; i < lowTerrain.Heights.Count; i++)
            {
                Assert.Equal(SimitoneNeighbourTerrain.HeightByteFor(48), lowTerrain.Heights[i]);
                Assert.Equal(SimitoneNeighbourTerrain.HeightByteFor(200), highTerrain.Heights[i]);
            }
        }

        [Fact]
        public void TheGridCornersAreTheLotsFirstAndLastTiles()
        {
            // The four samples on an axis are the rectangle subdivided three times, so the outer
            // two land on the lot's own first and last tile - not one past them, which would be the
            // sidewalk beside the lot and would tilt every lot towards its neighbour.
            //
            // Sixteen tiles into three cells: 0, 5, 10, 15. The four corners are raised to four
            // well-separated elevations and everything else stays at the background, so each corner
            // sample is distinguishable from an interior one.
            var map = FlatMap(240);
            var lot = Lot(11, 20, 20);
            map.ElevationData[map.Index(20, 20)] = 0;
            map.ElevationData[map.Index(35, 20)] = 60;
            map.ElevationData[map.Index(20, 35)] = 120;
            map.ElevationData[map.Index(35, 35)] = 180;

            var terrain = TerrainFor(map, lot);
            var background = SimitoneNeighbourTerrain.HeightByteFor(240);

            Assert.Equal(SimitoneNeighbourTerrain.HeightByteFor(0), terrain.HeightAt(0, 0));
            Assert.Equal(SimitoneNeighbourTerrain.HeightByteFor(60), terrain.HeightAt(3, 0));
            Assert.Equal(SimitoneNeighbourTerrain.HeightByteFor(120), terrain.HeightAt(0, 3));
            Assert.Equal(SimitoneNeighbourTerrain.HeightByteFor(180), terrain.HeightAt(3, 3));

            // The four interior samples, and every edge sample that is not a corner, are the
            // background - the grid describes the lot, not its street.
            for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
            {
                for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                {
                    var corner = (x == 0 || x == 3) && (y == 0 || y == 3);
                    if (!corner) Assert.Equal(background, terrain.HeightAt(x, y));
                }
            }
        }

        [Fact]
        public void OneHeightByteIsAHundredBlueprintAltitudeUnitsAndTwentyTwoAndAHalfElevationBytes()
        {
            // The load-bearing conversion, stated as numbers rather than as prose, and composed
            // through the unified vertical contract (NeighbourhoodCityMeshBuilder) rather than
            // through a local divisor - the whole point of the unification is that there is one
            // spelling of "elevation byte to world height" and this is it.
            //
            // The chain. The FreeSO helpers write `byte * 100` into Blueprint.Altitude and
            // Blueprint.BaseAlt, so one height byte is 100 blueprint altitude units.
            // VMTS1Activator puts 10 altitude units in an ARRY(0) unit, so that is 10 ARRY units.
            // One ARRY unit is TilesPerArryUnit = 3/16 of a tile and one tile is ElevationScale =
            // 12 of them, so 10 ARRY units is 10 * 12 * 3/16 = 22.5 elevation bytes. Which is
            // exactly the reciprocal of HeightBytesPerElevationByte, and 22.5 / 12 = 1.875 tiles
            // of world height per height byte.
            Assert.Equal(12, SimitoneNeighbourTerrain.ElevationUnitsPerTile);
            Assert.Equal(NeighbourhoodCityMeshBuilder.ElevationScale,
                (float)SimitoneNeighbourTerrain.ElevationUnitsPerTile);
            Assert.Equal(10f, SimitoneNeighbourTerrain.LotAltitudeUnitsPerArryUnit);
            Assert.Equal(NeighbourhoodCityMeshBuilder.LotAltitudeUnitsPerArryUnit,
                SimitoneNeighbourTerrain.LotAltitudeUnitsPerArryUnit);
            Assert.Equal(3f / 16f, NeighbourhoodCityMeshBuilder.TilesPerArryUnit);
            // And the tile-to-ARRY direction, which is the one the surround's ground height needs:
            // one tile is 16/3 ARRY units. Composed from the mesh builder's own numbers so a
            // change to either shows up here rather than as a slope in the middle of a street.
            Assert.Equal(16f / 3f, 1f / NeighbourhoodCityMeshBuilder.TilesPerArryUnit);
            Assert.True(Math.Abs(SimitoneNeighbourTerrain.ArryUnitsPerTile - 16f / 3f) < 0.001f,
                "one tile should be 16/3 ARRY units, not "
                + SimitoneNeighbourTerrain.ArryUnitsPerTile);
            Assert.True(Math.Abs(SimitoneNeighbourTerrain.HeightByteScale - 0.01f) < 1e-6f,
                "a height byte should be a hundredth of the helpers' unit, not "
                + SimitoneNeighbourTerrain.HeightByteScale);

            // 10 ARRY units = 22.5 elevation bytes, so 225 elevation bytes is ten height bytes
            // The same number read from the map's end. 225 elevation bytes is 22.5 * 10, so it is
            // ten height bytes, and both ends agreeing is what makes the scale composed rather than
            // guessed.
            Assert.Equal(0, (int)SimitoneNeighbourTerrain.HeightByteFor(0));
            Assert.Equal(10, (int)SimitoneNeighbourTerrain.HeightByteFor(225));
            Assert.True(Math.Abs(
                    SimitoneNeighbourTerrain.ElevationBytesPerHeightByte - 22.5f) < 0.05f,
                "a height byte should be 22.5 elevation bytes, about two tiles of map elevation,"
                + " not " + SimitoneNeighbourTerrain.ElevationBytesPerHeightByte);

            // The composition, done from the mesh builder's numbers rather than read back from
            // SimitoneNeighbourTerrain: 100 altitude units per height byte, ten altitude units per
            // ARRY unit, TilesPerArryUnit of a tile per ARRY unit, ElevationScale bytes per tile.
            // 100 / 10 * 3/16 * 12 = 22.5. This is the number the whole vertical placement rests
            // on, so it is asserted from its inputs.
            var elevationBytesPerHeightByte = (100f
                / SimitoneNeighbourTerrain.LotAltitudeUnitsPerArryUnit)
                * NeighbourhoodCityMeshBuilder.TilesPerArryUnit
                * NeighbourhoodCityMeshBuilder.ElevationScale;
            Assert.True(Math.Abs(elevationBytesPerHeightByte - 22.5f) < 0.01f,
                "the chain should compose to 22.5 elevation bytes per height byte, not "
                + elevationBytesPerHeightByte);

            // And the same fact as world height: 10 ARRY units is 10 * 3/16 = 1.875 tiles, which
            // is what TerrainComponent multiplies by TerrainFactor * 3 to place the sub-lot.
            var tilesPerHeightByte = 100f / SimitoneNeighbourTerrain.LotAltitudeUnitsPerArryUnit
                * NeighbourhoodCityMeshBuilder.TilesPerArryUnit;
            Assert.True(Math.Abs(tilesPerHeightByte - 1.875f) < 0.001f,
                "one height byte should be 1.875 tiles of world height, not "
                + tilesPerHeightByte);
        }

        [Fact]
        public void TheHeightByteNeverWrapsAndNeverGoesBackwards()
        {
            // Every elevation byte the map format can hold, asserted in one pass. The wrap is the
            // failure that matters - a lot high on the elevation range must not be turned into one
            // at the bottom of the world - and monotonicity is what makes a stepped neighbourhood
            // come out as a staircase rather than as noise.
            var previous = 0;
            for (int elevation = 0; elevation <= 255; elevation++)
            {
                var value = (int)SimitoneNeighbourTerrain.HeightByteFor((byte)elevation);
                Assert.InRange(value, 0, 255);
                Assert.True(value >= previous,
                    "elevation " + elevation + " gave " + value + " after " + previous);
                previous = value;
            }
        }

        [Fact]
        public void TheBaseLevelIsTheMeanOfTheCentreFourTimesAHundred()
        {
            // VMLotTerrainRestoreTools.GetBaseLevel's own arithmetic over a one-lot grid: a flat
            // lot is its own byte times a hundred.
            var flat = TerrainFor(FlatMap(48), Lot(11, 8, 8));
            Assert.Equal(SimitoneNeighbourTerrain.HeightByteFor(48) * 100, flat.BaseLevel);

            // And a stepped one, whose centre four do not average to a whole byte. The helpers
            // divide in floating point, so the sum of seven over four is 1.75 and the level is
            // 175; integer arithmetic would give 100 and put the lot a hundred units of altitude
            // out - a hundredth of a lot altitude unit, which is a visible lip at the property
            // line.
            var map = FlatMap(30);
            var lot = Lot(12, 8, 8);
            // The lot's first interior sample is at tile (13, 13): 8 + 5.
            map.ElevationData[map.Index(13, 13)] = 90;

            var stepped = TerrainFor(map, lot);
            var centre = (stepped.HeightAt(1, 1) + stepped.HeightAt(2, 1)
                + stepped.HeightAt(1, 2) + stepped.HeightAt(2, 2));
            Assert.Equal(7, (int)centre);
            Assert.Equal(175, stepped.BaseLevel);
            Assert.Equal((int)((centre / 4f) * 100f), stepped.BaseLevel);
        }

        [Fact]
        public void TheBaseLevelDifferenceIsWhatPlacesTheNeighbourInTheWorld()
        {
            // The bridge writes BaseAlt = activeBase - neighbourBase, and this is the whole of what
            // that difference means: ten ARRY units of elevation between the lot the player is in
            // and its neighbour is one thousand units of blueprint altitude, which is ten ARRY units
            // at a hundred altitude units each, which is what FSO's lot view turns into ground
            // height.
            var active = TerrainFor(FlatMap(225), Lot(11, 4, 4));
            var neighbour = TerrainFor(FlatMap(0), Lot(12, 4, 26));
            Assert.Equal(1000, active.BaseLevel - neighbour.BaseLevel);
        }

        [Fact]
        public void TheBlendIsTheLotsOwnGroundType()
        {
            // A generated map stamps every tile of a lot's footprint with that lot's ground type,
            // so there is no second terrain type for the blend half of the blend to name, and the
            // base half is the lot's own.
            var map = BlankMap();

            var grass = TerrainFor(map, Lot(11, 4, 4, terrain: TerrainType.GRASS));
            Assert.Equal(TerrainType.GRASS, grass.Blend.Base);
            Assert.Equal(TerrainType.GRASS, grass.Blend.Blend);
            Assert.Equal(0, (int)grass.Blend.WaterFlags);

            var snow = TerrainFor(map, Lot(12, 40, 4, terrain: TerrainType.SNOW));
            Assert.Equal(TerrainType.SNOW, snow.Blend.Base);

            // TS1's own grass variants are not drawable terrains and are not in the five-entry
            // table PopulateBlankTerrain indexes, so they fold to grass here rather than being
            // cast through.
            var dark = TerrainFor(map, Lot(13, 4, 44, terrain: TerrainType.TS1DarkGrass));
            Assert.Equal(TerrainType.GRASS, dark.Blend.Base);
            Assert.InRange((int)dark.Blend.Base, 0, 4);
        }

        [Fact]
        public void NoRoadMeansNoRoadByteAndNoAdjacencyFlag()
        {
            var terrain = TerrainFor(BlankMap(), Lot(11, 8, 8));
            Assert.Equal(0, (int)terrain.Roads);
            Assert.Equal(0, (int)terrain.Blend.AdjFlags);
            Assert.Equal("no road on any edge", SimitoneNeighbourTerrain.RoadsInWords(terrain.Roads));
        }

        [Fact]
        public void ARoadBesideALotSetsThatEdgeAndTheAdjacencyFlag()
        {
            // A street three tiles north of the property line - the packer leaves a tile of
            // sidewalk and the road graph grows its bands into the gap - and nothing anywhere else.
            var map = BlankMap();
            for (int x = 0; x < Size; x++) map.RoadData[map.Index(x, 5)] = RoadMask.All;

            var terrain = TerrainFor(map, Lot(11, 8, 8));

            Assert.Equal(SimitoneNeighbourTerrain.RoadNorth, terrain.Roads);
            Assert.Equal(SimitoneNeighbourTerrain.AdjFlagMyEdge, terrain.Blend.AdjFlags);
            Assert.Equal("north", SimitoneNeighbourTerrain.RoadsInWords(terrain.Roads));
        }

        [Fact]
        public void ARoadOnTwoSidesSetsTheCornerBetweenThem()
        {
            // A corner where the street turns, which is what RestoreRoad's corner tilemaps draw.
            // The corner bit is only right when both of its edges have a street, because one edge
            // alone is a straight.
            var map = BlankMap();
            for (int x = 0; x < Size; x++) map.RoadData[map.Index(x, 6)] = RoadMask.All;
            for (int y = 0; y < Size; y++) map.RoadData[map.Index(6, y)] = RoadMask.All;

            var terrain = TerrainFor(map, Lot(11, 8, 8));

            Assert.Equal(
                SimitoneNeighbourTerrain.RoadNorth | SimitoneNeighbourTerrain.RoadWest
                    | SimitoneNeighbourTerrain.RoadCornerTopLeft,
                terrain.Roads);
            Assert.Equal(0, (int)(terrain.Roads & SimitoneNeighbourTerrain.RoadCornerTopRight));
            Assert.Equal(0, (int)(terrain.Roads & SimitoneNeighbourTerrain.RoadCornerBottomLeft));
            Assert.Equal(0, (int)(terrain.Roads & SimitoneNeighbourTerrain.RoadCornerBottomRight));
        }

        [Fact]
        public void TheRoadProbeReachesPastTheSidewalkAndNoFurther()
        {
            // Three tiles is the whole of the probe depth, and it is a constant for a reason: the
            // packer leaves a tile of sidewalk and the road graph grows its bands into the gap, so
            // the street next door is two or three tiles past the property line. Reach further and
            // you find the street on the far side of the next plot and put a road down a lot that
            // has none.
            var inside = BlankMap();
            for (int x = 0; x < Size; x++) inside.RoadData[inside.Index(x, 5)] = RoadMask.All;
            Assert.Equal(SimitoneNeighbourTerrain.RoadNorth,
                TerrainFor(inside, Lot(11, 8, 8)).Roads);

            var outside = BlankMap();
            for (int x = 0; x < Size; x++) outside.RoadData[outside.Index(x, 4)] = RoadMask.All;
            Assert.Equal(0, (int)TerrainFor(outside, Lot(11, 8, 8)).Roads);
        }

        [Fact]
        public void ALotOnTheEdgeOfTheMapGetsRoadsOnlyWhereThereIsMap()
        {
            // A lot flush against the border has streets on the sides that face the neighbourhood
            // and no ground at all on the others. That is a real lot in a real generated world, so
            // the probe has to run off the edge of the map without throwing and without claiming a
            // road where there is nothing.
            var map = BlankMap();
            // The lot's east edge is x = 15, so x = 17 is two tiles out and inside the probe.
            for (int y = 0; y < Size; y++) map.RoadData[map.Index(17, y)] = RoadMask.All;

            var terrain = TerrainFor(map, Lot(11, 0, 0));

            Assert.Equal(SimitoneNeighbourTerrain.RoadEast, terrain.Roads);
            Assert.Equal(0, (int)(terrain.Roads & SimitoneNeighbourTerrain.RoadNorth));
            Assert.Equal(0, (int)(terrain.Roads & SimitoneNeighbourTerrain.RoadWest));
        }

        [Fact]
        public void AnOffMapLotClampsInsteadOfThrowing()
        {
            // A layout from a sidecar can name a rectangle that is not on the map. TryBuild's
            // answer here is a clamped grid rather than a refusal, because the height field is
            // still readable - it is the plan's other checks that keep such a lot from being
            // placed, and B2's rule is that a bad lot costs that lot and nothing else.
            var map = FlatMap(48);
            var terrain = TerrainFor(map, Lot(11, Size - 4, Size - 4));

            Assert.True(terrain.IsValid);
            for (int i = 0; i < terrain.Heights.Count; i++)
            {
                Assert.Equal(SimitoneNeighbourTerrain.HeightByteFor(48), terrain.Heights[i]);
            }
        }

        [Fact]
        public void DescribeNamesTheEdgesAndPrintsTheGrid()
        {
            var map = BlankMap();
            for (int x = 0; x < Size; x++) map.RoadData[map.Index(x, 5)] = RoadMask.All;

            var text = TerrainFor(map, Lot(11, 8, 8)).Describe();

            Assert.Contains("terrain GRASS", text);
            Assert.Contains("north", text);
            Assert.Contains("base level", text);
        }

        // ============================================================== SimitoneSurroundingsPlan

        [Fact]
        public void APlanNeedsAPlotAMapAndALayout()
        {
            var map = NeighbourMap();
            var layout = StreetLayout();
            var placement = PlacementFor(layout, map, 11);

            // No placement at all.
            var none = default(SimitoneSurroundingsPlacement);
            var noPlacement = SimitoneSurroundingsPlan.For(none, map, layout, 8);
            Assert.False(noPlacement.IsValid);
            Assert.Contains("placement", noPlacement.Failure);

            // No layout, no map.
            var noLayout = SimitoneSurroundingsPlan.For(placement, map, null, 8);
            Assert.False(noLayout.IsValid);
            Assert.Contains("layout", noLayout.Failure);

            var noMap = SimitoneSurroundingsPlan.For(placement, null, layout, 8);
            Assert.False(noMap.IsValid);
            Assert.Contains("map", noMap.Failure);

            // A lot the layout does not contain is a caller bug, and the sentence says which lot:
            // every offset in the plan is relative to that lot's rectangle.
            var stranger = SimitoneSurroundingsPlacement.For(map, Lot(99, 4, 4));
            var plan = SimitoneSurroundingsPlan.For(stranger, map, layout, 8);
            Assert.False(plan.IsValid);
            Assert.Contains("99", plan.Failure);
            Assert.Empty(plan.Neighbours);
        }

        [Fact]
        public void TheActiveLotIsNeverItsOwnNeighbour()
        {
            // The lot view is already drawing the player's own lot in full, from its own house
            // file. A sub-world of it would be a second copy of the house the player is standing
            // in, drawn one lot width away.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var plan = Plan(map, layout, 11, 64);

            Assert.True(plan.IsValid);
            Assert.False(plan.Contains(11));
            Assert.NotEmpty(plan.Neighbours);
            foreach (var neighbour in plan.Neighbours)
            {
                Assert.NotEqual(11, neighbour.Lot.Number);
            }
        }

        [Fact]
        public void AHouseInsideTheRadiusIsPlannedEvenWhenTheStreetDoesNotReachIt()
        {
            // The selection rule, on a fixture where both halves are unambiguous. Lot 21 is four
            // tiles from the lot the player is in and its door stands on bare ground, so the road
            // graph cannot reach it - but it has a house and is inside the radius, so it is a real
            // neighbour anyway: the route is a preference, not a permission. Reachable lots are
            // still preferred, which is what the selection order below shows - the four the street
            // reaches sort ahead of lot 21 even though lot 21 is the nearest of all.
            var map = StreetMap();
            var lots = new List<NeighbourhoodLot>(StreetLayout().Lots)
            {
                Lot(21, 24, 20)
            };
            var layout = LayoutOf(lots.ToArray());
            var entrances = new List<NeighbourhoodEntrance>(StreetEntrances())
            {
                // Bare ground in the gap between the two columns: not a road tile, so no component.
                new NeighbourhoodEntrance(21, 32, 19, 0, -1)
            };

            var plan = SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map, 11), map,
                new NeighbourhoodQuery(layout, map, entrances), layout, null, 64);

            Assert.True(plan.IsValid);
            Assert.True(plan.RoadReachability);
            // The whole in-radius set, route or not, ascending by lot number...
            Assert.Equal(new short[] { 12, 13, 14, 15, 21 }, plan.NeighbourNumbers);
            // ...and the reachable half ahead of the route-blind one in the budget's own order,
            // even though lot 21 is the nearest lot to the player (gap four against lot 12's six).
            Assert.Equal(new short[] { 12, 13, 14, 15, 21 }, plan.SelectionOrder);
            Assert.Equal(4, SimitoneSurroundingsPlan.SubWorldGapFor(
                layout.FindLot(11), layout.FindLot(21)));
        }

        [Fact]
        public void APlotWithNoDoorIsNotANeighbour()
        {
            // Inherited from RoadGraph rather than invented here: a bulldozed lot has no driveway,
            // so it has no entrance, so it has no neighbours. A blank sub-world on an empty plot is
            // not what B2 wants standing in the street.
            var map = StreetMap();
            var layout = LayoutOf(
                Lot(11, 4, 4), Lot(12, 4, 26), Lot(13, 40, 4, hasHouse: false));
            var entrances = new[]
            {
                new NeighbourhoodEntrance(11, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(12, 12, StreetY, 0, -1)
            };

            var plan = SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map, 11), map,
                new NeighbourhoodQuery(layout, map, entrances), layout, null, 64);

            Assert.Equal(new short[] { 12 }, plan.NeighbourNumbers);
        }

        [Fact]
        public void TheRadiusIsAGapBetweenRectanglesNotACentreDistance()
        {
            // Two sixteen-tile lots with a forty-tile gap between them. A radius of forty includes
            // the far one and thirty-nine does not, which is the whole reading: the same sense
            // SimitoneSurroundingsCull gives "forty tiles past the property". A radius that meant
            // "centres within n tiles" would select a different set of lots for every lot size.
            var near = Lot(11, 0, 0);
            var far = Lot(12, 56, 0);

            Assert.True(SimitoneSurroundingsPlan.WithinRadius(near, far, 40));
            Assert.False(SimitoneSurroundingsPlan.WithinRadius(near, far, 39));

            // Touching is inside a radius of zero, and so is overlapping.
            var touching = Lot(13, 16, 0);
            Assert.True(SimitoneSurroundingsPlan.WithinRadius(near, touching, 0));
            var overlapping = Lot(14, 8, 8);
            Assert.True(SimitoneSurroundingsPlan.WithinRadius(near, overlapping, 0));

            // The gap is measured per axis, so a lot that is far on one axis is out even when it is
            // close on the other.
            var sideways = Lot(15, 8, 400);
            Assert.False(SimitoneSurroundingsPlan.WithinRadius(near, sideways, 8));

            // A negative radius is zero, not an error: the only thing a caller can mean by one is
            // "no neighbourhood at all".
            Assert.True(SimitoneSurroundingsPlan.WithinRadius(near, touching, -4));
            Assert.False(SimitoneSurroundingsPlan.WithinRadius(near, far, -4));
            Assert.False(SimitoneSurroundingsPlan.WithinRadius(null, far, 100));
        }

        [Fact]
        public void TheRadiusExcludesLotsTheStreetReaches()
        {
            // Reachability is transitive, and in a neighbourhood that verifies it is most of the
            // map - so the radius is a budget, not a description, and a lot twenty tiles across is
            // not stood up as a World and a VM just because it is walkable.
            var map = StreetMap();
            var layout = StreetLayout();
            var query = StreetQuery(layout, map);
            var placement = PlacementFor(layout, map, 11);

            var wide = SimitoneSurroundingsPlan.ForQuery(placement, map, query, layout, null, 64);
            var narrow = SimitoneSurroundingsPlan.ForQuery(placement, map, query, layout, null, 8);

            Assert.Equal(new short[] { 12, 13, 14, 15 }, wide.NeighbourNumbers);
            // Only lot 12 is within eight: 13 and 14 are twenty across, 15 is twenty-eight down.
            Assert.Equal(new short[] { 12 }, narrow.NeighbourNumbers);
        }

        [Fact]
        public void EveryPlannedNeighbourIsInsideTheRadiusAndHasATerrain()
        {
            // The invariant, asserted rather than a count: whatever the reachability source
            // produced, nothing outside the radius survives, and every neighbour is buildable.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var active = layout.FindLot(11);
            var plan = Plan(map, layout, 11, 24);

            Assert.NotEmpty(plan.Neighbours);
            foreach (var neighbour in plan.Neighbours)
            {
                Assert.True(neighbour.IsValid, neighbour.Describe());
                Assert.True(neighbour.Terrain.IsValid, neighbour.Describe());
                Assert.True(SimitoneSurroundingsPlan.WithinRadius(active, neighbour.Lot, 24),
                    neighbour.Describe());
                // A flat map means every neighbour is at the active lot's own ground level, which
                // is the answer that keeps a neighbour standing on the same plane as the lot the
                // player is in.
                Assert.Equal(plan.ActiveBaseLevel, neighbour.BaseLevel);
            }
        }

        [Fact]
        public void TheSubWorldPositionIsTheLayoutsTileDelta()
        {
            // The number B2 exists to get right, and the one TSO's RestoreSurroundings gets wrong
            // for us: it derives its offsets from (size - 2) spacing on a uniform 3x3 grid, while a
            // generated layout is packed on a pitch with a margin between lots. The delta between
            // two rectangles' minimum corners is the only distance that means anything here.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var plan = Plan(map, layout, 11, 64);

            Assert.True(plan.Contains(12));
            var down = plan.NeighbourFor(12);
            Assert.Equal(0, (int)down.SubWorldPosition.X);   // lot 12 is in the same column
            Assert.Equal(22, (int)down.SubWorldPosition.Y);  // 26 - 4: the lots' own origins

            var across = plan.NeighbourFor(13);
            Assert.Equal(36, (int)across.SubWorldPosition.X); // 40 - 4
            Assert.Equal(0, (int)across.SubWorldPosition.Y);

            var far = plan.NeighbourFor(15);
            Assert.Equal(0, (int)far.SubWorldPosition.X);
            Assert.Equal(44, (int)far.SubWorldPosition.Y);    // 48 - 4
        }

        [Fact]
        public void TheSubWorldPositionIsTheTileDeltaSubDrawPutsTheSubLotAt()
        {
            // The sign, tested rather than asserted in prose. SubWorldComponent.SubDraw is the only
            // place a sub-lot is drawn, and in 2D it does `parentState.CenterTile += GlobalPosition`
            // while in 3D it sets `Cameras.ModelTranslation` to GlobalPosition * 3 - so the
            // sub-lot's own tile (0, 0) lands on the parent's tile GlobalPosition, unswapped and
            // unsigned. The expected delta is therefore the neighbour's minimum corner minus the
            // active lot's, in that order, and a negation or a transposition puts the lot on the
            // diagonally opposite plot.
            //
            // UpdateBounds disagrees - it builds its box at -GlobalPosition * 3 - and that is FSO's
            // inconsistency on a bounds-only path: the value is merged into World.SkyBounds and read
            // by the 3D sky box alone, while the parent's own box in InitSubWorlds is
            // [0, Width*3] x [0, Height*3], i.e. the parent is at GlobalPosition 0 under the same
            // positive convention. So the drawing paths are the authority and the delta is positive.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var plan = Plan(map, layout, 11, 64);
            var active = layout.FindLot(11);

            foreach (var neighbour in plan.Neighbours)
            {
                // SubDraw's arithmetic, written out: the camera's centre tile, moved by the
                // sub-world's GlobalPosition, is where the sub-lot's own origin is drawn.
                var parentCentre = new Vector2(300f, 300f);
                var drawnAt = parentCentre + neighbour.SubWorldPosition;

                // The lot's own packed corner, expressed relative to the active lot's - which is
                // what "standing on its own plot" means in the parent's tile space, since the lot
                // view puts the active lot's minimum corner at the lot view's (0, 0).
                var expected = new Vector2(
                    neighbour.Lot.WorldX - active.WorldX,
                    neighbour.Lot.WorldY - active.WorldY);

                // No message argument: xunit's two-argument Assert.Equal is the only one that
                // takes a Vector2 pair, and a third argument would bind to the comparer overload.
                // Assert.True instead, so a failure says which lot.
                Assert.True(expected == drawnAt - parentCentre,
                    "lot " + neighbour.Lot.Number + " is drawn at " + (drawnAt - parentCentre)
                    + " but its plot is at " + expected + " - " + neighbour.Describe());
                Assert.True(expected == SimitoneSurroundingsPlan.SubWorldPositionFor(
                        active, neighbour.Lot),
                    "lot " + neighbour.Lot.Number + " came out of the plan at "
                    + SimitoneSurroundingsPlan.SubWorldPositionFor(active, neighbour.Lot)
                    + " but its plot is at " + expected + " - " + neighbour.Describe());
            }

            // Explicitly: the three directions the fixture offers, against SubDraw's rule. A lot
            // below stands at +Y and a lot across at +X, both positive - which is the decision the
            // sign question turns on.
            var below = plan.NeighbourFor(12);
            Assert.Equal(22, (int)(new Vector2(0, 0) + below.SubWorldPosition).Y);
            Assert.Equal(-22, (int)(new Vector2(0, 0) - below.SubWorldPosition).Y);
            var right = plan.NeighbourFor(13);
            Assert.Equal(36, (int)(new Vector2(0, 0) + right.SubWorldPosition).X);

            // And a null on either side is the origin rather than a throw, so a caller that has
            // lost a lot gets a lot at the player's feet - visible - instead of a crash.
            Assert.Equal(Vector2.Zero,
                SimitoneSurroundingsPlan.SubWorldPositionFor(null, active));
            Assert.Equal(Vector2.Zero,
                SimitoneSurroundingsPlan.SubWorldPositionFor(active, null));
        }

        [Fact]
        public void ASubWorldsHeightIsTheDifferenceBetweenTheTwoLotsBaseLevels()
        {
            // The vertical half of the placement, and the load-bearing number the recon flagged. The
            // claim is that a sub-world's BaseAlt = activeBase - neighbourBase makes its ground
            // land on the surround's ground under it, and it can be made without a device because
            // both quantities are arithmetic.
            //
            // The scale, composed through the unified vertical contract rather than a local
            // constant: the FreeSO helpers write `byte * 100` into Blueprint.Altitude, so one
            // SimitoneNeighbourTerrain height byte is 100 blueprint altitude units, which is
            // 100 / 10 = 10 ARRY(0) units, which is 10 * ElevationUnitsPerArryUnit = 22.5
            // elevation bytes, which is 22.5 / ElevationScale = 1.875 tiles of world height. So
            // one elevation byte is 1/22.5 of a height byte, which is the reciprocal of
            // ElevationBytesPerHeightByte - and that is the number the whole vertical placement
            // rests on: a neighbour's BaseAlt is the DIFFERENCE of the two lots' levels, and
            // because both are in these same hundredth-of-a-helper-byte units the difference is
            // already a blueprint altitude unit. There is no conversion to do and no scale factor
            // to get wrong.
            Assert.Equal(12f, NeighbourhoodCityMeshBuilder.ElevationScale);
            Assert.Equal(9f / 4f, NeighbourhoodCityMeshBuilder.ElevationUnitsPerArryUnit);
            Assert.Equal(3f / 16f, NeighbourhoodCityMeshBuilder.TilesPerArryUnit);
            Assert.Equal(10f, SimitoneNeighbourTerrain.LotAltitudeUnitsPerArryUnit);
            Assert.True(Math.Abs(SimitoneNeighbourTerrain.ElevationBytesPerHeightByte - 22.5f) < 0.05f,
                "one height byte should be 22.5 elevation bytes, not "
                + SimitoneNeighbourTerrain.ElevationBytesPerHeightByte);

            // The same fact from the map's side: a flat map at elevation e gives a lot at byte
            // b = HeightByteFor(e), and its base level is b * 100. So a difference of one height
            // byte - 1.875 tiles of world height - is a difference of 100 blueprint altitude units
            // between the two lots, which is exactly what BaseAltitudeFor returns and what the
            // sub-world is hung by.
            var low = TerrainFor(FlatMap(100), Lot(11, 4, 4));
            var high = TerrainFor(FlatMap(140), Lot(12, 4, 26));
            var deltaBytes = high.BaseLevel - low.BaseLevel;
            Assert.Equal(
                (int)(SimitoneNeighbourTerrain.HeightByteFor(140) - SimitoneNeighbourTerrain.HeightByteFor(100)) * 100,
                deltaBytes);
            Assert.Equal(low.BaseLevel - high.BaseLevel,
                SimitoneSurroundingsRestore.BaseAltitudeFor(low.BaseLevel, high.BaseLevel));

            // A level neighbourhood is level, which is the common case and the one that must not
            // be off by a quantisation step.
            var flat = TerrainFor(FlatMap(120), Lot(13, 40, 4));
            Assert.Equal(0, SimitoneSurroundingsRestore.BaseAltitudeFor(flat.BaseLevel, flat.BaseLevel));

            // And the sign: a neighbour standing on higher ground than the lot the player is in
            // needs a NEGATIVE BaseAlt, because the sub-world's ground is drawn at
            // (altitude - BaseAlt) * TerrainFactor * 3 and altitude is ~0 over its own footprint.
            // Getting this backwards hangs the tall neighbour below the street.
            Assert.True(SimitoneSurroundingsRestore.BaseAltitudeFor(
                low.BaseLevel, high.BaseLevel) < 0,
                "a neighbour on higher ground must hang below the active lot");
        }

        [Fact]
        public void TheLotDirectionComesFromTheDoorSide()
        {
            // The packer chose DoorSide from the lot's distance to the map border and the road
            // graph grew the driveway on that side, so it is where this lot's road is.
            // VMLotTerrainRestoreTools.PickRoadDir would read TSO's road byte instead, and a lot
            // facing the wrong way puts its mailbox and car portal on the wrong pavement.
            //
            // Note the integers are deliberately NOT the door side. TSO's lot direction names the
            // edge its landmark row goes on and the offset from the layout's clockwise side order
            // is two - see SimitoneSurroundingsPlan.LotDirectionFor, and the corner arithmetic
            // below is what pins it.
            var map = NeighbourMap();
            var layout = LayoutOf(
                Lot(11, 4, 4, door: NeighbourhoodLotSide.North),
                Lot(12, 4, 26, door: NeighbourhoodLotSide.East),
                Lot(13, 40, 4, door: NeighbourhoodLotSide.South),
                Lot(14, 40, 26, door: NeighbourhoodLotSide.West));
            var entrances = new[]
            {
                new NeighbourhoodEntrance(11, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(12, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(13, 48, StreetY, 0, -1),
                new NeighbourhoodEntrance(14, 48, StreetY, 0, -1)
            };
            var plan = SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map, 11), map,
                new NeighbourhoodQuery(layout, map, entrances), layout, null, 128);

            // The plan's own entries, against each lot's own DoorSide. Read the expectation off the
            // lot rather than writing the integer out: this assertion failed once because the four
            // integers were transcribed from the wrong lots, which is the same class of mistake the
            // mapping itself used to be.
            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                var neighbour = plan.Neighbours[i];
                var expected = SimitoneSurroundingsPlan.LotDirectionFor(neighbour.Lot.DoorSide);
                Assert.Equal(expected, neighbour.LotDirection);
            }

            // The three neighbours' doors, and therefore the three directions they are given.
            Assert.Equal((ushort)3, plan.NeighbourFor(12).LotDirection);  // east  -> right edge
            Assert.Equal((ushort)0, plan.NeighbourFor(13).LotDirection);  // south -> bottom edge
            Assert.Equal((ushort)1, plan.NeighbourFor(14).LotDirection);  // west  -> left edge
            Assert.Equal((ushort)2,
                SimitoneSurroundingsPlan.LotDirectionFor(NeighbourhoodLotSide.North));
            Assert.Equal((ushort)1,
                SimitoneSurroundingsPlan.LotDirectionFor(NeighbourhoodLotSide.West));

            // And the plan's own entries round-trip: what the lot says, what TSO reads back. Asserted
            // with Assert.True rather than a three-argument Assert.Equal, which would bind to
            // xunit's comparer overload for an enum pair.
            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                var neighbour = plan.Neighbours[i];
                var readBack = SimitoneSurroundingsPlan.DoorSideForLotDirection(
                    neighbour.LotDirection);
                Assert.True(neighbour.Lot.DoorSide == readBack,
                    "lot " + neighbour.Lot.Number + " has its driveway on its "
                    + neighbour.Lot.DoorSide + " edge, but lot direction " + neighbour.LotDirection
                    + " puts the landmark row on the " + readBack + " edge");
            }
        }

        [Fact]
        public void ALotDirectionPutsTheLandmarkRowOnTheLotsOwnDoorEdge()
        {
            // The claim the direction exists to make, stated the way the two FreeSO helpers
            // actually read it. Both VMContext.GetTSOBuildableArea and
            // VMLotTerrainRestoreTools.PositionLandmarkObjects compute
            //     pt1 = corners[(lotDir + 2) % 4], pt2 = corners[(lotDir + 3) % 4]
            // over corners = { (5,5), (w-7,5), (w-7,h-7), (5,h-7) }, and the midpoint of that
            // pair is where the buildable area and the mailbox, bin and car portal row go. So the
            // direction is right when the midpoint lands on the edge the packer put the driveway
            // on - and nothing weaker. The helpers' arithmetic is transcribed rather than called,
            // so this suite still needs no VM and no device.
            const float Low = 5f;            // the helpers' inner corner, on both axes
            const float High = LotSize - 7f; // and the far one
            var corners = new[]
            {
                new Vector2(Low, Low),     // top-left
                new Vector2(High, Low),    // top-right
                new Vector2(High, High),   // bottom-right
                new Vector2(Low, High)     // bottom-left
            };

            // Which of the four boundary lines each door side names.
            var doorLines = new Dictionary<NeighbourhoodLotSide, string>
            {
                { NeighbourhoodLotSide.North, "low-y" },
                { NeighbourhoodLotSide.East, "high-x" },
                { NeighbourhoodLotSide.South, "high-y" },
                { NeighbourhoodLotSide.West, "low-x" }
            };

            foreach (NeighbourhoodLotSide side in Enum.GetValues(typeof(NeighbourhoodLotSide)))
            {
                var lotDir = SimitoneSurroundingsPlan.LotDirectionFor(side);
                var pt1 = corners[(lotDir + 2) % 4];
                var pt2 = corners[(lotDir + 3) % 4];
                var ctr = (pt1 + pt2) / 2f;

                // The midpoint has to sit on the door edge. A row one tile in from it is a mailbox
                // in the lawn; a row on the opposite edge is a mailbox in next door's garden, which
                // is the bug this mapping is here to prevent.
                var onDoorLine = false;
                switch (doorLines[side])
                {
                    case "low-y": onDoorLine = Math.Abs(ctr.Y - Low) < 0.001f; break;
                    case "high-y": onDoorLine = Math.Abs(ctr.Y - High) < 0.001f; break;
                    case "high-x": onDoorLine = Math.Abs(ctr.X - High) < 0.001f; break;
                    default: onDoorLine = Math.Abs(ctr.X - Low) < 0.001f; break;
                }

                Assert.True(onDoorLine,
                    "a driveway on the " + side + " edge gave lot direction " + lotDir
                    + ", whose landmark row sits at (" + ctr.X + "," + ctr.Y
                    + ") - it has to be on the " + doorLines[side] + " line");
            }
        }

        [Fact]
        public void ALotDirectionOutOfRangeFoldsToNorth()
        {
            // A hand-edited sidecar can name a side that does not exist. Folding it to north is
            // what LotEntrance.ClampFacing does with a facing it cannot read, and is much better
            // than an index that would put the buildable area somewhere off the lot entirely.
            Assert.Equal(
                SimitoneSurroundingsPlan.LotDirectionFor(NeighbourhoodLotSide.North),
                SimitoneSurroundingsPlan.LotDirectionFor((NeighbourhoodLotSide)7));
            Assert.Equal(
                SimitoneSurroundingsPlan.LotDirectionFor(NeighbourhoodLotSide.North),
                SimitoneSurroundingsPlan.LotDirectionFor((NeighbourhoodLotSide)(-3)));
        }

        [Fact]
        public void AHollowNeighbourComesFromTheStoreAndABlankOneFromItsAbsence()
        {
            // §5.3: a lot the player has left holds a hollow marshal, a lot nobody has entered
            // holds nothing and is built blank. The decision of which is which belongs to the plan,
            // so a test can assert it without a VM.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var store = new LotStore();
            store.SetHollow(13, new VMHollowMarshal());
            var query = StreetQuery(layout, map);
            var placement = PlacementFor(layout, map, 11);

            var plan = SimitoneSurroundingsPlan.ForQuery(placement, map, query, layout, store, 64);

            Assert.False(plan.NeighbourFor(12).IsHollow);
            Assert.True(plan.NeighbourFor(13).IsHollow);
            Assert.Equal(1, plan.HollowCount);

            // The same plan with no store behind it is all blank, and holds the same lots: the
            // store changes what a neighbour is made of, not which neighbours there are.
            var noStore = SimitoneSurroundingsPlan.ForQuery(placement, map, query, layout, null, 64);
            Assert.Equal(0, noStore.HollowCount);
            Assert.Equal(plan.NeighbourNumbers, noStore.NeighbourNumbers);
        }

        [Fact]
        public void NeighboursComeOutInAscendingLotNumberWhateverThePositionOrder()
        {
            // Lot numbers that count down as the lots go across the map, so an ordering by
            // position and an ordering by number are visibly different. B2's test list asks for
            // deterministic selection, and this sort is where it is decided.
            var map = NeighbourMap();
            var layout = LayoutOf(
                Lot(40, 4, 4), Lot(30, 4, 26), Lot(20, 40, 4), Lot(10, 40, 26));
            var entrances = new[]
            {
                new NeighbourhoodEntrance(40, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(30, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(20, 48, StreetY, 0, -1),
                new NeighbourhoodEntrance(10, 48, StreetY, 0, -1)
            };
            var plan = SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map, 40), map,
                new NeighbourhoodQuery(layout, map, entrances), layout, null, 128);

            Assert.Equal(new short[] { 10, 20, 30 }, plan.NeighbourNumbers);
            for (int i = 1; i < plan.Neighbours.Count; i++)
            {
                Assert.True(plan.Neighbours[i - 1].Lot.Number < plan.Neighbours[i].Lot.Number);
            }
        }

        [Fact]
        public void TwoPlansOverTheSameInputsAreElementForElementTheSame()
        {
            var map = NeighbourMap();
            var layout = StreetLayout();
            var first = Plan(map, layout, 12, 16);
            var second = Plan(map, layout, 12, 16);

            Assert.True(first.IsValid);
            Assert.NotEmpty(first.Neighbours);
            Assert.Equal(first.ActiveLot.Number, second.ActiveLot.Number);
            Assert.Equal(first.ActiveBaseLevel, second.ActiveBaseLevel);
            Assert.Equal(first.RadiusTiles, second.RadiusTiles);
            Assert.Equal(first.RoadReachability, second.RoadReachability);
            Assert.Equal(first.NeighbourNumbers, second.NeighbourNumbers);
            Assert.Equal(first.Describe(), second.Describe());
            for (int i = 0; i < first.Neighbours.Count; i++)
            {
                Assert.Equal(first.Neighbours[i].Describe(), second.Neighbours[i].Describe());
            }
        }

        [Fact]
        public void AValidPlanWithNoNeighboursIsNotAFailure()
        {
            // A lot whose neighbours are all out of the radius genuinely has none, and the caller's
            // answer to that is to draw ground and no houses rather than to log a problem. IsValid
            // is about the plan existing, not about it being worth running.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var plan = Plan(map, layout, 11, 0);

            Assert.True(plan.IsValid);
            Assert.Null(plan.Failure);
            Assert.Empty(plan.Neighbours);
            Assert.Empty(plan.Warnings);
            Assert.Contains("0 neighbour sub-worlds", plan.Describe());
        }

        [Fact]
        public void ALotTheMapCannotAnswerForIsSkippedWithAWarningNotFatal()
        {
            // B2's acceptance criterion, at the one place in the pure half where it can be
            // reached: a lot with no footprint has no height field, and the plan records why and
            // carries on with everything else.
            var map = NeighbourMap();
            var layout = LayoutOf(
                Lot(11, 4, 4), Lot(12, 4, 26), Lot(13, 40, 4, size: 0));
            var entrances = new[]
            {
                new NeighbourhoodEntrance(11, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(12, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(13, 48, StreetY, 0, -1)
            };
            var plan = SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map, 11), map,
                new NeighbourhoodQuery(layout, map, entrances), layout, null, 128);

            Assert.True(plan.IsValid);
            Assert.Equal(new short[] { 12 }, plan.NeighbourNumbers);
            Assert.Single(plan.Warnings);
            Assert.Contains("13", plan.Warnings[0]);
            Assert.Contains(plan.Warnings[0], plan.Describe());
        }

        [Fact]
        public void ThePlanIsEveryReachableLotAndEveryInRadiusHouseInsideTheRadius()
        {
            // Asserted against the query rather than against a count, so this stays true if the
            // generator's street layout changes: the plan may not drop a lot the query can reach,
            // and it may not drop an in-radius house the query cannot reach either. This is the only
            // test that uses a generated map, because it is the only one that is about a real packed
            // neighbourhood rather than about a particular byte. On this fixture every in-radius
            // house is reachable, so the union and the route agree - the assertion still spells the
            // union out, because that is the rule and not the fixture.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMapGenerator.Generate(input);
            var layout = NeighbourhoodMapAdapter.DescribeInput(input);
            var query = new NeighbourhoodQuery(layout, map);
            var placement = PlacementFor(layout, map, 11);

            var plan = SimitoneSurroundingsPlan.ForQuery(placement, map, query, layout, null, 24);

            Assert.True(plan.IsValid);
            // The fixture's own network, so the equality below is not vacuous: a generated
            // neighbourhood whose lots cannot reach each other would satisfy it for the wrong
            // reason.
            Assert.True(query.RoadComponentCount >= 1);
            Assert.NotEmpty(query.NeighboursOf(11));
            Assert.NotEmpty(plan.NeighbourNumbers);

            var reachable = new HashSet<short>();
            foreach (var number in query.NeighboursOf(11)) reachable.Add((short)number);
            var expected = new List<short>();
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var lot = layout.Lots[i];
                if (lot.Number == 11) continue;
                // The union rule: a lot the route reaches, or an in-radius house it does not.
                if (!reachable.Contains(lot.Number) && !lot.HasHouse) continue;
                if (!SimitoneSurroundingsPlan.WithinRadius(layout.FindLot(11), lot, 24)) continue;
                if (!SimitoneSurroundingsPlacement.GroundIsDrawnUnder(map, lot)) continue;
                expected.Add(lot.Number);
            }
            expected.Sort();
            Assert.Equal(expected, plan.NeighbourNumbers);

            // And building its own query selects the same lots as being handed one, which is what
            // makes the convenience overload safe to call from the world.
            var own = SimitoneSurroundingsPlan.For(placement, map, layout, null, 24);
            Assert.Equal(plan.NeighbourNumbers, own.NeighbourNumbers);
        }

        [Fact]
        public void ASubWorldIsBuiltAtTheLotsOwnSizeFlooredAtSixteen()
        {
            // RestoreRoad and PositionLandmarkObjects both work in offsets from the far edge of the
            // architecture - Width - 9, Height - 7 - so a lot narrower than about sixteen tiles has
            // no room for a road template or a mailbox row. A TS1 lot is 64, so this only fires on a
            // hand-written layout; building a nine-tile lot instead produces a neighbour with a
            // road across the middle of it.
            Assert.Equal(16, SimitoneSurroundingsPlan.SubWorldSizeFor(Lot(11, 0, 0, size: 9)));
            Assert.Equal(16, SimitoneSurroundingsPlan.SubWorldSizeFor(Lot(11, 0, 0, size: 1)));
            Assert.Equal(64, SimitoneSurroundingsPlan.SubWorldSizeFor(Lot(11, 0, 0, size: 64)));
            Assert.Equal(0, SimitoneSurroundingsPlan.SubWorldSizeFor(null));

            var map = NeighbourMap();
            var layout = LayoutOf(Lot(11, 4, 4), Lot(12, 4, 26, size: 8));
            var entrances = new[]
            {
                new NeighbourhoodEntrance(11, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(12, 12, StreetY, 0, -1)
            };
            var plan = SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map, 11), map,
                new NeighbourhoodQuery(layout, map, entrances), layout, null, 32);
            Assert.Equal(16, plan.NeighbourFor(12).SubWorldSize);
        }

        [Fact]
        public void ASubWorldIsBigEnoughToStandBesideAFullWidthStreet()
        {
            // The agreement between the plan and the road width, stated as one check. The surround
            // draws its streets at RoadGraph.MainStreetWidth tarmac tiles plus a pavement either
            // side, and a sub-world is a real lot standing in that surround at the engine's scale,
            // so a lot narrower than the corridor would have road through the middle of the house
            // the engine is standing up. Sixteen is RestoreRoad's own floor; the corridor is
            // MainStreetCorridorWidth, and it is the wider of the two.
            // Sixteen is RestoreRoad's own floor and the corridor is eight, so on this codebase
            // sixteen is the wider of the two - which is why the check is a range and not just the
            // corridor. Stated rather than assumed, because the two floors are chosen for different
            // reasons and either could become the binding one.
            Assert.Equal(RoadGraph.MainStreetCorridorWidth, RoadGraph.MainStreetWidth + 2);
            Assert.Equal(8, RoadGraph.MainStreetCorridorWidth);
            Assert.Equal(16, SimitoneSurroundingsPlan.MinSubWorldSize);
            Assert.True(
                SimitoneSurroundingsPlan.MinSubWorldSize >= RoadGraph.MainStreetCorridorWidth,
                "RestoreRoad's sixteen-tile floor is the wider of the two, so it is the binding one");

            // A TS1 lot clears it by a wide margin...
            Assert.True(SimitoneSurroundingsPlan.SubWorldFitsBesideAStreet(64));
            Assert.Equal(64, SimitoneSurroundingsPlan.SubWorldSizeFor(Lot(11, 0, 0, size: 64)));

            // ...and the degenerate sizes do not, which is the check earning its keep: a lot too
            // narrow for the street beside it is a layout the generator should not be asked to
            // surround with real lots in it.
            Assert.False(SimitoneSurroundingsPlan.SubWorldFitsBesideAStreet(9));
            Assert.False(SimitoneSurroundingsPlan.SubWorldFitsBesideAStreet(1));
            Assert.False(SimitoneSurroundingsPlan.SubWorldFitsBesideAStreet(0));
            Assert.False(SimitoneSurroundingsPlan.SubWorldFitsBesideAStreet(-4));
            Assert.False(SimitoneSurroundingsPlan.SubWorldFitsBesideAStreet(15));
            Assert.True(SimitoneSurroundingsPlan.SubWorldFitsBesideAStreet(
                SimitoneSurroundingsPlan.MinSubWorldSize));
        }

        [Fact]
        public void DescribeSaysWhatWasPlannedAndFromWhere()
        {
            // The log line a player reporting "the house next door is missing" is read against, so
            // it has to name the counts, the base level, and whether road reachability was used.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var plan = Plan(map, layout, 11, 64);

            var text = plan.Describe();
            Assert.Contains("4 neighbour sub-worlds", text);
            Assert.Contains("around lot 11", text);
            Assert.Contains("within 64 tile(s)", text);
            Assert.Contains("base level", text);
            Assert.Contains("0 hollow", text);
            Assert.DoesNotContain("no road reachability", text);

            var invalid = SimitoneSurroundingsPlan.For(
                default(SimitoneSurroundingsPlacement), map, layout, 8);
            Assert.Contains("no neighbour sub-worlds", invalid.Describe());
        }

        [Fact]
        public void ANeighbourDescribesItselfForALogLine()
        {
            var map = NeighbourMap();
            var layout = StreetLayout();
            var plan = Plan(map, layout, 11, 64);

            var text = plan.NeighbourFor(12).Describe();
            Assert.Contains("lot 12", text);
            Assert.Contains("0,22", text);   // the sub-world position
            Assert.Contains("16x16", text);
            Assert.Contains("blank", text);
            Assert.Equal("no neighbour", default(SimitoneSurroundingsNeighbour).Describe());
        }

        [Fact]
        public void TheOneOffTerrainContainerPutsTheLotInTheMiddleOfTheGrid()
        {
            // The shape the helpers read: the lot's blend and road byte at [1, 1] and the whole 4x4
            // of heights. It is the only thing standing between a generated map and
            // RestoreHeight, so both ends of it are asserted here rather than discovered in a stack
            // trace. No VM and no device: the container is a plain serialisable.
            var map = BlankMap();
            for (int x = 0; x < Size; x++) map.RoadData[map.Index(x, 5)] = RoadMask.All;
            var terrain = TerrainFor(map, Lot(11, 8, 8));

            var container = SimitoneSurroundingsRestore.TerrainContainerFor(terrain);

            Assert.Equal(terrain.Blend, container.BlendN[1, 1]);
            Assert.Equal(terrain.Roads, container.Roads[1, 1]);
            for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
            {
                for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                {
                    Assert.Equal(terrain.HeightAt(x, y), container.Height[x, y]);
                }
            }

            // The road byte is passed through, not re-derived, so its eight bits have to mean what
            // RestoreRoad reads them to mean - the four low bits are the four edges and the high
            // nibble is the four corner tilemaps. RestoreRoad tests `roads & 8` for the vertical
            // strip it lays at x = 1 (the west edge), `& 4` for the horizontal band at y = 1
            // (north), `& 2` for the strip at x = w-7 (east) and `& 1` for the band at y = h-7
            // (south); then `corners = roads >> 4` with bit 1 bottom-left, 2 bottom-right, 4
            // top-right and 8 top. That is exactly the four edge and four corner constants, and
            // the container has to carry it through unchanged - a transposed nibble here is a road
            // across the middle of a lot.
            var northRoad = TerrainFor(StreetAlong(5), Lot(12, 8, 8));
            Assert.Equal(SimitoneNeighbourTerrain.RoadNorth, northRoad.Roads);
            Assert.Equal(northRoad.Roads,
                SimitoneSurroundingsRestore.TerrainContainerFor(northRoad).Roads[1, 1]);

            // A street along two adjacent sides, so the corner bit is set as well. The lot at
            // (8,8) is 16 tiles, so its north edge is y = 8 and its west edge is x = 8, and the
            // three-tile probe reaches y = 5 and x = 5. A row and a COLUMN: the north probe walks
            // a row and the west probe walks a column, so two rows would only ever find the north
            // edge - which is exactly what this caught when it asserted two rows.
            var twoSided = TerrainFor(StreetAround(5, 5), Lot(13, 8, 8));
            Assert.Equal(
                SimitoneNeighbourTerrain.RoadNorth | SimitoneNeighbourTerrain.RoadWest
                    | SimitoneNeighbourTerrain.RoadCornerTopLeft,
                twoSided.Roads);
            Assert.Equal(twoSided.Roads,
                SimitoneSurroundingsRestore.TerrainContainerFor(twoSided).Roads[1, 1]);

            // A default terrain is a container of zeros rather than a throw: the only caller that
            // can pass one is a lot the map could not answer for, and the plan does not select such
            // a lot, so this is a backstop rather than a supported input.
            var empty = SimitoneSurroundingsRestore.TerrainContainerFor(
                default(SimitoneNeighbourTerrain));
            Assert.Equal(0, (int)empty.Roads[1, 1]);
            for (int y = 0; y < SimitoneNeighbourTerrain.GridSize; y++)
            {
                for (int x = 0; x < SimitoneNeighbourTerrain.GridSize; x++)
                {
                    Assert.Equal(0, (int)empty.Height[x, y]);
                }
            }
        }

        /// <summary>A map with a full-width street along each named row.</summary>
        private static NeighbourhoodMap StreetAlong(params int[] rows)
        {
            var map = BlankMap();
            foreach (var row in rows)
            {
                for (int x = 0; x < Size; x++) map.RoadData[map.Index(x, row)] = RoadMask.All;
            }
            return map;
        }

        /// <summary>
        /// A map with a full-width street along the first named line and a full-height street
        /// along the second - one row and one column, which is the only shape that can reach two
        /// adjacent edges of one lot, since the road-byte probe walks a row for a north or south
        /// edge and a column for an east or west one.
        /// </summary>
        private static NeighbourhoodMap StreetAround(int row, int column)
        {
            var map = BlankMap();
            for (int x = 0; x < Size; x++) map.RoadData[map.Index(x, row)] = RoadMask.All;
            for (int y = 0; y < Size; y++) map.RoadData[map.Index(column, y)] = RoadMask.All;
            return map;
        }

        [Fact]
        public void TheBridgeRefusesRatherThanThrowing()
        {
            // Every refusal here is a sentence and no exception, because this runs while a lot is
            // being brought up and a surround that will not build is a reason in the log, not a
            // failed lot load. The VM is null throughout, so nothing below the first check is
            // reached - which is the point: the checks are in the right order.
            string reason;

            Assert.False(SimitoneSurroundingsRestore.TryRestore(null, null, out reason));
            Assert.Contains("no lot", reason);

            var map = NeighbourMap();
            var layout = StreetLayout();
            var plan = SimitoneSurroundingsPlan.For(
                PlacementFor(layout, map, 11), map, layout, null, 8);

            Assert.False(SimitoneSurroundingsRestore.TryRestore(null, plan, out reason));
            Assert.Contains("no lot", reason);

            // And disposing from nothing is nothing, not a throw.
            SimitoneSurroundingsRestore.DisposeSubWorlds(null);
        }

        [Fact]
        public void TheBuildableAreaIndexIsAValidIndexThatFitsTheSubWorld()
        {
            // SizeFor used to put the lot's TILE COUNT in the low half of TSOState.Size, and
            // VMContext.GetTSOBuildableArea reads that half as an index into
            // VMBuildableAreaInfo.BuildableSizes - a twelve-entry table. A 16- or 64-tile lot
            // indexed it at 16 or 64 and threw. The restore path happens to survive that today
            // because RestoreHeight overrides lotSInfo to 10 when OwnerID is 0, but
            // UpdateTSOBuildableArea makes no such override, so the index has to be a real one.
            Assert.Equal(0, SimitoneSurroundingsRestore.BuildableSizeIndexFor(0));
            Assert.Equal(0, SimitoneSurroundingsRestore.BuildableSizeIndexFor(12));
            Assert.Equal(0, SimitoneSurroundingsRestore.BuildableSizeIndexFor(16));
            Assert.Equal(1, SimitoneSurroundingsRestore.BuildableSizeIndexFor(20));
            Assert.Equal(2, SimitoneSurroundingsRestore.BuildableSizeIndexFor(26));

            // A full TS1 lot gets the table's own 64-tile entry - index 10 of 11, which is what a
            // TS1 lot's own state carries. Read off the table rather than written down, so a change
            // to BuildableSizes is a change here and not a stale literal.
            var sizes = FSO.SimAntics.Model.VMBuildableAreaInfo.BuildableSizes;
            var last = sizes.Length - 1;
            Assert.Equal(64, sizes[last]);
            Assert.Equal(last, SimitoneSurroundingsRestore.BuildableSizeIndexFor(64));

            // Anything past the top of the table clamps rather than running off the end, and a
            // corrupt negative size is the first entry rather than an exception.
            Assert.Equal(last, SimitoneSurroundingsRestore.BuildableSizeIndexFor(1000));
            Assert.Equal(0, SimitoneSurroundingsRestore.BuildableSizeIndexFor(-5));

            // Every size the plan can ask for is a valid index, and the entry it names is the
            // biggest one that fits - or, for a lot narrower than the table's own minimum, the
            // smallest entry there is, which is the only honest answer a lookup can give. (A TS1
            // lot is 64 tiles, so that branch only fires on a hand-written layout.)
            foreach (var size in new[] { 0, 1, 8, 9, 12, 16, 20, 26, 31, 40, 50, 60, 64, 128 })
            {
                var index = SimitoneSurroundingsRestore.BuildableSizeIndexFor(size);
                Assert.InRange(index, 0, last);
                if (size < sizes[0])
                {
                    Assert.Equal(0, index);
                }
                else
                {
                    Assert.InRange(sizes[index], 1, size);
                    // And the next entry up must not fit, or the one chosen is not the biggest.
                    if (index < last) Assert.True(sizes[index + 1] > size);
                }
            }
        }
    }
}
