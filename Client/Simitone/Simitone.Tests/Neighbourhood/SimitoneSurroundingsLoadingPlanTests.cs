/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using System.Linq;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The pure halves of the surround's loading: which blank neighbours are topped up on later
    /// frames and how a failing one is handled, which lots feed the facade cache, the per-slot
    /// mesh cache key, and the chunk table the crop leaves for the frustum cut.
    /// </summary>
    public class SimitoneSurroundingsLoadingPlanTests
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

        private static NeighbourhoodMap FlatMap()
        {
            var map = NeighbourhoodMap.Create(MapSize);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;
            return map;
        }

        // ---- the later-frame latch ------------------------------------------------------------

        [Fact]
        public void TopUpServesNeighboursInTheOrderGivenOnePerSpacingWindow()
        {
            var topUp = new SimitoneHollowTopUp();
            topUp.Begin(new short[] { 7, 3, 9 });
            Assert.Equal(3, topUp.PendingCount);

            short lot;
            Assert.True(topUp.TryNext(10, out lot));
            Assert.Equal(7, lot);
            topUp.MarkBuilt(7, 10);

            //inside the spacing window nothing is offered, so builds never run back to back
            for (long f = 10; f < 10 + SimitoneHollowTopUp.MinFramesBetweenBuilds; f++)
            {
                Assert.False(topUp.TryNext(f, out lot));
            }
            Assert.True(topUp.TryNext(10 + SimitoneHollowTopUp.MinFramesBetweenBuilds, out lot));
            Assert.Equal(3, lot);
        }

        [Fact]
        public void ABuiltNeighbourIsNeverOfferedAgain()
        {
            var topUp = new SimitoneHollowTopUp();
            topUp.Begin(new short[] { 1, 2 });
            short lot;
            long frame = 0;
            var seen = new List<short>();
            for (int i = 0; i < 50 && topUp.TryNext(frame, out lot); i++)
            {
                seen.Add(lot);
                topUp.MarkBuilt(lot, frame);
                frame += SimitoneHollowTopUp.MinFramesBetweenBuilds;
            }
            Assert.Equal(new short[] { 1, 2 }, seen.ToArray());
            Assert.True(topUp.IsComplete);
            Assert.Equal(2, topUp.BuiltCount);
            Assert.False(topUp.TryNext(frame + 1000, out lot));
        }

        [Fact]
        public void AFailingNeighbourIsRetriedAfterADelayThenGivenUpOnNotRetriedEveryFrame()
        {
            var topUp = new SimitoneHollowTopUp();
            topUp.Begin(new short[] { 5 });
            short lot;
            Assert.True(topUp.TryNext(0, out lot));
            Assert.True(topUp.MarkFailed(5, 0), "the first failure earns one more attempt");

            //not offered again until the delay has passed
            for (long f = 1; f < SimitoneHollowTopUp.RetryDelayFrames; f++)
            {
                Assert.False(topUp.TryNext(f, out lot));
            }
            Assert.True(topUp.TryNext(SimitoneHollowTopUp.RetryDelayFrames, out lot));
            Assert.False(topUp.MarkFailed(5, SimitoneHollowTopUp.RetryDelayFrames),
                "the attempt budget is spent");
            Assert.True(topUp.IsComplete);
            Assert.Equal(1, topUp.GaveUpCount);
            Assert.False(topUp.TryNext(SimitoneHollowTopUp.RetryDelayFrames * 10, out lot));
        }

        [Fact]
        public void AFailureDoesNotHoldBackTheNeighboursBehindIt()
        {
            var topUp = new SimitoneHollowTopUp();
            topUp.Begin(new short[] { 5, 6 });
            short lot;
            Assert.True(topUp.TryNext(0, out lot));
            Assert.Equal(5, lot);
            topUp.MarkFailed(5, 0);
            Assert.True(topUp.TryNext(SimitoneHollowTopUp.MinFramesBetweenBuilds, out lot));
            Assert.Equal(6, lot);
        }

        [Fact]
        public void AnUnreadableNeighbourIsGivenUpOnAtOnce()
        {
            var topUp = new SimitoneHollowTopUp();
            topUp.Begin(new short[] { 4 });
            short lot;
            Assert.True(topUp.TryNext(0, out lot));
            topUp.MarkUnreadable(4, 0);
            Assert.True(topUp.IsComplete);
            Assert.Equal(1, topUp.GaveUpCount);
            Assert.False(topUp.TryNext(1000, out lot));
        }

        [Fact]
        public void BeginListsALotOnceAndClearEmptiesIt()
        {
            var topUp = new SimitoneHollowTopUp();
            topUp.Begin(new short[] { 2, 2, 3, 2 });
            Assert.Equal(2, topUp.PendingCount);
            topUp.Clear();
            Assert.True(topUp.IsComplete);
            topUp.Begin(null);
            Assert.True(topUp.IsComplete);
        }

        // ---- the far-facade feed --------------------------------------------------------------

        [Fact]
        public void TheFeedHasEveryLotInTheRegionAndTheNearestFarOnesCapped()
        {
            var map = FlatMap();
            var here = Lot(68, 200, 300);
            var lots = new List<NeighbourhoodLot> { here };
            lots.Add(Lot(69, 200 + 64 + 10, 300));       //inside the 40-tile region
            //a row of lots far to the east, numbered so that the number order is NOT the distance order
            for (short i = 0; i < 6; i++) lots.Add(Lot((short)(100 - i), 330 + i * 20, 300));
            var layout = LayoutOf(lots.ToArray());

            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var fed = SimitoneSurroundingsFacadeFeed.LotsToFeed(layout, placement.Cull, 68, farCap: 3);

            Assert.DoesNotContain(fed, l => l.Number == 68);
            Assert.Contains(fed, l => l.Number == 69);
            var region = placement.Cull.LotsInRange(layout, (short)68).Select(l => l.Number).ToList();
            var far = fed.Where(l => !region.Contains(l.Number)).Select(l => l.Number).ToList();
            Assert.Equal(3, far.Count);
            //nearest first: the row's lot 100 is the closest of the far ones, then 99, then 98
            Assert.Equal(new short[] { 100, 99, 98 }, far.ToArray());
            Assert.Equal(fed.Count, fed.Select(l => l.Number).Distinct().Count());
        }

        [Fact]
        public void AFarCapOfZeroIsTheOldRegionOnlyFeedAndANullLayoutIsEmpty()
        {
            var map = FlatMap();
            var here = Lot(68, 200, 300);
            var layout = LayoutOf(here, Lot(69, 200 + 64 + 10, 300), Lot(70, 450, 300));
            var placement = SimitoneSurroundingsPlacement.For(map, here);

            var fed = SimitoneSurroundingsFacadeFeed.LotsToFeed(layout, placement.Cull, 68, farCap: 0);
            Assert.Equal(
                placement.Cull.LotsInRange(layout, (short)68).Select(l => l.Number).ToArray(),
                fed.Select(l => l.Number).ToArray());
            Assert.Empty(SimitoneSurroundingsFacadeFeed.LotsToFeed(null, placement.Cull, 68));
        }

        [Fact]
        public void TheFeedIsBoundedByTheCapWhateverTheNeighbourhoodSize()
        {
            var map = FlatMap();
            var here = Lot(1, 20, 20);
            var lots = new List<NeighbourhoodLot> { here };
            for (short i = 2; i < 60; i++) lots.Add(Lot(i, 20 + (i % 8) * 70, 20 + (i / 8) * 70));
            var layout = LayoutOf(lots.ToArray());
            var placement = SimitoneSurroundingsPlacement.For(map, here);

            var fed = SimitoneSurroundingsFacadeFeed.LotsToFeed(layout, placement.Cull, 1);
            var region = placement.Cull.LotsInRange(layout, (short)1).Count;
            Assert.True(fed.Count <= region + SimitoneSurroundingsFacadeFeed.FarFacadeCap);
            Assert.True(fed.Count > region, "the far ring is fed when there are far lots");
        }

        // ---- the per-slot mesh cache ----------------------------------------------------------

        [Fact]
        public void TheMeshCacheAnswersTheSameSlotMapAndEpochAndRebuildsWhenAnyOfThemMoves()
        {
            SimitoneSurroundingsMeshCache.Clear();
            var map = NeighbourhoodMap.Create(MapSize);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var builds = 0;
            System.Func<NeighbourhoodMap, NeighbourhoodCityMesh> build = m => { builds++; return mesh; };
            bool hit;

            Assert.Same(mesh, SimitoneSurroundingsMeshCache.GetOrBuild(5, map, 0, build, out hit));
            Assert.False(hit);
            Assert.Same(mesh, SimitoneSurroundingsMeshCache.GetOrBuild(5, map, 0, build, out hit));
            Assert.True(hit);
            Assert.Equal(1, builds);

            //an invalidation bumps the slot's epoch: the entry is stale
            SimitoneSurroundingsMeshCache.GetOrBuild(5, map, 1, build, out hit);
            Assert.False(hit);
            Assert.Equal(2, builds);

            //a regenerated map is a different object, even in the same slot at the same epoch
            var regenerated = NeighbourhoodMap.Create(MapSize);
            SimitoneSurroundingsMeshCache.GetOrBuild(5, regenerated, 1, build, out hit);
            Assert.False(hit);
            Assert.Equal(3, builds);
            Assert.Equal(1, SimitoneSurroundingsMeshCache.Count);
            SimitoneSurroundingsMeshCache.Clear();
        }

        [Fact]
        public void TheMeshCacheIsBoundedAndNeverKeepsAnUnslottedMesh()
        {
            SimitoneSurroundingsMeshCache.Clear();
            var mesh = NeighbourhoodCityMeshBuilder.Build(NeighbourhoodMap.Create(MapSize));
            var builds = 0;
            System.Func<NeighbourhoodMap, NeighbourhoodCityMesh> build = m => { builds++; return mesh; };
            bool hit;
            var maps = new List<NeighbourhoodMap>();
            for (int slot = 0; slot < SimitoneSurroundingsMeshCache.Capacity + 1; slot++)
            {
                var map = NeighbourhoodMap.Create(MapSize);
                maps.Add(map);
                SimitoneSurroundingsMeshCache.GetOrBuild(slot, map, 0, build, out hit);
            }
            Assert.Equal(SimitoneSurroundingsMeshCache.Capacity, SimitoneSurroundingsMeshCache.Count);

            //the oldest slot was evicted, the newest is still there
            SimitoneSurroundingsMeshCache.GetOrBuild(0, maps[0], 0, build, out hit);
            Assert.False(hit);
            SimitoneSurroundingsMeshCache.GetOrBuild(SimitoneSurroundingsMeshCache.Capacity, maps[SimitoneSurroundingsMeshCache.Capacity], 0, build, out hit);
            Assert.True(hit);

            var before = SimitoneSurroundingsMeshCache.Count;
            SimitoneSurroundingsMeshCache.GetOrBuild(-1, maps[0], 0, build, out hit);
            SimitoneSurroundingsMeshCache.GetOrBuild(-1, maps[0], 0, build, out hit);
            Assert.False(hit);
            Assert.Equal(before, SimitoneSurroundingsMeshCache.Count);
            SimitoneSurroundingsMeshCache.Clear();
        }

        // ---- the chunk table the crop leaves for the frustum cut -------------------------------

        [Fact]
        public void TheCropKeepsEachSourceChunksQuadsTogetherInATableTheDrawCanCut()
        {
            var map = FlatMap();
            var here = Lot(68, 200, 300);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var crop = SimitoneSurroundingsCrop.Crop(mesh, placement.DrawnRegion);

            Assert.NotNull(crop.LayerChunkStarts);
            for (int layer = 0; layer < NeighbourhoodCityMeshBuilder.LayerCount; layer++)
            {
                var starts = crop.LayerChunkStarts[layer];
                Assert.NotNull(starts);
                Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount + 1, starts.Length);
                Assert.Equal(0, starts[0]);
                Assert.Equal(crop.LayerIndices[layer].Length, starts[starts.Length - 1]);
                for (int c = 1; c < starts.Length; c++)
                {
                    Assert.True(starts[c] >= starts[c - 1], "layer " + layer + " chunk " + c + " goes backwards");
                    Assert.Equal(0, starts[c] % 6);
                }
            }

            Assert.NotNull(crop.RoadChunkStarts);
            Assert.Equal(crop.RoadIndices.Length, crop.RoadAlwaysIndices + crop.RoadChunkStarts[crop.RoadChunkStarts.Length - 1]);
        }

        [Fact]
        public void ACutToOneChunkSubmitsOnlyThatChunksQuadsAndTheyLieInsideItsBounds()
        {
            var map = FlatMap();
            var here = Lot(68, 200, 300);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var mesh = NeighbourhoodCityMeshBuilder.Build(map);
            var crop = SimitoneSurroundingsCrop.Crop(mesh, placement.DrawnRegion);

            //a chunk well inside the diamond, over grass (layer 0), away from the bank at the lot
            var chunk = 20 + 20 * NeighbourhoodCityMeshBuilder.ChunksPerAxis;
            var starts = crop.LayerChunkStarts[0];
            var ranges = new List<NeighbourhoodMeshDrawRange>();
            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(starts, new List<int> { chunk }, ranges);
            Assert.Single(ranges);
            Assert.True(ranges[0].PrimitiveCount > 0);
            Assert.True(ranges[0].PrimitiveCount < crop.LayerPrimitives[0] / 100,
                "one chunk of a 1024-chunk map is a small share of the layer");

            //every vertex in the range is inside the chunk's own bounds (skirt included)
            var bounds = NeighbourhoodCityMeshBuilder.ChunkBounds(chunk);
            var vertices = crop.LayerVertices[0];
            var indices = crop.LayerIndices[0];
            for (int i = ranges[0].StartIndex; i < ranges[0].StartIndex + ranges[0].IndexCount; i++)
            {
                var p = vertices[indices[i]].Position;
                Assert.True(p.X >= bounds.X - 1 && p.X <= bounds.X + bounds.Width + 1, "x " + p.X);
                Assert.True(p.Z >= bounds.Y - 1 && p.Z <= bounds.Y + bounds.Height + 1, "z " + p.Z);
            }
        }

        [Fact]
        public void EveryChunkTogetherIsTheWholeLayerSoAFullFrustumDrawsExactlyWhatItDidBefore()
        {
            var map = FlatMap();
            var here = Lot(68, 200, 300);
            var placement = SimitoneSurroundingsPlacement.For(map, here);
            var crop = SimitoneSurroundingsCrop.Crop(NeighbourhoodCityMeshBuilder.Build(map), placement.DrawnRegion);

            var all = Enumerable.Range(0, NeighbourhoodCityMeshBuilder.ChunkCount).ToList();
            var ranges = new List<NeighbourhoodMeshDrawRange>();
            for (int layer = 0; layer < NeighbourhoodCityMeshBuilder.LayerCount; layer++)
            {
                NeighbourhoodCityMeshBuilder.VisibleDrawRanges(crop.LayerChunkStarts[layer], all, ranges);
                var total = ranges.Sum(r => r.PrimitiveCount);
                Assert.Equal(crop.LayerPrimitives[layer], total);
            }
        }
    }
}
