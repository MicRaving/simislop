/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using FSO.SimAntics;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The neighbourhood's own clock - the one authority for what time it is.
    ///
    /// <para>
    /// Part A's <see cref="LotClockMirror"/> made a point of not owning time: it borrowed the
    /// active lot's <c>VMClock</c> and made it structurally impossible to write through. That is
    /// the right answer for a screen that has no business advancing anything. A world does: its
    /// lots come and go, the player is regularly standing in none of them, and §5.2 of the Part B
    /// handoff asks for the neighbourhood to keep time while that is true. So this type is the
    /// other half of the pair - it owns the seven time fields and the elapsed tick count, and a
    /// lot's clock is a projection of it rather than the other way round.
    /// </para>
    ///
    /// <para>
    /// <b>Why it delegates its rollover.</b> <see cref="Tick"/> does not carry its own cascade from
    /// minute to hour to day to month to year. It pushes the fields into a private
    /// <see cref="VMClock"/>, calls that clock's <c>Tick</c>, and pulls the fields back, so there
    /// is exactly one implementation of TS1's calendar behaviour in the build and the world cannot
    /// drift from the lots it is supposed to agree with. Every other method on this class is
    /// field copying, which is what makes the round-trip in §5.2 an equality a test can assert.
    /// </para>
    ///
    /// <para>
    /// <b>What it does not touch.</b> <c>VM.GlobalState</c>, ever. The seed below reads the six
    /// time globals and nothing else, and no method on this type takes a <c>VM</c> to write one;
    /// the globals are the engine's, and a clock that can rewrite them would be able to change what
    /// a lot's own primitives read as the time. <c>FirePercent</c> and <c>UTCStart</c> are the lot's
    /// too, and are likewise never read or written here - see <see cref="PushTo"/>.
    /// </para>
    ///
    /// <para>
    /// <b>Thread affinity.</b> The game thread, like everything else that holds a clock. This type
    /// does no locking because there is nothing to lock against: a neighbourhood has one clock and
    /// one writer (the world's tick), and a second one is a bug rather than a race to be tolerated.
    /// </para>
    /// </summary>
    public sealed class WorldClock : INeighbourhoodClock
    {
        /// <summary>
        /// <c>GlobalState</c> index holding the hour. See <c>VMTS1Activator.LoadFromIff</c>, which
        /// assigns <c>VM.Context.Clock.Hours</c> from exactly this word.
        /// </summary>
        public const ushort HoursGlobal = 0;

        /// <summary>
        /// <c>GlobalState</c> index holding the day of the month, 1..30 in TS1's calendar - which
        /// is why every month here has 30 days and February is not shorter.
        /// </summary>
        public const ushort DayOfMonthGlobal = 1;

        /// <summary>
        /// <c>GlobalState</c> index holding the minute. It is 5, not 2: indices 2..4 are the
        /// selected sim and the active family, which are set by the same activator and are not
        /// time.
        /// </summary>
        public const ushort MinutesGlobal = 5;

        /// <summary>
        /// <c>GlobalState</c> index holding the fraction of the minute elapsed, in units of
        /// 1/<c>TicksPerMinute</c> rather than in the clock's own sub-minute ticks. That is why
        /// <see cref="SeedFromGlobalState"/> multiplies by <see cref="TicksPerMinute"/> instead
        /// of copying it: the global is a fraction, the field is a tick count, and the activator's
        /// line does the same conversion.
        /// </summary>
        public const ushort MinuteFractionsGlobal = 6;

        /// <summary><c>GlobalState</c> index holding the month, 1..12.</summary>
        public const ushort MonthGlobal = 7;

        /// <summary><c>GlobalState</c> index holding the year.</summary>
        public const ushort YearGlobal = 8;

        /// <summary>
        /// The sub-minute rate a TS1 lot runs at: 30 ticks to the minute, so one game minute per
        /// real second. It is what <c>VMContext</c> assigns when the content manager reports TS1, and
        /// what <c>VMTS1ActivatorNew</c> writes into its clock marshal.
        /// </summary>
        public const int TicksPerMinuteTs1 = 30;

        private int _hours;
        private int _minutes;
        private int _minuteFractions;
        private int _ticksPerMinute;
        private int _dayOfMonth;
        private int _month;
        private int _year;
        private long _ticks;

        /// <summary>
        /// Has this clock been given a time yet? Distinguishes a neighbourhood sitting at midnight
        /// from a neighbourhood that has never read one - the same distinction
        /// <see cref="UnavailableNeighbourhoodClock"/> makes, on the other side of the seam.
        /// </summary>
        private bool _hasTime;

        /// <summary>
        /// Scratch <c>VMClock</c> used only by <see cref="Tick"/>, and only as the rollover
        /// engine. It is never handed out and never written to by anything else, which is what
        /// makes it safe that its constructor defaults - <c>FirePercent</c> at 20000,
        /// <c>UTCStart</c> at <c>DateTime.UtcNow</c> - cannot leak: <see cref="PushTo"/> overwrites
        /// every field the tick reads before the tick happens.
        /// </summary>
        private readonly VMClock _rollover = new VMClock();

        /// <summary>
        /// A clock holding TS1's launch date and nothing else: 1 June 1997, 00:00, thirty ticks to
        /// the minute, zero elapsed ticks.
        /// </summary>
        /// <remarks>
        /// These are <c>VMClock</c>'s own constructor defaults, deliberately. A world that has not
        /// ticked yet must read the same as a lot that has just been constructed, or "with Part B
        /// off nothing changes" would be a claim about a clock that starts somewhere else.
        /// </remarks>
        public WorldClock()
        {
            _dayOfMonth = 1;
            _month = 6;
            _year = 1997;
            _ticksPerMinute = TicksPerMinuteTs1;
            //VMClock's own default for this field is zero, and PushTo now declines to write a
            //non-positive rate - so a clock that ticked before it had ever pulled from a lot would
            //roll its scratch clock's minute on every single tick. One line, and Tick is correct
            //from the first call rather than from the first lot load.
            _rollover.TicksPerMinute = TicksPerMinuteTs1;
        }

        public bool IsAvailable
        {
            get { return _hasTime; }
        }

        public int Hours
        {
            get { return _hours; }
        }

        public int Minutes
        {
            get { return _minutes; }
        }

        public int MinuteFractions
        {
            get { return _minuteFractions; }
        }

        public int TicksPerMinute
        {
            get { return _ticksPerMinute; }
        }

        public int DayOfMonth
        {
            get { return _dayOfMonth; }
        }

        public int Month
        {
            get { return _month; }
        }

        public int Year
        {
            get { return _year; }
        }

        public int Seconds
        {
            get
            {
                //VMClock divides unguarded, so a lot whose rate has been zeroed would throw here;
                //the neighbourhood must not be what surfaces that
                return _ticksPerMinute > 0 ? (_minuteFractions * 60) / _ticksPerMinute : 0;
            }
        }

        public long Ticks
        {
            get { return _ticks; }
        }

        /// <summary>
        /// Seed this clock from a game's <c>GlobalState</c> array - the six time words
        /// <c>VMTS1Activator.LoadFromIff</c> copies into a freshly loaded lot's clock.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The inverse of that seed, so a lot loaded from a <c>HouseNN.iff</c> and a world seeded
        /// from the same globals agree field for field. That is the property §5.2 asks for, and it
        /// is why the indices are named constants rather than literals: the activator's line
        /// numbers and these are the same six words, and a change to one that is not a change to
        /// the other would silently move time by an hour.
        /// </para>
        /// <para>
        /// <b>Index-guarded, per index.</b> A <c>short[]</c> shorter than
        /// <see cref="YearGlobal"/> + 1 - a corrupt save, a truncated <c>SIMI</c>, or a caller
        /// passing a scratch array it only meant to fill with the hour - is not an error here. Each
        /// word is read only if the array actually has it, and the ones that are missing leave
        /// this clock's current value alone. Throwing would mean one bad array could not be
        /// partially useful, and the caller's alternative is to re-derive the indices anyway.
        /// </para>
        /// <para>
        /// <b>Not normalised, and not copied into the array.</b> The values land exactly as the
        /// globals have them, hour 47 or day 0 included, because that is what the activator does
        /// and clamping here would move the divergence between the world and the lot from the
        /// corrupt file into the clock. The array is read and never written: the globals are the
        /// engine's, and <see cref="PullFrom"/>/<see cref="PushTo"/> are the only paths between a
        /// lot's clock and this one.
        /// </para>
        /// </remarks>
        /// <param name="globals">The <c>GlobalState</c> array, or <c>null</c> for no seed at all.</param>
        public void SeedFromGlobalState(short[] globals)
        {
            if (globals == null) return;

            if (globals.Length > HoursGlobal) _hours = globals[HoursGlobal];
            if (globals.Length > DayOfMonthGlobal) _dayOfMonth = globals[DayOfMonthGlobal];
            if (globals.Length > MinutesGlobal) _minutes = globals[MinutesGlobal];
            if (globals.Length > MinuteFractionsGlobal) _minuteFractions = globals[MinuteFractionsGlobal] * _ticksPerMinute;
            if (globals.Length > MonthGlobal) _month = globals[MonthGlobal];
            if (globals.Length > YearGlobal) _year = globals[YearGlobal];

            //Ticks is deliberately not seeded: the six globals are a wall-clock reading and carry
            //no elapsed time, so a clock that had been running keeps its count rather than being
            //wound back to zero by the arrival of a lot file
            _hasTime = true;
        }

        /// <summary>
        /// Take the time out of a lot's <c>VMClock</c> and make it the world's time.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the half of the lot-switch sync that runs on the way out of a lot, and the half
        /// an off-screen lot runs when it is ticked (§5.2's "pushes back on save/tick"). All eight
        /// time fields move together, so the clock cannot be half-synced.
        /// </para>
        /// <para>
        /// <c>FirePercent</c> and <c>UTCStart</c> are not among them. <c>FirePercent</c> is the
        /// lot's own fire budget and is not a time at all; <c>UTCStart</c> is the anchor the lot
        /// derives <c>UTCNow</c> from, and moving it would shift the lot's wall clock by an
        /// arbitrary amount. Both belong to the lot, which is the same reason this type never
        /// reaches for <c>VM.GlobalState</c> - see the class remarks.
        /// </para>
        /// </remarks>
        /// <param name="clock">The lot's clock. Required; a null here is a caller bug, not an
        /// unavailable neighbourhood, so it throws rather than reading as midnight.</param>
        public void PullFrom(VMClock clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            _hours = clock.Hours;
            _minutes = clock.Minutes;
            _minuteFractions = clock.MinuteFractions;
            _ticksPerMinute = clock.TicksPerMinute;
            _dayOfMonth = clock.DayOfMonth;
            _month = clock.Month;
            _year = clock.Year;
            _ticks = clock.Ticks;
            _hasTime = true;
        }

        /// <summary>
        /// Write this clock's time into a lot's <c>VMClock</c> - the half of the sync that runs on
        /// the way into a lot, and the reason a lot reloaded from its own file starts where the
        /// neighbourhood has got to rather than where its globals say.
        /// </summary>
        /// <remarks>
        /// <c>FirePercent</c> and <c>UTCStart</c> are left alone, for the reasons given on
        /// <see cref="PullFrom"/>. The round trip this and that method make is exact: pull from a
        /// lot, push to a second lot, and the two are field-for-field identical including
        /// <see cref="Ticks"/>.
        /// </remarks>
        /// <param name="clock">The lot's clock. Required; a null here is a caller bug and throws.</param>
        public void PushTo(VMClock clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            clock.Hours = _hours;
            clock.Minutes = _minutes;
            clock.MinuteFractions = _minuteFractions;
            //Never a non-positive rate. VMClock.Seconds divides by TicksPerMinute and VMClock.Tick
            //compares against it, both unguarded, so a lot handed zero throws the first time
            //anything reads its seconds and runs its minutes up thirty times too fast afterwards.
            //Nothing reachable produces one - the default is TicksPerMinuteTs1 and PullFrom reads a
            //live lot's own - but this writes into somebody else's object on the way into a lot
            //load, and one comparison is the price of never being why a lot's clock stopped.
            if (_ticksPerMinute > 0) clock.TicksPerMinute = _ticksPerMinute;
            clock.DayOfMonth = _dayOfMonth;
            clock.Month = _month;
            clock.Year = _year;
            clock.Ticks = _ticks;
            _hasTime = true;
        }

        /// <summary>
        /// Advance the neighbourhood by one sub-minute tick.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Delegated to a private <see cref="VMClock"/> on purpose. TS1's calendar is not uniform -
        /// a month is 30 days, the day goes 1..30 and the month 1..12 - and re-deriving that here
        /// would give the world a second calendar that could disagree with every lot's. So the
        /// fields are pushed into the scratch clock, its own <c>Tick</c> does the carrying, and the
        /// fields are pulled back out; the only thing this method contributes is the decision to
        /// keep the two in step.
        /// </para>
        /// <para>
        /// It is one tick, not one frame. The caller owns the frame budget: B6's off-screen lots
        /// are meant to be able to tick the world thousands of times for an hour of game time, and
        /// a world that ticked itself inside this method could not be fast-forwarded.
        /// </para>
        /// </remarks>
        public void Tick()
        {
            PushTo(_rollover);
            _rollover.Tick();
            PullFrom(_rollover);
        }

        public override string ToString()
        {
            return (IsAvailable ? string.Empty : "unset ") + DayOfMonth + "/" + Month + "/" + Year
                + " " + Hours + ":" + Minutes + " (" + MinuteFractions + "/" + TicksPerMinute + ")"
                + " ticks " + Ticks;
        }
    }
}
