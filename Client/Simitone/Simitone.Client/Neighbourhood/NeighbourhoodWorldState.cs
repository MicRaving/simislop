/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// One hop in progress: the player is between two lots rather than in either of them.
    ///
    /// <para>
    /// B7's "quit mid-transit and resume" (handoff §B7 task 4) needs three facts and nothing
    /// else: which Sim is travelling, where they came from, where they are going, and how far
    /// along they are. This is those four plus the mode, as plain integers.
    /// </para>
    ///
    /// <para>
    /// <b>Why the mode is an <c>int</c> and not an enum here.</b> The set of ways to cross a
    /// neighbourhood belongs to whoever builds the crossing - B3's discrete travel, B4's walk -
    /// and two types with the same values on either side of a seam is the mistake this
    /// repository has already paid for once (see <see cref="NeighbourhoodLotSide"/>). So this
    /// type stores the code and does not define the vocabulary; a reader that wants to know what
    /// mode 2 means asks the type that owns it. The same argument applies to
    /// <see cref="Progress"/>: it is a 0..100 percentage of the hop, chosen here because it needs
    /// no unit of its own to be readable, and a mode that wants a different unit converts on the
    /// way in and out.
    /// </para>
    ///
    /// <para>
    /// A plain data holder, like <see cref="NeighbourhoodLot"/>: no validation, no file system,
    /// no VM. The one validation that exists is at the file boundary, in
    /// <see cref="NeighbourhoodLayoutStore"/>, so there is a single place where a hand-edited
    /// sidecar is made to say something sane.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodTransitRecord
    {
        /// <summary>A hop that has not started.</summary>
        public const int MinProgress = 0;

        /// <summary>A hop that has arrived. The one value that means "done".</summary>
        public const int MaxProgress = 100;

        /// <summary>
        /// The Sim who is travelling. 0 when the hop belongs to nobody - which, like
        /// <see cref="INeighbourhoodWorld.ActiveLotNumber"/>, is not a lot number and not a
        /// Sim number either.
        /// </summary>
        public int Sim { get; set; }

        /// <summary>Lot number the hop started at.</summary>
        public int Origin { get; set; }

        /// <summary>Lot number the hop is going to.</summary>
        public int Destination { get; set; }

        /// <summary>
        /// The crossing's own mode code. Not interpreted here; see the remarks on the class.
        /// </summary>
        public int Mode { get; set; }

        /// <summary>
        /// How far along the hop is, in percent (<see cref="MinProgress"/>..
        /// <see cref="MaxProgress"/>). The reason this is an integer: a float in a sidecar is a
        /// rounding difference between two builds, and a half-finished hop is not worth one.
        /// </summary>
        public int Progress { get; set; }

        public NeighbourhoodTransitRecord()
        {
        }

        public NeighbourhoodTransitRecord(int sim, int origin, int destination, int mode, int progress)
        {
            Sim = sim;
            Origin = origin;
            Destination = destination;
            Mode = mode;
            Progress = progress;
        }

        /// <summary>
        /// Has this hop arrived? The test B7's resume path asks first, because "finish the hop"
        /// and "start the next one" are different work and a record at 100% is the former.
        /// </summary>
        public bool IsComplete
        {
            get { return Progress >= MaxProgress; }
        }

        /// <summary>
        /// Is this a hop at all? A record whose origin is its destination is not one - a lot
        /// travelling to itself - and the defaulted record is the degenerate case of that.
        /// </summary>
        public bool IsHop
        {
            get { return Origin != Destination; }
        }

        /// <summary>
        /// Field-for-field equality, for the round-trip assertions and for a caller that wants to
        /// know whether a resave would change anything. A method rather than an
        /// <c>Equals</c> override because the type is mutable and a hash of a mutable object is
        /// a trap; <see cref="SameAs"/> cannot be cached by accident.
        /// </summary>
        public bool SameAs(NeighbourhoodTransitRecord other)
        {
            return other != null
                && Sim == other.Sim
                && Origin == other.Origin
                && Destination == other.Destination
                && Mode == other.Mode
                && Progress == other.Progress;
        }

        public override string ToString()
        {
            return "hop sim " + Sim + " lot " + Origin + "->" + Destination
                + " mode " + Mode + " " + Progress + "%";
        }
    }

    /// <summary>
    /// Everything about a neighbourhood that is true of the <em>session</em> rather than of the
    /// generated map: what time it is, which lot the player is in, and which hops they are in
    /// the middle of.
    ///
    /// <para>
    /// This is B7's schema-2 payload - handoff §10's "World/session state ... same sidecar,
    /// schema 2 (clock, active lot, transit, lot states, seed)" - and it is deliberately
    /// <em>not</em> a place to put lot contents. Those are still <c>HouseNN.iff</c>, written
    /// through <c>SaveService</c>/<c>SaveHouse</c>; a sidecar that grew a lot's objects would be
    /// a second, worse copy of the save. The per-lot half of session state - which lots are
    /// hollow, when each was last simulated, where its door is - is on
    /// <see cref="NeighbourhoodLot"/> instead, because those are facts <em>about a lot</em> and
    /// travel with it through a layout patch.
    /// </para>
    ///
    /// <para>
    /// <b>The clock mirrors <see cref="INeighbourhoodClock"/> and does not own one.</b> Every
    /// member here is a copy taken at a moment, including
    /// <see cref="ClockAvailable"/>: a state with an unavailable clock is the honest record of
    /// "the player quit on the neighbourhood screen", which is different from "it was midnight",
    /// and a sidecar that could not tell them apart would silently move every world to midnight
    /// the first time somebody saved from the map. The mirror interface stays the thing a live
    /// world reads; this is the thing a file holds.
    /// </para>
    ///
    /// <para>
    /// <b>Defaults are "nothing has happened".</b> Clock unavailable, no active lot, no hops, no
    /// flags, and <see cref="WorldVersion"/> 0 - which is why a schema-1 sidecar migrates to
    /// exactly this and a corrupt world body is replaced by exactly this. It is also why
    /// <see cref="CreateDefault"/> hands out a fresh instance rather than a shared one: this type
    /// is mutable session state, and a process-wide "default" would be a global everybody writes
    /// to.
    /// </para>
    ///
    /// <para>
    /// <b>Not validated here.</b> Ranges are stated as constants and are enforced once, by the
    /// store, at the file boundary - so a caller that sets <see cref="Hours"/> to 25 gets 25 (a
    /// bug it can see) rather than a silently corrected 23, and a hand-edited file gets something
    /// a clock cannot divide by.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldState
    {
        /// <summary>
        /// Version of the <em>world state</em> this build writes and understands. Independent of
        /// the sidecar's own schema stamp, and of the generator version: this is the shape of the
        /// clock / active lot / transit triple, and nothing else.
        /// </summary>
        public const int CurrentWorldVersion = 1;

        public const int MinHours = 0;
        public const int MaxHours = 23;
        public const int MinMinutes = 0;
        public const int MaxMinutes = 59;
        public const int MinDayOfMonth = 1;
        public const int MaxDayOfMonth = 30;
        public const int MinMonth = 1;
        public const int MaxMonth = 12;

        /// <summary>
        /// The shape of this state, or 0 for "not stamped". 0 is the default so that a state
        /// nobody has written to is byte-for-byte absent from a sidecar; a world body that
        /// <em>is</em> stamped with a version this build does not know is refused, and the
        /// layout around it is still used.
        /// </summary>
        public int WorldVersion { get; set; }

        /// <summary>
        /// Session flags, as a bit set. No bit has an owner yet.
        /// </summary>
        /// <remarks>
        /// It exists so a later scope can add a session flag - paused, first visit, anything else
        /// a world wants to remember - without a second schema migration, and it is written only
        /// when it is non-zero, so a world that has nothing to say about itself says nothing.
        /// Deliberately opaque: a named constant per bit would be a claim about which flags
        /// exist, and the handoff's list (§7) is a list of build capabilities, not of things a
        /// saved session records.
        /// </remarks>
        public int Flags { get; set; }

        /// <summary>
        /// The lot the player is standing in, or 0 when they are on the neighbourhood screen and
        /// therefore in no lot. Same convention as <see cref="INeighbourhoodWorld.ActiveLotNumber"/>.
        /// </summary>
        public int ActiveLot { get; set; }

        /// <summary>
        /// Was there a clock to copy when this state was taken? <c>false</c> means "no", and every
        /// other clock member is then meaningless - but is written anyway, because a state that
        /// was captured with no clock and is later read with one must not acquire a time.
        /// </summary>
        public bool ClockAvailable { get; set; }

        /// <summary>0..23. See <see cref="INeighbourhoodClock.Hours"/>.</summary>
        public int Hours { get; set; }

        /// <summary>0..59. See <see cref="INeighbourhoodClock.Minutes"/>.</summary>
        public int Minutes { get; set; }

        /// <summary>Sub-minute progress, 0..<see cref="TicksPerMinute"/>, not normalised.</summary>
        public int MinuteFractions { get; set; }

        /// <summary>
        /// The sub-minute unit these fractions are counted in, copied rather than assumed - a lot
        /// that runs at a different rate has to keep its own, or the resume point drifts.
        /// </summary>
        public int TicksPerMinute { get; set; }

        /// <summary>1..30. See <see cref="INeighbourhoodClock.DayOfMonth"/>.</summary>
        public int DayOfMonth { get; set; }

        /// <summary>1..12. See <see cref="INeighbourhoodClock.Month"/>.</summary>
        public int Month { get; set; }

        /// <summary>The year. See <see cref="INeighbourhoodClock.Year"/>.</summary>
        public int Year { get; set; }

        /// <summary>
        /// The lot's elapsed tick count, which is the actual resume point: the wall-clock fields
        /// are what a player reads, and this is what a simulation continues from.
        /// </summary>
        public long Ticks { get; set; }

        /// <summary>
        /// The hops in progress, in the order they were started. Empty rather than null from the
        /// constructor and never set to null, so a caller cannot make the store write a null array
        /// by accident.
        /// </summary>
        public IList<NeighbourhoodTransitRecord> Transit { get; private set; }

        /// <summary>
        /// Whole seconds within the current minute, derived exactly as
        /// <see cref="INeighbourhoodClock.Seconds"/> is - and zero rather than a division by
        /// zero when the state was taken with no sub-minute unit.
        /// </summary>
        public int Seconds
        {
            get { return TicksPerMinute > 0 ? (MinuteFractions * 60) / TicksPerMinute : 0; }
        }

        /// <summary>How many hops are recorded. Safe on a state whose transit was never filled.</summary>
        public int TransitCount
        {
            get { return Transit == null ? 0 : Transit.Count; }
        }

        /// <summary>
        /// Is the player part-way through a hop? This is the question B7's resume path asks before
        /// deciding between "finish the travel" and "the player is in a lot", so it is stated once
        /// here rather than as a scan every caller writes.
        /// </summary>
        public bool InTransit
        {
            get
            {
                if (Transit == null) return false;
                for (int i = 0; i < Transit.Count; i++)
                {
                    var hop = Transit[i];
                    if (hop != null && !hop.IsComplete) return true;
                }
                return false;
            }
        }

        public NeighbourhoodWorldState()
        {
            Transit = new List<NeighbourhoodTransitRecord>();
        }

        /// <summary>
        /// The state of a neighbourhood nothing has happened to: no clock, no active lot, no hops,
        /// no flags, unstamped. This is what a schema-1 sidecar migrates to and what a corrupt
        /// world body is replaced with, and it is a new instance every call.
        /// </summary>
        public static NeighbourhoodWorldState CreateDefault()
        {
            return new NeighbourhoodWorldState();
        }

        /// <summary>
        /// Copy a live clock into a storable state, stamped at the current world version.
        /// </summary>
        /// <remarks>
        /// The one place that reads all nine clock members, so a field added to
        /// <see cref="INeighbourhoodClock"/> is added here rather than in each of the places that
        /// decide when to save. A null clock is not an error: it produces the same answer as
        /// <see cref="CreateDefault"/> apart from the stamp, which is what "there is no lot loaded"
        /// means.
        /// </remarks>
        /// <param name="clock">The clock to copy, or <c>null</c> for none.</param>
        /// <param name="activeLot">The lot the player is in, or 0 for none.</param>
        public static NeighbourhoodWorldState FromClock(INeighbourhoodClock clock, int activeLot)
        {
            var state = new NeighbourhoodWorldState
            {
                WorldVersion = CurrentWorldVersion,
                ActiveLot = activeLot
            };

            if (clock == null) return state;

            state.ClockAvailable = clock.IsAvailable;
            state.Hours = clock.Hours;
            state.Minutes = clock.Minutes;
            state.MinuteFractions = clock.MinuteFractions;
            state.TicksPerMinute = clock.TicksPerMinute;
            state.DayOfMonth = clock.DayOfMonth;
            state.Month = clock.Month;
            state.Year = clock.Year;
            state.Ticks = clock.Ticks;
            return state;
        }

        /// <summary>
        /// Field-for-field equality, transit included and in order. See the note on
        /// <see cref="NeighbourhoodTransitRecord.SameAs"/> for why this is a method.
        /// </summary>
        public bool SameAs(NeighbourhoodWorldState other)
        {
            if (other == null) return false;
            if (WorldVersion != other.WorldVersion
                || Flags != other.Flags
                || ActiveLot != other.ActiveLot
                || ClockAvailable != other.ClockAvailable
                || Hours != other.Hours
                || Minutes != other.Minutes
                || MinuteFractions != other.MinuteFractions
                || TicksPerMinute != other.TicksPerMinute
                || DayOfMonth != other.DayOfMonth
                || Month != other.Month
                || Year != other.Year
                || Ticks != other.Ticks
                || TransitCount != other.TransitCount)
            {
                return false;
            }

            for (int i = 0; i < TransitCount; i++)
            {
                if (!Transit[i].SameAs(other.Transit[i])) return false;
            }
            return true;
        }

        public override string ToString()
        {
            if (!ClockAvailable)
            {
                return "world state (no clock) lot " + ActiveLot + " hops " + TransitCount;
            }
            return "world state " + (Year > 0 ? Year + "-" : string.Empty)
                + (Month < 10 ? "0" : string.Empty) + Month
                + "-" + (DayOfMonth < 10 ? "0" : string.Empty) + DayOfMonth
                + " " + (Hours < 10 ? "0" : string.Empty) + Hours
                + ":" + (Minutes < 10 ? "0" : string.Empty) + Minutes
                + " lot " + ActiveLot + " hops " + TransitCount;
        }
    }
}
