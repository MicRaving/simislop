/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.IO;
using System.Linq;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// §11's <c>NeighbourhoodMapGeneratorTests</c>: same seed and layout gives identical arrays, a
    /// different seed gives a different forest, lot interiors preserve their type and height, and
    /// roads never cross lot interiors.
    ///
    /// <para>
    /// The determinism tests are the load-bearing ones. Everything else in this milestone assumes
    /// that "same seed plus same layout equals the same bytes", because that is what lets the
    /// layout sidecar store a seed instead of a 512x512 raster: regeneration has to be able to
    /// throw the old map away. So the comparison is element by element across all six arrays, not
    /// a visual check and not a hash of one of them.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodMapGeneratorTests
    {
        // ---------------------------------------------------------------- determinism

        [Fact]
        public void SameSeedAndInputProduceIdenticalArrays()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var first = NeighbourhoodMapGenerator.Generate(input);
            var second = NeighbourhoodMapGenerator.Generate(
                new NeighbourhoodMapInput(
                    NeighbourhoodTestInput.DefaultSeed,
                    NeighbourhoodTestInput.SmallMap,
                    NeighbourhoodTestInput.FourFlatLots().Lots));

            Assert.True(first.ContentEquals(second), "the two maps are not byte identical");

            // Spelled out per array so a failure says which one moved, rather than "not equal".
            Assert.Equal(first.TerrainType, second.TerrainType);
            Assert.Equal(first.ElevationData, second.ElevationData);
            Assert.Equal(first.RoadData, second.RoadData);
            Assert.Equal(first.ForestDensityData, second.ForestDensityData);
            Assert.Equal(first.TerrainTypeColorData, second.TerrainTypeColorData);
            Assert.Equal(first.ForestTypeData, second.ForestTypeData);
        }

        [Fact]
        public void RegeneratingTwiceIsStableAcrossManyRuns()
        {
            // The sidecar stores a seed, not a raster, so regeneration has to be reproducible
            // every time rather than only the first time. Six rounds also catches a stage that
            // mutates shared static state, which a two-round comparison would let through.
            var reference = NeighbourhoodMapGenerator.Generate(NeighbourhoodTestInput.FourFlatLots());
            for (int i = 0; i < 5; i++)
            {
                Assert.True(reference.ContentEquals(
                    NeighbourhoodMapGenerator.Generate(NeighbourhoodTestInput.FourFlatLots())),
                    "round " + i + " differed");
            }
        }

        [Fact]
        public void DifferentSeedProducesADifferentForest()
        {
            var a = NeighbourhoodMapGenerator.Generate(NeighbourhoodTestInput.FourFlatLots());
            var b = NeighbourhoodMapGenerator.Generate(
                NeighbourhoodTestInput.FourFlatLotsWithSeed(NeighbourhoodTestInput.DefaultSeed + 1));

            Assert.NotEqual(a.ForestDensityData, b.ForestDensityData);
            Assert.NotEqual(a.ForestTypeData, b.ForestTypeData);

            // And the seed is not simply being ignored: both must actually have woods, or "not
            // equal" would be satisfied by one of them being empty.
            Assert.Contains((byte)0, a.ForestDensityData);
            Assert.Contains(a.ForestDensityData, v => v > 0);
            Assert.Contains(b.ForestDensityData, v => v > 0);
        }

        [Fact]
        public void DifferentSeedLeavesTheStreetsAlone()
        {
            // Roads come from the layout's own geometry - rectangles, door sides - and not from
            // the random stream. That is deliberate: the same neighbourhood regenerated with a new
            // seed should have the same streets and different planting, not a different road
            // layout, because nothing about the lots suggests the roads should move.
            var a = NeighbourhoodMapGenerator.Generate(NeighbourhoodTestInput.FourFlatLots());
            var b = NeighbourhoodMapGenerator.Generate(
                NeighbourhoodTestInput.FourFlatLotsWithSeed(NeighbourhoodTestInput.DefaultSeed + 7777));

            Assert.Equal(a.RoadData, b.RoadData);
            Assert.Equal(a.ElevationData, b.ElevationData);
        }

        [Fact]
        public void RetuningTheWaterMaskDoesNotMoveTheForest()
        {
            // This is the property §15's derived streams exist for, stated at the pipeline level.
            // Water and foliage each take a labelled sub-stream, so a water parameter that has no
            // effect at all - because water is off - must have no effect on anything, least of all
            // on a tree. With one shared random sequence, inserting or removing the water pass
            // would shift every draw after it and the woods would move.
            //
            // Note what this deliberately does *not* claim: turning water on does change the
            // forest, because flooded ground is not eligible for a tree. That is a real
            // dependency, not stream coupling, and asserting otherwise would be asserting that
            // trees grow in a river.
            var d = TerrainFillOptions.Default;
            var input = NeighbourhoodTestInput.FourFlatLots();

            var reference = NeighbourhoodMapGenerator.Generate(
                input, TerrainFillOptions.WithoutWater, TerrainSeamOptions.Default, out var a);
            var retuned = NeighbourhoodMapGenerator.Generate(
                input,
                new TerrainFillOptions(
                    false,
                    d.WaterLevel + 111,
                    d.WaterMinLotDistance + 5,
                    d.TreeSpacing,
                    d.MaxTrees,
                    d.DartsPerActiveTree,
                    d.MaxTreeDensity,
                    d.CommunityClearance),
                TerrainSeamOptions.Default,
                out var b);

            Assert.Equal(a.TerrainFill.TreeTiles, b.TerrainFill.TreeTiles);
            Assert.Equal(reference.ForestDensityData, retuned.ForestDensityData);
            Assert.Equal(reference.ForestTypeData, retuned.ForestTypeData);
            Assert.Equal(reference.TerrainType, retuned.TerrainType);
            Assert.Equal(reference.RoadData, retuned.RoadData);
            Assert.Equal(reference.ElevationData, retuned.ElevationData);
        }

        [Fact]
        public void LotGroundIsSeedIndependent()
        {
            // The seed moves the water and the woods. It does not move a lot's own ground: the type
            // comes from the lot record, and a neighbourhood generated twice has to have the same
            // houses on the same grass.
            var a = NeighbourhoodMapGenerator.Generate(NeighbourhoodTestInput.FourFlatLots());
            var b = NeighbourhoodMapGenerator.Generate(
                NeighbourhoodTestInput.FourFlatLotsWithSeed(NeighbourhoodTestInput.DefaultSeed + 99));

            var input = NeighbourhoodTestInput.FourFlatLots();
            foreach (var lot in input.UsableLots)
            {
                for (var y = lot.WorldY; y < lot.Bottom; y++)
                {
                    for (var x = lot.WorldX; x < lot.Right; x++)
                    {
                        var i = y * input.MapSize + x;
                        Assert.Equal(lot.TerrainType, a.TerrainType[i]);
                        Assert.Equal(a.TerrainType[i], b.TerrainType[i]);
                        Assert.Equal(a.TerrainTypeColorData[i], b.TerrainTypeColorData[i]);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- lot invariants

        [Fact]
        public void LotInteriorsKeepTheirOwnTypeAndHeight()
        {
            var input = new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, NeighbourhoodTestInput.SmallMap, new[]
                {
                    // A grass lot, a sand lot (a TS1 beach) and a snow lot (alpine), so the type
                    // really is being carried per lot rather than defaulted.
                    NeighbourhoodTestInput.Lot(
                        11, 8, 8, 16, 6, NeighbourhoodDoorSide.East, true, NeighbourhoodTerrain.Grass),
                    NeighbourhoodTestInput.Lot(
                        12, 8, 32, 16, 9, NeighbourhoodDoorSide.East, true, NeighbourhoodTerrain.Sand),
                    NeighbourhoodTestInput.Lot(
                        13, 40, 8, 16, 12, NeighbourhoodDoorSide.West, true, NeighbourhoodTerrain.Snow),
                });

            var map = NeighbourhoodMapGenerator.Generate(input);

            foreach (var lot in input.UsableLots)
            {
                for (var y = lot.WorldY; y < lot.Bottom; y++)
                {
                    for (var x = lot.WorldX; x < lot.Right; x++)
                    {
                        var i = y * input.MapSize + x;
                        Assert.Equal(lot.TerrainType, map.TerrainType[i]);
                        Assert.Equal(lot.BaseHeight, map.ElevationData[i]);
                    }
                }
            }
        }

        [Fact]
        public void LotInteriorTerrainColoursMatchTheType()
        {
            // The parity contract with CityMapData's TerrainTypeMap: the colour array is not
            // decoration, it is the inverse of the table CityMapData.Load uses, and a renderer
            // that samples it for a tint has to get the same pixel.
            var input = new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, NeighbourhoodTestInput.SmallMap, new[]
                {
                    NeighbourhoodTestInput.Lot(
                        11, 8, 8, 16, 6, NeighbourhoodDoorSide.East, true, NeighbourhoodTerrain.Sand),
                });

            var map = NeighbourhoodMapGenerator.Generate(input);
            var index = 10 * input.MapSize + 10;

            Assert.Equal(NeighbourhoodTerrain.Sand, map.TerrainType[index]);
            Assert.Equal(new Microsoft.Xna.Framework.Color(255, 255, 0), map.TerrainTypeColorData[index]);
        }

        [Fact]
        public void RoadsNeverCrossLotInteriors()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMapGenerator.Generate(input);

            foreach (var lot in input.UsableLots)
            {
                for (var y = lot.WorldY; y < lot.Bottom; y++)
                {
                    for (var x = lot.WorldX; x < lot.Right; x++)
                    {
                        Assert.Equal(0, map.RoadData[y * input.MapSize + x]);
                    }
                }
            }
        }

        [Fact]
        public void EveryLotWithAHouseIsReachable()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            NeighbourhoodMapGenerator.Generate(
                input, TerrainFillOptions.Default, TerrainSeamOptions.Default, out var report);

            Assert.Equal(4, report.RoadGraph.Entrances.Count);
            Assert.Empty(report.RoadGraph.UnreachableLots);
            Assert.Equal(1, report.RoadGraph.ComponentCount);
            Assert.Null(Record.Exception(() => report.RoadGraph.VerifyConnected()));
        }

        [Fact]
        public void ABulldozedLotKeepsItsGroundButGetsNoDriveway()
        {
            var withHouse = NeighbourhoodTestInput.FourFlatLots();
            var bulldozed = new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, NeighbourhoodTestInput.SmallMap,
                withHouse.Lots.Select(l =>
                    l.Number == 14
                        ? NeighbourhoodTestInput.Lot(
                            l.Number, l.WorldX, l.WorldY, l.Size, l.BaseHeight, l.DoorSide, false)
                        : l).ToArray());

            var map = NeighbourhoodMapGenerator.Generate(
                bulldozed, TerrainFillOptions.Default, TerrainSeamOptions.Default, out var report);

            // No driveway, and therefore no entrance and no road leading to it.
            Assert.Equal(3, report.RoadGraph.Entrances.Count);
            Assert.DoesNotContain(report.RoadGraph.Entrances, e => e.LotNumber == 14);
            Assert.Empty(report.RoadGraph.UnreachableLots);
            Assert.Equal(1, report.RoadGraph.ComponentCount);

            // But the ground is still generated: the plot is still there, it is just empty.
            var lot = bulldozed.UsableLots.First(l => l.Number == 14);
            for (var y = lot.WorldY; y < lot.Bottom; y++)
            {
                for (var x = lot.WorldX; x < lot.Right; x++)
                {
                    var i = y * bulldozed.MapSize + x;
                    Assert.Equal(0, map.RoadData[i]);
                    Assert.Equal(lot.BaseHeight, map.ElevationData[i]);
                }
            }
        }

        // ---------------------------------------------------------------- shape and edges

        [Fact]
        public void TheMapHasTheShapeTheModelPromises()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMapGenerator.Generate(input);
            var count = input.MapSize * input.MapSize;

            Assert.Equal(input.MapSize, map.Width);
            Assert.Equal(input.MapSize, map.Height);
            Assert.Equal(count, map.TerrainType.Length);
            Assert.Equal(count, map.TerrainTypeColorData.Length);
            Assert.Equal(count, map.ElevationData.Length);
            Assert.Equal(count, map.RoadData.Length);
            Assert.Equal(count, map.ForestDensityData.Length);
            Assert.Equal(count, map.ForestTypeData.Length);
        }

        [Fact]
        public void ANeighbourhoodWithNoLotsStillGenerates()
        {
            var map = NeighbourhoodMapGenerator.Generate(NeighbourhoodTestInput.NoLots());

            Assert.All(map.RoadData, r => Assert.Equal(0, r));
            Assert.All(map.ElevationData, h => Assert.Equal(0, h));
            Assert.All(map.TerrainType, t => Assert.Equal(NeighbourhoodTerrain.Grass, t));
        }

        [Fact]
        public void TheGeneratorVersionIsPinned()
        {
            // Bumped whenever a change here produces different bytes for the same seed and layout.
            // The sidecar stores it beside the seed, so this number is the contract that decides
            // whether an old generated map is regenerated or trusted.
            //
            // Version 2 was the street rewrite: the skeleton came from the gaps between lots rather
            // than from a minimum spanning tree over the doors snapped to a fixed eight-tile grid,
            // which produced almost no street at all on a real packed map.
            //
            // Version 276 is Part 8: the roads are the 2D map's own, rasterised from an inferred
            // connector mask rather than measured from the packing, and the lots are at their real
            // positions. Both change the bytes for the same seed and the same layout sidecar.
            //
            // Version 277 changes nothing about the bytes - a given seed generates exactly what it
            // generated under 276 - but the set of maps that exist changed: generation used to throw
            // for a road network it could not make one piece, so those layouts never produced a
            // raster at all. Letting them through means a map with no cached raster is now a normal
            // thing to have, and a cache written before this number must not be trusted to keep not
            // having one.
            Assert.Equal(277, NeighbourhoodMapGenerator.GeneratorVersion);
        }

        [Fact]
        public void TheDefaultMapSizeIsTheOneCityGeometryIndexes()
        {
            // CityGeometry's tile loop is hardcoded to a 512-wide array, so a generated map is
            // 512x512 or it is not a generated map.
            Assert.Equal(512, NeighbourhoodMap.DefaultSize);
        }

        // ---------------------------------------------------------------- prep seams

        [Fact]
        public void TheThreeStreamsAreDerivedFromTheSeedAndAreIndependent()
        {
            // §15: worldSeed -> {generation, simulation, foliage}, "so tuning one does not disturb
            // the others", and "A only uses generation". All three exist and are stable; only
            // generation is consumed here, and simulation and foliage are not the same stream as
            // each other or as generation - a future milestone that starts drawing from them gets
            // numbers this build would also have produced.
            var a = NeighbourhoodStreams.FromSeed(NeighbourhoodTestInput.DefaultSeed);
            var b = NeighbourhoodStreams.FromSeed(NeighbourhoodTestInput.DefaultSeed);
            var other = NeighbourhoodStreams.FromSeed(NeighbourhoodTestInput.DefaultSeed + 1);

            Assert.True(a.Matches(b));
            Assert.False(a.Matches(other));

            var states = new[] { a.Generation.State, a.Simulation.State, a.Foliage.State };
            Assert.Equal(3, states.Distinct().Count());
        }

        [Fact]
        public void ALabelledSubStreamIsStableAndIndependent()
        {
            var a = NeighbourhoodStreams.FromSeed(1234).Generation.ForLabel("forest");
            var b = NeighbourhoodStreams.FromSeed(1234).Generation.ForLabel("forest");
            var c = NeighbourhoodStreams.FromSeed(1234).Generation.ForLabel("water");

            Assert.Equal(a.State, b.State);
            Assert.NotEqual(a.State, c.State);
        }

        [Fact]
        public void DrawingFromOneStreamDoesNotMoveAnother()
        {
            // The isolation property stated as a fact rather than as a comment: advance the
            // "roads" sub-stream as far as it likes and the "forest" sub-stream is untouched.
            var root = NeighbourhoodStreams.FromSeed(99).Generation;
            var roadsBefore = root.ForLabel("roads");
            var forestBefore = root.ForLabel("forest");

            var roads = roadsBefore;
            for (int i = 0; i < 1000; i++) roads.NextULong();

            Assert.Equal(forestBefore.State, root.ForLabel("forest").State);
            Assert.Equal(roadsBefore.State, root.ForLabel("roads").State);
            Assert.NotEqual(roadsBefore.NextULong(), roads.NextULong());
        }

        [Fact]
        public void StreamsForReportsWhatAGenerationWouldUse()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            Assert.True(
                NeighbourhoodMapGenerator.StreamsFor(input).Matches(
                    NeighbourhoodStreams.FromSeed(input.Seed)));
        }

        // ---------------------------------------------------------------- debug aids

        [Fact]
        public void TheDebugDumpWritesTheFivePngs()
        {
            // CityMapData.Save writes exactly these five files under exactly these names, through
            // a Texture2D on the game thread - which is why this cannot be the only way to look at
            // a map from a test. Doing it headlessly through PngCodec means the same files are
            // available to a build server and to a developer with no GPU.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMapGenerator.Generate(input);

            var directory = Path.Combine(
                Path.GetTempPath(), "simitone-neighbourhood-" + Guid.NewGuid().ToString("N"));
            try
            {
                map.SavePng(directory);

                foreach (var name in new[]
                {
                    "roadmap.png", "elevation.png", "forestdensity.png",
                    "foresttype.png", "terraintype.png",
                })
                {
                    var file = Path.Combine(directory, name);
                    Assert.True(File.Exists(file), name + " was not written");
                    Assert.True(new FileInfo(file).Length > 0, name + " is empty");
                }

                var summary = map.WriteDebugSummary();
                Assert.Contains("road:", summary);
                Assert.Contains("elevation:", summary);
                Assert.Contains("terrainType:", summary);
            }
            finally
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void TheDebugDumpCanStretchTheElevationForEyeballing()
        {
            // The default dump is byte-faithful to CityMapData.Save, which for a real TS1
            // neighbourhood means a nearly black elevation image: the heights occupy a handful of
            // the 0-255 range. Both forms have to exist - comparable for a diff, visible for a
            // look.
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            var map = NeighbourhoodMapGenerator.Generate(input);

            var raw = Path.Combine(Path.GetTempPath(), "simitone-elev-raw-" + Guid.NewGuid().ToString("N"));
            var stretched = Path.Combine(Path.GetTempPath(), "simitone-elev-stretched-" + Guid.NewGuid().ToString("N"));
            try
            {
                map.SavePng(raw);
                map.SavePng(stretched, true);

                var rawBytes = File.ReadAllBytes(Path.Combine(raw, "elevation.png"));
                var stretchedBytes = File.ReadAllBytes(Path.Combine(stretched, "elevation.png"));

                Assert.NotEqual(rawBytes, stretchedBytes);
                Assert.True(stretchedBytes.Length > 0);
            }
            finally
            {
                if (Directory.Exists(raw)) Directory.Delete(raw, true);
                if (Directory.Exists(stretched)) Directory.Delete(stretched, true);
            }
        }

        [Fact]
        public void DescribeNamesEveryFieldOfATile()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMapGenerator.Generate(input);

            var text = map.Describe(4, 4);
            Assert.Contains("terrain=", text);
            Assert.Contains("elev=", text);
            Assert.Contains("mask=0x", text);
            Assert.Equal("out of bounds", map.Describe(-1, 0));
        }

        [Fact]
        public void CloneIsADeepCopy()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMapGenerator.Generate(input);
            var copy = map.Clone();

            Assert.True(map.ContentEquals(copy));

            copy.RoadData[0] = 15;
            copy.ElevationData[0] = 200;
            Assert.NotEqual(map.RoadData[0], copy.RoadData[0]);
            Assert.NotEqual(map.ElevationData[0], copy.ElevationData[0]);
        }

        [Fact]
        public void ContentEqualsRejectsNullAndMismatchedMaps()
        {
            var map = NeighbourhoodMapGenerator.Generate(NeighbourhoodTestInput.FourFlatLots());

            Assert.False(map.ContentEquals(null));
            Assert.False(map.ContentEquals(NeighbourhoodMap.Create(map.Width + 1)));
        }
    }
}
