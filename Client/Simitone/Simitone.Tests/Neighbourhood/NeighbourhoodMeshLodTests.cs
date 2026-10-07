/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The level of detail, as two numbers and the rules that choose them.
    ///
    /// <para>
    /// <b>What is being asserted, and why it is arithmetic rather than a picture.</b> The question
    /// this file exists for is "does zooming in show more detail", and the answer is two decisions
    /// that can both be made without a device: how finely the closest level cuts a tile up
    /// (<see cref="NeighbourhoodMeshLod.Subdivisions"/> and the slice geometry that implements
    /// it), and which of the levels a camera of a given width gets
    /// (<see cref="NeighbourhoodMeshLodSelector"/>). Neither is observable in a headless build, and
    /// both are the kind of decision that goes wrong silently: a level that never subdivides still
    /// draws, and a level rule with no hysteresis still draws - it just pops twice per zoom.
    /// </para>
    ///
    /// <para>
    /// The subdivision tests are the load-bearing ones, because the subdivision is the part that
    /// is new and the part that can be wrong in a way only a mismatch shows: if the subdivided
    /// surface and the un-subdivided one disagree on a tile corner, the ground is fine everywhere
    /// and cracked exactly where one becomes the other, which is a screenshot somebody has to find
    /// rather than a test failing.
    /// </para>
    /// </summary>
    public class NeighbourhoodMeshLodTests
    {
        private const float Epsilon = 0.0005f;

        /// <summary>A full-size map with smooth, sloping ground and one terrain type.</summary>
        private static NeighbourhoodMap RollingMap(byte terrain = NeighbourhoodTerrain.Grass)
        {
            var map = NeighbourhoodMap.Create(NeighbourhoodCityMeshBuilder.MapSize);
            var size = NeighbourhoodCityMeshBuilder.MapSize;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var i = map.Index(x, y);
                    map.TerrainType[i] = terrain;
                    //A smooth slope with a ripple, so a cubic interpolation has something to do and
                    //is not being asked to reproduce a flat map it would pass either way
                    map.ElevationData[i] = (byte)(120f
                        + 50f * MathF.Sin(x * 0.05f) * MathF.Cos(y * 0.04f)
                        + 20f * MathF.Sin((x + y) * 0.11f));
                }
            }
            return map;
        }

        /// <summary>
        /// The elevation byte a tile's own ground height comes from, resolved the way the builder
        /// resolves it: clamped into the row's diamond first.
        /// </summary>
        private static float GroundHeightAt(NeighbourhoodMap map, int x, int y)
        {
            y = Math.Max(0, Math.Min(NeighbourhoodCityMeshBuilder.MapSize - 1, y));
            var left = NeighbourhoodCityMeshBuilder.DiamondLeft(y);
            var right = NeighbourhoodCityMeshBuilder.DiamondRight(y);
            var cx = Math.Min(Math.Max(left, x), right - 1);
            return NeighbourhoodCityMeshBuilder.WorldHeightFor(map.ElevationData[y * map.Width + cx]);
        }

        // ------------------------------------------------------------ the two axes

        [Fact]
        public void TheClosestLevelSubdividesAndTheCoarseOnesDoNot()
        {
            // The answer to "does zooming in show detail", as one table. Only the finest level
            // subdivides: a coarse level's whole purpose is that a quad is cheap, and sixteen quads
            // per tile would make it dearer than the level it replaced.
            Assert.Equal(NeighbourhoodMeshLod.MaxSubdivisions,
                NeighbourhoodMeshLod.SubdivisionsFor(NeighbourhoodMeshLod.Full));
            Assert.Equal(1, NeighbourhoodMeshLod.SubdivisionsFor(NeighbourhoodMeshLod.Half));
            Assert.Equal(1, NeighbourhoodMeshLod.SubdivisionsFor(NeighbourhoodMeshLod.Quarter));

            Assert.True(NeighbourhoodMeshLod.ForTilesAcross(30f).IsSubdivided,
                "a 30-tile view did not get the subdivided level");
            Assert.Equal(NeighbourhoodMeshLod.MaxSubdivisions,
                NeighbourhoodMeshLod.SubdivisionsForTilesAcross(30f));
        }

        [Fact]
        public void SubdivisionIsChosenByItsOwnThresholdNotByTheCoarseOne()
        {
            // The two axes are different questions, and the band between them is the proof. A view
            // of 100 tiles is close enough that one quad per tile is visibly one quad - so it gets
            // the full-detail factor - and far enough that subdividing each tile would be sixteen
            // quads where a pixel of them is visible, so it does not subdivide. A single threshold
            // cannot produce that, and producing it is the difference between a level of detail
            // that costs what it is worth and one that costs sixteen times that for nothing.
            var at100 = NeighbourhoodMeshLod.ForTilesAcross(100f);
            Assert.Equal(NeighbourhoodMeshLod.Full, at100.Factor);
            Assert.Equal(1, at100.Subdivisions);
            Assert.False(at100.IsSubdivided);

            Assert.True(NeighbourhoodMeshLod.SubdivTilesAcross < NeighbourhoodMeshLod.MinTilesAcross,
                "the two thresholds are the same number, so there is no band between them");
        }

        [Fact]
        public void AWholeMapLevelIsNeverSubdivided()
        {
            // The subdivision is a close-level thing and a close-level thing cannot be a whole-map
            // thing: at four quads per tile a 512x512 map is a quarter of a million tiles of them.
            // The whole-map levels are what the far zoom draws, so they have to be the cheap ones -
            // and the log has to be able to say so, which it cannot if the level claims to be
            // subdivided and the buffers are not.
            var map = RollingMap();
            foreach (var factor in NeighbourhoodMeshLod.Factors)
            {
                var mesh = NeighbourhoodCityMeshBuilder.Build(map, factor, out _);
                Assert.Equal(1, mesh.Subdivisions);
                Assert.False(mesh.Lod.IsSubdivided);
            }
        }

        [Fact]
        public void TheWideZoomsStillGetTheCheapLevels()
        {
            // The cost half, unchanged by any of this: the whole map framed has to draw the quarter
            // level, and a lot at the ordinary 1.5x zoom the half one. Adding an axis to the level
            // must not have moved either boundary.
            var full = NeighbourhoodCameraPlacement.VisibleWidthFor(NeighbourhoodCityMeshBuilder.MapSize);
            Assert.Equal(NeighbourhoodMeshLod.Quarter,
                NeighbourhoodMeshLod.ForTilesAcross(full).Factor);
            Assert.Equal(NeighbourhoodMeshLod.Half,
                NeighbourhoodMeshLod.ForTilesAcross(full / 1.5f).Factor);
            Assert.True(NeighbourhoodMeshLod.ForTilesAcross(full).Factor
                == NeighbourhoodMeshLod.Quarter
                && !NeighbourhoodMeshLod.ForTilesAcross(full).IsSubdivided,
                "a whole-map framing should be cheap in both axes");
        }

        // ------------------------------------------------------------ the subdivision itself

        [Fact]
        public void ASubdividedTileIsSixteenQuadsOfAQuarterOfATileEach()
        {
            // The shape of the reference geometry: CityGeometry.SubRegenMeshVerts builds a
            // (subdiv + 1) * (subdiv + 1) vertex grid per tile and submits the interior of it, which
            // is a five by five grid and sixteen quads for subdiv = 4. Counting the vertices that
            // fall inside one tile is the direct statement of that, and it is a count rather than a
            // formula so a grid that is five by five but indexed wrongly still fails here.
            var map = RollingMap();
            var slice = MiddleSlice();
            var mesh = NeighbourhoodCityMeshBuilder.BuildSubdivSlice(map, slice,
                NeighbourhoodMeshLod.MaxSubdivisions, out _);

            Assert.Equal(NeighbourhoodMeshLod.MaxSubdivisions, mesh.Subdivisions);
            Assert.True(mesh.Lod.IsSubdivided);

            var bounds = NeighbourhoodCityMeshBuilder.SliceBounds(slice);
            var tileX = bounds.X + 8;
            var tileY = bounds.Y + 8;
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(tileX, tileY),
                "the test's own tile is outside the drawn map");

            //Counted as quads rather than as vertices, because a tile's edge vertices are its own
            //and its neighbours': the reference emits one grid per tile and does not weld between
            //them, so a vertex at an integer coordinate belongs to up to four tiles. A quad belongs
            //to exactly one, and a quad that straddles the tile's edge has a vertex outside the
            //tile, so "every vertex of the triangle is inside this tile" is this tile's quads and
            //only this tile's.
            var triangles = 0;
            var xSteps = new HashSet<int>();
            var ySteps = new HashSet<int>();
            foreach (var layer in mesh.Layers)
            {
                var vertices = layer.Vertices;
                var indices = layer.Indices;
                if (vertices == null || indices == null) continue;
                for (int t = 0; t + 2 < indices.Length; t += 3)
                {
                    var inside = true;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        var p = vertices[indices[t + corner]].Position;
                        if (p.X < tileX || p.X > tileX + 1f || p.Z < tileY || p.Z > tileY + 1f)
                        {
                            inside = false;
                            break;
                        }
                    }
                    if (!inside) continue;
                    triangles++;
                    for (int corner = 0; corner < 3; corner++)
                    {
                        var p = vertices[indices[t + corner]].Position;
                        xSteps.Add((int)MathF.Round((p.X - tileX) * NeighbourhoodMeshLod.MaxSubdivisions));
                        ySteps.Add((int)MathF.Round((p.Z - tileY) * NeighbourhoodMeshLod.MaxSubdivisions));
                    }
                }
            }

            //sixteen quads a tile, which is subdiv * subdiv, and the grid they are cut from is
            //subdiv + 1 by subdiv + 1 with its corners on the tile's own
            Assert.Equal(2 * NeighbourhoodMeshLod.MaxSubdivisions
                * NeighbourhoodMeshLod.MaxSubdivisions, triangles);
            var side = NeighbourhoodMeshLod.MaxSubdivisions + 1;
            Assert.Equal(side, xSteps.Count);
            Assert.Equal(side, ySteps.Count);
            for (int step = 0; step <= NeighbourhoodMeshLod.MaxSubdivisions; step++)
            {
                Assert.Contains(step, xSteps);
                Assert.Contains(step, ySteps);
            }
        }

        [Fact]
        public void TheSubdividedGroundAndTheWholeMapLevelAgreeOnEveryTileCorner()
        {
            // The property the whole mixed draw rests on. The draw submits a refined chunk from its
            // slice and an unrefined one from the whole-map level, and the boundary between them is
            // a line the two surfaces have to agree on: if the subdivided surface's corner heights
            // were even slightly off the un-subdivided ones, the ground would be seamless away from
            // the boundary and cracked exactly on it, in a ring that follows the camera as it moves
            // and is very hard to see in a screenshot.
            //
            // They agree because the interpolated height at a grid corner is the sampled elevation
            // at that corner, which is what both levels read. Asserted against the whole-map
            // level's own vertices rather than against the arithmetic, so the two are held against
            // each other rather than both against a formula they might share.
            var map = RollingMap();
            var slice = MiddleSlice();
            var whole = NeighbourhoodCityMeshBuilder.Build(map, 1, out _);
            var refined = NeighbourhoodCityMeshBuilder.BuildSubdivSlice(map, slice,
                NeighbourhoodMeshLod.MaxSubdivisions, out _);

            //the whole-map level's integer-grid heights, keyed by position
            var corners = new Dictionary<long, float>();
            foreach (var layer in whole.Layers)
            {
                if (layer.Vertices == null) continue;
                foreach (var vertex in layer.Vertices)
                {
                    var x = vertex.Position.X;
                    var z = vertex.Position.Z;
                    if (Math.Abs(x - MathF.Round(x)) > 0.001f) continue;
                    if (Math.Abs(z - MathF.Round(z)) > 0.001f) continue;
                    corners[Key((int)MathF.Round(x), (int)MathF.Round(z))] = vertex.Position.Y;
                }
            }
            Assert.True(corners.Count > 0, "the whole-map level produced no whole-tile vertices");

            var compared = 0;
            foreach (var layer in refined.Layers)
            {
                if (layer.Vertices == null) continue;
                foreach (var vertex in layer.Vertices)
                {
                    var x = vertex.Position.X;
                    var z = vertex.Position.Z;
                    if (Math.Abs(x - MathF.Round(x)) > 0.001f) continue;
                    if (Math.Abs(z - MathF.Round(z)) > 0.001f) continue;
                    var expected = GroundHeightAt(map, (int)MathF.Round(x), (int)MathF.Round(z));
                    Assert.True(Math.Abs(vertex.Position.Y - expected) < Epsilon,
                        "the subdivided surface's corner at " + x + "," + z + " is "
                        + vertex.Position.Y + " where the elevation says " + expected);
                    if (!corners.TryGetValue(Key((int)MathF.Round(x), (int)MathF.Round(z)), out var wholeHeight))
                    {
                        continue;
                    }
                    compared++;
                    Assert.True(Math.Abs(vertex.Position.Y - wholeHeight) < Epsilon,
                        "the two levels disagree at tile corner " + x + "," + z + ": subdivided "
                        + vertex.Position.Y + ", whole-map " + wholeHeight);
                }
            }
            Assert.True(compared > 500,
                "only " + compared + " tile corners were compared, which is not enough of a slice to"
                + " say anything about a seam");
        }

        [Fact]
        public void TheSubdividedGroundIsADifferentSurfaceAndNotSixteenCopiesOfTheSameOne()
        {
            // What the subdivision actually buys, stated as a measurement rather than a claim. The
            // un-subdivided level's tile is a bilinear patch over the elevation bytes at its four
            // corners, and a bilinear patch is a tent: it is pinned at the corners and sags across
            // each tile diagonal, so a hillside is a grid of little tents rather than a hill. The
            // subdivided level interpolates the same bytes with a Catmull-Rom, so the surface
            // between the corners is curved, and the two differ in the middle of a tile by a real
            // fraction of a tile rather than by a rounding error.
            //
            // The claim is worth the test because the two levels agree exactly on every tile corner -
            // the test above - so a subdivision that quietly produced the same surface would pass
            // that one and would be sixteen times the geometry for nothing.
            var map = RollingMap();
            var slice = MiddleSlice();
            var mesh = NeighbourhoodCityMeshBuilder.BuildSubdivSlice(map, slice,
                NeighbourhoodMeshLod.MaxSubdivisions, out _);

            var bounds = NeighbourhoodCityMeshBuilder.SliceBounds(slice);
            var tileX = bounds.X + 8;
            var tileY = bounds.Y + 8;
            var centre = -1f;
            //the four corners, keyed by position: a corner is a vertex of all four tiles that meet
            //there, because the reference emits a grid per tile and does not weld between them
            var corners = new Dictionary<long, float>();
            foreach (var layer in mesh.Layers)
            {
                if (layer.Vertices == null) continue;
                foreach (var vertex in layer.Vertices)
                {
                    var x = vertex.Position.X;
                    var z = vertex.Position.Z;
                    if (x < tileX || x > tileX + 1f) continue;
                    if (z < tileY || z > tileY + 1f) continue;
                    if (Math.Abs(x - tileX - 0.5f) < 0.001f && Math.Abs(z - tileY - 0.5f) < 0.001f)
                    {
                        centre = vertex.Position.Y;
                    }
                    if (Math.Abs(x - MathF.Round(x)) < 0.001f && Math.Abs(z - MathF.Round(z)) < 0.001f)
                    {
                        corners[Key((int)MathF.Round(x), (int)MathF.Round(z))] = vertex.Position.Y;
                    }
                }
            }
            Assert.True(centre > -1f, "the tile has no vertex at its centre, so it is not subdivided");
            Assert.Equal(4, corners.Count);

            //the bilinear value the un-subdivided level's surface has at the centre: the mean of
            //the four corners, which is what a bilinear patch pinned at all four of them does
            var bilinear = 0f;
            foreach (var height in corners.Values) bilinear += height;
            bilinear /= 4f;
            var difference = MathF.Abs(centre - bilinear);
            Assert.True(difference > 0.0005f,
                "the subdivided surface's centre is exactly the bilinear one, so the ground is the"
                + " same tent tiled sixteen times");
        }

        [Fact]
        public void ASliceTouchesOnlyItsOwnChunks()
        {
            // The other half of what makes a refined chunk and an unrefined chunk able to share a
            // draw: a slice is a whole 2x2 of chunks, so its index bookkeeping is zero everywhere
            // else and a range over a chunk it does not hold is empty and gets skipped. A slice
            // that stopped at the chunk grid could not be mixed with the whole-map level at all.
            var map = RollingMap();
            var slice = MiddleSlice();
            var mesh = NeighbourhoodCityMeshBuilder.BuildSubdivSlice(map, slice,
                NeighbourhoodMeshLod.MaxSubdivisions, out _);

            var own = new HashSet<int>(NeighbourhoodCityMeshBuilder.ChunksInSlice(slice));
            var ranges = new List<NeighbourhoodMeshDrawRange>();
            var visible = new List<NeighbourhoodMeshDrawRange>();

            for (int layer = 0; layer < mesh.Layers.Length; layer++)
            {
                var starts = mesh.LayerChunkIndexStarts[layer];
                Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount + 1, starts.Length);
                var populated = mesh.Layers[layer].Indices.Length > 0;
                for (int chunk = 0; chunk < NeighbourhoodCityMeshBuilder.ChunkCount; chunk++)
                {
                    var size = starts[chunk + 1] - starts[chunk];
                    if (!populated) continue; //an empty layer is empty in every chunk
                    if (own.Contains(chunk))
                    {
                        Assert.True(size > 0, "a chunk of the slice holds no geometry at all");
                        //six indices per quad, and subdiv * subdiv quads per tile
                        Assert.Equal(0, size % (6 * NeighbourhoodMeshLod.MaxSubdivisions
                            * NeighbourhoodMeshLod.MaxSubdivisions));
                    }
                    else
                    {
                        Assert.True(size == 0,
                            "chunk " + chunk + " is outside slice " + slice + " and holds geometry");
                    }
                }

                NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, own, ranges);
                var ownIndices = 0;
                foreach (var range in ranges) ownIndices += range.IndexCount;
                Assert.Equal(mesh.Layers[layer].Indices.Length, ownIndices);

                var others = new List<int>();
                for (int chunk = 0; chunk < NeighbourhoodCityMeshBuilder.ChunkCount; chunk++)
                {
                    if (!own.Contains(chunk)) others.Add(chunk);
                }
                NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, others, visible);
                Assert.Equal(0, visible.Count);
            }

            //the partition itself, stated as a fact about the chunk grid
            for (int chunk = 0; chunk < NeighbourhoodCityMeshBuilder.ChunkCount; chunk++)
            {
                var owning = NeighbourhoodCityMeshBuilder.SliceOfChunk(chunk);
                Assert.InRange(owning, 0, NeighbourhoodCityMeshBuilder.SliceCount - 1);
                Assert.Contains(chunk, NeighbourhoodCityMeshBuilder.ChunksInSlice(owning));
            }
        }

        [Fact]
        public void RefiningWhatTheCameraCanSeeCostsAPittanceOfTheWholeMap()
        {
            // The number that justifies building the close level a slice at a time rather than
            // whole. A camera close enough to want subdivision frames under 48 tiles; a 48-tile
            // region is 0.9% of the map's area, so refining all of it is about one per cent of
            // refining all of it - and refining all of it at once is the several-hundred-megabyte
            // buffer the slicing exists to avoid. Stated as a measurement on a real map so that
            // "the close level is cheap" is arithmetic.
            var map = RollingMap();
            var full = NeighbourhoodCityMeshBuilder.Build(map, 1, out _);
            var tilesAcross = NeighbourhoodMeshLod.SubdivTilesAcross;
            var region = new Rectangle(256 - (int)tilesAcross / 2, 224 - (int)tilesAcross / 2,
                (int)tilesAcross, (int)tilesAcross);

            var refined = 0;
            var slices = NeighbourhoodCityMeshBuilder.SlicesInRegion(region);
            foreach (var slice in slices)
            {
                var mesh = NeighbourhoodCityMeshBuilder.BuildSubdivSlice(map, slice,
                    NeighbourhoodMeshLod.MaxSubdivisions, out _);
                refined += mesh.GroundPrimitives;
            }

            var whole = (long)full.GroundPrimitives
                * NeighbourhoodMeshLod.MaxSubdivisions * NeighbourhoodMeshLod.MaxSubdivisions;
            Assert.True(refined > 0, "the camera's own region refined to nothing");
            Assert.True(slices.Length <= 9,
                "a " + tilesAcross + "-tile view needed " + slices.Length + " slices, so the"
                + " subdivided cache would be asked for more than a couple of dozen resident slices"
                + " at the zoom it is meant for");
            Assert.True(refined * 20 < whole,
                "refining a " + tilesAcross + "-tile region is " + refined + " of " + whole
                + " ground triangles for the whole map subdivided, which is not the twentieth it"
                + " is supposed to be");
        }

        [Fact]
        public void CubicInterpolatesItsOwnSamples()
        {
            // The property the corner agreement above is an instance of, asserted on the spline
            // itself so a change to it is caught here rather than as a seam. CityGeometry.Cubic with
            // zero continuity is a Catmull-Rom, and a Catmull-Rom passes through the samples it was
            // given - which is the whole reason a subdivided surface and an un-subdivided one can
            // meet on a tile boundary.
            var samples = new[] { 10f, 40f, 90f, 160f };
            Assert.Equal(samples[1], NeighbourhoodCityMeshBuilder.Cubic(samples[0], samples[1],
                samples[2], samples[3], 0f), 5);
            Assert.Equal(samples[2], NeighbourhoodCityMeshBuilder.Cubic(samples[0], samples[1],
                samples[2], samples[3], 1f), 5);
            //and halfway through, the midpoint of the cubic: the tangents are (v2 - v0) / 4 = 20
            //and (v3 - v1) / 4 = 30, and the Hermite basis at a half is (1/2, 1/8, -1/8, 1/2) - so
            //20 + 2.5 - 3.75 + 45 = 63.75, which is not the linear midpoint and is the whole
            //difference between the two surfaces
            var middle = NeighbourhoodCityMeshBuilder.Cubic(samples[0], samples[1], samples[2],
                samples[3], 0.5f);
            Assert.Equal(63.75f, middle, 3);
        }

        // ------------------------------------------------------------ choosing a level, with hysteresis

        [Fact]
        public void TheFirstSelectionTakesTheStatelessAnswer()
        {
            // A selector starts at the finest level, because that is the safe one, and a
            // neighbourhood screen starts zoomed out on the whole map. With a deadband applied from
            // the first frame the two would never meet - getting from the finest level to the
            // coarsest needs a wider view than the widest view there is - and the world would be
            //meshed at its most expensive setting for ever. History needs something to be history
            //about.
            var selector = new NeighbourhoodMeshLodSelector();
            var whole = NeighbourhoodCameraPlacement.VisibleWidthFor(NeighbourhoodCityMeshBuilder.MapSize);
            Assert.Equal(NeighbourhoodMeshLod.Quarter, selector.Select(whole).Factor);
            Assert.Equal(1, selector.Changes);
        }

        [Fact]
        public void ALevelIsHeldAcrossItsOwnBoundaryByTheMargin()
        {
            // The deadband, as a number, on both sides of one boundary. A level's own reach is the
            // width at which it becomes eligible - 320 tiles for the half level - and it is kept
            // until the view is fifteen per cent past it, so the full level is released at 368 on
            // the way out and the half level gives way to it at 184 on the way in. A level dropped
            // *at* its boundary is the version that pops on the way back, and this is the pair of
            // numbers that says which way round it is.
            var halfReach = NeighbourhoodMeshLod.BoundaryAboveFactor(NeighbourhoodMeshLod.Half);
            Assert.Equal(NeighbourhoodMeshLod.MinTilesAcross * NeighbourhoodMeshLod.Half, halfReach);
            var release = halfReach * (1f + NeighbourhoodMeshLodSelector.Hysteresis);
            var fullReach = NeighbourhoodMeshLod.BoundaryAboveFactor(NeighbourhoodMeshLod.Full);
            var fullRelease = fullReach * (1f + NeighbourhoodMeshLodSelector.Hysteresis);
            //a twentieth of a tile either side, because the thresholds are products of floats and
            //the exact value is not exactly the one written down
            var just = halfReach / 20f;

            var zoomingOut = new NeighbourhoodMeshLodSelector();
            var zoomingIn = new NeighbourhoodMeshLodSelector();
            //The first call on each is exempt from the deadband and just takes the stateless rule, so
            //they are spent on a width that already answers with the level being tested - otherwise
            //the first assertion would be measuring the exemption rather than the band.
            Assert.Equal(NeighbourhoodMeshLod.Full, zoomingOut.Select(200f).Factor);
            Assert.Equal(NeighbourhoodMeshLod.Half, zoomingIn.Select(400f).Factor);

            //from the inside, the level holds over its own boundary
            Assert.Equal(NeighbourhoodMeshLod.Full, zoomingOut.Select(halfReach).Factor);
            Assert.Equal(NeighbourhoodMeshLod.Full, zoomingOut.Select(release - just).Factor);
            Assert.Equal(NeighbourhoodMeshLod.Half, zoomingOut.Select(release + just).Factor);

            //from the outside, it holds down to the fine level's own release point
            Assert.Equal(NeighbourhoodMeshLod.Half, zoomingIn.Select(halfReach).Factor);
            Assert.Equal(NeighbourhoodMeshLod.Half, zoomingIn.Select(fullRelease + just).Factor);
            Assert.Equal(NeighbourhoodMeshLod.Full, zoomingIn.Select(fullRelease - just).Factor);
        }

        [Fact]
        public void TheSameWidthGivesTheSameLevelOnTheWayOutAndOnTheWayBackOutsideTheDeadbands()
        {
            // The whole point of the hysteresis, stated as the property a player would notice, and
            // stated honestly: outside a deadband the two directions have to agree exactly, and
            // inside one they are allowed not to - that is what a deadband is. A stateless rule has
            // no deadband and so pops twice per zoom step, once out and once in, from a camera that
            // ended up exactly where it started. So the assertion is that the sweeps agree except
            // where IsInsideDeadband says they may not, and that a zoom across the whole range
            // changes level a bounded number of times rather than once per step.
            var selector = new NeighbourhoodMeshLodSelector();
            var widths = new List<float>();
            for (var tiles = 200f; tiles <= 800f; tiles += 4f) widths.Add(tiles);

            var outward = new List<NeighbourhoodMeshLod>();
            foreach (var tiles in widths) outward.Add(selector.Select(tiles));
            var changesOut = selector.Changes;
            var inward = new List<NeighbourhoodMeshLod>();
            for (int i = widths.Count - 1; i >= 0; i--) inward.Add(selector.Select(widths[i]));
            var changesIn = selector.Changes - changesOut;
            inward.Reverse();

            var differences = 0;
            for (int i = 0; i < widths.Count; i++)
            {
                if (NeighbourhoodMeshLod.SameLevel(outward[i], inward[i])) continue;
                differences++;
                Assert.True(NeighbourhoodMeshLodSelector.IsInsideDeadband(widths[i]),
                    "at " + widths[i] + " tiles across the level on the way out was " + outward[i]
                    + " and on the way back " + inward[i] + ", which is outside every deadband, so a"
                    + " zoom out and back pops");
            }
            Assert.True(differences > 0,
                "the two sweeps never disagreed, so there is no deadband and the flicker this is"
                + " meant to remove is still there");

            //Three boundaries are in this range - two factors and one subdivision, of which the
            //sweep crosses the two factors - and each is owned by one band, so a monotone sweep
            //crosses each of them once per direction: out to quarter is two changes plus the
            //exempt first selection, and back down it re-crosses both on the way to the full level.
            Assert.True(changesOut + changesIn <= 6,
                "a zoom across the range changed level " + (changesOut + changesIn) + " times");
            Assert.True(changesOut <= 3 && changesIn <= 3,
                "one direction changed level " + Math.Max(changesOut, changesIn) + " times, which is"
                + " more than one change per boundary");
        }
        [Fact]
        public void AZoomInNeverPicksACoarserLevelThanTheZoomBeforeIt()
        {
            // Monotonicity, over the whole range and at a step small enough to cross every boundary
            // the level has. A rule that could go back the other way would be oscillating rather
            // than merely popping.
            var selector = new NeighbourhoodMeshLodSelector();
            var previous = selector.Select(800f);
            for (var tiles = 796f; tiles >= 8f; tiles -= 2f)
            {
                var factor = selector.Select(tiles);
                //the two axes point the same way here, which is worth stating: a bigger factor is
                //coarser and a bigger subdivision count is finer, so "never coarser" is
                //factor <= and subdivisions >=
                Assert.True(factor.Factor <= previous.Factor
                    && factor.Subdivisions >= previous.Subdivisions,
                    "at " + tiles + " tiles across the level got coarser: " + previous + " -> " + factor);
                previous = factor;
            }
            Assert.True(selector.Current.IsFull && selector.Current.IsSubdivided,
                "the closest zoom did not end on the finest level but on " + selector.Current);
        }

        [Fact]
        public void SubdivisionHasItsOwnDeadbandAndIsStickyInBothDirections()
        {
            // The subdivision axis is not the factor axis and does not inherit its boundary. Its
            // threshold is 48 tiles across, so the band is 48 * 0.85 = 40.8 to 48 * 1.15 = 55.2:
            // inside it the subdivision keeps whatever it was, whether the camera is on its way in
            // or on its way out.
            var boundary = NeighbourhoodMeshLod.SubdivTilesAcross;
            var inBand = boundary * (1f - NeighbourhoodMeshLodSelector.Hysteresis);
            var outBand = boundary * (1f + NeighbourhoodMeshLodSelector.Hysteresis);
            var just = boundary / 20f;
            Assert.True(inBand < boundary && boundary < outBand);

            var zoomingOut = new NeighbourhoodMeshLodSelector();
            zoomingOut.Select(20f);
            Assert.True(zoomingOut.Current.IsSubdivided, "a 20-tile view did not refine its ground");
            Assert.True(zoomingOut.Select(boundary).IsSubdivided,
                "the subdivision gave up at its own threshold rather than past it");
            Assert.True(zoomingOut.Select(outBand - just).IsSubdivided,
                "the subdivision gave up inside its own band");
            Assert.False(zoomingOut.Select(outBand + just).IsSubdivided);

            var zoomingIn = new NeighbourhoodMeshLodSelector();
            zoomingIn.Select(boundary + 5f);
            Assert.False(zoomingIn.Current.IsSubdivided);
            Assert.False(zoomingIn.Select(inBand + just).IsSubdivided,
                "the subdivision turned on before its own boundary");
            Assert.True(zoomingIn.Select(inBand - just).IsSubdivided);
        }

        [Fact]
        public void AWidthTheRuleCannotReadKeepsTheLevelInHand()
        {
            // The degenerate-camera cases, and they are worth a rule of their own: a placement that
            // has not been computed, a camera mid-resize and a matrix that has gone to NaN all
            // produce widths no zoom will ever have, and swapping buffers on one of those would be a
            // change of geometry for no reason at all.
            var selector = new NeighbourhoodMeshLodSelector();
            selector.Select(60f);
            var held = selector.Current;
            foreach (var width in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
            {
                Assert.True(NeighbourhoodMeshLod.SameLevel(held, selector.Select(width)),
                    "a width of " + width + " changed the level");
            }
        }

        [Fact]
        public void ASingleLevelChangeIsBoundedPerZoomStepByTheFrameFloor()
        {
            // The belt to the deadband's braces, and it is what makes a fast zoom readable rather
            // than a strobe: a level cannot change more often than this, whatever the width is
            // doing. Zero is the default so the deadband alone is the rule a sweep sees.
            Assert.Equal(0, NeighbourhoodMeshLodSelector.DefaultMinFramesBetweenChanges);

            var selector = new NeighbourhoodMeshLodSelector();
            selector.Select(200f);
            var changes = selector.Changes;
            for (var tiles = 200f; tiles < 800f; tiles += 2f) selector.Select(tiles, 4);
            Assert.True(selector.Changes - changes <= 2,
                "a zoom across three boundaries changed level " + (selector.Changes - changes)
                + " times with a four frame floor");
        }

        [Fact]
        public void TheSliceGridIsTheCamerasRegionAndNothingElse()
        {
            // The queue is told where to look by a tile rectangle, and the answer has to be every
            // slice the camera can see, not the ones it happened to start inside: a region whose
            // corners fall in four different slices has to queue all four, or the ground at one
            // corner of the view is refined and the other three are not.
            var region = new Rectangle(250, 220, 48, 48);
            var slices = NeighbourhoodCityMeshBuilder.SlicesInRegion(region);
            Assert.True(slices.Length >= 4, "a 48x48 region straddling a slice grid returned "
                + slices.Length + " slices");

            //every slice the walk returns overlaps the region, and the first and the last bracket
            //it - a walk that returned the slices the region merely touches at one corner would
            //leave the opposite corner of the view unrefined
            foreach (var slice in slices)
            {
                var b = NeighbourhoodCityMeshBuilder.SliceBounds(slice);
                Assert.True(b.Left <= region.Right && b.Right >= region.Left
                    && b.Top <= region.Bottom && b.Bottom >= region.Top,
                    "slice " + slice + " at " + b + " does not overlap " + region);
            }
            var first = NeighbourhoodCityMeshBuilder.SliceBounds(slices[0]);
            var last = NeighbourhoodCityMeshBuilder.SliceBounds(slices[slices.Length - 1]);
            Assert.True(first.Left <= region.Left && first.Right >= region.Left,
                "the first slice does not contain the region's left edge");
            Assert.True(last.Right >= region.Right && last.Left <= region.Right,
                "the last slice does not contain the region's right edge");

            //ascending, and no duplicates, because the queue is sorted by distance afterwards and a
            //duplicate would be built twice
            for (int i = 1; i < slices.Length; i++)
            {
                Assert.True(slices[i] > slices[i - 1],
                    "SlicesInRegion returned " + slices[i] + " after " + slices[i - 1]);
            }

            //a rectangle entirely off the map, and one on the far corner
            Assert.Equal(0, NeighbourhoodCityMeshBuilder.SlicesInRegion(
                new Rectangle(-100, -100, 10, 10)).Length);
            Assert.Equal(0, NeighbourhoodCityMeshBuilder.SlicesInRegion(
                new Rectangle(NeighbourhoodCityMeshBuilder.MapSize, 0, 10, 10)).Length);
            Assert.Equal(1, NeighbourhoodCityMeshBuilder.SlicesInRegion(
                new Rectangle(0, 0, 1, 1)).Length);
        }

        private static long Key(int x, int z)
        {
            return (long)x * 100000L + z;
        }

        /// <summary>
        /// A slice well inside the isometric diamond, so the geometry it builds is ground rather
        /// than fade skirt.
        /// </summary>
        private static int MiddleSlice()
        {
            var x = NeighbourhoodCityMeshBuilder.SliceSize * 8;
            var y = NeighbourhoodCityMeshBuilder.SliceSize * 7;
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(x + 8, y + 8),
                "the test's own slice is outside the drawn map");
            return NeighbourhoodCityMeshBuilder.SliceIndex(8, 7);
        }
    }
}
