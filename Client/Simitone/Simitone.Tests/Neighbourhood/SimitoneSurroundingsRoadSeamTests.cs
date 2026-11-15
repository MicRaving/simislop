/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The seam between the generated surround's street and the street the real neighbouring lots
    /// draw for themselves.
    ///
    /// <para>
    /// A player reported the surround as having "no accurate line-up", and then as having
    /// "mini streets" between the surrounding lots. The part of that which is arithmetic rather than
    /// taste is this: every real neighbour is a sub-world, and
    /// <c>VMLotTerrainRestoreTools.RestoreRoad</c> stamps each lot's own half of a street on its own
    /// architecture at fixed offsets from its own edges - three or four tiles of tarmac, one of
    /// nothing, one of pavement - at <c>size - 2</c> pitch. The generated surround used to lay the
    /// road graph's own band instead, in the gap the packer leaves between two footprints. The packer
    /// reserved two tiles, which is a sidewalk and not a street, so that band could not fit the gap
    /// at all; widening the generated street instead made it overlap the lots. Both are now one
    /// grammar and one pitch: the packer reserves the whole street corridor
    /// (<see cref="SimitoneLotViewRoad.StreetCorridorTiles"/>) and the band draws exactly the tiles
    /// no sub-world draws.
    /// </para>
    ///
    /// <para>
    /// Every assertion here is arithmetic over a map, a layout and the placement's own matrix. The
    /// one that matters is
    /// <see cref="ASubWorldsPavementIsOnTheSameWorldTilesAsTheGeneratedBand"/>: the engine draws a
    /// sub-world at <c>GlobalPosition * 3</c>, so a sub-world's own lot-local tile <c>i</c> is at
    /// world tile <c>delta + i</c>, and the band has to be on the map tiles that come to.
    /// <see cref="TheSurroundDrawsOnlyTheTilesNoSubWorldDraws"/> is the corridor half of the same
    /// claim: the fill covers the reserved corridor and the two outermost rows and nothing else, so
    /// the two halves of the street meet with neither a hole nor a second band.
    /// </para>
    /// </summary>
    public class SimitoneSurroundingsRoadSeamTests
    {
        private const int MapSize = NeighbourhoodCityMeshBuilder.MapSize;
        private const int LotSize = 64;
        private const float Scale = SimitoneSurroundingsPlacement.LotViewUnitsPerTile;

        private static NeighbourhoodLot Lot(short number, int worldX, int worldY, int size = LotSize)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
                WorldX = worldX,
                WorldY = worldY,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = 0xABCD,
                HasHouse = true
            };
        }

        private static NeighbourhoodMap FlatMap(int elevation)
        {
            var map = NeighbourhoodMap.Create(MapSize);
            for (int i = 0; i < map.ElevationData.Length; i++)
            {
                map.ElevationData[i] = (byte)elevation;
                map.TerrainType[i] = NeighbourhoodTerrain.Grass;
            }
            return map;
        }

        /// <summary>
        /// A vertical street on the map: the road graph's own band, one column of tiles, north and
        /// south. The lot view's grammar is derived from where this is, not from where it is not.
        /// </summary>
        private static void MarkVerticalStreet(NeighbourhoodMap map, int x, int zFrom, int zTo)
        {
            for (int z = zFrom; z <= zTo; z++)
            {
                map.RoadData[map.Index(x, z)] = RoadMask.North | RoadMask.South;
            }
        }

        /// <summary>
        /// A street running the other way, so a lot's north edge has a road on it and nothing else.
        /// </summary>
        private static void MarkHorizontalStreet(NeighbourhoodMap map, int xFrom, int xTo, int z)
        {
            for (int x = xFrom; x <= xTo; x++)
            {
                map.RoadData[map.Index(x, z)] = RoadMask.East | RoadMask.West;
            }
        }

        /// <summary>
        /// The player's lot at (200,300) and its eastern neighbour at the packer's own pitch - the
        /// lot's side plus the whole street corridor - with the road graph's own street laid in that
        /// corridor the way it lays one in any gap this wide: six tarmac tiles starting one tile in
        /// from the low lot, which leaves a footway against each of the two lots and puts the tarmac
        /// inside <see cref="SimitoneNeighbourTerrain.RoadProbeDepth"/> of both borders, so both
        /// lots' road bytes see it and both draw their own half of the street.
        /// </summary>
        private static NeighbourhoodMap PackedStreet(out NeighbourhoodLot here, out NeighbourhoodLot next)
        {
            var map = FlatMap(48);
            here = Lot(68, 200, 300);
            next = Lot(69, here.WorldX + LotSize + SimitoneLotViewRoad.StreetCorridorTiles, here.WorldY);
            for (var j = 1; j <= RoadGraph.MainStreetWidth; j++)
            {
                MarkVerticalStreet(map, here.WorldX + LotSize + j,
                    here.WorldY, here.WorldY + here.Size - 1);
            }
            return map;
        }

        private static SimitoneLotViewRoadBand BandFor(
            NeighbourhoodMap map, SimitoneSurroundingsCull cull, params NeighbourhoodLot[] lots)
        {
            return SimitoneLotViewRoadBand.For(map, lots,
                new Rectangle(cull.MinX, cull.MinZ, cull.WidthTiles, cull.HeightTiles));
        }

        // ---- the grammar ------------------------------------------------------------------------

        [Fact]
        public void TheBandIsTheLotViewsOwnThreeTarmacOneGapOnePavementRun()
        {
            // Read straight off RestoreRoad's tilemaps: TopLeftRoadTile and TopRightRoadTile are
            // "9, 9, 9, 0, 12" per row and both start at arch tile 1, so a near edge is three tiles
            // of tarmac, a tile of nothing, and a tile of pavement. BottomRightRoadTile is
            // "12, 0, 9, 9, 9, 11" at arch.Width - 7, so a far edge is tarmac at offsets 1 to 4,
            // nothing at 5 and pavement at 6.
            var map = FlatMap(48);
            var lot = Lot(68, 200, 300);
            MarkVerticalStreet(map, lot.WorldX - 2, lot.WorldY, lot.WorldY + lot.Size - 1);

            var byte68 = SimitoneLotViewRoad.RoadByteFor(map, lot);
            Assert.True((byte68 & SimitoneNeighbourTerrain.RoadWest) != 0,
                "a street one tile outside the west edge has to set the west bit");

            var cull = SimitoneSurroundingsCull.For(
                map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, lot);
            var z = lot.WorldY + 10;

            Assert.Equal(SimitoneLotViewRoad.TarmacFromEdge,
                SimitoneLotViewRoad.TarmacOffsetFor(byte68, SimitoneNeighbourTerrain.RoadWest));
            Assert.Equal(SimitoneLotViewRoad.PavementFromEdge,
                SimitoneLotViewRoad.PavementOffsetFor(byte68, SimitoneNeighbourTerrain.RoadWest));
            //the far edge is the mirror, and it is the one RestoreRoad's BottomRightRoadTile draws:
            //pavement at offset 6 from the far edge, not 5
            Assert.Equal(SimitoneLotViewRoad.FarPavementFromEdge,
                SimitoneLotViewRoad.PavementOffsetFor(
                    SimitoneNeighbourTerrain.RoadEast, SimitoneNeighbourTerrain.RoadEast));
            Assert.Equal(SimitoneLotViewRoad.FarTarmacTiles,
                SimitoneLotViewRoad.TarmacCountFor(SimitoneNeighbourTerrain.RoadEast));
            Assert.Equal(SimitoneLotViewRoad.TarmacTiles,
                SimitoneLotViewRoad.TarmacCountFor(SimitoneNeighbourTerrain.RoadWest));
            Assert.Equal(-1, SimitoneLotViewRoad.PavementOffsetFor(
                byte68, SimitoneNeighbourTerrain.RoadNorth));

            for (int i = 1; i <= 3; i++)
            {
                Assert.True(band.IsTarmac(lot.WorldX + i, z),
                    "tarmac should be at local tile " + i + " of the west edge");
            }
            Assert.False(band.IsRoad(lot.WorldX + 4, z), "local tile 4 is the nothing column");
            Assert.True(band.IsPavement(lot.WorldX + 5, z), "local tile 5 is the pavement column");
            //local tile 0 is not this lot's own band - StampTilemap starts at arch tile 1 - but it
            //is still street: in the lot view the *next* lot's far-edge tarmac lands on it, and the
            //corridor fill is what puts it there here.
            //TheBandReachesTilesTheGeneratedMapsOwnRoadDataDoesNot pins the two-lot version.
            Assert.True(band.IsTarmac(lot.WorldX, z),
                "the lot's own outermost tile carries the street, as the next lot's tarmac does"
                + " in the lot view");
        }

        [Fact]
        public void TheBandOnlyHasStreetsOnTheEdgesTheSubWorldIsGiven()
        {
            // One road byte, asked once, by both ends of the seam. If the band and the bridge could
            // disagree about which edge has a street on it, the pavement would go straight back to
            // being somewhere else.
            var map = FlatMap(48);
            var lot = Lot(68, 200, 300);
            MarkVerticalStreet(map, lot.WorldX - 1, lot.WorldY, lot.WorldY + lot.Size - 1);

            var roads = SimitoneLotViewRoad.RoadByteFor(map, lot);
            Assert.Equal(SimitoneNeighbourTerrain.RoadWest, roads);
            Assert.Equal(roads, SimitoneNeighbourTerrain.RoadByteFor(map,
                lot.WorldX, lot.WorldY, lot.WorldX + lot.Size - 1, lot.WorldY + lot.Size - 1));

            var cull = SimitoneSurroundingsCull.For(
                map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, lot);
            var z = lot.WorldY + 10;
            Assert.True(band.IsTarmac(lot.WorldX + 1, z));
            Assert.False(band.IsRoad(lot.WorldX + 10, z), "the north edge has no street on it");
            //the west edge does have one, and its own street runs out to the fill's bound and no
            //further - which is the bound that keeps a lot with nothing opposite it from having a
            //street the width of the drawn region
            Assert.False(band.IsRoad(
                lot.WorldX - SimitoneLotViewRoadBand.MaxCorridorFillTiles - 1, z));
            Assert.False(band.IsRoad(lot.WorldX + lot.Size - 10, z), "nor the east edge");
        }

        [Fact]
        public void TheStreetIsContinuousBetweenTwoPackedLots()
        {
            // In the lot view the two bands are size - 2 apart and abut, so the middle of the street
            // is covered by the far edge's four tarmac tiles. The packer leaves size + 2 instead, so
            // four more tiles are the middle of the street here - and a hole in the middle of a road
            // is the one thing a street must not have.
            NeighbourhoodLot here, next;
            var map = PackedStreet(out here, out next);
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, here, next);

            var z = here.WorldY + 32;
            var from = here.WorldX + here.Size - 1 - SimitoneLotViewRoad.FarTarmacTiles;
            var to = next.WorldX + SimitoneLotViewRoad.TarmacTiles;
            for (int x = from; x <= to; x++)
            {
                Assert.True(band.IsTarmac(x, z),
                    "the street has a hole at map tile " + x + " between two lots that are "
                    + (next.WorldX - here.WorldX) + " tiles apart");
            }
            //and the two pavements are inside the lots, at the lot view's offsets, with the gap
            //column between each of them and the tarmac
            var hereMax = here.WorldX + here.Size - 1;
            Assert.True(band.IsPavement(
                hereMax - SimitoneLotViewRoad.FarPavementFromEdge, z));
            Assert.False(band.IsRoad(
                hereMax - SimitoneLotViewRoad.FarGapFromEdge, z), "the gap column is not road");
            Assert.True(band.IsPavement(next.WorldX + SimitoneLotViewRoad.PavementFromEdge, z));
        }

        [Fact]
        public void TheNorthEdgeIsFourTarmacAndItsPavementAtSix()
        {
            // The west edge's tilemap is "9, 9, 9, 0, 12" per row, so three tiles of tarmac, a tile of
            // nothing and a tile of pavement. The north edge's is four rows of four 9s, then zeros,
            // then twelves, so it is one tile deeper all the way. The band used to lay the west
            // grammar on all four edges, which put the surround's north pavement a tile short of the
            // sub-world's own - and a sub-world standing on that lot draws its north band itself.
            var map = FlatMap(48);
            var lot = Lot(68, 200, 300);
            MarkHorizontalStreet(map, lot.WorldX, lot.WorldX + lot.Size - 1, lot.WorldY - 1);

            var roads = SimitoneLotViewRoad.RoadByteFor(map, lot);
            Assert.Equal(SimitoneNeighbourTerrain.RoadNorth, roads);
            Assert.Equal(SimitoneLotViewRoad.NorthTarmacTiles,
                SimitoneLotViewRoad.TarmacCountFor(SimitoneNeighbourTerrain.RoadNorth));
            Assert.Equal(SimitoneLotViewRoad.NorthPavementFromEdge,
                SimitoneLotViewRoad.PavementOffsetFor(roads, SimitoneNeighbourTerrain.RoadNorth));
            Assert.Equal(SimitoneLotViewRoad.NorthGapFromEdge,
                SimitoneLotViewRoad.GapOffsetFor(roads, SimitoneNeighbourTerrain.RoadNorth));

            var cull = SimitoneSurroundingsCull.For(
                map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, lot);
            var x = lot.WorldX + 30;
            for (var i = 1; i <= SimitoneLotViewRoad.NorthTarmacTiles; i++)
            {
                Assert.True(band.IsTarmac(x, lot.WorldY + i),
                    "the north edge's tarmac is at local tile " + i);
            }
            Assert.False(band.IsRoad(x, lot.WorldY + SimitoneLotViewRoad.NorthGapFromEdge),
                "local tile 5 is the nothing row");
            Assert.True(band.IsPavement(x, lot.WorldY + SimitoneLotViewRoad.NorthPavementFromEdge),
                "the north edge's pavement is at local tile 6, not 5");
        }

        // ---- the corridor, which is the packer's half of the seam ------------------------------

        [Fact]
        public void TwoLotsThePackerPlacedAreExactlyOneCorridorApart()
        {
            // The pitch is the one number both ends of the seam measure against, so it is stated
            // here from the packer rather than from a hand-placed pair of rectangles: the footprint
            // is the lot's own side plus the corridor, and two lots on a shelf are a footprint apart.
            var here = Lot(68, 200, 300);
            var pitch = NeighbourhoodLayoutBuilder.FootprintFor(here.Size);
            var next = Lot(69, here.WorldX + pitch, here.WorldY);

            Assert.Equal(LotSize + SimitoneLotViewRoad.StreetCorridorTiles, pitch);
            Assert.Equal(SimitoneLotViewRoad.StreetCorridorTiles,
                SimitoneLotViewRoad.CorridorBetween(here, next));
            Assert.Equal(NeighbourhoodLayoutBuilder.SidewalkMargin,
                NeighbourhoodLayoutBuilder.StreetCorridorTiles);
            //and the corridor is the width a generated street occupies, which is the reason for it
            Assert.Equal(NeighbourhoodMapAdapter.StreetCorridorMapTiles,
                SimitoneLotViewRoad.StreetCorridorTiles);
            //while the lot view's own street is wider still, and cannot be the packer's pitch
            Assert.True(SimitoneLotViewRoad.LotViewStreetTiles
                < SimitoneLotViewRoad.StreetWidthFor(SimitoneNeighbourTerrain.RoadEast,
                    SimitoneNeighbourTerrain.RoadWest),
                "the two half-streets and the reserved corridor add up to more than the lot view's"
                + " own eleven, which is what the no-overlap invariant costs");
        }

        [Fact]
        public void TheSurroundDrawsOnlyTheTilesNoSubWorldDraws()
        {
            // The invariant, as arithmetic over two sets of map tiles. A sub-world stamps its own band
            // on local tiles 1..6 of its own rectangle; the outermost tile is the lot view's own
            // ground, which in the lot view is the *next* lot's tarmac. So the only tiles of the
            // street left for the surround are the two outermost rows and the reserved corridor
            // between them - CorridorFillTiles - and the two sets cannot share a tile, which is what
            // "no overlap" means here: the surround draws the corridor, the sub-worlds draw the rest.
            NeighbourhoodLot here, next;
            var map = PackedStreet(out here, out next);
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, here, next);

            var z = here.WorldY + 32;
            var hereOuter = here.WorldX + here.Size - 1;
            var nextOuter = next.WorldX;

            //no gap: the corridor and the two outermost rows are one unbroken run of tarmac
            for (var x = hereOuter; x <= nextOuter; x++)
            {
                Assert.True(band.IsTarmac(x, z), "the corridor has a hole at map tile " + x);
            }
            Assert.Equal(SimitoneLotViewRoad.CorridorFillTiles, nextOuter - hereOuter + 1);
            Assert.Equal(SimitoneLotViewRoad.StreetCorridorTiles + 2,
                SimitoneLotViewRoad.CorridorFillTiles);

            //no overlap: this lot's own band is inside its rectangle and ends one tile short of the
            //fill, and the neighbour's starts one tile past it
            Assert.True(band.IsTarmac(
                hereOuter - SimitoneLotViewRoad.FarTarmacTiles, z),
                "this lot's own far-edge tarmac is the tile inside the fill's first one");
            Assert.True(band.IsTarmac(
                nextOuter + SimitoneLotViewRoad.TarmacFromEdge, z),
                "and the neighbour's near-edge tarmac is the tile just past the fill's last one");
            Assert.False(band.IsRoad(hereOuter - SimitoneLotViewRoad.HalfStreetDepth - 1, z),
                "the buildable ground past the band is the lot's own, not street");
        }

        [Fact]
        public void AGapWiderThanTheReservedOneIsStillBridged()
        {
            // A hand-written or re-zoned layout can leave a wider gap than the packer reserves, and
            // that is the case a player reports as a "mini street": the road graph lays its street in
            // the middle of the wide ground while the lot view's own bands sit at either end of it,
            // so the two are two different streets. The fill walks out until it meets the facing lot,
            // so the one grammar draws one street across any gap the bound allows.
            var map = FlatMap(48);
            var here = Lot(68, 200, 300);
            var gap = 2 * SimitoneLotViewRoad.StreetCorridorTiles;
            var wide = Lot(69, here.WorldX + here.Size + gap, here.WorldY);
            for (var j = 2; j < gap - 2; j++)
            {
                MarkVerticalStreet(map, here.WorldX + here.Size + j,
                    here.WorldY, here.WorldY + here.Size - 1);
            }
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, here, wide);

            var z = here.WorldY + 32;
            var hereOuter = here.WorldX + here.Size - 1;
            for (var x = hereOuter; x <= wide.WorldX; x++)
            {
                Assert.True(band.IsTarmac(x, z),
                    "a " + gap + "-tile gap has a hole in its street at map tile " + x);
            }
        }

        [Fact]
        public void TheStreetIsAsWideAsTheLotViewDrawsPlusTheCorridor()
        {
            // Measured off the band rather than asserted off a constant: the street between two lots
            // the packer placed is each lot's own half-street either side of the reserved corridor,
            // which is the same width every time because the pitch is. The lot view draws eleven such
            // tiles because its own packing overlaps two lot rectangles by two; this is the width the
            // same grammar adds up to when the packer promises that no two lots share a tile.
            NeighbourhoodLot here, next;
            var map = PackedStreet(out here, out next);
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, here, next);

            var z = here.WorldY + 32;
            var hereMax = here.WorldX + here.Size - 1;
            var from = hereMax - SimitoneLotViewRoad.FarPavementFromEdge;
            var to = next.WorldX + SimitoneLotViewRoad.PavementFromEdge;
            Assert.True(band.IsPavement(from, z), "this lot's own footway, at its offset 6");
            Assert.False(band.IsRoad(hereMax - SimitoneLotViewRoad.FarGapFromEdge, z),
                "the nothing column between the tarmac and the footway");
            Assert.True(band.IsPavement(to, z), "and the neighbour's");

            //the tarmac run across the whole street is one unbroken stretch, which is the whole of
            //the complaint: a hole in it is a "mini street" beside a real one
            for (var x = hereMax - SimitoneLotViewRoad.FarTarmacTiles;
                x <= next.WorldX + SimitoneLotViewRoad.TarmacTiles; x++)
            {
                Assert.True(band.IsTarmac(x, z), "the tarmac has a hole at map tile " + x);
            }

            Assert.Equal(21, to - from + 1);
            Assert.Equal(SimitoneLotViewRoad.StreetWidthFor(SimitoneNeighbourTerrain.RoadEast,
                SimitoneNeighbourTerrain.RoadWest), to - from + 1);
            Assert.Equal(SimitoneLotViewRoad.CorridorFillTiles
                + SimitoneLotViewRoad.HalfStreetDepthFor(SimitoneNeighbourTerrain.RoadEast)
                + SimitoneLotViewRoad.HalfStreetDepthFor(SimitoneNeighbourTerrain.RoadWest),
                to - from + 1);
        }

        [Fact]
        public void ASurroundWithNoLotFacingItStillGetsAFullStreet()
        {
            // The bound on the fill is what stops a lot on the edge of the world running a street to
            // the horizon, and it has to be longer than the reserved corridor: it used to be the
            // road graph's tarmac width, which reached barely halfway across the corridor and left
            // four tiles of nothing in the middle of every street.
            Assert.True(SimitoneLotViewRoadBand.MaxCorridorFillTiles
                > SimitoneLotViewRoad.CorridorFillTiles,
                "the fill's bound has to be longer than one corridor or a packed street has a hole");

            var map = FlatMap(48);
            var lot = Lot(68, 200, 300);
            MarkVerticalStreet(map, lot.WorldX - 2, lot.WorldY, lot.WorldY + lot.Size - 1);
            var cull = SimitoneSurroundingsCull.For(
                map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, lot);

            var z = lot.WorldY + 32;
            var run = 0;
            for (var x = lot.WorldX; x > lot.WorldX - SimitoneLotViewRoadBand.MaxCorridorFillTiles; x--)
            {
                if (!band.IsTarmac(x, z)) break;
                run++;
            }
            Assert.Equal(SimitoneLotViewRoadBand.MaxCorridorFillTiles, run);
        }

        [Fact]
        public void ASubWorldsOwnHalfStreetIsTheSameTilesAsTheBands()
        {
            // The question the bridge asks about every neighbour it builds, asked here of the map's
            // own road byte: the sub-world's RestoreRoad band and the surround's band are the same
            // tiles on the same edges. It is pure arithmetic - the neighbour is built from the
            // terrain the map gives it, which is where the road byte both ends were handed comes
            // from - so a change to the grammar, to the offsets or to the rectangle moves it.
            NeighbourhoodLot here, next;
            var map = PackedStreet(out here, out next);
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, here, next);

            Assert.True(SimitoneSurroundingsRestore.HalfStreetAgreesWithTheBand(
                NeighbourFor(map, next), band),
                "the sub-world's own half-street and the surround's band are not the same tiles");
            //and the same question of the lot the player is standing on, which the lot view draws
            //itself rather than as a sub-world
            Assert.True(SimitoneSurroundingsRestore.HalfStreetAgreesWithTheBand(
                NeighbourFor(map, here), band),
                "this lot's own band and the surround's band are not the same tiles");

            //a band that knows about neither lot cannot agree with either, and says so rather than
            //passing: that is the surround with no layout behind it, which draws the map's own roads
            var empty = SimitoneLotViewRoadBand.For(map, new NeighbourhoodLot[0],
                new Rectangle(cull.MinX, cull.MinZ, cull.WidthTiles, cull.HeightTiles));
            Assert.True(empty.IsEmpty);
            Assert.False(SimitoneSurroundingsRestore.HalfStreetAgreesWithTheBand(
                NeighbourFor(map, next), empty));
            Assert.False(SimitoneSurroundingsRestore.HalfStreetAgreesWithTheBand(
                NeighbourFor(map, next), null));

            //and the check has teeth: the same lot with its rectangle one tile along is a different
            //sub-world, on different tiles, and the band does not have its street
            var shifted = new NeighbourhoodLot
            {
                Number = next.Number,
                Zone = next.Zone,
                Size = next.Size,
                MapX = next.MapX,
                MapY = next.MapY,
                WorldX = next.WorldX + 1,
                WorldY = next.WorldY,
                TerrainType = next.TerrainType,
                BaseHeight = next.BaseHeight,
                HasHouse = next.HasHouse
            };
            Assert.False(SimitoneSurroundingsRestore.HalfStreetAgreesWithTheBand(
                    NeighbourFor(map, shifted), band),
                "a sub-world one tile along the street must not be answered by this lot's band");
        }

        /// <summary>
        /// A neighbour as the bridge would build it from the map's terrain - which is the only place
        /// the road byte a sub-world is given comes from, and therefore the one both ends of the
        /// seam share.
        /// </summary>
        private static SimitoneSurroundingsNeighbour NeighbourFor(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            SimitoneNeighbourTerrain terrain;
            string reason;
            Assert.True(SimitoneNeighbourTerrain.TryBuild(map, lot, out terrain, out reason),
                "the map cannot answer for lot " + lot.Number + ": " + reason);
            Assert.NotEqual(0, terrain.Roads);
            return SimitoneSurroundingsNeighbour.Create(lot, terrain, null, Vector2.Zero, 0, null);
        }

        // ---- the alignment, which is the point ----------------------------------------------------

        [Fact]
        public void ASubWorldsPavementIsOnTheSameWorldTilesAsTheGeneratedBand()
        {
            // SubDraw puts a sub-lot's own tile (0, 0) at the parent's tile GlobalPosition, and
            // GlobalPosition is the raw map-tile delta, so the sub-world's own lot-local tile i is
            // at world tile (delta + i) * 3. RestoreRoad stamps its pavement on local tile 5 of an
            // edge that has a street. The band has to be on the map tile that lands there.
            NeighbourhoodLot here, next;
            var map = PackedStreet(out here, out next);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var cull = placement.Cull;
            var band = BandFor(map, cull, here, next);

            var delta = SimitoneSurroundingsPlan.SubWorldPositionFor(here, next);
            Assert.Equal(next.WorldX - here.WorldX, delta.X);

            //the sub-world's own west edge, and the one the layout gave it
            var edge = (byte)SimitoneNeighbourTerrain.RoadWest;
            var offset = SimitoneLotViewRoad.PavementOffsetFor(
                SimitoneLotViewRoad.RoadByteFor(map, next), edge);
            Assert.Equal(SimitoneLotViewRoad.PavementFromEdge, offset);

            //where the engine will draw the sub-world's local tile `offset`
            var worldX = (delta.X + offset) * Scale;
            //and where the surround draws the tile the band put its pavement on
            var bandTile = next.WorldX + offset;
            Assert.True(band.IsPavement(bandTile, next.WorldY + 30),
                "the band has no pavement on the neighbour's west edge at map tile " + bandTile);
            Assert.Equal(worldX,
                placement.ToLotView(new Vector3(bandTile, 0f, next.WorldY + 30)).X, 3);

            //and the same for the tarmac the sub-world stamps on its own tiles 1..3
            for (var i = 1; i <= SimitoneLotViewRoad.TarmacTiles; i++)
            {
                Assert.True(band.IsTarmac(next.WorldX + i, next.WorldY + 30),
                    "the band has no tarmac on the neighbour's local tile " + i);
                Assert.Equal((delta.X + i) * Scale,
                    placement.ToLotView(new Vector3(next.WorldX + i, 0f, next.WorldY + 30)).X, 3);
            }
        }

        [Fact]
        public void TheBandReachesTilesTheGeneratedMapsOwnRoadDataDoesNot()
        {
            // The parallel grammar is the bug. The road graph's band is in the gap; the lot view's is
            // in the lots, at fixed offsets from their own edges. So the band has to put tarmac on
            // the player's own outermost tile and on the neighbour's own first tile - tiles the map
            // carries no road byte on at all - which is the whole of the disagreement stated as two
            // assertions.
            NeighbourhoodLot here, next;
            var map = PackedStreet(out here, out next);
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, here, next);

            var z = here.WorldY + 32;
            var hereMax = here.WorldX + here.Size - 1;
            Assert.Equal(RoadMask.None, map.RoadMaskAt(map.Index(hereMax, z)));
            Assert.Equal(RoadMask.None, map.RoadMaskAt(map.Index(next.WorldX, z)));
            Assert.True(band.IsTarmac(hereMax, z),
                "the lot view's street starts at the lot's own outermost tile, not in the gap");
            Assert.True(band.IsTarmac(hereMax + 1, z), "and it runs through the gap");
            Assert.True(band.IsTarmac(hereMax + 2, z));
            Assert.True(band.IsTarmac(next.WorldX, z));
        }

        // ---- the height -------------------------------------------------------------------------

        [Fact]
        public void TheSurroundAndAFlatSubWorldStandOnOneGroundPlane()
        {
            // BaseAltitudeFor hangs the sub-world at activeBase - neighbourBase, and the surround is
            // measured down to this lot's own centre-four mean, so on a flat neighbourhood the two
            // grounds meet at the same height and the join has no step in it at all.
            NeighbourhoodLot here, next;
            var map = PackedStreet(out here, out next);

            var activeTerrain = TerrainOf(map, here);
            var neighbourTerrain = TerrainOf(map, next);
            var baseAlt = SimitoneSurroundingsRestore.BaseAltitudeFor(
                activeTerrain.BaseLevel, neighbourTerrain.BaseLevel);
            Assert.Equal(0, baseAlt);

            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var drawn = placement.ToLotView(new Vector3(next.WorldX,
                NeighbourhoodCityMeshBuilder.WorldHeightFor(
                    map.ElevationData[map.Index(next.WorldX, next.WorldY)]),
                next.WorldY)).Y;

            //the sub-world's own ground: (altitude - BaseAlt) * TerrainFactor * 3, with the helpers
            //leaving a lot's own footprint at altitude ~= 0
            var altitude = neighbourTerrain.HeightAt(1, 1) * 100 - neighbourTerrain.BaseLevel;
            var subWorld = (altitude - baseAlt) * SimitoneNeighbourTerrain.LotTerrainFactor * 3f;

            Assert.True(Math.Abs(drawn - subWorld) < 1e-3f,
                "the surround draws the neighbour's border at " + drawn
                + " units and its sub-world draws itself at " + subWorld);
        }

        [Fact]
        public void TheSurroundIsMeasuredToThePlaneTheSubWorldsAreHungFrom()
        {
            // A player's lot on a slope, with flat neighbours. The surround used to be measured to
            // the lowest elevation on the property's border ring, which on this map is a third of a
            // tile below the centre - so the whole neighbourhood stood a third of a tile above every
            // sub-world standing in it, and the ring test below is what says so.
            var map = FlatMap(48);
            var here = Lot(68, 200, 300);
            for (int i = 0; i < here.Size; i++)
            {
                map.ElevationData[map.Index(here.WorldX + i, here.WorldY)] = 44;
                map.ElevationData[map.Index(here.WorldX + i, here.WorldY + here.Size - 1)] = 44;
            }

            var placement = SimitoneSurroundingsPlacement.For(map, here);

            //The plane is the mean of the four centre samples in the *map's own* elevation bytes -
            //the scale ToLotView subtracts it in, and the only one the mesh's vertices are in.
            //The four samples are interior tiles, which this map holds at the flat 48 the fixture
            //was built from, so 48 over ElevationScale is the mean. GetBaseLevel's own number is a
            //different one (2 height bytes, 3.75 tiles): that is the helpers' *quantised* height
            //field, and taking it as the plane would stand the surround a quarter of a tile above
            //every sub-world in a flat neighbourhood - which is the opposite of what this is for.
            //TheSurroundAndAFlatSubWorldStandOnOneGroundPlane is the test that says so.
            const float centreMean = 48f;
            Assert.Equal(centreMean / NeighbourhoodCityMeshBuilder.ElevationScale,
                placement.MapTileOrigin.Y, 3);
            Assert.Equal(4f, placement.MapTileOrigin.Y);

            //and the four samples that mean is made of are the four GetBaseLevel averages: every one
            //of them is off an interior tile of the flat 48, not off the graded border ring
            var centre = TerrainOf(map, here);
            for (int j = 1; j <= 2; j++)
            {
                for (int i = 1; i <= 2; i++)
                {
                    Assert.Equal(SimitoneNeighbourTerrain.HeightByteFor(48), centre.HeightAt(i, j));
                }
            }

            //and the lowest ring tile, which is what it used to be measured to
            Assert.True(placement.MapTileOrigin.Y > 44f / NeighbourhoodCityMeshBuilder.ElevationScale,
                "the surround should no longer be measured down to the border ring's lowest tile");
        }

        // ---- the geometry that gets uploaded -----------------------------------------------------

        [Fact]
        public void TheCropDrawsBothGrammarsAndTheMapOwnIsNeverDropped()
        {
            // Part 8 §8.3, and the "green mini-street" of the bug report. This used to be an
            // either/or - the band OR the map's road quads - and the either was the band, which is a
            // function of a lot's four edges and has no driveways in it at all. So the space between
            // two lots was a strip of lot-view tarmac with the map's real connectors missing and
            // the grass texture showing through where they were. Both are uploaded now, and which
            // is which has to be visible from the result rather than from a comment.
            NeighbourhoodLot here, next;
            var map = PackedStreet(out here, out next);
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var band = BandFor(map, cull, here, next);

            var withBand = SimitoneSurroundingsCrop.Crop(mesh, cull, band);
            var withoutBand = SimitoneSurroundingsCrop.Crop(mesh, cull);

            Assert.Equal(band.RoadTileCount, withBand.BandRoadTiles);
            Assert.Contains("RestoreRoad", withBand.RoadSource);
            Assert.Equal(0, withoutBand.BandRoadTiles);
            Assert.Contains("generated map", withoutBand.RoadSource);
            Assert.True(withoutBand.RoadPrimitives > 0, "the map's own roads are the fallback");

            //and the map's own connectors survive the band, which is the whole point: the band's
            //tile count is its own and the map's triangles are counted separately, so a band that
            //covered the region would show up as MapRoadPrimitives of zero
            Assert.Equal(withoutBand.RoadPrimitives, withBand.MapRoadPrimitives);
            Assert.True(withBand.MapRoadPrimitives > 0,
                "the map's own connectors are still drawn when there is a band");
            Assert.True(withBand.BandRoadPrimitives > 0, "and so is the band's corridor fill");
            Assert.Equal(withBand.BandRoadPrimitives + withBand.MapRoadPrimitives,
                withBand.RoadPrimitives);

            //every band road vertex is on a whole map tile, which is what puts it on the same grid
            //as the sub-worlds' own geometry
            for (int i = 0; i < withBand.RoadVertices.Length; i++)
            {
                var p = withBand.RoadVertices[i].Position;
                Assert.Equal(Math.Round(p.X), p.X, 4);
                Assert.Equal(Math.Round(p.Z), p.Z, 4);
            }

            //Every road vertex is opaque. The road pass is drawn with BlendState.Opaque precisely
            //because of it, and a road quad with a non-zero transparency would be the one place a
            //green texel could still show through a street.
            for (int i = 0; i < withBand.RoadVertices.Length; i++)
            {
                Assert.Equal(0f, withBand.RoadVertices[i].Transparency);
            }
        }

        [Fact]
        public void TheCorridorBetweenTwoLotsIsStreetAndNotGrass()
        {
            // The half of the green-mini-street that is the band's to own. Between two lots the
            // reserved corridor is covered by the band's fill and by each lot's own outermost tile -
            // no gap and no second band - so there is no tile of the street that nothing draws, and
            // therefore no tile where the ground texture shows through.
            NeighbourhoodLot here, next;
            var map = PackedStreet(out here, out next);
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);
            var band = BandFor(map, cull, here, next);

            //the whole corridor, from the low lot's outermost tile to the facing lot's first tile
            for (var x = here.WorldX + here.Size - 1; x <= next.WorldX; x++)
            {
                for (var z = here.WorldY; z < here.WorldY + here.Size; z += 7)
                {
                    Assert.True(band.IsRoad(x, z),
                        "map tile (" + x + "," + z + ") is in the corridor between lots 68 and 69"
                        + " and nothing draws it, so the ground texture shows through the street");
                }
            }

            //and the band reaches the property line on both sides, which is where the two halves of
            //the street meet a sub-world's own band
            Assert.True(band.IsRoad(here.WorldX + here.Size - 1, here.WorldY + 30));
            Assert.True(band.IsRoad(next.WorldX, here.WorldY + 30));
        }

        [Fact]
        public void ARegionWithNoLotsStillFallsBackToTheMapsOwnRoads()
        {
            // The band is a function of a layout. A surround built with none keeps the street it
            // had, rather than drawing a neighbourhood with no roads in it.
            var map = FlatMap(48);
            var here = Lot(68, 200, 300);
            MarkVerticalStreet(map, 210, here.WorldY, here.WorldY + 40);
            var cull = SimitoneSurroundingsCull.For(
                map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);

            var none = SimitoneSurroundingsCrop.Crop(
                NeighbourhoodCityMeshBuilder.Build(map), cull, null);
            Assert.True(none.RoadPrimitives > 0);
            Assert.Equal(0, none.BandRoadTiles);
            Assert.Equal(0, none.BandRoadPrimitives);
            Assert.Equal(none.RoadPrimitives, none.MapRoadPrimitives);
        }

        private static SimitoneNeighbourTerrain TerrainOf(
            NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            SimitoneNeighbourTerrain terrain;
            string reason;
            Assert.True(SimitoneNeighbourTerrain.TryBuild(map, lot, out terrain, out reason), reason);
            return terrain;
        }
    }
}
