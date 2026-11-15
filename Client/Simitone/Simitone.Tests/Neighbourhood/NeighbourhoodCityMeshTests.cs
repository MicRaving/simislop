/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.Utils;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// M0's answer, as tests.
    ///
    /// <para>
    /// The plan's M0 milestone is a reuse spike: hand-build a map, run the city's mesh generator,
    /// draw it, and see whether the result is acceptable. Three of those four steps cannot run in a
    /// test - <c>CityGeometry.RegenMeshVerts</c> builds a mesh only to upload it, and uploading
    /// needs a <c>GraphicsDevice</c> - and the one that can is the part that actually decides
    /// whether the reuse is worth attempting. So the milestone's question is answered here in the
    /// form that is checkable: <b>does the generated map survive the transformation?</b> The tile
    /// loop's input is six 512x512 arrays and nothing else; the only assumptions it makes about
    /// them are the size, the terrain type range, and the road nibble range. All three are
    /// asserted, against a real generated map and against hand-built worst cases.
    /// </para>
    ///
    /// <para>
    /// <b>What is deliberately not here.</b> A test that a mesh builds, and a test that the
    /// result looks right. Both need a GPU, and a fake one proves nothing about a shader. What is
    /// here is the half that has bitten before: the diamond clip silently clipping everything, a
    /// terrain byte outside 0..4 indexing five buffers, a road nibble pointing at an atlas cell
    /// that does not exist. Those are the ways this fails, and they are all checkable.
    /// </para>
    /// </summary>
    public class NeighbourhoodCityMeshTests
    {
        /// <summary>A full-size generated map, from a real layout and the real generator.</summary>
        private static NeighbourhoodMap GeneratedFullMap()
        {
            //The generator is O(size^2) and the mesh builder is too, so this is the one test in the
            //file that works at 512x512. Everything below it is small.
            var lots = new[]
            {
                Lot(11, 40, 40, NeighbourhoodDoorSide.East),
                Lot(12, 40, 200, NeighbourhoodDoorSide.East),
                Lot(13, 200, 40, NeighbourhoodDoorSide.West),
                Lot(14, 200, 200, NeighbourhoodDoorSide.West),
            };
            var input = new NeighbourhoodMapInput(NeighbourhoodTestInput.DefaultSeed, 512, lots);
            return NeighbourhoodMapGenerator.Generate(input);
        }

        private static NeighbourhoodLotInput Lot(short number, int x, int y, byte doorSide)
        {
            return new NeighbourhoodLotInput(number, x, y, 32, LotZoning.ZoneResidential,
                NeighbourhoodTerrain.Grass, 48, doorSide, true);
        }

        /// <summary>A full-size map with a straight road on every tile, so it has road geometry.</summary>
        private static NeighbourhoodMap UniformMapWithRoads()
        {
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int i = 0; i < map.TerrainType.Length; i++)
            {
                map.TerrainType[i] = NeighbourhoodTerrain.Grass;
                map.RoadData[i] = 5; //the low nibble's straight road; see the road layout table
            }
            return map;
        }

        [Fact]
        public void AGeneratedMapIsMeshable()
        {
            var map = GeneratedFullMap();

            string reason;
            Assert.True(NeighbourhoodCityMeshBuilder.CanMesh(map, out reason), reason);
            Assert.Null(reason);
        }

        [Fact]
        public void AGeneratedMapHasNoTerrainTypeOutsideTheFiveLayers()
        {
            // The one way a generated map breaks the mesh builder: the terrain type is a raw byte
            // used as a layer index, and there are exactly five layers. 255 is the documented
            // "emit nothing" value and is skipped before the index is used, so it is legal; 5..254
            // is not, and would be an IndexOutOfRange on the layer list.
            var map = GeneratedFullMap();

            Assert.Equal(-1, NeighbourhoodCityMeshProbe.FirstUnmeshableTerrainType(map));

            var drawable = 0;
            var nothing = 0;
            for (int i = 0; i < map.TerrainType.Length; i++)
            {
                var type = map.TerrainType[i];
                Assert.True(type <= NeighbourhoodCityMeshBuilder.LayerCount
                    || type == NeighbourhoodTerrain.Nothing,
                    "terrain type " + type + " is neither a drawable terrain nor 255");
                if (type == NeighbourhoodTerrain.Nothing) nothing++;
                else drawable++;
            }

            // a map with nothing but 255 in it would mesh to an empty mesh, which is a different
            // failure from "cannot mesh" and should not be able to pass unnoticed
            Assert.True(drawable > 0);
            Assert.True(nothing < map.TerrainType.Length);
        }

        [Fact]
        public void AGeneratedMapsRoadNibblesAllNameRealAtlasCells()
        {
            var map = GeneratedFullMap();

            // The road nibbles index the 16-entry layout tables directly, and those name cells in
            // an 8x4 atlas. A nibble that names a cell outside it samples another road's pixels -
            // which looks like a road texture bug on somebody's machine and nowhere else.
            var seen = new HashSet<int>();
            for (int i = 0; i < map.RoadData.Length; i++)
            {
                var straight = map.RoadData[i] & RoadMask.MaskBits;
                var corner = map.RoadData[i] >> 4;
                if (straight == 0 && corner == 0) continue;

                if (straight > 0)
                {
                    Assert.True(straight <= 15);
                    seen.Add(NeighbourhoodRoadLayout.CellFor(straight, false));
                }
                if (corner > 0)
                {
                    Assert.True(corner <= 15);
                    seen.Add(NeighbourhoodRoadLayout.CellFor(corner, true));
                }
            }

            Assert.NotEmpty(seen);
            foreach (var cell in seen)
            {
                Assert.True(NeighbourhoodRoadLayout.IsRealCell(cell),
                    "road shape names atlas cell " + cell + ", which is outside the "
                    + NeighbourhoodRoadLayout.Width + "x" + NeighbourhoodRoadLayout.Height + " atlas");
            }
        }

        // ------------------------------------------------------------ the blend masks (Part 9)

        [Fact]
        public void TheBlendMaskTableIsTheCitysSixteenEntryRemap()
        {
            // CityContent.FlagLayout (CityContent.cs:49-52), verbatim, and the whole of the
            // mesh's half of the blend contract. It is a table rather than the identity because the
            // 7x3 atlas is arranged to keep mipmapped and linearly-filtered cell boundaries from
            // bleeding into each other - so the order is authored data, and re-sorting it "tidily"
            // would put every blend mask in the wrong place with nothing to say so.
            Assert.Equal(new[] { 11, 7, 15, 2, 9, 6, 0, 4, 1, 16, 20, 12, 14, 18, 10, 8 },
                (int[])NeighbourhoodBlendFlags.FlagLayout.Clone());

            //Every mask names a real cell of a 7x3 grid, and no two masks share one - a duplicate
            //would mean two different adjacency patterns drawing the same cross-fade, which is a
            //content difference nobody can see.
            var cells = new HashSet<int>();
            for (int mask = 0; mask < NeighbourhoodBlendFlags.MaskCount; mask++)
            {
                var cell = NeighbourhoodBlendFlags.FlagLayout[mask];
                Assert.InRange(cell, 0,
                    NeighbourhoodBlendFlags.AtlasWidth * NeighbourhoodBlendFlags.AtlasHeight - 1);
                Assert.True(cells.Add(cell), "two masks name the same atlas cell: " + cell);
            }
            Assert.Equal(16, cells.Count);
        }

        [Fact]
        public void TheBlendAtlasCellSizeAndLayerIndexAreTheCitys()
        {
            // CityGeometry.RegenMeshVerts' bDelta (CityGeometry.cs:164) and DrawAll's per-layer
            // index (CityGeometry.cs:864). Both are arithmetic rather than data and both are easy to
            // "simplify", which is how a mask ends up sampling a neighbouring cell.
            var cell = NeighbourhoodBlendFlags.CellSize;
            Assert.Equal(1f / 7f, cell.X, 6);
            Assert.Equal(1f / 3f, cell.Y, 6);
            Assert.Equal(7, NeighbourhoodBlendFlags.AtlasWidth);
            Assert.Equal(3, NeighbourhoodBlendFlags.AtlasHeight);

            //(1 - (i - 1) / 2) * 2 + ((4 - i) % 2), integer division, for i = 1..4.
            Assert.Equal(3, NeighbourhoodBlendFlags.AtlasIndexForLayer(1));
            Assert.Equal(2, NeighbourhoodBlendFlags.AtlasIndexForLayer(2));
            Assert.Equal(1, NeighbourhoodBlendFlags.AtlasIndexForLayer(3));
            Assert.Equal(0, NeighbourhoodBlendFlags.AtlasIndexForLayer(4));
            //and every layer's index is a real atlas
            for (int layer = 0; layer < NeighbourhoodCityMeshBuilder.LayerCount; layer++)
            {
                Assert.InRange(NeighbourhoodBlendFlags.AtlasIndexForLayer(layer), 0,
                    NeighbourhoodBlendFlags.AtlasCount - 1);
            }
        }

        [Fact]
        public void ABlendIsOnlyEmittedForAHigherPriorityNeighbourInsideTheDiamond()
        {
            // GetBlend's three conditions (CityGeometry.cs:41-80), stated as a hand-built map so
            // each one is isolated:
            //  - a neighbour counts only if its terrain is STRICTLY greater than this tile's
            //  - 255 ("nothing") never counts, in either direction
            //  - mask 15 - no edge present - draws no quad at all (CityGeometry.cs:303)
            // The base is grass, the lowest of the five, because the rule is about being
            // *higher*: a rock tile does not blend into the sand beside it, and a test that put
            // rock under the sand would be asserting nothing.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int i = 0; i < map.TerrainType.Length; i++) map.TerrainType[i] = NeighbourhoodTerrain.Grass;

            const int row = 306;
            const int column = 200;
            //the centre of a wide row, so it is strictly inside the diamond
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(column, row));

            //a grass tile with only grass neighbours: no edge is higher, so the mask is all-bits-set
            var flat = NeighbourhoodCityMeshBuilder.GetBlend(map.TerrainType, row, column);
            Assert.Equal(NeighbourhoodBlendFlags.NoBlend, flat.Binary);
            Assert.False(flat.NeedsBlend);
            //maxEdge stays at its initial 4 (water), because the loop only ever moves DOWN from
            //there and no edge qualified (CityGeometry.cs:69-72)
            Assert.Equal(4, flat.MaxEdge);

            //sand to the east: one edge present (edge 1, +X), which is bit 0 CLEAR
            map.TerrainType[map.Index(column + 1, row)] = NeighbourhoodTerrain.Sand;
            var oneEdge = NeighbourhoodCityMeshBuilder.GetBlend(map.TerrainType, row, column);
            Assert.True(oneEdge.NeedsBlend);
            Assert.Equal(0, oneEdge.Binary & 1);
            Assert.Equal(1, oneEdge.MaxEdge);

            //rock to the west and snow to the north as well: maxEdge must stay 1, because the
            //reference's loop starts at 4 and only moves DOWN (CityGeometry.cs:69-72) - so a tile
            //beside both sand and rock cross-fades to the *sand*, not to whichever came first
            map.TerrainType[map.Index(column - 1, row)] = NeighbourhoodTerrain.Rock;
            map.TerrainType[map.Index(column, row - 1)] = NeighbourhoodTerrain.Snow;
            var threeEdges = NeighbourhoodCityMeshBuilder.GetBlend(map.TerrainType, row, column);
            Assert.Equal(1, threeEdges.MaxEdge);
            //all three edges present, so three bits clear
            Assert.Equal(0, threeEdges.Binary & 1);
            Assert.Equal(0, threeEdges.Binary & 2);
            Assert.Equal(0, threeEdges.Binary & 4);
            //and the fourth bit is set, because there is no higher terrain to the south
            Assert.Equal(8, threeEdges.Binary & 8);

            //Water to the south: 4 is the highest terrain number, so it counts, and maxEdge is still
            //1 because the reference's loop only moves down from 4 - the *lowest* qualifying
            //neighbour wins, and a name that reads as "the highest edge" is the reference's.
            map.TerrainType[map.Index(column, row + 1)] = NeighbourhoodTerrain.Water;
            var withWater = NeighbourhoodCityMeshBuilder.GetBlend(map.TerrainType, row, column);
            Assert.True(withWater.NeedsBlend);
            Assert.Equal(0, withWater.Binary & 8);
            Assert.Equal(1, withWater.MaxEdge);

            //255 is "nothing, do not blend into this" (CityMapData.TerrainTypeMap, CityMapData.cs:26)
            //and must be excluded in both directions
            map.TerrainType[map.Index(column + 1, row)] = NeighbourhoodTerrain.Nothing;
            map.TerrainType[map.Index(column - 1, row)] = NeighbourhoodTerrain.Nothing;
            map.TerrainType[map.Index(column, row - 1)] = NeighbourhoodTerrain.Nothing;
            map.TerrainType[map.Index(column, row + 1)] = NeighbourhoodTerrain.Nothing;
            var nothingNeighbours = NeighbourhoodCityMeshBuilder.GetBlend(map.TerrainType, row, column);
            Assert.Equal(NeighbourhoodBlendFlags.NoBlend, nothingNeighbours.Binary);
            Assert.False(nothingNeighbours.NeedsBlend);

            //and 255 as the tile's OWN type is not a neighbour of anything, which is the "in the
            //other direction" half: it is skipped before the mask is even computed
            map.TerrainType[map.Index(column, row)] = NeighbourhoodTerrain.Nothing;
            map.TerrainType[map.Index(column + 1, row)] = NeighbourhoodTerrain.Snow;
            var fromNothing = NeighbourhoodCityMeshBuilder.GetBlend(map.TerrainType, row, column);
            Assert.Equal(NeighbourhoodBlendFlags.NoBlend, fromNothing.Binary);
            Assert.False(fromNothing.NeedsBlend);

            //and the atlas position is the remapped cell's top-left corner
            var expected = NeighbourhoodBlendFlags.FlagLayout[oneEdge.Binary];
            Assert.Equal((expected % 7) / 7f, oneEdge.AtlasPosition.X, 5);
            Assert.Equal((expected / 7) / 3f, oneEdge.AtlasPosition.Y, 5);
        }

        [Fact]
        public void ABlendOnlyCountsStrictlyInsideTheDiamondNotInTheFadeSkirt()
        {
            // Both of TSO's blend emissions are inside `if (j > rXS && j < rXE)` -
            // RegenMeshVerts at CityGeometry.cs:301 and SubRegenMeshVerts's normalTile at :666.
            // The ten-tile fade skirt is ground only: a cross-fade hanging over a cosmetic edge
            // would be a visible seam hanging over nothing. A tile on the edge itself is excluded
            // too, which is the strict inequality and the reason a road stops one tile short of
            // the border.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int i = 0; i < map.TerrainType.Length; i++) map.TerrainType[i] = NeighbourhoodTerrain.Grass;

            //Row 100 rather than the widest row, and that is load-bearing rather than incidental: the
            //skirt column below is placed ten tiles left of the diamond's edge, so the edge has to be
            //at least that far into the map for the column to have a column index at all. On the
            //widest row the left edge is 0 and "ten tiles left of it" is off the map.
            const int row = 100;
            var left = NeighbourhoodCityMeshBuilder.DiamondLeft(row);
            Assert.True(left >= NeighbourhoodCityMeshBuilder.FadeRange - 1,
                "this test's skirt column needs a diamond edge at least FadeRange tiles into the"
                + " map, and row " + row + "'s is at " + left);
            //A column of sand, eight tiles in from the diamond's left edge and nine rows tall, so
            //it is strictly inside on every row it covers. Its west and east grass neighbours all
            //have a strictly higher neighbour and so all blend; the identical column ten tiles out
            //in the fade skirt has grass neighbours that the mask arithmetic would also blend and
            //that the j > rXS && j < rXE gate has to throw away.
            const int sandColumn = 8;
            for (int y = row - 4; y <= row + 4; y++)
            {
                map.TerrainType[map.Index(left + sandColumn, y)] = NeighbourhoodTerrain.Sand;
            }
            //and the same column ten tiles out, in the fade skirt, on rows that are inside the map
            var skirtColumn = left - NeighbourhoodCityMeshBuilder.FadeRange + 1;
            for (int y = row - 4; y <= row + 4; y++)
            {
                map.TerrainType[map.Index(skirtColumn, y)] = NeighbourhoodTerrain.Sand;
            }

            //The gate is the difference between the two columns, so it is stated as a difference:
            //walk the map the same way the probe does and count the strictly-inside blending tiles
            //directly, and confirm the mesh emitted exactly those and no skirt contribution.
            NeighbourhoodCityMeshBuilder.Build(map, out var report);
            //walk the map the same way the probe does and count the strictly-inside blending tiles
            //directly, and confirm the mesh emitted exactly those and no skirt contribution.
            var inside = 0;
            for (int y = 0; y < NeighbourhoodCityMeshBuilder.MapSize; y++)
            {
                var yl = NeighbourhoodCityMeshBuilder.DiamondLeft(y);
                var yr = NeighbourhoodCityMeshBuilder.DiamondRight(y);
                for (int x = yl + 1; x < yr; x++)
                {
                    var cx = Math.Min(Math.Max(yl, x), yr - 1);
                    if (map.TerrainType[map.Index(cx, y)] >= NeighbourhoodCityMeshBuilder.LayerCount)
                    {
                        continue;
                    }
                    if (NeighbourhoodCityMeshBuilder.GetBlend(map.TerrainType, y, cx).NeedsBlend) inside++;
                }
            }
            Assert.True(inside > 0, "the test map should have strictly-inside blending tiles");
            Assert.Equal(inside, report.EmittedBlendQuads);
            Assert.True(report.EmissionMatchesProbe, "the report and the walk disagree: " + report);
            Assert.Equal(inside, report.BlendQuads);
            Assert.Equal(inside, report.BlendQuadsPerLayer[NeighbourhoodTerrain.Sand]);
            //and the skirt column's own west neighbours are the tiles the gate threw away, which is
            //what makes the two columns a test of the gate rather than of the mask
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(skirtColumn - 1, row));
            Assert.True(NeighbourhoodCityMeshBuilder.GetBlend(
                map.TerrainType, row, skirtColumn - 1).NeedsBlend);

            //And a map of one terrain emits nothing at all, which is the degenerate case a blend
            //count has to get right: with every tile the same there is no edge with a higher
            //neighbour anywhere, so every mask is 15 and no quad is drawn. A generated neighbourhood
            //has several terrains and so does emit blends; this is the floor.
            var uniform = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int i = 0; i < uniform.TerrainType.Length; i++)
            {
                uniform.TerrainType[i] = NeighbourhoodTerrain.Grass;
            }
            NeighbourhoodCityMeshBuilder.Build(uniform, out var uniformReport);
            Assert.Equal(0, uniformReport.EmittedBlendQuads);
            Assert.Equal(0, uniformReport.BlendQuads);

            //The full check, on a map with a boundary that crosses the diamond and therefore both
            //its interior and its fade skirt: grass on the left of the map and sand on the right.
            var twoTerrains = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int y = 0; y < twoTerrains.Height; y++)
            {
                for (int x = 0; x < twoTerrains.Width; x++)
                {
                    //grass on the left of the map, sand on the right: a long boundary that crosses
                    //the diamond, so the border and the skirt are both walked
                    twoTerrains.TerrainType[twoTerrains.Index(x, y)] = x < 256
                        ? NeighbourhoodTerrain.Grass : NeighbourhoodTerrain.Sand;
                }
            }
            var bordered = NeighbourhoodCityMeshBuilder.Build(twoTerrains, out var borderedReport);
            Assert.True(borderedReport.EmittedBlendQuads > 0, "a grass/sand border should blend");

            //Every blend *quad* is strictly inside. The test is on the quad's minimum corner, not on
            //each vertex: a tile at column j has corners at j and j + 1, and the tile is inside when
            //j < rXE, so its far corner lands exactly ON the edge. Testing the vertex would fail on
            //a correct mesh - it is a test artefact, not a boundary the builder crossed.
            var checkedQuads = 0;
            foreach (var layer in bordered.Layers)
            {
                if (layer.Indices == null || layer.Vertices == null) continue;
                for (int t = 0; t + 2 < layer.Indices.Length; t += 3)
                {
                    //(-1,-1) is the shader's "no mask" sentinel (PixShader.fx:382-384) and this
                    //quad is ground, not a blend
                    if (layer.Vertices[layer.Indices[t]].MaskTextureCoord.X < -0.5f) continue;
                    checkedQuads++;
                    var minX = int.MaxValue;
                    var minZ = int.MaxValue;
                    for (int c = 0; c < 3; c++)
                    {
                        var p = layer.Vertices[layer.Indices[t + c]].Position;
                        minX = Math.Min(minX, (int)Math.Floor(p.X));
                        minZ = Math.Min(minZ, (int)Math.Floor(p.Z));
                    }
                    Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(minX, minZ),
                        "a blend face was emitted for tile " + minX + "," + minZ
                        + ", which is outside the isometric diamond");
                }
            }
            Assert.True(checkedQuads > 0, "no blend faces were found to check");
        }

        [Fact]
        public void TheGeneratedBlendAtlasCoversTheRemappedCellsAndNothingElse()
        {
            // The generated atlas has to be the 7x3 grid the mask table indexes, with a black cell
            // wherever no mask lands - that black is the "no blend" answer, and the reference gets it
            // by clearing the target before drawing fifteen cells (CityContent.cs:276, :287-290).
            var pixels = NeighbourhoodBlendTextures.PixelsFor(0);
            Assert.Equal(NeighbourhoodBlendTextures.AtlasWidth * NeighbourhoodBlendTextures.AtlasHeight,
                pixels.Length);
            Assert.Equal(7 * NeighbourhoodBlendTextures.CellSize, NeighbourhoodBlendTextures.AtlasWidth);
            Assert.Equal(3 * NeighbourhoodBlendTextures.CellSize, NeighbourhoodBlendTextures.AtlasHeight);

            //A named cell is never entirely black. Sampled anywhere, not at a corner: a cell's
            //corners are the *edges* of the tile, and a mask that names one edge ramps on that side
            //only - so a corner on an unnamed side is legitimately black and asserting on corners
            //would be asserting about which edges a given mask names, which the ramp test below
            //does far more precisely.
            var named = new HashSet<int>(NeighbourhoodBlendFlags.FlagLayout);
            for (int cy = 0; cy < 3; cy++)
            {
                for (int cx = 0; cx < 7; cx++)
                {
                    var cell = cx + cy * 7;
                    var max = 0;
                    for (int py = 0; py < NeighbourhoodBlendTextures.CellSize; py++)
                    {
                        for (int px = 0; px < NeighbourhoodBlendTextures.CellSize; px++)
                        {
                            var x = cx * NeighbourhoodBlendTextures.CellSize + px;
                            var y = cy * NeighbourhoodBlendTextures.CellSize + py;
                            max = Math.Max(max, pixels[y * NeighbourhoodBlendTextures.AtlasWidth + x].R);
                        }
                    }
                    if (named.Contains(cell))
                    {
                        Assert.NotEqual(-1, NeighbourhoodBlendTextures.MaskAt(
                            cx * NeighbourhoodBlendTextures.CellSize, cy * NeighbourhoodBlendTextures.CellSize));
                        //Mask 15's cell is black on purpose: it names every edge, and a mask that
                        //names every edge is "nothing to blend" - the quad is not even emitted for
                        //it (CityGeometry.cs:303). So six cells are black, not five, and the count
                        //below is stated in terms of the table rather than the grid.
                        if (NeighbourhoodBlendTextures.MaskAt(
                            cx * NeighbourhoodBlendTextures.CellSize, cy * NeighbourhoodBlendTextures.CellSize)
                            == NeighbourhoodBlendFlags.NoBlend)
                        {
                            Assert.Equal(0, max);
                            continue;
                        }
                        Assert.True(max > 0, "atlas cell " + cell + " is entirely black");
                        continue;
                    }
                    //an unnamed cell is the "no blend" answer, and black is what that is - the
                    //reference gets it by clearing the target before drawing (CityContent.cs:276)
                    Assert.Equal(-1, NeighbourhoodBlendTextures.MaskAt(
                        cx * NeighbourhoodBlendTextures.CellSize, cy * NeighbourhoodBlendTextures.CellSize));
                    Assert.Equal(0, max);
                }
            }
            //The grid has 21 cells and the table names 16, so five are unnamed and black. That is
            //what leaves room for the reference's black filler block (CityContent.cs:287-290, whose
            //comment says it exists "to cause no blend if adjacency bitmask is 0000") and it is why
            //the two counts are not interchangeable: a cell the table does not name is never sampled
            //for a blend, so its contents are free, and a generated atlas fills it with black.
            Assert.Equal(5, NeighbourhoodBlendFlags.AtlasWidth
                * NeighbourhoodBlendFlags.AtlasHeight - NeighbourhoodBlendFlags.MaskCount);
        }

        [Fact]
        public void ABlendMaskRampsOnTheEdgesItNamesAndIsClearInTheMiddle()
        {
            // The bit order is the reference's and is not the edge order, so this is the assertion
            // that says which side of the tile a given mask ramps on. A mask of 1 has exactly one
            // edge present - edge 1, the +X one - and its bits are therefore 1110: bits 1, 2 and 3
            // set (the absent edges) and bit 0 clear.
            const int eastOnly = 0b1110;
            Assert.Equal(eastOnly, 14);

            //One pixel in from the eastern edge is opaque, and the ramp is smoothstepped rather than
            //linear - so the value just inside the edge is very slightly under 1 rather than exactly
            //1, and a two-decimal assertion is the honest one. (A linear ramp would be 1.00 here and
            //would have a visible crease where it met the clear interior.)
            Assert.True(NeighbourhoodBlendTextures.AlphaFor(eastOnly, 63, 32, 1f) > 0.99f);
            //clear at the western edge
            Assert.Equal(0f, NeighbourhoodBlendTextures.AlphaFor(eastOnly, 0, 32, 1f));
            //and the middle of the cell is clear too, because the ramp stops at the half tile
            Assert.Equal(0f, NeighbourhoodBlendTextures.AlphaFor(eastOnly, 31, 31, 1f));
            //and the ramp really is gradual rather than a step
            var near = NeighbourhoodBlendTextures.AlphaFor(eastOnly, 60, 32, 1f);
            var far = NeighbourhoodBlendTextures.AlphaFor(eastOnly, 50, 32, 1f);
            Assert.True(near > 0.5f && near < 1f, "a pixel four in from the edge should be a"
                + " gradient value, not opaque and not clear: " + near);
            Assert.True(far < near, "the ramp must fall as it leaves the edge");

            //a mask naming no edge (15) has no ramp at all - which is TSO's "nothing to blend"
            //answer, and why the blend quad is not emitted for it at all
            Assert.Equal(0f, NeighbourhoodBlendTextures.AlphaFor(15, 63, 32, 1f));
            Assert.Equal(0f, NeighbourhoodBlendTextures.AlphaFor(15, 0, 32, 1f));

            //a mask naming all four (0) is solid everywhere - the "this tile is surrounded by higher
            //terrain" case, where the blend layer covers the tile completely
            for (int x = 0; x < 64; x += 8)
            {
                for (int y = 0; y < 64; y += 8)
                {
                    Assert.True(NeighbourhoodBlendTextures.AlphaFor(0, x, y, 1f) > 0.99f);
                }
            }

            //Two opposite edges is a band, one on each side and clear down the middle. East is edge
            //1 (bit 0) and west is edge 3 (bit 2), so both present means both bits CLEAR: 0b1010.
            const int eastAndWest = 0b1010;
            Assert.True(NeighbourhoodBlendTextures.AlphaFor(eastAndWest, 63, 32, 1f) > 0.99f);
            Assert.True(NeighbourhoodBlendTextures.AlphaFor(eastAndWest, 0, 32, 1f) > 0.99f);
            Assert.Equal(0f, NeighbourhoodBlendTextures.AlphaFor(eastAndWest, 32, 32, 1f));

            //and two ADJACENT edges is a corner, which is the only way to tell the two apart. +X is
            //edge 1 (bit 0) and is the cell's RIGHT (u = 1); +Z is edge 2 (bit 3) and is the cell's
            //TOP (v = 1). Both present means bits 0 and 3 clear: 0b0110. So the right and the top
            //edges ramp and the left and the bottom do not, and the pixel that is clear on both
            //counted axes is the bottom-left one.
            const int eastAndSouth = 0b0110;
            Assert.True(NeighbourhoodBlendTextures.AlphaFor(eastAndSouth, 63, 63, 1f) > 0.99f);
            Assert.True(NeighbourhoodBlendTextures.AlphaFor(eastAndSouth, 63, 0, 1f) > 0.99f);
            Assert.True(NeighbourhoodBlendTextures.AlphaFor(eastAndSouth, 0, 63, 1f) > 0.99f);
            Assert.Equal(0f, NeighbourhoodBlendTextures.AlphaFor(eastAndSouth, 0, 0, 1f));
            //and the middle is clear, because both ramps stop at the half tile
            Assert.Equal(0f, NeighbourhoodBlendTextures.AlphaFor(eastAndSouth, 31, 31, 1f));

            //which edge is which, stated once: -Z is edge 0 (bit 1) and is the cell's bottom, v = 0,
            //per the corner order CityGeometry.cs:316-343 writes the mask UVs in. So 0b1101 - the
            //only clear bit is bit 1 - ramps along the bottom edge and nowhere else.
            const int minusZ = 0b1101;
            Assert.True(NeighbourhoodBlendTextures.AlphaFor(minusZ, 32, 0, 1f) > 0.99f);
            Assert.Equal(0f, NeighbourhoodBlendTextures.AlphaFor(minusZ, 32, 63, 1f));
            //and 0b1011, the only clear bit being bit 2 (edge 3, -X), ramps along the left edge
            const int minusX = 0b1011;
            Assert.True(NeighbourhoodBlendTextures.AlphaFor(minusX, 0, 32, 1f) > 0.99f);
            Assert.Equal(0f, NeighbourhoodBlendTextures.AlphaFor(minusX, 63, 32, 1f));
        }

        [Fact]
        public void TheRoadLayoutTablesCoverOnlyRealAtlasCells()
        {
            // The tables are copied from TSO's CityContent, and a copy is a thing that can go
            // stale. This is the test that says it has not: every non-empty entry in both tables
            // has to be a cell the 8x4 atlas actually has, and entry 0 has to be the -1 that means
            // "no road" - which is why the road passes both check the nibble before indexing.
            for (int mask = 1; mask < 16; mask++)
            {
                Assert.True(NeighbourhoodRoadLayout.IsRealCell(NeighbourhoodRoadLayout.CellFor(mask, false)),
                    "RoadLayout[" + mask + "] names a cell outside the atlas");
                Assert.True(NeighbourhoodRoadLayout.IsRealCell(NeighbourhoodRoadLayout.CellFor(mask, true)),
                    "RoadCLayout[" + mask + "] names a cell outside the atlas");
            }

            Assert.Equal(-1, NeighbourhoodRoadLayout.Straights[0]);
            Assert.Equal(-1, NeighbourhoodRoadLayout.Corners[0]);
            Assert.Equal(-1, NeighbourhoodRoadLayout.CellFor(0, false));
            Assert.Equal(-1, NeighbourhoodRoadLayout.CellFor(0, true));
            Assert.Equal(-1, NeighbourhoodRoadLayout.CellFor(16, false));
            Assert.Equal(-1, NeighbourhoodRoadLayout.CellFor(-1, true));
        }

        [Fact]
        public void TheDrawnClipIsTheMapInsetByItsFadeSkirt()
        {
            // The mesh draws the 512x512 square inset by about ClipInset on every side (audit O1), not
            // TSO's isometric diamond: the diamond holds 125,460 tiles and a 41-lot neighbourhood
            // needs about 123,000 tiles of footprint, so it could not be packed inside it. Every row
            // has the same span, and rows above and below the square are outside.
            var inset = NeighbourhoodCityMeshBuilder.ClipInset;
            var size = NeighbourhoodCityMeshBuilder.MapSize;
            Assert.Equal(NeighbourhoodCityMeshBuilder.FadeRange + 1, inset);
            for (int row = 0; row < size; row++)
            {
                Assert.Equal(inset - 1, NeighbourhoodCityMeshBuilder.DiamondLeft(row));
                Assert.Equal(size - inset + 1, NeighbourhoodCityMeshBuilder.DiamondRight(row));
            }

            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(size / 2, size / 2));
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(inset, inset));
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(size - inset - 1, size - inset - 1));
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(inset - 1, size / 2));
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(size / 2, inset - 1));
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(size - inset, size - inset));
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(size - inset + 1, size / 2));
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(size / 2, size - inset + 1));
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(0, 0));
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(size - 1, size - 1));

            // the fade skirt is on the map on every side: the left and top edges fade to fully
            // transparent at tile 0, and the interior is opaque in both directions
            Assert.Equal(1f, NeighbourhoodCityMeshBuilder.FadeTransparency(inset - 1, size - inset + 1, 0, size / 2));
            Assert.Equal(1f, NeighbourhoodCityMeshBuilder.FadeTransparency(inset - 1, size - inset + 1, size, size / 2));
            Assert.Equal(1f, NeighbourhoodCityMeshBuilder.FadeTransparency(inset - 1, size - inset + 1, size / 2, 0));
            Assert.Equal(1f, NeighbourhoodCityMeshBuilder.RowFadeTransparency(size));
            Assert.Equal(0f, NeighbourhoodCityMeshBuilder.RowFadeTransparency(inset - 1));
            Assert.Equal(0f, NeighbourhoodCityMeshBuilder.RowFadeTransparency(size - inset + 1));
            Assert.Equal(0f, NeighbourhoodCityMeshBuilder.FadeTransparency(inset - 1, size - inset + 1, size / 2, size / 2));
            var partial = NeighbourhoodCityMeshBuilder.RowFadeTransparency(inset - 5);
            Assert.InRange(partial, 0.01f, 0.99f);

            // and TSO's diamond, which the backdrop transform still fits into, is almost all inside it
            long diamond = 0, diamondInside = 0;
            for (int row = 0; row < size; row++)
            {
                var left = row < NeighbourhoodCityMeshBuilder.DiamondCentre ? NeighbourhoodCityMeshBuilder.DiamondCentre - row : row - NeighbourhoodCityMeshBuilder.DiamondCentre;
                var right = row < NeighbourhoodCityMeshBuilder.DiamondRightPivot
                    ? NeighbourhoodCityMeshBuilder.DiamondRightBase + row
                    : size - (row - NeighbourhoodCityMeshBuilder.DiamondRightPivot);
                for (int x = left + 1; x < right; x++)
                {
                    diamond++;
                    if (NeighbourhoodCityMeshBuilder.IsInsideDiamond(x, row)) diamondInside++;
                }
            }
            Assert.True(diamondInside > diamond * 95 / 100, diamondInside + " of " + diamond);
        }

        [Fact]
        public void TheDiamondAndItsFadeCoverEveryTileOfEveryRowOnce()
        {
            // The diamond alone is a rotated square; the fade adds a skirt ten tiles wide on each
            // side. Every tile in that window has to be covered, none twice, and the total has to
            // exceed the diamond's analytic area by the skirt's worth of tiles. This is the test
            // that says the border of the map fades out rather than ending in a cliff, and that
            // the fade does not double-count against the interior.
            var diamond = NeighbourhoodCityMeshProbe.AnalyticDiamond();

            var covered = 0;
            var doubled = 0;
            for (int row = 0; row < NeighbourhoodCityMeshBuilder.MapSize; row++)
            {
                var rowLeft = NeighbourhoodCityMeshBuilder.DiamondLeft(row);
                var rowRight = NeighbourhoodCityMeshBuilder.DiamondRight(row);
                if (rowRight <= rowLeft) continue;

                var rowCount = 0;
                for (int x = 0; x < NeighbourhoodCityMeshBuilder.MapSize; x++)
                {
                    if (x < rowLeft - NeighbourhoodCityMeshBuilder.FadeRange) continue;
                    if (x >= rowRight + NeighbourhoodCityMeshBuilder.FadeRange) continue;
                    rowCount++;
                }
                covered += rowCount;

                // the window is a contiguous span, so its size is exactly the span it claims -
                // clipped to the map, because the first and last rows' skirts run off the edge
                var spanStart = Math.Max(0, rowLeft - NeighbourhoodCityMeshBuilder.FadeRange);
                var spanEnd = Math.Min(NeighbourhoodCityMeshBuilder.MapSize,
                    rowRight + NeighbourhoodCityMeshBuilder.FadeRange);
                if (rowCount != spanEnd - spanStart) doubled++;
            }

            Assert.Equal(0, doubled);
            Assert.True(covered > diamond.Tiles, "the fade skirt added no tiles at all");
            Assert.Equal(NeighbourhoodCityMeshBuilder.MapSize, diamond.Rows);
        }

        [Fact]
        public void TheMeshBuilderAndTheProbeAgreeOnAGeneratedMap()
        {
            // The two independent derivations. The builder walks chunks and emits vertices; the
            // probe walks rows and emits counts. They share the clip and nothing else, so where
            // they disagree one of them has a bug - and this is the test that says which.
            var map = GeneratedFullMap();
            var probe = NeighbourhoodCityMeshProbe.Probe(map);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);

            Assert.Null(probe.Refusal);
            Assert.True(probe.IsMeshable, probe.ToString());
            Assert.Equal(0, probe.OutOfAtlasCells);
            //the probe counts quads and the mesh counts triangles, and a quad is two of them
            var layerTriangles = 0;
            for (int i = 0; i < mesh.Layers.Length; i++) layerTriangles += mesh.Layers[i].PrimitiveCount;
            // CHANGED by the Part 9 TSO-alignment rework. A layer buffer's triangles are no longer
            // only its own ground: a blend face is a cross-fade quad emitted into a *neighbour's*
            // layer (CityGeometry.RegenMeshVerts :309-352), so the count is that layer's ground plus
            // every blend other tiles sent into it. The probe counts both, per layer, and the two
            // still have to agree - which is the point of the assertion.
            Assert.Equal((probe.VisitedTiles + probe.BlendQuads) * 2, layerTriangles);
            Assert.Equal((probe.RoadQuads + probe.CornerQuads) * 2, mesh.Roads.PrimitiveCount);

            for (int type = 0; type < probe.LayerQuads.Length; type++)
            {
                Assert.Equal((probe.LayerQuads[type] + probe.BlendQuadsPerLayer[type]) * 2,
                    mesh.Layers[type].PrimitiveCount);
            }
        }

        [Fact]
        public void TheMeshBuilderEmitsNoGeometryForAnUnmeshableTile()
        {
            // 255 is legal and means "emit nothing". A map that is entirely 255 is therefore
            // meshable and produces an empty mesh - which is a different answer from "cannot
            // mesh", and the two must not be confused.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int i = 0; i < map.TerrainType.Length; i++)
            {
                map.TerrainType[i] = NeighbourhoodTerrain.Nothing;
            }

            string reason;
            Assert.True(NeighbourhoodCityMeshBuilder.CanMesh(map, out reason), reason);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            Assert.Equal(0, mesh.TotalPrimitives);
            Assert.Equal(0, mesh.TotalVertices);
        }

        [Fact]
        public void AMapOfTheWrongSizeIsRefusedWithAReason()
        {
            // The mesh builder indexes a 512-wide array with literal bounds, so a test-sized map is
            // not "a smaller city" - it is a wrong one. The generator produces those for tests, so
            // the refusal has to be a sentence a log can carry, not an exception.
            var small = NeighbourhoodMapGenerator.Generate(NeighbourhoodTestInput.FourFlatLots());

            string reason;
            Assert.False(NeighbourhoodCityMeshBuilder.CanMesh(small, out reason));
            Assert.Contains("512", reason);
            Assert.Null(NeighbourhoodCityMeshBuilder.TryBuild(small, out reason));
            Assert.NotNull(reason);
        }

        [Fact]
        public void EveryMeshQuadUsesTheLayerItsTerrainTypeNames()
        {
            // A map where the terrain types differ tile to tile, so the five buffers really are five
            // and not five empty lists with everything in one of them.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    var type = (byte)((x / 16 + y / 16) % NeighbourhoodCityMeshBuilder.LayerCount);
                    map.TerrainType[map.Index(x, y)] = type;
                }
            }

            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            for (int type = 0; type < NeighbourhoodCityMeshBuilder.LayerCount; type++)
            {
                Assert.True(mesh.Layers[type].PrimitiveCount > 0,
                    "terrain " + type + " has no geometry at all");
            }

            // CHANGED by the Part 9 TSO-alignment rework. This used to assert that *every* vertex
            // carries MaskTextureCoord (-1,-1), which was true only because the builder emitted no
            // blend faces at all. It now has to say the two things that are actually true and both
            // matter:
            //
            //  - a ground quad still carries (-1,-1). PixShader.fx's GetNCityColor reads
            //    TextureCoords.z >= -0.5 as "this quad has a blend mask" (PixShader.fx:382-384), so
            //    (-1,-1) is the shader's "no mask" sentinel and the ground must keep it or every
            //    grass tile would sample a mask.
            //  - a blend face carries a real cell of the 7x3 atlas. The old assertion would have
            //    rejected exactly the quads the cross-fades need, so it is replaced rather than
            //    loosened: a blend UV is checked against the atlas's own extent.
            var groundVertices = 0;
            var blendVertices = 0;
            var cell = NeighbourhoodBlendFlags.CellSize;
            foreach (var layer in mesh.Layers)
            {
                foreach (var vert in layer.Vertices)
                {
                    if (vert.MaskTextureCoord.X < -0.5f)
                    {
                        groundVertices++;
                        //the ground sentinel is exactly (-1,-1), not merely "negative"
                        Assert.Equal(-1f, vert.MaskTextureCoord.X);
                        Assert.Equal(-1f, vert.MaskTextureCoord.Y);
                        continue;
                    }
                    blendVertices++;
                    Assert.InRange(vert.MaskTextureCoord.X, 0f, 1f);
                    Assert.InRange(vert.MaskTextureCoord.Y, 0f, 1f);
                    //and inside the 7x3 grid, which is what catches a remap that lands outside it
                    var cx = vert.MaskTextureCoord.X / cell.X;
                    var cy = vert.MaskTextureCoord.Y / cell.Y;
                    Assert.InRange(cx, 0f, NeighbourhoodBlendFlags.AtlasWidth);
                    Assert.InRange(cy, 0f, NeighbourhoodBlendFlags.AtlasHeight);
                }
            }
            Assert.True(groundVertices > 0, "the map has no ground quads at all");
            // This map's terrain type is (x/16 + y/16) % 5, so every 16x16 block is uniform and
            // every interior tile borders a different terrain: there are plenty of masks to draw.
            Assert.True(blendVertices > 0,
                "a map with five terrains in 16x16 blocks should cross-fade at every block border,"
                + " and no blend face was emitted");
        }

        [Fact]
        public void VerticesAreScaledAndPositionedAsTheCityScalesThem()
        {
            // The three conversions the whole pipeline shares with TSO's city geometry, and the
            // ones a native writer has to get exactly right or every house floats or sinks:
            // elevation is divided by twelve, normals by six, and one texture repeat covers four
            // tiles.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            // grass everywhere, height 24 - a flat neighbourhood, so the only interesting vertex
            // is the one that is not the others
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 24;
            // a 7-tile step, so the normals have something to be
            for (int y = 0; y < 64; y++)
            {
                for (int x = 256; x < 320; x++) map.ElevationData[map.Index(x, y)] = 31;
            }

            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var verts = mesh.Layers[NeighbourhoodTerrain.Grass].Vertices;
            Assert.NotEmpty(verts);

            foreach (var vert in verts)
            {
                Assert.Equal(vert.Position.Y, (float)Math.Floor(vert.Position.Y * 1000) / 1000f, 3);
                Assert.InRange(vert.Position.Y, 0f, 255f / 12f);
                // flat ground means straight up
                Assert.True(vert.Normal.Y > 0.9f, "flat ground should have an upward normal, got "
                    + vert.Normal);
            }

            // the uv divisor: four tiles per repeat
            foreach (var vert in verts)
            {
                var repeats = vert.TextureCoord * NeighbourhoodCityMeshBuilder.TilesPerTextureRepeat;
                Assert.InRange(repeats.X, 0f, NeighbourhoodCityMeshBuilder.MapSize);
                Assert.InRange(repeats.Y, 0f, NeighbourhoodCityMeshBuilder.MapSize);
            }
        }

        [Fact]
        public void TheFadeSkirtFadesOutAndTheInteriorDoesNot()
        {
            // Ten tiles past the diamond edge the ground is fully transparent, on the edge it is
            // fully opaque, and in between it is a gradient. This is what stops the neighbourhood
            // ending in a cliff, and it is also why the pixel shader is put on the fog pass: the
            // plain no-shadow pass ignores the per-vertex transparency entirely.
            Assert.Equal(0f, NeighbourhoodCityMeshBuilder.FadeTransparency(100, 200, 150));
            Assert.Equal(1f, NeighbourhoodCityMeshBuilder.FadeTransparency(100, 200, 100 - NeighbourhoodCityMeshBuilder.FadeRange));
            Assert.Equal(1f, NeighbourhoodCityMeshBuilder.FadeTransparency(100, 200, 200 + NeighbourhoodCityMeshBuilder.FadeRange));

            var partial = NeighbourhoodCityMeshBuilder.FadeTransparency(100, 200, 100 - 5);
            Assert.InRange(partial, 0f, 1f);
            Assert.NotEqual(0f, partial);
        }

        [Fact]
        public void TheTerrainIsOpaqueInsideTheDiamondWhichIsWhatTheShaderReads()
        {
            // The blank screen, as arithmetic.
            //
            // PixShader.fx's Fog finishes with
            //     BCol.w *= (1.0 - Input.NormalTrans.w);
            //     BCol.xyz = lerp(BCol.xyz, FogColor.xyz, fogDistance) * BCol.w;
            // so the vertex's Transparency slot is a *transparency*: zero is opaque, one is
            // invisible. FSO's own TLayerVertex says so on the field ("0 is opaque") and
            // CityGeometry writes clamp(outside * fR, 0, 1) into it.
            //
            // This mesh builder used to write the complement - 1.0 for solid ground, 0.0 in the
            // fade skirt. Every terrain fragment then came out of the shader with alpha zero and
            // black, blended away to nothing, and the player saw a blank screen while the log said
            // "315424 terrain triangles". A triangle count cannot see that; these two assertions
            // can, and they are the reason the field bug is not a field bug any more.
            var map = GeneratedFullMap();
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);

            foreach (var layer in mesh.Layers)
            {
                foreach (var vertex in layer.Vertices)
                {
                    //inside the diamond the ground is fully opaque, i.e. transparency 0
                    var insideDiamond = NeighbourhoodCityMeshBuilder.IsInsideDiamond(
                        (int)Math.Floor(vertex.Position.X), (int)Math.Floor(vertex.Position.Z));
                    if (!insideDiamond) continue;
                    Assert.Equal(0f, vertex.Transparency, 3);
                }
            }

            //and the transparency is a genuine fade, not a step: some vertices are between the two
            var skirted = 0;
            var opaque = 0;
            foreach (var layer in mesh.Layers)
            {
                foreach (var vertex in layer.Vertices)
                {
                    if (vertex.Transparency <= 0.001f) { opaque++; continue; }
                    if (vertex.Transparency < 0.999f) skirted++;
                }
            }
            Assert.True(opaque > 0, "no vertex in the mesh is opaque, so the whole map is invisible");
            Assert.True(skirted > 0, "no vertex is partially transparent, so the map edge is a cliff"
                + " rather than a fade");
        }

        [Fact]
        public void TheRoadQuadsAreOpaqueToo()
        {
            // Roads go through the same Fog function as the ground, so the same inverted field
            //would have made the streets invisible even with the ground fixed. A road only exists
            //strictly inside the diamond, so its transparency is zero everywhere - which is also
            // what CityGeometry gets by never assigning the field at all.
            var map = UniformMapWithRoads();
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);

            Assert.True(mesh.Roads.Vertices.Length > 0, "this map should have road geometry");
            foreach (var vertex in mesh.Roads.Vertices)
            {
                Assert.Equal(0f, vertex.Transparency, 3);
            }
        }

        [Fact]
        public void AMeshingFailureIsReportedNotThrown()
        {
            // The panel's fallback path depends on this: a map it cannot mesh has to come back as
            // null plus a sentence, because the alternative is an exception on the game thread
            // while a neighbourhood screen is being built.
            string reason;
            Assert.Null(NeighbourhoodCityMeshBuilder.TryBuild(null, out reason));
            Assert.Contains("no generated map", reason);

            var map = NeighbourhoodMap.Create(64);
            Assert.Null(NeighbourhoodCityMeshBuilder.TryBuild(map, out reason));
            Assert.Contains("64x64", reason);
        }

        [Fact]
        public void TheMeanGroundHeightIsTheStoredElevationOnTheCityScale()
        {
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;

            Assert.Equal(4f, NeighbourhoodCityMeshBuilder.MeanGroundHeight(map), 4);
            Assert.Equal(0f, NeighbourhoodCityMeshBuilder.MeanGroundHeight(null));
        }

        [Fact]
        public void TheElevationScaleIsOneTileIsTwelveBytesAndOneArryUnitIsThreeSixteenths()
        {
            // The vertical contract, as arithmetic, so that a change to any of the four numbers
            // fails here rather than as a neighbourhood whose hills are five times too steep.
            //
            // The three facts it composes, none of which is Simitone's to choose:
            //   * the city mesh divides an ElevationData byte by twelve (CityGeometry.RegenMeshVerts,
            //     Terrain's GetElevationVert), so one tile is twelve bytes;
            //   * Blueprint.TerrainFactor is 3/160, and Blueprint.InterpAltitude returns altitude
            //     units already multiplied by it, so one altitude unit is 3/160 of a tile;
            //   * VMTS1Activator sets Blueprint.Altitude = ARRY(0) * 10, so there are ten altitude
            //     units - and therefore ten * 3/160 = 3/16 of a tile - per ARRY(0) unit.
            //
            // The factor of ten is the whole trap. Compare one ARRY(0) unit with one altitude unit
            // and the two scales look 4.44x apart (3/160 against 1/12); compare one ARRY(0) unit
            // with a whole tile, which is what a lot's base height actually is, and they are 5.33x
            // apart (1/12 against 3/16). Only the second is a bug anyone can see.
            Assert.Equal(3f / 160f, NeighbourhoodCityMeshBuilder.LotTerrainFactor, 6);
            Assert.Equal(10f, NeighbourhoodCityMeshBuilder.LotAltitudeUnitsPerArryUnit);
            Assert.Equal(NeighbourhoodCityMeshBuilder.LotTerrainFactor
                * NeighbourhoodCityMeshBuilder.LotAltitudeUnitsPerArryUnit,
                NeighbourhoodCityMeshBuilder.TilesPerArryUnit, 6);
            Assert.Equal(0.1875f, NeighbourhoodCityMeshBuilder.TilesPerArryUnit, 6);
            Assert.Equal(16f / 3f, NeighbourhoodCityMeshBuilder.ArryUnitsPerTile, 5);

            // and the bridge into the map's own units
            Assert.Equal(12f, NeighbourhoodCityMeshBuilder.ElevationScale);
            Assert.Equal(9f / 4f, NeighbourhoodCityMeshBuilder.ElevationUnitsPerArryUnit, 6);
            Assert.Equal(2.25f, NeighbourhoodCityMeshBuilder.ElevationUnitsPerArryUnit, 6);
            // one tile is twelve bytes and twelve/2.25 ARRY units, or the two have drifted apart
            Assert.Equal(NeighbourhoodCityMeshBuilder.ElevationScale
                / NeighbourhoodCityMeshBuilder.ElevationUnitsPerArryUnit,
                NeighbourhoodCityMeshBuilder.ArryUnitsPerTile, 4);
        }

        [Fact]
        public void WorldHeightForIsTheOnlyByteToTileConversion()
        {
            // The mesh, the facade placement and the layout-to-map seam all route through this one
            // function, and the adapter derives its own constant from the same place - so there is
            // nothing left that can disagree about how tall a byte is.
            Assert.Equal(0f, NeighbourhoodCityMeshBuilder.WorldHeightFor(0), 5);
            Assert.Equal(1f, NeighbourhoodCityMeshBuilder.WorldHeightFor(12), 5);
            Assert.Equal(4f, NeighbourhoodCityMeshBuilder.WorldHeightFor(48), 5);
            Assert.Equal(21.25f, NeighbourhoodCityMeshBuilder.WorldHeightFor(255), 4);
            Assert.Equal(NeighbourhoodCityMeshBuilder.MaxChunkHeight,
                NeighbourhoodCityMeshBuilder.WorldHeightFor(255), 4);

            // a lot's own ARRY(0) altitude, on the same scale
            Assert.Equal(0f, NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(0), 5);
            Assert.Equal(0.1875f, NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(1), 5);
            Assert.Equal(3.75f, NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(20), 4);

            // NeighbourhoodMapAdapter derives rather than restates, which is the property that keeps
            // a second spelling of the divisor from appearing
            Assert.Equal(12, NeighbourhoodMapAdapter.ElevationUnitsPerTile);
            for (int i = 0; i <= byte.MaxValue; i++)
            {
                //an int counter, deliberately: a byte one would wrap at 255 and never stop
                var b = (byte)i;
                Assert.Equal(NeighbourhoodCityMeshBuilder.WorldHeightFor(b),
                    NeighbourhoodMapAdapter.DequantiseAltitude(b), 5);
            }
        }

        [Fact]
        public void TheMapToWorldConversionRoundTripsThroughTheElevationByte()
        {
            // An ARRY(0) altitude quantised onto the map and read back as world height has to come
            // out where the lot view would put it: three sixteenths of a tile per unit, not a tile.
            for (short arry = 0; arry <= 100; arry++)
            {
                var bytes = NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(arry);
                var fromMap = NeighbourhoodCityMeshBuilder.WorldHeightFor(bytes);
                var fromLot = NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(arry);
                // one elevation byte is 1/12 of a tile, so the round trip cannot be better than that
                Assert.True(Math.Abs(fromMap - fromLot) <= 1f / NeighbourhoodCityMeshBuilder.ElevationScale + 1e-4f,
                    "ARRY(0) " + arry + " is " + fromLot + " tiles in the lot view and " + fromMap
                    + " tiles through the map");
            }

            // clamped rather than wrapped: a byte cannot hold a 40-tile cliff
            Assert.Equal(0, NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(-5));
            Assert.Equal(255, NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(200));
            Assert.Equal(255, NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(short.MaxValue));
            // and one unit is 2.25 bytes, so four units are nine
            Assert.Equal((byte)9, NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(4));
        }

        [Fact]
        public void TheFacadeGroundIsTheMeshVertexHeightForTheSameByte()
        {
            // The agreement, stated as a test rather than as a promise in a comment. A facade's base
            // height and the ground quad under it are converted from one byte by one function, so
            // there is no arithmetic left in which they could drift - and if this ever fails, houses
            // float or sink by the amount the two conversions disagree about.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;
            map.ElevationData[map.Index(200, 300)] = 137;

            var lot = new NeighbourhoodLot
            {
                Number = 11,
                Zone = LotZoning.ZoneResidential,
                Size = 32,
                WorldX = 200,
                WorldY = 300,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HasHouse = true
            };

            var ground = NeighbourhoodFacadeTransform.GroundHeightFor(map, lot);
            Assert.Equal(NeighbourhoodCityMeshBuilder.WorldHeightFor(
                map.ElevationData[map.Index(200, 300)]), ground, 5);
            // the same byte through the layout seam's own inverse, which derives from the same place
            Assert.Equal(ground, NeighbourhoodMapAdapter.DequantiseAltitude(137), 5);
            // and the vertex that stands there is at that height too: WorldHeightFor is what the
            // mesh's AddQuad is given, so there is no second conversion to compare against
            Assert.Equal(137f / NeighbourhoodCityMeshBuilder.ElevationScale, ground, 5);

            // the facade's other end: a raw ARRY(0) altitude, three sixteenths of a tile per unit,
            // not a whole tile
            Assert.Equal(0.75f, NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(lot.BaseHeight), 5);
        }

        [Fact]
        public void BuildingTheSameMapTwiceGivesTheSameMesh()
        {
            // The mesh is derived from the map and the clip, with no clock, no environment and no
            // hash code in it - so a regenerated world is byte-identical and can be thrown away
            // and rebuilt rather than stored, which is the property the sidecar's whole scheme
            // rests on.
            var first = NeighbourhoodCityMeshBuilder.Build(GeneratedFullMap());
            var second = NeighbourhoodCityMeshBuilder.Build(GeneratedFullMap());

            Assert.Equal(first.TotalPrimitives, second.TotalPrimitives);
            Assert.Equal(first.TotalVertices, second.TotalVertices);

            var a = first.Layers[NeighbourhoodTerrain.Grass].Vertices;
            var b = second.Layers[NeighbourhoodTerrain.Grass].Vertices;
            Assert.Equal(a.Length, b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                Assert.Equal(a[i].Position, b[i].Position);
                Assert.Equal(a[i].TextureCoord, b[i].TextureCoord);
            }
        }
    }

}
