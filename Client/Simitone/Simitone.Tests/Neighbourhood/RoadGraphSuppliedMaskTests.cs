/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The road graph's second producer: the connector mask Part 8 reads off the 2D map, rasterised
    /// through the same nibble encoding as a measured street.
    ///
    /// <para>
    /// <b>Why this needs its own file.</b> <see cref="RoadGraph"/> is the whole of the generated
    /// world's contract with the renderer, and the nibble layout in particular is read by
    /// <c>NeighbourhoodCityMeshBuilder.AddRoadQuadsForTile</c> and
    /// <c>SimitoneNeighbourTerrain.RoadByteFor</c>, neither of which knows or cares where the road
    /// came from. Adding a second producer is exactly the moment a contract stops being obvious and
    /// starts being a test: what has to be shown is that the inferred streets arrive in
    /// <see cref="NeighbourhoodMap.RoadData"/> in the <em>same</em> encoding, that they are clipped
    /// out of lot interiors by the same rule a measured edge is, and that the connectivity invariant
    /// - §8.1's "one connected map" - still holds for an inferred network rather than quietly
    /// becoming conditional on a heuristic.
    /// </para>
    /// </summary>
    public sealed class RoadGraphSuppliedMaskTests
    {
        private const int MapSize = 64;

        // ---- the encoding -----------------------------------------------------------------------

        [Fact]
        public void AnInferredStreetArrivesInTheSameNibbles()
        {
            // A mask with a long, one-tile-wide horizontal street, clear of the lots. The low nibble
            // is the 4-bit adjacency mask (RoadMask.East|West on a middle tile) and the high nibble
            // is the same mask again on a turn or a T - exactly the rule WriteMasks has always
            // applied, because it is the same method.
            var input = TwoLots(input_mask: MaskRoad(x0: 4, x1: 60, y: 32, y1: 33));
            var map = NeighbourhoodMap.Create(MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.True(graph.UsesSuppliedRoadMask);

            //a tile in the middle of a straight has east and west and nothing else
            Assert.Equal(RoadMask.East | RoadMask.West, map.RoadData[32 * MapSize + 32] & RoadMask.MaskBits);
            //and no corner nibble, because a straight is not a turn
            Assert.Equal(0, map.RoadData[32 * MapSize + 32] >> 4);

            //the run's western end is a dead end
            Assert.Equal(RoadMask.East, map.RoadData[32 * MapSize + 4] & RoadMask.MaskBits);
        }

        [Fact]
        public void AnInferredTurnGetsTheCornerNibbleJustAsAMeasuredOneDoes()
        {
            // An L whose corner tile connects east and north and nothing else, which is mask 9 -
            // and 9 is a turn, so it takes the corner piece in the high nibble as well.
            var mask = new bool[MapSize * MapSize];
            for (int x = 40; x <= 50; x++) mask[32 * MapSize + x] = true;
            for (int y = 12; y <= 32; y++) mask[y * MapSize + 40] = true;

            var input = TwoLots(input_mask: mask);
            var map = NeighbourhoodMap.Create(MapSize);
            RoadGraph.Build(input, map);

            var corner = map.RoadData[32 * MapSize + 40];
            Assert.Equal(RoadMask.East | RoadMask.North, corner & RoadMask.MaskBits);
            Assert.Equal(corner & RoadMask.MaskBits, corner >> 4);
            Assert.True(RoadMask.IsCorner((byte)(corner & RoadMask.MaskBits)));
        }

        [Fact]
        public void TheEncodingIsAPureFunctionOfTheRoadTiles()
        {
            // The strongest form of "the encoding did not move": whatever tiles the first pass put
            // road on, feeding exactly those tiles back as a mask must produce exactly the same road
            // bytes. It says two things at once - that a mask round-trips through the raster, and
            // that a second pass over its own output is a fixed point, which is what makes the
            // connectivity repair's re-rasterise safe for an inferred network.
            //an L, because an L has a turn in it and a turn is the only thing that puts a byte in
            //the high nibble - so the round trip is only interesting if there is a corner to lose
            var mask = new bool[MapSize * MapSize];
            for (int x = 8; x <= 50; x++) mask[32 * MapSize + x] = true;
            for (int y = 12; y <= 32; y++) mask[y * MapSize + 50] = true;

            //no lots, so nothing adds a driveway and the comparison is purely about the encoding
            var first = BuildNoLots(mask);
            var tiles = new bool[MapSize * MapSize];
            var corners = 0;
            for (int i = 0; i < first.RoadData.Length; i++)
            {
                if (first.RoadData[i] == 0) continue;
                tiles[i] = true;
                if ((first.RoadData[i] >> 4) != 0) corners++;
            }

            //the mask really was encoded rather than passed through, so the test is not vacuous
            Assert.True(corners > 0, "no tile took the corner nibble");
            Assert.True(tiles[32 * MapSize + 8], "the western end of the bar is road");

            var second = BuildNoLots(tiles);
            Assert.Equal(first.RoadData, second.RoadData);
        }

        // ---- the invariant that outranks connectivity ---------------------------------------------

        [Fact]
        public void AnInferredStreetIsNeverDrawnInsideALot()
        {
            // The invariant that outranks connectivity, enforced by the same Mark a measured edge
            // goes through. A road that ran under a block is cut there rather than tunnelled
            // through it.
            var mask = new bool[MapSize * MapSize];
            for (int y = 0; y < MapSize; y++) mask[y * MapSize + 20] = true;
            //and one stray mask tile that lands squarely inside lot 12
            mask[12 * MapSize + 30] = true;

            var input = TwoLots(input_mask: mask);
            var map = NeighbourhoodMap.Create(MapSize);
            var graph = RoadGraph.Build(input, map);

            //lots 11 and 12 occupy x[4,20) and x[28,44), y[8,24); the mask runs straight through both
            for (var y = 8; y < 24; y++)
            {
                Assert.False(graph.IsRoadAt(12, y), "a road is inside lot 11 at y=" + y);
                Assert.False(graph.IsRoadAt(36, y), "a road is inside lot 12 at y=" + y);
                Assert.True(graph.IsRoadAt(20, y), "the mask column should survive outside both lots");
            }
            Assert.False(graph.IsRoadAt(30, 12), "the stray mask tile inside a lot was drawn");
        }

        // ---- driveways ----------------------------------------------------------------------------

        [Fact]
        public void ADrivewayReachesTheInferredStreet()
        {
            // The point of a supplied mask for a lot that is not adjacent to a street: its driveway
            // still has to arrive. The doorstep alone would not be a driveway, and the lot would be
            // reported as unreachable.
            var mask = new bool[MapSize * MapSize];
            for (int x = 0; x < MapSize; x++) mask[56 * MapSize + x] = true;

            var input = TwoLots(input_mask: mask);
            var map = NeighbourhoodMap.Create(MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.True(graph.UnreachableLots.Count == 0, graph.WriteDebugSummary());
            var components = graph.Components();
            foreach (var entrance in graph.Entrances)
            {
                Assert.Equal(components.LargestId,
                    components.ComponentOf[entrance.Y * MapSize + entrance.X]);
            }
        }

        [Fact]
        public void EveryLotWithAHouseStillGetsAnEntrance()
        {
            var mask = new bool[MapSize * MapSize];
            for (int y = 0; y < MapSize; y++) mask[y * MapSize + 40] = true;

            var input = TwoLots(input_mask: mask);
            var map = NeighbourhoodMap.Create(MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.Equal(2, graph.Entrances.Count);
            Assert.Empty(graph.OffMapLots);
        }

        // ---- the invariant of §6.1 ----------------------------------------------------------------

        [Fact]
        public void TheConnectivityRepairStillRunsForAnInferredNetwork()
        {
            // §8.1 requires one connected map whichever way the streets were found, and an inferred
            // mask is *more* likely to be in pieces than a measured one - a road that runs behind a
            // block comes back as two stubs once the lot interiors have clipped it. So the repair
            // runs, and this is the fixture where it has to: two road islands and a lot between them.
            var mask = new bool[MapSize * MapSize];
            for (int x = 2; x < 30; x++) mask[4 * MapSize + x] = true;
            for (int x = 34; x < 62; x++) mask[58 * MapSize + x] = true;

            var input = TwoLots(input_mask: mask);
            var map = NeighbourhoodMap.Create(MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.True(graph.UsesSuppliedRoadMask);
            //the network is one piece - possibly after repair, which is the point
            Assert.True(graph.IsSingleNetwork, graph.WriteDebugSummary());
            //and the verification still throws when it cannot be made so, rather than the invariant
            //quietly becoming conditional on a heuristic
            graph.VerifyConnected();
        }

        [Fact]
        public void AnUnrepairedNetworkIsStillLoudlyRefused()
        {
            // The counterpart to the test above, so it cannot pass for the wrong reason. The same two
            // islands, taken before the repair: two components, and VerifyConnected throws rather
            // than being quietly skipped because the streets came from a bitmap. That the
            // connectivity invariant is checked at all for an inferred network is the whole claim.
            var mask = new bool[MapSize * MapSize];
            for (int x = 2; x < 30; x++) mask[4 * MapSize + x] = true;
            for (int x = 34; x < 62; x++) mask[58 * MapSize + x] = true;

            var network = RoadGraph.BuildNetwork(TwoLots(input_mask: mask));
            var map = NeighbourhoodMap.Create(MapSize);
            network.Rasterise(map);

            Assert.True(network.ComponentCount > 1, "the two islands should still be separate");
            Assert.Throws<InvalidOperationException>(() => network.VerifyConnected());

            //and the full build rejoins them
            var repaired = RoadGraph.Build(TwoLots(input_mask: mask), NeighbourhoodMap.Create(MapSize));
            Assert.True(repaired.IsSingleNetwork, repaired.WriteDebugSummary());
        }

        // ---- the fallback --------------------------------------------------------------------------

        [Fact]
        public void NoMaskMeansTheStreetsAreMeasuredAsBefore()
        {
            var input = TwoLots(input_mask: null);
            var map = NeighbourhoodMap.Create(MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.False(graph.UsesSuppliedRoadMask);
            Assert.True(graph.StreetRuns.Count > 0, "the measured skeleton produced no street");
            Assert.Equal(RoadGraph.MainStreetWidth, graph.StreetWidth);
        }

        [Fact]
        public void AMaskOfTheWrongLengthIsRefused()
        {
            // A half-length mask would index row y of the wrong map and paint one neighbourhood's
            // streets into another's ground, so it is dropped and the measured path answers.
            var input = TwoLots(input_mask: new bool[16]);
            var map = NeighbourhoodMap.Create(MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.False(graph.UsesSuppliedRoadMask);
        }

        [Fact]
        public void AnAllFalseMaskIsRefused()
        {
            // The shape a failed classification takes. Honouring it would be a neighbourhood with no
            // streets at all, which is worse than the measured answer and looks like a success.
            var input = TwoLots(input_mask: new bool[MapSize * MapSize]);
            var map = NeighbourhoodMap.Create(MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.False(graph.UsesSuppliedRoadMask);
            Assert.True(graph.StreetRuns.Count > 0);
        }

        [Fact]
        public void TheMaskCanArriveOnEitherArgument()
        {
            // The frozen signature carries it, and the input carries it, and both have to reach the
            // same place - which is what lets the caller decide without the generator changing.
            var mask = MaskRoad(4, 60, 20, 24);
            var lots = new[]
            {
                NeighbourhoodTestInput.Lot(11, 4, 8, 16, 4, NeighbourhoodDoorSide.South),
                NeighbourhoodTestInput.Lot(12, 28, 8, 16, 4, NeighbourhoodDoorSide.South),
            };

            var byArgument = NeighbourhoodMapInput.Of(NeighbourhoodTestInput.DefaultSeed, MapSize, lots);
            var a = NeighbourhoodMap.Create(MapSize);
            RoadGraph.Build(byArgument, a, mask);

            var byInput = new NeighbourhoodMapInput(NeighbourhoodTestInput.DefaultSeed, MapSize, lots, mask);
            var b = NeighbourhoodMap.Create(MapSize);
            RoadGraph.Build(byInput, b);

            Assert.Equal(a.RoadData, b.RoadData);
            Assert.True(byInput.HasRoadMask);
            Assert.False(byArgument.HasRoadMask);
        }

        [Fact]
        public void BuildNetworkCanBeAskedWithoutTouchingAMap()
        {
            // The stage split still holds for the supplied path, so a caller can look at the
            // network - the runs, the nodes, the entrances - before anything is rasterised.
            var input = TwoLots(input_mask: MaskRoad(4, 60, 20, 24));
            var graph = RoadGraph.BuildNetwork(input);

            Assert.True(graph.UsesSuppliedRoadMask);
            Assert.NotEmpty(graph.StreetRuns);
            Assert.NotEmpty(graph.Nodes);
            Assert.Equal(2, graph.Entrances.Count);
        }

        // ---- helpers -----------------------------------------------------------------------------

        /// <summary>Two lots, and a mask in the same tile space as the map they generate.</summary>
        private static NeighbourhoodMapInput TwoLots(bool[] input_mask)
        {
            var lots = new[]
            {
                NeighbourhoodTestInput.Lot(11, 4, 8, 16, 4, NeighbourhoodDoorSide.South),
                NeighbourhoodTestInput.Lot(12, 28, 8, 16, 4, NeighbourhoodDoorSide.South),
            };
            return new NeighbourhoodMapInput(NeighbourhoodTestInput.DefaultSeed, MapSize, lots, input_mask);
        }

        /// <summary>
        /// A neighbourhood with no lots at all, so the only road is whatever the mask said. Used
        /// where a driveway would otherwise be a second thing changing between two builds.
        /// </summary>
        private static NeighbourhoodMap BuildNoLots(bool[] mask)
        {
            var input = new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, MapSize,
                new NeighbourhoodLotInput[0], mask);
            var map = NeighbourhoodMap.Create(MapSize);
            var graph = RoadGraph.Build(input, map);
            Assert.True(graph.UsesSuppliedRoadMask);
            return map;
        }

        private static bool[] MaskRoad(int x0, int x1, int y, int y1)
        {
            var mask = new bool[MapSize * MapSize];
            for (int row = y; row < y1; row++)
            {
                for (int x = x0; x < x1; x++) mask[row * MapSize + x] = true;
            }
            return mask;
        }
    }
}
