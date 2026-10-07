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
    /// §6 B4's transit lot, tested on its own: the route between two doors, how far along it the
    /// avatar is, and the exactly-one-active-lot guard.
    ///
    /// <para>
    /// <b>The fixture is hand-written, for the same reason
    /// <see cref="NeighbourhoodQueryTests"/> writes its own.</b> The thing under test is "given a
    /// map with a road network and a table of doors, is there a walk between these two, and is it
    /// the walk I expect". A generated neighbourhood answers that with a route nobody can check by
    /// eye, and a bug that rerouted around one block would pass. So there are two small maps here,
    /// both readable on paper:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>The block</b>, 24x24, one road column down <c>x = 12</c> crossed by one road row
    ///     across <c>y = 12</c>. Six 4x4 lots sit in the quadrants with their doors on the two
    ///     arms, so a route between two of them is a straight run of named tiles or one turn at the
    ///     junction, and its length is arithmetic rather than a diff of two coordinate dumps.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The islands</b>, 16x16, two road stubs that touch nothing: a column at <c>x = 8</c>
    ///     for <c>y 0..5</c> and one at <c>x = 4</c> for <c>y 10..15</c>, each with a lot whose
    ///     door is on it. The point of it is the negative case: two doors that are perfectly real
    ///     and perfectly unreachable from each other, which is the only way "no path" gets tested
    ///     as a fact rather than as an absence.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The road nibbles are written the way <c>RoadGraph.WriteMasks</c> writes them - a tile's
    /// mask agrees with its neighbours' occupancy - because <see cref="NeighbourhoodQuery"/>
    /// reads road out of the low nibble and a fixture whose masks disagree with its own shape
    /// would be testing a different map than the one it claims to be.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodTransitLotTests
    {
        // ---- the block ----------------------------------------------------------------------

        private const int BlockSize = 24;
        private const int BlockColumn = 12;
        private const int BlockRow = 12;

        // The four lots on the column, and the two on the row. Doors are one tile out from the lot
        // on the side facing the road, at a y inside the lot's own extent - the same shape
        // RoadGraph's anchors have.
        private const int NorthWestLot = 11;   // (8,4), door east at (12,6)
        private const int SouthWestLot = 12;   // (8,16), door east at (12,18)
        private const int NorthEastLot = 13;   // (13,1), door west at (12,3)
        private const int SouthEastLot = 14;   // (13,18), door west at (12,19)
        private const int WestRowLot = 15;     // (4,13), door north at (5,12)
        private const int Plot = 16;           // (1,1), bulldozed, no door

        private const int NorthWestDoorX = BlockColumn;
        private const int NorthWestDoorY = 6;
        private const int WestRowDoorX = 5;
        private const int WestRowDoorY = BlockRow;

        // ---- the islands --------------------------------------------------------------------

        private const int IslandSize = 16;
        private const int EastIslandLot = 21;
        private const int WestIslandLot = 22;

        // ---- fixtures ------------------------------------------------------------------------

        private static bool InBounds(int x, int y, int size)
        {
            return x >= 0 && y >= 0 && x < size && y < size;
        }

        /// <summary>
        /// The road mask writer, copied in shape from <see cref="NeighbourhoodQueryTests"/>: a
        /// tile's four bits are its road neighbours' occupancy, and a turn or a junction also
        /// gets the same mask in the high nibble.
        /// </summary>
        private static NeighbourhoodMap RoadMap(int size, Func<int, int, bool> isRoad)
        {
            var map = NeighbourhoodMap.Create(size);
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    if (!isRoad(x, y)) continue;
                    byte mask = RoadMask.None;
                    if (isRoad(x + 1, y)) mask |= RoadMask.East;
                    if (isRoad(x, y + 1)) mask |= RoadMask.South;
                    if (isRoad(x - 1, y)) mask |= RoadMask.West;
                    if (isRoad(x, y - 1)) mask |= RoadMask.North;
                    map.RoadData[map.Index(x, y)] = RoadMask.IsTurnOrJunction(mask)
                        ? (byte)(mask | (mask << 4))
                        : mask;
                }
            }
            return map;
        }

        private static NeighbourhoodLot Lot(short number, int x, int y,
            NeighbourhoodLotSide doorSide, bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Size = 4,
                WorldX = x,
                WorldY = y,
                DoorSide = doorSide,
                HasHouse = hasHouse,
                TerrainType = TerrainType.GRASS,
                Zone = LotZoning.ZoneResidential,
                MapX = number,
                MapY = number,
            };
        }

        private static NeighbourhoodLayout Layout(int size, params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(0, 4242, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                size, lots);
        }

        private static NeighbourhoodEntrance Door(short lot, int x, int y, byte side)
        {
            return new NeighbourhoodEntrance(lot, x, y,
                NeighbourhoodDoorSide.DeltaX(side), NeighbourhoodDoorSide.DeltaY(side), side);
        }

        private static NeighbourhoodQuery Query(NeighbourhoodLayout layout, NeighbourhoodMap map,
            IReadOnlyList<NeighbourhoodEntrance> entrances)
        {
            return new NeighbourhoodQuery(layout, map, entrances);
        }

        /// <summary>The 24x24 block: a road column and a road row, and six lots on their arms.</summary>
        private static NeighbourhoodQuery BlockQuery()
        {
            var map = RoadMap(BlockSize, (x, y) => InBounds(x, y, BlockSize)
                && (x == BlockColumn || y == BlockRow));

            var layout = Layout(BlockSize,
                Lot(NorthWestLot, 8, 4, NeighbourhoodLotSide.East),
                Lot(SouthWestLot, 8, 16, NeighbourhoodLotSide.East),
                Lot(NorthEastLot, 13, 1, NeighbourhoodLotSide.West),
                Lot(SouthEastLot, 13, 18, NeighbourhoodLotSide.West),
                Lot(WestRowLot, 4, 13, NeighbourhoodLotSide.North),
                Lot(Plot, 1, 1, NeighbourhoodLotSide.East, false));

            var entrances = new List<NeighbourhoodEntrance>
            {
                Door(NorthWestLot, 12, 6, NeighbourhoodDoorSide.East),
                Door(SouthWestLot, 12, 18, NeighbourhoodDoorSide.East),
                Door(NorthEastLot, 12, 3, NeighbourhoodDoorSide.West),
                Door(SouthEastLot, 12, 19, NeighbourhoodDoorSide.West),
                Door(WestRowLot, 5, 12, NeighbourhoodDoorSide.North),
            };

            return Query(layout, map, entrances);
        }

        /// <summary>
        /// The 16x16 islands: two road stubs with a door each, touching nothing. Both stubs are
        /// several tiles long so that no tile on them is a lone road tile with a zero mask - a
        /// zero mask is indistinguishable from bare ground, and a stub of one tile would be a
        /// fixture testing that quirk instead of the router.
        /// </summary>
        private static NeighbourhoodQuery IslandsQuery()
        {
            var map = RoadMap(IslandSize, (x, y) => InBounds(x, y, IslandSize)
                && ((x == 8 && y <= 5) || (x == 4 && y >= 10)));

            var layout = Layout(IslandSize,
                Lot(EastIslandLot, 9, 2, NeighbourhoodLotSide.West),
                Lot(WestIslandLot, 5, 10, NeighbourhoodLotSide.West));

            var entrances = new List<NeighbourhoodEntrance>
            {
                Door(EastIslandLot, 8, 3, NeighbourhoodDoorSide.West),
                Door(WestIslandLot, 4, 11, NeighbourhoodDoorSide.West),
            };

            return Query(layout, map, entrances);
        }

        private static NeighbourhoodTransitLot TransitLot(NeighbourhoodQuery query, int activeLot)
        {
            return new NeighbourhoodTransitLot(query, ActiveLotKind.Active, activeLot);
        }

        // ---- the fixture is what it claims to be ---------------------------------------------

        [Fact]
        public void TheBlockFixtureIsOneConnectedNetworkWithFiveDoors()
        {
            //If this fails, every route assertion below is testing a different map than the one
            //the comments above describe, so it is worth pinning on its own.
            var query = BlockQuery();

            Assert.Equal(1, query.RoadComponentCount);
            Assert.Equal(5, query.Entrances.Count);
            Assert.NotNull(query.EntranceFor(NorthWestLot));
            Assert.Null(query.EntranceFor(Plot));

            Assert.True(query.IsRoad(BlockColumn, 0));
            Assert.True(query.IsRoad(0, BlockRow));
            Assert.False(query.IsRoad(0, 0));

            //Every lot with a door is a neighbour of every other: one component means road
            //reachability, and lot 16 is not in the list because a plot has no driveway.
            Assert.Equal(4, query.NeighboursOf(NorthWestLot).Count);
            Assert.Contains(SouthWestLot, query.NeighboursOf(NorthWestLot));
            Assert.Contains(WestRowLot, query.NeighboursOf(NorthWestLot));
        }

        [Fact]
        public void TheIslandsFixtureIsTwoNetworks()
        {
            var query = IslandsQuery();

            Assert.Equal(2, query.RoadComponentCount);
            Assert.Equal(2, query.Entrances.Count);
            Assert.NotEqual(
                query.RoadComponentAt(8, 3),
                query.RoadComponentAt(4, 11));
        }

        // ---- planning: a path exists ---------------------------------------------------------

        [Fact]
        public void APathExistsBetweenAdjacentEntrances()
        {
            //Lots 11 and 12 face each other across the junction from the same arm, so their doors
            //are on one straight run of the column: the shortest possible non-trivial walk, and
            //one whose length is arithmetic.
            var query = BlockQuery();
            var transit = TransitLot(query, NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryPlanPath(NorthWestLot, SouthWestLot, out path, out reason));
            Assert.Null(reason);
            Assert.True(path.IsValid);

            Assert.Equal(NorthWestLot, path.OriginLot);
            Assert.Equal(SouthWestLot, path.DestinationLot);
            Assert.Equal(12, path.Length);
            Assert.Equal(13, path.Waypoints.Count);

            Assert.Equal(new TransitWaypoint(12, 6, NorthWestLot), path.Start);
            Assert.Equal(new TransitWaypoint(12, 18, SouthWestLot), path.End);

            for (int i = 0; i < path.Waypoints.Count; i++)
            {
                var waypoint = path.Waypoints[i];
                Assert.True(query.IsRoad(waypoint.X, waypoint.Y));
                if (i == 0) continue;
                var previous = path.Waypoints[i - 1];
                Assert.Equal(1, Math.Abs(previous.X - waypoint.X) + Math.Abs(previous.Y - waypoint.Y));
            }

            //A straight walk has no corners beyond its two ends, and the lot numbers are on the
            //ends only - the middle of it is street, not anybody's doorstep.
            Assert.Equal(2, path.Corners.Count);
            Assert.All(path.Waypoints, w =>
            {
                if (w != path.Start && w != path.End) Assert.Equal(TransitPath.NoLot, w.LotNumber);
            });
        }

        [Fact]
        public void APathThatTurnsAtAJunctionHasExactlyOneCorner()
        {
            //Lot 15's door is on the row arm west of the junction and lot 11's is on the column
            //north of it, so the walk is 7 tiles east and 6 north with one turn: 13 steps, three
            //corners, and the middle one is the junction tile itself.
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryPlanPath(WestRowLot, NorthWestLot, out path, out reason));

            Assert.Equal(7 + 6, path.Length);
            Assert.Equal(3, path.Corners.Count);
            Assert.Equal(new TransitWaypoint(5, 12, WestRowLot), path.Corners[0]);
            Assert.Equal(new TransitWaypoint(12, 12, TransitPath.NoLot), path.Corners[1]);
            Assert.Equal(new TransitWaypoint(12, 6, NorthWestLot), path.Corners[2]);
        }

        [Fact]
        public void AWalkFromALotToItselfIsAZeroLengthPath()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryPlanPath(NorthWestLot, NorthWestLot, out path, out reason));

            Assert.True(path.IsValid);
            Assert.Equal(0, path.Length);
            Assert.Single(path.Waypoints);
            Assert.Equal(path.Start, path.End);
        }

        [Fact]
        public void PlanningIsDeterministic()
        {
            //B7 resumes a walk from a sidecar, so a pathfinder that re-tied its frontier
            //differently on a second run would move the avatar somewhere the save did not say.
            var query = BlockQuery();

            TransitPath first;
            TransitPath second;
            string reason;
            Assert.True(TransitLot(query, NorthWestLot)
                .TryPlanPath(NorthEastLot, SouthEastLot, out first, out reason));
            Assert.True(TransitLot(query, NorthWestLot)
                .TryPlanPath(NorthEastLot, SouthEastLot, out second, out reason));

            Assert.Equal(first.Waypoints.Count, second.Waypoints.Count);
            for (int i = 0; i < first.Waypoints.Count; i++)
            {
                Assert.Equal(first.Waypoints[i], second.Waypoints[i]);
            }
        }

        [Fact]
        public void PlanningDoesNotStartAWalk()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryPlanPath(NorthWestLot, SouthWestLot, out path, out reason));

            Assert.False(transit.IsWalking);
            Assert.False(transit.State.IsValid);
            Assert.Equal(NorthWestLot, transit.ActiveLotNumber);
            Assert.Equal(ActiveLotKind.Active, transit.Kind);
        }

        // ---- planning: no path ---------------------------------------------------------------

        [Fact]
        public void ThereIsNoPathBetweenTwoDisconnectedLots()
        {
            var query = IslandsQuery();
            var transit = TransitLot(query, EastIslandLot);

            TransitPath path;
            string reason;
            Assert.False(transit.TryPlanPath(EastIslandLot, WestIslandLot, out path, out reason));

            Assert.False(path.IsValid);
            Assert.Equal(TransitPath.None, path);
            Assert.Empty(path.Waypoints);
            Assert.Contains("different road networks", reason);
            Assert.Contains("lot 21", reason);
            Assert.Contains("lot 22", reason);

            //and the refusal left the world exactly as it was
            Assert.False(transit.IsWalking);
            Assert.Equal(EastIslandLot, transit.ActiveLotNumber);
            Assert.False(transit.Advance(1000.0));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(99)]
        [InlineData(-3)]
        public void ALotThatIsNotInTheNeighbourhoodHasNoDoor(int lotNumber)
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.False(transit.TryPlanPath(lotNumber, SouthWestLot, out path, out reason));
            Assert.Equal(TransitPath.None, path);
            Assert.Contains("is not in this neighbourhood", reason);
        }

        [Fact]
        public void ABulldozedPlotHasNoDoorToWalkOutOf()
        {
            //A plot has no house, so it has no driveway and no entrance - a rule the query inherits
            //from RoadGraph rather than inventing, and one a walk has to inherit with it.
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.False(transit.TryPlanPath(NorthWestLot, Plot, out path, out reason));
            Assert.Contains("has no house", reason);

            Assert.False(transit.TryPlanPath(Plot, NorthWestLot, out path, out reason));
            Assert.Contains("has no house", reason);
        }

        [Fact]
        public void ADoorThatIsNotOnRoadIsRefusedRatherThanRoutedTo()
        {
            //Lot 15's door is moved onto bare ground. The tile is inside the map and it is a real
            //entrance, so this is the case the road check exists for: without it the router would
            //happily start a walk on a lawn.
            var map = RoadMap(BlockSize, (x, y) => InBounds(x, y, BlockSize)
                && (x == BlockColumn || y == BlockRow));
            var layout = Layout(BlockSize,
                Lot(NorthWestLot, 8, 4, NeighbourhoodLotSide.East),
                Lot(WestRowLot, 4, 13, NeighbourhoodLotSide.North));
            var query = Query(layout, map, new List<NeighbourhoodEntrance>
            {
                Door(NorthWestLot, 12, 6, NeighbourhoodDoorSide.East),
                Door(WestRowLot, 5, 6, NeighbourhoodDoorSide.North),
            });
            var transit = TransitLot(query, NorthWestLot);

            TransitPath path;
            string reason;
            Assert.False(transit.TryPlanPath(NorthWestLot, WestRowLot, out path, out reason));
            Assert.Contains("carries no road", reason);
        }

        // ---- advancing -----------------------------------------------------------------------

        [Fact]
        public void AdvancingWalksThePathAndReportsArrival()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(NorthWestLot, SouthWestLot, out path, out reason));
            Assert.Null(reason);
            Assert.Equal(12, path.Length);

            Assert.True(transit.IsWalking);
            var state = transit.State;
            Assert.Equal(NorthWestLot, state.Origin);
            Assert.Equal(SouthWestLot, state.Destination);
            Assert.Equal(0.0, state.Progress);
            Assert.Equal(0.0, state.Fraction);
            Assert.Equal(NorthWestDoorX, state.Current.X);
            Assert.Equal(NorthWestDoorY, state.Current.Y);
            Assert.Equal(new TransitWaypoint(12, 7, TransitPath.NoLot), state.Next);
            Assert.Equal(NorthWestDoorX, state.PositionX);
            Assert.Equal(NorthWestDoorY, state.PositionY);

            //Four and a half tiles: a partial tile, so the position is between two whole tiles
            //rather than snapped to one of them, and the index still names the tile behind it.
            Assert.False(transit.Advance(4.5));
            state = transit.State;
            Assert.Equal(4.5, state.Progress);
            Assert.Equal(7.5, state.Remaining);
            Assert.Equal(0.375, state.Fraction);
            Assert.Equal(4, state.WaypointIndex);
            Assert.Equal(new TransitWaypoint(12, 10, TransitPath.NoLot), state.Current);
            Assert.Equal(new TransitWaypoint(12, 11, TransitPath.NoLot), state.Next);
            Assert.Equal(12.0, state.PositionX);
            Assert.Equal(10.5, state.PositionY);
            Assert.False(state.IsArrived);

            Assert.False(transit.Advance(0.5));
            Assert.False(transit.Advance(5.0));
            Assert.False(transit.Advance(1.5));
            state = transit.State;
            Assert.Equal(11.5, state.Progress);
            Assert.Equal(0.5, state.Remaining);

            //The step that finishes it.
            Assert.True(transit.Advance(0.5));
            state = transit.State;
            Assert.Equal(12.0, state.Progress);
            Assert.Equal(0.0, state.Remaining);
            Assert.Equal(1.0, state.Fraction);
            Assert.True(state.IsArrived);
            Assert.False(state.IsWalking);
            Assert.Equal(path.End, state.Current);
            Assert.Equal(TransitPath.NoLot, state.Next.LotNumber);
        }

        [Fact]
        public void ArrivingOvershootsOntoTheDoorAndNoFurther()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(NorthWestLot, SouthWestLot, out path, out reason));

            Assert.True(transit.Advance(1000.0));
            Assert.Equal(12.0, transit.State.Progress);
            Assert.Equal(path.End, transit.State.Current);

            //Arrival is sticky: a fixed-step loop that runs one more frame, or a transition retried
            //after a failure, keeps reporting arrival and moves nothing.
            Assert.True(transit.Advance(1.0));
            Assert.True(transit.Advance(1.0));
            Assert.Equal(12.0, transit.State.Progress);
        }

        [Fact]
        public void AZeroLengthWalkIsAlreadyArrived()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(NorthWestLot, NorthWestLot, out path, out reason));

            Assert.False(transit.IsWalking);
            Assert.True(transit.State.IsArrived);
            Assert.Equal(1.0, transit.State.Fraction);
            Assert.True(transit.Advance(0.5));
        }

        [Theory]
        [InlineData(0.0)]
        [InlineData(-4.0)]
        [InlineData(double.NaN)]
        public void AStepThatIsNotAPositiveDistanceIsIgnored(double distance)
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(NorthWestLot, SouthWestLot, out path, out reason));

            Assert.False(transit.Advance(distance));
            Assert.Equal(0.0, transit.State.Progress);
        }

        [Fact]
        public void AdvancingWithNoWalkInProgressDoesNothing()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            Assert.False(transit.Advance(1.0));
            Assert.False(transit.Advance(1000.0));
            Assert.False(transit.State.IsValid);
            Assert.False(transit.State.IsWalking);
            Assert.False(transit.State.IsArrived);
            Assert.Equal(0.0, transit.State.Fraction);
        }

        [Fact]
        public void ASecondWalkIsRefusedWhileOneIsInProgress()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(NorthWestLot, SouthWestLot, out path, out reason));

            TransitPath second;
            Assert.False(transit.TryBeginTransit(NorthWestLot, NorthEastLot, out second, out reason));
            Assert.Contains("already in progress", reason);

            //and the walk in progress is the one that is still there
            Assert.Equal(SouthWestLot, transit.State.Destination);
        }

        [Fact]
        public void CompletingAShortenedWalkIsRefused()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(NorthWestLot, SouthWestLot, out path, out reason));
            transit.Advance(4.0);

            Assert.False(transit.CompleteTransit(out reason));
            Assert.Contains("tile(s) short of lot", reason);
            Assert.True(transit.IsWalking);
        }

        [Fact]
        public void CompletingAWalkHandsTheAvatarToTheDestination()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(NorthWestLot, SouthWestLot, out path, out reason));
            transit.Advance(1000.0);

            Assert.True(transit.CompleteTransit(out reason));
            Assert.Null(reason);
            Assert.False(transit.IsWalking);
            Assert.False(transit.State.IsValid);
            Assert.Equal(SouthWestLot, transit.ActiveLotNumber);
            Assert.Equal(ActiveLotKind.Active, transit.Kind);
            Assert.True(transit.HasOwner);
        }

        [Fact]
        public void CancellingPutsTheWorldBackWhereItStarted()
        {
            var transit = new NeighbourhoodTransitLot(
                BlockQuery(), ActiveLotKind.Hollow, WestRowLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(WestRowLot, SouthWestLot, out path, out reason));
            transit.Advance(3.0);

            Assert.True(transit.CancelTransit(out reason));
            Assert.Null(reason);
            Assert.False(transit.IsWalking);
            Assert.Equal(WestRowLot, transit.ActiveLotNumber);
            Assert.Equal(ActiveLotKind.Hollow, transit.Kind);
        }

        [Fact]
        public void ThereIsNothingToCompleteOrCancelWithoutAWalk()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            string reason;
            Assert.False(transit.CompleteTransit(out reason));
            Assert.Contains("no walk in progress", reason);
            Assert.False(transit.CancelTransit(out reason));
            Assert.Contains("no walk in progress", reason);
        }

        [Fact]
        public void APathWithNoWaypointsIsNotAWalk()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            string reason;
            Assert.False(transit.BeginTransit(TransitPath.None, out reason));
            Assert.Contains("no planned walk", reason);
            Assert.False(transit.BeginTransit(null, out reason));
            Assert.False(transit.IsWalking);

            Assert.Throws<ArgumentNullException>(() => new TransitPath(1, 2, null));
            Assert.Throws<ArgumentException>(() => new TransitPath(1, 2, new TransitWaypoint[0]));
        }

        // ---- the invariant --------------------------------------------------------------------

        [Theory]
        [InlineData(ActiveLotKind.Active, 11)]
        [InlineData(ActiveLotKind.Hollow, 22)]
        [InlineData(ActiveLotKind.Transit, TransitPath.NoLot)]
        public void OneOwnerIsTheInvariant(ActiveLotKind kind, int lotNumber)
        {
            Assert.True(NeighbourhoodTransitLot.IsExactlyOneActiveLot(kind, lotNumber));
        }

        [Theory]
        [InlineData(null, 11)]
        [InlineData(ActiveLotKind.Active, TransitPath.NoLot)]
        [InlineData(ActiveLotKind.Hollow, TransitPath.NoLot)]
        [InlineData(ActiveLotKind.Transit, 11)]
        [InlineData((ActiveLotKind)7, 11)]
        public void NoOwnerIsOrAnOwnerThatNamesTheWrongThingIsNotTheInvariant(
            ActiveLotKind? kind, int lotNumber)
        {
            string reason;
            Assert.False(NeighbourhoodTransitLot.IsExactlyOneActiveLot(kind, lotNumber, out reason));
            Assert.False(string.IsNullOrEmpty(reason));
        }

        [Fact]
        public void TheInvariantHoldsAtEveryStepOfAWalk()
        {
            //This is the assertion B4 task 5 asks for, made after each transition the walk
            //performs. The transit lot is the reason the middle of it is legal: the avatar is in a
            //lot of its own, so the world genuinely is in no numbered lot for the duration.
            var transit = TransitLot(BlockQuery(), NorthWestLot);
            Assert.True(transit.HasOwner);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(NorthWestLot, WestRowLot, out path, out reason));
            Assert.Equal(ActiveLotKind.Transit, transit.Kind);
            Assert.Equal(TransitPath.NoLot, transit.ActiveLotNumber);
            Assert.True(transit.HasOwner);

            transit.Advance(4.0);
            Assert.True(transit.HasOwner);

            Assert.True(transit.Advance(1000.0));
            Assert.True(transit.HasOwner);

            Assert.True(transit.CompleteTransit(out reason));
            Assert.Equal(ActiveLotKind.Active, transit.Kind);
            Assert.Equal(WestRowLot, transit.ActiveLotNumber);
            Assert.True(transit.HasOwner);
        }

        [Fact]
        public void AWorldWithNoLotOwnsNothingAndSaysSo()
        {
            //The neighbourhood screen has no lot, which is legitimate for a renderer and is
            //deliberately not a pass for the guard: nothing owns the avatar, so nothing can hand
            //it to the destination when a walk ends.
            var transit = new NeighbourhoodTransitLot(BlockQuery());

            Assert.False(transit.HasOwner);
            Assert.Equal(TransitPath.NoLot, transit.ActiveLotNumber);
        }

        [Fact]
        public void AQueryIsRequired()
        {
            Assert.Throws<ArgumentNullException>(() => new NeighbourhoodTransitLot(null));
        }

        [Fact]
        public void ThePlannerDescribesItselfAndItsWalk()
        {
            var transit = TransitLot(BlockQuery(), NorthWestLot);

            TransitPath path;
            string reason;
            Assert.True(transit.TryBeginTransit(NorthWestLot, WestRowLot, out path, out reason));
            transit.Advance(5.0);

            Assert.Contains("transit lot", transit.Describe());
            Assert.Contains("5/13", transit.Describe());
            Assert.Contains("lot 11 -> lot 15", path.Describe());
            Assert.Contains("13 tile(s)", path.Describe());
            Assert.Equal("no path", TransitPath.None.Describe());
            Assert.Contains("lot 11 -> lot 15", transit.State.Describe());
            Assert.Equal("not walking", new NeighbourhoodTransitLot(BlockQuery()).State.Describe());
        }
    }
}
