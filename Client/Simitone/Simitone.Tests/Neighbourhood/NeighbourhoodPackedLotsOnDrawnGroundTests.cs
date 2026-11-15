/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Audit O1: the shelf packer and the mesh's clip have to agree about where the map is. The
    /// packer laid lots out from tile (0,0) across the whole square while the mesh drew only TSO's
    /// isometric diamond, so a 41-lot neighbourhood had 24 lots - and a 10-lot one all 10 - on
    /// ground nothing draws, and the lot view's surround showed neither house nor lot for them.
    /// Every lot is now packed inside the drawn region with a street corridor of drawn ground round
    /// it, and these tests pin that on a real neighbourhood's lot table and on synthetic ones.
    /// </summary>
    public sealed class NeighbourhoodPackedLotsOnDrawnGroundTests
    {
        private const int MapSize = NeighbourhoodCityMeshBuilder.MapSize;

        /// <summary>
        /// A real 41-lot neighbourhood's lot table - number, lot size, and the backdrop position
        /// the shelf is ordered by - as the game read it (slot 4 of the owner's install,
        /// 2026-11-15; the retail lots 1-10 and 50-80). The positions only order the shelf.
        /// </summary>
        private static readonly int[,] RealSlot4 =
        {
            { 1, 56, 480, 84 }, { 2, 56, 722, 75 }, { 3, 54, 578, 142 }, { 4, 50, 506, 177 },
            { 5, 52, 658, 214 }, { 6, 43, 456, 262 }, { 7, 48, 589, 249 }, { 8, 50, 735, 265 },
            { 9, 43, 538, 303 }, { 10, 50, 667, 299 }, { 50, 47, 87, 104 }, { 51, 41, 226, 110 },
            { 52, 47, 84, 182 }, { 53, 47, 147, 213 }, { 54, 47, 163, 143 }, { 55, 47, 227, 172 },
            { 56, 47, 303, 213 }, { 57, 41, 74, 253 }, { 58, 54, 225, 262 }, { 59, 41, 133, 296 },
            { 60, 47, 181, 331 }, { 61, 47, 52, 347 }, { 62, 41, 208, 396 }, { 63, 47, 76, 412 },
            { 64, 41, 14, 458 }, { 65, 41, 77, 490 }, { 66, 46, 143, 444 }, { 67, 41, 273, 428 },
            { 68, 41, 298, 350 }, { 69, 41, 359, 319 }, { 70, 60, 421, 368 }, { 71, 45, 699, 413 },
            { 72, 45, 765, 444 }, { 73, 60, 595, 453 }, { 74, 41, 534, 504 }, { 75, 45, 467, 534 },
            { 76, 41, 400, 577 }, { 77, 47, 672, 508 }, { 78, 47, 592, 551 }, { 79, 41, 660, 580 },
            { 80, 41, 730, 542 },
        };

        private static List<NeighbourhoodLotRead> Reads(int[,] table)
        {
            var reads = new List<NeighbourhoodLotRead>();
            for (int i = 0; i < table.GetLength(0); i++)
            {
                reads.Add(new NeighbourhoodLotRead
                {
                    Number = (short)table[i, 0],
                    Size = table[i, 1],
                    MapX = table[i, 2],
                    MapY = table[i, 3],
                    HasHouse = true
                });
            }
            return reads;
        }

        /// <summary>
        /// Every tile of the lot, and of a street corridor round it, is strictly inside the clip;
        /// and no two lots share a tile.
        /// </summary>
        private static void AssertEveryLotOnDrawnGround(NeighbourhoodLayout layout, int expectedLots)
        {
            Assert.Equal(expectedLots, layout.LotCount);
            var map = NeighbourhoodMap.Create(MapSize);
            var corridor = NeighbourhoodLayoutBuilder.StreetCorridorTiles;
            foreach (var lot in layout.Lots)
            {
                Assert.True(SimitoneSurroundingsPlacement.GroundIsDrawnUnder(map, lot), "lot " + lot.Number
                    + " at (" + lot.WorldX + "," + lot.WorldY + ") stands on ground the mesh never draws");
                Assert.Equal(0, SimitoneSurroundingsPlacement.GroundlessTilesOf(map, lot));
                for (int z = lot.WorldY - corridor; z < lot.WorldY + lot.Size + corridor; z++)
                {
                    for (int x = lot.WorldX - corridor; x < lot.WorldX + lot.Size + corridor; x++)
                    {
                        Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(x, z), "lot " + lot.Number
                            + "'s street corridor reaches undrawn tile (" + x + "," + z + ")");
                    }
                }
            }
            Assert.Equal(0, NeighbourhoodLayoutBuilder.CountOverlaps(layout));
        }

        [Fact]
        public void TheRealFortyOneLotNeighbourhoodIsPackedEntirelyOnDrawnGround()
        {
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(Reads(RealSlot4), 4, 182778065);
            AssertEveryLotOnDrawnGround(layout, 41);

            // and the block is centred: its middle is within a street corridor of the map's middle
            var extent = layout.Extent;
            var slack = 2 * NeighbourhoodLayoutBuilder.StreetCorridorTiles;
            Assert.InRange(extent.CenterX, MapSize / 2 - slack, MapSize / 2 + slack);
            Assert.InRange(extent.CenterY, MapSize / 2 - slack, MapSize / 2 + slack);
        }

        [Fact]
        public void TheOldPackingOfTheRealNeighbourhoodIsWhatTheLogReported()
        {
            // The owner's log: "24 lot(s) stand on ground the map never draws ... 1..10, 50..56, 59,
            // 62, 63, 67, 69, 71, 77". Reproduced from the sidecar the old packer wrote (generator 73):
            // its lot origins under TSO's diamond, which was the clip then.
            int[,] old =
            {
                { 1, 64, 0 }, { 2, 0, 0 }, { 3, 232, 0 }, { 4, 404, 0 }, { 5, 165, 64 }, { 6, 392, 64 },
                { 7, 225, 64 }, { 8, 443, 64 }, { 9, 107, 126 }, { 10, 49, 126 }, { 50, 128, 0 },
                { 51, 183, 0 }, { 52, 0, 64 }, { 53, 55, 64 }, { 54, 294, 0 }, { 55, 349, 0 },
                { 56, 110, 64 }, { 57, 281, 64 }, { 58, 330, 64 }, { 59, 0, 126 }, { 60, 207, 126 },
                { 61, 262, 126 }, { 62, 434, 126 }, { 63, 0, 194 }, { 64, 332, 194 }, { 65, 381, 194 },
                { 66, 157, 194 }, { 67, 108, 194 }, { 68, 317, 126 }, { 69, 158, 126 }, { 70, 366, 126 },
                { 71, 55, 194 }, { 72, 211, 194 }, { 73, 264, 194 }, { 74, 430, 194 }, { 75, 55, 262 },
                { 76, 212, 262 }, { 77, 0, 262 }, { 78, 157, 262 }, { 79, 261, 262 }, { 80, 108, 262 },
            };
            var outside = new List<int>();
            for (int i = 0; i < old.GetLength(0); i++)
            {
                if (!InsideTsoDiamond(old[i, 1], old[i, 2])) outside.Add(old[i, 0]);
            }
            Assert.Equal(new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 50, 51, 52, 53, 54, 55, 56, 59, 62, 63, 67, 69, 71, 77 },
                outside.ToArray());

            // and the diamond could not have held them however they were packed: their footprints
            // need more tiles than it has
            long footprint = 0;
            for (int i = 0; i < RealSlot4.GetLength(0); i++)
            {
                long side = NeighbourhoodLayoutBuilder.FootprintFor(RealSlot4[i, 1]);
                footprint += side * side;
            }
            long diamond = 0;
            for (int z = 0; z < MapSize; z++)
            {
                for (int x = 0; x < MapSize; x++)
                {
                    if (InsideTsoDiamond(x, z)) diamond++;
                }
            }
            Assert.True(footprint > diamond * 9 / 10, footprint + " footprint tiles, " + diamond + " in the diamond");
        }

        /// <summary>The clip the mesh used before audit O1: TSO's diamond, strictly inside.</summary>
        private static bool InsideTsoDiamond(int x, int row)
        {
            if (row < 0 || row >= MapSize) return false;
            var left = row < NeighbourhoodCityMeshBuilder.DiamondCentre
                ? NeighbourhoodCityMeshBuilder.DiamondCentre - row : row - NeighbourhoodCityMeshBuilder.DiamondCentre;
            var right = row < NeighbourhoodCityMeshBuilder.DiamondRightPivot
                ? NeighbourhoodCityMeshBuilder.DiamondRightBase + row
                : MapSize - (row - NeighbourhoodCityMeshBuilder.DiamondRightPivot);
            return x > left && x < right;
        }

        [Fact]
        public void TheRealNeighbourhoodGeneratesAMapWithEveryLotOnItsGround()
        {
            // through the real generator, so the map the mesh is built from is asked, not a blank
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(Reads(RealSlot4), 4, 182778065);
            var map = NeighbourhoodMapGenerator.Generate(NeighbourhoodMapAdapter.InputFor(layout));
            Assert.NotNull(map);
            Assert.True(SimitoneSurroundingsPlacement.IsMeshed(map));
            foreach (var lot in layout.Lots)
            {
                Assert.True(SimitoneSurroundingsPlacement.GroundIsDrawnUnder(map, lot), "lot " + lot.Number);
                Assert.Equal(0, SimitoneSurroundingsPlacement.GroundlessTilesOf(map, lot));
            }
        }

        [Theory]
        [InlineData(1, 1)]
        [InlineData(9, 2)]
        [InlineData(10, 3)]
        [InlineData(20, 4)]
        [InlineData(41, 5)]
        [InlineData(45, 6)]
        public void SyntheticNeighbourhoodsArePackedEntirelyOnDrawnGround(int count, int seed)
        {
            var random = new Random(seed);
            var reads = new List<NeighbourhoodLotRead>();
            for (int i = 0; i < count; i++)
            {
                reads.Add(new NeighbourhoodLotRead
                {
                    Number = (short)(i + 1),
                    Size = random.Next(20, 65),
                    MapX = random.Next(0, 800),
                    MapY = random.Next(0, 600),
                    HasHouse = true
                });
            }
            AssertEveryLotOnDrawnGround(NeighbourhoodLayoutBuilder.PackOnGrid(reads, 1, seed), count);
        }

        [Fact]
        public void ThirtySixLotsOfTheLargestSizeStillFit()
        {
            var reads = new List<NeighbourhoodLotRead>();
            for (int i = 0; i < 36; i++)
            {
                reads.Add(new NeighbourhoodLotRead
                {
                    Number = (short)(i + 1),
                    Size = NeighbourhoodLayout.MaxLotSize,
                    MapX = i * 20,
                    MapY = i,
                    HasHouse = true
                });
            }
            AssertEveryLotOnDrawnGround(NeighbourhoodLayoutBuilder.PackOnGrid(reads, 1, 7), 36);
        }

        [Fact]
        public void EveryNeighbourhoodSidecarOnThisMachineRepacksOntoDrawnGround()
        {
            // The owner's live sidecars, read-only, when this machine has them (a CI runner does
            // not; the embedded table above is the pin there).
            var userDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Simitone");
            for (int slot = 0; slot <= 10; slot++)
            {
                var path = NeighbourhoodLayoutStore.PathFor(userDir, slot);
                if (path == null || !File.Exists(path)) continue;
                var stored = NeighbourhoodLayoutStore.Load(userDir, slot);
                if (stored == null) continue;

                var reads = new List<NeighbourhoodLotRead>();
                foreach (var lot in stored.Lots)
                {
                    reads.Add(new NeighbourhoodLotRead
                    {
                        Number = lot.Number, Size = lot.Size, MapX = lot.MapX, MapY = lot.MapY,
                        HasHouse = lot.HasHouse
                    });
                }
                AssertEveryLotOnDrawnGround(
                    NeighbourhoodLayoutBuilder.PackOnGrid(reads, slot, stored.Seed), reads.Count);
            }
        }
    }
}
