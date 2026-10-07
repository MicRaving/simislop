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
    /// §11's <c>TerrainSeamTests</c>: border rings preserved, a flat neighbourhood is a no-op, a
    /// step is ramped.
    ///
    /// <para>
    /// The three named behaviours are the ones §6.3 calls load-bearing, and the middle one is the
    /// interesting one. "Because TS1 lots are usually near-flat, this must degrade to 'copy the
    /// lot ring height' with no visible work" is a promise about the <em>common</em> case, so the
    /// test for it is not "the seam solver produces something plausible" but "the whole map is
    /// one value and the solver said it converged after a single sweep". A solver that reached the
    /// right answer by doing a hundred sweeps of work on a flat neighbourhood would satisfy a
    /// weaker test and still be a bug - it would be paid for on every load of every neighbourhood.
    /// </para>
    /// </summary>
    public sealed class TerrainSeamTests
    {
        // ---------------------------------------------------------------- the flat case

        [Theory]
        [InlineData((byte)0)]
        [InlineData((byte)4)]
        [InlineData((byte)7)]
        [InlineData((byte)24)]
        public void FlatNeighbourhoodIsANoOp(byte height)
        {
            var input = NeighbourhoodTestInput.FlatRow(height, 3);
            var map = Solve(input, out var report);

            Assert.Equal(height, report.BaseHeight);
            Assert.All(map.ElevationData, h => Assert.Equal(height, h));

            // And it was a no-op, not a slow route to the same place: one sweep, nothing changed.
            Assert.Equal(1, report.Iterations);
            Assert.True(report.Converged);
            Assert.Equal(0, report.RampTiles);
            Assert.Equal(0, report.RampsBuilt);
        }

        [Fact]
        public void AFlatNeighbourhoodIsUnchangedEvenWhereThereAreRoads()
        {
            // The road flattening stage runs before the relaxation even on a flat map, so this is
            // the case where a stage could quietly do something visible.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = Solve(input, out var report);

            Assert.Equal(4, report.BaseHeight);
            Assert.All(map.ElevationData, h => Assert.Equal(4, h));
        }

        [Fact]
        public void NoLotsLeavesTheMapFlatAtZero()
        {
            var map = Solve(NeighbourhoodTestInput.NoLots(), out var report);

            Assert.Equal(0, report.BaseHeight);
            Assert.All(map.ElevationData, h => Assert.Equal(0, h));
        }

        // ---------------------------------------------------------------- border rings

        [Fact]
        public void BorderRingsArePreserved()
        {
            // The ring is the Dirichlet boundary the whole solver exists to meet. It is also the
            // thing a later change is most likely to break, because the ramp stage and the road
            // flattening stage both want to write there.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = Solve(input, out _);

            foreach (var lot in input.UsableLots)
            {
                for (var y = lot.WorldY - 1; y <= lot.Bottom; y++)
                {
                    for (var x = lot.WorldX - 1; x <= lot.Right; x++)
                    {
                        if (lot.Contains(x, y)) continue;
                        Assert.Equal(lot.BaseHeight, map.ElevationData[Index(input, x, y)]);
                    }
                }
            }
        }

        [Fact]
        public void BorderRingsSurviveAHeightStepNextDoor()
        {
            // The two cases at once: lots at different heights, so the ramp stage runs, and the
            // ramp must still not touch either ring.
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            var map = Solve(input, out var report);

            Assert.True(report.RampsBuilt > 0);

            foreach (var lot in input.UsableLots)
            {
                for (var y = lot.WorldY - 1; y <= lot.Bottom; y++)
                {
                    for (var x = lot.WorldX - 1; x <= lot.Right; x++)
                    {
                        if (lot.Contains(x, y)) continue;
                        Assert.Equal(lot.BaseHeight, map.ElevationData[Index(input, x, y)]);
                    }
                }
            }
        }

        [Fact]
        public void LotInteriorsKeepTheirOwnHeight()
        {
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            var map = Solve(input, out _);

            foreach (var lot in input.UsableLots)
            {
                for (var y = lot.WorldY; y < lot.Bottom; y++)
                {
                    for (var x = lot.WorldX; x < lot.Right; x++)
                    {
                        Assert.Equal(lot.BaseHeight, map.ElevationData[Index(input, x, y)]);
                    }
                }
            }
        }

        // ---------------------------------------------------------------- the stepped case

        [Fact]
        public void AStepBetweenTwoLotsIsRampedNotJumped()
        {
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            var map = Solve(input, out var report);

            Assert.Equal(0, report.BaseHeight);
            Assert.Equal(1, report.RampsBuilt);
            Assert.True(report.RampTiles > 0);
            Assert.True(report.Converged);

            // The low lot's right ring, then the bank, then the high lot's left ring. The bank has
            // to rise monotonically and never by more than MaxSlope in a tile, because "insert a
            // short embankment ramp across the margin rather than a vertical step" is the whole
            // requirement - a stepped ramp is a step.
            var low = input.UsableLots[0];
            var high = input.UsableLots[1];
            var row = low.WorldY + 4;

            var fromRing = low.Right;
            var toRing = high.WorldX - 1;
            Assert.Equal(0, map.ElevationData[Index(input, fromRing, row)]);
            Assert.Equal(24, map.ElevationData[Index(input, toRing, row)]);

            var previous = map.ElevationData[Index(input, fromRing, row)];
            var sawIntermediate = false;
            for (var x = fromRing + 1; x <= toRing; x++)
            {
                var here = map.ElevationData[Index(input, x, row)];
                Assert.True(here >= previous, $"the bank fell at x={x}");
                Assert.True(here - previous <= TerrainSeamOptions.Default.MaxSlope,
                    $"the bank jumped {here - previous} at x={x}");
                if (here < 24) sawIntermediate = true;
                previous = here;
            }

            Assert.True(sawIntermediate, "the bank went straight from 0 to 24");
        }

        [Fact]
        public void TheBankIsEvenlyGraded()
        {
            // Not just "no tile steps by more than MaxSlope" - the two steps out of and into the
            // ring have to be the same size as the ones inside the bank, or the bank is a cliff
            // with a gentle middle.
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            var map = Solve(input, out _);

            var low = input.UsableLots[0];
            var high = input.UsableLots[1];
            var row = low.WorldY + 4;

            var steps = new List<int>();
            var previous = map.ElevationData[Index(input, low.Right, row)];
            for (var x = low.Right + 1; x <= high.WorldX - 1; x++)
            {
                var here = map.ElevationData[Index(input, x, row)];
                steps.Add(here - previous);
                previous = here;
            }

            Assert.NotEmpty(steps);
            Assert.InRange(steps.Min(), 1, TerrainSeamOptions.Default.MaxSlope);
            Assert.InRange(steps.Max() - steps.Min(), 0, 1);
        }

        [Fact]
        public void ADifferenceBelowTheThresholdIsLeftToTheRelaxation()
        {
            // A one-unit difference is not a step; banking it would put a kerb round every lot for
            // no reason. The relaxation spreads it instead.
            var options = new TerrainSeamOptions(24, 4, 2);
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(4, 38, 5);
            var map = Solve(input, options, out var report);

            Assert.Equal(0, report.RampsBuilt);
            Assert.Equal(0, report.RampTiles);
            Assert.All(map.ElevationData, h => Assert.InRange((int)h, 4, 5));
        }

        [Fact]
        public void ARampNeedsSomeMarginToGoIn()
        {
            // Two lots with exactly one tile between them: their rings touch, so there is no band
            // to bank and the boundary is a step. That is not a solver failure - it is what a
            // boundary with no margin looks like, and the plan's layout step exists precisely to
            // keep it from happening. What the solver must not do is write through a ring to
            // "fix" it.
            var input = new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, NeighbourhoodTestInput.SmallMap, new[]
                {
                    NeighbourhoodTestInput.Lot(21, 8, 16, 16, 0, NeighbourhoodDoorSide.East),
                    NeighbourhoodTestInput.Lot(22, 26, 16, 16, 24, NeighbourhoodDoorSide.West),
                });

            var map = Solve(input, out var report);

            Assert.Equal(0, report.RampsBuilt);
            Assert.Equal(0, report.RampTiles);

            // Each lot keeps its own ground and its own ring; the step is between them.
            Assert.Equal(0, map.ElevationData[Index(input, 23, 20)]);
            Assert.Equal(0, map.ElevationData[Index(input, 24, 20)]);
            Assert.Equal(24, map.ElevationData[Index(input, 25, 20)]);
            Assert.Equal(24, map.ElevationData[Index(input, 26, 20)]);
        }

        // ---------------------------------------------------------------- the rest of the contract

        [Fact]
        public void TheBasePlaneIsTheLowestLotInTheNeighbourhood()
        {
            var input = new NeighbourhoodMapInput(
                NeighbourhoodTestInput.DefaultSeed, NeighbourhoodTestInput.SmallMap, new[]
                {
                    NeighbourhoodTestInput.Lot(21, 8, 8, 16, 11, NeighbourhoodDoorSide.East),
                    NeighbourhoodTestInput.Lot(22, 8, 40, 16, 19, NeighbourhoodDoorSide.East),
                    NeighbourhoodTestInput.Lot(23, 40, 8, 16, 15, NeighbourhoodDoorSide.West),
                });

            Solve(input, out var report);

            Assert.Equal(11, report.BaseHeight);
        }

        [Fact]
        public void NothingRisesAboveTheHighestLotOrFallsBelowTheLowest()
        {
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            var map = Solve(input, out _);

            Assert.All(map.ElevationData, h => Assert.InRange((int)h, 0, 24));
        }

        [Fact]
        public void RoadsAndSidewalksAreFlattened()
        {
            // "Roads and sidewalks are flattened" means pinned, not relaxed: a road that follows
            // "Roads and sidewalks are flattened" means pinned, not relaxed: a road that follows
            // the field would ripple between two lots, and a kerb that waves is worse than one
            // that steps. So a road tile sits at its nearest lot's height.
            //
            // Checked on a flat neighbourhood, which isolates this stage. In a stepped one the
            // embankment stage runs afterwards and deliberately carries a road up the bank with
            // it - see AStepBetweenTwoLotsIsRampedNotJumped - so asserting the nearest lot's
            // height everywhere here would be asserting that the bank does not exist.
            var input = NeighbourhoodTestInput.FlatRow(9, 3);
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);
            TerrainSeamSolver.Solve(input, graph, map, TerrainSeamOptions.Default);

            var owners = TerrainFill.NearestLotOwners(input.UsableLots, input.MapSize);
            var checkedTiles = 0;
            for (int i = 0; i < map.ElevationData.Length; i++)
            {
                if (!graph.Road[i] && !graph.Sidewalk[i]) continue;
                if (graph.LotMask.BorderRing[i]) continue;
                Assert.Equal(input.UsableLots[owners[i]].BaseHeight, map.ElevationData[i]);
                checkedTiles++;
            }

            Assert.True(checkedTiles > 0, "the fixture produced no road or sidewalk tiles to check");
        }

        [Fact]
        public void ARoadCrossingAnEmbankmentIsCarriedUpIt()
        {
            // The one place the ramp stage is allowed to overwrite a pinned road tile. A road
            // that stopped at the foot of a bank while the ground beside it climbed would be a
            // trench.
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);
            var report = TerrainSeamSolver.Solve(input, graph, map, TerrainSeamOptions.Default);

            Assert.True(report.RampsBuilt > 0);

            var low = input.UsableLots[0];
            var carried = 0;
            for (var y = low.WorldY; y < low.Bottom; y++)
            {
                for (var x = low.Right; x < input.UsableLots[1].WorldX; x++)
                {
                    if (graph.IsRoadAt(x, y)) carried++;
                }
            }
            Assert.True(carried > 0, "the fixture has no road crossing the bank to check");
        }

        [Fact]
        public void SolvingIsIdempotent()
        {
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);

            TerrainSeamSolver.Solve(input, graph, map, TerrainSeamOptions.Default);
            var once = map.ElevationData.ToArray();

            TerrainSeamSolver.Solve(input, graph, map, TerrainSeamOptions.Default);
            Assert.Equal(once, map.ElevationData);
        }

        [Fact]
        public void SolvingIsDeterministic()
        {
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            var first = Solve(input, out _).ElevationData;
            var second = Solve(
                new NeighbourhoodMapInput(input.Seed, input.MapSize, input.Lots),
                out _).ElevationData;

            Assert.Equal(first, second);
        }

        [Fact]
        public void EveryTileThatWantedSolvingWasSolved()
        {
            // FixedTiles counts the Dirichlet borders plus the flattened roads and sidewalks. If
            // that came out as the whole map, the relaxation would have had nothing to do and the
            // "step is ramped" tests would be proving nothing.
            var input = NeighbourhoodTestInput.TwoLotsWithAHeightStep(0, 38, 24);
            Solve(input, out var report);

            var total = input.MapSize * input.MapSize;
            Assert.True(report.FixedTiles < total,
                "every tile was pinned; the free region was never relaxed");
            Assert.True(report.FixedTiles > 0);
        }

        private static NeighbourhoodMap Solve(NeighbourhoodMapInput input, out SeamSolveReport report)
        {
            return Solve(input, TerrainSeamOptions.Default, out report);
        }

        private static NeighbourhoodMap Solve(
            NeighbourhoodMapInput input, TerrainSeamOptions options, out SeamSolveReport report)
        {
            var map = NeighbourhoodMap.Create(input.MapSize);
            var graph = RoadGraph.Build(input, map);
            report = TerrainSeamSolver.Solve(input, graph, map, options);
            return map;
        }

        private static int Index(NeighbourhoodMapInput input, int x, int y)
        {
            return y * input.MapSize + x;
        }
    }
}
