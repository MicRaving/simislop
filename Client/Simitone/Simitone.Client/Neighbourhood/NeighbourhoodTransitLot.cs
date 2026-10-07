/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Which of the world's lot VMs is the one the travelling avatar belongs to right now.
    ///
    /// <para>
    /// §6 B4 task 1 is the reason this type exists at all: <b>exactly one VM owns the avatar at any
    /// moment</b>, and a "transit lot" - a <c>World</c> + <c>VM</c> whose blueprint is the
    /// generated street region around a hop - is how a walk is simulated without two lot VMs
    /// sharing one sim. Two lot VMs holding the same avatar is the failure mode the whole design
    /// exists to avoid, so the state is a three-valued enum rather than a set of booleans: a
    /// caller cannot write "active <em>and</em> transit" in the first place.
    /// </para>
    /// <para>
    /// The three values are mutually exclusive by construction and
    /// <see cref="NeighbourhoodTransitLot.IsExactlyOneActiveLot"/> is the pure predicate that
    /// states the whole invariant - see that method for why the enum alone is not the assertion.
    /// </para>
    /// </summary>
    public enum ActiveLotKind
    {
        /// <summary>
        /// A real lot is loaded and owns the avatar: the player is standing inside a house. The
        /// lot named by the world's <c>ActiveLotNumber</c> is that house.
        /// </summary>
        Active = 0,

        /// <summary>
        /// The transit lot owns the avatar: it is on the generated streets between two doors, in
        /// no numbered lot at all. This is the only state in which the world is legitimately in
        /// no lot, and it is why the transit lot is a lot rather than a world-space drawable -
        /// routing and motives need a VM, and a VM needs a lot.
        /// </summary>
        Transit = 1,

        /// <summary>
        /// A hollow neighbour lot's VM owns the avatar. Distinct from <see cref="Active"/> only so
        /// a log can say which of the two resident lots the player is standing in; it is the same
        /// one-VM-owns-the-avatar guarantee.
        /// </summary>
        Hollow = 2,
    }

    /// <summary>
    /// One tile on a planned walk, in the same map-tile space as
    /// <see cref="NeighbourhoodMap.Index"/> - not lot-local, not pixels, so a caller can index any
    /// generated array with <see cref="X"/>/<see cref="Y"/> without a conversion.
    /// </summary>
    public readonly struct TransitWaypoint : IEquatable<TransitWaypoint>
    {
        /// <summary>Tile x.</summary>
        public int X { get; }

        /// <summary>Tile y.</summary>
        public int Y { get; }

        /// <summary>
        /// The lot whose door this tile is, or <see cref="NeighbourhoodTransitLot.NoLot"/> for an
        /// ordinary street tile. Set on the first and last waypoint of a path, which are the two
        /// doors it joins.
        /// </summary>
        /// <remarks>
        /// A path may pass <em>over</em> a third lot's door without stopping, and then that
        /// waypoint carries that lot's number. That is the truth about the tile rather than a
        /// bug: the tile is that lot's doorstep, the walk simply does not pause on it.
        /// </remarks>
        public int LotNumber { get; }

        /// <summary>Creates a waypoint.</summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        /// <param name="lotNumber">The lot whose door this is, or 0 for a street tile.</param>
        public TransitWaypoint(int x, int y, int lotNumber)
        {
            X = x;
            Y = y;
            LotNumber = lotNumber;
        }

        /// <summary>Whether this tile is a lot's door rather than plain street.</summary>
        public bool IsEntrance
        {
            get { return LotNumber != NeighbourhoodTransitLot.NoLot; }
        }

        /// <summary>Two waypoints are equal when all three fields are.</summary>
        /// <param name="other">The waypoint to compare with.</param>
        public bool Equals(TransitWaypoint other)
        {
            return X == other.X && Y == other.Y && LotNumber == other.LotNumber;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is TransitWaypoint && Equals((TransitWaypoint)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ LotNumber;
                return hash;
            }
        }

        /// <summary>Two waypoints are equal when all three fields are.</summary>
        /// <param name="a">One waypoint.</param>
        /// <param name="b">The other.</param>
        public static bool operator ==(TransitWaypoint a, TransitWaypoint b)
        {
            return a.Equals(b);
        }

        /// <summary>The negation of <see cref="op_Equality"/>.</summary>
        /// <param name="a">One waypoint.</param>
        /// <param name="b">The other.</param>
        public static bool operator !=(TransitWaypoint a, TransitWaypoint b)
        {
            return !a.Equals(b);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return "(" + X.ToString(CultureInfo.InvariantCulture) + ","
                + Y.ToString(CultureInfo.InvariantCulture) + ")"
                + (IsEntrance ? " lot " + LotNumber.ToString(CultureInfo.InvariantCulture) : string.Empty);
        }
    }

    /// <summary>
    /// A planned walk between two doors: the tiles it crosses, in order, and the two lots it joins.
    ///
    /// <para>
    /// <b>An immutable decision.</b> Nothing revises a path once it has been planned - a
    /// re-plan produces a different object - so a follower walking this list and a camera
    /// following that follower cannot be looking at two different routes. The list is a
    /// read-only wrapper over an array built at construction and the same instance comes back
    /// every time, so a caller may hold it for the length of a walk.
    /// </para>
    /// <para>
    /// <b>Every tile, not every corner.</b> <see cref="Waypoints"/> is the full four-connected
    /// chain, one tile per step, which is what a path follower wants; <see cref="Corners"/> is the
    /// turn-only subset, which is what a debug draw and a distance summary want. Both are views
    /// of the same walk, computed once, so the two can never disagree.
    /// </para>
    /// </summary>
    public sealed class TransitPath
    {
        private static readonly TransitWaypoint[] NoWaypoints = new TransitWaypoint[0];

        /// <summary>
        /// The path that does not exist. Handed back on a refused <c>TryPlanPath</c> rather than
        /// <c>null</c>, so a caller that ignores the boolean still cannot walk off the end of
        /// nothing: <see cref="IsValid"/> is <c>false</c> and <see cref="Length"/> is 0.
        /// </summary>
        public static readonly TransitPath None =
            new TransitPath(NoLot, NoLot, NoWaypoints, false);

        /// <summary>The lot the walk starts at - the one whose door is the first waypoint.</summary>
        public int OriginLot { get; }

        /// <summary>The lot the walk ends at - the one whose door is the last waypoint.</summary>
        public int DestinationLot { get; }

        /// <summary>
        /// Every tile of the walk, first to last, starting on the origin's door and ending on the
        /// destination's. Adjacent entries are always one orthogonal step apart. Never null.
        /// </summary>
        public IReadOnlyList<TransitWaypoint> Waypoints { get; }

        /// <summary>
        /// The turn points: the first and last waypoint, plus every tile where the walk changes
        /// direction. Never null. For a straight walk this is just the two ends.
        /// </summary>
        public IReadOnlyList<TransitWaypoint> Corners { get; }

        /// <summary>
        /// The walk's length in tiles, which is <c>Waypoints.Count - 1</c> because every step is
        /// one tile. Zero for a walk from a lot to itself.
        /// </summary>
        public int Length { get; }

        /// <summary>
        /// Whether this is a walkable path. False for <see cref="None"/> and for a path built with
        /// no waypoints at all; a caller that checks this cannot start walking.
        /// </summary>
        public bool IsValid { get; }

        /// <summary>The origin lot's door, or <c>default</c> on an invalid path.</summary>
        public TransitWaypoint Start { get; }

        /// <summary>The destination lot's door, or <c>default</c> on an invalid path.</summary>
        public TransitWaypoint End { get; }

        private TransitPath(int origin, int destination, IReadOnlyList<TransitWaypoint> waypoints,
            bool isValid)
        {
            OriginLot = origin;
            DestinationLot = destination;
            Waypoints = waypoints;
            Length = waypoints.Count > 0 ? waypoints.Count - 1 : 0;
            IsValid = isValid && waypoints.Count > 0;
            Start = waypoints.Count > 0 ? waypoints[0] : default(TransitWaypoint);
            End = waypoints.Count > 0 ? waypoints[waypoints.Count - 1] : default(TransitWaypoint);
            Corners = IsValid ? new ReadOnlyCollection<TransitWaypoint>(FindCorners(waypoints))
                : new ReadOnlyCollection<TransitWaypoint>(NoWaypoints);
        }

        /// <summary>
        /// Creates a path from a chain of waypoints. The chain must be non-empty and four-connected;
        /// the planner produces one per search step and nothing else in the tree does.
        /// </summary>
        /// <param name="origin">The lot the walk starts at.</param>
        /// <param name="destination">The lot the walk ends at.</param>
        /// <param name="waypoints">Every tile of the walk, first to last.</param>
        /// <exception cref="ArgumentNullException"><paramref name="waypoints"/> is null.</exception>
        /// <exception cref="ArgumentException">The chain is empty, or two neighbours are not one
        /// orthogonal step apart.</exception>
        public TransitPath(int origin, int destination, IReadOnlyList<TransitWaypoint> waypoints)
        {
            if (waypoints == null) throw new ArgumentNullException(nameof(waypoints));
            if (waypoints.Count == 0)
            {
                throw new ArgumentException(
                    "a path with no waypoints is not a walk; use TransitPath.None instead.",
                    nameof(waypoints));
            }
            for (int i = 1; i < waypoints.Count; i++)
            {
                var a = waypoints[i - 1];
                var b = waypoints[i];
                var step = Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y);
                if (step != 1)
                {
                    throw new ArgumentException(
                        "waypoint " + i + " (" + b + ") is " + step
                        + " tile(s) from waypoint " + (i - 1) + " (" + a
                        + "); a transit path is a chain of single-tile steps.",
                        nameof(waypoints));
                }
            }

            Waypoints = new ReadOnlyCollection<TransitWaypoint>(new List<TransitWaypoint>(waypoints));
            OriginLot = origin;
            DestinationLot = destination;
            Length = Waypoints.Count - 1;
            IsValid = true;
            Start = Waypoints[0];
            End = Waypoints[Waypoints.Count - 1];
            Corners = new ReadOnlyCollection<TransitWaypoint>(FindCorners(Waypoints));
        }

        /// <summary>One line for a log, or for a failing assertion's message.</summary>
        public string Describe()
        {
            if (!IsValid) return "no path";
            var sb = new StringBuilder();
            sb.Append("lot ").Append(OriginLot).Append(" -> lot ").Append(DestinationLot)
              .Append(": ").Append(Length).Append(" tile(s), ").Append(Waypoints.Count)
              .Append(" waypoint(s), ").Append(Corners.Count).Append(" corner(s) [")
              .Append(Start).Append(" .. ").Append(End).Append(']');
            return sb.ToString();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }

        private static List<TransitWaypoint> FindCorners(IReadOnlyList<TransitWaypoint> waypoints)
        {
            var corners = new List<TransitWaypoint>();
            if (waypoints.Count == 0) return corners;

            corners.Add(waypoints[0]);
            for (int i = 1; i + 1 < waypoints.Count; i++)
            {
                var previous = waypoints[i - 1];
                var here = waypoints[i];
                var next = waypoints[i + 1];
                //A turn is a step that does not continue in the same direction, which on a
                //four-connected chain is exactly "the direction from previous to here differs from
                //the one from here to next". Comparing directions rather than checking for
                //diagonals is what keeps a two-step corner (right then down) out of the list.
                var inX = here.X - previous.X;
                var inY = here.Y - previous.Y;
                var outX = next.X - here.X;
                var outY = next.Y - here.Y;
                if (inX != outX || inY != outY) corners.Add(here);
            }
            if (waypoints.Count > 1) corners.Add(waypoints[waypoints.Count - 1]);
            return corners;
        }

        /// <summary>
        /// The lot number that means "no lot", and the value
        /// <see cref="NeighbourhoodTransitLot.IsExactlyOneActiveLot"/> reads as "the owner of the
        /// avatar is not a numbered lot". It is 0 because
        /// <see cref="INeighbourhoodWorld.ActiveLotNumber"/> already says 0 for "the player is in
        /// no lot" - a second spelling of the same nothing would be a second answer to the same
        /// question.
        /// </summary>
        public const int NoLot = 0;
    }

    /// <summary>
    /// Where a walk has got to: the two lots, the path, and how far along it the avatar is.
    ///
    /// <para>
    /// <b>A snapshot, not a handle.</b> It is a readonly value rebuilt on every read, so a caller
    /// cannot hold a reference and watch the walk move underneath it - the two halves of a frame
    /// (where the avatar is, what the HUD says) are made from one consistent read. The
    /// <see cref="NeighbourhoodTransitLot"/> that produced it is the only thing that advances.
    /// </para>
    /// <para>
    /// <b><see cref="Progress"/> is in tiles</b>, because <see cref="NeighbourhoodTransitLot.Advance"/>
    /// is given tiles and converting here would put one scale into a question that already has
    /// one. It runs 0 to <see cref="TransitPath.Length"/>; <see cref="Fraction"/> is the same fact
    /// normalised, for a progress bar.
    /// </para>
    /// </summary>
    public readonly struct TransitState
    {
        /// <summary>The lot the walk started at, or <see cref="TransitPath.NoLot"/> for none.</summary>
        public int Origin { get; }

        /// <summary>The lot the walk ends at, or <see cref="TransitPath.NoLot"/> for none.</summary>
        public int Destination { get; }

        /// <summary>The planned walk, or <c>null</c> when no walk is in progress.</summary>
        public TransitPath Path { get; }

        /// <summary>How far along <see cref="Path"/> the avatar has walked, in tiles.</summary>
        public double Progress { get; }

        /// <summary>Creates a state.</summary>
        /// <param name="origin">The lot the walk starts at.</param>
        /// <param name="destination">The lot the walk ends at.</param>
        /// <param name="path">The path, or null for no walk.</param>
        /// <param name="progress">Tiles walked.</param>
        public TransitState(int origin, int destination, TransitPath path, double progress)
        {
            Origin = origin;
            Destination = destination;
            Path = path;
            Progress = progress;
        }

        /// <summary>Whether this state describes a walk at all.</summary>
        public bool IsValid
        {
            get { return Path != null && Path.IsValid; }
        }

        /// <summary>Whether the avatar is still on the street.</summary>
        public bool IsWalking
        {
            get { return IsValid && Progress < Path.Length; }
        }

        /// <summary>Whether the avatar has reached the destination's door.</summary>
        public bool IsArrived
        {
            get { return IsValid && Progress >= Path.Length; }
        }

        /// <summary>
        /// Which waypoint of <see cref="Path"/> the avatar is on or has just left, clamped to the
        /// path. The index a follower uses to pick the next tile.
        /// </summary>
        public int WaypointIndex
        {
            get
            {
                if (!IsValid) return 0;
                var index = (int)Math.Floor(Progress);
                if (index < 0) return 0;
                return index > Path.Length ? Path.Length : index;
            }
        }

        /// <summary>How many tiles are left, never negative.</summary>
        public double Remaining
        {
            get
            {
                if (!IsValid) return 0.0;
                var left = (double)Path.Length - Progress;
                return left > 0.0 ? left : 0.0;
            }
        }

        /// <summary>
        /// Progress from 0 to 1, or 0 when there is no walk. A zero-length walk reads as 1: the
        /// avatar is standing on the destination's door, which is arrived.
        /// </summary>
        public double Fraction
        {
            get
            {
                if (!IsValid) return 0.0;
                if (Path.Length == 0) return 1.0;
                return Progress / Path.Length;
            }
        }

        /// <summary>The waypoint the avatar is on, or <c>default</c> with no walk.</summary>
        public TransitWaypoint Current
        {
            get
            {
                var index = WaypointIndex;
                return IsValid ? Path.Waypoints[index] : default(TransitWaypoint);
            }
        }

        /// <summary>
        /// The waypoint the avatar is walking towards, or <c>default</c> once it has arrived - a
        /// follower asked for a next tile after the last one must stop rather than run off the end.
        /// </summary>
        public TransitWaypoint Next
        {
            get
            {
                var index = WaypointIndex;
                return IsValid && index < Path.Length ? Path.Waypoints[index + 1]
                    : default(TransitWaypoint);
            }
        }

        /// <summary>
        /// Where the avatar is, in tiles, between two whole tiles. A follower positions a sprite
        /// from this and a camera follows the sprite; neither has to re-derive it.
        /// </summary>
        public double PositionX
        {
            get { return Interpolate(true); }
        }

        /// <summary>
        /// Where the avatar is, in tiles, between two whole tiles. A follower positions a sprite
        /// from this and a camera follows the sprite; neither has to re-derive it.
        /// </summary>
        public double PositionY
        {
            get { return Interpolate(false); }
        }

        /// <summary>One line for a log, or for a failing assertion's message.</summary>
        public string Describe()
        {
            if (!IsValid) return "not walking";
            return "lot " + Origin + " -> lot " + Destination + ": "
                + Progress.ToString("0.###", CultureInfo.InvariantCulture) + "/"
                + Path.Length + " tile(s) at (" + Current + ")";
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }

        private double Interpolate(bool x)
        {
            if (!IsValid) return 0.0;
            var index = WaypointIndex;
            var from = Path.Waypoints[index];
            if (index >= Path.Length) return x ? from.X : from.Y;
            var to = Path.Waypoints[index + 1];
            var step = Progress - index;
            var a = x ? from.X : from.Y;
            var b = x ? to.X : to.Y;
            return a + (b - a) * step;
        }
    }

    /// <summary>
    /// B4's transit lot, minus the VM: the pure half of a walk between two lots. It plans the
    /// route over <see cref="NeighbourhoodQuery"/>'s road graph, it holds how far along that route
    /// the avatar is, and it states the exactly-one-active-lot invariant as a predicate a caller
    /// can assert. There is no <c>World</c>, no <c>VM</c>, no graphics device, no clock and no
    /// file system here, so every claim in this file is testable without a TS1 install - which is
    /// the whole point of splitting it out of the file that would have all of those.
    ///
    /// <para>
    /// <b>Why a transit lot at all.</b> §6 B4 task 1: two lot VMs sharing one avatar is the
    /// failure mode, so the avatar lives in a lot of its own while it is on the street. A
    /// world-space avatar drawn with no VM was considered and rejected for a first cut - routing
    /// and motives need a VM. What this class supplies is the third piece: where the avatar goes,
    /// and when it has arrived.
    /// </para>
    ///
    /// <para><b>How the walk is found.</b> An A* over the road tiles the query already
    /// classified, from the origin lot's door tile to the destination lot's door tile, four-connected
    /// and unit-cost, with Manhattan distance as the heuristic - admissible on a uniform grid, so
    /// the route found is a shortest one. The result is a <see cref="TransitPath"/>: every tile of
    /// the walk, in order.</para>
    /// <para>
    /// <b>Determinism, and why it is not free.</b> The frontier is ordered by
    /// (cost + heuristic, heuristic, tile index) and neighbours are expanded in the fixed
    /// <see cref="RoadMask"/> bit order, so two plans over the same query return the same tiles in
    /// the same order. That is a requirement rather than a nicety: B7 resumes a walk from a
    /// sidecar, and a pathfinder that re-ties its open set differently on a later run would move
    /// the avatar somewhere the save did not say.
    /// </para>
    /// <para>
    /// <b>What the walk is allowed to cross.</b> Road tiles only. The generated map records road
    /// in <c>RoadData</c> and nothing else about walkable ground - the sidewalk ring is not in it
    /// at all - so "road" is the only answer to "may I stand here" the map gives, and a route
    /// that cut across the ground between two streets would be a route through a player's garden.
    /// </para>
    /// <para>
    /// <b>The invariant, and where it is enforced.</b> B4 task 5 asks for "exactly one active lot
    /// at a time (active / transit / hollow), asserted". <see cref="IsExactlyOneActiveLot"/> is
    /// that assertion, and it is static and pure so a test can call it without a world. The half
    /// of the guarantee this type can enforce by shape is that there is only ever <em>one</em>
    /// owner: <see cref="ActiveLotKind"/> is a single value, not a set of flags, so "active and
    /// transit" is not a state this object can be in. The predicate adds the two facts the shape
    /// cannot state - that the owner is a declared kind, and that the kind and the lot number
    /// agree - and this class's own lifecycle is written to satisfy it at every step, so a caller
    /// can assert it after each transition and get an answer rather than an exception.
    /// </para>
    /// <para>
    /// <b>One walk at a time.</b> <see cref="BeginTransit"/> refuses while another walk is in
    /// progress. A retarget mid-street is a decision the caller has to make explicitly, by
    /// cancelling and beginning again, because the transit lot holds one avatar and a second plan
    /// silently replacing the first is how two of them end up driving the same sim.
    /// </para>
    /// <para>
    /// <b>Threading.</b> Pure managed code and no device, but the mutable half - the walk in
    /// progress - belongs to the game thread, because it is going to be driven by a VM. Planning
    /// does not care.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodTransitLot
    {
        /// <summary>
        /// The lot number that means "no lot": the neighbourhood screen, or the transit lot, whose
        /// blueprint is a street region rather than anybody's house. See
        /// <see cref="TransitPath.NoLot"/>, which is the same constant stated on the path.
        /// </summary>
        public const int NoLot = TransitPath.NoLot;

        /// <summary>
        /// The four step directions in <see cref="RoadMask"/> bit order: east, south, west, north.
        /// Fixed, because the order neighbours are expanded in is what makes the frontier's
        /// contents - and therefore the tie-breaks inside it - reproducible.
        /// </summary>
        private static readonly int[] StepX = { 1, 0, -1, 0 };
        private static readonly int[] StepY = { 0, 1, 0, -1 };

        /// <summary>
        /// A tile the search has not reached: one more than any shortest route can be, because a
        /// route on a tile grid is at most as long as the grid has tiles.
        /// </summary>
        private const int Unreached = int.MaxValue;

        private readonly NeighbourhoodQuery _query;

        private ActiveLotKind _kind;
        private int _activeLot;
        private ActiveLotKind _kindBeforeWalk;
        private int _lotBeforeWalk;

        private int _origin;
        private int _destination;
        private TransitPath _path;
        private double _progress;

        /// <summary>
        /// A transit lot over a neighbourhood that has no active lot: the neighbourhood screen,
        /// before a player has walked into anything. The first lot opened is the caller's to
        /// declare with the other constructor.
        /// </summary>
        /// <param name="query">The spatial substrate to plan over. Required.</param>
        /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
        public NeighbourhoodTransitLot(NeighbourhoodQuery query)
            : this(query, ActiveLotKind.Active, NoLot)
        {
        }

        /// <summary>
        /// A transit lot for a world that already knows which VM owns the avatar - the state a
        /// neighbourhood is in when a player is standing inside a lot, or inside a hollow one.
        /// </summary>
        /// <param name="query">The spatial substrate to plan over. Required.</param>
        /// <param name="kind">Which kind of lot owns the avatar now.</param>
        /// <param name="activeLotNumber">That lot's number, or <see cref="NoLot"/> for none.</param>
        /// <exception cref="ArgumentNullException"><paramref name="query"/> is null.</exception>
        public NeighbourhoodTransitLot(NeighbourhoodQuery query, ActiveLotKind kind, int activeLotNumber)
        {
            if (query == null) throw new ArgumentNullException(nameof(query));
            _query = query;
            _kind = kind;
            _activeLot = activeLotNumber;
        }

        /// <summary>The query this transit lot plans over. Held, not copied - a query is immutable
        /// from a reader's point of view, and copying its tables would mean two to keep in step.</summary>
        public NeighbourhoodQuery Query
        {
            get { return _query; }
        }

        /// <summary>Which kind of lot owns the avatar right now. See <see cref="ActiveLotKind"/>.</summary>
        public ActiveLotKind Kind
        {
            get { return _kind; }
        }

        /// <summary>
        /// The lot that owns the avatar, or <see cref="NoLot"/>.
        ///
        /// <para>
        /// <see cref="NoLot"/> while a walk is in progress, and that is not an oversight: the
        /// avatar is on the street, in the transit lot, in no numbered lot at all. The lot being
        /// left is <see cref="TransitState.Origin"/> and the lot being entered is
        /// <see cref="TransitState.Destination"/>; the world itself names no lot for the whole
        /// duration, which is exactly why the transit lot exists.
        /// </para>
        /// </summary>
        public int ActiveLotNumber
        {
            get { return _activeLot; }
        }

        /// <summary>Whether this object's own state satisfies the exactly-one-active-lot invariant.</summary>
        public bool HasOwner
        {
            get { return IsExactlyOneActiveLot(_kind, _activeLot); }
        }

        /// <summary>Whether a walk is in progress. False once it has arrived or been cancelled.</summary>
        public bool IsWalking
        {
            get { return _path != null && _progress < _path.Length; }
        }

        /// <summary>
        /// The walk in progress, or <c>default</c> when there is none - which reads as
        /// <see cref="TransitState.IsValid"/> false rather than as a crash.
        /// </summary>
        public TransitState State
        {
            get { return new TransitState(_origin, _destination, _path, _progress); }
        }

        // ---------------------------------------------------------------- the invariant

        /// <summary>
        /// B4's guard, as a pure predicate: <b>is exactly one lot the owner of the avatar?</b>
        ///
        /// <para>
        /// The three owner kinds are mutually exclusive by construction -
        /// <see cref="ActiveLotKind"/> is one value and not a set of flags - so the "two lots are
        /// active at once" failure cannot be written down in this vocabulary at all. What remains
        /// is the half the shape cannot state, and it is two checks rather than one:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>
        ///     The kind has to be a <em>declared</em> one. A value cast in from nowhere is not a
        ///     state the world can be in, and answering <c>true</c> for one would make this
        ///     predicate agree with a corrupt caller.
        ///   </description></item>
        ///   <item><description>
        ///     The kind and the lot number have to agree. <see cref="ActiveLotKind.Transit"/> is a
        ///     street region, not a house, so it owns the avatar while naming no lot;
        ///     <see cref="ActiveLotKind.Active"/> and <see cref="ActiveLotKind.Hollow"/> are
        ///     houses, so they own it while naming exactly one - and 0 is not a lot number
        ///     (<see cref="INeighbourhoodWorld.ActiveLotNumber"/> says 0 for "in no lot").
        ///   </description></item>
        /// </list>
        /// <para>
        /// A null kind is the answer "nobody owns the avatar", which is what the neighbourhood
        /// screen is and is deliberately <em>not</em> a pass: a world that has loaded nothing is a
        /// legitimate state for a renderer and an illegitimate one for a walk.
        /// </para>
        /// <para>
        /// This is a static predicate on purpose. A caller asserts it over the world's own view -
        /// which VM currently holds the avatar, and which lot that VM is - and a test asserts it
        /// with no world at all.
        /// </para>
        /// </summary>
        /// <param name="kind">The owning lot's kind, or null for none.</param>
        /// <param name="activeLotNumber">The owning lot's number, or <see cref="NoLot"/>.</param>
        /// <returns>Whether exactly one lot owns the avatar.</returns>
        public static bool IsExactlyOneActiveLot(ActiveLotKind? kind, int activeLotNumber)
        {
            string reason;
            return IsExactlyOneActiveLot(kind, activeLotNumber, out reason);
        }

        /// <summary>
        /// <see cref="IsExactlyOneActiveLot(ActiveLotKind?, int)"/> with the reason it said no, so
        /// a failed assertion names the state that broke it rather than just failing.
        /// </summary>
        /// <param name="kind">The owning lot's kind, or null for none.</param>
        /// <param name="activeLotNumber">The owning lot's number, or <see cref="NoLot"/>.</param>
        /// <param name="reason">Why the invariant does not hold, or <c>null</c> when it does.</param>
        /// <returns>Whether exactly one lot owns the avatar.</returns>
        public static bool IsExactlyOneActiveLot(ActiveLotKind? kind, int activeLotNumber,
            out string reason)
        {
            if (!kind.HasValue)
            {
                reason = "no lot is active: the player is on the neighbourhood screen, not in a lot";
                return false;
            }
            if (kind.Value < ActiveLotKind.Active || kind.Value > ActiveLotKind.Hollow)
            {
                reason = "active lot kind " + (int)kind.Value + " is not one of Active, Transit or"
                    + " Hollow, so it names no state this world can be in";
                return false;
            }
            if (kind.Value == ActiveLotKind.Transit)
            {
                if (activeLotNumber != NoLot)
                {
                    reason = "the transit lot is a street region, not house " + activeLotNumber
                        + "; while the avatar is walking, the world is in no numbered lot";
                    return false;
                }
                reason = null;
                return true;
            }
            if (activeLotNumber == NoLot)
            {
                reason = "a " + kind.Value + " lot owns the avatar but no lot number names it,"
                    + " so there is no lot to own it";
                return false;
            }
            reason = null;
            return true;
        }

        // ---------------------------------------------------------------- planning

        /// <summary>
        /// Plans a walk from one lot's door to another's over the road network, and reports why not
        /// when it cannot.
        ///
        /// <para>
        /// Pure with respect to this object: the walk in progress, if any, is untouched. A caller
        /// that wants a walk <em>started</em> wants <see cref="TryBeginTransit"/>, and a caller
        /// that only wants to know whether a destination is reachable - the neighbourhood screen
        /// greying out a lot - wants this and nothing else.
        /// </para>
        /// <para>
        /// The refusals, all of them answers rather than exceptions, because "can the player walk
        /// there" is the question being asked: the lot is not in this neighbourhood; it has no
        /// house, so it has no door to walk out of; its door is off the map or on a tile carrying
        /// no road; the two doors are on different connected pieces of road, which
        /// <see cref="NeighbourhoodQuery.RoadComponentAt"/> answers without a flood fill; or the
        /// search ran out of road. <paramref name="reason"/> is a sentence naming the case, and is
        /// <c>null</c> on success.
        /// </para>
        /// </summary>
        /// <param name="origin">The lot the walk starts at.</param>
        /// <param name="destination">The lot the walk ends at.</param>
        /// <param name="path">The planned walk, or <see cref="TransitPath.None"/> on a refusal.</param>
        /// <param name="reason">Why no walk was planned, or <c>null</c> when one was.</param>
        /// <returns>Whether a walk was planned.</returns>
        public bool TryPlanPath(int origin, int destination, out TransitPath path, out string reason)
        {
            path = TransitPath.None;

            int start;
            if (!TryResolveDoor(origin, "origin", out start, out reason)) return false;
            int goal;
            if (!TryResolveDoor(destination, "destination", out goal, out reason)) return false;

            if (start == goal)
            {
                //Not a special case in the search, just the shortest walk there is: the avatar is
                //already standing on the door it was asked to reach. A one-waypoint path is what
                //says that, and Advance reports arrival on the first step.
                path = new TransitPath(origin, destination,
                    new[] { new TransitWaypoint(start % _query.Width, start / _query.Width, origin) });
                reason = null;
                return true;
            }

            var from = _query.RoadComponentAt(start % _query.Width, start / _query.Width);
            var to = _query.RoadComponentAt(goal % _query.Width, goal / _query.Width);
            if (from != to)
            {
                //The query flood-filled the network when it was built, so this is the disconnection
                //test already done - and saying which components are involved is the difference
                //between "no route" and a bug report that can be acted on.
                reason = "lot " + origin + " and lot " + destination + " are on different road"
                    + " networks (component " + from + " and component " + to
                    + "), so there is nothing to walk between them";
                return false;
            }

            var waypoints = new List<TransitWaypoint>();
            if (!Search(start, goal, origin, destination, waypoints, out reason)) return false;

            path = new TransitPath(origin, destination, waypoints);
            reason = null;
            return true;
        }

        // ---------------------------------------------------------------- the walk

        /// <summary>
        /// Plans a walk and starts it. The state's origin and destination come from the path, and
        /// the avatar begins on the origin's door.
        /// </summary>
        /// <param name="origin">The lot the walk starts at.</param>
        /// <param name="destination">The lot the walk ends at.</param>
        /// <param name="path">The planned walk, or <see cref="TransitPath.None"/> on a refusal.</param>
        /// <param name="reason">Why no walk started, or <c>null</c> when one did.</param>
        /// <returns>Whether a walk is now in progress.</returns>
        public bool TryBeginTransit(int origin, int destination, out TransitPath path, out string reason)
        {
            if (!TryPlanPath(origin, destination, out path, out reason)) return false;
            return BeginTransit(path, out reason);
        }

        /// <summary>
        /// Starts an already-planned walk. Refused while another walk is in progress, because the
        /// transit lot holds one avatar and a second plan quietly replacing the first is how two of
        /// them end up driving the same sim.
        /// </summary>
        /// <param name="path">The walk to start.</param>
        /// <param name="reason">Why the walk did not start, or <c>null</c> when it did.</param>
        /// <returns>Whether a walk is now in progress.</returns>
        public bool BeginTransit(TransitPath path, out string reason)
        {
            if (path == null || !path.IsValid)
            {
                reason = "there is no planned walk to start";
                return false;
            }
            if (IsWalking)
            {
                reason = "a walk from lot " + _origin + " to lot " + _destination
                    + " is already in progress; a retarget has to cancel it first";
                return false;
            }

            //Remember where the avatar was, so a cancelled walk puts it back where it started
            //rather than leaving the world claiming a lot the avatar is standing on the street
            //outside of.
            _kindBeforeWalk = _kind;
            _lotBeforeWalk = _activeLot;
            _origin = path.OriginLot;
            _destination = path.DestinationLot;
            _path = path;
            _progress = 0.0;
            _kind = ActiveLotKind.Transit;
            _activeLot = NoLot;
            reason = null;
            return true;
        }

        /// <summary>
        /// Moves the avatar <paramref name="distanceTiles"/> further along the walk, and reports
        /// whether that step arrived at the destination's door.
        ///
        /// <para>
        /// Progress is clamped at the door: an overshoot stops on the destination rather than
        /// carrying the remainder past it, because what happens after arrival - the transition
        /// into the destination VM - is the caller's, and the transit lot's job ends the moment the
        /// avatar is on the door.
        /// </para>
        /// <para>
        /// Arrival is sticky. Once the avatar has arrived every further step keeps reporting
        /// arrival and moves nothing, so a caller that advances a frame's worth of walking twice -
        /// a fixed-step loop that catches up, a transition retried after a failure - cannot walk
        /// the avatar past the door or un-arrive it. A step that is not a positive number (zero, a
        /// negative distance, a NaN) is ignored and reports no arrival.
        /// </para>
        /// </summary>
        /// <param name="distanceTiles">How far to walk, in tiles.</param>
        /// <returns>Whether the avatar is on the destination's door after the step.</returns>
        public bool Advance(double distanceTiles)
        {
            if (_path == null) return false;
            if (!(distanceTiles > 0.0)) return false;

            var length = _path.Length;
            var next = _progress + distanceTiles;
            if (next >= length)
            {
                _progress = length;
                return true;
            }
            _progress = next;
            return false;
        }

        /// <summary>
        /// Completes a walk that has actually arrived: the transit lot gives the avatar up and the
        /// world names the destination. Refused mid-street, which is
        /// <see cref="CancelTransit"/>'s job rather than this one's.
        /// </summary>
        /// <param name="reason">Why the walk did not complete, or <c>null</c> when it did.</param>
        /// <returns>Whether a walk completed.</returns>
        public bool CompleteTransit(out string reason)
        {
            if (_path == null)
            {
                reason = "there is no walk in progress";
                return false;
            }
            if (_progress < _path.Length)
            {
                reason = "the avatar is still "
                    + ((double)_path.Length - _progress).ToString("0.###", CultureInfo.InvariantCulture)
                    + " tile(s) short of lot " + _destination + "'s door";
                return false;
            }

            var destination = _destination;
            ClearWalk();
            _kind = ActiveLotKind.Active;
            _activeLot = destination;
            reason = null;
            return true;
        }

        /// <summary>
        /// Abandons a walk and puts the world's bookkeeping back where it was before it started:
        /// the avatar is on the street, not on a door, so the caller has to put it somewhere. This
        /// is the path a failed transition takes, and the path a player who cancels a walk takes.
        /// </summary>
        /// <param name="reason">Why the walk was not cancelled, or <c>null</c> when it was.</param>
        /// <returns>Whether a walk was in progress and has been cancelled.</returns>
        public bool CancelTransit(out string reason)
        {
            if (_path == null)
            {
                reason = "there is no walk in progress";
                return false;
            }

            ClearWalk();
            _kind = _kindBeforeWalk;
            _activeLot = _lotBeforeWalk;
            reason = null;
            return true;
        }

        /// <summary>One line for a log, or for a failing assertion's message.</summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("transit lot over ").Append(_query)
              .Append("; owner ").Append(_kind);
            if (_activeLot != NoLot) sb.Append(" lot ").Append(_activeLot);
            else sb.Append(" (no numbered lot)");
            if (_path != null)
            {
                sb.Append("; ").Append(_progress.ToString("0.###", CultureInfo.InvariantCulture))
                  .Append('/').Append(_path.Length).Append(" tile(s) from lot ").Append(_origin)
                  .Append(" to lot ").Append(_destination);
            }
            return sb.ToString();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }

        // ---------------------------------------------------------------- the search

        private void ClearWalk()
        {
            _origin = NoLot;
            _destination = NoLot;
            _path = null;
            _progress = 0.0;
        }

        private bool TryResolveDoor(int lotNumber, string role, out int index, out string reason)
        {
            NeighbourhoodLot lot;
            if (!_query.TryGetLot(lotNumber, out lot))
            {
                index = 0;
                reason = "lot " + lotNumber + " is not in this neighbourhood, so it has no "
                    + role + " to walk from or to";
                return false;
            }

            var entrance = _query.EntranceFor(lotNumber);
            if (entrance == null)
            {
                index = 0;
                reason = "lot " + lotNumber + " has no house, so it has no door to walk "
                    + (role == "origin" ? "out of" : "into");
                return false;
            }
            if (!_query.IsInBounds(entrance.X, entrance.Y))
            {
                index = 0;
                reason = "lot " + lotNumber + "'s door is at (" + entrance.X + "," + entrance.Y
                    + "), which is off the map";
                return false;
            }
            if (!_query.IsRoad(entrance.X, entrance.Y))
            {
                index = 0;
                reason = "lot " + lotNumber + "'s door is at (" + entrance.X + "," + entrance.Y
                    + "), which carries no road, so the " + role + " of a walk is not on the network";
                return false;
            }

            index = entrance.Y * _query.Width + entrance.X;
            reason = null;
            return true;
        }

        /// <summary>
        /// A* over the road tiles, four-connected and unit-cost, Manhattan heuristic.
        ///
        /// <para>
        /// The frontier is a binary min-heap ordered by (cost + heuristic, heuristic, tile index).
        /// That triple is a total order over the tiles, and the neighbours of a tile are expanded in
        /// a fixed direction order, so the sequence of expansions - and therefore the route - is a
        /// function of the query alone. A tile is closed the first time it is popped, which makes a
        /// better-cost duplicate that is still in the heap a no-op rather than a re-expansion.
        /// </para>
        /// </summary>
        private bool Search(int start, int goal, int origin, int destination,
            List<TransitWaypoint> waypoints, out string reason)
        {
            var width = _query.Width;
            var height = _query.Height;
            var count = _query.TileCount;

            //Three tile-sized tables, allocated per search and thrown away with it. The cost table
            //is an int because every step costs exactly one tile, so the shortest route can never
            //exceed the tile count and int.MaxValue is an honest "not reached yet" rather than a
            //magic number; a 512x512 neighbourhood is 262,144 tiles, so this is a few megabytes on
            //the largest map the game ever generates and nothing at all on a test-sized one.
            var cost = new int[count];
            var cameFrom = new int[count];
            var closed = new bool[count];
            for (int i = 0; i < count; i++)
            {
                cost[i] = Unreached;
                cameFrom[i] = -1;
            }

            var open = new OpenSet();
            cost[start] = 0;
            var startHeuristic = Heuristic(start, goal, width);
            open.Push(start, startHeuristic, startHeuristic);

            while (open.Count > 0)
            {
                var current = open.Pop();
                if (closed[current]) continue;
                closed[current] = true;
                if (current == goal)
                {
                    Reconstruct(start, goal, origin, destination, cameFrom, waypoints);
                    reason = null;
                    return true;
                }

                var x = current % width;
                var y = current / width;
                for (int d = 0; d < StepX.Length; d++)
                {
                    var nx = x + StepX[d];
                    var ny = y + StepY[d];
                    if (nx < 0 || ny < 0 || nx >= width || ny >= height) continue;
                    var next = ny * width + nx;
                    if (closed[next] || !_query.IsRoad(nx, ny)) continue;

                    var nextCost = cost[current] + 1;
                    if (nextCost >= cost[next]) continue;
                    cost[next] = nextCost;
                    cameFrom[next] = current;
                    var remaining = Heuristic(next, goal, width);
                    open.Push(next, nextCost + remaining, remaining);
                }
            }

            reason = "no walkable route from lot " + origin + "'s door to lot " + destination
                + "'s door; the search ran out of road before it reached the far side";
            return false;
        }

        private void Reconstruct(int start, int goal, int origin, int destination, int[] cameFrom,
            List<TransitWaypoint> waypoints)
        {
            var width = _query.Width;
            waypoints.Clear();
            var index = goal;
            while (index != -1)
            {
                waypoints.Add(new TransitWaypoint(index % width, index / width, NoLot));
                if (index == start) break;
                index = cameFrom[index];
            }
            waypoints.Reverse();

            //The two ends are doors and the middle is street, so the lot numbers go on the ends -
            //and on a walk from a lot to itself the single waypoint is both, which is exactly what
            //it is.
            waypoints[0] = new TransitWaypoint(waypoints[0].X, waypoints[0].Y, origin);
            waypoints[waypoints.Count - 1] = new TransitWaypoint(
                waypoints[waypoints.Count - 1].X, waypoints[waypoints.Count - 1].Y, destination);
        }

        /// <summary>
        /// Manhattan distance, which never over-estimates on a four-connected unit-cost grid - the
        /// condition A* needs to still return a shortest route when it reaches the goal.
        /// </summary>
        private static int Heuristic(int from, int to, int width)
        {
            var dx = Math.Abs(from % width - to % width);
            var dy = Math.Abs(from / width - to / width);
            return dx + dy;
        }

        /// <summary>
        /// The A* frontier: a binary min-heap over tiles, ordered by
        /// (cost + heuristic, heuristic, tile index) so that the order does not depend on the
        /// order tiles happened to be pushed in.
        /// </summary>
        private sealed class OpenSet
        {
            private struct Node
            {
                public int Index;
                public int Score;
                public int Heuristic;
            }

            private Node[] _items = new Node[64];
            private int _count;

            public int Count
            {
                get { return _count; }
            }

            public void Push(int index, int score, int heuristic)
            {
                if (_count == _items.Length) Array.Resize(ref _items, _items.Length * 2);
                var node = new Node { Index = index, Score = score, Heuristic = heuristic };

                var i = _count++;
                while (i > 0)
                {
                    var parent = (i - 1) / 2;
                    if (!Less(node, _items[parent])) break;
                    _items[i] = _items[parent];
                    i = parent;
                }
                _items[i] = node;
            }

            public int Pop()
            {
                var top = _items[0];
                var last = _items[--_count];
                if (_count > 0)
                {
                    var i = 0;
                    while (true)
                    {
                        var left = 2 * i + 1;
                        if (left >= _count) break;
                        var right = left + 1;
                        var child = right < _count && Less(_items[right], _items[left]) ? right : left;
                        if (!Less(_items[child], last)) break;
                        _items[i] = _items[child];
                        i = child;
                    }
                    _items[i] = last;
                }
                return top.Index;
            }

            private static bool Less(Node a, Node b)
            {
                if (a.Score != b.Score) return a.Score < b.Score;
                if (a.Heuristic != b.Heuristic) return a.Heuristic < b.Heuristic;
                return a.Index < b.Index;
            }
        }
    }
}
