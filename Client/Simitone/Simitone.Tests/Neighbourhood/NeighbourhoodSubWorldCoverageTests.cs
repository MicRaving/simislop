/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The drawn region is all real: every lot the surround draws ground for is a sub-world, the
    /// only thing that can stop that is the sub-world budget, and what the budget cuts is the
    /// nearest-first tail of the candidates and is drawn as a facade rather than dropped.
    ///
    /// <para>
    /// <b>What this replaces.</b> The lot view asked for a plan at eight tiles of gap while the
    /// surround drew forty, so a player standing in a middle lot saw two or three real houses, a
    /// ring of facades, and then real-looking ground with nothing on it. The two numbers answered
    /// two different questions and the user-facing answer was "some of these houses aren't real".
    /// <see cref="SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles"/> is the drawn region's own
    /// radius now, and these tests are what stops that going back.
    /// </para>
    ///
    /// <para>
    /// <b>Why a hand-written fixture and not a generated one.</b> The decisions here are arithmetic
    /// - a radius, a count, an ordering - and a fixture whose gaps are written down by hand is the
    /// only one where "the lot next door is six tiles away and the one across the street is twenty"
    /// is a fact rather than an inference from a packer. The generated-layout measurement that says
    /// a real packed neighbourhood never reaches the budget is
    /// <c>NeighbourhoodScaleAndNeighbourTests.TheShippedBudgetIsNeverReachedByAPackedNeighbourhood</c>,
    /// over the packer's own pitch; and the whole decision over a real generated neighbourhood and
    /// a real drawn region is
    /// <c>PartBIntegrationTests.TheDrawnRegionIsFullyRealAndOnlyTheBudgetEverMakesItNot</c>.
    /// </para>
    ///
    /// <para>
    /// <b>Every assertion here is pure arithmetic over a layout, a map and a plan.</b> No graphics
    /// device, no <c>VM</c>, no lot store - which is why a coverage rule about what the player sees
    /// can be tested at all rather than only looked for on screen.
    /// </para>
    /// </summary>
    public class NeighbourhoodSubWorldCoverageTests
    {
        /// <summary>The fixture's map, which is small because the decisions are size independent.</summary>
        private const int Size = 96;

        /// <summary>The row the fixture's single street runs along.</summary>
        private const int StreetY = 36;

        /// <summary>Every lot in the fixture is this size, a quarter of a TS1 lot.</summary>
        private const int LotSize = 16;

        /// <summary>The lot the player is standing in, at (4, 4).</summary>
        private const short Active = 11;

        /// <summary>Next door: six tiles of gap, so the nearest lot and the first the budget takes.</summary>
        private const short Near = 12;

        /// <summary>Across the street: twenty tiles of gap, inside the radius and inside the region.</summary>
        private const short Far = 13;

        /// <summary>Below next door, twenty-eight tiles away, with nothing built on it.</summary>
        private const short Empty = 14;

        /// <summary>
        /// The gap between the active lot and <see cref="Near"/>, written down rather than derived,
        /// because it is a number the nearest-first order is asserted against.
        /// </summary>
        private const int NearGap = 6;

        /// <summary>The gap between the active lot and <see cref="Far"/>.</summary>
        private const int FarGap = 20;

        /// <summary>The gap between the active lot and <see cref="Empty"/>.</summary>
        private const int EmptyGap = 28;

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

        /// <summary>
        /// Lot 11 at (4, 4) is the player's. Lot 12 is six tiles below it, lot 13 is twenty across,
        /// and lot 14 is a bulldozed plot twenty-eight below. All three are inside the drawn region
        /// and all three are inside the plan's radius, because the radius is the region's own.
        /// </summary>
        private static NeighbourhoodLayout StreetLayout()
        {
            return new NeighbourhoodLayout(4, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                Size, new[]
                {
                    Lot(Active, 4, 4),
                    Lot(Near, 4, 26),
                    Lot(Far, 40, 4),
                    Lot(Empty, 4, 48, LotSize, false)
                });
        }

        /// <summary>
        /// Doors on the street, stated rather than derived: what is under test is the decision made
        /// from a plan, not how the road graph arrives at one.
        /// </summary>
        private static NeighbourhoodQuery StreetQuery(
            NeighbourhoodLayout layout, NeighbourhoodMap map)
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

        /// <summary>The plan for the fixture at the radius under test and the shipped budget.</summary>
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

        /// <summary>
        /// The lots the drawn region covers, other than the one the player is standing in - which is
        /// exactly the set <c>SimitoneSurroundings.NearLotsLayout</c> hands the facade cache.
        /// </summary>
        private static IReadOnlyList<NeighbourhoodLot> Region(
            NeighbourhoodLayout layout, SimitoneSurroundingsCull cull)
        {
            return cull.LotsInRange(layout, Active);
        }

        /// <summary>The radius the lot view now builds its plan at: the drawn region's own.</summary>
        private static int SharedRadius
        {
            get { return SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles; }
        }

        // ================================================================ the shared radius

        [Fact]
        public void TheSubWorldRadiusIsTheDrawnRegionsOwnRadius()
        {
            // The coverage claim as a fact about two numbers rather than as a loop over a layout.
            // Forty tiles is how much *ground* is worth drawing and it is nearly free; eight used to
            // be how many *lots* were worth standing up, which left a ring of facades between the
            // real houses and the drawn ground. The two questions turned out to have the same
            // answer, so there is one number now - and what bounds the count is the budget.
            Assert.Equal(SimitoneSurroundingsPlacement.CullRadiusTiles,
                SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles);

            Assert.True(SimitoneSurroundingsPlan.RadiusIsShared(
                SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles,
                SimitoneSurroundingsPlacement.CullRadiusTiles));
            Assert.True(SimitoneSurroundingsPlan.RadiusIsShared(40, 40));
            Assert.True(SimitoneSurroundingsPlan.RadiusIsShared(-4, 0));
            Assert.False(SimitoneSurroundingsPlan.RadiusIsShared(8, 40),
                "eight against forty is the shape this change ends: a real centre ringed by facades");
            Assert.False(SimitoneSurroundingsPlan.RadiusIsShared(41, 40));

            //and for a real plan and a real region together, which is the form the screen and the
            //surround actually ask it in
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var plan = Plan(map, layout, SharedRadius);

            Assert.True(plan.RadiusSharedWith(cull));
            Assert.True(NeighbourhoodRenderDecision.RadiiAreShared(plan, cull));
            Assert.False(NeighbourhoodRenderDecision.RadiiAreShared(Plan(map, layout, 8), cull));

            //No cut is not "shared" - it is "nothing to share it with", and the caller that has no
            //region is the map view, which has no sub-worlds to be inconsistent about.
            Assert.False(NeighbourhoodRenderDecision.RadiiAreShared(plan, null));
            Assert.False(NeighbourhoodRenderDecision.RadiiAreShared(null, cull));

            //The one-sided rule still holds too, and is still worth keeping: a caller may plan a
            //deliberately narrow ring of real lots, and no real lot may ever stand outside the drawn
            //region, because a lot that did would stand on ground the surround never drew.
            Assert.True(SimitoneSurroundingsPlan.RadiusFitsCut(8, 40));
            Assert.True(SimitoneSurroundingsPlan.RadiusFitsCut(40, 40));
            Assert.False(SimitoneSurroundingsPlan.RadiusFitsCut(41, 40));
            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                Assert.True(cull.CoversLot(plan.Neighbours[i].Lot),
                    "lot " + plan.Neighbours[i].Lot.Number
                    + " is a real sub-world standing outside the drawn region");
            }
        }

        [Fact]
        public void EveryLotInsideTheDrawnRegionIsARealSubWorldAndNoFacadeIsLeftInIt()
        {
            // The whole of the change, over a whole region. Not "the near ones are real and the far
            // ones are facades" - every lot the surround draws ground for is a real lot, and there is
            // no band of facades between them and the edge of the ground.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var region = Region(layout, cull);
            Assert.Equal(3, region.Count);

            var plan = Plan(map, layout, SharedRadius);
            Assert.True(plan.IsValid, plan.Failure);
            Assert.Equal(3, plan.Neighbours.Count);
            Assert.Equal(0, plan.DeferredCount);
            Assert.True(plan.IsFullyCovered);
            Assert.Equal(SimitoneSurroundingsPlan.MaxSubWorlds, plan.SubWorldBudget);
            Assert.Equal(SharedRadius, plan.RadiusTiles);

            //Every lot in the region, the bulldozed plot included, is a real sub-world. A plot with
            //no house can be one: a sub-world is a whole lot with its own ground, and a bare plot
            //still has ground.
            for (int i = 0; i < region.Count; i++)
            {
                var lot = region[i];
                Assert.True(plan.Contains(lot.Number), lot.Number.ToString());
                Assert.True(NeighbourhoodRenderDecision.IsSubWorld(
                        NeighbourhoodRenderDecision.Decide(lot, Active, plan, cull)),
                    "lot " + lot.Number + " is inside the drawn region but is not a real lot");
                //and it is not also a facade, which is the double draw the decision exists to stop
                Assert.False(NeighbourhoodFacadePlan.ShouldDraw(lot, Active, plan.NeighbourNumbers),
                    "lot " + lot.Number + " is a real sub-world and is still a facade candidate");
            }

            //and no lot in the whole layout is left as a facade, which is the count a player means
            //by "some of these houses aren't real"
            Assert.Equal(0, NeighbourhoodRenderDecision.FacadeFallbackCount(
                layout, Active, plan, cull));
            Assert.True(NeighbourhoodRenderDecision.RegionIsFullyReal(layout, Active, plan, cull));

            //The player's own lot is in the region and is the one lot drawn by neither - it is
            //inside the cut and excluded from the region's lot list by the same rule the facade
            //queue uses, because the lot view draws it in full from its own house file.
            Assert.Equal(NeighbourhoodLotRender.None, NeighbourhoodRenderDecision.Decide(
                layout.FindLot(Active), Active, plan, cull));
            //the region's own list leaves it out, and the list without an exclusion does not - which
            //is the exclusion the facade queue makes and the reason the player's own lot is never
            //built as a facade or stood up as a sub-world
            Assert.Equal(4, cull.LotsInRange(layout).Count);
            Assert.Contains(layout.FindLot(Active), cull.LotsInRange(layout));
        }

        // ================================================================ the budget

        [Fact]
        public void AnOverBudgetPlanTakesTheNearestLotsAndDefersTheRestToFacades()
        {
            // The budget asked as behaviour rather than as prose. A lot the budget could not take is
            // not dropped: it is inside the drawn region, so the render decision still draws it, as a
            // facade. The budget therefore costs flatness at the far edge and never a hole in the
            // street, which is the only property that makes a hard budget safe to ship.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var region = Region(layout, cull);
            var withHouses = 0;
            for (int i = 0; i < region.Count; i++)
            {
                if (region[i].HasHouse) withHouses++;
            }
            //two of the three have a house; the third is the bulldozed plot, which is the one lot in
            //the region that draws nothing whichever way the budget went
            Assert.Equal(2, withHouses);

            foreach (var budget in new[] { 0, 1, 2, 3, 4 })
            {
                var trimmed = Plan(map, layout, SharedRadius, budget);
                Assert.True(trimmed.IsValid, trimmed.Failure);
                Assert.Equal(budget < 0 ? 0 : budget, trimmed.SubWorldBudget);
                Assert.Equal(Math.Min(budget, region.Count), trimmed.Neighbours.Count);
                Assert.Equal(region.Count - Math.Min(budget, region.Count), trimmed.DeferredCount);
                Assert.Equal(trimmed.DeferredCount == 0, trimmed.IsFullyCovered);

                var subWorlds = 0;
                var facades = 0;
                for (int i = 0; i < region.Count; i++)
                {
                    var lot = region[i];
                    var render = NeighbourhoodRenderDecision.Decide(lot, Active, trimmed, cull);

                    //never both: one decision per lot, or the same house is drawn twice
                    Assert.False(NeighbourhoodRenderDecision.IsFacade(render)
                            && NeighbourhoodRenderDecision.IsSubWorld(render));
                    //and never neither, for a lot with a house on it. A bulldozed plot is the one
                    //exception, and that is Part A's rule rather than this one: it has no mesh to
                    //build, though it can still be a real sub-world, which it is at budget 3.
                    if (lot.HasHouse) Assert.NotEqual(NeighbourhoodLotRender.None, render);

                    if (NeighbourhoodRenderDecision.IsSubWorld(render))
                    {
                        subWorlds++;
                        Assert.True(trimmed.Contains(lot.Number), lot.Number.ToString());
                    }
                    if (NeighbourhoodRenderDecision.IsFacade(render))
                    {
                        facades++;
                        //A facade is always a lot the budget deferred - never one the radius
                        //excluded, because the radius is the region's own.
                        Assert.Contains(lot.Number, trimmed.DeferredNumbers);
                        Assert.False(trimmed.Contains(lot.Number));
                    }
                }

                Assert.Equal(trimmed.Neighbours.Count, subWorlds);
                //The deferred count includes the bulldozed plot, which draws nothing whichever way
                //it was deferred, so the facades are the deferred lots with a house on them.
                Assert.Equal(facades, NeighbourhoodRenderDecision.FacadeFallbackCount(
                    layout, Active, trimmed, cull));
                Assert.Equal(facades == 0,
                    NeighbourhoodRenderDecision.RegionIsFullyReal(layout, Active, trimmed, cull));
                //The facades are exactly the deferred lots that have a house on them - a bulldozed
                //plot draws nothing whichever way the budget went, so deferring it costs nothing to
                //look at. Which lots those are depends on the budget, because the nearest-first
                //order puts the bare plot last and a budget that stops one short of the end cuts
                //it rather than a house.
                var deferredWithHouse = 0;
                for (int i = 0; i < trimmed.DeferredNumbers.Count; i++)
                {
                    if (layout.FindLot(trimmed.DeferredNumbers[i]).HasHouse) deferredWithHouse++;
                }
                Assert.Equal(deferredWithHouse, facades);
                Assert.Equal(deferredWithHouse == 0,
                    NeighbourhoodRenderDecision.RegionIsFullyReal(layout, Active, trimmed, cull));
                //and a budget that reaches every lot in the region has deferred nothing at all
                Assert.Equal(budget >= region.Count, trimmed.DeferredCount == 0);
            }
        }

        [Fact]
        public void SelectionIsNearestFirstAndDeferredLotsAreItsTail()
        {
            // Which lot survives a full budget, stated as a fact about a plan. Nearest first by the
            // same gap the radius thresholds - so the two cannot disagree about what "nearer" means -
            // and the lot number breaks a tie, so the same lot is deferred on every visit and after
            // any re-entry to the neighbourhood.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var full = Plan(map, layout, SharedRadius);

            //the gaps are the ones the fixture is built around, asserted rather than assumed
            Assert.Equal(NearGap, SimitoneSurroundingsPlan.SubWorldGapFor(
                layout.FindLot(Active), layout.FindLot(Near)));
            Assert.Equal(FarGap, SimitoneSurroundingsPlan.SubWorldGapFor(
                layout.FindLot(Active), layout.FindLot(Far)));
            Assert.Equal(EmptyGap, SimitoneSurroundingsPlan.SubWorldGapFor(
                layout.FindLot(Active), layout.FindLot(Empty)));

            //so the whole order is next door, then across the street, then the far plot - and the
            //plan's own list is ascending lot number, which is the layout's order and what
            //everything downstream of the plan reads
            Assert.Equal(new short[] { Near, Far, Empty }, full.SelectionOrder);
            Assert.Equal(new short[] { Near, Far, Empty }, full.NeighbourNumbers);

            //a budget of one takes the nearest and defers the rest, in nearest-first order among
            //themselves - not "whichever happened to be last in the input"
            var one = Plan(map, layout, SharedRadius, 1);
            Assert.Equal(new short[] { Near }, one.NeighbourNumbers);
            Assert.Equal(new short[] { Far, Empty }, one.DeferredNumbers);
            //and the selection order is the same over the same inputs whatever the budget, because
            //it records the candidates rather than the outcome
            Assert.Equal(full.SelectionOrder, one.SelectionOrder);

            //the order is monotonic in the gap, with the lot number as the tie-break, over the whole
            //of it - which is the property a prefix cut depends on
            for (int i = 1; i < full.SelectionOrder.Count; i++)
            {
                var previous = layout.FindLot(full.SelectionOrder[i - 1]);
                var current = layout.FindLot(full.SelectionOrder[i]);
                var previousGap = SimitoneSurroundingsPlan.SubWorldGapFor(
                    layout.FindLot(Active), previous);
                var currentGap = SimitoneSurroundingsPlan.SubWorldGapFor(
                    layout.FindLot(Active), current);
                Assert.True(previousGap < currentGap
                        || (previousGap == currentGap && previous.Number < current.Number),
                    "lot " + current.Number + " is nearer than lot " + previous.Number
                    + " but comes after it in the selection order");
            }

            //and a budget of everything gives the untrimmed plan back, while the same budget twice
            //gives the same street
            Assert.Equal(full.NeighbourNumbers,
                Plan(map, layout, SharedRadius, full.Neighbours.Count).NeighbourNumbers);
            Assert.Equal(one.NeighbourNumbers, Plan(map, layout, SharedRadius, 1).NeighbourNumbers);
            Assert.Equal(one.DeferredNumbers, Plan(map, layout, SharedRadius, 1).DeferredNumbers);

            //the radius holds every lot in the region whatever the budget, which is the difference
            //between the budget being a count and being a second radius
            for (int i = 0; i < full.SelectionOrder.Count; i++)
            {
                var lot = layout.FindLot(full.SelectionOrder[i]);
                Assert.True(cull.CoversLot(lot), lot.Number.ToString());
                Assert.True(SimitoneSurroundingsPlan.WithinRadius(
                    layout.FindLot(Active), lot, SharedRadius), lot.Number.ToString());
            }
        }

        [Fact]
        public void TheBudgetOrdersByGapBetweenRectanglesWithTheLotNumberAsTheTieBreak()
        {
            // The ordering rule as arithmetic over two lots, independent of any layout or plan. The
            // gap is the Chebyshev one - the larger of the two axis gaps, never the sum - because that
            // is the measure <c>WithinRadius</c> already thresholds, and a budget ordered by one
            // measure while a radius cuts by another is how the two would come to disagree about
            // which lot is nearer.
            var active = Lot(Active, 0, 0, 64);

            Assert.Equal(0, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(1, 64, 0, 64)));
            Assert.Equal(0, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(2, 0, 0, 64)));
            Assert.Equal(0, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(3, 32, 32, 64)));
            Assert.Equal(20, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(4, 84, 0, 64)));
            Assert.Equal(20, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(5, 0, 84, 64)));
            //diagonally out on both axes is the larger of the two, not the sum
            Assert.Equal(20, SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(6, 84, 84, 64)));
            //no lot at all is the furthest away there is
            Assert.Equal(int.MaxValue, SimitoneSurroundingsPlan.SubWorldGapFor(active, null));
            Assert.Equal(int.MaxValue, SimitoneSurroundingsPlan.SubWorldGapFor(null, active));

            //and the gap is exactly what WithinRadius thresholds, in both directions
            Assert.True(SimitoneSurroundingsPlan.WithinRadius(active, Lot(7, 84, 0, 64), 20));
            Assert.False(SimitoneSurroundingsPlan.WithinRadius(active, Lot(8, 84, 0, 64), 19));
            for (int radius = 0; radius <= 24; radius++)
            {
                Assert.Equal(
                    SimitoneSurroundingsPlan.SubWorldGapFor(active, Lot(9, 84, 0, 64)) <= radius,
                    SimitoneSurroundingsPlan.WithinRadius(active, Lot(10, 84, 0, 64), radius));
            }
        }

        [Fact]
        public void AnUnreachableHouseIsStillPlannedButABulldozedPlotIsNot()
        {
            // The route is a preference, not a permission, and the budget is not a way round either
            // rule. Only the active lot and its next-door neighbour have a door on the street, so the
            // road graph reaches only Near - but Far is inside the radius and has a house, so it is a
            // real neighbour all the same. The bulldozed plot Empty is not: it has no house and no
            // door, and the facade side draws nothing for it either, so it is neither a sub-world nor
            // a facade. This is the rule that keeps "the whole region is real" from becoming
            // reachable by standing up a lot the player could not walk to while still not dropping a
            // real house merely because the generator did not join its driveway up.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var placement = PlacementFor(layout, map);

            //only the active lot and its next-door neighbour have a door
            var query = new NeighbourhoodQuery(layout, map, new[]
            {
                new NeighbourhoodEntrance(Active, 12, StreetY, 0, -1),
                new NeighbourhoodEntrance(Near, 12, StreetY, 0, -1)
            });
            var plan = SimitoneSurroundingsPlan.ForQuery(placement, map, query, layout, null,
                SharedRadius, SimitoneSurroundingsPlan.MaxSubWorlds);

            Assert.True(plan.IsValid, plan.Failure);
            Assert.Equal(new short[] { Near, Far }, plan.NeighbourNumbers);
            Assert.DoesNotContain(Empty, plan.NeighbourNumbers);
            Assert.Equal(0, plan.DeferredCount);
            Assert.True(plan.IsFullyCovered);
            //and the route was available and is still recorded as the source of the preference
            Assert.True(plan.RoadReachability);
        }

        // ================================================================ capability off

        [Fact]
        public void WithNoPlanEveryLotInTheRegionIsAFacadeExactlyAsBefore()
        {
            // The capability-off behaviour, what it was before this feature. A null plan and an empty
            // withheld set say the same thing, so the facade pass builds and draws what it always
            //did - and the shared radius changes nothing about that, because the radius is read off
            // the plan and a plan with no sub-worlds names none.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var region = Region(layout, cull);
            Assert.Equal(3, region.Count);

            var facades = 0;
            for (int i = 0; i < region.Count; i++)
            {
                var lot = region[i];
                Assert.Equal(lot.HasHouse, NeighbourhoodFacadePlan.ShouldDraw(lot, Active));
                Assert.Equal(lot.HasHouse, NeighbourhoodFacadePlan.ShouldDraw(lot, Active, null));
                Assert.Equal(lot.HasHouse,
                    NeighbourhoodFacadePlan.ShouldDraw(lot, Active, new short[0]));
                Assert.Equal(
                    lot.HasHouse ? NeighbourhoodLotRender.Facade : NeighbourhoodLotRender.None,
                    NeighbourhoodRenderDecision.Decide(
                        lot, Active, (SimitoneSurroundingsPlan)null, cull));
                if (lot.HasHouse) facades++;
            }

            Assert.Equal(2, facades);
            Assert.Equal(facades, NeighbourhoodFacadePlan.Eligible(layout, Active));
            Assert.Equal(facades, NeighbourhoodFacadePlan.Eligible(layout, Active, null));
            Assert.Equal(facades, NeighbourhoodRenderDecision.FacadeFallbackCount(
                layout, Active, null, cull));
            Assert.False(NeighbourhoodRenderDecision.RegionIsFullyReal(layout, Active, null, cull));
        }

        [Fact]
        public void ABudgetOfZeroDrawsTheSameStreetAsHavingNoPlan()
        {
            // Turning the switch off is a null plan; setting the budget to zero is a plan that names
            // no lots. The two must draw the same street, or "no sub-worlds" would mean two different
            // things depending on which one the caller reached for.
            var map = NeighbourMap();
            var layout = StreetLayout();
            var cull = PlacementFor(layout, map).Cull;
            var region = Region(layout, cull);
            var none = Plan(map, layout, SharedRadius, 0);

            Assert.True(none.IsValid, none.Failure);
            Assert.Empty(none.Neighbours);
            Assert.Equal(region.Count, none.DeferredCount);
            Assert.False(none.IsFullyCovered);
            //a negative budget is zero, not an error and not an unlimited pass
            var negative = Plan(map, layout, SharedRadius, -4);
            Assert.Equal(0, negative.SubWorldBudget);
            Assert.Empty(negative.Neighbours);
            Assert.Equal(region.Count, negative.DeferredCount);

            //a plan that names nothing withholds nothing, so the facade queue is byte for byte the
            //one it had before B2 existed
            var withPlan = 0;
            var withoutPlan = 0;
            for (int i = 0; i < region.Count; i++)
            {
                var lot = region[i];
                Assert.Equal(lot.HasHouse,
                    NeighbourhoodFacadePlan.ShouldDraw(lot, Active, none.NeighbourNumbers));
                if (NeighbourhoodFacadePlan.ShouldDraw(lot, Active, none.NeighbourNumbers)) withPlan++;
                if (NeighbourhoodFacadePlan.ShouldDraw(lot, Active)) withoutPlan++;
            }
            Assert.Equal(withoutPlan, withPlan);
            Assert.Equal(withoutPlan, NeighbourhoodRenderDecision.FacadeFallbackCount(
                layout, Active, none, cull));
            Assert.Equal(withoutPlan, NeighbourhoodFacadePlan.Eligible(
                layout, Active, none.NeighbourNumbers));
        }
    }
}
