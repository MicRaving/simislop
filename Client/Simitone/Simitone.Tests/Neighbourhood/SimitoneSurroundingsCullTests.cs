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
    /// How much of the generated neighbourhood is drawn around the lot the player is standing in,
    /// and the ground join that puts the player's own property edge on the generated road. M4.
    ///
    /// <para>
    /// A player standing on one lot reported "graphical glitches beyond the property" with
    /// 275,902 terrain triangles in the log. That is the whole 512x512 generated world - about
    /// 138,000 quads and a million vertex shader invocations a frame - drawn around a 192x192 unit
    /// lot, with no distance limit of any kind. These are the tests for the decisions that replaced
    /// it: a region of the map that is drawn, the player's own property drawn under the lot with a
    /// bank along its border ring, and a fog that finishes before the edge of the region so the
    /// edge is never visible.
    /// </para>
    ///
    /// <para>
    /// All of it is arithmetic, which is the point. The numbers a player would otherwise have to
    /// go and check on screen are numbers a test can write down.
    /// </para>
    /// </summary>
    public class SimitoneSurroundingsCullTests
    {
        private const int MapSize = NeighbourhoodCityMeshBuilder.MapSize;

        private static NeighbourhoodLot Lot(short number, int worldX, int worldY, int size = 64)
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

        private static NeighbourhoodLayout LayoutOf(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(4, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion, MapSize, lots);
        }

        private static NeighbourhoodMap FlatMap(int size, byte elevation)
        {
            var map = NeighbourhoodMap.Create(size);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = elevation;
            return map;
        }

        // ---- the drawn region -------------------------------------------------------------------

        [Fact]
        public void TheDrawnRegionIsTheLotPlusAFixedMarginOnEverySide()
        {
            // The whole of the cull: the player's own rectangle, and a fixed number of map tiles
            // of neighbourhood past it on every side. Anything else is not drawn.
            var map = FlatMap(MapSize, 48);            var lot = Lot(68, 200, 300);
            var cull = SimitoneSurroundingsCull.For(map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);

            Assert.True(cull.IsValid);
            Assert.Equal(lot.WorldX - SimitoneSurroundingsPlacement.CullRadiusTiles, cull.MinX);
            Assert.Equal(lot.WorldY - SimitoneSurroundingsPlacement.CullRadiusTiles, cull.MinZ);
            Assert.Equal(lot.WorldX + lot.Size + SimitoneSurroundingsPlacement.CullRadiusTiles, cull.MaxX);
            Assert.Equal(lot.WorldY + lot.Size + SimitoneSurroundingsPlacement.CullRadiusTiles, cull.MaxZ);
            Assert.Equal(lot.Size + 2 * SimitoneSurroundingsPlacement.CullRadiusTiles, cull.WidthTiles);
            Assert.Equal(lot.Size + 2 * SimitoneSurroundingsPlacement.CullRadiusTiles, cull.HeightTiles);
        }

        [Fact]
        public void TheDrawnRegionIsFarSmallerThanTheGeneratedMap()
        {
            // The number in the bug report, restated as the number it should be. A 64-tile lot with
            // the shipped margin is 144x144 tiles: about five per cent of the map's area, and less
            // than that again once the isometric diamond has had its say.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);

            var share = cull.TileCount / (double)(MapSize * (long)MapSize);
            Assert.True(share < 0.09,
                "the drawn region covers " + share + " of the generated map, which is not a cull");
            Assert.True(cull.TileCount < (long)NeighbourhoodCityMeshProbe.AnalyticDiamond().Tiles / 4,
                "the drawn region should be a fraction of the diamond the mesh actually covers");
        }

        [Fact]
        public void TheDrawnRegionIsClippedToTheMapRatherThanRunningOffIt()
        {
            // A lot on the corner of the world - which is where the isometric diamond's own apex
            // lots end up - cannot have forty tiles of neighbourhood past it in two directions.
            var map = FlatMap(MapSize, 48);
            var lot = Lot(68, 0, 0);
            var cull = SimitoneSurroundingsCull.For(map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);

            Assert.Equal(0, cull.MinX);
            Assert.Equal(0, cull.MinZ);
            //the far corner of a 64-tile lot at the map origin is exclusive, so 64
            Assert.Equal(lot.Size, cull.LotMaxX);
            Assert.Equal(lot.Size, cull.LotMaxZ);
            Assert.Equal(lot.Size + SimitoneSurroundingsPlacement.CullRadiusTiles, cull.MaxX);
            Assert.Equal(lot.Size + SimitoneSurroundingsPlacement.CullRadiusTiles, cull.MaxZ);
            Assert.True(cull.MaxX <= MapSize);
            Assert.True(cull.MaxZ <= MapSize);
        }

        [Fact]
        public void TheDrawnRegionAlwaysCoversTheWholePropertyEvenAtTheMapEdge()
        {
            // The one thing the surround must never do is stop short of the player's own ground:
            // a region smaller than the property would leave a gap between the lot view's ground
            // and the neighbourhood's, which reads as a hole at the property line.
            foreach (var position in new[]
            {
                new Point(0, 0), new Point(MapSize - 64, MapSize - 64),
                new Point(-10, 5), new Point(MapSize + 40, 12)
            })
            {
                var lot = Lot(68, position.X, position.Y, 64);
                var cull = SimitoneSurroundingsCull.For(
                    FlatMap(MapSize, 48), lot, SimitoneSurroundingsPlacement.CullRadiusTiles);

                Assert.True(cull.IsValid, position + " should have a region");
                //every in-map corner of the property has to be inside the drawn region
                Assert.True(cull.ContainsTile(cull.LotMinX + 0.5f, cull.LotMinZ + 0.5f), position.ToString());
                Assert.True(cull.ContainsTile(cull.LotMaxX - 0.5f, cull.LotMinZ + 0.5f), position.ToString());
                Assert.True(cull.ContainsTile(cull.LotMinX + 0.5f, cull.LotMaxZ - 0.5f), position.ToString());
                Assert.True(cull.ContainsTile(cull.LotMaxX - 0.5f, cull.LotMaxZ - 0.5f), position.ToString());
                Assert.True(cull.InsideActiveLot(cull.LotMinX + 0.5f, cull.LotMinZ + 0.5f), position.ToString());
            }
        }

        [Fact]
        public void NothingOutsideTheDrawnRegionIsKept()
        {
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);

            //the region is half-open, so the tile that straddles each of its far edges is kept and
            //the next one out is not
            Assert.False(cull.ContainsTile(cull.MinX - 0.5f, 300f));
            Assert.False(cull.ContainsTile(cull.MaxX + 0.5f, 300f));
            Assert.False(cull.ContainsTile(300f, cull.MinZ - 0.5f));
            Assert.False(cull.ContainsTile(300f, cull.MaxZ + 0.5f));

            Assert.True(cull.ContainsTile(cull.MinX + 0.5f, 300f));
            Assert.True(cull.ContainsTile(cull.MaxX - 0.5f, 300f));
            Assert.True(cull.ContainsTile(300f, cull.MinZ + 0.5f));
            Assert.True(cull.ContainsTile(300f, cull.MaxZ - 0.5f));
        }

        [Fact]
        public void ThePlayersOwnPropertyIsDrawnUnderTheLot()
        {
            // The cut that used to be here is the reason the join looked wrong. The lot view draws
            // its own ground after the surround and clears the depth buffer in between, so the
            // surround's copy of the property is invisible - and cutting it left the neighbourhood's
            // ground stopping dead at the property line, which reads as a step all the way round the
            // lot wherever the two are not at the same height. It is drawn now.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);

            Assert.True(cull.ContainsTile(232.5f, 332.5f));
            Assert.True(cull.InsideActiveLot(232.5f, 332.5f));
            Assert.True(cull.KeepsTile(232.5f, 332.5f));

            //and the ring of ground immediately outside the property is, which is the join
            Assert.True(cull.KeepsTile(199.5f, 332.5f));
            Assert.True(cull.KeepsTile(264.5f, 332.5f));
            Assert.True(cull.KeepsTile(232.5f, 299.5f));
            Assert.True(cull.KeepsTile(232.5f, 364.5f));
        }

        [Fact]
        public void WithNoMapOrNoLotNothingIsDrawnAtAll()
        {
            // The refusal has to be "nothing", not "everything": a default region that kept
            // everything would be the bug this cull exists to fix, wearing a different hat.
            foreach (var cull in new[]
            {
                SimitoneSurroundingsCull.For(null, Lot(68, 200, 300), 40),
                SimitoneSurroundingsCull.For(FlatMap(MapSize, 48), null, 40),
                default(SimitoneSurroundingsCull)
            })
            {
                Assert.False(cull.IsValid);
                Assert.Equal(0, cull.TileCount);
                Assert.False(cull.KeepsTile(1f, 1f));
                Assert.Contains("nothing is drawn", cull.Describe());
            }
        }

        [Fact]
        public void TheRegionSaysWhereItIsInOneLine()
        {
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);
            var text = cull.Describe();

            Assert.Contains("x[160,304)", text);
            Assert.Contains("z[260,404)", text);
            Assert.Contains("40 tiles past the property", text);
            Assert.Contains("is drawn under the lot", text);
            Assert.Contains("fog completes at", text);
        }

        [Fact]
        public void TheBankGoesOnThePropertyLineAndNowhereElse()
        {
            // The bank is the strip of ground that ties the neighbourhood's ground to the lot's own,
            // so it belongs on the property's border ring and nowhere else: a tile further in would
            // be standing in the player's own lawn, and a tile further out is ground that already
            // meets the ground either side of it.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300, 64), SimitoneSurroundingsPlacement.CullRadiusTiles);

            //the four corners, which are the only tiles with two sides on the line
            Assert.True(cull.IsLotBorderTile(200.5f, 300.5f));
            Assert.True(cull.IsLotBorderTile(263.5f, 363.5f));
            Assert.True(cull.IsLotBorderTile(200.5f, 363.5f));
            Assert.True(cull.IsLotBorderTile(263.5f, 300.5f));
            //the middle of the property is not the ring
            Assert.False(cull.IsLotBorderTile(232.5f, 332.5f));
            //and neither is the ground just outside it
            Assert.False(cull.IsLotBorderTile(199.5f, 332.5f));
            Assert.False(cull.IsLotBorderTile(264.5f, 332.5f));
            Assert.False(cull.IsLotBorderTile(232.5f, 299.5f));
            Assert.False(cull.IsLotBorderTile(232.5f, 364.5f));
        }

        [Fact]
        public void ThePropertyIsDrawnWithTheLotsOwnTerrain()
        {
            // "no proper floor textures": the ground the lot's own edge meets is the lot's own ground
            // type rather than whatever the neighbourhood happened to put there. The generated map
            // already stamps it across the lot's footprint, and the region records which layer that
            // is so the claim is a test rather than a comment.
            var lot = Lot(68, 200, 300, 64);
            lot.TerrainType = TerrainType.SAND;
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), lot, SimitoneSurroundingsPlacement.CullRadiusTiles);

            Assert.Equal(NeighbourhoodTerrain.Sand, cull.LotTerrainIndex);
            //and a region with no lot in it has no terrain to name, rather than a default that
            //would read as grass
            Assert.Equal(-1, default(SimitoneSurroundingsCull).LotTerrainIndex);
            Assert.Equal(-1, SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), null, 40).LotTerrainIndex);
        }

        // ---- the facade queue ------------------------------------------------------------------

        [Fact]
        public void OnlyTheLotsTheRegionCoversAreHandedToTheFacadeQueue()
        {
            // "38 still building" is a queue of every lot in the neighbourhood, each one a World
            // and a VM stood up, for houses eight lot widths away. The cache is handed a layout
            // holding only what is in view, so the queue is the two or three houses that are.
            var map = FlatMap(MapSize, 48);
            var lots = new List<NeighbourhoodLot> { Lot(68, 200, 300) };
            for (int i = 1; i < 38; i++) lots.Add(Lot((short)(68 + i), 200 + (i % 8) * 66, 300 + (i / 8) * 66));

            var cull = SimitoneSurroundingsCull.For(map, lots[0], SimitoneSurroundingsPlacement.CullRadiusTiles);
            var inRange = cull.LotsInRange(LayoutOf(lots.ToArray()));

            Assert.True(inRange.Count > 0, "at least the player's own lot is in its own region");
            Assert.True(inRange.Count < lots.Count,
                inRange.Count + " of " + lots.Count + " lots are close enough to be worth a facade");
            Assert.Contains(lots[0], inRange);
        }

        [Fact]
        public void TheImmediateNeighboursAreInRangeSoTheirHousesCanBeSeen()
        {
            // The neighbours either side are the whole point of the surround, and they are only
            // about two tiles past the property line, so the shipped margin has to reach them: a
            // facade standing on ground that was never drawn is worse than no facade.
            var map = FlatMap(MapSize, 48);
            var here = Lot(68, 200, 300);
            var cull = SimitoneSurroundingsCull.For(map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);

            for (int i = -1; i <= 1; i++)
            {
                for (int j = -1; j <= 1; j++)
                {
                    var neighbour = Lot((short)(100 + i * 3 + j), 200 + i * 66, 300 + j * 66);
                    Assert.True(cull.CoversLot(neighbour),
                        "the neighbour at " + neighbour + " should be in range of " + cull.Describe());
                }
            }
            //the lot across the street from the far neighbour is not, and should not be
            Assert.False(cull.CoversLot(Lot(200, 200 - 3 * 66, 300 - 3 * 66)));
        }

        // ---- one connected map, at the right places ------------------------------------------------

        [Fact]
        public void TheConnectedMapDrawsEveryRoadsOwnConnectorAndTheBandIsAddedNotSubstituted()
        {
            // §8.3 and the "green mini-street" of the bug report, over the region the lot view
            // actually draws with. The two grammars are one question asked twice - the mask read off
            // the 2D neighbourhood map is in the mesh's own road quads, and the lot view's
            // RestoreRoad band is the seam between two lots' sub-worlds - and the crop must upload
            // both. It was an either/or for a while and the either was the band, which has no
            // driveway in it at all: the space between two non-adjacent lots was a strip of tarmac
            // with the map's real connectors missing and the grass texture showing through.
            //
            // The region is the whole map here, so this also says the wide drawing costs nothing on
            // the road side: no road tile the mesh emitted is dropped, and the band adds to the count
            // rather than replacing it.
            var map = NeighbourhoodMap.Create(MapSize);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;
            var here = Lot(68, 200, 300);
            var next = Lot(69, here.WorldX + here.Size + SimitoneLotViewRoad.StreetCorridorTiles,
                here.WorldY);
            for (var j = 1; j <= RoadGraph.MainStreetWidth; j++)
            {
                for (var z = here.WorldY; z <= here.WorldY + here.Size - 1; z++)
                {
                    map.RoadData[map.Index(here.WorldX + here.Size + j, z)] =
                        RoadMask.North | RoadMask.South;
                }
            }

            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var drawn = placement.DrawnRegion;
            Assert.True(drawn.IsConnectedMap);

            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var withoutBand = SimitoneSurroundingsCrop.Crop(mesh, drawn);
            var region = new Rectangle(drawn.MinX, drawn.MinZ, drawn.WidthTiles, drawn.HeightTiles);
            var band = SimitoneLotViewRoadBand.For(map, new[] { here, next }, region);
            var withBand = SimitoneSurroundingsCrop.Crop(mesh, drawn, band);

            //the map's own connectors survive whole, and they are the majority of the street
            Assert.True(withoutBand.MapRoadPrimitives > 0,
                "the mesh emitted no road quads at all, so there is nothing for the band to sit on");
            Assert.Equal(withoutBand.MapRoadPrimitives, withBand.MapRoadPrimitives);
            Assert.Equal(withoutBand.RoadPrimitives, withBand.MapRoadPrimitives);

            //and the band is added on top rather than instead of them
            Assert.True(band.RoadTileCount > 0);
            Assert.True(withBand.BandRoadPrimitives > 0);
            Assert.Equal(withBand.MapRoadPrimitives + withBand.BandRoadPrimitives,
                withBand.RoadPrimitives);
            Assert.Equal(band.RoadTileCount, withBand.BandRoadTiles);
            Assert.Contains("RestoreRoad", withBand.RoadSource);
            Assert.Contains("generated map", withBand.RoadSource);

            //Every road quad is opaque and on a whole map tile. Opaque is why the road pass is drawn
            //without blending, and a whole tile is what puts the band on the same lattice the
            //sub-worlds' own geometry is on - a road quad off the lattice is a road that does not
            //meet the lot's own pavement.
            for (int i = 0; i < withBand.RoadVertices.Length; i++)
            {
                var v = withBand.RoadVertices[i].Position;
                Assert.Equal(0f, withBand.RoadVertices[i].Transparency);
                Assert.Equal(Math.Round(v.X), v.X, 4);
                Assert.Equal(Math.Round(v.Z), v.Z, 4);
            }
        }

        [Fact]
        public void AHouseAndTheGroundUnderItAreAtTheSameHeightOnTheSharedMap()
        {
            // §8.6: "the surrounding lots' ground matches the active lot's plane". The chain is three
            // numbers and they have to be one: the mesh's own vertex height at a lot's base tile,
            // the height a neighbouring facade is hung at, and the height the surround's translation
            // puts that ground at in the lot view. The first is read out of the built mesh rather
            // than out of the elevation byte, so this is a check that the three paths agree rather
            // than a restatement of one of them.
            var map = NeighbourhoodMap.Create(MapSize);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;
            var here = Lot(68, 200, 300);
            var next = Lot(69, here.WorldX + here.Size + SimitoneLotViewRoad.StreetCorridorTiles,
                here.WorldY);
            //a hill, so "the same height" is a claim and not a tautology
            for (int z = 0; z < MapSize; z++)
            {
                for (int x = 0; x < MapSize; x++)
                {
                    map.ElevationData[map.Index(x, z)] = (byte)(40 + (x + z) % 17);
                }
            }

            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var crop = SimitoneSurroundingsCrop.Crop(
                NeighbourhoodCityMeshBuilder.Build(map), placement.DrawnRegion);

            foreach (var neighbour in new[] { here, next })
            {
                //where the mesh actually put the ground at the lot's base tile
                var drawn = DrawnGroundAt(crop, neighbour.WorldX, neighbour.WorldY);
                Assert.True(drawn.HasValue,
                    "lot " + neighbour.Number + " has no drawn ground at its base tile ("
                    + neighbour.WorldX + ", " + neighbour.WorldY + ")");
                //where a facade for it is hung
                var facade = SimitoneSurroundingsPlacement.FacadeGroundInLotView(
                    placement, map, neighbour);
                //and where that same ground lands in the lot view
                var ground = drawn.Value;
                var surround = placement.ToLotView(new Vector3(
                    neighbour.WorldX, ground, neighbour.WorldY)).Y;
                var tolerance = SimitoneSurroundingsRestore.SubWorldGroundTolerance
                    * SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
                Assert.True(Math.Abs(facade - surround) < tolerance,
                    "lot " + neighbour.Number + " is hung at " + facade + " units and its ground is"
                    + " drawn at " + surround + ", so it is " + Math.Abs(facade - surround)
                    + " units out - on a plinth, or in a hole");
            }
        }

        /// <summary>
        /// The y of a drawn ground vertex at a map lattice point, or null when no kept ground vertex
        /// is there. The mesh's per-tile emitter writes its four corners at the lattice, so a corner
        /// is exactly one elevation byte and is comparable with a placement height directly.
        /// </summary>
        private static float? DrawnGroundAt(SimitoneSurroundingsCropResult crop, int x, int z)
        {
            for (int layer = 0; layer < crop.LayerVertices.Length; layer++)
            {
                var vertices = crop.LayerVertices[layer];
                if (vertices == null) continue;
                for (int i = 0; i < vertices.Length; i++)
                {
                    var p = vertices[i].Position;
                    if (Math.Abs(p.X - x) > 1e-4f || Math.Abs(p.Z - z) > 1e-4f) continue;
                    return p.Y;
                }
            }
            return null;
        }

        [Fact]
        public void ALotAcrossTheMapIsNotInRange()
        {
            // The player's lot is placed where the mesh draws ground - the isometric diamond the
            // city geometry is clipped to - because a lot outside the clip is refused rather than
            // drawn (see CoversLot and TheOnlyLotsWithoutDrawnGroundAreInTheCutCorner). This used to
            // use the map's top-left corner, which at a small row is outside the clip, so the "in
            // range" half of the assertion was passing for the wrong reason or failing for a reason
            // the test did not own.
            var map = FlatMap(MapSize, 48);
            var here = Lot(68, 200, 300);
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(here.WorldX, here.WorldY));
            var cull = SimitoneSurroundingsCull.For(map, here, SimitoneSurroundingsPlacement.CullRadiusTiles);

            Assert.False(cull.CoversLot(Lot(21, 400, 400)));
            Assert.False(cull.CoversLot(null));
            Assert.True(cull.CoversLot(Lot(21, 264, 300)));
        }

        [Fact]
        public void TheDrawnRegionIsTheWholeConnectedMapAndTheNeighbourOneIsNot()
        {
            // Part 8 §8.1: one connected map, not two views. The lot view's surroundings are the map
            // the neighbourhood view draws - the same raster, the same mesh, every tile of it - and
            // the forty-tile square that used to be it is now only the neighbour budget: which lots
            // are close enough to be stood up as real sub-worlds or built as facades. Nothing about
            // the two is a second generation; the difference is which question each rectangle answers.
            var map = FlatMap(MapSize, 48);

            //the region itself, asked of a lot in the middle of the island and of one in its
            //north-west corner, so a region that quietly depended on where the player stands
            //would fail
            foreach (var here in new[] { Lot(68, 200, 300), Lot(68, 0, 0), Lot(68, 400, 460) })
            {
                var placement = SimitoneSurroundingsPlacement.For(map, here);
                Assert.True(placement.IsValid);

                var drawn = placement.DrawnRegion;
                Assert.True(drawn.IsValid);
                Assert.True(drawn.IsConnectedMap, drawn.Describe());
                Assert.Equal(0, drawn.MinX);
                Assert.Equal(0, drawn.MinZ);
                Assert.Equal(MapSize, drawn.MaxX);
                Assert.Equal(MapSize, drawn.MaxZ);
                Assert.Equal((long)MapSize * MapSize, drawn.TileCount);

                //and the neighbour budget is unchanged, and still the smaller of the two
                var neighbours = placement.Cull;
                Assert.True(neighbours.IsValid);
                Assert.False(neighbours.IsConnectedMap, "the neighbour region should not be the"
                    + " whole map: a real sub-world costs a World and a VM and a facade costs seconds");
                Assert.Equal(SimitoneSurroundingsPlacement.CullRadiusTiles, neighbours.RadiusTiles);
                Assert.Equal(SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles, neighbours.RadiusTiles);
                Assert.True(neighbours.RadiusTiles <= SimitoneSurroundingsCull.ConnectedRadiusFor(map),
                    "the neighbour radius must fit inside the region the ground is drawn for");
            }

            //So the crop keeps the whole map: every tile the mesh emitted, and nothing beyond it.
            //One real mesh, because a 512x512 mesh is a few megabytes of vertices and three of them
            //in one test is a few more than this file wants to hold.
            var middle = SimitoneSurroundingsPlacement.For(map, Lot(68, 200, 300));
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var crop = SimitoneSurroundingsCrop.Crop(mesh, middle.DrawnRegion);
            Assert.Equal(0, crop.OutsideRegion);
            Assert.Equal(mesh.TotalPrimitives, crop.KeptFromSource);

            //and the fog floor follows the drawn region, so it is the far corner of the island
            //rather than the corner of a crop - which is why there is no edge for it to hide
            Assert.True(middle.DrawnRegion.CornerDistanceUnits > middle.Cull.CornerDistanceUnits);
        }

        [Fact]
        public void ALotOnGroundTheMeshNeverDrawsIsNotInRange()
        {
            // Part 9 §9.1: real positions landing in the diamond's cut corners put a house where the
            // mesh emits neither ground nor roads, which reads as a house floating in a void. The
            // shelf packer keeps the shipped layouts inside the clip (audit O1); this is the guard
            // for anything that is not one of them, and it is in the one
            // predicate the facade queue, the render decision and the sub-world invariant all ask -
            // so a lot refused here is refused everywhere at once. The region asked of is the drawn
            // one, the whole map, which is the point: "on the map" and "on ground" are different
            // questions now that the drawn region is the whole map.
            var map = FlatMap(MapSize, 48);
            var here = Lot(68, 200, 300);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var drawn = placement.DrawnRegion;

            //inside the drawn region - it is the whole map - and in the border the mesh's clip
            //leaves out: the clip is the map inset by ClipInset (audit O1), so at x=5 there is no
            //ground
            var offGround = Lot(21, 5, 150);
            Assert.True(drawn.ContainsTile(offGround.WorldX + 0.5f, offGround.WorldY + 0.5f),
                "the fixture's lot should be inside the drawn region, or this test is not about the"
                + " ground rule");
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(
                offGround.WorldX, offGround.WorldY));
            Assert.False(SimitoneSurroundingsPlacement.GroundIsDrawnUnder(map, offGround));
            Assert.False(drawn.CoversLot(offGround));
            Assert.False(NeighbourhoodRenderDecision.InsideCut(offGround, drawn));
            Assert.False(NeighbourhoodRenderDecision.IsFacade(
                NeighbourhoodRenderDecision.Decide(offGround, here.Number, null, drawn)),
                "a lot with no ground under it is not a facade either - it is nothing, and the log"
                + " names it");

            //and it is measured rather than hidden, so a report can say how much of it overhangs
            var overhang = SimitoneSurroundingsPlacement.GroundlessTilesOf(map, offGround);
            Assert.True(overhang > 0, "the fixture's lot should overhang the clip");
            Assert.True(overhang < offGround.Size * offGround.Size,
                "a refused lot with its whole footprint off the clip is not a plot overhanging an"
                + " edge; it is somewhere else entirely, and the number should say so");

            //the same lot one tile inside the clip is a normal neighbour again - the clip's left
            //edge is DiamondLeft = ClipInset - 1, and the predicate is strictly inside it
            var edge = NeighbourhoodCityMeshBuilder.DiamondLeft(150);
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(edge, 150));
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(edge + 1, 150));
            Assert.True(SimitoneSurroundingsPlacement.GroundIsDrawnUnder(map, Lot(21, edge + 1, 150)));

            //a map the builder does not mesh is not asked about the diamond at all, because
            //CanMesh refuses it and the surround refuses it too
            var small = NeighbourhoodMap.Create(64);
            Assert.False(SimitoneSurroundingsPlacement.IsMeshed(small));
            Assert.True(SimitoneSurroundingsPlacement.GroundIsDrawnUnder(small, Lot(21, 4, 4)));
            Assert.Equal(0, SimitoneSurroundingsPlacement.GroundlessTilesOf(small, Lot(21, 4, 4)));
        }

        [Fact]
        public void TheFacadeQueueForAWholeNeighbourhoodIsSmall()
        {
            // The real shape of it, on the shape the packer produces: a full grid of 64-tile lots
            // with a two-tile sidewalk between them, eight across and eight down.
            var lots = new List<NeighbourhoodLot>();
            for (int row = 0; row < 8; row++)
            {
                for (int column = 0; column < 8; column++)
                {
                    lots.Add(Lot((short)(1 + row * 8 + column), 10 + column * 66, 10 + row * 66));
                }
            }
            var layout = LayoutOf(lots.ToArray());
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), lots[28], SimitoneSurroundingsPlacement.CullRadiusTiles);

            var inRange = cull.LotsInRange(layout);
            Assert.True(inRange.Count >= 4, "the neighbours either side should be in view");
            Assert.True(inRange.Count <= 20,
                inRange.Count + " of 64 lots are in range, which is still a queue of houses nobody can see");
        }

        // ---- the fog ----------------------------------------------------------------------------

        [Theory]
        [InlineData(16)]
        [InlineData(32)]
        [InlineData(64)]
        public void TheFogAlwaysFinishesBeforeTheDrawnRegionEnds(int lotSize)
        {
            // Otherwise the cut at the region's edge is visible wherever the fog is not already
            // finished - which is the "graphical glitches beyond the property" all over again.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300, lotSize), SimitoneSurroundingsPlacement.CullRadiusTiles);

            foreach (var lotFog in new[] { 0f, -1f, 50f, cull.FogDistance * 0.5f, cull.FogDistance })
            {
                var distance = SimitoneSurroundings.FogDistanceFor(lotFog, cull);
                Assert.True(distance >= cull.CornerDistanceUnits,
                    "lot fog " + lotFog + " gave " + distance + " units for a region whose corner is "
                    + cull.CornerDistanceUnits + " units away");
            }
        }

        [Fact]
        public void TheLotWeatherCanSeeFurtherButNotPastTheNeighbourhood()
        {
            // Clear weather may see further than the default - that is what clear weather means -
            // but the map stops at the end of the drawn region, so a weather value of several
            // hundred units cannot bring the far side of a 512-tile map back into view.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);

            var clear = SimitoneSurroundings.FogDistanceFor(cull.FogDistance * 1.4f, cull);
            Assert.Equal(cull.FogDistance * 1.4f, clear, 3);

            var endless = SimitoneSurroundings.FogDistanceFor(900f, cull);
            Assert.Equal(cull.FogDistance * SimitoneSurroundings.MaxFogStretch, endless, 3);
            Assert.True(endless < 900f, "the lot's own weather fog was 900 units");
        }

        [Fact]
        public void ALotWithNoWeatherYetGetsAUsableFogDistance()
        {
            // A lot that has just come up has a weather whose FogColor.W is zero, and a zero fog
            // distance puts every vertex on the near plane.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);
            var distance = SimitoneSurroundings.FogDistanceFor(0f, cull);

            Assert.True(distance > 1f);
            Assert.True(distance >= cull.CornerDistanceUnits);
            Assert.True(SimitoneSurroundings.FogDistanceFor(float.NaN, cull) > 1f);
            Assert.True(SimitoneSurroundings.FogDistanceFor(1f, default(SimitoneSurroundingsCull))
                >= SimitoneSurroundings.DefaultFogDistance * 0.999f);
        }

        // ---- the ground join --------------------------------------------------------------------

        [Fact]
        public void TheSurroundIsMeasuredToTheSubWorldsOwnBaseLevel()
        {
            // The old answers were one tile at the rectangle's minimum corner and then the lowest
            // elevation on its border ring. Both made the offset independent of which tile happened
            // to sit at the origin, and neither was a height anything else in the pipeline is
            // measured from: the neighbours standing in the surround are hung from the active lot's
            // centre-four mean (GetBaseLevel), so that is the plane the ground has to lie on.
            var map = FlatMap(MapSize, 48);
            var lot = Lot(68, 200, 300);
            //a single step down in the middle of the far edge of the ring, which is row
            //WorldY + Size - 1: the ring's last row, and one row further out would not be on it
            map.ElevationData[map.Index(232, lot.WorldY + lot.Size - 1)] = 47;

            var placement = SimitoneSurroundingsPlacement.For(map, lot);

            //the four centre samples are interior tiles, so the ring's odd tile does not move it
            Assert.Equal(48 / NeighbourhoodCityMeshBuilder.ElevationScale, placement.MapTileOrigin.Y, 5);
            Assert.Equal(NeighbourhoodFacadeTransform.GroundHeightFor(map, lot), placement.MapTileOrigin.Y, 5);
        }

        [Fact]
        public void TheGeneratedGroundAndTheBanksOwnPlaneAreTheSameHeight()
        {
            // The join. The bank along the property line starts at the height the whole surround was
            // measured down to, so the two can never be different numbers - a bank that started
            // anywhere else would either float or be buried under the ground it is holding up.
            var map = FlatMap(MapSize, 48);
            var lot = Lot(68, 200, 300);
            for (int i = 0; i < lot.Size; i++)
            {
                map.ElevationData[map.Index(200 + i, 300)] = 52;
                map.ElevationData[map.Index(200 + i, 364)] = 52;
                map.ElevationData[map.Index(200, 300 + i)] = 44;
                map.ElevationData[map.Index(264, 300 + i)] = 44;
            }

            var placement = SimitoneSurroundingsPlacement.For(map, lot);
            var cull = placement.Cull;
            Assert.Equal(placement.MapTileOrigin.Y, cull.GroundHeight, 5);

            //and the surround's ground is above the low side of the ring and below the high side,
            //because the centre-four mean is between them - which is what a mean is for
            var scale = NeighbourhoodCityMeshBuilder.ElevationScale;
            for (int i = 0; i < lot.Size; i++)
            {
                Assert.True(placement.ToLotView(new Vector3(200 + i, 52f / scale, 300)).Y > 0f,
                    "the high side of the ring stands above the plane");
                Assert.True(placement.ToLotView(new Vector3(200, 44f / scale, 300 + i)).Y < 0f,
                    "the low side of the ring sits below it");
            }
        }

        [Fact]
        public void AFlatNeighbourhoodStillMeetsThePropertyAtZero()
        {
            // The common case, and the one that must not change: with every tile the same height
            // the ring minimum is the corner tile and the surround is exactly where it was.
            var map = FlatMap(MapSize, 48);
            var lot = Lot(68, 200, 300);

            var placement = SimitoneSurroundingsPlacement.For(map, lot);

            Assert.Equal(NeighbourhoodFacadeTransform.GroundHeightFor(map, lot), placement.MapTileOrigin.Y, 5);
            Assert.Equal(Vector3.Zero, placement.ToLotView(placement.MapTileOrigin));
            Assert.True(placement.Cull.IsValid);
        }

        [Fact]
        public void ARaisedLotAndTheSurroundStandOnOneGroundPlane()
        {
            // S1, the defect the owner reported as "the height maps of surrounding lots are
            // incorrect": on a lot whose own ARRY(0) median is not zero, the surround is measured
            // down to that lot's base level while the lot view drew the lot's own ground at its
            // ABSOLUTE altitude, because Blueprint.BaseAlt was left at 0. The two frames have to be
            // one frame, and this is the whole of the claim: the surround's plane, restated in the
            // lot view's own altitude units, is the level the lot's own ground is drawn from.
            //
            // Three separate numbers, all derived, none of them restated:
            //   * the surround's plane, in world units, is MapTileOrigin.Y * LotViewUnitsPerTile;
            //   * the lot view draws an altitude A at (A - BaseAlt) * TerrainFactor * 3, so the
            //     altitude whose drawn height IS that plane is
            //     BaseAlt = plane / (TerrainFactor * 3) = GroundOriginFor / TerrainFactor;
            //   * and a TS1 lot's own ground is ARRY(0) * 10, so its base level is
            //     10 * median(ARRY(0)), which is what Blueprint.AnchorBaseAltitude writes.
            // The third is measured from a real Blueprint, not asserted as a formula.
            const int arryMedian = 24;                     // 24 ARRY(0) units is 4.5 tiles of height
            var map = FlatMap(MapSize, NeighbourhoodMapAdapter.QuantiseAltitude(arryMedian));
            var lot = Lot(68, 200, 300);
            lot.BaseHeight = arryMedian;

            var placement = SimitoneSurroundingsPlacement.For(map, lot);
            var baseAltitude = SimitoneSurroundingsPlacement.BaseAltitudeFor(map, lot);

            //the surround's plane, and the altitude the lot's own ground has to be measured from
            Assert.True(placement.MapTileOrigin.Y > 0f,
                "a raised lot's surround is measured down to a plane above the map's own zero");
            Assert.Equal(NeighbourhoodCityMeshBuilder.LotAltitudeUnitsPerArryUnit * arryMedian,
                baseAltitude, 1);

            //and the lot's own ground, built through a real Blueprint, is drawn on that plane. The
            //two numbers are both in lot-view world units, and the lot view's is
            //(Altitude - BaseAlt) * TerrainFactor * 3 while the surround's is ToLotView's answer -
            //which already subtracts MapTileOrigin.Y, so the lot's height goes in unshifted.
            var blueprint = new FSO.LotView.Model.Blueprint(lot.Size, lot.Size);
            blueprint.Altitude = new short[lot.Size * lot.Size];
            for (var i = 0; i < blueprint.Altitude.Length; i++)
            {
                blueprint.Altitude[i] = (short)(arryMedian
                    * NeighbourhoodCityMeshBuilder.LotAltitudeUnitsPerArryUnit);
            }
            blueprint.AnchorBaseAltitude();
            Assert.Equal(baseAltitude, blueprint.BaseAlt);

            var middleX = lot.WorldX + lot.Size / 2;
            var middleZ = lot.WorldY + lot.Size / 2;
            var middleAltitude = blueprint.Altitude[(lot.Size / 2) * lot.Size + lot.Size / 2];
            var ownGround = (middleAltitude - blueprint.BaseAlt)
                * blueprint.TerrainFactor * SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
            var surroundGround = placement.ToLotView(new Vector3(middleX,
                NeighbourhoodCityMeshBuilder.WorldHeightFor(map.ElevationData[map.Index(middleX, middleZ)]),
                middleZ)).Y;
            Assert.True(Math.Abs(ownGround - surroundGround) < 0.1f,
                "the lot's own ground is at " + ownGround + " units and the surround's at "
                + surroundGround + ", so there is a step of "
                + Math.Abs(ownGround - surroundGround) + " units at the middle of the lot");

            //No double subtraction: the old frame is the same lot with BaseAlt at zero, which drew
            //its ground this far above the surround instead - and this is not a rounding, it is the
            //lot's whole own altitude.
            var unanchored = (middleAltitude - 0)
                * blueprint.TerrainFactor * SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
            Assert.True(unanchored > 10f,
                "before the anchor the step was " + unanchored + " units, which is the lot's own"
                + " altitude rather than a rounding: this fixture only means something while that is"
                + " true");
            Assert.Equal(unanchored, ownGround + unanchored, 3);

            //Idempotent, and the surround's own plane is not moved by any of it.
            var before = placement.MapTileOrigin.Y;
            blueprint.AnchorBaseAltitude();
            Assert.Equal(baseAltitude, blueprint.BaseAlt);
            Assert.Equal(before, SimitoneSurroundingsPlacement.For(map, lot).MapTileOrigin.Y, 5);
        }

        [Fact]
        public void TheGroundPlaneAnchorsTheSameLevelAFlatLotIsAlreadyOn()
        {
            // The case that must not move. A lot at altitude zero anchors at zero, so the anchor is
            // a no-op there and every lot in a flat neighbourhood keeps the plane it had.
            var map = FlatMap(MapSize, 0);
            var lot = Lot(68, 200, 300);
            lot.BaseHeight = 0;

            var placement = SimitoneSurroundingsPlacement.For(map, lot);
            Assert.Equal(0f, placement.MapTileOrigin.Y, 5);
            Assert.Equal(0, SimitoneSurroundingsPlacement.BaseAltitudeFor(map, lot));

            var blueprint = new FSO.LotView.Model.Blueprint(lot.Size, lot.Size);
            blueprint.Altitude = new short[lot.Size * lot.Size];
            blueprint.AnchorBaseAltitude();
            Assert.Equal(0, blueprint.BaseAlt);
            Assert.Equal(0, SimitoneSurroundings.GroundPlaneAltitudeFor(blueprint, placement));

            //and with no field at all the surround's own plane is the answer, so a blueprint whose
            //altitude arrives from somewhere else is still put on the surface being drawn
            var bare = new FSO.LotView.Model.Blueprint(lot.Size, lot.Size);
            Assert.Equal(SimitoneSurroundingsPlacement.BaseAltitudeFor(map, lot),
                SimitoneSurroundings.GroundPlaneAltitudeFor(bare, placement));
            Assert.Equal(int.MinValue,
                SimitoneSurroundings.GroundPlaneAltitudeFor(null, default(SimitoneSurroundingsPlacement)));
        }

        [Fact]
        public void ThePlayedLotKeepsBaseAltZeroAndTheSurroundIsLiftedToItsGround()
        {
            // The production arrangement: the played lot's BaseAlt stays 0 (its walls, roofs and
            // avatars bake the altitude in), so its ground is at the ABSOLUTE
            // median * TerrainFactor * 3 and the surround is raised by exactly that.
            const int arryMedian = 24;
            var map = FlatMap(MapSize, NeighbourhoodMapAdapter.QuantiseAltitude(arryMedian));
            var lot = Lot(68, 200, 300);
            lot.BaseHeight = arryMedian;
            var placement = SimitoneSurroundingsPlacement.For(map, lot);

            var blueprint = new FSO.LotView.Model.Blueprint(lot.Size, lot.Size);
            blueprint.Altitude = new short[lot.Size * lot.Size];
            for (var i = 0; i < blueprint.Altitude.Length; i++)
            {
                blueprint.Altitude[i] = (short)(arryMedian
                    * NeighbourhoodCityMeshBuilder.LotAltitudeUnitsPerArryUnit);
            }
            Assert.Equal(0, blueprint.BaseAlt);

            var lotGround = SimitoneSurroundingsPlacement.LotGroundAltitudeFor(blueprint);
            Assert.True(Math.Abs(SimitoneSurroundingsPlacement.BaseAltitudeFor(map, lot) - lotGround) <= 1);

            var middleX = lot.WorldX + lot.Size / 2;
            var middleZ = lot.WorldY + lot.Size / 2;
            var ownGround = (blueprint.Altitude[(lot.Size / 2) * lot.Size + lot.Size / 2]
                - blueprint.BaseAlt)
                * blueprint.TerrainFactor * SimitoneSurroundingsPlacement.LotViewUnitsPerTile;
            var surroundGround = Vector3.Transform(new Vector3(middleX,
                NeighbourhoodCityMeshBuilder.WorldHeightFor(map.ElevationData[map.Index(middleX, middleZ)]),
                middleZ), placement.WorldMatrixAt(lotGround)).Y;
            Assert.True(Math.Abs(ownGround - surroundGround) < 0.1f,
                "lot ground " + ownGround + " vs lifted surround " + surroundGround);

            //an unlifted surround is the old step, and zero lift is the old matrix exactly
            var unlifted = Vector3.Transform(new Vector3(middleX,
                NeighbourhoodCityMeshBuilder.WorldHeightFor(map.ElevationData[map.Index(middleX, middleZ)]),
                middleZ), placement.WorldMatrix).Y;
            Assert.True(Math.Abs(ownGround - unlifted) > 10f);
            Assert.Equal(placement.WorldMatrix, placement.WorldMatrixAt(0));

            //sub-worlds are lifted by the same amount: BaseAlt drops by the lot's ground
            Assert.Equal(7, SimitoneSurroundingsRestore.BaseAltitudeFor(10, 3, 0));
            Assert.Equal(7 - lotGround, SimitoneSurroundingsRestore.BaseAltitudeFor(10, 3, lotGround));

            //BaseAlt already set on the live blueprint is part of where its ground is drawn
            blueprint.BaseAlt = 50;
            Assert.Equal(lotGround - 50, SimitoneSurroundingsPlacement.LotGroundAltitudeFor(blueprint));
            Assert.Equal(0, SimitoneSurroundingsPlacement.LotGroundAltitudeFor(null));
            Assert.Equal(0, SimitoneSurroundingsPlacement.LotGroundAltitudeFor(
                new FSO.LotView.Model.Blueprint(4, 4)));
        }

        // ---- the crop ---------------------------------------------------------------------------

        [Fact]
        public void OnlyTheRegionsTrianglesSurviveTheCrop()
        {
            // A synthetic mesh: one quad per tile at the listed tile centres, so the answer is a
            // list rather than a count somebody has to trust.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);
            var mesh = MeshWith(
                new Vector2(100.5f, 100.5f),   // far outside
                new Vector2(232.5f, 332.5f),   // the player's own property
                new Vector2(199.5f, 332.5f),   // the ring immediately west of it
                new Vector2(264.5f, 332.5f),   // the ring immediately east of it
                new Vector2(232.5f, 299.5f));  // the ring immediately north of it

            var crop = SimitoneSurroundingsCrop.Crop(mesh, cull);

            //four quads in the region now, not three: the player's own property is drawn under the
            //lot rather than cut out of it, which is what makes the two grounds meet
            Assert.Equal(8, crop.KeptPrimitives);
            Assert.Equal(8, crop.LayerPrimitives[NeighbourhoodTerrain.Grass]);
            Assert.Equal(2, crop.OutsideRegion);
            Assert.Equal(2, crop.UnderActiveLot);
            Assert.Equal(0, crop.BankPrimitives);
            Assert.False(crop.IsEmpty);
            var kept = crop.LayerVertices[NeighbourhoodTerrain.Grass];
            Assert.Equal(4 * 4, kept.Length);
            for (int quad = 0; quad < 3; quad++)
            {
                //a quad is kept or dropped whole, so the decision is its centre - and the vertices
                //of a quad next to the property line straddle that line by a tile
                var centreX = (kept[quad * 4].Position.X + kept[quad * 4 + 1].Position.X
                    + kept[quad * 4 + 2].Position.X + kept[quad * 4 + 3].Position.X) * 0.25f;
                var centreZ = (kept[quad * 4].Position.Z + kept[quad * 4 + 1].Position.Z
                    + kept[quad * 4 + 2].Position.Z + kept[quad * 4 + 3].Position.Z) * 0.25f;
                Assert.True(cull.KeepsTile(centreX, centreZ),
                    "a kept quad centred at " + centreX + ", " + centreZ + " is not in the drawn region");
            }
        }

        [Fact]
        public void TheCropKeepsWholeQuadsAndRebuildsTheirIndices()
        {
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);
            var mesh = MeshWith(
                new Vector2(100.5f, 100.5f),
                new Vector2(199.5f, 332.5f),
                new Vector2(264.5f, 332.5f));

            var crop = SimitoneSurroundingsCrop.Crop(mesh, cull);

            //three quads in: one past the region's edge, two on the ring around the property. Every
            //quad the builder emits is two triangles over four vertices, so two quads are four
            //triangles, eight vertices and twelve indices - the index array is exactly the
            //triangles the draw submits, which is the invariant that keeps it from reaching past
            //the end of the vertex buffer
            var quads = 2;
            var primitives = quads * 2;
            Assert.Equal(primitives, crop.LayerPrimitives[NeighbourhoodTerrain.Grass]);
            Assert.Equal(quads * 4, crop.LayerVertices[NeighbourhoodTerrain.Grass].Length);
            Assert.Equal(quads * 6, crop.LayerIndices[NeighbourhoodTerrain.Grass].Length);
            Assert.Equal(quads * 4, MaxIndex(crop.LayerIndices[NeighbourhoodTerrain.Grass]) + 1);

            var indices = crop.LayerIndices[NeighbourhoodTerrain.Grass];
            for (int i = 0; i < quads; i++)
            {
                var first = i * 4;
                var triangle = i * 6;
                Assert.Equal(first, indices[triangle]);
                Assert.Equal(first + 1, indices[triangle + 1]);
                Assert.Equal(first + 2, indices[triangle + 2]);
                Assert.Equal(first, indices[triangle + 3]);
                Assert.Equal(first + 2, indices[triangle + 4]);
                Assert.Equal(first + 3, indices[triangle + 5]);
            }
        }

        [Fact]
        public void TheRoadsAreCroppedByTheSameRuleAsTheGround()
        {
            // A street that hangs past the edge of the drawn region is exactly the same artifact as
            // one on the ground layers. A street under the player's own ground is drawn too, for the
            // same reason the ground under it is: the lot view covers both, and cutting them out
            // left a gap where the lot's own ground is translucent.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);
            var mesh = new NeighbourhoodCityMesh();
            mesh.Roads = Quads(
                new Vector2(100.5f, 100.5f),
                new Vector2(232.5f, 332.5f),
                new Vector2(199.5f, 332.5f));

            var crop = SimitoneSurroundingsCrop.Crop(mesh, cull);

            Assert.Equal(4, crop.RoadPrimitives);
            Assert.Equal(2 * 4, crop.RoadVertices.Length);
            Assert.Equal(2 * 6, crop.RoadIndices.Length);
            Assert.Equal(2, crop.OutsideRegion);
            Assert.Equal(2, crop.UnderActiveLot);
        }

        [Fact]
        public void TheCropReEmitsEachKeptQuadsFourDistinctCorners()
        {
            // The bug, stated as the arithmetic that was wrong. The builder writes a quad as six
            // indices, (v0, v1, v2) and (v0, v2, v3); the crop read the fourth corner from
            // indices[i + 3], which is v0 again, so every kept quad was re-emitted as
            // (v0, v1, v2, v0) and BuildIndices drew (v0, v1, v2) plus a degenerate
            // (v0, v2, v0). Half of every ground quad was never rasterised and the holes showed
            // the cleared background - the dark triangle checkerboard around a live lot, which
            // only ever appeared here because the neighbourhood screen draws the whole-map mesh
            // and never crops it.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);
            var mesh = MeshWith(
                new Vector2(232.5f, 332.5f),   // the player's own property
                new Vector2(199.5f, 332.5f));  // the ring immediately west of it

            var crop = SimitoneSurroundingsCrop.Crop(mesh, cull);

            var source = mesh.Layers[NeighbourhoodTerrain.Grass].Vertices;
            var kept = crop.LayerVertices[NeighbourhoodTerrain.Grass];
            Assert.Equal(source.Length, kept.Length);
            for (int i = 0; i < kept.Length; i++)
            {
                Assert.Equal(source[i].Position, kept[i].Position);
            }
        }

        [Fact]
        public void EveryCroppedTriangleCoversRealArea()
        {
            // The same bug measured as area rather than as identity, because "the vertices are the
            // right ones" and "the ground that gets drawn is the ground that was kept" are not the
            // same claim: BuildIndices will happily draw a degenerate triangle and count it. Every
            // kept quad is one tile square, split by a diagonal into two half-tile triangles, so
            // the total area of the crop is exactly the number of quads it kept.
            var cull = SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles);
            var map = FlatMap(MapSize, 48);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);

            var crop = SimitoneSurroundingsCrop.Crop(mesh, cull);

            var degenerate = 0;
            var area = 0f;
            var outOfRange = 0;
            for (int layer = 0; layer < crop.LayerVertices.Length; layer++)
            {
                var vertices = crop.LayerVertices[layer];
                var indices = crop.LayerIndices[layer];
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] < 0 || indices[i] >= vertices.Length) outOfRange++;
                }
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    var triangle = Area(vertices[indices[t]], vertices[indices[t + 1]], vertices[indices[t + 2]]);
                    if (triangle <= 0f) degenerate++;
                    area += triangle;
                }
            }

            Assert.Equal(0, outOfRange);
            Assert.Equal(0, degenerate);
            //quads kept from the source, plus the bank the crop makes along the property line.
            //The count of quads is half the triangle count, and each of them is a tile square.
            var quads = 0;
            for (int layer = 0; layer < crop.LayerPrimitives.Length; layer++) quads += crop.LayerPrimitives[layer] / 2;
            Assert.True(quads > 0, "the region should hold some ground to measure");
            Assert.Equal((float)quads, area, 3);
        }

        /// <summary>The highest vertex index an index array refers to, or -1 when it is empty.</summary>
        private static int MaxIndex(int[] indices)
        {
            var max = -1;
            for (int i = 0; i < indices.Length; i++)
            {
                if (indices[i] > max) max = indices[i];
            }
            return max;
        }

        /// <summary>The area of one triangle, in world units squared.</summary>
        private static float Area(
            NeighbourhoodTerrainVertex a, NeighbourhoodTerrainVertex b, NeighbourhoodTerrainVertex c)
        {
            return 0.5f * Vector3.Cross(b.Position - a.Position, c.Position - a.Position).Length();
        }

        [Fact]
        public void AnInvalidRegionDrawsNothingAtAll()
        {
            var mesh = MeshWith(new Vector2(232.5f, 332.5f));

            var crop = SimitoneSurroundingsCrop.Crop(mesh, default(SimitoneSurroundingsCull));

            Assert.True(crop.IsEmpty);
            Assert.Equal(0, crop.KeptPrimitives);
            Assert.Equal(0, crop.SourcePrimitives);
        }

        [Fact]
        public void ACropOfNoMeshIsEmptyRatherThanAnException()
        {
            var crop = SimitoneSurroundingsCrop.Crop(null, SimitoneSurroundingsCull.For(
                FlatMap(MapSize, 48), Lot(68, 200, 300), SimitoneSurroundingsPlacement.CullRadiusTiles));

            Assert.True(crop.IsEmpty);
            Assert.NotNull(crop.LayerVertices);
            Assert.Equal(NeighbourhoodCityMeshBuilder.LayerCount, crop.LayerVertices.Length);
        }

        [Fact]
        public void AWholeGeneratedMapIsReducedToASmallFractionOfItsTriangles()
        {
            // The bug report's number, measured rather than argued about: a real 512x512 mesh, the
            // real region for a real lot, and the count that comes out the other side. Nothing is
            // lost - every triangle the whole-map mesh held is accounted for as kept or beyond the
            // region - and the kept count includes the lot's own ground, which is drawn under the
            // lot rather than cut out of it.
            var map = NeighbourhoodMap.Create(MapSize);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var lot = Lot(68, 200, 300);
            var cull = SimitoneSurroundingsCull.For(map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);

            var crop = SimitoneSurroundingsCrop.Crop(mesh, cull);

            Assert.Equal(mesh.TotalPrimitives, crop.SourcePrimitives);
            Assert.Equal(crop.SourcePrimitives, crop.KeptFromSource + crop.OutsideRegion);
            Assert.True(crop.KeptPrimitives > 0, "the region should hold some ground");
            Assert.True(crop.KeptPrimitives < crop.SourcePrimitives / 4,
                crop.KeptPrimitives + " of " + crop.SourcePrimitives
                + " triangles survived, which is not much of a cull");
            Assert.True(crop.UnderActiveLot > 0,
                "the player's own lot is inside the region, so its ground is drawn under the lot");
        }

        [Fact]
        public void TheRegionsTrianglesAreExactlyTheTilesItCovers()
        {
            // The count derived the other way, the way a player would: walk the map a tile at a
            // time and count the ones the region keeps. This is the assertion that would fail if
            // the crop were off by one tile on any edge, and it is the number a screenshot of the
            // bug would have to be reconciled against.
            var map = NeighbourhoodMap.Create(MapSize);
            var lot = Lot(68, 200, 300);
            var cull = SimitoneSurroundingsCull.For(map, lot, SimitoneSurroundingsPlacement.CullRadiusTiles);

            var expectedGroundQuads = 0;
            for (int z = 0; z < MapSize; z++)
            {
                var rowLeft = NeighbourhoodCityMeshBuilder.DiamondLeft(z);
                var rowRight = NeighbourhoodCityMeshBuilder.DiamondRight(z);
                if (rowRight <= rowLeft) continue;
                for (int x = Math.Max(0, rowLeft); x < Math.Min(MapSize, rowRight); x++)
                {
                    if (map.TerrainType[map.Index(x, z)] >= NeighbourhoodCityMeshBuilder.LayerCount) continue;
                    if (cull.KeepsTile(x + 0.5f, z + 0.5f)) expectedGroundQuads++;
                }
            }

            var crop = SimitoneSurroundingsCrop.Crop(NeighbourhoodCityMeshBuilder.Build(map), cull);

            //the ground layers carry exactly the region's tiles; the roads are whatever the road
            //graph put in the region, which is a separate question with its own tests
            var ground = 0;
            for (int i = 0; i < crop.LayerPrimitives.Length; i++) ground += crop.LayerPrimitives[i];

            Assert.True(expectedGroundQuads > 0);
            //a blank map is flat, so there is no step to bank over and the crop adds nothing - which
            //is the point: the bank is a consequence of a height difference, not a fixed cost
            Assert.Equal(0, crop.BankPrimitives);
            Assert.Equal(expectedGroundQuads * 2, ground);
            Assert.Equal(ground + crop.RoadPrimitives, crop.KeptPrimitives);
        }

        // ---- helpers ----------------------------------------------------------------------------

        /// <summary>A mesh with one grass quad per listed tile centre.</summary>
        private static NeighbourhoodCityMesh MeshWith(params Vector2[] centres)
        {
            var mesh = new NeighbourhoodCityMesh();
            mesh.Layers[NeighbourhoodTerrain.Grass] = Quads(centres);
            return mesh;
        }

        private static NeighbourhoodMeshLayer Quads(params Vector2[] centres)
        {
            var vertices = new List<NeighbourhoodTerrainVertex>();
            var indices = new List<int>();
            foreach (var centre in centres)
            {
                var first = vertices.Count;
                var x = centre.X - 0.5f;
                var z = centre.Y - 0.5f;
                vertices.Add(Vertex(x, z));
                vertices.Add(Vertex(x + 1, z));
                vertices.Add(Vertex(x + 1, z + 1));
                vertices.Add(Vertex(x, z + 1));
                indices.Add(first);
                indices.Add(first + 1);
                indices.Add(first + 2);
                indices.Add(first);
                indices.Add(first + 2);
                indices.Add(first + 3);
            }
            return new NeighbourhoodMeshLayer
            {
                Vertices = vertices.ToArray(),
                Indices = indices.ToArray()
            };
        }

        private static NeighbourhoodTerrainVertex Vertex(float x, float z)
        {
            return new NeighbourhoodTerrainVertex
            {
                Position = new Vector3(x, 0f, z),
                Normal = Vector3.Up,
                Transparency = 1f,
                TextureCoord = Vector2.Zero,
                MaskTextureCoord = new Vector2(-1, -1)
            };
        }
    }
}
