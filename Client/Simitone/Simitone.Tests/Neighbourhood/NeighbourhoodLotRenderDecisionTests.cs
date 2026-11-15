/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// What a neighbouring lot is drawn as: a real sub-world or a flat facade, and never both.
    /// B2 task 5.
    ///
    /// <para>
    /// The failure this exists for is invisible to every other kind of check. Part A built a
    /// facade for every lot in the drawn region; B2 stands the near ones up as real
    /// <c>SubWorldComponent</c> lots. Left alone, a near lot is drawn twice - once as a wall mesh
    /// at the right height, and once as a real lot with its own ground, its own walls and its own
    /// objects on the same plot. There is no exception, no wrong number and no log line: there is
    /// a neighbour that looks like a neighbour, and it is only wrong on the frames where the two
    /// copies' walls fight for the depth buffer.
    /// </para>
    ///
    /// <para>
    /// So the decision is a pure function of four facts and it is asserted here rather than
    /// reasoned about: near becomes a sub-world and not a facade, far stays a facade, and the
    /// player's own lot is neither.
    /// </para>
    /// </summary>
    public class NeighbourhoodLotRenderDecisionTests
    {
        /// <summary>The hand-written fixture's grid, as <c>SimitoneSurroundingsPlanTests</c> has it.</summary>
        private const int Size = 64;

        /// <summary>The row the fixture's single street runs along.</summary>
        private const int StreetY = 36;

        private const int LotSize = 16;

        private const short Active = 11;

        /// <summary>Next door: six tiles of gap, so inside the plan's radius of eight.</summary>
        private const short Near = 12;

        /// <summary>Across the street: twenty tiles of gap, inside the cull's forty and not inside eight.</summary>
        private const short Far = 13;

        /// <summary>A plot twenty tiles below next door, with nothing built on it.</summary>
        private const short Empty = 14;

        // ==================================================================== the fixture

        private static NeighbourhoodMap NeighbourMap()
        {
            var map = NeighbourhoodMap.Create(Size);
            for (int x = 0; x < Size; x++) map.RoadData[map.Index(x, StreetY)] = RoadMask.All;
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 48;
            return map;
        }

        private static NeighbourhoodLot Lot(short number, int x, int y, int size = LotSize,
            bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
                WorldX = x,
                WorldY = y,
                Rotation = 0,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                DoorSide = NeighbourhoodLotSide.North,
                HouseHash = 0xABCD,
                HasHouse = hasHouse
            };
        }

        private static NeighbourhoodLayout LayoutOf(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(4, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion, Size, lots);
        }

        /// <summary>
        /// Lot 11 at (4, 4) is the one a player is standing in. Lot 12 is six tiles below it, so
        /// inside the plan's radius of eight. Lot 13 is twenty across: outside eight, inside the
        /// forty the surround draws. Lot 14 is a bulldozed plot next door.
        /// </summary>
        private static NeighbourhoodLayout StreetLayout()
        {
            return LayoutOf(
                Lot(Active, 4, 4),
                Lot(Near, 4, 26),
                Lot(Far, 40, 4),
                Lot(Empty, 4, 48, LotSize, false));
        }

        /// <summary>
        /// Doors on the street, stated rather than derived: what is under test is the decision made
        /// from a plan, not how the road graph arrives at one - that is
        /// <c>SimitoneSurroundingsPlanTests</c>'s fixture and <c>RoadGraphTests</c>'s arithmetic.
        /// </summary>
        private static NeighbourhoodQuery StreetQuery(NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            return new NeighbourhoodQuery(layout, map, new[]
            {
                new NeighbourhoodEntrance(Active, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(Near, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(Far, 48, StreetY, 0, -1),
                new NeighbourhoodEntrance(Empty, 12, StreetY, 0, -1)
            });
        }

        private static SimitoneSurroundingsPlacement PlacementFor(
            NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            SimitoneSurroundingsPlacement placement;
            string reason;
            Assert.True(SimitoneSurroundingsPlacement.TryFor(
                layout, map, Active, out placement, out reason), reason);
            return placement;
        }

        /// <summary>
        /// The plan for the fixture at the radius under test, at the shared radius - the drawn
        /// region's own - so that the drawn region is fully real and the decision can be asked over
        /// it whole.
        /// </summary>
        private static SimitoneSurroundingsPlan Plan(
            NeighbourhoodMap map, NeighbourhoodLayout layout, int radius)
        {
            return SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map), map, StreetQuery(layout, map), layout, null, radius);
        }

        /// <summary>The plan for the fixture at a named radius and budget.</summary>
        private static SimitoneSurroundingsPlan Plan(
            NeighbourhoodMap map, NeighbourhoodLayout layout, int radius, int maxSubWorlds)
        {
            return SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map), map, StreetQuery(layout, map), layout, null,
                radius, maxSubWorlds);
        }

        // ==================================================================== the four-fact rule

        [Fact]
        public void ThePlayersOwnLotIsNeitherASubWorldNorAFacade()
        {
            // The lot view draws it in full, from its own house file, right now. A facade for it is
            // a whole World and a VM stood up to build the house the player can see through the
            // window; a sub-world for it is a copy of the lot standing inside the lot.
            Assert.Equal(NeighbourhoodLotRender.None,
                NeighbourhoodRenderDecision.Decide(true, true, false, true));
            Assert.Equal(NeighbourhoodLotRender.None,
                NeighbourhoodRenderDecision.Decide(false, true, false, false));

            // And the ordering: even if a plan did list the active lot, the lot view wins. The
            // decision must not depend on the plan having got that right.
            Assert.Equal(NeighbourhoodLotRender.None,
                NeighbourhoodRenderDecision.Decide(true, true, true, true));
        }

        [Fact]
        public void APlannedNeighbourIsASubWorldAndNotAFacade()
        {
            // The whole of B2 task 5 in one assertion. Inside the cut as well - it always is - and
            // the answer is still SubWorld, so no facade is built or drawn for it.
            Assert.Equal(NeighbourhoodLotRender.SubWorld,
                NeighbourhoodRenderDecision.Decide(true, false, true, true));
            Assert.True(NeighbourhoodRenderDecision.IsSubWorld(NeighbourhoodLotRender.SubWorld));
            Assert.False(NeighbourhoodRenderDecision.IsFacade(NeighbourhoodLotRender.SubWorld));
        }

        [Fact]
        public void APlannedNeighbourIsASubWorldEvenWithNothingBuiltOnIt()
        {
            // A bulldozed plot is still a real lot: the sub-world is built out of the generated
            // map, and a neighbour with no contents is built blank. It is not downgraded to
            // nothing because there is no HouseNN.iff to render walls from.
            Assert.Equal(NeighbourhoodLotRender.SubWorld,
                NeighbourhoodRenderDecision.Decide(false, false, true, true));
        }

        [Fact]
        public void AFarNeighbourIsStillAFacade()
        {
            // The plan's radius is a budget of real lots; the drawn region is much wider, and the
            // ring of lots between the two has to keep its houses or the street ends in grass.
            Assert.Equal(NeighbourhoodLotRender.Facade,
                NeighbourhoodRenderDecision.Decide(true, false, false, true));
            Assert.True(NeighbourhoodRenderDecision.IsFacade(NeighbourhoodLotRender.Facade));
            Assert.False(NeighbourhoodRenderDecision.IsSubWorld(NeighbourhoodLotRender.Facade));
        }

        [Fact]
        public void ALotOutsideTheDrawnRegionIsNeither()
        {
            // Nothing is drawn there: the ground was never cropped out to it, so a facade would be
            // a house floating over the void.
            Assert.Equal(NeighbourhoodLotRender.None,
                NeighbourhoodRenderDecision.Decide(true, false, false, false));
        }

        [Fact]
        public void AnEmptyPlotIsNeither()
        {
            // Inside the cut, with no house on it and not planned: there is no mesh to build and
            // nothing for a facade to be. The plan's "lots with no house draw nothing".
            Assert.Equal(NeighbourhoodLotRender.None,
                NeighbourhoodRenderDecision.Decide(false, false, false, true));
        }

        [Fact]
        public void EveryCombinationIsOneOfTheThreeAnswersAndNeverTwo()
        {
            // No combination of the four facts produces a fourth answer, and none of them produces
            // a lot that is both a sub-world and a facade - which is what "one decision per lot"
            // means when it is stated as arithmetic rather than as a hope.
            for (int flags = 0; flags < 16; flags++)
            {
                var render = NeighbourhoodRenderDecision.Decide(
                    (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0);
                Assert.True(render == NeighbourhoodLotRender.None
                    || render == NeighbourhoodLotRender.Facade
                    || render == NeighbourhoodLotRender.SubWorld);
                Assert.False(NeighbourhoodRenderDecision.IsSubWorld(render)
                    && NeighbourhoodRenderDecision.IsFacade(render));
            }
        }

        [Fact]
        public void NoPlanSubWorldsNothing()
        {
            // The capability-off case, and it has to be exact: no plan means no sub-worlds, so the
            // decision is the facade test Part A already had.
            Assert.False(NeighbourhoodRenderDecision.IsSubWorld((SimitoneSurroundingsPlan)null, Near));
            Assert.False(NeighbourhoodRenderDecision.IsSubWorld(
                (IReadOnlyCollection<short>)null, Near));
            Assert.False(NeighbourhoodRenderDecision.IsSubWorld(new short[0], Near));
        }

        // ==================================================================== the cut

        [Fact]
        public void InsideCutIsTheRegionsOwnTest()
        {
            // One call, so the facade queue and the decision cannot disagree about which lots
            // exist: a lot the cache would queue a mesh for is a lot the decision calls a facade.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var inRange = cull.LotsInRange(layout, Active);

            Assert.Equal(3, inRange.Count);
            for (int i = 0; i < inRange.Count; i++)
            {
                Assert.True(NeighbourhoodRenderDecision.InsideCut(inRange[i], cull));
            }
            //and a lot past the drawn region is not in it
            Assert.False(NeighbourhoodRenderDecision.InsideCut(Lot(99, 4, 4 + 40 + 16 + 8), cull));

            //the cut is about the region, not about what is built on the lot: the bulldozed plot
            //is inside it and still draws nothing, which is the next rule rather than this one
            Assert.True(NeighbourhoodRenderDecision.InsideCut(layout.FindLot(Empty), cull));
        }

        [Fact]
        public void NoRegionMeansNoCut()
        {
            // The map view's situation: it draws the whole neighbourhood and has no drawn region
            // at all, so every lot is in it. A null cull is "no cut", not "nothing is in it".
            Assert.True(NeighbourhoodRenderDecision.InsideCut(Lot(Near, 4000, 4000), null));

            //An invalid cull - no map, no lot - keeps nothing, which is the honest answer for a
            //region that does not exist.
            var nothing = default(SimitoneSurroundingsCull);
            Assert.False(NeighbourhoodRenderDecision.InsideCut(Lot(Near, 4000, 4000), nothing));
        }

        // ==================================================================== the two radii

        [Fact]
        public void TheSubWorldRadiusFitsInsideTheDrawnRegion()
        {
            // The two numbers are allowed to differ - a caller may deliberately plan a narrow ring of
            // real lots - but not in this direction. A sub-world radius wider than the cull's would
            // build a neighbour as a real lot standing on ground the surround never drew.
            Assert.True(SimitoneSurroundingsPlan.RadiusFitsCut(
                SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles,
                SimitoneSurroundingsPlacement.CullRadiusTiles));
            Assert.True(SimitoneSurroundingsPlan.RadiusFitsCut(40, 40));
            Assert.True(SimitoneSurroundingsPlan.RadiusFitsCut(-4, 0));
            Assert.False(SimitoneSurroundingsPlan.RadiusFitsCut(41, 40));
            Assert.False(SimitoneSurroundingsPlan.RadiusFitsCut(8, 7));
        }

        [Fact]
        public void TheSubWorldRadiusIsTheDrawnRegionsOwnRadius()
        {
            // The stronger half, and the shipped shape: the two radii are the same number, so the
            // drawn region is the sub-world region and the only thing that can leave a flat lot in
            // view is the budget. Equality rather than "fits" is what makes the coverage claim
            // readable off a log line, and a test asserting only RadiusFitsCut would still pass on
            // the eight-tile ring this replaced.
            Assert.Equal(SimitoneSurroundingsPlacement.CullRadiusTiles,
                SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles);

            Assert.True(SimitoneSurroundingsPlan.RadiusIsShared(
                SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles,
                SimitoneSurroundingsPlacement.CullRadiusTiles));
            Assert.True(SimitoneSurroundingsPlan.RadiusIsShared(40, 40));
            Assert.True(SimitoneSurroundingsPlan.RadiusIsShared(-4, 0));
            Assert.False(SimitoneSurroundingsPlan.RadiusIsShared(8, 40));
            Assert.False(SimitoneSurroundingsPlan.RadiusIsShared(41, 40));

            //and it holds for a real plan and a real region together, which is the form the screen
            //and the surround actually ask it in
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var plan = Plan(map, layout, SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles);

            Assert.True(plan.RadiusSharedWith(cull));
        }

        [Fact]
        public void TheSubWorldBudgetBoundsHowManyLotsStandUpAndTheRestAreFacades()
        {
            // The budget as a per-lot decision over a whole layout: every lot in the drawn region
            // is a sub-world while the budget holds, and a facade once it does not. Neither half is
            // vacuous here - the fixture has three neighbours and the budgets are one and two - and
            // the two halves partition the region rather than one of them being "whatever is left".
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var queued = cull.LotsInRange(layout, Active);
            Assert.Equal(3, queued.Count);

            foreach (var budget in new[] { 0, 1, 2, 3, 4 })
            {
                var plan = Plan(map, layout,
                    SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles, budget);
                Assert.True(plan.IsValid);
                Assert.Equal(budget < 0 ? 0 : budget, plan.SubWorldBudget);
                Assert.Equal(Math.Min(budget, 3), plan.Neighbours.Count);
                Assert.Equal(3 - Math.Min(budget, 3), plan.DeferredCount);
                Assert.Equal(plan.DeferredCount == 0, plan.IsFullyCovered);

                var subWorlds = 0;
                var facades = 0;
                var deferredWithHouse = 0;
                for (int i = 0; i < queued.Count; i++)
                {
                    var lot = queued[i];
                    var render = NeighbourhoodRenderDecision.Decide(lot, Active, plan, cull);
                    //never both
                    Assert.False(NeighbourhoodRenderDecision.IsFacade(render)
                            && NeighbourhoodRenderDecision.IsSubWorld(render));
                    //and never neither, for any lot with a house on it: a lot the budget deferred
                    //is a facade, not a hole. A bulldozed plot is the one lot that can be neither,
                    //and that is Part A's rule rather than this one's - it has no mesh to build,
                    //though it can still be a real sub-world, which is what it is on the full plan.
                    if (lot.HasHouse) Assert.NotEqual(NeighbourhoodLotRender.None, render);
                    if (NeighbourhoodRenderDecision.IsSubWorld(render)) subWorlds++;
                    if (NeighbourhoodRenderDecision.IsFacade(render))
                    {
                        facades++;
                        deferredWithHouse++;
                        //a facade is always a lot the budget deferred - never one the radius
                        //excluded, because the radius is the region's own
                        Assert.Contains(lot.Number, plan.DeferredNumbers);
                    }
                }
                Assert.Equal(plan.Neighbours.Count, subWorlds);
                //The deferred count includes the bulldozed plot, which draws nothing whatever it is
                //deferred to - so the facades are the deferred lots that have a house on them, and
                //the two counts only coincide when the bulldozed plot is itself deferred.
                Assert.Equal(facades, NeighbourhoodRenderDecision.FacadeFallbackCount(
                    layout, Active, plan, cull));
                Assert.Equal(plan.DeferredCount,
                    deferredWithHouse + (plan.DeferredNumbers.Contains((short)Empty) ? 1 : 0));
            }

            //The nearest-first prefix, over a budget of one. The gaps are the ones the fixture is
            //built around: next door is six tiles away, the plot across the street twenty, and the
            //bulldozed plot below next door twenty-eight - so the one that survives is next door,
            //and the two deferred are in nearest-first order among themselves.
            var one = Plan(map, layout, SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles, 1);
            Assert.Equal(new short[] { Near }, one.NeighbourNumbers);
            Assert.Equal(new short[] { Near, Far, Empty }, one.SelectionOrder);
            Assert.Equal(new short[] { Far, Empty }, one.DeferredNumbers);
            Assert.Equal(6, SimitoneSurroundingsPlan.SubWorldGapFor(
                layout.FindLot(Active), layout.FindLot(Near)));
            Assert.Equal(20, SimitoneSurroundingsPlan.SubWorldGapFor(
                layout.FindLot(Active), layout.FindLot(Far)));
        }

        [Fact]
        public void TheBudgetOrdersByNearestGapWithTheLotNumberAsTheTieBreak()
        {
            // The ordering rule as arithmetic over two lots, independent of any layout: the gap
            // between the rectangles is the measure, so it is the Chebyshev one the radius already
            // thresholds and not a centre distance, and the lot number is what settles two plots at
            // the same distance.
            var active = Lot(Active, 0, 0, 64);

            Assert.Equal(0, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(1, 64, 0, 64)));
            Assert.Equal(0, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(2, 0, 0, 64)));
            Assert.Equal(0, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(3, 32, 32, 64)));
            Assert.Equal(20, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(4, 84, 0, 64)));
            Assert.Equal(20, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(5, 0, 84, 64)));
            //diagonally out on both axes is the larger of the two, never the sum
            Assert.Equal(20, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(6, 84, 84, 64)));
            //no lot at all is furthest away there is
            Assert.Equal(int.MaxValue, SimitoneSurroundingsPlan.SubWorldGapFor(active, null));
            Assert.Equal(int.MaxValue, SimitoneSurroundingsPlan.SubWorldGapFor(null, active));

            //and the gap is what WithinRadius thresholds, so the budget's order and the radius
            //cannot disagree about which lot is "nearer"
            Assert.True(SimitoneSurroundingsPlan.WithinRadius(active, Lot(7, 84, 0, 64), 20));
            Assert.False(SimitoneSurroundingsPlan.WithinRadius(active, Lot(8, 84, 0, 64), 19));
            Assert.Equal(SimitoneSurroundingsPlan.WithinRadius(active, Lot(9, 84, 0, 64), 20),
                SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(10, 84, 0, 64)) <= 20);
        }

        [Fact]
        public void EveryNeighbourAPlanBuildsIsInsideTheDrawnRegion()
        {
            // The invariant as a fact about a real plan and a real region rather than as two
            // constants happening to satisfy a comparison. A plan whose neighbours reached past
            // the crop would open a gap in the ground rather than close a double draw.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var plan = Plan(map, layout, SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles);

            Assert.True(plan.IsValid);
            Assert.NotEmpty(plan.Neighbours);
            Assert.True(SimitoneSurroundingsPlan.RadiusFitsCut(plan.RadiusTiles, cull.RadiusTiles));
            Assert.True(plan.SubWorldsInside(cull));
            Assert.True(NeighbourhoodRenderDecision.SubWorldsAreInsideCut(plan, cull));

            //No plan, no cull: nothing to disagree about.
            Assert.True(NeighbourhoodRenderDecision.SubWorldsAreInsideCut(null, cull));
            Assert.True(NeighbourhoodRenderDecision.SubWorldsAreInsideCut(plan, null));
        }

        // ==================================================================== one decision, two halves

        [Fact]
        public void TheDecisionAndTheFacadeQueueNeverBothSayYesToALot()
        {
            // The facade cache asks ShouldDraw; the surround asks Decide. Given the same plan and
            // the same region they have to agree about every lot in the layout, and that is the
            // only statement that rules out a double draw.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var radius = SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles;

            //the queue is the cull's own layout, which is what SimitoneSurroundings hands the cache
            var queued = cull.LotsInRange(layout, Active);

            //A budget of one, because that is the only way this fixture produces a facade now: at
            //the shipped budget and the drawn region's own radius, all three of its lots are real -
            //next door, the one across the street and the bulldozed plot - and a test of "the two
            //halves never both say yes" needs a lot that is a facade to say yes to.
            var plan = Plan(map, layout, radius, 1);
            var withheld = new List<short>(plan.NeighbourNumbers);

            Assert.Contains<short>(Near, withheld);
            Assert.DoesNotContain<short>(Far, withheld);
            Assert.DoesNotContain<short>(Empty, withheld);

            var subWorlds = 0;
            var facades = 0;
            for (int i = 0; i < queued.Count; i++)
            {
                var lot = queued[i];
                var render = NeighbourhoodRenderDecision.Decide(lot, Active, plan, cull);
                var drawsFacade = NeighbourhoodFacadePlan.ShouldDraw(lot, Active, withheld);
                Assert.Equal(NeighbourhoodRenderDecision.IsFacade(render), drawsFacade);
                if (NeighbourhoodRenderDecision.IsSubWorld(render)) subWorlds++;
                if (NeighbourhoodRenderDecision.IsFacade(render)) facades++;
            }

            //next door is a real lot and the plot across the street is a facade - which is the only
            //arrangement a facade can be in now, and it is the budget saying so rather than a radius
            //reaching less far than the drawn ground. The bulldozed plot is the third lot and is
            //neither: it has no mesh to build, so deferring it costs nothing to look at.
            Assert.Equal(1, subWorlds);
            Assert.Equal(1, facades);
            Assert.Equal(plan.DeferredCount, facades + 1);

            //and at the shipped budget the same fixture has no facades at all, which is the
            //coverage claim this radius change exists for
            var full = Plan(map, layout, radius);
            Assert.Equal(3, full.Neighbours.Count);
            Assert.Equal(0, full.DeferredCount);
            Assert.Equal(0, NeighbourhoodRenderDecision.FacadeFallbackCount(layout, Active, full, cull));

            //and the active lot is in neither count, even though it is inside the region
            Assert.Equal(NeighbourhoodLotRender.None,
                NeighbourhoodRenderDecision.Decide(layout.FindLot(Active), Active, plan, cull));
        }

        [Fact]
        public void WithNoPlanEveryLotInTheRegionIsAFacadeExactlyAsBefore()
        {
            // The capability-off behaviour, unchanged: a null plan and an empty withheld set say
            // the same thing, so the pass builds and draws what it always did.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var queued = cull.LotsInRange(layout, Active);

            Assert.Equal(3, queued.Count);
            for (int i = 0; i < queued.Count; i++)
            {
                var lot = queued[i];
                //the bulldozed plot is the one exception, and it is the exception Part A already
                //had: a lot with no house file has no mesh to build
                var draws = NeighbourhoodFacadePlan.ShouldDraw(lot, Active);
                Assert.Equal(lot.HasHouse, draws);
                Assert.Equal(draws, NeighbourhoodFacadePlan.ShouldDraw(lot, Active, null));
                Assert.Equal(draws, NeighbourhoodFacadePlan.ShouldDraw(lot, Active, new short[0]));
                Assert.Equal(draws ? NeighbourhoodLotRender.Facade : NeighbourhoodLotRender.None,
                    NeighbourhoodRenderDecision.Decide(
                        lot, Active, (SimitoneSurroundingsPlan)null, cull));
            }
        }

        [Fact]
        public void TheQueueAndTheProgressLineBothDropWithheldLots()
        {
            // "Still to build" must not count a lot that is deliberately not being built, or the
            // surround drains and then sits saying "1 still to build" for ever.
            var layout = StreetLayout();
            var withheld = new HashSet<short> { Near, Empty };
            var cache = new Dictionary<short, uint>();

            //two lots have houses on them: the active one is the lot view's, the empty plot is
            //not drawable, and the other two are the neighbours
            Assert.Equal(2, NeighbourhoodFacadePlan.Eligible(layout, Active));
            Assert.Equal(1, NeighbourhoodFacadePlan.Eligible(layout, Active, withheld));

            var wanted = NeighbourhoodFacadePlan.ToBuild(layout, cache, Active, Vector2.Zero, 100);
            Assert.Equal(2, wanted.Count);
            Assert.Single(NeighbourhoodFacadePlan.ToBuild(
                layout, cache, Active, Vector2.Zero, 100, 0f, withheld));

            int outstanding, eligible;
            NeighbourhoodFacadePlan.Progress(layout, cache, Active, Vector2.Zero,
                out outstanding, out eligible, withheld);
            Assert.Equal(1, outstanding);
            Assert.Equal(1, eligible);
        }

        [Fact]
        public void AnInvalidPlanSubWorldsNothing()
        {
            // A plan that could not be built is not evidence that any lot is real. Reading one as
            // "no sub-worlds" is what stops a failed plan from blanking the neighbourhood.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var invalid = SimitoneSurroundingsPlan.ForQuery(
                PlacementFor(layout, map), map, null, null, null,
                SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles);

            Assert.False(invalid.IsValid);
            Assert.False(NeighbourhoodRenderDecision.IsSubWorld(invalid, Near));
            Assert.True(NeighbourhoodRenderDecision.SubWorldsAreInsideCut(invalid, cull));
            Assert.Equal(NeighbourhoodLotRender.Facade,
                NeighbourhoodRenderDecision.Decide(layout.FindLot(Near), Active, invalid, cull));
        }
    }
}