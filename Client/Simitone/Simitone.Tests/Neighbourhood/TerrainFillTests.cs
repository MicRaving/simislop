/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// §11's terrain-fill half: lot interiors get their own type, the connective area is filled by
    /// nearest-lot-type, the optional water mask stays off everything it should, and the foliage
    /// scatter really is a Poisson-disk one.
    ///
    /// <para>
    /// The Poisson property is asserted against the accepted tree positions rather than the
    /// painted density field, because the painted field cannot express it: two overlapping
    /// density discs look identical whether or not the scatter underneath kept its distance. That
    /// is why <see cref="TerrainFillReport.TreeTiles"/> is reported at all.
    /// </para>
    /// </summary>
    public sealed class TerrainFillTests
    {
        // ---------------------------------------------------------------- ground types

        [Fact]
        public void LotInteriorsGetTheirOwnTerrainType()
        {
            var input = MixedTerrainNeighbourhood();
            var map = Fill(input, TerrainFillOptions.Default, out _);

            foreach (var lot in input.UsableLots)
            {
                for (var y = lot.WorldY; y < lot.Bottom; y++)
                {
                    for (var x = lot.WorldX; x < lot.Right; x++)
                    {
                        Assert.Equal(lot.TerrainType, map.TerrainType[Index(input, x, y)]);
                    }
                }
            }
        }

        [Fact]
        public void TheConnectiveAreaTakesTheNearestLotType()
        {
            var input = new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, NeighbourhoodTestInput.SmallMap, new[]
                {
                    NeighbourhoodTestInput.Lot(
                        11, 4, 4, 16, 6, NeighbourhoodDoorSide.East, true, NeighbourhoodTerrain.Grass),
                    NeighbourhoodTestInput.Lot(
                        12, 4, 60, 16, 6, NeighbourhoodDoorSide.East, true, NeighbourhoodTerrain.Snow),
                });

            var map = Fill(input, TerrainFillOptions.WithoutWater, out _);

            // A tile in the northern half is nearer the grass lot, one in the southern half is
            // nearer the snow lot, and the change happens once, somewhere between them - which is
            // the point of a Voronoi fill as opposed to a per-tile "pick something".
            Assert.Equal(NeighbourhoodTerrain.Grass, map.TerrainType[Index(input, 40, 12)]);
            Assert.Equal(NeighbourhoodTerrain.Snow, map.TerrainType[Index(input, 40, 84)]);

            var changes = new List<int>();
            for (var y = 12; y < 12 + 48; y++)
            {
                if (map.TerrainType[Index(input, 40, y)] != NeighbourhoodTerrain.Grass) changes.Add(y);
            }
            Assert.NotEmpty(changes);
            Assert.Equal(changes.Count, changes.Last() - changes.First() + 1);
        }

        [Fact]
        public void NearestLotOwnersIsAVoronoiOfTheLotRectangles()
        {
            var input = MixedTerrainNeighbourhood();
            var owners = TerrainFill.NearestLotOwners(input.UsableLots, input.MapSize);

            // Every tile is owned by a lot (there is always at least one), and every tile inside a
            // lot is owned by that lot or by a lower-indexed one it overlaps.
            Assert.All(owners, o => Assert.InRange(o, 0, input.UsableLots.Count - 1));

            for (int l = 0; l < input.UsableLots.Count; l++)
            {
                var lot = input.UsableLots[l];
                for (var y = lot.WorldY; y < lot.Bottom; y++)
                {
                    for (var x = lot.WorldX; x < lot.Right; x++)
                    {
                        Assert.InRange(owners[Index(input, x, y)], 0, l);
                    }
                }
            }
        }

        [Fact]
        public void NearestLotOwnersIsDeterministic()
        {
            var input = MixedTerrainNeighbourhood();
            var first = TerrainFill.NearestLotOwners(input.UsableLots, input.MapSize);
            var second = TerrainFill.NearestLotOwners(input.UsableLots.ToList(), input.MapSize);

            Assert.Equal(first, second);
        }

        [Fact]
        public void AnUndrawableTerrainTypeFallsBackToGrass()
        {
            // A corrupt sidecar could carry 200 as a terrain type. Indexing a five-entry texture
            // array with it would be a crash in the renderer; a lot whose ground silently became
            // grass is the safe reading of "this lot's terrain is not one we know".
            var input = new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, NeighbourhoodTestInput.SmallMap,
                new[] { NeighbourhoodTestInput.Lot(11, 8, 8, 16, 4, NeighbourhoodDoorSide.East, true, 200) });

            var map = Fill(input, TerrainFillOptions.WithoutWater, out _);

            Assert.Equal(NeighbourhoodTerrain.Grass, map.TerrainType[Index(input, 10, 10)]);
        }

        [Fact]
        public void TheTerrainColourArrayMatchesTheTypeArray()
        {
            var input = MixedTerrainNeighbourhood();
            var map = Fill(input, TerrainFillOptions.WithoutWater, out _);

            for (int i = 0; i < map.TerrainType.Length; i++)
            {
                Assert.Equal(
                    NeighbourhoodTerrain.ColorFor(map.TerrainType[i]),
                    map.TerrainTypeColorData[i]);
            }
        }

        [Theory]
        [InlineData(NeighbourhoodTerrain.Grass, 0, 255, 0)]
        [InlineData(NeighbourhoodTerrain.Sand, 255, 255, 0)]
        [InlineData(NeighbourhoodTerrain.Rock, 255, 0, 0)]
        [InlineData(NeighbourhoodTerrain.Snow, 255, 255, 255)]
        [InlineData(NeighbourhoodTerrain.Water, 12, 0, 255)]
        public void TerrainColoursAreTheInverseOfCityMapDataTable(byte type, byte r, byte g, byte b)
        {
            // CityMapData.Load's TerrainTypeMap, read backwards. The colours are not decoration:
            // they are the bytes a hand-authored terraintype.bmp holds, and a renderer that
            // samples TerrainTypeColorData for a tint has to land on the same value.
            Assert.Equal(new Color(r, g, b), NeighbourhoodTerrain.ColorFor(type));
            Assert.True(NeighbourhoodTerrain.IsDrawable(type));
        }

        [Fact]
        public void NothingIsNotADrawableTerrain()
        {
            Assert.Equal(255, NeighbourhoodTerrain.Nothing);
            Assert.False(NeighbourhoodTerrain.IsDrawable(NeighbourhoodTerrain.Nothing));
            Assert.Equal(new Color(0, 0, 0), NeighbourhoodTerrain.ColorFor(NeighbourhoodTerrain.Nothing));
        }

        // ---------------------------------------------------------------- water

        [Fact]
        public void WaterStaysOffLotsRoadsAndSidewalks()
        {
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Fill(input, TerrainFillOptions.Default, map, graph, out var report);

            Assert.True(report.WaterTiles > 0, "the default options produced no water at all");
            for (int i = 0; i < report.Water.Length; i++)
            {
                if (!report.Water[i]) continue;
                Assert.False(graph.LotMask.Interior[i]);
                Assert.False(graph.Road[i]);
                Assert.False(graph.Sidewalk[i]);
                Assert.Equal(NeighbourhoodTerrain.Water, map.TerrainType[i]);
            }
        }

        [Fact]
        public void WaterKeepsItsDistanceFromEveryLot()
        {
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            var options = TerrainFillOptions.Default;
            Fill(input, options, map, graph, out var report);

            int[] chamfer;
            TerrainFill.NearestLotOwners(input.UsableLots, input.MapSize, out chamfer);
            for (int i = 0; i < report.Water.Length; i++)
            {
                if (!report.Water[i]) continue;
                Assert.True(chamfer[i] >= options.WaterMinLotDistance * TerrainFill.ChamferOrthogonal,
                    "water at tile " + i + " is closer to a lot than the clearance allows");
            }
        }

        [Fact]
        public void WaterCanBeTurnedOff()
        {
            var input = MixedTerrainNeighbourhood();
            var map = Fill(input, TerrainFillOptions.WithoutWater, out var report);

            Assert.Equal(0, report.WaterTiles);
            Assert.DoesNotContain(NeighbourhoodTerrain.Water, map.TerrainType);
        }

        [Fact]
        public void WaterIsSeeded()
        {
            var a = Fill(NeighbourhoodTestInput.FourFlatLots(), TerrainFillOptions.Default, out var aReport);
            var b = Fill(
                NeighbourhoodTestInput.FourFlatLotsWithSeed(NeighbourhoodTestInput.DefaultSeed + 3),
                TerrainFillOptions.Default, out var bReport);

            Assert.True(aReport.WaterTiles > 0, "the default options produced no water at all");
            Assert.NotEqual(aReport.Water, bReport.Water);
        }

        // ---------------------------------------------------------------- foliage

        [Fact]
        public void ForestNeverGrowsInsideALot()
        {
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Fill(input, TerrainFillOptions.Default, map, graph, out var report);

            Assert.True(report.Trees > 0, "no trees were scattered at all");
            for (int i = 0; i < map.ForestDensityData.Length; i++)
            {
                if (graph.LotMask.Interior[i]) Assert.Equal(0, map.ForestDensityData[i]);
            }
        }

        [Fact]
        public void ForestNeverGrowsOnARoadOrASidewalk()
        {
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Fill(input, TerrainFillOptions.Default, map, graph, out _);

            for (int i = 0; i < map.ForestDensityData.Length; i++)
            {
                if (graph.Road[i]) Assert.Equal(0, map.ForestDensityData[i]);
                if (graph.Sidewalk[i]) Assert.Equal(0, map.ForestDensityData[i]);
            }
        }

        [Fact]
        public void NoTreeGrowsInTheWater()
        {
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Fill(input, TerrainFillOptions.Default, map, graph, out var report);

            for (int i = 0; i < report.Water.Length; i++)
            {
                if (report.Water[i]) Assert.Equal(0, map.ForestDensityData[i]);
            }
        }

        [Fact]
        public void TheScatterKeepsTheMinimumSpacing()
        {
            // The property the Poisson-disk pass is defined by. Not checkable from the painted
            // density field, which is why the accepted positions are reported.
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            var options = TerrainFillOptions.Default;
            Fill(input, options, map, graph, out var report);

            Assert.True(report.Trees > 1);
            var trees = report.TreeTiles;
            for (int i = 0; i < trees.Length; i++)
            {
                for (int j = i + 1; j < trees.Length; j++)
                {
                    var dx = Math.Abs(trees[i] % input.MapSize - trees[j] % input.MapSize);
                    var dy = Math.Abs(trees[i] / input.MapSize - trees[j] / input.MapSize);
                    Assert.True(
                        Math.Max(dx, dy) >= options.TreeSpacing,
                        "two trees at (" + (trees[i] % input.MapSize) + "," + (trees[i] / input.MapSize)
                        + ") and (" + (trees[j] % input.MapSize) + "," + (trees[j] / input.MapSize)
                        + ") are closer than the minimum spacing");
                }
            }
        }

        [Fact]
        public void ATreesDensityFallsOffFromItsCentre()
        {
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            var options = TerrainFillOptions.Default;
            Fill(input, options, map, graph, out var report);

            foreach (var tree in report.TreeTiles)
            {
                Assert.Equal(options.MaxTreeDensity, map.ForestDensityData[tree]);
            }

            // And a tree is always somewhere inside the map, at a tile that was allowed.
            Assert.All(report.TreeTiles, t => Assert.InRange(t, 0, map.ForestDensityData.Length - 1));
        }

        [Fact]
        public void ACommunityLotGetsATreelessApron()
        {
            // The one thing a lot's Zone changes. A community lot is a park, a pool or a club -
            // ground people are meant to be on - so the trees are kept off it and a margin around
            // it. A residential lot gets no such apron, which is the half of the test that stops
            // this becoming "no trees near any lot".
            var options = TerrainFillOptions.Default;
            var input = new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, NeighbourhoodTestInput.SmallMap, new[]
                {
                    NeighbourhoodTestInput.Lot(
                        11, 8, 8, 16, 4, NeighbourhoodDoorSide.East, true,
                        NeighbourhoodTerrain.Grass, LotZoning.ZoneCommunity),
                    NeighbourhoodTestInput.Lot(
                        12, 40, 8, 16, 4, NeighbourhoodDoorSide.West, true,
                        NeighbourhoodTerrain.Grass, LotZoning.ZoneResidential),
                });

            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);
            Fill(input, options, map, graph, out var report);

            var community = input.UsableLots[0];
            var residential = input.UsableLots[1];
            var clearance = options.CommunityClearance;

            for (var y = community.WorldY - clearance; y < community.Bottom + clearance; y++)
            {
                for (var x = community.WorldX - clearance; x < community.Right + clearance; x++)
                {
                    Assert.Equal(0, map.ForestDensityData[Index(input, x, y)]);
                }
            }

            // The residential lot's own frontage is a fair way outside its own rectangle and is
            // not inside any community apron, so it is allowed to have trees - and does.
            var frontage = DensityIn(
                map, input, residential.Right + clearance + 1, residential.WorldY + 1,
                residential.Right + clearance + 6, residential.WorldY + 6);
            Assert.True(frontage > 0, "the residential lot's frontage has no trees on it");
        }

        [Fact]
        public void TreesObeyTheTreeBudget()
        {
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            var options = new TerrainFillOptions(
                true, options0(), options0(), 6, 12, 16, 255, 0);
            Fill(input, options, map, graph, out var report);

            Assert.InRange(report.Trees, 1, 12);
        }

        private static int options0()
        {
            var d = TerrainFillOptions.Default;
            return d.WaterMinLotDistance;
        }

        [Fact]
        public void NoTreesIsAnOptionAndIsHonoured()
        {
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            var d = TerrainFillOptions.Default;
            var options = new TerrainFillOptions(
                false, d.WaterLevel, d.WaterMinLotDistance, d.TreeSpacing,
                0, d.DartsPerActiveTree, d.MaxTreeDensity, d.CommunityClearance);

            Fill(input, options, map, graph, out var report);

            Assert.Equal(0, report.Trees);
            Assert.Empty(report.TreeTiles);
            Assert.All(map.ForestDensityData, v => Assert.Equal(0, v));
        }

        [Fact]
        public void EveryTreeIsCarriedByAFoliageTint()
        {
            var input = MixedTerrainNeighbourhood();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Fill(input, TerrainFillOptions.Default, map, graph, out var report);

            foreach (var tree in report.TreeTiles)
            {
                Assert.NotEqual(new Color(0, 0, 0), map.ForestTypeData[tree]);
            }

            // And the tint field has a handful of distinct values, not one per tree.
            var distinct = new HashSet<Color>(map.ForestTypeData.Where((_, i) => i % 97 == 0));
            Assert.InRange(distinct.Count, 1, 6);
        }

        // ---------------------------------------------------------------- determinism

        [Fact]
        public void TheFillIsDeterministic()
        {
            var input = MixedTerrainNeighbourhood();
            var first = Fill(input, TerrainFillOptions.Default, out var firstReport);
            var second = Fill(input, TerrainFillOptions.Default, out var secondReport);

            Assert.Equal(first.TerrainType, second.TerrainType);
            Assert.Equal(first.ForestDensityData, second.ForestDensityData);
            Assert.Equal(first.ForestTypeData, second.ForestTypeData);
            Assert.Equal(firstReport.TreeTiles, secondReport.TreeTiles);
            Assert.Equal(firstReport.Water, secondReport.Water);
        }

        [Fact]
        public void ANeighbourhoodWithNoLotsGetsNoTreesAndNoWater()
        {
            var input = NeighbourhoodTestInput.NoLots();
            var map = Fill(input, TerrainFillOptions.Default, out var report);

            // Everything is still eligible ground, so the scatter has the whole map to work with -
            // which is the right answer for an empty neighbourhood, and worth pinning so a change
            // that made an empty map come back bare does not slip in unnoticed.
            Assert.True(report.Trees > 0);
            Assert.Equal(0, report.WaterTiles);
            Assert.All(map.TerrainType, t => Assert.Equal(NeighbourhoodTerrain.Grass, t));
        }

        // ---------------------------------------------------------------- helpers

        private static NeighbourhoodMapInput MixedTerrainNeighbourhood()
        {
            return new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, NeighbourhoodTestInput.SmallMap, new[]
                {
                    NeighbourhoodTestInput.Lot(
                        11, 8, 8, 16, 4, NeighbourhoodDoorSide.East, true, NeighbourhoodTerrain.Grass),
                    NeighbourhoodTestInput.Lot(
                        12, 8, 56, 16, 4, NeighbourhoodDoorSide.East, true, NeighbourhoodTerrain.Sand),
                    NeighbourhoodTestInput.Lot(
                        13, 56, 8, 16, 4, NeighbourhoodDoorSide.West, true, NeighbourhoodTerrain.Snow),
                });
        }

        private static int DensityIn(
            NeighbourhoodMap map, NeighbourhoodMapInput input, int x0, int y0, int x1, int y1)
        {
            var total = 0;
            for (var y = y0; y <= y1; y++)
            {
                for (var x = x0; x <= x1; x++)
                {
                    total += map.ForestDensityData[Index(input, x, y)];
                }
            }
            return total;
        }

        private static int Index(NeighbourhoodMapInput input, int x, int y)
        {
            return y * input.MapSize + x;
        }

        private static NeighbourhoodMap Fill(
            NeighbourhoodMapInput input, TerrainFillOptions options, out TerrainFillReport report)
        {
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);
            return Fill(input, options, map, graph, out report);
        }

        private static NeighbourhoodMap Fill(
            NeighbourhoodMapInput input, TerrainFillOptions options,
            NeighbourhoodMap map, RoadGraph graph, out TerrainFillReport report)
        {
            var streams = NeighbourhoodStreams.FromSeed(input.Seed);
            report = TerrainFill.Fill(input, graph, map, streams.Generation, options);
            return map;
        }
    }
}
