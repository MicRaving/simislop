/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// §11's <c>RoadGraphTests</c>: every lot reachable, corner flags at junctions, an isolated
    /// component reconnected, determinism - plus the encoding contract the nibbles rest on.
    ///
    /// <para>
    /// The encoding tests are here rather than in the generator tests because the nibble layout
    /// is <see cref="RoadGraph"/>'s whole contract with the renderer, and because the bit order was
    /// established by reading the shipped road atlas rather than guessed - see
    /// <see cref="RoadMask"/>. A change to it would not fail any "does it look right" question; it
    /// would fail these.
    /// </para>
    /// </summary>
    public sealed class RoadGraphTests
    {
        // ---------------------------------------------------------------- connectivity

        [Fact]
        public void NetworkIsASingleConnectedComponent()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.True(graph.IsSingleNetwork, graph.WriteDebugSummary());
            Assert.Equal(1, graph.ComponentCount);
        }

        [Fact]
        public void AWellFormedNeighbourhoodNeedsNoRepairAtAll()
        {
            // The point of the street-grid layout is that a normal neighbourhood never depends on
            // the repair. If this starts failing, the node resolution has regressed and every
            // connectivity test below is proving less than it looks like it is.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.Empty(graph.Repairs);
            Assert.Equal(0, graph.RepairConnectivity(map));
        }

        [Fact]
        public void EveryLotWithAHouseHasAnEntranceOnTheMainNetwork()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.Equal(4, graph.Entrances.Count);
            Assert.Empty(graph.UnreachableLots);

            var components = graph.Components();
            var main = components.LargestId;
            foreach (var entrance in graph.Entrances)
            {
                Assert.True(graph.IsRoadAt(entrance.X, entrance.Y),
                    "lot " + entrance.LotNumber + " has no road on its door tile");
                Assert.True(components.Contains(entrance.Y * input.MapSize + entrance.X, main),
                    "lot " + entrance.LotNumber + " is not on the main network");
            }
        }

        [Fact]
        public void EntrancesFaceAwayFromTheirLot()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            foreach (var entrance in graph.Entrances)
            {
                var lot = input.UsableLots.First(l => l.Number == entrance.LotNumber);
                Assert.Equal(NeighbourhoodDoorSide.DeltaX(lot.DoorSide), entrance.FacingX);
                Assert.Equal(NeighbourhoodDoorSide.DeltaY(lot.DoorSide), entrance.FacingY);

                // One tile out along the normal, so the driveway starts outside the plot and the
                // door tile itself can hold road.
                var inwardX = entrance.X - entrance.FacingX;
                var inwardY = entrance.Y - entrance.FacingY;
                Assert.True(lot.Contains(inwardX, inwardY),
                    "lot " + entrance.LotNumber + " entrance is not on its own door edge");
            }
        }

        [Fact]
        public void IsolatedComponentIsReconnected()
        {
            // Four bulldozed plots that close a street off in a pocket too small for a band to be
            // seeded in, so the network comes out in two pieces; a four-connected search over the
            // free ground can walk out of the pocket and round the plots, which is the whole
            // difference between the repair being possible and the generator refusing.
            //
            // The fixture used to be two walls with a one-tile column between them, which split
            // only because a two-tile street would not fit down it. A street is now
            // min(RoadGraph.MainStreetWidth, the free gap), so that column is a one-tile street and
            // the network arrives whole - see NeighbourhoodTestInput for the shape that replaces it.
            var input = NeighbourhoodTestInput.StreetSplitByAOneTileChannel();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.BuildNetwork(input);
            graph.Rasterise(map);

            Assert.Equal(2, graph.ComponentCount);

            var repairs = graph.RepairConnectivity(map);

            Assert.Equal(1, repairs);
            Assert.Equal(1, graph.ComponentCount);
            graph.RefreshReachability(map);
            Assert.Empty(graph.UnreachableLots);
            Assert.Null(Record.Exception(() => graph.VerifyConnected()));
        }

        [Fact]
        public void RepairsAreRecordedWithThePathTheyAdded()
        {
            var input = NeighbourhoodTestInput.StreetSplitByAOneTileChannel();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            var repair = Assert.Single(graph.Repairs);
            Assert.Contains("component", repair.Reason);
            Assert.True(repair.Length > 0);
            Assert.Contains(
                graph.Edges,
                e => e.Kind == RoadEdgeKind.Repair);

            // Re-rasterising from the graph reproduces the same map: the repair is part of the
            // graph, not a scribble on the raster, which is what §15's "persisted road graph"
            // prep item needs.
            var again = NeighbourhoodMap.Create(input.MapSize);
            graph.Rasterise(again);
            Assert.Equal(map.RoadData, again.RoadData);
        }

        [Fact]
        public void RepairedPathsAlsoStayOffLotInteriors()
        {
            // The repair routes around the blocking lots. "Around" is the whole point: the cheaper
            // invariant to give up would be to tunnel through one.
            var input = NeighbourhoodTestInput.StreetSplitByAOneTileChannel();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.NotEmpty(graph.Repairs);
            AssertRoadsStayOffLotInteriors(graph, map, input);
        }

        [Fact]
        public void ABulldozedLotGetsNoEntrance()
        {
            // A plot with no house has no driveway: a road that dead-ends at an empty plot is worse
            // than no road, and it is the one thing HasHouse changes about the road graph. Its
            // edges are still streets - a bulldozed plot sits in a block like any other - so what
            // must be empty is the entrance and the driveways, not the network.
            var input = NeighbourhoodTestInput.OneBulldozedLot();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.Empty(graph.Entrances);
            Assert.DoesNotContain(graph.Edges, e => e.Kind == RoadEdgeKind.Driveway);
            Assert.Empty(graph.OffMapLots);
            Assert.True(graph.IsSingleNetwork);
        }

        [Fact]
        public void NoLotsIsAVacuouslyConnectedNetwork()
        {
            var input = NeighbourhoodTestInput.NoLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.True(graph.IsSingleNetwork);
            Assert.Empty(graph.UnreachableLots);
            Assert.Null(Record.Exception(() => graph.VerifyConnected()));
        }

        // ---------------------------------------------------------------- layout

        [Fact]
        public void StreetNodesSitOnStreetsAndNotInsideLots()
        {
            // Nodes are crossings, so every one of them has to be on road. The previous contract -
            // "on a multiple of the fixed eight-tile grid" - is deliberately gone: that grid is
            // unrelated to the packing's pitch, which is exactly why it produced a network made
            // of repair paths. See RoadGraph's type remarks.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.NotEmpty(graph.Nodes);
            foreach (var node in graph.Nodes)
            {
                Assert.True(node.OnStreetGrid);
                Assert.True(graph.IsRoadAt(node.X, node.Y),
                    "node " + node + " is not on any road");
                Assert.False(graph.IsLotInteriorAt(node.X, node.Y),
                    "node " + node + " is inside a lot");
            }
        }

        [Fact]
        public void AStreetNodeNeverLandsInsideALot()
        {
            // The failure this guards against is subtle and total: a lot whose edge is already on
            // a grid multiple has its door anchor round back inside itself, and then every street
            // through that node and the lot's own driveway are both refused as "inside a lot", so
            // the lot ends up with no road at all and the whole network splits.
            foreach (var input in new[]
            {
                NeighbourhoodTestInput.FourFlatLots(),
                NeighbourhoodTestInput.FlatRow(4, 4),
            })
            {
                var graph = RoadGraph.BuildNetwork(input);
                foreach (var node in graph.Nodes)
                {
                    Assert.False(graph.IsLotInteriorAt(node.X, node.Y),
                        "node " + node + " is inside a lot");
                }
            }
        }

        [Fact]
        public void EveryEdgeIsASingleAxisAlignedRun()
        {
            var graph = RoadGraph.BuildNetwork(NeighbourhoodTestInput.FourFlatLots());

            Assert.NotEmpty(graph.Edges);
            foreach (var edge in graph.Edges)
            {
                Assert.True(edge.AX == edge.BX || edge.AY == edge.BY,
                    "edge " + edge + " is not axis aligned");
                Assert.True(edge.Width >= 1);
            }
        }

        [Fact]
        public void StreetsAreWiderThanDriveways()
        {
            // FourFlatLots leaves a generous gap, so every street there is the full nominal width
            // and the "wider than" claim is an equality on both sides. A street is capped at
            // MainStreetWidth and floored at DrivewayWidth; a narrow gap narrows it, so the range
            // is the contract and this fixture is the case that pins the top of it.
            var graph = RoadGraph.BuildNetwork(NeighbourhoodTestInput.FourFlatLots());

            foreach (var edge in graph.Edges)
            {
                var expected = edge.Kind == RoadEdgeKind.Street
                    ? RoadGraph.MainStreetWidth
                    : RoadGraph.DrivewayWidth;
                Assert.Equal(expected, edge.Width);
            }
        }

        [Fact]
        public void NoStreetIsNarrowerThanADrivewayOrWiderThanTheNominalWidth()
        {
            // The range every street stays inside, on a layout with gaps of wildly different
            // widths. The four flat lots leave a sixteen-tile gap, so their streets are the full
            // six; a hand-placed one-tile gap would give a one-tile street, and neither extreme is
            // a bug - the ground decides, and the constants bound it.
            foreach (var input in new[]
            {
                NeighbourhoodTestInput.FourFlatLots(),
                NeighbourhoodTestInput.PackedNeighbourhood(9, 31, 1, 96),
                NeighbourhoodTestInput.WideGaps(),
                NeighbourhoodTestInput.StreetSplitByAOneTileChannel(),
            })
            {
                var graph = RoadGraph.BuildNetwork(input);
                Assert.NotEmpty(graph.StreetRuns);
                foreach (var edge in graph.StreetRuns)
                {
                    Assert.InRange(edge.Width, RoadGraph.DrivewayWidth, RoadGraph.MainStreetWidth);
                }
            }
        }

        [Fact]
        public void TheGraphIsDeterministic()
        {
            var a = RoadGraph.BuildNetwork(NeighbourhoodTestInput.FourFlatLots());
            var b = RoadGraph.BuildNetwork(NeighbourhoodTestInput.FourFlatLots());

            Assert.Equal(a.Nodes.Count, b.Nodes.Count);
            for (int i = 0; i < a.Nodes.Count; i++)
            {
                Assert.Equal(a.Nodes[i].X, b.Nodes[i].X);
                Assert.Equal(a.Nodes[i].Y, b.Nodes[i].Y);
                Assert.Equal(a.Nodes[i].LotNumber, b.Nodes[i].LotNumber);
            }
            Assert.Equal(a.Edges.Count, b.Edges.Count);
            for (int i = 0; i < a.Edges.Count; i++)
            {
                Assert.Equal(a.Edges[i].ToString(), b.Edges[i].ToString());
            }
        }

        [Fact]
        public void RasterisingIsIdempotent()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.BuildNetwork(input);

            graph.Rasterise(map);
            var first = map.RoadData.ToArray();
            graph.Rasterise(map);

            Assert.Equal(first, map.RoadData);
        }

        // ---------------------------------------------------------------- the nibbles

        [Fact]
        public void RoadMaskBitsAreEastSouthWestNorth()
        {
            // The order was read off the shipped atlas by sampling which edge of each roadNN.png
            // the asphalt reaches: road05 (mask 5) runs east-west and road10 (mask 10) runs
            // north-south, which pins bit0 = East, bit1 = South, bit2 = West, bit3 = North. A
            // rotation of these four would be silently wrong - every corner would be mirrored and
            // nothing else would notice - so it is asserted rather than documented.
            Assert.Equal(1, RoadMask.East);
            Assert.Equal(2, RoadMask.South);
            Assert.Equal(4, RoadMask.West);
            Assert.Equal(8, RoadMask.North);
            Assert.Equal(15, RoadMask.All);

            Assert.Equal(1, RoadMask.BitForStep(1, 0));
            Assert.Equal(4, RoadMask.BitForStep(-1, 0));
            Assert.Equal(2, RoadMask.BitForStep(0, 1));
            Assert.Equal(8, RoadMask.BitForStep(0, -1));
            Assert.Equal(0, RoadMask.BitForStep(0, 0));
        }

        [Fact]
        public void TheFifteenMasksClassifyIntoTheFifteenShippedRoadTextures()
        {
            // CityContent.RoadLayout and RoadCLayout are indexed by the mask, and the fifteen
            // shipped textures are the fifteen non-zero masks grouped by shape - which is the
            // whole reason the nibble is the mask and not a permutation of it. These are the four
            // groups; a generator that emitted a shape in the wrong class would draw a crossroads
            // where a corner belongs.
            var deadEnds = new List<byte>();
            var corners = new List<byte>();
            var straights = new List<byte>();
            var tees = new List<byte>();

            for (byte mask = 1; mask < 16; mask++)
            {
                if (RoadMask.IsDeadEnd(mask)) deadEnds.Add(mask);
                else if (RoadMask.IsCorner(mask)) corners.Add(mask);
                else if (RoadMask.IsStraight(mask)) straights.Add(mask);
                else if (RoadMask.IsTJunction(mask)) tees.Add(mask);
                else Assert.True(RoadMask.IsCross(mask), "mask " + mask + " is in no group");
            }

            Assert.Equal(new byte[] { 1, 2, 4, 8 }, deadEnds);
            Assert.Equal(new byte[] { 3, 6, 9, 12 }, corners);
            Assert.Equal(new byte[] { 5, 10 }, straights);
            Assert.Equal(new byte[] { 7, 11, 13, 14 }, tees);
        }

        [Fact]
        public void CornerNibbleIsSetAtTurnsAndJunctions()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            int turns = 0, junctions = 0;
            for (int i = 0; i < map.RoadData.Length; i++)
            {
                var mask = map.RoadMaskAt(i);
                if (mask == 0) continue;
                if (RoadMask.IsCorner(mask)) turns++;
                else if (RoadMask.IsTJunction(mask)) junctions++;
                else continue;

                Assert.Equal(mask, map.RoadCornerAt(i));
            }

            // The fixture is a 2x2 block with a street down the middle of the gap, so it has to
            // produce both: the elbow where the street turns, and every T where a driveway meets it.
            Assert.True(turns > 0, "no turn tiles in the generated network");
            Assert.True(junctions > 0, "no T-junction tiles in the generated network");
        }

        [Fact]
        public void CornerNibbleIsClearOnStraightsDeadEndsAndCrosses()
        {
            // A two-tile-wide street is two parallel rows of road, so every interior tile of a long
            // straight street is a four-way cross by construction. Putting a corner piece there
            // would draw a crossroads on every tile of the street, which is exactly the reason
            // RoadMask.IsTurnOrJunction excludes the cross.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            bool sawStraight = false, sawCross = false;
            for (int i = 0; i < map.RoadData.Length; i++)
            {
                var mask = map.RoadMaskAt(i);
                if (mask == 0) continue;
                if (RoadMask.IsStraight(mask)) sawStraight = true;
                if (RoadMask.IsCross(mask)) sawCross = true;
                if (RoadMask.IsStraight(mask) || RoadMask.IsCross(mask) || RoadMask.IsDeadEnd(mask))
                {
                    Assert.Equal(0, map.RoadCornerAt(i));
                }
            }

            Assert.True(sawStraight, "the fixture produced no straight road tiles");
            Assert.True(sawCross, "the fixture produced no two-tile-wide street tiles");
        }

        [Fact]
        public void RoadNibblesAgreeWithTheirNeighbours()
        {
            // A tile claims to connect to a neighbour or it does not, and the two tiles have to
            // agree. If they ever disagree the road renders with a kerb down the middle of itself,
            // and no connectivity test on the graph would notice - which is why the check is on
            // the nibbles themselves.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            int roadTiles = 0;
            for (int y = 0; y < input.MapSize; y++)
            {
                for (int x = 0; x < input.MapSize; x++)
                {
                    var mask = map.RoadMaskAt(y * input.MapSize + x);
                    if (mask == 0)
                    {
                        Assert.False(graph.IsRoadAt(x, y));
                        continue;
                    }
                    roadTiles++;

                    Assert.Equal(graph.IsRoadAt(x + 1, y), RoadMask.IsSet(mask, RoadMask.East));
                    Assert.Equal(graph.IsRoadAt(x, y + 1), RoadMask.IsSet(mask, RoadMask.South));
                    Assert.Equal(graph.IsRoadAt(x - 1, y), RoadMask.IsSet(mask, RoadMask.West));
                    Assert.Equal(graph.IsRoadAt(x, y - 1), RoadMask.IsSet(mask, RoadMask.North));
                }
            }

            Assert.True(roadTiles > 0);
        }

        [Fact]
        public void RoadsNeverCrossLotInteriors()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);
            AssertRoadsStayOffLotInteriors(graph, map, input);
        }

        [Fact]
        public void SidewalkRingSurroundsEveryLot()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            foreach (var lot in input.UsableLots)
            {
                // The corner of the ring, diagonally outside the lot. Chosen because a street
                // running down the middle of a gap is very likely to claim the straight sides of
                // the ring, but is very unlikely to claim a corner.
                Assert.True(graph.IsSidewalkAt(lot.WorldX - 1, lot.WorldY - 1),
                    "lot " + lot.Number + " has no sidewalk at its north-west corner");
                Assert.False(graph.IsLotInteriorAt(lot.WorldX - 1, lot.WorldY - 1));
            }
        }

        [Fact]
        public void SidewalkIsNeverUnderneathARoad()
        {
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            for (int i = 0; i < graph.Sidewalk.Length; i++)
            {
                Assert.False(graph.Sidewalk[i] && graph.Road[i]);
                Assert.False(graph.Sidewalk[i] && graph.LotMask.Interior[i]);
            }
        }

        [Fact]
        public void SidewalkIsNotWrittenIntoTheRoadNibbles()
        {
            // §6.1: "a separate cosmetic strip; does not need a road bit". If a sidewalk ever did
            // set a road bit, a 2D dump of RoadData would show pavements as roads and the
            // connectivity invariant would start counting them.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            for (int i = 0; i < graph.Sidewalk.Length; i++)
            {
                if (!graph.Sidewalk[i]) continue;
                Assert.Equal(0, map.RoadData[i]);
            }
        }

        [Fact]
        public void VerifyConnectedRefusesAnUnrepairableNetwork()
        {
            // The strict invariant, kept as an assertion: every other split network in this class
            // gets repaired; without a case that cannot be, a repair which silently did nothing
            // would be indistinguishable from one that worked.
            var input = NeighbourhoodTestInput.SolidWallBetweenTwoHouses();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.Equal(2, graph.ComponentCount);
            Assert.Empty(graph.Repairs);

            var failure = Assert.Throws<InvalidOperationException>(() => graph.VerifyConnected());
            Assert.Contains("components", failure.Message);

            // And Check draws the distinction the generator now acts on: not one network, but a
            // world with streets in it, which is not a reason to draw the 2D map instead.
            var check = graph.Check();
            Assert.False(check.SingleNetwork);
            Assert.True(check.Drawable);
            Assert.Contains("components", check.Message);
        }

        [Fact]
        public void TheGeneratorDrawsASplitNetworkRatherThanRefusingIt()
        {
            // What replaced the old "Generate throws for a split network" assertion. A neighbourhood
            // whose repair pass could not bridge one cul-de-sac used to be thrown away whole and
            // drawn as a 2D bitmap, which is a worse answer than a road that ends where it ends.
            var input = NeighbourhoodTestInput.SolidWallBetweenTwoHouses();
            var lines = new List<string>();
            var original = NeighbourhoodMapGenerator.DiagnosticSink;
            try
            {
                NeighbourhoodMapGenerator.DiagnosticSink = lines.Add;
                var map = NeighbourhoodMapGenerator.Generate(input);

                //drawn, and drawn with roads in it
                Assert.NotNull(map);
                var roadTiles = 0;
                for (int i = 0; i < map.RoadData.Length; i++)
                {
                    if (map.RoadData[i] != 0) roadTiles++;
                }
                Assert.True(roadTiles > 0, "a drawable neighbourhood has to have some road on it");
            }
            finally
            {
                NeighbourhoodMapGenerator.DiagnosticSink = original;
            }

            //and the reason it is not right has to be said out loud, because a warning that only
            //exists as an exception nobody throws is a warning nobody reads
            Assert.Contains(lines, l => l.Contains("components") && l.Contains("component(s)"));
        }

        private static void AssertRoadsStayOffLotInteriors(
            RoadGraph graph, NeighbourhoodMap map, NeighbourhoodMapInput input)
        {
            foreach (var lot in input.UsableLots)
            {
                for (var y = Math.Max(0, lot.WorldY); y < Math.Min(input.MapSize, lot.Bottom); y++)
                {
                    for (var x = Math.Max(0, lot.WorldX); x < Math.Min(input.MapSize, lot.Right); x++)
                    {
                        Assert.Equal(0, map.RoadData[y * input.MapSize + x]);
                    }
                }
            }

            for (int i = 0; i < map.RoadData.Length; i++)
            {
                Assert.False(graph.Road[i] && graph.LotMask.Interior[i]);
            }
        }
    }
}
