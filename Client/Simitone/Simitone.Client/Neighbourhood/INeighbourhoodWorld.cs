/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content;
using FSO.SimAntics;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Read-only view of the time a neighbourhood is at.
    ///
    /// <para>
    /// §15's preparation item. Scope A has no clock of its own: it does not advance time, and
    /// two lots in the neighbourhood do not share one. What it has is the <em>active lot's</em>
    /// clock, and it reads that - so anything the neighbourhood renders that depends on the hour
    /// (lighting, sky, whether a shop is open) is reading the same numbers the lot is, through
    /// one named seam rather than by reaching into <c>VM.Context.Clock</c> from three places.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing writes a clock.</b> There is no setter, no <c>Advance</c>, no
    /// <c>SetHour</c>, and the Scope A implementations are structurally incapable of one: the
    /// interface exposes properties only, and the mirror below holds a resolver for the live
    /// <c>VMClock</c> rather than a clock it could own. A later scope that wants an
    /// authoritative neighbourhood clock implements this interface again over its own store.
    /// </para>
    /// </summary>
    public interface INeighbourhoodClock
    {
        /// <summary>
        /// Is there a lot loaded whose clock can be read? <c>false</c> on the neighbourhood
        /// screen, where no lot is active - every other member is then meaningless and reads 0.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>0..23, from the active lot's <c>VMClock.Hours</c>.</summary>
        int Hours { get; }

        /// <summary>0..59, from the active lot's <c>VMClock.Minutes</c>.</summary>
        int Minutes { get; }

        /// <summary>
        /// Sub-minute progress, in the lot's own <c>MinuteFractions</c> units (0..
        /// <c>TicksPerMinute</c>). Not normalised: callers that want a fraction divide by
        /// <see cref="TicksPerMinute"/>, and clamping here would hide a misconfigured lot.
        /// </summary>
        int MinuteFractions { get; }

        /// <summary>
        /// The lot's sub-minute unit, from <c>VMClock.TicksPerMinute</c>. Zero when unavailable.
        /// </summary>
        int TicksPerMinute { get; }

        /// <summary>1..30, from the active lot's <c>VMClock.DayOfMonth</c>.</summary>
        int DayOfMonth { get; }

        /// <summary>1..12, from the active lot's <c>VMClock.Month</c>.</summary>
        int Month { get; }

        /// <summary>The year, from the active lot's <c>VMClock.Year</c>.</summary>
        int Year { get; }

        /// <summary>
        /// Whole seconds within the current minute, derived as
        /// <c>MinuteFractions * 60 / TicksPerMinute</c>. Zero rather than a division by zero
        /// when the lot has no sub-minute unit.
        /// </summary>
        int Seconds { get; }

        /// <summary>The active lot's elapsed tick count, or 0 when unavailable.</summary>
        long Ticks { get; }
    }

    /// <summary>
    /// The clock of a neighbourhood with no active lot: everything zero, and
    /// <see cref="IsAvailable"/> false so a caller can tell "midnight" from "no clock".
    /// </summary>
    /// <remarks>
    /// This is what the neighbourhood screen - which has no lot - reads, and what a caller gets
    /// if it builds a world without naming a clock. It is the honest answer rather than a
    /// stand-in for midnight.
    /// </remarks>
    public sealed class UnavailableNeighbourhoodClock : INeighbourhoodClock
    {
        /// <summary>The shared instance. The type holds no state.</summary>
        public static readonly UnavailableNeighbourhoodClock Instance = new UnavailableNeighbourhoodClock();

        private UnavailableNeighbourhoodClock()
        {
        }

        public bool IsAvailable
        {
            get { return false; }
        }

        public int Hours
        {
            get { return 0; }
        }

        public int Minutes
        {
            get { return 0; }
        }

        public int MinuteFractions
        {
            get { return 0; }
        }

        public int TicksPerMinute
        {
            get { return 0; }
        }

        public int DayOfMonth
        {
            get { return 0; }
        }

        public int Month
        {
            get { return 0; }
        }

        public int Year
        {
            get { return 0; }
        }

        public int Seconds
        {
            get { return 0; }
        }

        public long Ticks
        {
            get { return 0L; }
        }
    }

    /// <summary>
    /// Mirrors a live lot's <c>VM.Context.Clock</c> through <see cref="INeighbourhoodClock"/>,
    /// without ever writing to it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It holds a resolver, not a clock, for two reasons. The screen does not have a lot when it
    /// builds the world and does have one by the time anything renders, so the same mirror
    /// instance keeps working across that transition without being rebuilt. And because the
    /// mirror cannot reach a <c>VMClock</c> it did not borrow, it cannot advance it - which is
    /// the property §15 asks for, enforced by the type rather than by convention.
    /// </para>
    ///
    /// <para>
    /// Every read is defensive. A neighbourhood can be drawn while a lot is being torn down, and
    /// a <c>null</c> lot VM in that window must read as "no clock", not as a crash.
    /// </para>
    /// </remarks>
    public sealed class LotClockMirror : INeighbourhoodClock
    {
        private readonly Func<VMClock> _source;

        /// <summary>
        /// Mirror the clock <paramref name="source"/> currently resolves to. The resolver is
        /// consulted on every read, so it may return a different clock - or none - over time.
        /// </summary>
        /// <param name="source">
        /// Resolver for the active lot's clock, or <c>null</c> for a permanently unavailable one.
        /// </param>
        public LotClockMirror(Func<VMClock> source)
        {
            _source = source;
        }

        /// <summary>A mirror of a known clock instance, for tests and for callers that hold one.</summary>
        public LotClockMirror(VMClock source)
            : this(() => source)
        {
        }

        private VMClock Clock
        {
            get
            {
                if (_source == null) return null;
                try
                {
                    return _source();
                }
                catch (Exception)
                {
                    //a screen tearing a lot down can throw here; "no clock" is the answer
                    return null;
                }
            }
        }

        public bool IsAvailable
        {
            get { return Clock != null; }
        }

        public int Hours
        {
            get { var c = Clock; return c == null ? 0 : c.Hours; }
        }

        public int Minutes
        {
            get { var c = Clock; return c == null ? 0 : c.Minutes; }
        }

        public int MinuteFractions
        {
            get { var c = Clock; return c == null ? 0 : c.MinuteFractions; }
        }

        public int TicksPerMinute
        {
            get { var c = Clock; return c == null ? 0 : c.TicksPerMinute; }
        }

        public int DayOfMonth
        {
            get { var c = Clock; return c == null ? 0 : c.DayOfMonth; }
        }

        public int Month
        {
            get { var c = Clock; return c == null ? 0 : c.Month; }
        }

        public int Year
        {
            get { var c = Clock; return c == null ? 0 : c.Year; }
        }

        public int Seconds
        {
            get
            {
                var c = Clock;
                //VMClock divides by TicksPerMinute unguarded; a lot that has not been told its
                //rate would throw there, and the neighbourhood must not be what surfaces that
                if (c == null || c.TicksPerMinute <= 0) return 0;
                return (c.MinuteFractions * 60) / c.TicksPerMinute;
            }
        }

        public long Ticks
        {
            get { var c = Clock; return c == null ? 0L : c.Ticks; }
        }
    }

    /// <summary>
    /// The facade a neighbourhood screen will eventually own: which lot is active, what time it
    /// is, and what the layout says about each lot.
    ///
    /// <para>
    /// §15's preparation item, introduced here with an implementation that does nothing a
    /// viewer could notice. Scope A calls <see cref="Layout"/>, <see cref="Lots"/>,
    /// <see cref="TryGetLot"/>, <see cref="ActiveLotNumber"/> and <see cref="Clock"/>, and calls
    /// <see cref="Tick"/> from nowhere at all - it exists so the shape is settled, and its
    /// Scope A body returns <c>false</c> and changes nothing.
    /// </para>
    ///
    /// <para>
    /// <b>Part B grew it (§3), and growing it was meant to cost nothing.</b> The four members added
    /// with <see cref="LotStore"/>, <see cref="Transit"/>, <see cref="Query"/> and
    /// <see cref="WorldEnabled"/> are the stores §5.2-§5.4 say a world has, and the one new method,
    /// <see cref="SetActiveLot"/>, is the one path for changing which lot the player is in. Every
    /// one of them is a property or a single call that <see cref="ReadOnlyNeighbourhoodWorld"/>
    /// answers without doing anything, and the test is that asserting so is the only way the growth
    /// shows up: no renderer reads any of the new members, and deleting the type that implements
    /// them would change no pixels. <see cref="SetActiveLot"/> is the member to be most suspicious
    /// of - it is a write, on an interface whose other new members are reads - and it is here
    /// precisely so that the write happens in one place when it does happen, rather than in a
    /// screen, a provider and a lot-switch handler separately.
    /// </para>
    /// </summary>
    public interface INeighbourhoodWorld
    {
        /// <summary>The neighbourhood slot this world belongs to.</summary>
        int Slot { get; }

        /// <summary>The world this screen is drawing. Never null.</summary>
        NeighbourhoodLayout Layout { get; }

        /// <summary>The neighbourhood's clock. Never null; unavailable rather than null when no lot is loaded.</summary>
        INeighbourhoodClock Clock { get; }

        /// <summary>
        /// The lot the player is standing in, or 0 when the player is on the neighbourhood
        /// screen and therefore in no lot at all. Zero is not a lot number.
        /// </summary>
        int ActiveLotNumber { get; }

        /// <summary>Every lot in the layout, in ascending lot-number order.</summary>
        IReadOnlyList<NeighbourhoodLot> Lots { get; }

        /// <summary>The lot with this number, or <c>null</c>.</summary>
        bool TryGetLot(int number, out NeighbourhoodLot lot);

        /// <summary>
        /// The lots that have contents in memory (§5.3). Never null, and empty for a world that has
        /// not been entered - which includes <see cref="ReadOnlyNeighbourhoodWorld"/>, whose store
        /// exists to satisfy this member and is written by nobody.
        /// </summary>
        /// <remarks>
        /// A store rather than a method per state because a caller that has to ask "is lot 22
        /// resident" should not also have to ask "and what does <c>false</c> mean". Handover
        /// between the three states is <see cref="SetActiveLot"/>'s job and not this property's.
        /// </remarks>
        LotStore LotStore { get; }

        /// <summary>
        /// The sims currently between lots (§5.4). Never null, and empty until something travels.
        /// </summary>
        TransitStore Transit { get; }

        /// <summary>
        /// The spatial substrate: tiles, entrances, road components and on-foot reachability, over
        /// the layout and the generated map this world was built with.
        /// </summary>
        /// <remarks>
        /// Built once and read forever, so a renderer, a pathfinder and a test can hold the same
        /// instance and agree by construction. Never null.
        /// </remarks>
        NeighbourhoodQuery Query { get; }

        /// <summary>
        /// Is the world switched on - the §6 <c>NeighbourhoodWorld</c> flag, per world rather than
        /// per process? <c>false</c> means <see cref="Tick"/> advances nothing.
        /// </summary>
        /// <remarks>
        /// <b>Off must be inert, not broken.</b> A disabled world still answers every question it
        /// answered before it was disabled - the layout, the lots, the clock - because "the flag is
        /// off" has to mean "this build is what it was before Part B", and a world that returned
        /// nothing with the flag off would be a different, not an older, product. So the flag gates
        /// simulation only, and the acceptance test is the byte-identity of the layout with it off.
        /// </remarks>
        bool WorldEnabled { get; }

        /// <summary>
        /// Move the player into <paramref name="lotNumber"/>, or out of every lot when it is 0.
        /// </summary>
        /// <remarks>
        /// The single path for changing the active lot (§3's "one path for changing the active
        /// lot"). A world with no notion of lot contents answers it by recording the number and
        /// doing nothing else; <see cref="ReadOnlyNeighbourhoodWorld"/> goes further and does
        /// nothing at all, because its <see cref="ActiveLotNumber"/> is fixed at construction.
        /// </remarks>
        /// <param name="lotNumber">The lot to be in, or 0 for the neighbourhood screen.</param>
        void SetActiveLot(int lotNumber);

        /// <summary>
        /// Advance the world by <paramref name="deltaSeconds"/>.
        /// </summary>
        /// <returns>
        /// Whether anything changed and therefore needs redrawing. Always <c>false</c> in
        /// Scope A, which has nothing to advance, and <c>false</c> in a Part B world whose
        /// <see cref="WorldEnabled"/> is off.
        /// </returns>
        bool Tick(double deltaSeconds);
    }

    /// <summary>
    /// Scope A's <see cref="INeighbourhoodWorld"/>: a thin, immutable adapter over a
    /// <see cref="NeighbourhoodLayout"/>, an <see cref="INeighbourhoodClock"/>, and - when the
    /// caller has a content manager - the provider-backed <see cref="ILotContentSource"/>.
    ///
    /// <para>
    /// Everything it hands out is somebody else's data. The layout is not copied (it is already
    /// immutable from a reader's point of view), the clock is mirrored rather than owned, and
    /// the lot content is a read model. There is no state here to corrupt and no method that
    /// writes: <see cref="Tick"/> returns <c>false</c> and touches nothing, which is what makes
    /// the seam safe to leave in place.
    /// </para>
    ///
    /// <para>
    /// <b>The four stores Part B added to the interface are present and inert.</b>
    /// <see cref="LotStore"/> and <see cref="Transit"/> are per-instance empties that nothing here
    /// writes to and that a renderer never reads; per-instance rather than shared, so a caller that
    /// did write to one cannot be seen by another world. <see cref="Query"/> is the one member that
    /// has to build something, and it is built <em>on first use</em> - Scope A's screen never asks,
    /// so a read-only world costs nothing for it, which is the whole point of the laziness.
    /// <see cref="WorldEnabled"/> is <c>false</c>, so <see cref="Tick"/> is inert by two
    /// independent routes: there is nothing to advance and the flag that would allow it is off.
    /// </para>
    /// </summary>
    public sealed class ReadOnlyNeighbourhoodWorld : INeighbourhoodWorld
    {
        private readonly NeighbourhoodLayout _layout;
        private readonly INeighbourhoodClock _clock;
        private readonly int _activeLotNumber;
        private readonly ILotContentSource _lotContent;

        /// <summary>
        /// The two stores behind <see cref="LotStore"/> and <see cref="Transit"/>. Held per instance
        /// and written by nobody; see the class remarks.
        /// </summary>
        private readonly LotStore _lotStore = new LotStore();
        private readonly TransitStore _transit = new TransitStore();

        /// <summary>
        /// The query behind <see cref="Query"/>, built on first use and cached. Null until then,
        /// which is what keeps a read-only world free of a 512x512 blank map it will never use.
        /// </summary>
        private NeighbourhoodQuery _query;

        /// <summary>
        /// A world with a layout and a clock and no content manager - the neighbourhood screen
        /// before any lot has been opened, and what the tests use.
        /// </summary>
        /// <param name="layout">The layout to expose. A null layout becomes an empty one.</param>
        /// <param name="clock">The clock to mirror. A null clock becomes an unavailable one.</param>
        /// <param name="activeLotNumber">The lot the player is in, or 0 for none.</param>
        public ReadOnlyNeighbourhoodWorld(NeighbourhoodLayout layout, INeighbourhoodClock clock, int activeLotNumber = 0)
        {
            _layout = layout ?? NeighbourhoodLayout.Empty(0, 0, 0);
            _clock = clock ?? UnavailableNeighbourhoodClock.Instance;
            _activeLotNumber = activeLotNumber;
        }

        /// <summary>
        /// A world backed by a live content manager, which additionally exposes the
        /// neighbourhood's lot content through <see cref="LotContent"/>.
        /// </summary>
        /// <param name="content">The content manager, or null for a content-free world.</param>
        /// <param name="mode">The neighbourhood mode (the 1-based <c>LotLocations.iff</c> STR id).</param>
        /// <param name="layout">The layout to expose.</param>
        /// <param name="clock">The clock to mirror.</param>
        /// <param name="activeLotNumber">The lot the player is in, or 0 for none.</param>
        public ReadOnlyNeighbourhoodWorld(Content content, int mode, NeighbourhoodLayout layout,
            INeighbourhoodClock clock, int activeLotNumber = 0)
            : this(layout, clock, activeLotNumber)
        {
            if (content != null && content.Neighborhood != null)
            {
                _lotContent = new TS1LotContentSource(content.Neighborhood, mode);
            }
        }

        public int Slot
        {
            get { return _layout.Slot; }
        }

        public NeighbourhoodLayout Layout
        {
            get { return _layout; }
        }

        public INeighbourhoodClock Clock
        {
            get { return _clock; }
        }

        public int ActiveLotNumber
        {
            get { return _activeLotNumber; }
        }

        public IReadOnlyList<NeighbourhoodLot> Lots
        {
            get { return _layout.Lots; }
        }

        /// <summary>How many lots the layout places. A shortcut for the common "is there anything
        /// to draw" question, so a caller does not have to count a list itself.</summary>
        public int LotCount
        {
            get { return _layout.LotCount; }
        }

        /// <summary>
        /// The neighbourhood's own lot data, or <c>null</c> when this world was built without a
        /// content manager. Present so a later scope can ask about a lot's house without
        /// re-opening the provider; Scope A's renderer does not ask.
        /// </summary>
        public ILotContentSource LotContent
        {
            get { return _lotContent; }
        }

        /// <summary>Is there a content manager behind this world?</summary>
        public bool HasLotContent
        {
            get { return _lotContent != null; }
        }

        public bool TryGetLot(int number, out NeighbourhoodLot lot)
        {
            lot = _layout.FindLot(number);
            return lot != null;
        }

        public LotStore LotStore
        {
            get { return _lotStore; }
        }

        public TransitStore Transit
        {
            get { return _transit; }
        }

        /// <summary>
        /// A query over this world's layout and a blank map of the layout's own size, built the
        /// first time it is asked for.
        /// </summary>
        /// <remarks>
        /// The map is the honest answer for a read-only world: it has no generated map, because the
        /// screen that has one passes it to the map generator rather than through here. What a
        /// caller can rely on is the lot half of the query - which lots exist, which are
        /// bulldozed, which are reachable from which - since that is derived from the layout alone.
        /// A 512-tile layout makes this a few megabytes, which is why nothing in Scope A asks.
        /// </remarks>
        public NeighbourhoodQuery Query
        {
            get
            {
                if (_query == null)
                {
                    _query = new NeighbourhoodQuery(_layout, NeighbourhoodMap.Create(_layout.MapSize));
                }
                return _query;
            }
        }

        /// <summary>Always <c>false</c>: a read-only world has no simulation to switch on.</summary>
        public bool WorldEnabled
        {
            get { return false; }
        }

        /// <summary>
        /// Does nothing, and does not even record the number. The active lot of a read-only world is
        /// whatever its constructor was told, because a world that is somebody else's data does not
        /// get to change it - see the remarks on <see cref="INeighbourhoodWorld.SetActiveLot"/>.
        /// </summary>
        /// <param name="lotNumber">Ignored.</param>
        public void SetActiveLot(int lotNumber)
        {
        }

        /// <summary>
        /// Does nothing and says so. Scope A has no cross-lot simulation, no hollow lots and no
        /// off-screen ticking, so there is nothing for a world tick to do; this is the §15 hook,
        /// and removing the method (and any call to it) changes nothing about how the
        /// neighbourhood looks or plays.
        /// </summary>
        public bool Tick(double deltaSeconds)
        {
            return false;
        }

        public override string ToString()
        {
            return "read-only world " + _layout + (HasLotContent ? " +content" : string.Empty);
        }
    }
}
