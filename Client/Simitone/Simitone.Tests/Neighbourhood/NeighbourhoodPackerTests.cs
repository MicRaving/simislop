/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The layout builder: where lots end up in the generated map, and the properties the generator
    /// is entitled to assume about that answer.
    ///
    /// <para>
    /// <b>Every lot is inside the map.</b> <c>CityGeometry</c> clips its tile loop to the map, so
    /// a lot placed past the edge would be silently cut in half - a visible, unexplained seam
    /// rather than an error.
    /// </para>
    ///
    /// <para>
    /// <b>Every lot is where <c>LotLocations.iff</c> says it is.</b> Since Part 8 this is the
    /// headline property rather than a seed to be refined: <c>WorldX/WorldY</c> come from
    /// <see cref="NeighbourhoodPixelTransform.TileForPixel"/> and nothing else. A bit of slack is
    /// asserted, not equality - the rectangle's origin is half a lot back from the centre the
    /// bitmap gives, and a position that would hang off the map is clamped into it.
    /// </para>
    ///
    /// <para>
    /// <b>The same input always gives the same output.</b> The generated world is a pure function
    /// of the seed and the layout, and the sidecar is a cache of that layout; a builder that
    /// produced a different arrangement on the next launch would invalidate its own cache every
    /// time it was used. Nothing here may depend on iteration order, floating-point luck or the
    /// clock.
    /// </para>
    ///
    /// <para>
    /// <b>Lots may share tiles.</b> TSO's own <c>RestoreSurroundings</c> puts two neighbours
    /// <c>Size - 2</c> tiles apart, so two real lots overlap by two tiles on purpose. The no-overlap
    /// invariant the packer used to promise is gone with the packer; what replaced it is that a road
    /// is never drawn inside a lot, which the road graph enforces. So the tests that asserted
    /// "no two lots share a tile" and "a corridor separates every pair" now ask the shelf
    /// arrangement instead, which is the one layout rule that still guarantees both.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodPackerTests
    {
        private const int Slot = 0;
        private const int Seed = 123456789;

        /// <summary>
        /// A base-residential-shaped neighbourhood: twenty 64-tile lots in the sort of diamond
        /// the neighbourhood bitmap uses.
        /// </summary>
        /// <remarks>
        /// At TSO's own <c>Size - 2</c> pitch these rectangles overlap each other by two tiles, which
        /// is the point: it is what the bitmap spacing says and therefore what the world must draw.
        /// Since Part 9 they overlap by rather more than that, because the transform is fitted to the
        /// drawn diamond rather than to the square - see <see cref="WideResidential"/> for the
        /// size the island has room for.
        /// </remarks>
        private static List<NeighbourhoodLotRead> BaseResidential()
        {
            var reads = new List<NeighbourhoodLotRead>();
            var number = 1;
            for (int row = 0; row < 4; row++)
            {
                for (int column = 0; column < 5; column++)
                {
                    //the bitmap's own skew: each row down shifts left, each column right
                    var mapX = 260 + column * 90 - row * 40;
                    var mapY = 120 + row * 80;
                    reads.Add(new NeighbourhoodLotRead
                    {
                        Number = (short)number++,
                        MapX = mapX,
                        MapY = mapY,
                        HasHouse = true,
                        Size = 64,
                        TerrainType = TerrainType.GRASS
                    });
                }
            }
            return reads;
        }

        /// <summary>
        /// A base-residential neighbourhood spread widely enough on the bitmap that its lots do not
        /// overlap: four columns 200 pixels apart and three rows 240 apart, which on a 512-tile map is
        /// a 74- and a 78-tile pitch for 64-tile lots.
        /// <remarks>
        /// Used by the tests that still want to ask "are these rectangles disjoint?", which is a
        /// property of <em>these coordinates</em> and not of the builder - see
        /// <see cref="AWidelySpacedNeighbourhood_HasNoOverlappingLots"/>.
        ///
        /// <para>CHANGED by the Part 9 TSO-alignment rework. This was a five-by-four grid, five
        /// columns 170 pixels apart and four rows 190 apart: a 109- and a 162-tile pitch under the
        /// axis-aligned square mapping, which is what a 64-tile lot needs plus a street corridor. On
        /// the diamond-aligned projection a bitmap pixel is 0.74 tiles along the difference diagonal
        /// and 0.65 along the sum, so the same grid is a 74- and a 78-tile pitch - and four rows
        /// of it need 880 pixels of a 600-pixel-tall bitmap. The drawn island is 48% of the map's
        /// area and holds fewer disjoint 64-tile lots than the square did, so this is the fixture at
        /// the size the island actually has room for.</para>
        /// </remarks>
        private static List<NeighbourhoodLotRead> WideResidential()
        {
            var reads = new List<NeighbourhoodLotRead>();
            var number = 1;
            for (int row = 0; row < 3; row++)
            {
                for (int column = 0; column < 4; column++)
                {
                    reads.Add(new NeighbourhoodLotRead
                    {
                        Number = (short)number++,
                        MapX = 90 + column * 200,
                        MapY = 90 + row * 240,
                        HasHouse = true,
                        Size = 64,
                        TerrainType = TerrainType.GRASS
                    });
                }
            }
            return reads;
        }

        /// <summary>
        /// A square block of same-sized lots, which is the shape the shelf arrangement is exact
        /// about: four across is four lots on one shelf, and the pitch between them is the whole
        /// question.
        /// </summary>
        private static List<NeighbourhoodLotRead> GridOf(int size, int perSide)
        {
            var reads = new List<NeighbourhoodLotRead>();
            var number = 1;
            for (int row = 0; row < perSide; row++)
            {
                for (int column = 0; column < perSide; column++)
                {
                    reads.Add(new NeighbourhoodLotRead
                    {
                        Number = (short)number++,
                        MapX = 100 + column * 100,
                        MapY = 100 + row * 100,
                        HasHouse = true,
                        Size = size,
                        TerrainType = TerrainType.GRASS
                    });
                }
            }
            return reads;
        }

        private static void AssertNoOverlaps(NeighbourhoodLayout layout, int margin)
        {
            var clashes = new List<string>();
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                for (int j = i + 1; j < layout.Lots.Count; j++)
                {
                    if (NeighbourhoodLot.Overlaps(layout.Lots[i], layout.Lots[j], margin))
                    {
                        clashes.Add(layout.Lots[i] + " / " + layout.Lots[j]);
                    }
                }
            }
            Assert.True(clashes.Count == 0, "lots overlap: " + string.Join("; ", clashes));
        }

        private static void AssertInsideMap(NeighbourhoodLayout layout)
        {
            foreach (var lot in layout.Lots)
            {
                Assert.True(lot.WorldX >= 0, "lot " + lot.Number + " starts before the map");
                Assert.True(lot.WorldY >= 0, "lot " + lot.Number + " starts before the map");
                Assert.True(lot.WorldX + lot.Size <= layout.MapSize,
                    "lot " + lot.Number + " runs off the right edge of the map");
                Assert.True(lot.WorldY + lot.Size <= layout.MapSize,
                    "lot " + lot.Number + " runs off the bottom edge of the map");
            }
        }

        // ---- the invariants --------------------------------------------------------------------

        [Fact]
        public void AWidelySpacedNeighbourhood_HasNoOverlappingLots()
        {
            // Disjointness is a property of the coordinates rather than of the builder, and this is
            // the fixture that has them: twelve 64-tile lots a 74- and a 78-tile pitch apart on a 512 map.
            // (CHANGED by the Part 9 TSO-alignment rework: it was twenty, at a 109- and a 162-tile pitch.)
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(WideResidential(), Slot, Seed);
            Assert.Equal(12, layout.LotCount);
            AssertNoOverlaps(layout, 0);
        }

        [Fact]
        public void AWidelySpacedNeighbourhood_LeavesRoomForTheSidewalks()
        {
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(WideResidential(), Slot, Seed);

            //the footprints - size plus margin - must not touch, or the sidewalk strip the road
            //graph draws would have nowhere to go
            AssertNoOverlaps(layout, NeighbourhoodLayoutBuilder.SidewalkMargin);
        }

        // ---- the pitch, which is the one number the street seam measures against ---------------

        [Fact]
        public void AFootprintIsTheLotPlusTheWholeStreetCorridor()
        {
            // The pitch, stated as arithmetic: a lot's packed rectangle is its own side plus the
            // corridor a street occupies, and the corridor is the same number the map's own street
            // is eight tiles wide - so the road graph can lay a full-width street with a footway on
            // each side, and a lot's own road byte can still find it within its probe depth.
            Assert.Equal(64 + SimitoneLotViewRoad.StreetCorridorTiles,
                NeighbourhoodLayoutBuilder.FootprintFor(64));
            Assert.Equal(64 + NeighbourhoodLayoutBuilder.StreetCorridorTiles,
                NeighbourhoodLayoutBuilder.FootprintFor(64));
            Assert.Equal(NeighbourhoodMapAdapter.StreetCorridorMapTiles,
                NeighbourhoodLayoutBuilder.StreetCorridorTiles);
            Assert.Equal(RoadGraph.MainStreetCorridorWidth,
                NeighbourhoodLayoutBuilder.StreetCorridorTiles);
            //and the old name for it is the same number, so no caller is left on the two-tile value
            Assert.Equal(NeighbourhoodLayoutBuilder.StreetCorridorTiles,
                NeighbourhoodLayoutBuilder.SidewalkMargin);

            //a corrupt or hand-edited size is clamped before it becomes a footprint
            Assert.Equal(NeighbourhoodLayout.MinLotSize + NeighbourhoodLayoutBuilder.StreetCorridorTiles,
                NeighbourhoodLayoutBuilder.FootprintFor(0));
            Assert.Equal(NeighbourhoodLayout.MaxLotSize + NeighbourhoodLayoutBuilder.StreetCorridorTiles,
                NeighbourhoodLayoutBuilder.FootprintFor(999));
        }

        [Fact]
        public void ShelfLotsAreNeverCloserThanOneStreetCorridor()
        {
            // The corridor invariant, asked of the arrangement that still makes it. Two lots closer
            // than the corridor cannot have a street between them: the lot view's own bands reach six
            // tiles into each lot, so a narrower gap is a street with its own columns already spoken
            // for, and the generated one is a sliver beside it. A shelf advances by whole
            // footprints, so every gap in it is exactly the corridor - and that guarantee belongs to
            // the shelf, not to a map-derived layout, whose lots sit at TSO's own Size-2 pitch and
            // therefore overlap on purpose.
            foreach (var layout in new[]
            {
                NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed),
                NeighbourhoodLayoutBuilder.PackOnGrid(WideResidential(), Slot, Seed)
            })
            {
                Assert.Equal(0, NeighbourhoodLayoutBuilder.CountOverlaps(layout));
                Assert.Equal(0, NeighbourhoodLayoutBuilder.CountOverlaps(
                    layout.Lots, NeighbourhoodLayoutBuilder.StreetCorridorTiles));
                for (int i = 0; i < layout.LotCount; i++)
                {
                    for (int j = 0; j < layout.LotCount; j++)
                    {
                        if (i == j) continue;
                        var gap = SimitoneSurroundingsPlan.SubWorldGapFor(
                            layout.Lots[i], layout.Lots[j]);
                        Assert.True(gap >= NeighbourhoodLayoutBuilder.StreetCorridorTiles,
                            "lots " + layout.Lots[i].Number + " and " + layout.Lots[j].Number
                            + " are " + gap + " tiles apart, which is less than the "
                            + NeighbourhoodLayoutBuilder.StreetCorridorTiles
                            + "-tile street corridor the lot view draws between them");
                    }
                }
            }
        }

        private static int CountRoad(NeighbourhoodMap map)
        {
            var n = 0;
            for (int i = 0; i < map.RoadData.Length; i++)
            {
                if (map.RoadData[i] != 0) n++;
            }
            return n;
        }

        [Fact]
        public void TheShelfArrangementPutsEveryPairExactlyOneCorridorApart()
        {
            // The shelf's pitch is the footprint, so its gaps are the corridor exactly - which is
            // the guarantee the band relies on when it fills the middle of a street. Asked of the
            // rows the layout actually produced rather than of a lot order the test would have to
            // guess. (It used to be called the grid fallback; that name went with the packer.)
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(GridOf(64, 4), Slot, Seed);
            Assert.Equal(16, layout.LotCount);

            var rows = new Dictionary<int, List<NeighbourhoodLot>>();
            foreach (var lot in layout.Lots)
            {
                List<NeighbourhoodLot> row;
                if (!rows.TryGetValue(lot.WorldY, out row))
                {
                    row = new List<NeighbourhoodLot>();
                    rows.Add(lot.WorldY, row);
                }
                row.Add(lot);
            }

            var pitch = NeighbourhoodLayoutBuilder.FootprintFor(64);
            var shelves = new List<int>(rows.Keys);
            shelves.Sort();
            Assert.True(shelves.Count > 1, "sixteen 72-tile lots cannot share one shelf of a 512 map");
            for (var s = 1; s < shelves.Count; s++)
            {
                Assert.Equal(pitch, shelves[s] - shelves[s - 1]);
            }
            foreach (var row in rows.Values)
            {
                row.Sort((a, b) => a.WorldX.CompareTo(b.WorldX));
                Assert.Equal(0, row[0].WorldX);
                for (var i = 0; i < row.Count; i++)
                {
                    Assert.Equal(i * pitch, row[i].WorldX);
                    if (i == 0) continue;
                    Assert.Equal(NeighbourhoodLayoutBuilder.StreetCorridorTiles,
                        SimitoneSurroundingsPlan.SubWorldGapFor(row[i - 1], row[i]));
                }
            }
        }

        [Fact]
        public void EveryLotStaysInsideTheMap()
        {
            AssertInsideMap(NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed));
        }

        [Fact]
        public void PackingIsStableAcrossRuns()
        {
            var first = NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed);
            var second = NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed);

            Assert.Equal(first.ToString(), second.ToString());
            for (int i = 0; i < first.Lots.Count; i++)
            {
                Assert.Equal(first.Lots[i].Number, second.Lots[i].Number);
                Assert.Equal(first.Lots[i].WorldX, second.Lots[i].WorldX);
                Assert.Equal(first.Lots[i].WorldY, second.Lots[i].WorldY);
                Assert.Equal(first.Lots[i].DoorSide, second.Lots[i].DoorSide);
            }
        }

        [Fact]
        public void PackingDoesNotDependOnTheOrderTheLotsArrivedIn()
        {
            var reads = BaseResidential();
            var shuffled = new List<NeighbourhoodLotRead>(reads);
            //a fixed, non-reversed permutation: reversing would make the "first duplicate wins"
            //rule visible for a different reason than the one under test
            var order = new[] { 7, 3, 19, 0, 12, 5, 1, 16, 9, 2, 14, 8, 11, 4, 18, 6, 13, 10, 17, 15 };
            var reordered = new List<NeighbourhoodLotRead>(reads.Count);
            for (int i = 0; i < order.Length; i++) reordered.Add(reads[order[i]]);

            var a = NeighbourhoodLayoutBuilder.PackOnGrid(reads, Slot, Seed);
            var b = NeighbourhoodLayoutBuilder.PackOnGrid(reordered, Slot, Seed);

            for (int i = 0; i < a.Lots.Count; i++)
            {
                Assert.Equal(a.Lots[i].Number, b.Lots[i].Number);
                Assert.Equal(a.Lots[i].WorldX, b.Lots[i].WorldX);
                Assert.Equal(a.Lots[i].WorldY, b.Lots[i].WorldY);
            }
        }

        [Fact]
        public void PackingIsAFunctionOfTheLotRecordsOnly_SoAnySeedGivesTheSameArrangement()
        {
            //Scope A's arrangement is derived from the bitmap, not from the seed; the seed drives
            //terrain, roads and foliage. Pinning that down here means a later change to the
            //builder cannot quietly start making the layout depend on a value the cache key
            //already covers - which would be harmless, but would be surprising.
            var a = NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, 1);
            var b = NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, 2);

            for (int i = 0; i < a.Lots.Count; i++)
            {
                Assert.Equal(a.Lots[i].WorldX, b.Lots[i].WorldX);
                Assert.Equal(a.Lots[i].WorldY, b.Lots[i].WorldY);
            }
        }

        // ---- the shelf arrangement, which is what a layout with no positions gets -------------

        [Fact]
        public void TheShelfArrangement_PlacesEveryLotWithoutOverlaps()
        {
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed);

            Assert.Equal(20, layout.LotCount);
            AssertNoOverlaps(layout, 0);
            AssertInsideMap(layout);
        }

        [Fact]
        public void TheShelfArrangement_OrdersLotsByMapPosition()
        {
            var reads = new List<NeighbourhoodLotRead>
            {
                new NeighbourhoodLotRead { Number = 30, MapX = 10, MapY = 300, HasHouse = true, Size = 30 },
                new NeighbourhoodLotRead { Number = 10, MapX = 300, MapY = 300, HasHouse = true, Size = 30 },
                new NeighbourhoodLotRead { Number = 20, MapX = 100, MapY = 100, HasHouse = true, Size = 30 },
                new NeighbourhoodLotRead { Number = 5, MapX = 50, MapY = 400, HasHouse = true, Size = 30 }
            };

            //a map three footprints wide, so the fourth lot has to wrap onto the next row
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(reads, Slot, Seed,
                3 * NeighbourhoodLayoutBuilder.FootprintFor(30));
            var byNumber = new Dictionary<int, NeighbourhoodLot>();
            foreach (var lot in layout.Lots) byNumber[lot.Number] = lot;

            //MapY first, then MapX, is the pack order - and within a row the order shows up as
            //left to right: 20 (MapY 100) then 30 (MapY 300, MapX 10) then 10 (MapY 300, MapX 300)
            Assert.True(byNumber[20].WorldX < byNumber[30].WorldX, byNumber[20] + " / " + byNumber[30]);
            Assert.True(byNumber[30].WorldX < byNumber[10].WorldX, byNumber[30] + " / " + byNumber[10]);
            Assert.Equal(byNumber[20].WorldY, byNumber[30].WorldY);
            Assert.Equal(byNumber[30].WorldY, byNumber[10].WorldY);

            //and lot 5, which has the largest MapY of all, is the one that runs out of row
            Assert.True(byNumber[10].WorldY < byNumber[5].WorldY, byNumber[10] + " / " + byNumber[5]);
        }

        [Fact]
        public void TheShelfArrangement_FillsARowBeforeStartingTheNext()
        {
            //enough lots that the map cannot be one row
            var reads = new List<NeighbourhoodLotRead>();
            for (int i = 0; i < 30; i++)
            {
                reads.Add(new NeighbourhoodLotRead
                {
                    Number = (short)(i + 1),
                    MapX = 100 + i,
                    MapY = 100,
                    HasHouse = true,
                    Size = 40
                });
            }

            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(reads, Slot, Seed);

            AssertNoOverlaps(layout, 0);
            AssertInsideMap(layout);
            //a footprint of 42 fits 12 across a 512 map, so thirty lots must take three rows
            var rows = new HashSet<int>();
            foreach (var lot in layout.Lots) rows.Add(lot.WorldY);
            Assert.Equal(3, rows.Count);
        }

        [Fact]
        public void MoreLotsThanDistinctPositions_AreAllStillPlaced()
        {
            //far more lots than the bitmap has distinct pixels for. The builder used to answer this
            //by falling back to the shelf; it now stacks them, because the table is the authority and
            //the contract is that every lot in it is placed and every rectangle is legal. What is
            //asserted is the part of the old answer that still holds.
            var reads = new List<NeighbourhoodLotRead>();
            for (int i = 0; i < 60; i++)
            {
                reads.Add(new NeighbourhoodLotRead
                {
                    Number = (short)(i + 1),
                    MapX = 100 + (i % 6) * 3,
                    MapY = 100 + (i / 6) * 3,
                    HasHouse = true,
                    Size = 24
                });
            }

            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(reads, Slot, Seed);

            Assert.Equal(60, layout.LotCount);
            AssertInsideMap(layout);
        }

        // ---- bounds and degenerate input --------------------------------------------------------

        [Fact]
        public void ALotBiggerThanTheMap_IsClampedInsteadOfLeavingIt()
        {
            var reads = new List<NeighbourhoodLotRead>
            {
                new NeighbourhoodLotRead { Number = 21, MapX = 10, MapY = 10, HasHouse = true, Size = 64 }
            };

            //a map too small to hold the lot at all: the layout must still be a legal one
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(reads, Slot, Seed, 32);

            Assert.Equal(1, layout.LotCount);
            var lot = layout.Lots[0];
            Assert.True(lot.WorldX >= 0 && lot.WorldY >= 0);
            Assert.True(lot.WorldX <= Math.Max(0, 32 - lot.Size));
            Assert.True(lot.WorldY <= Math.Max(0, 32 - lot.Size));
        }

        [Fact]
        public void AnEmptyNeighbourhood_PacksToNothing()
        {
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(new List<NeighbourhoodLotRead>(), Slot, Seed);

            Assert.Equal(0, layout.LotCount);
            Assert.Equal(0, NeighbourhoodLayoutBuilder.CountOverlaps(layout));
        }

        [Fact]
        public void ANullMapSize_IsRefusedRatherThanSilentlyMadeSmall()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed, 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed, -1));
        }

        [Fact]
        public void OverlapCounting_IsTheDiagnosticItAlwaysWas()
        {
            // The question a diagnostic asks: zero for a null layout and zero for the shelf
            // arrangement.
            Assert.Equal(0, NeighbourhoodLayoutBuilder.CountOverlaps((NeighbourhoodLayout)null));
            Assert.Equal(0, NeighbourhoodLayoutBuilder.CountOverlaps(
                NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed)));

            //and it can count a specific arrangement exactly: 1 overlaps 2, 2 overlaps 3, and
            //1 and 3 are exactly adjacent
            var clashing = new[]
            {
                new NeighbourhoodLot { Number = 1, Size = 10, WorldX = 0, WorldY = 0 },
                new NeighbourhoodLot { Number = 2, Size = 10, WorldX = 5, WorldY = 0 },
                new NeighbourhoodLot { Number = 3, Size = 10, WorldX = 10, WorldY = 0 }
            };
            Assert.Equal(2, NeighbourhoodLayoutBuilder.CountOverlaps(clashing, 0));
        }

        [Fact]
        public void TouchingLotsDoNotCountAsOverlapping()
        {
            //rectangles are half-open, so a lot that ends exactly where the next begins leaves a
            //continuous street rather than a shared tile
            var adjacent = new[]
            {
                new NeighbourhoodLot { Number = 1, Size = 10, WorldX = 0, WorldY = 0 },
                new NeighbourhoodLot { Number = 2, Size = 10, WorldX = 10, WorldY = 0 }
            };

            Assert.Equal(0, NeighbourhoodLayoutBuilder.CountOverlaps(adjacent, 0));
            Assert.False(NeighbourhoodLot.Overlaps(adjacent[0], adjacent[1], 0));
        }

        // ---- the pixel transform, and the isometric projection kept beside it -------------------

        [Fact]
        public void TheIsometricScaleFollowsTheExportersRule()
        {
            //(count > 30 ? 1f : 2f) * sqrt(2)
            Assert.Equal(2f * NeighbourhoodLayoutBuilder.IsometricScaleFactor,
                NeighbourhoodLayoutBuilder.IsometricScaleFor(20), 6);
            Assert.Equal(NeighbourhoodLayoutBuilder.IsometricScaleFactor,
                NeighbourhoodLayoutBuilder.IsometricScaleFor(31), 6);
        }

        [Theory]
        [InlineData(0, 0, 20)]
        [InlineData(100, 200, 20)]
        [InlineData(200, 100, 31)]
        [InlineData(636, 102, 20)]
        public void TheIsometricProjectionIsTheExportersTransform(int mapX, int mapY, int lotCount)
        {
            var scale = (lotCount > 30 ? 1f : 2f) * NeighbourhoodLayoutBuilder.IsometricScaleFactor;

            NeighbourhoodLayoutBuilder.ProjectMapPosition(mapX, mapY, lotCount, out var x, out var z);

            //SimitoneNeighOBJExporter: (x + 2y, 0, 2y - x) / scale
            Assert.Equal((mapX + mapY * 2) / scale, x, 4);
            Assert.Equal((mapY * 2 - mapX) / scale, z, 4);
        }

        // ---- door side -------------------------------------------------------------------------

        [Theory]
        [InlineData(0, 0, 20, 512, NeighbourhoodLotSide.North)]      //flush with the top-left corner
        [InlineData(0, 246, 20, 512, NeighbourhoodLotSide.West)]      //hard against the left edge
        [InlineData(492, 0, 20, 512, NeighbourhoodLotSide.North)]     //along the top edge
        [InlineData(492, 492, 20, 512, NeighbourhoodLotSide.East)]    //bottom-right corner
        [InlineData(246, 246, 20, 512, NeighbourhoodLotSide.North)]   //exactly centred: a four-way tie
        [InlineData(0, 492, 20, 512, NeighbourhoodLotSide.South)]     //bottom-left corner: a tie, and the documented order decides
        public void DoorSideFor_IsTheEdgeNearestTheMapBorder(int x, int y, int footprint, int mapSize, NeighbourhoodLotSide expected)
        {
            // Still a real function with real callers - NeighbourhoodLayoutPatch calls it - but no
            // longer the primary answer: with a connector mask the driveway leaves by the edge the
            // 2D map shows the road on, and this is the fallback for a mode with no backdrop.
            Assert.Equal(expected, NeighbourhoodLayoutBuilder.DoorSideFor(x, y, footprint, mapSize));
        }

        [Fact]
        public void DoorSide_TiesResolveTheSameWayEveryTime()
        {
            //a lot diagonally opposite itself: north==east==south==west is impossible, but a
            //partial tie must not depend on the order the comparisons happen to be written in
            for (int i = 0; i < 4; i++)
            {
                Assert.Equal(NeighbourhoodLotSide.North, NeighbourhoodLayoutBuilder.DoorSideFor(0, 0, 20, 40));
                Assert.Equal(NeighbourhoodLotSide.North, NeighbourhoodLayoutBuilder.DoorSideFor(0, 0, 20, 40));
            }
        }

        [Fact]
        public void EveryDoorSide_IsOneOfTheFourRectangleEdges()
        {
            foreach (var layout in new[]
            {
                NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed),
                NeighbourhoodLayoutBuilder.PackOnGrid(BaseResidential(), Slot, Seed)
            })
            {
                foreach (var lot in layout.Lots)
                {
                    Assert.True((int)lot.DoorSide >= 0 && (int)lot.DoorSide <= 3,
                        "lot " + lot.Number + " has door side " + lot.DoorSide);
                }
            }
        }
    }
}
