/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FSO.SimAntics;
using FSO.SimAntics.Marshals;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Part B's world: the §3 object that owns one neighbourhood's time, its stored lots, its
    /// travelling sims and its spatial query, and that has exactly one way to change which lot the
    /// player is in.
    ///
    /// <para>
    /// <b>Why it is a type and not a bag of statics.</b> §5.1 says one world per slot per mode, and
    /// a process-wide "the world" would make a second neighbourhood - and a test - impossible. So
    /// every fact here belongs to an instance: the slot, the seed, the clock, the lot store, the
    /// transit table, and the one mutable number, the active lot.
    /// </para>
    ///
    /// <para>
    /// <b>What it owns, and what it borrows.</b> The <see cref="NeighbourhoodLayout"/> and the
    /// <see cref="NeighbourhoodMap"/> are handed in and kept, not copied: both are already
    /// immutable from a reader's point of view, and copying either would mean two lot lists or two
    /// 512x512 arrays to keep in step. The <see cref="WorldClock"/>, the <see cref="LotStore"/> and
    /// the <see cref="TransitStore"/> are its own, and the <see cref="NeighbourhoodQuery"/> is built
    /// once from the two it was given. Nothing here reaches for a content manager, a graphics
    /// device, a file or a VM, which is why the whole of B1 is testable headless.
    /// </para>
    ///
    /// <para>
    /// <b>Time is this world's, and the lots are projections of it.</b> §5.2's rule is that a lot's
    /// clock is seeded from the world on load and pushes back on save or tick, so the neighbourhood
    /// keeps time while the player is standing in no lot at all. <see cref="Tick"/> is the only
    /// thing here that advances it - nothing else in the class writes to <see cref="WorldClock"/> -
    /// and <see cref="SyncClockFrom"/> / <see cref="SyncClockTo"/> are the two halves of the lot
    /// handshake, exposed but not called by anything in this file.
    /// </para>
    ///
    /// <para>
    /// <b>Off means off.</b> <see cref="WorldEnabled"/> defaults to <c>false</c>, and a disabled
    /// world's <see cref="Tick"/> returns <c>false</c> without reading the clock at all - not
    /// "ticks a disabled world slower", which is the failure mode a flag on a tick is usually
    /// written to invite. Everything else still answers, because a world that stopped answering
    /// would be a different product rather than an older one.
    /// </para>
    ///
    /// <para>
    /// <b>B6 hangs off one flag, and the flag is asked for where the branch is taken.</b>
    /// <see cref="Tick"/> delegates to the <see cref="OffscreenSimulator"/> it owns when
    /// <see cref="NeighbourhoodWorldFlags.IsOn"/> says
    /// <see cref="NeighbourhoodWorldFlag.Offscreen"/> is on, and otherwise runs the
    /// clock-and-nothing-else path it has always run - same arithmetic, same truncation, same
    /// bounds, no simulator constructed and no record created. That is §7's "off ⇒ the previous
    /// stage's behaviour", and it is a property of the branch rather than a promise about it: with
    /// the flag off there is no off-screen object to have done anything.
    /// </para>
    ///
    /// <para>
    /// <b>Session state is a value, not a file.</b> <see cref="SaveWorldState"/> and
    /// <see cref="ApplyWorldState"/> are the two halves of B7's schema-2 round-trip and they deal
    /// in <see cref="NeighbourhoodWorldState"/>, which is data. Nothing here opens, reads or writes
    /// a file: <c>AtomicFileCommit</c> and the sidecar are the caller's, and a world that knew how
    /// to save itself would be saving itself at moments its owner did not choose.
    /// </para>
    ///
    /// <para>
    /// <b>B8 hangs off the same flag as B1, and the same rule: asked for at the branch.</b> The
    /// world's <see cref="NeighbourhoodWorldFeatures"/> - one sun, one sky, one weather, one idea of
    /// who is visiting - is built on first ask and re-read by <see cref="Tick"/>, but only when
    /// <see cref="NeighbourhoodWorldFlags.IsOn"/> says <see cref="NeighbourhoodWorldFlag.World"/>
    /// is on. With it off, no features object is ever constructed, none is ever ticked, and
    /// <see cref="ToString"/>, the tick arithmetic and every other observable are exactly what they
    /// were before B8. §7's rule is a property of the branch, not a promise about it: with the
    /// flag off there is no features object in the world to have done anything.
    /// </para>
    ///
    /// <para>
    /// <b>The features are re-read, not ticked, and that is the load-bearing detail.</b>
    /// <see cref="NeighbourhoodWorldFeatures.Tick"/> advances the clock itself, and this class has
    /// already advanced it by the time it gets there - so calling it would run the neighbourhood at
    /// double time the moment the flag was switched on, which is precisely the failure
    /// <see cref="Tick"/>'s own remarks warn about for the off-screen path. So the world keeps the
    /// one authority for time, ticks it as it always has, and then calls
    /// <see cref="NeighbourhoodWorldFeatures.Refresh"/> to re-read what the new hour looks like.
    /// The consequence is the one worth having: <see cref="Features"/> shares this world's
    /// <see cref="Clock"/> instance rather than shadowing it, so the weather cannot end up on a
    /// different clock from the Sims.
    /// </para>
    ///
    /// <para>
    /// <b>What the lot view reads.</b> <see cref="CurrentLighting"/> is the whole of B8's drawable
    /// answer - sun direction, sun colour, fill and background colour, fog colour and distance, sky
    /// tint and strip - and it is a pure function of <see cref="Features"/>' cached state, so it is
    /// recomputed on read rather than stored, and cannot go stale.
    /// <see cref="Lighting"/>, <see cref="Weather"/> and <see cref="Visitors"/> pass B8's own values
    /// through for a caller that wants the model's vocabulary rather than the lot view's. All four
    /// answer for a world whose features were never built - <see cref="CurrentLighting"/> as
    /// <c>default</c>, the others as their own defaults and an empty list - because a world that
    /// stopped answering would be a different product rather than an older one, and because
    /// "nothing has been derived yet" is a better answer than an exception on the first frame of a
    /// lot load. What a caller must *not* do is read a zeroed <see cref="CurrentLighting"/> and
    /// apply it: the flag is the gate, not the value.
    /// </para>
    ///
    /// <para>
    /// <b>Thread affinity: the game thread, only</b> (§7). One world, one owner, one writer, and no
    /// locking, for the same reason <see cref="LotStore"/> and <see cref="TransitStore"/> decline
    /// it.
    /// </para>
    /// </summary>
    public sealed class SimitoneNeighbourhoodWorld : INeighbourhoodWorld
    {
        /// <summary>
        /// The lot number meaning "the player is on the neighbourhood screen, in no lot". Not a lot
        /// number - the same convention as <see cref="INeighbourhoodWorld.ActiveLotNumber"/> and
        /// <see cref="NeighbourhoodWorldState.ActiveLot"/>.
        /// </summary>
        public const int NoActiveLot = 0;

        /// <summary>
        /// The most sub-minute ticks one <see cref="Tick"/> call will spend. A longer delta is
        /// clamped rather than obeyed, because a world tick is a frame-loop operation and a caller
        /// with a nonsense delta should get a wrong clock rather than a game that has stopped
        /// responding. See the remarks on <see cref="Tick"/>.
        /// </summary>
        /// <remarks>
        /// <b>The number is larger than the caption it used to carry, and that is worth knowing
        /// before anyone relies on it.</b> It is <c>TicksPerMinute * 60 * 60 * 10</c>, which counts
        /// sub-minute ticks as though each were a second: 1,080,000 ticks, which at TS1's thirty
        /// ticks to the game minute is 36,000 game minutes - six hundred game hours, or twenty-five
        /// days of real time. Ten game hours would be 18,000. The value is left exactly as it was
        /// found, because B1's own test pins the clamp against this constant by name and the
        /// arithmetic is not this milestone's to re-tune; the doc said "ten game hours" and the doc
        /// was wrong. A caller that wants a shorter frame-loop guard should clamp the delta itself.
        /// </remarks>
        public const long MaxTicksPerCall = WorldClock.TicksPerMinuteTs1 * 60L * 60L * 10L;

        private readonly NeighbourhoodLayout _layout;
        private readonly NeighbourhoodMap _map;
        private readonly NeighbourhoodQuery _query;
        private readonly WorldClock _clock = new WorldClock();
        private readonly LotStore _lotStore = new LotStore();
        private readonly TransitStore _transit = new TransitStore();

        /// <summary>
        /// The off-screen simulator, or <c>null</c> until somebody asks for <see cref="Offscreen"/>.
        /// Not built in the constructor because it is dead weight for a world whose capability is
        /// off, and "off" is where every world in a Part A build lives - see the remarks on
        /// <see cref="Offscreen"/>.
        /// </summary>
        private OffscreenSimulator _offscreen;

        /// <summary>
        /// What the last <see cref="NeighbourhoodWorldFlag.Offscreen"/> tick did, or <c>null</c> if
        /// there has not been one. Kept because the simulator's result says more than a <c>bool</c>
        /// can - how many lots, how many steps, whether a backlog was deferred - and a caller
        /// drawing or logging the world wants those without running a tick to ask.
        /// </summary>
        private OffscreenTickResult _lastOffscreenTick;

        /// <summary>
        /// B8's world features, or <c>null</c> until the <see cref="NeighbourhoodWorldFlag.World"/>
        /// capability has asked for them. Not built in the constructor, for the reason
        /// <see cref="_offscreen"/> gives: a Part A world has no reason to derive a weather stream
        /// and a visitor list, and a world whose capability is off must not pay for either.
        /// </summary>
        private NeighbourhoodWorldFeatures _features;

        /// <summary>
        /// Nobody is about, shared by every world that has not derived its visitor list. A static
        /// empty read-only list rather than a null, because <see cref="Visitors"/> is read on a
        /// lot's every frame and a null there is a NullReferenceException in a caller.
        /// </summary>
        private static readonly ReadOnlyCollection<NeighbourhoodCommunityVisitor> NoVisitors =
            new ReadOnlyCollection<NeighbourhoodCommunityVisitor>(new NeighbourhoodCommunityVisitor[0]);

        /// <summary>Fraction of a sub-minute tick left over from the last clock-only <see cref="Tick"/>.</summary>
        private double _tickRemainder;

        private int _activeLotNumber;
        private VMMarshal _activeMarshal;

        /// <summary>
        /// A world over a layout and a map. Disabled, at the world's default time, with nothing
        /// stored and nobody travelling.
        /// </summary>
        /// <param name="layout">The layout to own. A null layout becomes an empty one.</param>
        /// <param name="map">
        /// The generated map <paramref name="layout"/> was packed against. A null map becomes a
        /// blank one of the layout's own size, which is enough for the query's lot half and not for
        /// its road half - so a real caller passes the map the screen already has.
        /// </param>
        public SimitoneNeighbourhoodWorld(NeighbourhoodLayout layout, NeighbourhoodMap map)
            : this(layout, map, false)
        {
        }

        /// <summary>
        /// A world, switched on or off. The flag is the §6 <c>NeighbourhoodWorld</c> capability
        /// switch, held per world so that a second neighbourhood - or a test - can be off while
        /// another is on.
        /// </summary>
        /// <param name="layout">The layout to own. A null layout becomes an empty one.</param>
        /// <param name="map">The generated map, or null for a blank one of the layout's size.</param>
        /// <param name="worldEnabled">Whether <see cref="Tick"/> advances anything.</param>
        public SimitoneNeighbourhoodWorld(NeighbourhoodLayout layout, NeighbourhoodMap map,
            bool worldEnabled)
        {
            _layout = layout ?? NeighbourhoodLayout.Empty(0, 0, 0);
            _map = map ?? NeighbourhoodMap.Create(_layout.MapSize);
            _query = new NeighbourhoodQuery(_layout, _map);
            WorldEnabled = worldEnabled;
        }

        /// <summary>
        /// A world over a layout with no map, which builds a blank one. Convenient, and honest about
        /// what it is: the query's lot half is real and its road half is not, because a blank map
        /// has no roads.
        /// </summary>
        /// <param name="layout">The layout to own. A null layout becomes an empty one.</param>
        public SimitoneNeighbourhoodWorld(NeighbourhoodLayout layout)
            : this(layout, null)
        {
        }

        public int Slot => _layout.Slot;

        /// <summary>
        /// The seed this neighbourhood's generated world was derived from, copied from the layout so
        /// a caller does not have to reach through to it. §3 lists the RNG as a member of this
        /// object, and B6 is what now derives from it: the <see cref="Offscreen"/> simulator's
        /// streams are built from this one number, and it is the only reader in the class.
        /// </summary>
        public int Seed => _layout.Seed;

        public NeighbourhoodLayout Layout => _layout;

        /// <summary>
        /// The generated map this world was built with. The same instance that was handed in, and
        /// the one <see cref="Query"/> reads.
        /// </summary>
        public NeighbourhoodMap Map => _map;

        public NeighbourhoodQuery Query => _query;

        public LotStore LotStore => _lotStore;

        public TransitStore Transit => _transit;

        /// <summary>
        /// The neighbourhood's own clock, as a <see cref="WorldClock"/> rather than only as an
        /// <see cref="INeighbourhoodClock"/>, because a world may push it to and pull it from a lot
        /// and only this half of the interface can do that. See <see cref="SyncClockTo"/> and
        /// <see cref="SyncClockFrom"/>.
        /// </summary>
        /// <remarks>
        /// Satisfies <see cref="INeighbourhoodWorld.Clock"/> through the explicit implementation
        /// below rather than covariantly - an interface member cannot be implemented by a narrower
        /// return type implicitly - and having both means a caller holding the interface gets the
        /// read-only clock it asked for, while a caller holding this class gets the one it needs.
        /// </remarks>
        public WorldClock Clock => _clock;

        INeighbourhoodClock INeighbourhoodWorld.Clock => _clock;

        /// <summary>
        /// This neighbourhood's off-screen simulator - the B6 object that advances stored lots
        /// while the player is elsewhere - built on first ask over <see cref="Seed"/> and kept
        /// from then on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Lazy, and never built for a world that does not ask.</b> A world whose
        /// <see cref="NeighbourhoodWorldFlag.Offscreen"/> capability is off reaches this property
        /// only if a caller has a reason to, and <see cref="Tick"/> does not: the simulator exists
        /// on that path in no form, so "the flag is off and the world still moved" cannot be
        /// written. Building it in the constructor would have cost every Part A world a stream
        /// derivation and a dictionary for a capability it does not have.
        /// </para>
        /// <para>
        /// <b>Same instance every time, and the same instance the world ticks.</b> A simulator
        /// holds its per-lot records, and a record is where the resume point lives, so a second
        /// simulator would be a second account of the same lot: two objects, two answers, and
        /// whichever the caller happened to be holding.
        /// </para>
        /// <para>
        /// <b>Fidelity is a dial the world does not turn.</b> The simulator arrives at
        /// <see cref="OffscreenSimulator.DefaultFidelity"/> - Coarse, the handoff's own default -
        /// and a caller that wants <see cref="OffscreenFidelity.Paused"/> or
        /// <see cref="OffscreenFidelity.Full"/> sets it on the object this hands back. The world
        /// has no opinion about how much a neighbour should be doing; what it does insist on is
        /// that whatever the dial says is honoured, and "Paused" means nothing at all moves.
        /// </para>
        /// <para>
        /// <b>What determinism means here, precisely.</b> Every draw the simulator makes comes from
        /// <c>NeighbourhoodStreams.Simulation</c> - the stream Part A §15 reserved for this and the
        /// only one B6 may touch - keyed by lot number and by the <em>absolute</em> step index
        /// derived from <see cref="WorldClock.Ticks"/>. So the same seed and the same sequence of
        /// ticks reach the same numbers, retuning map generation cannot move an off-screen motive,
        /// and adding a second lot cannot move the first lot's. The seed it reads is this world's own
        /// <see cref="Seed"/>, so a neighbourhood's off-screen behaviour is a property of the
        /// neighbourhood rather than of the session that happened to load it.
        /// </para>
        /// <para>
        /// <b>What it does not yet mean.</b> The absolute step index is supposed to buy the stronger
        /// claim - that eight hours in one tick and eight hours in sixty reach the <em>same</em>
        /// state, because a frame rate is not something a save file records. Measured on
        /// 2026-09-30 that is not currently true: a lot whose record is created and stepped in the
        /// same tick derives its step streams from a stream its own seeding draw has already
        /// advanced, so the first tick's jitter differs from a later tick's for the same step index.
        /// The simulator's own invariance test does not see it because its seed's jitter never lands
        /// a point near a boundary. The fix belongs in the simulator - seed from a labelled
        /// sub-stream, or derive the steps from the pre-seed state - and until it is made, a caller
        /// that fast-forwards and a caller that plays at 60fps hold slightly different motive
        /// tables. The clock, the step count, the resume stamp and the frame composition invariant
        /// are unaffected; only the off-screen motive numbers are.
        /// </para>
        /// </remarks>
        public OffscreenSimulator Offscreen
        {
            get
            {
                if (_offscreen == null) _offscreen = new OffscreenSimulator(_layout.Seed);
                return _offscreen;
            }
        }

        /// <summary>
        /// What the last off-screen tick did, or <c>null</c> when there has not been one - which
        /// for a world whose capability is off is every tick it has ever been given.
        /// </summary>
        /// <remarks>
        /// A snapshot of a finished tick, not a live view: it is replaced on the next one and does
        /// not change under a reader. <see cref="OffscreenTickResult.Changed"/> is the same
        /// question <see cref="Tick"/> answers as a <c>bool</c>, and it is answered from here when
        /// the caller wants to know why.
        /// </remarks>
        public OffscreenTickResult LastOffscreenTick
        {
            get { return _lastOffscreenTick; }
        }

        /// <summary>
        /// This lot's off-screen record, or <c>null</c> if it has never been simulated off-screen.
        /// Never creates one, so a caller can ask about a lot without the asking becoming the thing
        /// that simulated it.
        /// </summary>
        /// <param name="lot">The lot number.</param>
        /// <param name="record">The record, or <c>null</c>.</param>
        /// <returns>Whether a record exists.</returns>
        public bool TryGetOffscreenRecord(short lot, out OffscreenLotRecord record)
        {
            //The field, not the property: asking "does this lot have a record" must not be the
            //thing that builds a simulator for a world which has not needed one.
            if (_offscreen == null)
            {
                record = null;
                return false;
            }
            record = _offscreen.RecordFor(lot);
            return record != null;
        }

        // ---------------------------------------------------------------- B8: one sun, one sky, one weather

        /// <summary>
        /// This neighbourhood's world features - B8's one sun, one sky, one weather and one idea of
        /// who is visiting - built on first ask over <see cref="Clock"/> and <see cref="Layout"/>,
        /// and kept from then on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It shares this world's clock rather than shadowing it.</b> That is the reason the
        /// world's tick re-reads the features instead of calling
        /// <see cref="NeighbourhoodWorldFeatures.Tick"/>: there is only one clock in the
        /// neighbourhood, this one, so a features object that drew its weather on any other would
        /// put a neighbourhood's sky and its Sims' moods on different times.
        /// </para>
        /// <para>
        /// <b>Asking is what builds it, and a tick never does that with the capability off.</b>
        /// <see cref="Tick"/> reads <see cref="NeighbourhoodWorldFlags.IsOn"/> at the branch and
        /// returns before it gets here when the flag is off, so a world nobody asks about never
        /// allocates a features object at all. A caller that wants the features on a world whose
        /// capability is off is asking a question this class will answer - it is only
        /// <see cref="Tick"/> that is gated - and the answer is a features object over the same
        /// clock and the same seed as the one a gated tick would have built.
        /// </para>
        /// </remarks>
        public NeighbourhoodWorldFeatures Features
        {
            get
            {
                //The field, not a flag check: the flag decides who may cause this, and the caller
                //reaching for Features is a caller that has decided to cause it.
                if (_features == null) _features = new NeighbourhoodWorldFeatures(_clock, _layout);
                return _features;
            }
        }

        /// <summary>
        /// Has a features object ever been built for this world? Never constructs one, so asking
        /// cannot be the thing that builds it - which is how a test can say that a world ticked
        /// with the capability off never reached B8 at all.
        /// </summary>
        public bool HasFeatures
        {
            get { return _features != null; }
        }

        /// <summary>
        /// How many times this world has re-read its features, which is a gated refresh and not a
        /// clock advance - see the remarks on <see cref="Tick"/>. Zero for a world whose capability
        /// is off, for ever.
        /// </summary>
        public long FeatureTicks { get; private set; }

        /// <summary>
        /// B8's own lighting right now: the sun's colour and strength and the world's fill light,
        /// as a pure function of the clock. The lot view's drawable answer is
        /// <see cref="CurrentLighting"/>, which is this and the weather combined.
        /// </summary>
        /// <remarks>
        /// <c>default</c> - a midnight the features type has never drawn - for a world that has not
        /// built its features. Reading this never builds them.
        /// </remarks>
        public WorldLightingState Lighting
        {
            get { return _features == null ? default : _features.Lighting; }
        }

        /// <summary>
        /// B8's own weather right now, shared by every lot in this neighbourhood and a pure function
        /// of the layout's seed and <see cref="Clock"/>. <c>default</c> - no cloud, no rain - for a
        /// world that has not built its features. Reading this never builds them.
        /// </summary>
        public WorldWeather Weather
        {
            get { return _features == null ? default : _features.Weather; }
        }

        /// <summary>
        /// The lots with company right now, at most
        /// <see cref="NeighbourhoodWorldFeatures.MaxActiveVisitorLots"/> of them, ascending by lot
        /// number. Never null, and never built by being read: an empty list for a world that has not
        /// built its features, and the features' own list otherwise.
        /// </summary>
        public IReadOnlyList<NeighbourhoodCommunityVisitor> Visitors
        {
            get { return _features == null ? NoVisitors : _features.Visitors; }
        }

        /// <summary>
        /// What a lot view draws with right now: the sun's direction, colour and strength, the
        /// fill and background colours, the fog's colour and distance, and the sky's tint and strip
        /// position.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Computed on read, from the features' own cached state</b>, and that is what makes it
        /// safe to have no invalidation story: the adapter is a pure function of
        /// <see cref="Lighting"/> and <see cref="Weather"/>, both of which the features object
        /// re-reads from the clock on every gated tick and on every <see cref="PullClockFrom"/>. A
        /// cached copy here would be a third thing to remember to invalidate, and it is a value
        /// type, so recomputing it costs a struct copy and no allocation.
        /// </para>
        /// <para>
        /// <c>default</c> - a black sun and no sky - for a world that has not built its features.
        /// A caller gates on the capability, not on whether the value looks plausible; see the class
        /// remarks.
        /// </para>
        /// </remarks>
        public NeighbourhoodWorldLighting CurrentLighting
        {
            get
            {
                if (_features == null) return default;
                return NeighbourhoodWorldLighting.From(_features.Lighting, _features.Weather);
            }
        }

        /// <summary>
        /// Re-read the features from the clock, if the <see cref="NeighbourhoodWorldFlag.World"/>
        /// capability says this world is allowed to have any. The one place B8 is wired into the
        /// world, and the whole of the wiring: the flag is asked at the branch, so with it off this
        /// returns before constructing, re-reading or counting anything.
        /// </summary>
        private void RefreshFeatures()
        {
            //§7, asked where the branch is taken. Cached on the world would need a second way to be
            //told the flag had changed, and read once per construction would be a second opinion
            //about a setting that §7 says is read from the sidecar.
            if (!NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.World)) return;

            //Refresh, not Tick: this world's tick has already advanced the shared clock, and
            //NeighbourhoodWorldFeatures.Tick would advance it a second time.
            Features.Refresh();
            FeatureTicks++;
        }

        public int ActiveLotNumber => _activeLotNumber;

        public IReadOnlyList<NeighbourhoodLot> Lots => _layout.Lots;

        /// <summary>How many lots this world has. A shortcut for "is there anything to draw".</summary>
        public int LotCount => _layout.LotCount;

        /// <summary>
        /// Whether this world is switched on. Defaults to <c>false</c>; see the class remarks for
        /// what off does and does not mean.
        /// </summary>
        public bool WorldEnabled { get; set; }

        public bool TryGetLot(int number, out NeighbourhoodLot lot)
        {
            lot = _layout.FindLot(number);
            return lot != null;
        }

        /// <summary>
        /// The marshal of the lot the player is standing in, or <c>null</c> - in no lot, in a lot
        /// nothing has been marshalled for, or in a read-only world that has no such thing.
        /// </summary>
        /// <remarks>
        /// This is the live VM's marshalled state and it is <em>not</em> in <see cref="LotStore"/>:
        /// a resident lot is not a stored one, and §5.3's "marshalled into <c>LotStore</c> on switch"
        /// is the moment the player leaves it. <see cref="SetActiveLot"/> is what moves a marshal
        /// between this property and the store.
        /// </remarks>
        public VMMarshal ActiveMarshal => _activeMarshal;

        /// <summary>
        /// Take the time out of <paramref name="lotClock"/> and make it the neighbourhood's, and
        /// report whether that moved the clock.
        /// </summary>
        /// <remarks>
        /// The half of the §5.2 handshake that runs on the way <em>out</em> of a lot, and the one an
        /// off-screen lot runs when it ticks. It is exposed rather than called from
        /// <see cref="SetActiveLot"/> for one reason: this world has no idea when a lot is being
        /// unloaded, and a lot switch that did not pull the clock first would lose the minutes the
        /// player spent in it.
        /// </remarks>
        /// <param name="lotClock">The lot's clock. Required; null is a caller bug and throws.</param>
        /// <returns>Whether the neighbourhood's time changed.</returns>
        public bool SyncClockFrom(VMClock lotClock)
        {
            if (lotClock == null) throw new ArgumentNullException(nameof(lotClock));

            //compared before the pull, on all eight fields including the tick count: "how far did
            //this lot run" is as much a part of arriving somewhere as the wall clock is
            var same = SameTimeAs(lotClock);
            PullClockFrom(lotClock);
            return !same;
        }

        /// <summary>
        /// Write the neighbourhood's time into <paramref name="lotClock"/>, and report whether the
        /// lot was behind - the half of the handshake that runs on the way <em>into</em> a lot, and
        /// the reason a lot reloaded from its own file starts where the neighbourhood has got to.
        /// </summary>
        /// <param name="lotClock">The lot's clock. Required; null is a caller bug and throws.</param>
        /// <returns>Whether the lot's time was not already the neighbourhood's.</returns>
        public bool SyncClockTo(VMClock lotClock)
        {
            if (lotClock == null) throw new ArgumentNullException(nameof(lotClock));

            var behind = !SameTimeAs(lotClock);
            PushClockTo(lotClock);
            return behind;
        }

        /// <summary>
        /// Take the time out of <paramref name="clock"/> and make it the neighbourhood's - the
        /// §5.2 handshake's way <em>out</em>, as a statement rather than a question.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the whole of the operation, and it is deliberately unconditional. §5.2 says a
        /// lot's clock "pushes back on save or tick", and the caller that saves or ticks a lot has
        /// already decided the transfer happens; the only question left is whether the neighbourhood
        /// had noticed, which is what <see cref="SyncClockFrom"/> is for. Making the decision
        /// twice - once here, once there - is how a handshake ends up skipped because nobody
        /// compared the two clocks first.
        /// </para>
        /// <para>
        /// All eight time fields move together, <c>Ticks</c> included, so the clock cannot be half
        /// synced and a lot's elapsed count is never discarded by arriving somewhere.
        /// <c>FirePercent</c> and <c>UTCStart</c> are the lot's own and are not among them - see
        /// <see cref="WorldClock.PullFrom"/>.
        /// </para>
        /// <para>
        /// <b>B8's features are re-read here, and this is the one place that happens off the tick
        /// path.</b> Every way the clock can move without a tick - a lot switch, a save, a load -
        /// arrives through this method, so putting the gated refresh here rather than at each
        /// caller is what stops a resumed neighbourhood from showing the pre-load sky for one frame.
        /// It is the same gated call the tick makes, so a world with the capability off is not
        /// affected by it either.
        /// </para>
        /// </remarks>
        /// <param name="clock">The lot's clock. Required; null is a caller bug and throws.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
        public void PullClockFrom(VMClock clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            _clock.PullFrom(clock);
            RefreshFeatures();
        }

        /// <summary>
        /// Write the neighbourhood's time into <paramref name="clock"/> - the handshake's way
        /// <em>in</em>, as a statement rather than a question.
        /// </summary>
        /// <remarks>
        /// The other half of <see cref="PullClockFrom"/>, and the reason a lot reloaded from its own
        /// <c>HouseNN.iff</c> starts where the neighbourhood has got to rather than where the file's
        /// globals say. The round trip the two make is exact: pull from one lot, push to another,
        /// and the two are field-for-field identical including the tick count.
        /// </remarks>
        /// <param name="clock">The lot's clock. Required; null is a caller bug and throws.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
        public void PushClockTo(VMClock clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            _clock.PushTo(clock);
        }

        /// <summary>
        /// Move the player into <paramref name="lotNumber"/>, or out of every lot when it is 0, and
        /// move a lot's marshal between the store and <see cref="ActiveMarshal"/> on the way.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rule, stated once so both halves cannot disagree: <b>the store holds the lots the
        /// player is not in, and this object holds the one they are.</b> So leaving a lot
        /// stashes its marshal in the store under its own number - keyed as the lot number and
        /// nothing else, which is what today's <c>SavedLots</c> dictionary is and what
        /// <see cref="LotStore"/>'s own keying is - and entering a lot takes any stashed marshal
        /// back out and hands it to the caller to resume from. That is §5.3's third state
        /// (unloaded: only the house file) arriving on its own: a lot with no marshal in the store
        /// and none on hand is a lot that gets built from disk.
        /// </para>
        /// <para>
        /// Nothing else moves. The clock is untouched here on purpose, for the reason given on
        /// <see cref="SyncClockFrom"/>: this class cannot know that a lot is finished with. A
        /// negative or over-large <paramref name="lotNumber"/> is a caller bug and throws rather
        /// than being clamped, because a silently wrong lot number is exactly the failure this
        /// world exists to make impossible.
        /// </para>
        /// </remarks>
        /// <param name="lotNumber">The lot to be in, or 0 for the neighbourhood screen.</param>
        /// <remarks>
        /// The marshal of the lot now being entered is <see cref="ActiveMarshal"/> afterwards - the
        /// one that was stashed, or <c>null</c> when the lot had none stored and none was supplied,
        /// in which case the caller builds it from the house file. Read it as a property rather than
        /// a return value so this method can be the <see cref="INeighbourhoodWorld"/> member, which
        /// is <c>void</c>: a caller that forgets to look has a marshal on the world rather than one
        /// it dropped.
        /// </remarks>
        public void SetActiveLot(int lotNumber)
        {
            if (lotNumber < NoActiveLot || lotNumber > short.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(lotNumber), lotNumber,
                    "A lot number is 0 (no lot) or a positive value that fits the 16-bit house number.");
            }

            if (lotNumber == _activeLotNumber)
            {
                //already here: the stored marshal is the resume point, and picking it up here is
                //what makes "tell me again which lot you are in" a resume rather than a switch
                VMMarshal here;
                if (_activeMarshal == null && lotNumber != NoActiveLot)
                {
                    _lotStore.TryGetActive((short)lotNumber, out here);
                    _activeMarshal = here;
                }
                return;
            }

            // out: the lot being left keeps its contents, stashed under its own number
            if (_activeLotNumber != NoActiveLot && _activeMarshal != null)
            {
                _lotStore.SetActive((short)_activeLotNumber, _activeMarshal);
            }

            // in: whatever this lot had stored comes out of the store and onto the world
            VMMarshal incoming = null;
            if (lotNumber != NoActiveLot && _lotStore.TryGetActive((short)lotNumber, out incoming))
            {
                _lotStore.Remove((short)lotNumber);
            }

            _activeLotNumber = lotNumber;
            _activeMarshal = incoming;
        }

        /// <summary>
        /// Hand the world a freshly marshalled lot and make it the active one - the other direction
        /// of a store round-trip, and what a caller that has just built a lot from its house file
        /// calls. Split from <see cref="SetActiveLot"/> so that the direction of a marshal movement
        /// is in the method's name rather than in which argument is null.
        /// </summary>
        /// <param name="lotNumber">The lot the player is now in, or 0 for none.</param>
        /// <param name="marshal">The lot's marshalled <c>VM</c>, or null for none.</param>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="lotNumber"/> is not a lot
        /// number or 0.</exception>
        public void SetActiveLotWith(int lotNumber, VMMarshal marshal)
        {
            SetActiveLot(lotNumber);
            _activeMarshal = marshal;
        }

        /// <summary>
        /// Advance the neighbourhood by <paramref name="deltaSeconds"/> of real time, and say
        /// whether anything moved.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Two paths, and the flag chooses between them before either one starts.</b> With
        /// <see cref="NeighbourhoodWorldFlag.Offscreen"/> on, this hands the delta to the
        /// <see cref="OffscreenSimulator"/> - which advances the clock <em>and</em> decays the
        /// stored lots, and reports which of the two it managed. With the flag off it runs the
        /// clock-only arithmetic below, which is unchanged from B1 and is the whole of what this
        /// method did before B6 existed. The clock is advanced on exactly one of the two paths and
        /// never on both: a world that ticked its own clock and then let the simulator tick it
        /// again would run at double time the moment a capability was switched on.
        /// </para>
        /// <para>
        /// <b>One game minute per second is TS1's rate, and it comes from the clock rather than
        /// from here.</b> A sub-minute tick is a fixed number of real seconds long - thirty of them
        /// to the minute, which is <see cref="WorldClock.TicksPerMinuteTs1"/> - so the count is
        /// <c>deltaSeconds * TicksPerMinute</c> and a lot running at some other rate is carried by
        /// the same arithmetic rather than by a second opinion about how long a minute is.
        /// </para>
        /// <para>
        /// <b>Whole ticks only, with the remainder banked.</b> The clock moves in whole sub-minute
        /// ticks, and the part of a delta that does not fill one is carried into the next call
        /// rather than dropped, so a world ticked in 10ms steps and a world ticked in 16ms ones end
        /// up in the same place. (It used to be dropped, which at 60 fps meant every frame was half
        /// a tick and rounded to none.) B6 fast-forwards by calling this with a large delta, so it
        /// is deliberately not a frame.
        /// </para>
        /// <para>
        /// A delta that is negative, not a number, infinite, or too short to fill a tick advances
        /// nothing and returns <c>false</c>. That is the right answer rather than an error: a
        /// paused game and a clock that has been rewound are both callers with nothing to ask for,
        /// and neither should be able to make the neighbourhood's time jump. Infinite is in the
        /// list for a concrete reason - it is a number, it compares as greater than zero, and
        /// casting it to a tick count saturates rather than throwing, so a caller that divided by
        /// a zero frame time would otherwise ask for a world that never finishes ticking. The
        /// off-screen path is reached only after this filter, so the simulator's own rejection of
        /// NaN and negative deltas is a second line of defence rather than the one that fires.
        /// </para>
        /// <para>
        /// <b>Bounded per call.</b> A delta longer than <see cref="MaxTicksPerCall"/> is clamped and
        /// the remainder is dropped, because the alternative is a caller that cannot be stopped: B6
        /// fast-forwards by calling this with a large delta, and a caller that wants longer than the
        /// cap calls it again. The cap is a frame-loop hazard guard, not a policy about how long a
        /// fast-forward may be - see the remarks on the constant for how long it actually is. It is
        /// applied on the off-screen path too, as a delta rather than a tick count, so that this
        /// method's own bound is the one that holds whichever capability is on: the simulator has a
        /// budget of its own, tighter than this one at every default, and a caller that raises it
        /// must still not be able to make this method unbounded.
        /// </para>
        /// <para>
        /// <b>B8's features are re-read here, after the clock has moved and on whichever path moved
        /// it</b>, so the sun, the weather and the visitor list belong to the time this tick just
        /// produced rather than to the one before it. The refresh is inside the
        /// <see cref="NeighbourhoodWorldFlag.World"/> branch and the flag is asked at the branch, so
        /// a world with that capability off constructs no features object and counts no feature
        /// ticks however long it is run.
        /// </para>
        /// </remarks>
        /// <param name="deltaSeconds">Real seconds elapsed.</param>
        /// <returns>
        /// Whether the world moved and needs redrawing: the clock advanced on the clock-only path,
        /// or <see cref="OffscreenTickResult.Changed"/> on the off-screen one. B8's refresh is a
        /// consequence of that answer and never the cause of it - a tick that moved nothing does not
        /// re-read the features, because there is nothing new to read.
        /// </returns>
        public bool Tick(double deltaSeconds)
        {
            if (!WorldEnabled) return false;
            if (!double.IsFinite(deltaSeconds) || deltaSeconds <= 0) return false;

            //§7: off means the previous stage's behaviour. Asked here, at the branch, rather than
            //cached on the world - the world is not where a setting is, and a world that remembered
            //the flag would need a second way to be told it had changed.
            if (NeighbourhoodWorldFlags.IsOn(NeighbourhoodWorldFlag.Offscreen))
            {
                var moved = TickOffscreen(deltaSeconds);
                //B8, after whichever path advanced the clock and not before it: the features read
                //the clock, they do not move it, and a refresh on a tick that moved nothing is a
                //re-read of the numbers that were already there.
                if (moved) RefreshFeatures();
                return moved;
            }

            //The sub-tick remainder is banked, not dropped. The screen calls this once a frame with a
            //frame-sized delta, and at 60 fps a frame is half a sub-minute tick: flooring each call
            //on its own made the clock advance zero ticks on every frame at 60 fps and above, so
            //neighbourhood time never moved in a real session.
            var exact = deltaSeconds * TicksPerMinute(_clock) + _tickRemainder;
            var ticks = (long)Math.Floor(exact);
            if (ticks > MaxTicksPerCall)
            {
                ticks = MaxTicksPerCall;
                _tickRemainder = 0;
            }
            else
            {
                _tickRemainder = exact - ticks;
            }
            if (ticks <= 0) return false;

            for (long i = 0; i < ticks; i++) _clock.Tick();
            RefreshFeatures();
            return true;
        }

        /// <summary>
        /// The B6 path: hand the elapsed time to the off-screen simulator and apply what it did.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The simulator advances the clock itself, so this method advances nothing of its own -
        /// the only thing it contributes is the world's own per-call budget, expressed as a delta
        /// so that both paths are bounded by the same number.
        /// </para>
        /// <para>
        /// <b>The active lot is stamped on every tick</b>, rather than being left to the simulator's
        /// fallback to the layout's world state. The world knows which lot the player is in; the
        /// simulator can only ask, and §B6 task 4's "never the active lot's live tick" is a
        /// property of this assignment rather than of the store happening not to hold the marshal.
        /// </para>
        /// </remarks>
        private bool TickOffscreen(double deltaSeconds)
        {
            var perMinute = TicksPerMinute(_clock);
            var capSeconds = (double)MaxTicksPerCall / perMinute;
            if (deltaSeconds > capSeconds) deltaSeconds = capSeconds;

            var simulator = Offscreen;
            simulator.ActiveLot = _activeLotNumber;
            var result = simulator.Tick(deltaSeconds, _clock, _lotStore, _layout);
            _lastOffscreenTick = result;
            return result.Changed;
        }

        /// <summary>
        /// The sub-minute rate to count elapsed time in, falling back to TS1's for a clock that has
        /// never been given one. Shared by both tick paths so they cannot disagree about it.
        /// </summary>
        private static int TicksPerMinute(WorldClock clock)
        {
            return clock.TicksPerMinute > 0 ? clock.TicksPerMinute : WorldClock.TicksPerMinuteTs1;
        }

        /// <summary>
        /// This world's session state as a storable value: the clock, the active lot, and the
        /// hops in progress. B7's schema-2 payload, handed over rather than written anywhere.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Nothing is written to disk and no file is opened. The sidecar, its schema stamp and
        /// <c>AtomicFileCommit</c> belong to whoever is saving - a world that saved itself would be
        /// saving itself at moments its owner did not choose, which is how a half-written session
        /// state gets committed.
        /// </para>
        /// <para>
        /// The two halves are each written by the object that owns them and neither is a copy the
        /// caller has to keep in step: the clock and the active lot by
        /// <see cref="NeighbourhoodWorldState.FromClock"/>, the hops by
        /// <see cref="TransitStore.WriteTo"/>, which copies each record so a hop that advances
        /// after the save cannot move the saved one.
        /// </para>
        /// <para>
        /// <b>Saving is also when the off-screen stamps land.</b> If a simulator has been built -
        /// that is, if the capability has been on at least once this world's lifetime - the
        /// simulated-tick marks it has accumulated are applied to the layout's lot records here and
        /// nowhere else, for the reason given on
        /// <see cref="OffscreenSimulator.ApplyStamps"/>: a per-frame tick is not the moment to
        /// change the sidecar-bound part of the world.
        /// </para>
        /// </remarks>
        /// <returns>A new state every call. Nothing held here is shared with the live world.</returns>
        public NeighbourhoodWorldState SaveWorldState()
        {
            var state = NeighbourhoodWorldState.FromClock(_clock, _activeLotNumber);
            _transit.WriteTo(state);

            //The field, not the Offscreen property: a save must not be the thing that builds a
            //simulator for a world that has never simulated anything off-screen.
            if (_offscreen != null) _offscreen.ApplyStamps(_layout);

            return state;
        }

        /// <summary>
        /// Adopt a saved session: its clock, its active lot and its hops. The resume half of
        /// <see cref="SaveWorldState"/>, and what B7's "quit and resume" calls.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The clock goes through the same handshake as a lot's.</b> The state is written into a
        /// scratch <c>VMClock</c> and pulled, rather than being assigned field by field, so a
        /// resumed world cannot reach a clock state no lot swap could produce - which is the same
        /// argument as having one implementation of the transfer rather than two.
        /// </para>
        /// <para>
        /// <b>An unavailable clock is not adopted.</b>
        /// <see cref="NeighbourhoodWorldState.ClockAvailable"/> false means the session was saved
        /// with no clock to copy, and every other clock member in that state is meaningless; loading
        /// it would hand a world a time nobody ever had. The default clock is left as it is, which
        /// is the honest "nothing has happened yet".
        /// </para>
        /// <para>
        /// <b>A lot number that names no lot becomes "no lot".</b> The layout store does not range
        /// check the active lot it reads, so a hand-edited sidecar can carry one - and a world that
        /// threw on it would be a session that cannot be resumed at all. The same stance
        /// <see cref="TransitStore.LoadFrom"/> takes on a null hop: a corrupt file is not a reason
        /// to refuse the whole load.
        /// </para>
        /// <para>
        /// <b>Off-screen records are not restored</b>, and deliberately so: a resumed world
        /// re-seeds its neighbours from the world seed and simulates them forward from the resumed
        /// clock. Nothing was lost - a lot whose contents were never in memory had no state to
        /// lose - and the alternative would be a schema field B6 has no version number for.
        /// </para>
        /// </remarks>
        /// <param name="state">The state to adopt. Required; null is a caller bug and throws.</param>
        /// <exception cref="ArgumentNullException"><paramref name="state"/> is null.</exception>
        public void ApplyWorldState(NeighbourhoodWorldState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));

            _transit.LoadFrom(state);

            if (state.ClockAvailable)
            {
                //A sub-minute rate of zero would be a clock nothing can divide by, and the one
                //place that could produce it is a hand-edited sidecar; TS1's rate is what an
                //unrated clock means, and both tick paths already fall back to it.
                PullClockFrom(new VMClock
                {
                    Hours = state.Hours,
                    Minutes = state.Minutes,
                    MinuteFractions = state.MinuteFractions,
                    TicksPerMinute = state.TicksPerMinute > 0
                        ? state.TicksPerMinute
                        : WorldClock.TicksPerMinuteTs1,
                    DayOfMonth = state.DayOfMonth,
                    Month = state.Month,
                    Year = state.Year,
                    Ticks = state.Ticks
                });
            }

            var lot = state.ActiveLot;
            if (lot < NoActiveLot || lot > short.MaxValue) lot = NoActiveLot;
            SetActiveLot(lot);
        }

        public override string ToString()
        {
            return (WorldEnabled ? "world " : "world (off) ") + _layout
                + " clock " + _clock
                + " active " + (_activeLotNumber == NoActiveLot ? "none" : _activeLotNumber.ToString())
                + " " + _lotStore
                + " " + _transit
                + (_offscreen != null ? " " + _offscreen : string.Empty)
                //The same shape as the off-screen half: a world that has no features says nothing
                //about them, so with the capability off this line is byte-for-byte what it was
                //before B8 existed.
                + (_features != null ? " " + _features : string.Empty);
        }

        /// <summary>
        /// Do this world's eight time fields already equal a lot's? Asked by both sync directions
        /// <em>before</em> the transfer, because afterwards the answer is true by construction and
        /// would report a change that did not happen.
        /// </summary>
        private bool SameTimeAs(VMClock lotClock)
        {
            return _clock.Hours == lotClock.Hours
                && _clock.Minutes == lotClock.Minutes
                && _clock.MinuteFractions == lotClock.MinuteFractions
                && _clock.TicksPerMinute == lotClock.TicksPerMinute
                && _clock.DayOfMonth == lotClock.DayOfMonth
                && _clock.Month == lotClock.Month
                && _clock.Year == lotClock.Year
                && _clock.Ticks == lotClock.Ticks;
        }
    }
}
