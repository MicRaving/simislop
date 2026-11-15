/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using FSO.Files.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The decisions the generated neighbourhood's draw makes, asserted without a GPU.
    ///
    /// <para>
    /// The draw itself cannot be tested - a fake <c>GraphicsDevice</c> proves nothing about a
    /// shader and a real one is not available to a test. What can be tested is everything the
    /// draw <em>decides</em> before it touches the device, and that is where this renderer has
    /// actually gone wrong:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     which tiles become road quads, and which road nibble names which atlas cell;
    ///   </description></item>
    ///   <item><description>
    ///     the isometric clip - which tiles are inside it, and therefore drawn, and which are
    ///     outside it and therefore silently discarded along with the streets on them;
    ///   </description></item>
    ///   <item><description>
    ///     the layer selection - which terrain type goes in which buffer;
    ///   </description></item>
    ///   <item><description>
    ///     the camera cull - which chunks a tile rectangle touches, and how those chunks
    ///     coalesce back into index ranges that cover exactly them and nothing else;
    ///   </description></item>
    ///   <item><description>
    ///     the ground texture's tiling, which is a property of the noise function and is visible
    ///     on screen as a grid of seams one repeat apart;
    ///   </description></item>
    ///   <item><description>
    ///     the tree budget, which is a plan over a map and nothing else.
    ///   </description></item>
    /// </list>
    /// </summary>
    public class NeighbourhoodRenderDecisionTests
    {
        /// <summary>A full-size map with a single terrain and a single road nibble everywhere.</summary>
        private static NeighbourhoodMap UniformMap(byte terrain, byte roadNibble, byte cornerNibble)
        {
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    var i = map.Index(x, y);
                    map.TerrainType[i] = terrain;
                    map.RoadData[i] = (byte)((cornerNibble << 4) | roadNibble);
                }
            }
            return map;
        }

        private static int CountInsideDiamond(NeighbourhoodMap map, Func<int, int, bool> tileWanted)
        {
            var count = 0;
            for (int y = 0; y < NeighbourhoodCityMeshBuilder.MapSize; y++)
            {
                for (int x = 0; x < NeighbourhoodCityMeshBuilder.MapSize; x++)
                {
                    if (!NeighbourhoodCityMeshBuilder.IsInsideDiamond(x, y)) continue;
                    if (tileWanted(x, y)) count++;
                }
            }
            return count;
        }

        // ---------------------------------------------------------------- ground textures

        [Fact]
        public void EveryGroundTextureTilesOnBothAxes()
        {
            // The mesh repeats a ground texture every four tiles and a 512-tile map has 128 repeats
            // across it. A texture whose opposite edges disagree therefore draws a hard seam 128
            // times, which is exactly the "graphical glitches" a flat-looking neighbourhood gets
            // reported as - and it is invisible in a screenshot of a single tile, so it has to be
            // asserted rather than looked for.
            for (byte type = 0; type < NeighbourhoodTerrainTextures.Count; type++)
            {
                for (int y = 0; y < NeighbourhoodTerrainTextures.Size; y++)
                {
                    Assert.Equal(
                        NeighbourhoodTerrainTextures.NoiseAt(0, y, type),
                        NeighbourhoodTerrainTextures.NoiseAt(NeighbourhoodTerrainTextures.Size, y, type));
                }
                for (int x = 0; x < NeighbourhoodTerrainTextures.Size; x++)
                {
                    Assert.Equal(
                        NeighbourhoodTerrainTextures.NoiseAt(x, 0, type),
                        NeighbourhoodTerrainTextures.NoiseAt(x, NeighbourhoodTerrainTextures.Size, type));
                }
            }
        }

        [Fact]
        public void TheGroundTexturesTileAtTheCornersToo()
        {
            //the diagonal case, which the two axis tests do not reach
            for (byte type = 0; type < NeighbourhoodTerrainTextures.Count; type++)
            {
                var size = NeighbourhoodTerrainTextures.Size;
                Assert.Equal(
                    NeighbourhoodTerrainTextures.NoiseAt(0, 0, type),
                    NeighbourhoodTerrainTextures.NoiseAt(size, size, type));
                Assert.Equal(
                    NeighbourhoodTerrainTextures.NoiseAt(size, 0, type),
                    NeighbourhoodTerrainTextures.NoiseAt(0, size, type));
            }
        }

        [Fact]
        public void TheGroundTexturesAreDeterministicOpaqueAndNotFlat()
        {
            // the tiling fix must not have flattened them: a constant lattice is periodic too
            for (byte type = 0; type < NeighbourhoodTerrainTextures.Count; type++)
            {
                var first = NeighbourhoodTerrainTextures.PixelsFor(type);
                var second = NeighbourhoodTerrainTextures.PixelsFor(type);
                Assert.Equal(first.Length, second.Length);
                Assert.Equal(NeighbourhoodTerrainTextures.Size * NeighbourhoodTerrainTextures.Size, first.Length);

                var distinct = new HashSet<Color>();
                for (int i = 0; i < first.Length; i++)
                {
                    Assert.Equal(first[i], second[i]);
                    Assert.Equal((byte)255, first[i].A);
                    distinct.Add(first[i]);
                }
                Assert.True(distinct.Count > 4,
                    "terrain " + type + " has only " + distinct.Count + " distinct pixels, so the noise is not doing anything");
            }
        }

        // ---------------------------------------------------------------- the diamond clip

        [Fact]
        public void TheDiamondClipKeepsLessThanTheWholeSquare()
        {
            // §0 decision 2 accepts the isometric diamond rather than forking CityGeometry, so the
            // drawn world is a kite inside the 512x512 square. That is a decision, and this is its
            // cost stated as a number: anything laid out over the square rather than over the
            // diamond - the packer, the road graph - loses this much of the map without being told.
            var span = NeighbourhoodCityMeshProbe.AnalyticDiamond();
            var square = (long)NeighbourhoodCityMeshBuilder.MapSize * NeighbourhoodCityMeshBuilder.MapSize;

            Assert.True(span.Tiles < square);
            Assert.InRange(span.Tiles, square / 4, square);
            Assert.Equal(NeighbourhoodCityMeshBuilder.MapSize, span.Rows);
        }

        [Fact]
        public void TheDiamondPredicateAgreesWithTheClipTheBuilderWalks()
        {
            // One definition, so the road pass, the tree walk and the report cannot drift apart.
            // The rule is the city's own "j > rXS && j < rXE", where rXE is *one past* the last
            // tile: so the row's first tile is outside (it gets ground from the clip but no road)
            // and its last tile is inside.
            for (int row = 0; row < NeighbourhoodCityMeshBuilder.MapSize; row += 7)
            {
                var left = NeighbourhoodCityMeshBuilder.DiamondLeft(row);
                var right = NeighbourhoodCityMeshBuilder.DiamondRight(row);
                //rows above and below the inset square (audit O1) are outside along their whole
                //span: they carry the vertical fade skirt and nothing else
                if (row <= left || row >= right)
                {
                    for (int x = left; x <= right; x++)
                    {
                        Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(x, row));
                    }
                    continue;
                }
                Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(left, row),
                    "row " + row + "'s first tile is inside the diamond, which is one tile too far");
                Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(left - 1, row));
                Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(right, row));

                var span = right - left;
                if (span > 1)
                {
                    Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(left + 1, row));
                    Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(right - 1, row));
                }
                else
                {
                    // a one-tile row is entirely on the edge, so nothing in it carries a road
                    Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(left, row));
                }
            }

            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(0, 0));
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(0, -1));
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(0, NeighbourhoodCityMeshBuilder.MapSize));
        }

        [Fact]
        public void EveryChunkBoundsContainsItsOwnTilesAndTheFadeSkirt()
        {
            // The cull is only correct if the box it tests a chunk against is at least as large as
            // the chunk's geometry, and the geometry overhangs the chunk by the fade range on
            // both sides. Testing against the bare 16x16 drops the skirt and the map edge ends in
            // a hard line instead of fading.
            for (int chunk = 0; chunk < NeighbourhoodCityMeshBuilder.ChunkCount; chunk++)
            {
                var bounds = NeighbourhoodCityMeshBuilder.ChunkBounds(chunk);
                var cx = chunk % NeighbourhoodCityMeshBuilder.ChunksPerAxis;
                var cy = chunk / NeighbourhoodCityMeshBuilder.ChunksPerAxis;
                Assert.True(bounds.Contains(new Point(cx * NeighbourhoodCityMeshBuilder.ChunkSize,
                    cy * NeighbourhoodCityMeshBuilder.ChunkSize)));
                Assert.True(bounds.Contains(new Point(
                    (cx + 1) * NeighbourhoodCityMeshBuilder.ChunkSize - 1,
                    (cy + 1) * NeighbourhoodCityMeshBuilder.ChunkSize - 1)));
                Assert.True(bounds.Left <= cx * NeighbourhoodCityMeshBuilder.ChunkSize
                    - NeighbourhoodCityMeshBuilder.FadeRange);
                Assert.True(bounds.Top <= cy * NeighbourhoodCityMeshBuilder.ChunkSize
                    - NeighbourhoodCityMeshBuilder.FadeRange);
            }
        }

        // ---------------------------------------------------------------- which tiles become road quads

        [Fact]
        public void OnlyRoadBearingTilesStrictlyInsideTheDiamondBecomeRoadQuads()
        {
            // the whole square is one straight road, so the only question is the clip. A quad count
            // that matches an independent tile count is the assertion; anything less means streets
            // were dropped for being a tile out, and anything more means they were drawn over the
            // fade skirt.
            var map = UniformMap(NeighbourhoodTerrain.Grass, RoadMask.MaskBits, 0);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map, out var report);

            var expected = CountInsideDiamond(map, (x, y) => true);
            Assert.Equal(expected * 2, mesh.Roads.PrimitiveCount);
            Assert.Equal(expected, report.RoadQuads);
            Assert.Equal(expected * 4, mesh.Roads.Vertices.Length);
        }

        [Fact]
        public void ARoadlessMapEmitsNoRoadGeometryAtAll()
        {
            // the question a screenshot cannot answer: is the road pass missing, or is the atlas
            // missing, or is there simply nothing to draw. This is the "nothing" case.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map, out var report);

            Assert.Equal(0, mesh.Roads.PrimitiveCount);
            Assert.Empty(mesh.Roads.Vertices ?? new NeighbourhoodTerrainVertex[0]);
            Assert.Equal(0, report.RoadQuads);
            Assert.Equal(0, report.CornerQuads);
            Assert.Empty(report.RoadCells);
            Assert.True(mesh.TotalPrimitives > 0, "the ground should still be there");
        }

        [Fact]
        public void AStraightNibbleAndACornerNibbleOnOneTileMakeTwoQuads()
        {
            // A junction is a turn drawn over a through-road, which is two quads on one tile: the
            // low nibble's straight cell and the high nibble's corner cell. Collapsing them into
            // one would lose every T-junction in the neighbourhood, and the count is how that
            // shows up.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            var only = 0;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    map.TerrainType[map.Index(x, y)] = NeighbourhoodTerrain.Grass;
                    if (x == 256 && y == 256)
                    {
                        map.RoadData[map.Index(x, y)] = (byte)((7 << 4) | 5); // corner 7, straight 5
                        only = map.Index(x, y);
                    }
                }
            }

            var mesh = NeighbourhoodCityMeshBuilder.Build(map, out var report);
            Assert.Equal(2 * 2, mesh.Roads.PrimitiveCount);
            Assert.Equal(1, report.RoadQuads);
            Assert.Equal(1, report.CornerQuads);
            Assert.Equal(only, map.Index(256, 256));

            // and the two quads sample two different cells of the atlas, which is the whole point
            Assert.Contains(NeighbourhoodRoadLayout.CellFor(5, false), report.RoadCells);
            Assert.Contains(NeighbourhoodRoadLayout.CellFor(7, true), report.RoadCells);
        }

        [Fact]
        public void EveryRoadQuadSamplesACellTheAtlasActuallyHas()
        {
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int i = 0; i < map.RoadData.Length; i++)
            {
                // every mask in both nibbles, so every table entry is reached
                map.RoadData[i] = (byte)((i & 15) << 4 | ((i >> 4) & 15));
                map.TerrainType[i] = NeighbourhoodTerrain.Grass;
            }

            var mesh = NeighbourhoodCityMeshBuilder.Build(map, out var report);
            Assert.True(report.RoadQuads > 0);
            foreach (var cell in report.RoadCells)
            {
                Assert.True(NeighbourhoodRoadLayout.IsRealCell(cell),
                    "a road quad samples atlas cell " + cell + ", which does not exist");
            }

            // and every sampled uv lies inside exactly one cell of the 8x4 atlas
            foreach (var vertex in mesh.Roads.Vertices)
            {
                var u = vertex.TextureCoord.X;
                var v = vertex.TextureCoord.Y;
                Assert.InRange(u, 0f, 1f);
                Assert.InRange(v, 0f, 1f);
                Assert.InRange(u / NeighbourhoodRoadLayout.CellSize.X, 0f, NeighbourhoodRoadLayout.Width);
                Assert.InRange(v / NeighbourhoodRoadLayout.CellSize.Y, 0f, NeighbourhoodRoadLayout.Height);
            }
        }

        // ---------------------------------------------------------------- the layer selection

        [Fact]
        public void EachTerrainTypeGoesToItsOwnLayerBuffer()
        {
            // The five buffers exist because the five textures do, and a terrain type is a raw
            // byte used as a layer index. This is the assertion that the index and the buffer
            // still agree after a change to either.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    map.TerrainType[map.Index(x, y)] =
                        (byte)((x / 16 + y / 16) % NeighbourhoodCityMeshBuilder.LayerCount);
                }
            }

            var mesh = NeighbourhoodCityMeshBuilder.Build(map, out var report);
            var total = 0;
            for (int type = 0; type < NeighbourhoodCityMeshBuilder.LayerCount; type++)
            {
                Assert.True(mesh.Layers[type].PrimitiveCount > 0, "terrain " + type + " has no geometry");
                total += mesh.Layers[type].PrimitiveCount;
                // CHANGED by the Part 9 TSO-alignment rework. A layer buffer holds its own ground
                // *and* every blend face other tiles sent into it, because that is where TSO puts a
                // cross-fade (CityGeometry.RegenMeshVerts :309-352 - the blend quad goes into
                // vertices[blendT], the neighbour's layer, not the tile's own). So the count is
                // ground plus blends, and both are reported per layer.
                Assert.Equal((report.LayerQuads[type] + report.BlendQuadsPerLayer[type]) * 2,
                    mesh.Layers[type].PrimitiveCount);
            }
            Assert.Equal((report.TotalLayerQuads + report.BlendQuads) * 2, total);
        }

        [Fact]
        public void TheReportAgreesWithTheBuilderAboutWhatWasEmitted()
        {
            // The report is what the log prints, so a report that disagrees with the mesh is worse
            // than no report. Two independent walks: the builder's chunk loop and the report's own.
            var lots = new List<NeighbourhoodLotInput>();
            short n = 1;
            for (int gy = 0; gy < 4; gy++)
            {
                for (int gx = 0; gx < 4; gx++)
                {
                    lots.Add(NeighbourhoodTestInput.Lot(n++, (short)(40 + gx * 96), (short)(40 + gy * 96),
                        30, 48, (byte)(gx % 2 == 0 ? NeighbourhoodDoorSide.East : NeighbourhoodDoorSide.West)));
                }
            }
            var map = NeighbourhoodMapGenerator.Generate(new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, 512, lots));

            var mesh = NeighbourhoodCityMeshBuilder.Build(map, out var report);
            Assert.True(report.EmissionMatchesProbe,
                "the report's expectations and the builder's own counts disagree: " + report);

            var emittedTotal = 0;
            for (int i = 0; i < mesh.Layers.Length; i++) emittedTotal += mesh.Layers[i].PrimitiveCount;
            // CHANGED by the Part 9 TSO-alignment rework: ground plus the blend faces, for the same
            // reason as the test above - a cross-fade quad is emitted into a neighbour's layer.
            Assert.Equal((report.TotalLayerQuads + report.BlendQuads) * 2, emittedTotal);
            Assert.Equal(report.TotalRoadQuads * 2, mesh.Roads.PrimitiveCount);

            // and the independent probe agrees too
            var probe = NeighbourhoodCityMeshProbe.Probe(map);
            Assert.Equal(probe.RoadQuads, report.RoadQuads);
            Assert.Equal(probe.CornerQuads, report.CornerQuads);
            Assert.Equal(probe.VisitedTiles, report.TotalLayerQuads);
            //and about the blends, which are a third thing the mesh now emits and which neither of
            //the two counts above covers on its own
            Assert.Equal(probe.BlendQuads, report.BlendQuads);
            for (int i = 0; i < NeighbourhoodCityMeshBuilder.LayerCount; i++)
            {
                Assert.Equal(probe.BlendQuadsPerLayer[i], report.BlendQuadsPerLayer[i]);
            }
        }

        [Fact]
        public void TheReportSaysHowMuchOfTheRoadNetworkTheClipThrowsAway()
        {
            // The number that distinguishes "the generator drew no streets" from "the streets are
            // there and the clip ate them". On a square map with roads everywhere it is large,
            // and it has to be reported rather than discovered from a screenshot.
            var map = UniformMap(NeighbourhoodTerrain.Grass, RoadMask.MaskBits, 0);
            NeighbourhoodCityMeshBuilder.Build(map, out var report);

            Assert.Equal(NeighbourhoodCityMeshBuilder.MapSize * NeighbourhoodCityMeshBuilder.MapSize, report.MapRoadTiles);
            Assert.True(report.DiamondRoadBearingTiles > 0);
            Assert.True(report.ClippedRoadTiles > 0);
            Assert.Equal(report.MapRoadTiles, report.DiamondRoadBearingTiles + report.ClippedRoadTiles);
            Assert.InRange(report.RoadCoverage, 0f, 1f);
            //the inset square (audit O1): all but a ClipInset-wide border of the map
            var inner = NeighbourhoodCityMeshBuilder.DiamondRight(0) - NeighbourhoodCityMeshBuilder.DiamondLeft(0) - 1;
            Assert.Equal((long)inner * inner, report.DiamondRoadBearingTiles);
            Assert.InRange(report.DiamondCoverage, 0.85f, 0.99f);
            Assert.Contains("road tiles", report.ToString());
        }

        [Fact]
        public void AGeneratedNeighbourhoodDrawsRoadsAndTheReportShowsHowManySurvive()
        {
            // The end-to-end version: a real generated map has streets, they reach the mesh, and
            // the report accounts for every one of them.
            var lots = new List<NeighbourhoodLotInput>();
            short n = 1;
            for (int gy = 0; gy < 5; gy++)
            {
                for (int gx = 0; gx < 5; gx++)
                {
                    lots.Add(NeighbourhoodTestInput.Lot(n++, (short)(24 + gx * 90), (short)(24 + gy * 90),
                        30, 48, (byte)(gx % 2 == 0 ? NeighbourhoodDoorSide.East : NeighbourhoodDoorSide.West)));
                }
            }
            var map = NeighbourhoodMapGenerator.Generate(new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, 512, lots));
            var mesh = NeighbourhoodCityMeshBuilder.Build(map, out var report);

            Assert.True(report.MapRoadTiles > 0, "the generator drew no streets at all");
            Assert.True(report.RoadQuads > 0, "no street survived the clip");
            Assert.Equal((report.RoadQuads + report.CornerQuads) * 2, mesh.Roads.PrimitiveCount);
            Assert.NotEmpty(report.RoadCells);
            Assert.True(report.ToString().Length > 40, "the report has to say something in the log");
        }

        // ---------------------------------------------------------------- the camera cull

        [Fact]
        public void EveryLayerChunkIndexStartsArrayTilesItsOwnIndexBufferExactly()
        {
            // The per-chunk starts are what makes a partial draw possible, and a partial draw that
            // is off by one index is a corrupted frame rather than a wrong one. So: the array is
            // the whole buffer exactly once, in order, with nothing dropped and nothing doubled.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    map.TerrainType[map.Index(x, y)] =
                        (byte)((x / 16 + y / 16) % NeighbourhoodCityMeshBuilder.LayerCount);
                }
            }
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);

            for (int layer = 0; layer < NeighbourhoodCityMeshBuilder.LayerCount; layer++)
            {
                var starts = mesh.LayerChunkIndexStarts[layer];
                Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount + 1, starts.Length);
                Assert.Equal(0, starts[0]);
                Assert.Equal(mesh.Layers[layer].Indices.Length, starts[NeighbourhoodCityMeshBuilder.ChunkCount]);
                var total = 0;
                for (int chunk = 0; chunk < NeighbourhoodCityMeshBuilder.ChunkCount; chunk++)
                {
                    Assert.True(starts[chunk] <= starts[chunk + 1], "chunk starts go backwards");
                    total += starts[chunk + 1] - starts[chunk];
                }
                Assert.Equal(mesh.Layers[layer].Indices.Length, total);
            }

            var roadStarts = mesh.RoadChunkIndexStarts;
            Assert.Equal(0, roadStarts[0]);
            Assert.Equal(mesh.Roads.Indices.Length, roadStarts[NeighbourhoodCityMeshBuilder.ChunkCount]);
        }

        [Fact]
        public void ChunksInRegionFindsTheChunksATileRectangleTouches()
        {
            // the pure half of the cull, including the two cases that are easy to get wrong: a
            // rectangle hanging off the map, and one entirely off it
            var all = NeighbourhoodCityMeshBuilder.ChunksInRegion(
                new Rectangle(0, 0, NeighbourhoodCityMeshBuilder.MapSize, NeighbourhoodCityMeshBuilder.MapSize));
            Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount, all.Length);

            var one = NeighbourhoodCityMeshBuilder.ChunksInRegion(new Rectangle(0, 0, 1, 1));
            Assert.Single(one);
            Assert.Equal(0, one[0]);

            var corner = NeighbourhoodCityMeshBuilder.ChunksInRegion(new Rectangle(
                NeighbourhoodCityMeshBuilder.MapSize - 1, NeighbourhoodCityMeshBuilder.MapSize - 1, 1, 1));
            Assert.Single(corner);
            Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount - 1, corner[0]);

            var straddling = NeighbourhoodCityMeshBuilder.ChunksInRegion(new Rectangle(15, 15, 2, 2));
            Assert.Equal(4, straddling.Length);

            Assert.Empty(NeighbourhoodCityMeshBuilder.ChunksInRegion(new Rectangle(-100, 0, 10, 10)));
            Assert.Empty(NeighbourhoodCityMeshBuilder.ChunksInRegion(new Rectangle(0, -100, 10, 10)));
            Assert.Empty(NeighbourhoodCityMeshBuilder.ChunksInRegion(new Rectangle(
                NeighbourhoodCityMeshBuilder.MapSize + 10, 0, 10, 10)));

            // off the map, but overlapping it: clipped, not discarded
            var overhanging = NeighbourhoodCityMeshBuilder.ChunksInRegion(
                new Rectangle(-64, -64, 128, 128));
            Assert.Equal(16, overhanging.Length);
            Assert.Equal(0, overhanging[0]);

            // the ends' order does not matter
            var reversed = NeighbourhoodCityMeshBuilder.ChunksInRegion(new Rectangle(15, 15, 2, 2));
            var backwards = NeighbourhoodCityMeshBuilder.ChunksInRegion(new Rectangle(17, 17, -2, -2));
            Assert.Equal(reversed, backwards);
        }

        [Fact]
        public void VisibleDrawRangesCoverExactlyTheVisibleChunks()
        {
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var starts = mesh.LayerChunkIndexStarts[NeighbourhoodTerrain.Grass];

            var visible = new List<int>();
            for (int chunk = 0; chunk < NeighbourhoodCityMeshBuilder.ChunkCount; chunk += 7) visible.Add(chunk);
            // plus a run of consecutive ones, which is what a contiguous view produces. The set is
            // deduplicated first: the ranges are a set of chunks, and a caller that handed over
            // the same chunk twice would be asking about the union
            var distinct = new HashSet<int>(visible);
            for (int chunk = 100; chunk < 130; chunk++) distinct.Add(chunk);
            visible = new List<int>(distinct);
            visible.Sort();

            var ranges = new List<NeighbourhoodMeshDrawRange>();
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, visible, ranges);

            // A chunk the clip never reaches has no geometry in a layer, and a coalesced run
            // spans the empty chunks between two non-empty ones. So the invariant is containment
            // plus the total: every visible chunk that has geometry is inside a range, nothing
            // outside the visible set is, and the indices add up to exactly those chunks' worth.
            var withGeometry = new List<int>();
            foreach (var chunk in visible)
            {
                if (starts[chunk + 1] > starts[chunk]) withGeometry.Add(chunk);
            }
            Assert.NotEmpty(withGeometry);
            var visibleSet = new HashSet<int>(visible);

            var covered = new HashSet<int>();
            var indices = 0;
            foreach (var range in ranges)
            {
                Assert.True(range.IndexCount % 3 == 0, "a draw range must be whole triangles");
                indices += range.IndexCount;
                for (int chunk = range.FirstChunk; chunk < range.EndChunk; chunk++) covered.Add(chunk);
            }

            foreach (var chunk in withGeometry) Assert.Contains(chunk, covered);
            foreach (var chunk in covered) Assert.Contains(chunk, visibleSet);

            var expected = 0;
            foreach (var chunk in visible) expected += starts[chunk + 1] - starts[chunk];
            Assert.Equal(expected, indices);
        }

        [Fact]
        public void VisibleDrawRangesCoalesceWhatItCan()
        {
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var starts = mesh.LayerChunkIndexStarts[NeighbourhoodTerrain.Grass];

            // everything in one run is one draw call, which is what the full-screen view gets
            var everything = new List<int>();
            for (int chunk = 0; chunk < NeighbourhoodCityMeshBuilder.ChunkCount; chunk++) everything.Add(chunk);
            var ranges = new List<NeighbourhoodMeshDrawRange>();
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, everything, ranges);
            Assert.Single(ranges);
            Assert.Equal(0, ranges[0].FirstChunk);
            Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount, ranges[0].EndChunk);
            Assert.Equal(starts[NeighbourhoodCityMeshBuilder.ChunkCount], ranges[0].IndexCount);

            // one chunk in the middle is one draw call of just that chunk
            var one = new List<int> { 500 };
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, one, ranges);
            Assert.Single(ranges);
            Assert.Equal(500, ranges[0].FirstChunk);
            Assert.Equal(501, ranges[0].EndChunk);
            Assert.Equal(starts[501] - starts[500], ranges[0].IndexCount);

            // nothing visible submits nothing
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, new List<int>(), ranges);
            Assert.Empty(ranges);
        }

        [Fact]
        public void AFullScreenCameraKeepsEveryChunk()
        {
            // The cull's most dangerous property, and the one the field report turned out to depend
            // on: at full screen the frustum covers the whole map, so the cull must keep all 1024
            // chunks. A cull that removes anything at full screen is not a fast frame, it is a view
            // of most of a neighbourhood - and the log, which counted the mesh's triangles, said
            // 315424 of them were drawn.
            //
            // Both halves of the renderer: the whole-map shortcut it takes first, and the per-chunk
            // test behind it, so neither can be right while the other is wrong. The framing is
            // NeighCameraMachine.FullScreen()'s, from the map's centre.
            var side = NeighbourhoodCityMeshBuilder.MapSize;
            var placement = NeighbourhoodCameraPlacement.Framing(
                new Vector3(side / 2f, 0f, side / 2f),
                NeighbourhoodCameraPlacement.VisibleWidthFor(side),
                NeighbourhoodCameraPlacement.DefaultAspect);
            var frustum = placement.Frustum(1f, 8000f, NeighbourhoodCameraPlacement.DefaultAspect);
            Assert.NotEqual(ContainmentType.Disjoint,
                frustum.Contains(NeighbourhoodTerrainRenderer.WholeMapBounds()));

            //and so the renderer's whole-map shortcut would take, and the per-chunk cull agrees
            var visible = new List<int>();
            NeighbourhoodCityMeshBuilder.ChunksInFrustum(frustum, visible);
            Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount, visible.Count);
        }

        [Fact]
        public void ACameraLookingAwayFromTheMapKeepsNothing()
        {
            // The other half of the same property, because a cull that is too generous is a slow
            // frame and one that is too tight is a blank one. A camera pointed at a target well
            // outside the map has the map entirely behind it, so it keeps no chunks at all.
            //
            // Asserted on the chunk list rather than on frustum.Contains(the whole map) on purpose:
            // a far plane thousands of units out makes any frustum intersect the map's bounding box
            // somewhere along its length, however narrow it is at the near end. The chunk list is
            // what the draw actually submits, so that is the honest question.
            //
            // Asserted on the region rather than on frustum.Contains(the whole map) on purpose: a
            // far plane thousands of units out makes any frustum intersect the map's bounding box
            // somewhere along its length, however narrow it is at the near end. The region is what
            // the draw actually submits, so that is the honest question.
            var side = NeighbourhoodCityMeshBuilder.MapSize;
            var placement = NeighbourhoodCameraPlacement.Framing(
                new Vector3(side * 4f, 0f, side * 4f), 64f,
                NeighbourhoodCameraPlacement.DefaultAspect);
            var frustum = placement.Frustum(1f, 8000f, NeighbourhoodCameraPlacement.DefaultAspect);

            var visible = new List<int>();
            NeighbourhoodCityMeshBuilder.ChunksInFrustum(frustum, visible);
            Assert.Empty(visible);
        }

        [Fact]
        public void ACameraZoomedIntoTheMiddleCullsMostOfTheMap()
        {
            // And the cull actually culls: a lot-focused view must submit a fraction of the map.
            // This is the case the cull was written for, and it is also the one that proves the cull
            // is not simply "return everything".
            //
            // The bar is a quarter of the chunks, not a fiftieth, and the reason is worth stating
            // because it is a property of the camera rather than of the cull: a 45-degree yaw with
            // a far plane thousands of units out keeps a wedge that reaches a long way down the view
            // direction, and the ground at the far end of that wedge really is on screen. The
            // renderer additionally picks a coarser level of detail at this zoom, which is where the
            // rest of the saving comes from - see ACoarserLevelIsAQuarterOrASixteenthOfTheTriangles.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var starts = mesh.LayerChunkIndexStarts[NeighbourhoodTerrain.Grass];

            var placement = NeighbourhoodCameraPlacement.Framing(
                new Vector3(256f, 0f, 256f), 64f, NeighbourhoodCameraPlacement.DefaultAspect);
            var frustum = placement.Frustum(1f, 8000f, NeighbourhoodCameraPlacement.DefaultAspect);

            var visible = new List<int>();
            NeighbourhoodCityMeshBuilder.ChunksInFrustum(frustum, visible);
            Assert.NotEmpty(visible);

            //and the target's own chunk is among them, so "small" is not small in the wrong place.
            //Not a bounding box around the whole set: the camera's yaw is 45 degrees and its far
            //plane is thousands of units out, so the frustum legitimately reaches a long way down
            //the view direction and the chunks it keeps are a wedge, not a square.
            var targetChunk = (256 / NeighbourhoodCityMeshBuilder.ChunkSize) * NeighbourhoodCityMeshBuilder.ChunksPerAxis
                + (256 / NeighbourhoodCityMeshBuilder.ChunkSize);
            Assert.Contains(targetChunk, visible);

            //and the saving, counted over the chunks it kept rather than over the draw ranges.
            //The ranges coalesce consecutive chunks including the empty ones between them, so on a
            //wedge-shaped view they span more chunks than were kept; the sum of the kept chunks'
            //own index counts is the number the cull is actually responsible for.
            //
            //A quarter, not a tenth, and the reason is the frustum's shape rather than the cull's:
            //a 45-degree camera with a far plane thousands of units out keeps a wedge that reaches a
            //long way down the view direction, and the ground at the far end of that wedge is
            //genuinely on screen. The comparison that matters is against the alternative - this is
            //what a 64-tile view would cost with no culling at all - and against the 1024 chunks
            //the cull considered.
            var keptIndices = 0;
            foreach (var chunk in visible) keptIndices += starts[chunk + 1] - starts[chunk];
            var allIndices = starts[NeighbourhoodCityMeshBuilder.ChunkCount];
            Assert.True(keptIndices > 0, "the cull kept chunks but no geometry at all");
            Assert.True(keptIndices < allIndices / 2,
                "a 64-tile view still submits " + keptIndices + " of " + allIndices + " indices");
            Assert.True(visible.Count < NeighbourhoodCityMeshBuilder.ChunkCount / 2,
                "a 64-tile view still keeps " + visible.Count + " of "
                + NeighbourhoodCityMeshBuilder.ChunkCount + " chunks");
        }

        [Fact]
        public void TheWholeMapIsVisibleToAFullScreenCamera()
        {
            // The per-chunk cull on its own, with no shortcut in front of it: it must keep every
            // chunk when the camera frames the whole map. If this is ever false, the renderer's
            // whole-map shortcut is the only thing standing between a full-screen view and a
            // cropped one, which is a single point of failure for the guarantee.
            var side = NeighbourhoodCityMeshBuilder.MapSize;
            var placement = NeighbourhoodCameraPlacement.Framing(
                new Vector3(side / 2f, 0f, side / 2f),
                NeighbourhoodCameraPlacement.VisibleWidthFor(side),
                NeighbourhoodCameraPlacement.DefaultAspect);
            var frustum = placement.Frustum(1f, 8000f, NeighbourhoodCameraPlacement.DefaultAspect);

            var visible = new List<int>();
            NeighbourhoodCityMeshBuilder.ChunksInFrustum(frustum, visible);
            Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount, visible.Count);
        }

        [Fact]
        public void CullingASmallRegionSubmitsFarLessThanTheWholeMap()
        {
            // The reason the cull exists, as a number: a view a few chunks across should not be
            // paying for a quarter of a million triangles.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var starts = mesh.LayerChunkIndexStarts[NeighbourhoodTerrain.Grass];

            var near = NeighbourhoodCityMeshBuilder.ChunksInRegion(new Rectangle(240, 240, 64, 64));
            var whole = new List<int>();
            for (int chunk = 0; chunk < NeighbourhoodCityMeshBuilder.ChunkCount; chunk++) whole.Add(chunk);

            var nearRanges = new List<NeighbourhoodMeshDrawRange>();
            var wholeRanges = new List<NeighbourhoodMeshDrawRange>();
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, near, nearRanges);
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, whole, wholeRanges);

            var nearIndices = 0;
            foreach (var range in nearRanges) nearIndices += range.IndexCount;
            var wholeIndices = wholeRanges[0].IndexCount;

            Assert.True(nearIndices < wholeIndices / 10,
                "a 64x64 tile view still submits " + nearIndices + " of " + wholeIndices + " indices");
            Assert.True(wholeIndices > 0);
        }

        // ------------------------------------------------------------ the level of detail

        [Fact]
        public void ZoomedOutDrawsACoarserLevelAndZoomedInDrawsTheFullOne()
        {
            // The cost half of the fix. 315424 triangles a frame at full screen is a lot for one
            // draw call, and the cull removes nothing there because everything is in frame, so the
            // level of detail is what has to carry it. The rule is in tiles so it needs no device,
            // which is the only reason it can be asserted at all.
            var side = NeighbourhoodCityMeshBuilder.MapSize;

            //the whole map framed: the coarsest level that still spans MinTilesAcross tiles
            var full = NeighbourhoodCameraPlacement.VisibleWidthFor(side);
            Assert.Equal(NeighbourhoodMeshLod.Quarter,
                NeighbourhoodMeshLod.ForTilesAcross(full).Factor);
            //and the level rule is what picks it because the view really is that wide
            Assert.True(full / NeighbourhoodMeshLod.Quarter >= NeighbourhoodMeshLod.MinTilesAcross);

            //a lot focused at the ordinary 1.5x zoom. The zoom constant is spelled out rather than
            //taken from NeighCameraMachine so this file does not have to reach into the UI panel for
            //a number the mesh builder has no business knowing.
            Assert.Equal(NeighbourhoodMeshLod.Half,
                NeighbourhoodMeshLod.ForTilesAcross(full / 1.5f).Factor);

            //and hard in on a lot: the full-detail level
            Assert.True(NeighbourhoodMeshLod.ForTilesAcross(48f).IsFull);
        }

        [Fact]
        public void ANonsenseOrTinyViewAlwaysGetsTheFullDetailLevel()
        {
            // The direction that matters. A camera looking at a fraction of a tile, or at a
            // non-finite width, must never select a coarse mesh: the seams between coarse blocks
            // are exactly what a close-up view is made of, so the fine level is the safe answer for
            // every input the rule cannot interpret.
            foreach (var tiles in new[] { 0f, -1f, float.NaN, float.PositiveInfinity, 1f, 8f })
            {
                Assert.True(NeighbourhoodMeshLod.ForTilesAcross(tiles).IsFull,
                    "a " + tiles + "-tile view was given a coarse level");
            }
        }

        [Fact]
        public void TheLevelRuleIsMonotonicInTheZoom()
        {
            // Zooming in never picks a coarser level than zooming out did. A rule that could go back
            // the other way would pop between levels as the camera tweened, which is the one thing
            // a level of detail must never do.
            var previous = NeighbourhoodMeshLod.Quarter;
            for (float tiles = 512f; tiles >= 8f; tiles -= 4f)
            {
                var factor = NeighbourhoodMeshLod.ForTilesAcross(tiles).Factor;
                Assert.True(factor <= previous,
                    "at " + tiles + " tiles across the level got coarser (" + previous
                    + " -> " + factor + ")");
                previous = factor;
            }
        }

        [Fact]
        public void ACoarserLevelIsAQuarterOrASixteenthOfTheTriangles()
        {
            // The saving, as a number, on a real generated map. This is what makes "it is not a
            // freeze either" a measurement rather than a hope.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            var full = NeighbourhoodCityMeshBuilder.Build(map, 1, out _);
            var half = NeighbourhoodCityMeshBuilder.Build(map, NeighbourhoodMeshLod.Half, out _);
            var quarter = NeighbourhoodCityMeshBuilder.Build(map, NeighbourhoodMeshLod.Quarter, out _);

            var fullGround = 0;
            for (int i = 0; i < full.Layers.Length; i++) fullGround += full.Layers[i].PrimitiveCount;
            var halfGround = 0;
            for (int i = 0; i < half.Layers.Length; i++) halfGround += half.Layers[i].PrimitiveCount;
            var quarterGround = 0;
            for (int i = 0; i < quarter.Layers.Length; i++) quarterGround += quarter.Layers[i].PrimitiveCount;

            Assert.True(fullGround > 0, "this map produced no ground at all");
            Assert.True(halfGround < fullGround, "the half level is not smaller than the full one");
            Assert.True(quarterGround < halfGround, "the quarter level is not smaller than the half one");
            //roughly a quarter and a sixteenth, with a tolerance for the diamond's edge blocks
            Assert.True(halfGround < fullGround * 0.4f,
                "the half level is " + halfGround + " of " + fullGround + " ground triangles");
            Assert.True(quarterGround < fullGround * 0.15f,
                "the quarter level is " + quarterGround + " of " + fullGround + " ground triangles");
        }

        [Fact]
        public void ACoarserLevelStillCoversTheWholeMap()
        {
            // The level of detail must not become a hole in the map. A coarse level drops the fade
            // skirt and emits one quad per block, so the risk is that it also drops part of the
            // diamond - which would be a quarter of the neighbourhood missing rather than a cheaper
            // version of it.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            for (int i = 0; i < map.TerrainType.Length; i++) map.TerrainType[i] = NeighbourhoodTerrain.Grass;

            foreach (var factor in NeighbourhoodMeshLod.Factors)
            {
                var mesh = NeighbourhoodCityMeshBuilder.Build(map, factor, out _);
                var covered = new bool[mesh.Layers[NeighbourhoodTerrain.Grass].PrimitiveCount];
                var verts = mesh.Layers[NeighbourhoodTerrain.Grass].Vertices;
                var indices = mesh.Layers[NeighbourhoodTerrain.Grass].Indices;

                //every tile strictly inside the diamond must be under at least one quad
                for (int triangle = 0; triangle + 2 < indices.Length; triangle += 3)
                {
                    var a = verts[indices[triangle]].Position;
                    var b = verts[indices[triangle + 1]].Position;
                    var c = verts[indices[triangle + 2]].Position;
                    if (a.Y <= 0f && b.Y <= 0f && c.Y <= 0f) covered[triangle / 3] = true;
                }

                //sample tiles along the diamond's own edge, which is where coverage is hardest
                for (int y = NeighbourhoodCityMeshBuilder.ClipInset;
                    y < NeighbourhoodCityMeshBuilder.MapSize - NeighbourhoodCityMeshBuilder.ClipInset; y += factor)
                {
                    var left = NeighbourhoodCityMeshBuilder.DiamondLeft(y) + 2;
                    var right = NeighbourhoodCityMeshBuilder.DiamondRight(y) - 2;
                    for (var x = left; x < right; x += factor)
                    {
                        Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(x, y)
                            || x > right, "the test's own sampling is wrong at " + x + "," + y);
                    }
                }

                //the simpler, stronger statement: the coarse mesh's ground reaches both ends of
                //the map on both axes
                var minX = float.MaxValue;
                var maxX = float.MinValue;
                var minZ = float.MaxValue;
                var maxZ = float.MinValue;
                foreach (var vertex in verts)
                {
                    if (vertex.Position.X < minX) minX = vertex.Position.X;
                    if (vertex.Position.X > maxX) maxX = vertex.Position.X;
                    if (vertex.Position.Z < minZ) minZ = vertex.Position.Z;
                    if (vertex.Position.Z > maxZ) maxZ = vertex.Position.Z;
                }
                Assert.True(minX <= 0f && maxX >= NeighbourhoodCityMeshBuilder.MapSize - 1,
                    "the factor-" + factor + " level spans x " + minX + ".." + maxX);
                Assert.True(minZ <= 0f && maxZ >= NeighbourhoodCityMeshBuilder.MapSize - 1,
                    "the factor-" + factor + " level spans z " + minZ + ".." + maxZ);
            }
        }

        [Fact]
        public void EveryLevelKeepsTheRoadsAtFullDetail()
        {
            // A road quad is a mask-driven cell of the road atlas, so a coarse level cannot average
            // four tiles' masks without drawing a street that is not on the map. The road geometry
            // must therefore be identical at every level, which also means the roads are always
            // visible on a coarse ground rather than being a separate thing that has to be switched
            // on.
            var map = UniformMap(NeighbourhoodTerrain.Grass, 5, 0);

            var full = NeighbourhoodCityMeshBuilder.Build(map, 1, out _);
            var half = NeighbourhoodCityMeshBuilder.Build(map, NeighbourhoodMeshLod.Half, out _);
            var quarter = NeighbourhoodCityMeshBuilder.Build(map, NeighbourhoodMeshLod.Quarter, out _);

            Assert.True(full.Roads.PrimitiveCount > 0, "this map should have road geometry");
            Assert.Equal(full.Roads.PrimitiveCount, half.Roads.PrimitiveCount);
            Assert.Equal(full.Roads.PrimitiveCount, quarter.Roads.PrimitiveCount);
        }

        [Fact]
        public void EveryLevelHasTheSameChunkBookkeeping()
        {
            // The property that lets one draw path serve all three levels: the cull and the
            // draw-range coalescing both index the same 32x32 chunk grid, so a level cannot come
            // with its own idea of which chunks exist.
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);

            foreach (var factor in NeighbourhoodMeshLod.Factors)
            {
                var mesh = NeighbourhoodCityMeshBuilder.Build(map, factor, out _);
                Assert.Equal(factor, mesh.LodFactor);
                Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount + 1, mesh.RoadChunkIndexStarts.Length);
                for (int layer = 0; layer < mesh.Layers.Length; layer++)
                {
                    var starts = mesh.LayerChunkIndexStarts[layer];
                    Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount + 1, starts.Length);
                    //monotone, and it ends at the layer's real index count
                    for (int chunk = 1; chunk < starts.Length; chunk++)
                    {
                        Assert.True(starts[chunk] >= starts[chunk - 1],
                            "the factor-" + factor + " level's layer " + layer
                            + " chunk starts go backwards at " + chunk);
                    }
                    Assert.Equal(mesh.Layers[layer].Indices.Length, starts[NeighbourhoodCityMeshBuilder.ChunkCount]);
                }
                Assert.Equal(mesh.Roads.Indices.Length,
                    mesh.RoadChunkIndexStarts[NeighbourhoodCityMeshBuilder.ChunkCount]);
            }
        }

        [Fact]
        public void AnImpossibleLevelFactorIsClampedRatherThanBuilt()
        {
            // A caller asking for a factor that does not exist gets the nearest one that does, so a
            // typo in a camera rule cannot produce a mesh the draw path has never seen.
            Assert.Equal(NeighbourhoodMeshLod.Full, NeighbourhoodMeshLod.ClampFactor(0));
            Assert.Equal(NeighbourhoodMeshLod.Full, NeighbourhoodMeshLod.ClampFactor(1));
            Assert.Equal(NeighbourhoodMeshLod.Half, NeighbourhoodMeshLod.ClampFactor(2));
            Assert.Equal(NeighbourhoodMeshLod.Quarter, NeighbourhoodMeshLod.ClampFactor(4));
            Assert.Equal(NeighbourhoodMeshLod.Quarter, NeighbourhoodMeshLod.ClampFactor(99));

            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map, 99, out _);
            Assert.Equal(NeighbourhoodMeshLod.Quarter, mesh.LodFactor);
        }

        // ---------------------------------------------------------------- the tree budget

        [Fact]
        public void TheShippedTreeModelNamesAllResolveToRealFiles()
        {
            // The shipped Content/3D folder has EIGHTEEN tree models, not nineteen:
            // pine1..4, tree1..4, palm1..4, cactus1..3, snow1..3. The load site used to assume four
            // variants of each of four species plus three of snow and derive the names as
            // FileNames[i / 4] + (i % 4) + 1, so index 15 came out as cactus4.obj - a file that has
            // never shipped. The log said "could not read the tree model ...\cactus4.obj: Could
            // not find file" and a desert tint drew nothing on every fourth tree.
            //
            // This is the assertion that would have caught it: the index-to-name mapping is now a
            // list of the eighteen real files, and this checks the list against the directory that
            // actually ships, which is the only version of "these names are right" that stays
            // right when somebody adds a species.
            var folder = Path.Combine(AppContext.BaseDirectory,
                NeighbourhoodContent.ModelFolder.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(Directory.Exists(folder), "the test run has no " + folder
                + ", so this test cannot check the shipped model names at all");

            for (int i = 0; i < NeighbourhoodFoliageTint.ModelCount; i++)
            {
                var name = NeighbourhoodFoliageTint.FileNameFor(i);
                Assert.NotNull(name);
                Assert.True(File.Exists(Path.Combine(folder, name)),
                    "model index " + i + " asks for " + name + ", which is not in " + folder);
            }
        }

        [Fact]
        public void TheTreeModelTableIsExactlyTheShippedEighteen()
        {
            // Pinned by name as well as by count. "Every name in the table exists" is satisfied by
            // a table of pine1..4 four times over; "the table is the shipped set" is not, and the
            // second is the property the loader depends on.
            var expected = new[]
            {
                "pine1.obj", "pine2.obj", "pine3.obj", "pine4.obj",
                "tree1.obj", "tree2.obj", "tree3.obj", "tree4.obj",
                "palm1.obj", "palm2.obj", "palm3.obj", "palm4.obj",
                "cactus1.obj", "cactus2.obj", "cactus3.obj",
                "snow1.obj", "snow2.obj", "snow3.obj"
            };
            Assert.Equal(expected, NeighbourhoodFoliageTint.ModelFiles);
            Assert.Equal(expected.Length, NeighbourhoodFoliageTint.ModelFiles.Length);
            // snow is what is left over after the species blocks, and it is three of them
            Assert.Equal(15, NeighbourhoodFoliageTint.SnowModelBase);
            Assert.Equal(3, NeighbourhoodFoliageTint.SnowVariantCount);
            Assert.Equal(NeighbourhoodFoliageTint.ModelCount,
                NeighbourhoodFoliageTint.SnowModelBase + NeighbourhoodFoliageTint.SnowVariantCount);
            // the const the loader sizes its arrays with is the length of the table
            var modelCount = NeighbourhoodFoliageTint.ModelCount;
            Assert.Equal(expected.Length, modelCount);
            // and cactus is the one short block
            Assert.Equal(3, NeighbourhoodFoliageTint.VariantCount(NeighbourhoodFoliageTint.Species.Cactus));
            Assert.Equal(4, NeighbourhoodFoliageTint.VariantCount(NeighbourhoodFoliageTint.Species.Pine));
        }

        [Fact]
        public void NoModelIndexEverNamesAFileThatIsNotShipped()
        {
            // The regression, stated as a property rather than as a list: whatever tint and
            // variant the walk rolls - including a negative variant, which used to produce a
            // negative index and a tree that was silently skipped - the file it names is one of
            // the eighteen.
            var shipped = new HashSet<string>(NeighbourhoodFoliageTint.ModelFiles, StringComparer.Ordinal);
            var tints = new[]
            {
                new Color(0, 128, 0), new Color(200, 180, 60), new Color(60, 120, 40),
                new Color(0, 0, 0), new Color(255, 255, 255), new Color(0, 0, 0, 0),
                new Color(255, 0, 0), new Color(40, 90, 30), new Color(120, 130, 140)
            };
            for (int variant = -5; variant < 8; variant++)
            {
                foreach (var tint in tints)
                {
                    foreach (var terrain in new byte[] { 0, 1, 2, NeighbourhoodTerrain.Snow, 4 })
                    {
                        var model = NeighbourhoodFoliageTint.ModelFor(tint, terrain, variant);
                        Assert.True(model >= 0 && model < NeighbourhoodFoliageTint.ModelCount,
                            "tint " + tint + " variant " + variant + " asked for model " + model);
                        var name = NeighbourhoodFoliageTint.FileNameFor(model);
                        Assert.NotNull(name);
                        Assert.True(shipped.Contains(name),
                            "tint " + tint + " variant " + variant + " asked for " + name
                            + ", which is not one of the shipped models");
                    }
                }
            }
        }

        [Fact]
        public void AMissingTreeModelFallsBackToTheLowestOneThatLoaded()
        {
            // The deterministic part of the fallback, which is the part that decides what the
            // neighbourhood looks like. Lowest, not first-found and not random: the same install
            // has to produce the same wood on every machine and in every run.
            Assert.Equal(-1, NeighbourhoodFoliage.FallbackModelIndex(null));
            Assert.Equal(-1, NeighbourhoodFoliage.FallbackModelIndex(new DGRP3DVert[0][]));
            Assert.Equal(-1, NeighbourhoodFoliage.FallbackModelIndex(new[]
            {
                null, new DGRP3DVert[0], null
            }));

            var verts = new[]
            {
                null, null, new DGRP3DVert[1], new DGRP3DVert[1], null
            };
            Assert.Equal(2, NeighbourhoodFoliage.FallbackModelIndex(verts));
        }

        [Fact]
        public void ATreeModelWarningIsSaidOncePerProcess()
        {
            // Not per instance and not per frame. The foliage pass is rebuilt every time the
            // neighbourhood view is opened, so a warning suppressed only per instance repeats for
            // the whole session - which is the same thing as per frame as far as the log is
            // concerned.
            var line = "NeighbourhoodFoliageTests: a warning only one process should say once";
            Assert.True(NeighbourhoodFoliage.WarnOnce(line));
            Assert.False(NeighbourhoodFoliage.WarnOnce(line));
            // a different line is still its own news
            Assert.True(NeighbourhoodFoliage.WarnOnce(line + " (and a different one)"));
            Assert.False(NeighbourhoodFoliage.WarnOnce(""));
        }

        [Fact]
        public void SnowGroundAlwaysAsksForASnowModel()
        {
            var tints = new[]
            {
                new Color(0, 128, 0), new Color(200, 180, 60), new Color(60, 120, 40),
                new Color(0, 0, 0), new Color(255, 255, 255), new Color(0, 0, 0, 0)
            };
            for (int variant = 0; variant < 4; variant++)
            {
                foreach (var tint in tints)
                {
                    var model = NeighbourhoodFoliageTint.ModelFor(tint, NeighbourhoodTerrain.Snow, variant);
                    Assert.True(model >= NeighbourhoodFoliageTint.SnowModelBase,
                        "snowy ground asked for model " + model + " (" + NeighbourhoodFoliageTint.FileNameFor(model) + ")");
                    Assert.True(model < NeighbourhoodFoliageTint.ModelCount);
                }
            }

            // and all three snow models are reachable
            var seen = new HashSet<int>();
            for (int variant = 0; variant < NeighbourhoodFoliageTint.SnowVariantCount; variant++)
            {
                seen.Add(NeighbourhoodFoliageTint.ModelFor(tints[0], NeighbourhoodTerrain.Snow, variant));
            }
            Assert.Equal(NeighbourhoodFoliageTint.SnowVariantCount, seen.Count);
        }

        [Fact]
        public void EveryFoliageModelNameIsDistinct()
        {
            var names = new HashSet<string>();
            for (int i = 0; i < NeighbourhoodFoliageTint.ModelCount; i++)
            {
                Assert.True(names.Add(NeighbourhoodFoliageTint.FileNameFor(i)),
                    "two model indices resolve to " + NeighbourhoodFoliageTint.FileNameFor(i));
            }
            Assert.Equal(NeighbourhoodFoliageTint.ModelCount, names.Count);
        }

        [Fact]
        public void TheTreeBudgetIsSpreadOverTheWholeDrawnMapNotTheFirstRows()
        {
            // The bug. A dense generated forest is fifteen trees to the tile almost everywhere, so
            // a first-come walk in map order spends the whole budget inside the first two rows -
            // and the first rows are the narrow top of the isometric diamond, which is off screen.
            // The log said four thousand trees; the player saw an empty field.
            var map = UniformMap(NeighbourhoodTerrain.Grass, 0, 0);
            for (int i = 0; i < map.ForestDensityData.Length; i++) map.ForestDensityData[i] = 255;

            var plan = NeighbourhoodFoliagePlan.For(map, NeighbourhoodFoliage.TreeBudget);
            Assert.True(plan.EligibleChunks > 1, "only " + plan.EligibleChunks + " chunks could hold a tree");
            Assert.True(plan.SharePerChunk >= 1);
            Assert.True(plan.TilesOutsideClip > 0, "the clip should be discarding forest on a square map");

            // the middle of the drawn world gets trees, not just the top rows
            var centre = 256 / NeighbourhoodFoliage.ChunkSize;
            var middle = centre + centre * NeighbourhoodFoliage.ChunksPerAxis;
            Assert.True(plan.IsEligible(middle), "the middle of the map gets no trees at all");
            Assert.True(plan.TreesFor(middle) > 0);

            // and the share is enough to be worth a buffer
            Assert.True(plan.SharePerChunk * plan.EligibleChunks + plan.RemainderChunks
                == NeighbourhoodFoliage.TreeBudget);
            Assert.Contains("clip", plan.ToString());
        }

        [Fact]
        public void TheTreePlanOnlyPlacesTreesInsideTheDrawnMap()
        {
            // Eligibility is the clip, so a chunk that has forest but none of it inside the
            // isometric kite is not on the list - and the clipped forest is counted, because
            // "where did the budget go" is answerable from the number.
            // Forest only in the border the clip never reaches (the inset square leaves no chunk
            // wholly outside it, so a uniform forest would make chunk 0 eligible; audit O1).
            var map = NeighbourhoodMap.Create(NeighbourhoodFoliage.MapSize);
            for (int oy = 0; oy < map.Height; oy++)
            {
                for (int ox = 0; ox < map.Width; ox++)
                {
                    if (!NeighbourhoodCityMeshBuilder.IsInsideDiamond(ox, oy)) map.ForestDensityData[map.Index(ox, oy)] = 255;
                }
            }

            // a tile in the top-left corner of the square, which the clip never reaches
            var clipped = map.Index(2, 2);
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(2, 2));
            Assert.Equal(0, map.RoadData[clipped]);

            var plan = NeighbourhoodFoliagePlan.For(map, NeighbourhoodFoliage.TreeBudget);
            Assert.Equal(0, plan.TreesFor(0));
            Assert.False(plan.IsEligible(0));
            Assert.True(plan.TilesOutsideClip > 0);
            Assert.Equal(0, plan.TreesFor(NeighbourhoodFoliage.MapSize)); // out of range, not eligible

            // roads are not treeable, exactly as the chunk walk requires
            var roaded = UniformMap(NeighbourhoodTerrain.Grass, RoadMask.MaskBits, 0);
            for (int i = 0; i < roaded.ForestDensityData.Length; i++) roaded.ForestDensityData[i] = 255;
            Assert.Equal(0, NeighbourhoodFoliagePlan.For(roaded, NeighbourhoodFoliage.TreeBudget).EligibleChunks);
        }

        [Fact]
        public void AMapWithNoForestHasNoEligibleChunks()
        {
            var map = NeighbourhoodMap.Create(NeighbourhoodFoliage.MapSize);
            var plan = NeighbourhoodFoliagePlan.For(map, NeighbourhoodFoliage.TreeBudget);
            Assert.Equal(0, plan.EligibleChunks);
            Assert.Equal(0, plan.SharePerChunk);
            Assert.Equal(0, plan.RemainderChunks);
            Assert.Equal(0, plan.TilesOutsideClip);
            Assert.Equal(0, plan.TreesFor(500));
        }

        [Fact]
        public void TheTreePlanIsDeterministicAndSafeOnRubbishInput()
        {
            var map = UniformMap(NeighbourhoodTerrain.Grass, 0, 0);
            for (int i = 0; i < map.ForestDensityData.Length; i++) map.ForestDensityData[i] = 200;

            var first = NeighbourhoodFoliagePlan.For(map, 512);
            var second = NeighbourhoodFoliagePlan.For(map, 512);
            Assert.Equal(first.Eligible, second.Eligible);
            Assert.Equal(first.SharePerChunk, second.SharePerChunk);
            Assert.Equal(first.TilesOutsideClip, second.TilesOutsideClip);

            Assert.Equal(0, NeighbourhoodFoliagePlan.For(null, 512).EligibleChunks);
            Assert.Equal(0, NeighbourhoodFoliagePlan.For(NeighbourhoodMap.Create(64), 512).EligibleChunks);
            Assert.Equal(0, NeighbourhoodFoliagePlan.For(map, 0).SharePerChunk);
        }

        [Fact]
        public void AGeneratedMapsTreesLandWhereThePlanSaysTheyDo()
        {
            // the end-to-end version, on a real generated map: the plan covers the drawn world,
            // the budget reaches every chunk it lists, and the total is inside the budget.
            var lots = new List<NeighbourhoodLotInput>();
            short n = 1;
            for (int gy = 0; gy < 4; gy++)
            {
                for (int gx = 0; gx < 4; gx++)
                {
                    lots.Add(NeighbourhoodTestInput.Lot(n++, (short)(40 + gx * 96), (short)(40 + gy * 96),
                        30, 48, (byte)(gx % 2 == 0 ? NeighbourhoodDoorSide.East : NeighbourhoodDoorSide.West)));
                }
            }
            var map = NeighbourhoodMapGenerator.Generate(new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, 512, lots));

            var plan = NeighbourhoodFoliagePlan.For(map, NeighbourhoodFoliage.TreeBudget);
            Assert.True(plan.EligibleChunks > 8, "a generated neighbourhood's forest is too thin: "
                + plan.EligibleChunks + " chunks");
            Assert.True(plan.TilesOutsideClip > 0);

            var allocated = 0;
            foreach (var chunk in plan.Eligible) allocated += plan.TreesFor(chunk);
            Assert.InRange(allocated, 1, NeighbourhoodFoliage.TreeBudget);

            // the trees a chunk is offered are all inside the clip, by construction
            foreach (var chunk in plan.Eligible)
            {
                var cx = chunk % NeighbourhoodFoliage.ChunksPerAxis;
                var cy = chunk / NeighbourhoodFoliage.ChunksPerAxis;
                var inside = 0;
                for (int oy = cy * NeighbourhoodFoliage.ChunkSize;
                    oy < (cy + 1) * NeighbourhoodFoliage.ChunkSize; oy++)
                {
                    for (int ox = cx * NeighbourhoodFoliage.ChunkSize;
                        ox < (cx + 1) * NeighbourhoodFoliage.ChunkSize; ox++)
                    {
                        if (NeighbourhoodCityMeshBuilder.IsInsideDiamond(ox, oy)) inside++;
                    }
                }
                if (inside == 0) Assert.Equal(0, plan.TreesFor(chunk));
            }
        }
    }
}
