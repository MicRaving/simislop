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
    /// The contract a *packed* neighbourhood's streets have to keep: real street runs rather than
    /// driveway stubs, every lot on the network, the widths §6.1 asks for, pavement where there is
    /// room for it, and the same map twice.
    ///
    /// <para>
    /// These are deliberately separate from <see cref="RoadGraphTests"/>. That class builds small
    /// hand-placed neighbourhoods with generous gaps, which is the right way to test the encoding
    /// and the repair but the wrong way to test the street layout - every layout rule survives a
    /// generous gap, and the bug this milestone fixes only appears when the lots are packed the
    /// way the packer really packs them. Forty-one 64-tile lots with a two-tile margin on a 512 map
    /// is that case, and it is the fixture every test here is built on.
    /// </para>
    /// <para>
    /// The numbers below are not arbitrary thresholds. A two-tile street crossing a packed
    /// neighbourhood has to run most of the length of the map, and a network whose street share is
    /// small is a network made of driveways - which satisfies every connectivity invariant in the
    /// codebase and still looks like nothing at all, which is exactly how the original bug passed.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class NeighbourhoodStreetTests
    {
        /// <summary>
        /// A base residential neighbourhood as <c>NeighbourhoodLayoutBuilder</c> packs it: forty-one
        /// lots, sixty-four tiles square, the packer's own two-tile margin, on a 512 map.
        /// </summary>
        private static NeighbourhoodMapInput PackedNeighbourhood()
        {
            return NeighbourhoodTestInput.PackedNeighbourhood(
                41, 64, NeighbourhoodLayoutBuilder.SidewalkMargin,
                NeighbourhoodTestInput.RealMapSize);
        }

        private static RoadGraph PackedGraph(out NeighbourhoodMap map)
        {
            var input = PackedNeighbourhood();
            map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);
            graph.VerifyConnected();
            return graph;
        }

        // ---------------------------------------------------------------- the streets exist

        [Fact]
        public void APackedNeighbourhoodGetsStreetsRatherThanOnlyDrivewayStubs()
        {
            // The regression this milestone exists for. The previous skeleton snapped street
            // segments to a fixed eight-tile grid; the lots are on a sixty-six tile pitch, so no
            // multiple of eight ever fell in the two-tile gap between them, every street run was
            // refused for crossing a lot, and what survived was nine one-tile repair paths.
            var stats = PackedGraph(out _).Stats;

            Assert.True(stats.StreetRuns >= 10,
                "a packed neighbourhood should have a grid of streets, got " + stats);
            Assert.True(stats.StreetShare > 0.8,
                "a packed neighbourhood's network should be mostly street, got " + stats);
        }

        [Fact]
        public void StreetsRunTheLengthOfTheNeighbourhoodRatherThanStoppingAtTheNextLot()
        {
            // A stub is the failure mode: one run per driveway instead of one run per block. On a
            // packed map the gaps are full height, so the streets through them are too.
            var stats = PackedGraph(out _).Stats;

            Assert.True(stats.LongestStreetRun >= NeighbourhoodTestInput.RealMapSize / 2,
                "the longest street should span the map, got " + stats);
            Assert.True(stats.StreetTiles > stats.DrivewayTiles * 10,
                "there should be far more street than driveway, got " + stats);
        }

        [Fact]
        public void AWellFormedPackedNeighbourhoodNeedsNoRepairAtAll()
        {
            // The repair is a safety net, not the street plan. A packed neighbourhood that needs
            // one is a neighbourhood whose skeleton did not reach the layout.
            var graph = PackedGraph(out _);

            Assert.Empty(graph.Repairs);
            Assert.Empty(graph.UnreachableLots);
            Assert.Empty(graph.OffMapLots);
            Assert.Equal(1, graph.ComponentCount);
        }

        [Fact]
        public void StreetsRunInTheGroundThePackerLeftAndNotThroughAnyLot()
        {
            var input = PackedNeighbourhood();
            var graph = PackedGraph(out _);

            Assert.NotEmpty(graph.StreetRuns);
            foreach (var street in graph.StreetRuns)
            {
                for (var y = street.AY; y <= street.BY; y++)
                {
                    for (var x = street.AX; x < street.AX + street.Width; x++)
                    {
                        Assert.False(graph.IsLotInteriorAt(x, y),
                            "street " + street + " crosses lot ground at " + x + "," + y);
                    }
                }
            }

            Assert.NotEmpty(input.UsableLots);
        }

        [Fact]
        public void EveryStreetCrossesTheOthersRatherThanRunningParallelToThem()
        {
            // Junctions are what turn a set of parallel bands into a network a car could drive.
            // Without them the connectivity invariant still passes - the bands are connected
            // through the driveways - and the map reads as a set of fences.
            var graph = PackedGraph(out _);

            Assert.True(graph.Nodes.Count >= 8,
                "a packed neighbourhood should have a grid of junctions, got "
                + graph.Nodes.Count);
            foreach (var node in graph.Nodes)
            {
                Assert.True(graph.IsRoadAt(node.X, node.Y), "node " + node + " is not on road");
            }
        }

        // ---------------------------------------------------------------- every lot is served

        [Fact]
        public void EveryLotWithAHouseReachesTheStreetNetwork()
        {
            // §6.1's connectivity check, stated per lot rather than as a global component count: a
            // lot is served when a walk from its door along road reaches a main street.
            var input = PackedNeighbourhood();
            var graph = PackedGraph(out _);
            var components = graph.Components();
            var main = components.LargestId;

            var houses = input.UsableLots.Count(l => l.HasHouse);
            Assert.Equal(houses, graph.Entrances.Count);

            foreach (var entrance in graph.Entrances)
            {
                Assert.True(graph.IsRoadAt(entrance.X, entrance.Y),
                    "lot " + entrance.LotNumber + " has no road on its door tile");
                Assert.True(components.Contains(entrance.Y * input.MapSize + entrance.X, main),
                    "lot " + entrance.LotNumber + " is not on the main network");
                Assert.True(ReachesAStreet(graph, input.MapSize, entrance.X, entrance.Y),
                    "lot " + entrance.LotNumber + " cannot reach a street from its door");
            }
        }

        [Fact]
        public void EveryLotKeepsTheDoorSideTheLayoutGaveItWhenItHasRoomOnThatSide()
        {
            // The generator only overrides DoorSide when the lot is flush against a map border and
            // that side has no ground at all - the corner lots, whose door side points off the edge
            // of the world. Everywhere else the layout's own choice stands, because a
            // neighbourhood whose driveways all faced the same way regardless of the layout would
            // be a layout the renderer cannot reason about.
            var input = PackedNeighbourhood();
            var graph = PackedGraph(out _);

            foreach (var entrance in graph.Entrances)
            {
                var lot = input.UsableLots.First(l => l.Number == entrance.LotNumber);
                if (entrance.Side == NeighbourhoodEntrance.UnknownSide) continue;

                var fits = RoadGraph.TryResolveAnchor(
                    lot, LotMaskOf(graph), input.MapSize, out _, out _);
                if (!fits) continue;

                Assert.Equal(lot.DoorSide, entrance.Side);
                Assert.Equal(NeighbourhoodDoorSide.DeltaX(lot.DoorSide), entrance.FacingX);
                Assert.Equal(NeighbourhoodDoorSide.DeltaY(lot.DoorSide), entrance.FacingY);
            }
        }

        [Fact]
        public void ALotFlushAgainstTheMapBorderStillGetsADriveway()
        {
            // The corner lot. M1 picks the door side nearest a map border, which for the lots
            // packed along the edge of the map is a side with no ground on it: the anchor would be
            // at y = -1 and the lot would silently drop out of the network. On a real packed map
            // that is a twelfth of the lots, and "a lot with no entrance" is not something the
            // connectivity check can even notice.
            var graph = PackedGraph(out _);
            var entrances = graph.Entrances;

            Assert.Equal(41, entrances.Count);
            Assert.All(entrances, e =>
            {
                Assert.InRange(e.X, 0, NeighbourhoodTestInput.RealMapSize - 1);
                Assert.InRange(e.Y, 0, NeighbourhoodTestInput.RealMapSize - 1);
            });

            // And the fallback really does fire: at least one lot's own door side pointed off the
            // edge of the map and had to be replaced.
            var input = PackedNeighbourhood();
            Assert.Contains(
                graph.Entrances,
                e => input.UsableLots
                    .First(l => l.Number == e.LotNumber).DoorSide != e.Side);
        }

        // ---------------------------------------------------------------- widths

        [Fact]
        public void StreetsAreTheLotViewsWidthAndSpursAreOne()
        {
            // §6.1's "spurs 1" is unchanged. The street width is six, because that is the road
            // RestoreRoad draws around the player (three tarmac tiles a side, and both sides are
            // drawn at the lot view's own Size - 2 pitch) and a generated map tile is a lot tile.
            var graph = PackedGraph(out _);

            Assert.Equal(6, RoadGraph.MainStreetWidth);
            Assert.Equal(1, RoadGraph.DrivewayWidth);
            Assert.Equal(RoadGraph.MainStreetWidth, graph.StreetWidth);

            foreach (var edge in graph.Edges)
            {
                if (edge.Kind != RoadEdgeKind.Street)
                {
                    Assert.Equal(RoadGraph.DrivewayWidth, edge.Width);
                    continue;
                }

                //A street is as wide as the ground it is laid in, capped at the nominal - so this
                //is a range and not an equality, and the packed fixture lands at the low end
                //because the packer's two-tile margin cannot hold six tiles of tarmac
                Assert.InRange(edge.Width, RoadGraph.DrivewayWidth, RoadGraph.MainStreetWidth);
            }
        }

        [Fact]
        public void AStreetIsAsWideAsTheGroundItIsLaidInRatherThanTheWholeCorridor()
        {
            // The rule the width follows: a street takes the free run it was measured from, capped
            // at MainStreetWidth, and leaves the pavement on the lot's own side. On the packed
            // fixture every interior gap is the packer's own corridor, and that corridor is wider
            // than the nominal - so every interior street is the full six tiles and the two tiles
            // it does not need are footway against the lots, rather than the street taking the
            // gap whole as it did when the packer reserved two tiles.
            var graph = PackedGraph(out _);
            var margin = NeighbourhoodLayoutBuilder.SidewalkMargin;

            Assert.NotEmpty(graph.StreetRuns);
            Assert.Equal(RoadGraph.MainStreetWidth, graph.StreetWidth);
            Assert.All(graph.StreetRuns, e =>
                Assert.InRange(e.Width, RoadGraph.DrivewayWidth, RoadGraph.MainStreetWidth));
            //and the corridor is wide enough for a full-width street and a footway on each side,
            //which is the whole reason the packer reserves it rather than a sidewalk
            Assert.True(margin >= RoadGraph.MainStreetCorridorWidth,
                "the packer reserves " + margin + " tiles between two lots, which cannot hold a "
                    + RoadGraph.MainStreetWidth + "-tile street and a footway either side");
            Assert.Contains(graph.StreetRuns,
                e => e.Width == Math.Min(RoadGraph.MainStreetWidth, margin));

            //and the narrowest street is never narrower than one tile, however tight the packing
            Assert.All(graph.StreetRuns, e => Assert.True(e.Width >= 1));
        }

        [Fact]
        public void AStreetIsTwoTilesOfRoadRatherThanTwoSeparateOneTileLanes()
        {
            // Every tile of a street's width has to be road, or the street renders as a
            // single-width road with a hole beside it. Checked at the run's own width rather than
            // at the nominal, because a street in a two-tile gap is two tiles and a street in a
            // wider gap is six, and both are whole bands of tarmac.
            var graph = PackedGraph(out _);
            var street = graph.StreetRuns.First(e => e.IsVertical && e.Length > 100);

            for (var y = street.AY + 1; y < street.BY; y++)
            {
                Assert.True(graph.IsStreetAt(street.AX, y), "missing tile at x=" + street.AX);
                Assert.True(graph.IsStreetAt(street.AX + street.Width - 1, y),
                    "missing tile at x=" + (street.AX + street.Width - 1));
            }
        }

        [Fact]
        public void AWideGapGetsAFullWidthStreetRatherThanAPavementStrip()
        {
            // The other side of the same rule, and the reason the width is worth widening: where
            // the ground allows six tiles of tarmac, the generator must take six. A hand-placed
            // layout with a twelve-tile gap either side of a lot is the smallest honest way to ask
            // the question, and it is what a neighbourhood with room between its blocks looks like.
            var input = NeighbourhoodTestInput.WideGaps();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            Assert.NotEmpty(graph.StreetRuns);
            Assert.Equal(RoadGraph.MainStreetWidth, graph.StreetWidth);
            Assert.Contains(graph.StreetRuns,
                e => e.Width == RoadGraph.MainStreetWidth && e.Tiles > RoadGraph.MainStreetWidth);
        }

        [Fact]
        public void ATightPackingStillGetsStreetsJustNarrowerOnes()
        {
            // A layout whose every gap is one tile cannot hold a two-tile street, and the answer
            // is a one-tile street rather than no street at all. "No street" is the bug this
            // milestone fixed; the width is the tunable part. Nine 31-tile lots on a 32-tile
            // pitch fill a 96 map to within one tile on every side, so there is nowhere at all
            // for a second tile.
            var input = NeighbourhoodTestInput.PackedNeighbourhood(9, 31, 1, 96);
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            //The nominal is still six - the ground is what narrows the street, not the generator
            //changing its mind about how wide a street is - and every run in this layout came out
            //of a one-tile gap, so every run is one tile.
            Assert.Equal(RoadGraph.MainStreetWidth, graph.StreetWidth);
            Assert.NotEmpty(graph.StreetRuns);
            Assert.True(graph.Stats.StreetShare > 0.5, graph.DescribeNetwork());
            Assert.All(graph.StreetRuns, e => Assert.Equal(1, e.Width));
            Assert.Null(Record.Exception(() => graph.VerifyConnected()));
        }

        // ---------------------------------------------------------------- pavements

        [Fact]
        public void AGapWiderThanTheStreetLeavesAPavementAgainstTheLot()
        {
            // The pavement rule: whatever the gap has spare becomes footway, and as much of it as
            // SidewalkWidth stays on the lot's side, so a lot has a footway in front of its own
            // door rather than across the road.
            var input = NeighbourhoodTestInput.WideGaps();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            foreach (var lot in input.UsableLots)
            {
                // Not the doorway itself: the driveway's doorstep is road by design, so the check
                // has to look along the edge rather than at the tile in the middle of it.
                Assert.True(graph.IsSidewalkAt(lot.Right, lot.WorldY + 1),
                    "lot " + lot.Number + " has no pavement along the top of its door edge");
                Assert.True(graph.IsSidewalkAt(lot.Right, lot.Bottom - 2),
                    "lot " + lot.Number + " has no pavement along the bottom of its door edge");
            }
        }

        [Fact]
        public void SidewalkIsNeverUnderneathARoadOrInsideALot()
        {
            var graph = PackedGraph(out _);
            var input = PackedNeighbourhood();

            for (int i = 0; i < graph.Sidewalk.Length; i++)
            {
                if (!graph.Sidewalk[i]) continue;
                Assert.False(graph.Road[i]);
                Assert.False(graph.LotMask.Interior[i]);
                Assert.Equal(0, graph.Sidewalk[i] ? 0 : 1);
            }

            Assert.True(graph.Stats.SidewalkTiles > 0,
                "a neighbourhood with open ground at its edges should have some pavement");
            Assert.NotEmpty(input.UsableLots);
        }

        [Fact]
        public void TheSidewalkPavementRuleIsReportedNotAssumed()
        {
            // The packer reserves a whole street corridor between two lots, and the rule for what
            // the corridor has spare is that as much of it as RoadGraph.SidewalkWidth stays on the
            // lot's own side. That is a real limit of the search rather than of the packing, so it
            // is stated in the numbers a bug report can quote instead of being left for somebody
            // to rediscover from a screenshot: a gap eight tiles wide and a gap twenty-four tiles
            // wide both put one tile of footway against the lot, and what the wider gap really
            // buys is ground that is neither street nor footway. It was the other way round before
            // the corridor was reserved - the packer left two tiles, which a six-tile street
            // swallowed whole, so a packed neighbourhood reported no pavement at all. The
            // comparison is per lot, because the two fixtures differ in map size and lot count and
            // a raw tile count would say nothing.
            var packedInput = PackedNeighbourhood();
            var packedStats = PackedGraph(out _).Stats;
            var wideInput = NeighbourhoodTestInput.WideGaps();
            var wideStats = RoadGraph.Build(
                wideInput, NeighbourhoodMap.Create(wideInput.MapSize)).Stats;

            var packedPerLot = packedStats.SidewalkTiles / (double)packedInput.UsableLots.Count;
            var widePerLot = wideStats.SidewalkTiles / (double)wideInput.UsableLots.Count;

            //The cap, reported: no more footway than a lot's own four edges can carry, whatever
            //the surplus - so a wide gap is not "more pavement", it is more open ground.
            var packedCap = 4 * RoadGraph.SidewalkWidth * packedInput.UsableLots[0].EffectiveSize;
            var wideCap = 4 * RoadGraph.SidewalkWidth * wideInput.UsableLots[0].EffectiveSize;
            Assert.True(packedPerLot <= packedCap,
                "a lot's four edges cannot carry more than " + packedCap + " tiles of footway, and "
                    + packedInput.UsableLots.Count + " lot(s) reported " + packedStats.SidewalkTiles);
            Assert.True(widePerLot <= wideCap,
                "a lot's four edges cannot carry more than " + wideCap + " tiles of footway, and "
                    + wideInput.UsableLots.Count + " lot(s) reported " + wideStats.SidewalkTiles);

            //And the packed fixture now reports some, which is what reserving the corridor bought:
            // six tiles of tarmac in an eight-tile gap leaves two for the footway.
            Assert.True(RoadGraph.MainStreetWidth < NeighbourhoodLayoutBuilder.SidewalkMargin,
                "a corridor the street fills whole cannot leave a footway, so the packer has to "
                    + "reserve more than MainStreetWidth");
            Assert.True(packedPerLot > 0,
                "a packed neighbourhood with a full corridor reserved should leave footway against "
                    + "its lots, and it reported none");
        }

        // ---------------------------------------------------------------- determinism

        [Fact]
        public void TheSamePackedNeighbourhoodGeneratesIdenticalStreetsTwice()
        {
            var a = PackedGraph(out var mapA);
            var b = PackedGraph(out var mapB);

            Assert.Equal(mapA.RoadData, mapB.RoadData);
            Assert.Equal(a.Stats.Summary(), b.Stats.Summary());
            Assert.Equal(a.StreetRuns.Count, b.StreetRuns.Count);
            for (int i = 0; i < a.StreetRuns.Count; i++)
            {
                Assert.Equal(a.StreetRuns[i].ToString(), b.StreetRuns[i].ToString());
            }
        }

        [Fact]
        public void StreetsDoNotDependOnTheOrderTheLotsAreHandedOver()
        {
            var input = PackedNeighbourhood();
            var shuffled = new NeighbourhoodMapInput(
                input.Seed, input.MapSize, input.Lots.Reverse().ToArray());

            var a = RoadGraph.Build(input, NeighbourhoodMap.Create(input.MapSize));
            var b = RoadGraph.Build(shuffled, NeighbourhoodMap.Create(input.MapSize));

            // The lot order decides which lot's door is where, so the entrances move with it; what
            // must not move is the shape of the network itself.
            Assert.Equal(a.Stats.StreetRuns, b.Stats.StreetRuns);
            Assert.Equal(a.Stats.LongestStreetRun, b.Stats.LongestStreetRun);
            Assert.Equal(a.Stats.StreetTiles, b.Stats.StreetTiles);
        }

        // ---------------------------------------------------------------- diagnostics

        [Fact]
        public void TheNetworkDescribesItselfInOneLineWithTheNumbersThatMatter()
        {
            var graph = PackedGraph(out _);
            var stats = graph.Stats;
            var line = graph.DescribeNetwork();

            Assert.Equal(stats.Summary(), line);
            Assert.Contains(stats.StreetRuns + " street run(s)", line);
            Assert.Contains(stats.StreetTiles + " tile(s)", line);
            Assert.Contains("longest " + stats.LongestStreetRun, line);
            Assert.Contains(stats.DrivewaySpurs + " driveway spur(s)", line);
            Assert.Contains(stats.Components + " component(s)", line);
            Assert.Contains(stats.SidewalkTiles + " sidewalk tile(s)", line);
        }

        [Fact]
        public void AGeneratedMapHandsTheDiagnosticLineToWhicheverSinkAsked()
        {
            var original = NeighbourhoodMapGenerator.DiagnosticSink;
            var lines = new List<string>();
            try
            {
                NeighbourhoodMapGenerator.DiagnosticSink = lines.Add;
                NeighbourhoodMapGenerator.Generate(PackedNeighbourhood());
            }
            finally
            {
                NeighbourhoodMapGenerator.DiagnosticSink = original;
            }

            Assert.Contains(lines, l => l.Contains("street run(s)") && l.Contains("component(s)"));
        }

        [Fact]
        public void AFailureSaysWhatTheNetworkLookedLike()
        {
            // The 2D fallback logs the exception's message, so the one line a player ever sees
            // about a broken neighbourhood has to carry the numbers as well as the reason.
            var input = NeighbourhoodTestInput.SolidWallBetweenTwoHouses();
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            var failure = Assert.Throws<InvalidOperationException>(() => graph.VerifyConnected());
            Assert.Contains("components", failure.Message);
            Assert.Contains("street run(s)", failure.Message);
        }

        // ---------------------------------------------------------------- helpers

        private static NeighbourhoodLotMask LotMaskOf(RoadGraph graph)
        {
            return graph.LotMask;
        }

        private static bool ReachesAStreet(RoadGraph graph, int size, int x, int y)
        {
            var seen = new bool[graph.Road.Length];
            var queue = new Queue<int>();
            var start = y * size + x;
            seen[start] = true;
            queue.Enqueue(start);

            while (queue.Count > 0)
            {
                var index = queue.Dequeue();
                if (graph.IsStreetAt(index % size, index / size)) return true;

                var cx = index % size;
                var cy = index / size;
                for (int step = 0; step < 4; step++)
                {
                    int dx, dy;
                    RoadMask.GetStep((byte)(step + 1), out dx, out dy);
                    var nx = cx + dx;
                    var ny = cy + dy;
                    if (nx < 0 || ny < 0 || nx >= size || ny >= size) continue;
                    if (!graph.IsRoadAt(nx, ny)) continue;
                    var ni = ny * size + nx;
                    if (seen[ni]) continue;
                    seen[ni] = true;
                    queue.Enqueue(ni);
                }
            }

            return false;
        }
    }
}
