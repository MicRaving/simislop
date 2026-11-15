/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Model;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// How much of a lot happens while nobody is standing in it. Handoff §B6 task 3's three
    /// settings, and they are a budget dial, so the middle one is the one a world runs at.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Paused freezes everything, including the clock.</b> Not "the clock keeps running but
    /// the Sims do not" - that is a different product, and it is the reading a reader is most
    /// likely to bring to the word "paused" from a game menu. So
    /// <see cref="OffscreenSimulator.Tick"/> under <see cref="Paused"/> does not tick the
    /// <see cref="WorldClock"/>, does not write a single motive, does not even create a record for
    /// a lot being looked at for the first time, and returns a result whose
    /// <see cref="OffscreenTickResult.Changed"/> is <c>false</c>. Who paused, and why, is not this
    /// type's question.
    /// </para>
    ///
    /// <para>
    /// <b>Coarse is the default</b>, exactly as the handoff says, and the reason is a budget one.
    /// <see cref="Coarse"/> applies one decay step per
    /// <see cref="OffscreenSimulator.CoarseStepGameMinutes"/> game minutes;
    /// <see cref="Full"/> applies one per <see cref="OffscreenSimulator.FullStepGameMinutes"/>,
    /// which is The Sims 1's own cadence - <c>VMTS1MotiveDecay</c> guards on
    /// <c>minutes/2 == LastMinute</c>, so the engine itself decays every other clock minute. That
    /// is fifteen times the work for a difference no player can read off a neighbour's motives.
    /// Coarse also declines to read per-Sim personality, which is a second and separate saving:
    /// it is why the two differ in <em>what they know</em> and not only in how often they look.
    /// </para>
    ///
    /// <para>
    /// <b>Neither non-paused setting touches a live VM.</b> The <see cref="LotStore"/> holds
    /// marshals, not <c>VM</c>s, so this enum cannot describe a setting that would; see
    /// <see cref="OffscreenSimulator"/> for the structural argument.
    /// </para>
    /// </remarks>
    public enum OffscreenFidelity
    {
        /// <summary>Nothing happens: no clock tick, no motive change, no record.</summary>
        Paused = 0,

        /// <summary>
        /// The default. One decay step per
        /// <see cref="OffscreenSimulator.CoarseStepGameMinutes"/> game minutes, from the
        /// population-average personality rather than each Sim's own.
        /// </summary>
        Coarse = 1,

        /// <summary>
        /// The Sims 1 cadence and per-Sim personality, applied to the same stored data. Still
        /// off-screen: this never runs a lot's own tick.
        /// </summary>
        Full = 2
    }

    /// <summary>
    /// One neighbourhood's account of one lot it is not showing, for the lots whose motives are
    /// not reachable.
    ///
    /// <para>
    /// <b>Why a lot needs one of these at all.</b> A lot is off-screen in one of two ways and the
    /// two need different answers. A lot the store holds a <see cref="VMMarshal"/> for has real
    /// motive data sitting in it - <see cref="VMAvatarMarshal.MotiveData"/>, one <c>short[16]</c>
    /// per Sim - and the simulator writes that, in place. A lot the store holds nothing for, or
    /// holds only a hollow marshal, has motive data nowhere: <c>VMHollowMarshal</c> keeps game
    /// objects and nothing else, deliberately, and an unentered lot exists as a
    /// <c>HouseNN.iff</c> on disk. For those this record <em>is</em> the lot's simulated state,
    /// and the model is written to be plausible rather than faithful, because there is nothing to
    /// be faithful to.
    /// </para>
    ///
    /// <para>
    /// <b>Where it lives, and why not in the world state.</b> In the
    /// <see cref="OffscreenSimulator"/> that produced it, readable through
    /// <see cref="OffscreenSimulator.TryGetRecord"/>. The eventual home is a per-lot member of
    /// <see cref="NeighbourhoodWorldState"/>, which is B7's schema-2 payload: these are session
    /// facts about a lot, and sidecar schema 2 is the thing meant to hold them. B6 does not add
    /// them, for two reasons that are both about not lying. The record is a model of a lot with no
    /// contents in memory, and a world that quits and resumes has not lost anything by not
    /// restoring it - a lot that was never loaded had no state to lose either. And a sidecar that
    /// carried it would need a migration this milestone has no schema number to give it. So the
    /// per-lot summary the layout already has a field for,
    /// <see cref="NeighbourhoodLot.LastSimulated"/>, is stamped instead: the forward-compatible
    /// half of "this lot moved" is recorded now, and the motive numbers stay in memory.
    /// </para>
    ///
    /// <para>
    /// <b>Aggregate, not per-Sim.</b> A record describes a <em>lot</em>. Its
    /// <see cref="Motives"/> are one value per motive, and for a lot that does have a marshal they
    /// are the mean of its Sims after the tick, so a caller asking "how is this neighbour doing"
    /// gets a number rather than a list. <see cref="AvatarCount"/> says how many were averaged,
    /// or -1 for a lot with no marshal - which is the honest answer, where zero would be a
    /// different claim.
    /// </para>
    /// </summary>
    public sealed class OffscreenLotRecord
    {
        /// <summary>How many motives a TS1 lot decays. Seven, and it is not a coincidence.</summary>
        public const int MotiveCount = 7;

        /// <summary>The lowest motive a seeded lot starts on.</summary>
        public const int MinSeededMotive = 40;

        /// <summary>One past the highest motive a seeded lot starts on.</summary>
        public const int MaxSeededMotive = 100;

        private readonly short[] _motives = new short[MotiveCount];
        private readonly short[] _fractions = new short[MotiveCount];
        private readonly Dictionary<int, short[]> _avatarFractions = new Dictionary<int, short[]>();

        private int _avatarCount = -1;
        private long _steps;
        private long _lastSimulatedTick;
        private short _mood;
        private bool _seeded;
        private bool _started;
        private int _companySteps;
        private int _lastVisitorCount;
        private short _roomScore;
        private bool _roomScored;
        private short _familyNumber = OffscreenSimulator.NoFamilyNumber;
        private int _estimatedDailyIncome;
        private int _paydayHour = OffscreenSimulator.NoPaydayHour;
        private int _estimatedPaydays;

        /// <summary>Which lot this is. Never <c>0</c>; see the constructor.</summary>
        public short Lot { get; private set; }

        internal OffscreenLotRecord(short lot)
        {
            //Lot 0 is "the player is in no lot" (INeighbourhoodWorld.ActiveLotNumber), so a record
            //claiming lot 0 would be a record about nothing. Only a caller can get here, and a
            //silently-wrong lot number is exactly the class of mistake this neighbourhood exists
            //to make impossible - so it throws rather than clamping.
            if (lot == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(lot), lot,
                    "Lot 0 means 'in no lot' and is not a lot a record can describe.");
            }
            Lot = lot;
        }

        /// <summary>
        /// The lot's aggregate motive vector, in <c>VMTS1MotiveDecay.DecrementMotives</c> order -
        /// hunger, comfort, hygiene, bladder, energy, fun, social.
        /// </summary>
        /// <remarks>
        /// The live array, not a copy, so a per-frame reader does not allocate. The only writer is
        /// <see cref="OffscreenSimulator"/>, which is why the setters are internal and the getter
        /// is a <see cref="IReadOnlyList{T}"/> - a caller can read every value and change none of
        /// them without this type needing a defensive copy per access.
        /// </remarks>
        public IReadOnlyList<short> Motives
        {
            get { return _motives; }
        }

        /// <summary>
        /// The lot's mood, recomputed the way <c>VMTS1MotiveDecay</c> recomputes it: the seven
        /// decaying motives plus the room score, divided by eight.
        /// </summary>
        public short Mood
        {
            get { return _mood; }
        }

        /// <summary>
        /// How many Sims were averaged into <see cref="Motives"/>, or -1 for a lot with no marshal,
        /// where there are no Sims to average.
        /// </summary>
        public int AvatarCount
        {
            get { return _avatarCount; }
        }

        /// <summary>
        /// How many decay steps this record has actually been given. Not the elapsed time: the two
        /// fidelities reach the same state by different numbers of steps, and this is the honest
        /// count of the work done.
        /// </summary>
        public long Steps
        {
            get { return _steps; }
        }

        /// <summary>
        /// The <see cref="WorldClock.Ticks"/> value this record was last advanced to, and the real
        /// resume point. The reason a fidelity switch does not replay a gap, and the reason a lot
        /// added to the store mid-window owes only the part of that window it was present for.
        /// </summary>
        public long LastSimulatedTick
        {
            get { return _lastSimulatedTick; }
        }

        /// <summary>Has this lot been simulated at all yet, as opposed to merely recorded?</summary>
        public bool HasSimulated
        {
            get { return _started; }
        }

        /// <summary>
        /// How many of this lot's decay steps ran while somebody was in it - the B8 visitor count,
        /// counted in the same unit as <see cref="Steps"/> rather than in visitors, because a step
        /// is the granularity the model has and a visitor is not.
        /// </summary>
        /// <remarks>
        /// Always zero unless a caller handed <see cref="OffscreenSimulator.VisitorLots"/> a
        /// source: the model has no opinion about company of its own, and inventing one would be
        /// the same mistake as inventing an income. What the count is for is a caller that wants
        /// to ask "was this lot busy?" without also holding the schedule that answered the
        /// question.
        /// </remarks>
        public int CompanySteps
        {
            get { return _companySteps; }
        }

        /// <summary>
        /// How many were in the lot at the most recent step counted by
        /// <see cref="CompanySteps"/>; zero if there has not been one. The head count, not a
        /// rate: <see cref="CompanyScaleFor"/> turns it into the one number the decay actually
        /// uses.
        /// </summary>
        public int LastVisitorCount
        {
            get { return _lastVisitorCount; }
        }

        /// <summary>Has any of this lot's steps run with somebody in it?</summary>
        public bool HadCompany
        {
            get { return _companySteps > 0; }
        }

        // ---- the two models this record now carries ---------------------------------------------------

        /// <summary>
        /// The room score this lot is modelled at, in <c>VMRoomScore.Compute</c>'s own
        /// <c>-100..100</c> range, and folded into <see cref="Mood"/> exactly as
        /// <c>VMTS1MotiveDecay</c> folds in <c>VMMotive.Room</c>. Zero for a lot with no modelled
        /// score, which is every lot unless <see cref="OffscreenSimulator.ModelsRoomScore"/> is on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>It is a model and not a reading.</b> The engine's value is
        /// <c>context.GetRoomScore(context.GetRoomAt(avatar.Position))</c>: a room table built when
        /// the lot loads, the room a given Sim is standing in, and the per-object room impact that
        /// decides the score. A marshal has none of the three - a room is a thing a running lot
        /// computes. So this number is bounded, deterministic and derived from what the store and
        /// the layout actually know, and it is documented there rather than here. What it is
        /// <em>for</em>, off-screen, is mood: the engine adds room to the mood sum and nothing else
        /// uses it for a Sim.
        /// </para>
        /// <para>
        /// <b>One number per lot, and it does not move.</b> Nothing happens in a lot the player
        /// is not in, so neither do its rooms. A per-step draw here would be a second lottery on a
        /// value the engine itself changes on a scale of hours, and it would make a lot's mood a
        /// function of how the absence was chopped into frames.
        /// </para>
        /// </remarks>
        public short RoomScore
        {
            get { return _roomScore; }
        }

        /// <summary>
        /// Does this record carry a modelled room score, as opposed to the zero a lot with no
        /// describable rooms reports? A caller that wants to show a room score should ask this
        /// first: the two answers - "modelled" and "nothing was there to model" - are different
        /// claims and 0 alone cannot say which is which.
        /// </summary>
        public bool HasRoomScore
        {
            get { return _roomScored; }
        }

        /// <summary>
        /// The <c>FAMI</c> chunk id this lot's money is keyed on, read out of its stored Sims'
        /// <c>PersonData</c>, or <see cref="OffscreenSimulator.NoFamilyNumber"/> for a lot with no
        /// family to key on - a hollow lot, a marshal with no Sims, or a person whose family was
        /// never assigned.
        /// </summary>
        /// <remarks>
        /// The number the family authority itself keys on:
        /// <c>SimitoneGlobalLink.ResolveFamily</c> reads the same
        /// <see cref="VMPersonDataVariable.TS1FamilyNumber"/> word off the same avatar. It is
        /// carried here so an estimate can be attributed, and it is emphatically not a handle:
        /// nothing in this file resolves it, reads a budget or moves money.
        /// </remarks>
        public short FamilyNumber
        {
            get { return _familyNumber; }
        }

        /// <summary>
        /// What this household is modelled as earning per game day, in
        /// <see cref="OffscreenSimulator.MaxEstimatedDailyIncome"/> at the very most, and zero for
        /// a lot with no modelled estimate.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A rate, never a balance.</b> A lot's funds belong to <c>FAMI</c> and move only
        /// through <c>SimitoneGlobalLink.TransactBudgetForFamily</c>; this is the rate the
        /// off-screen model imagines, and there is no code path from here to a budget. The reason
        /// that is a structural claim rather than a promise is
        /// <see cref="EstimatedIncomePaid"/>: the running total is
        /// <em>the product of this number and <see cref="EstimatedPaydays"/></em>, computed on
        /// read, so there is no stored balance that could drift away from the rate that produced
        /// it. A ledger has hidden state; this has none.
        /// </para>
        /// <para>
        /// The unit is simoleons per game day and the number is this model's own. TS1's real pay is
        /// a content value - a job level's <c>Salary</c>, and the hours it is worked, come out of
        /// <c>work.iff</c> and not out of code - so an estimate that could be read from a marshal
        /// does not exist, and this one says so by being bounded and obviously small.
        /// </para>
        /// </remarks>
        public int EstimatedDailyIncome
        {
            get { return _estimatedDailyIncome; }
        }

        /// <summary>
        /// Does this lot carry an income estimate at all - which is a question about the family, not
        /// about the money: a household with a family and nobody in a job is modelled at zero, and
        /// that zero is an answer rather than an absence.
        /// </summary>
        public bool HasIncomeEstimate
        {
            get { return _familyNumber != OffscreenSimulator.NoFamilyNumber; }
        }

        /// <summary>
        /// The hour of the day this household is modelled as being paid, or
        /// <see cref="OffscreenSimulator.NoPaydayHour"/> when it has no estimate. Drawn once from
        /// the lot's own stream, because a neighbourhood in which every household is paid at four
        /// in the morning is a neighbourhood with one household in it.
        /// </summary>
        public int PaydayHour
        {
            get { return _paydayHour; }
        }

        /// <summary>
        /// How many paydays the model has paid this lot since its record was made. A count of
        /// events, in the same spirit as <see cref="Steps"/>: the honest tally of work done, not a
        /// money total.
        /// </summary>
        public int EstimatedPaydays
        {
            get { return _estimatedPaydays; }
        }

        /// <summary>
        /// What the model believes it has paid this lot, which is exactly
        /// <see cref="EstimatedDailyIncome"/> times <see cref="EstimatedPaydays"/>.
        /// </summary>
        /// <remarks>
        /// A derived property and not a field, on purpose. The property is what makes "this is not
        /// a second money ledger" checkable: the total cannot disagree with the rate and the count
        /// that are printed either side of it, so there is nothing to reconcile and nothing to
        /// migrate, and a caller that wants a balance has to say that it wants a balance rather than
        /// being handed one.
        /// </remarks>
        public long EstimatedIncomePaid
        {
            get { return (long)_estimatedDailyIncome * _estimatedPaydays; }
        }

        /// <summary>
        /// Does this record carry its own modelled motives, as opposed to mirroring a marshal's?
        /// True for a hollow or unentered lot, which is the case where
        /// <see cref="OffscreenLotRecord"/> is the only account of the lot that exists.
        /// </summary>
        public bool IsCoarseModel
        {
            get { return _avatarCount < 0; }
        }

        /// <summary>One motive, by index into <see cref="Motives"/>.</summary>
        /// <param name="index">0..6, in <c>DecrementMotives</c> order.</param>
        public short MotiveAt(int index)
        {
            if (index < 0 || index >= MotiveCount)
            {
                throw new ArgumentOutOfRangeException(nameof(index), index,
                    "A TS1 lot decays seven motives, and the count is not negotiable.");
            }
            return _motives[index];
        }

        /// <summary>
        /// The writable aggregate, for the coarse decay to run against. Internal because the
        /// simulator is the only thing that may move these numbers.
        /// </summary>
        internal short[] MotivesBuffer
        {
            get { return _motives; }
        }

        /// <summary>This lot's own accumulator, for a lot with no per-Sim data to carry one.</summary>
        internal short[] Fractions
        {
            get { return _fractions; }
        }

        /// <summary>
        /// The 1/1000ths accumulator for one Sim, created on first ask. Keyed by object id rather
        /// than by position in the entity array, so a marshal rebuilt in a different order keeps
        /// the rounding it had.
        /// </summary>
        internal short[] FractionsFor(int objectId)
        {
            short[] fractions;
            if (!_avatarFractions.TryGetValue(objectId, out fractions))
            {
                fractions = new short[MotiveCount];
                _avatarFractions.Add(objectId, fractions);
            }
            return fractions;
        }

        /// <summary>Set the aggregate after a tick. The mean of <paramref name="avatars"/> Sims.</summary>
        internal void SetAggregate(int avatars, short[] motives, short mood)
        {
            _avatarCount = avatars;
            for (int i = 0; i < MotiveCount; i++) _motives[i] = motives[i];
            _mood = mood;
        }

        /// <summary>Set the mood alone, for a coarse lot whose motives are already in place.</summary>
        internal void SetMood(short mood)
        {
            _mood = mood;
        }

        /// <summary>
        /// Draw this lot's starting motives, once, from the seed.
        ///
        /// <para>
        /// Drawn rather than fixed because a neighbourhood in which every never-visited lot starts
        /// at the same numbers is a neighbourhood where the world seed does not reach the one part
        /// of the world that is always off-screen. The range is deliberately mid-table: a lot
        /// nobody has entered is not a lot whose Sims are starving, and letting the decay be what
        /// makes them hungry is the whole point of the model.
        /// </para>
        /// </summary>
        internal void SeedIfNew(ref NeighbourhoodRng rng)
        {
            if (_seeded) return;
            for (int i = 0; i < MotiveCount; i++)
            {
                _motives[i] = (short)rng.NextInt(MinSeededMotive, MaxSeededMotive);
            }
            _mood = MoodOfBuffer(0);
            _seeded = true;
        }

        /// <summary>
        /// Whether the seed draw has happened. Distinct from <see cref="HasSimulated"/>: a lot can
        /// be seeded and never stepped, which is what a lot first seen on a tick with no time
        /// elapsed looks like.
        /// </summary>
        internal bool Seeded
        {
            get { return _seeded; }
        }

        /// <summary>Record that the lot was simulated up to <paramref name="tick"/>.</summary>
        internal void MarkSimulated(long tick, long steps)
        {
            _lastSimulatedTick = tick;
            _steps += steps;
            _started = true;
        }

        /// <summary>
        /// Model this lot's room score, once, from the one thing a store and a layout both know
        /// about a house - how big it is - and one bounded per-lot draw standing in for the objects
        /// the score is really made of.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What it can see, and what it cannot.</b> It can see the lot's footprint
        /// (<see cref="NeighbourhoodLot.Size"/>, the <c>Size x Size</c> the activator builds from
        /// the lot's own file) and how the lot is zoned. It cannot see a single object, a wall, a
        /// tile, a light, or which room anybody is standing in - and a room score is a sum of object
        /// room impacts divided by an area, so the footprint is the only term here that is a
        /// measurement rather than a guess. How many people the lot holds is on the marshal and is
        /// deliberately <em>not</em> used: dividing one guessed quantity by another is how a model
        /// stops being bounded by anything.
        /// </para>
        /// <para>
        /// <b>Refuses rather than guesses twice.</b> A lot the layout has never heard of gets
        /// nothing: the retry happens on the next tick instead, because a layout is a caller's to
        /// rebuild and a lot can be added to one at any time, and because the answer is a pure
        /// function of the seed either way. A lot with no house file gets nothing for a different
        /// reason - an empty plot has no rooms to score, and zero is what today's build says about
        /// every lot, so nothing is claimed that was not already claimed.
        /// </para>
        /// </remarks>
        /// <param name="lot">The layout's record for this lot, or <c>null</c> for "unknown".</param>
        /// <param name="rng">
        /// This lot's own labelled sub-stream for the room model, so the draw cannot move a decay
        /// draw - see <see cref="OffscreenSimulator.RoomStreamLabel"/>.
        /// </param>
        internal void ModelRoomIfNew(NeighbourhoodLot lot, ref NeighbourhoodRng rng)
        {
            if (_roomScored) return;
            if (lot == null || !lot.HasHouse) return;

            var size = Math.Min(Math.Max(0, lot.Size), NeighbourhoodLayout.MaxLotSize);
            var area = Math.Min(size * size, OffscreenSimulator.MaxRoomAreaTiles);
            var impact = OffscreenSimulator.RoomImpactFor(lot.Zone)
                + rng.NextInt(0, OffscreenSimulator.RoomImpactSpread + 1);
            //The engine's own arithmetic, on the engine's own bound. Outside-ness is false because
            //the score being modelled is a room's, and a lot's rooms are inside it; the area term
            //is real, and the impact term is the guess.
            _roomScore = VMRoomScore.Compute(false, area, impact);
            _roomScored = true;
        }

        /// <summary>
        /// Model what this household earns, once, from the family its stored Sims name and the jobs
        /// their person data records - the two things about money a marshal actually carries.
        /// </summary>
        /// <remarks>
        /// <b>No family, no estimate.</b> A hollow lot, a marshal with no Sims and a person whose
        /// family was never assigned all reach this with
        /// <see cref="OffscreenSimulator.NoFamilyNumber"/>, and all three are left with nothing
        /// rather than with a plausible average. The reason is the routing, not squeamishness:
        /// money is a family's, a family is identified by a chunk id, and a number attached to
        /// nothing is how a second ledger starts.
        /// </remarks>
        /// <param name="family">The <c>FAMI</c> chunk id, or <see cref="OffscreenSimulator.NoFamilyNumber"/>.</param>
        /// <param name="earners">How many of the lot's stored Sims have a job type.</param>
        /// <param name="promotion">The highest promotion level among them.</param>
        /// <param name="rng">This lot's own labelled sub-stream for the income model.</param>
        internal void ModelIncomeIfNew(short family, int earners, int promotion, ref NeighbourhoodRng rng)
        {
            if (_familyNumber != OffscreenSimulator.NoFamilyNumber) return;
            if (family <= OffscreenSimulator.NoFamilyNumber) return;

            //One draw for the household, not one per earner: the rate is a single number a caller
            //can show, and N draws would make the total a function of how many Sims happen to be
            //in the marshal rather than of what they earn.
            var householdTerm = rng.NextInt(0, OffscreenSimulator.EarnerIncomeSpread + 1);
            _paydayHour = rng.NextInt(0, OffscreenSimulator.HoursPerGameDay);
            _estimatedDailyIncome = OffscreenSimulator.DailyIncomeFor(earners, promotion, householdTerm);
            _familyNumber = family;
        }

        /// <summary>
        /// Record that the model paid this lot <paramref name="paydays"/> times. Internal because
        /// the simulator is the only thing that may say money moved, for the same reason
        /// <see cref="AddCompany"/> is: a record that claimed a payday no clock produced would be a
        /// claim about a transaction that never happened.
        /// </summary>
        internal void AddPaydays(int paydays)
        {
            if (paydays <= 0) return;
            _estimatedPaydays += paydays;
        }

        /// <summary>
        /// Record that <paramref name="steps"/> of this lot's decay ran with
        /// <paramref name="visitors"/> people in it. Internal because the simulator is the only
        /// thing that may say a lot had company - a record that claimed otherwise on its own
        /// would be a claim about a visit that no schedule ever reported.
        /// </summary>
        internal void AddCompany(int steps, int visitors)
        {
            if (steps <= 0) return;
            _companySteps += steps;
            _lastVisitorCount = visitors;
        }

        /// <summary>
        /// Mood from the aggregate, on the engine's own rule: the seven decaying motives plus the
        /// room score, over eight.
        /// </summary>
        /// <param name="room">The room score to fold in; zero for a lot with no room table.</param>
        internal short MoodOfBuffer(short room)
        {
            var sum = room;
            for (int i = 0; i < MotiveCount; i++) sum += _motives[i];
            return (short)OffscreenSimulator.ClampMotive(sum / 8);
        }

        /// <summary>
        /// Field-for-field equality, accumulators included, for the determinism assertions. A
        /// method rather than an <c>Equals</c> override because the type is mutable, and a hash of
        /// a mutable object is a trap.
        /// </summary>
        public bool SameAs(OffscreenLotRecord other)
        {
            if (other == null) return false;
            if (Lot != other.Lot || _avatarCount != other._avatarCount || _steps != other._steps
                || _lastSimulatedTick != other._lastSimulatedTick || _mood != other._mood
                || _seeded != other._seeded || _started != other._started
                || _companySteps != other._companySteps
                || _lastVisitorCount != other._lastVisitorCount
                || _roomScore != other._roomScore || _roomScored != other._roomScored
                || _familyNumber != other._familyNumber
                || _estimatedDailyIncome != other._estimatedDailyIncome
                || _paydayHour != other._paydayHour
                || _estimatedPaydays != other._estimatedPaydays)
            {
                return false;
            }
            for (int i = 0; i < MotiveCount; i++)
            {
                if (_motives[i] != other._motives[i]) return false;
                if (_fractions[i] != other._fractions[i]) return false;
            }
            if (_avatarFractions.Count != other._avatarFractions.Count) return false;
            foreach (var pair in _avatarFractions)
            {
                short[] mine;
                if (!other._avatarFractions.TryGetValue(pair.Key, out mine)) return false;
                for (int i = 0; i < MotiveCount; i++)
                {
                    if (pair.Value[i] != mine[i]) return false;
                }
            }
            return true;
        }

        public override string ToString()
        {
            var names = VMTS1MotiveDecay.DecrementMotives;
            var parts = new string[MotiveCount];
            for (int i = 0; i < MotiveCount; i++)
            {
                parts[i] = (names != null && i < names.Length ? names[i].ToString() : i.ToString())
                    + " " + _motives[i];
            }
            return "lot " + Lot + " mood " + _mood + " " + (_avatarCount < 0 ? "coarse" : _avatarCount + " sims")
                + " [" + string.Join(", ", parts) + "] steps " + _steps
                + (_companySteps > 0 ? " with company " + _companySteps + " step(s), x" + _lastVisitorCount : string.Empty)
                + (_roomScored ? ", room " + _roomScore : string.Empty)
                + (_familyNumber != OffscreenSimulator.NoFamilyNumber
                    ? ", family " + _familyNumber + " earns " + _estimatedDailyIncome + "/day"
                        + (_estimatedPaydays > 0 ? ", " + _estimatedPaydays + " payday(s)" : string.Empty)
                    : string.Empty);
        }
    }

    /// <summary>
    /// What one call to <see cref="OffscreenSimulator.Tick"/> did.
    /// </summary>
    /// <remarks>
    /// A value rather than a <c>bool</c>, because the caller that owns the frame budget needs to
    /// tell "nothing happened" from "a clock moved and three lots decayed" - the second wants a
    /// redraw and a log line, the first wants silence. The handoff's own
    /// <c>INeighbourhoodWorld.Tick</c> returns a <c>bool</c> for a much smaller job.
    /// </remarks>
    public sealed class OffscreenTickResult
    {
        /// <summary>The fidelity this tick ran at. <see cref="OffscreenFidelity.Paused"/> iff nothing happened.</summary>
        public OffscreenFidelity Fidelity { get; private set; }

        /// <summary>Was this a paused tick? If so every other count is zero and the clock did not move.</summary>
        public bool Paused { get; private set; }

        /// <summary>Sub-minute ticks the <see cref="WorldClock"/> was advanced by.</summary>
        public int ClockTicks { get; private set; }

        /// <summary>Decay steps applied across every lot, at this fidelity's cadence.</summary>
        public int DecaySteps { get; private set; }

        /// <summary>Lots that received at least one decay step.</summary>
        public int LotsDecayed { get; private set; }

        /// <summary>Sims whose stored motive table was written. Zero for a lot with no marshal.</summary>
        public int AvatarsDecayed { get; private set; }

        /// <summary>
        /// Lots whose whole backlog was refused because it exceeded
        /// <see cref="OffscreenSimulator.MaxDecayStepsPerLot"/>. The work is <em>deferred</em>, not
        /// dropped - <see cref="OffscreenLotRecord.LastSimulatedTick"/> simply lags - so this is a
        /// statement about pace rather than about loss.
        /// </summary>
        public int LotsBacklogged { get; private set; }

        /// <summary>
        /// Real seconds of requested time this tick declined to simulate, because they exceeded
        /// <see cref="OffscreenSimulator.MaxSecondsPerTick"/>. Non-zero means a very long absence
        /// was truncated, and it is reported rather than absorbed: a silent truncation reads as a
        /// bug, a loud one reads as a design decision.
        /// </summary>
        public int TruncatedSeconds { get; private set; }

        /// <summary>
        /// Lots that had company for at least one decay step this tick, counted by
        /// <see cref="OffscreenSimulator.VisitorLots"/>. Always zero with no source, and always
        /// zero under <see cref="OffscreenFidelity.Paused"/>, which never reaches the decay at all.
        /// </summary>
        public int LotsWithCompany { get; private set; }

        /// <summary>
        /// Decay steps applied while somebody was in the lot, summed over every lot. The
        /// denominator a caller divides by <see cref="DecaySteps"/> to get "how much of this
        /// neighbourhood's off-screen decay happened with the doors open".
        /// </summary>
        public int CompanySteps { get; private set; }

        /// <summary>
        /// Lots that were modelled as paid at least once this tick, out of every lot with an
        /// income estimate. Always zero with no estimate, always zero under
        /// <see cref="OffscreenFidelity.Paused"/>, and always zero with
        /// <see cref="OffscreenSimulator.EstimatesHouseholdIncome"/> off.
        /// </summary>
        public int LotsPaid { get; private set; }

        /// <summary>
        /// Paydays the model paid across every lot this tick. A count of modelled events and not
        /// a sum of money: the money is on
        /// <see cref="OffscreenLotRecord.EstimatedIncomePaid"/>, and it is a product of two
        /// numbers rather than a running total that could be reconciled against anything.
        /// </summary>
        public int ModelledPaydays { get; private set; }

        /// <summary>Did anything change at all - the clock, a motive, or a record?</summary>
        public bool Changed
        {
            get { return ClockTicks > 0 || DecaySteps > 0 || LotsDecayed > 0; }
        }

        /// <summary>The result of a paused tick: everything zero, <see cref="Changed"/> false.</summary>
        public static OffscreenTickResult PausedResult()
        {
            return new OffscreenTickResult { Paused = true };
        }

        internal OffscreenTickResult()
        {
        }

        /// <summary>
        /// A result stamped with the fidelity it ran at. A factory rather than a settable property
        /// so that no caller can relabel a finished tick - a result that says it ran at
        /// <see cref="OffscreenFidelity.Coarse"/> when it ran paused is worse than no result.
        /// </summary>
        internal static OffscreenTickResult At(OffscreenFidelity fidelity)
        {
            return new OffscreenTickResult { Fidelity = fidelity };
        }

        internal void AddClock(int ticks)
        {
            ClockTicks += ticks;
        }

        internal void AddDecay(int steps, int lots, int avatars)
        {
            DecaySteps += steps;
            LotsDecayed += lots;
            AvatarsDecayed += avatars;
        }

        internal void AddBacklog()
        {
            LotsBacklogged++;
        }

        internal void AddTruncation(int seconds)
        {
            TruncatedSeconds += seconds;
        }

        /// <summary>One lot had company for <paramref name="steps"/> of this tick's decay.</summary>
        internal void AddCompany(int lots, int steps)
        {
            LotsWithCompany += lots;
            CompanySteps += steps;
        }

        /// <summary>One lot was modelled as paid on <paramref name="paydays"/> of this tick's steps.</summary>
        internal void AddPaydays(int lots, int paydays)
        {
            LotsPaid += lots;
            ModelledPaydays += paydays;
        }

        public override string ToString()
        {
            if (Paused) return "offscreen tick (paused)";
            return "offscreen tick " + Fidelity + " ticks " + ClockTicks + " steps " + DecaySteps
                + " lots " + LotsDecayed + " sims " + AvatarsDecayed
                + (LotsBacklogged > 0 ? " backlogged " + LotsBacklogged : string.Empty)
                + (TruncatedSeconds > 0 ? " truncated " + TruncatedSeconds + "s" : string.Empty)
                + (CompanySteps > 0 ? " company " + LotsWithCompany + " lot(s)/" + CompanySteps + " step(s)" : string.Empty)
                + (ModelledPaydays > 0 ? " paid " + LotsPaid + " lot(s)/" + ModelledPaydays + " payday(s)" : string.Empty);
        }
    }

    /// <summary>
    /// Where an <see cref="OffscreenSimulator"/> gets its answer to "which lots have company right
    /// now", as a function of the clock alone.
    ///
    /// <para>
    /// A delegate rather than a <see cref="NeighbourhoodWorldFeatures"/> field, and the reason is
    /// the one that matters for §B8: the simulator is B6's type and the visitor schedule is B8's,
    /// and a field would have made the newer of the two a hard dependency of the older. As a
    /// delegate the connection is exactly what §7 calls an additive stage - wire it and the
    /// visitors exist, leave it null and this file's behaviour is the behaviour it had before
    /// <see cref="NeighbourhoodCommunityVisitor"/> existed at all, byte for byte.
    /// </para>
    ///
    /// <para>
    /// <b>The contract, which the simulator relies on and cannot enforce.</b> The answer must be a
    /// pure function of the clock it is handed: no state, no order of calls, the same list for the
    /// same clock every time. That is what
    /// <see cref="NeighbourhoodWorldFeatures.VisitorsAt"/> already is, and it is what lets a step
    /// ask about the step's own instant and get an answer that does not depend on which frame
    /// happened to ask. A source with state in it would break the frame-invariance the absolute
    /// step index exists to guarantee, and it would do so invisibly.
    /// </para>
    ///
    /// <para>
    /// <b>The clock handed over is not the world's clock.</b> It is a projection pinned to the
    /// instant the decay step being applied belongs to - see
    /// <see cref="OffscreenSimulator.StepClock"/>. Reading <c>clock.Hours</c> and
    /// <c>clock.Ticks</c> off it is exact; see that method for what a source that reads the
    /// calendar fields instead is getting.
    /// </para>
    ///
    /// <para>May return null, which this type reads as "nobody is anywhere".</para>
    /// </summary>
    /// <param name="clock">
    /// The instant to answer for. Required - a null is a caller bug and is treated as "nobody",
    /// because a visitor schedule that cannot say is not a reason to stop the world decaying.
    /// </param>
    /// <returns>The lot numbers that have company, in any order; see
    /// <see cref="OffscreenSimulator.LotsWithVisitors"/> for how they are normalised.</returns>
    public delegate IReadOnlyList<short> OffscreenVisitorLots(INeighbourhoodClock clock);

    /// <summary>
    /// How many are in one lot, for the lots <see cref="OffscreenVisitorLots"/> reported.
    ///
    /// <para>
    /// Optional and separate, and it is optional because the gate is the interesting half: which
    /// lots are open is a question the world has an answer to, and how many are inside one is a
    /// question with four plausible answers. A caller with no count source gets
    /// <see cref="OffscreenSimulator.DefaultVisitorsWhenUncounted"/> - one, the smallest number the
    /// model admits - and a caller with one gets its number clamped into the model's bounds, so
    /// neither a null nor a hundred can move a decay rate outside the range the type documents.
    /// </para>
    ///
    /// <para>
    /// <b>Zero is a real answer</b> and is not the same as not being on the list: a lot the gate
    /// named that turns out to have nobody in it is treated as an empty house for that step, which
    /// is the honest reading and keeps a stale entry from quietly damping a lot's decay forever.
    /// </para>
    /// </summary>
    /// <param name="clock">The same instant the gate was asked about.</param>
    /// <param name="lot">The lot the caller is asking about, as the gate named it.</param>
    /// <returns>How many are in it; zero or less for "nobody".</returns>
    public delegate int OffscreenVisitorCount(INeighbourhoodClock clock, short lot);

    /// <summary>
    /// Part B6: the world moves on while the player is somewhere else.
    ///
    /// <para>
    /// <b>What it reads, and what it writes.</b> It is handed the neighbourhood's
    /// <see cref="WorldClock"/>, its <see cref="LotStore"/> and its
    /// <see cref="NeighbourhoodLayout"/>, and it writes two things: the clock, and motive data
    /// that is already inert. Every lot it reaches is reached through <see cref="LotStore"/>,
    /// which holds <see cref="VMMarshal"/>s and <c>VMHollowMarshal</c>s - plain data, buildable and
    /// comparable with no content manager, no graphics device and no game content. <b>There is no
    /// path from this type to a <c>VM</c></b>: no method takes one, no field holds one, and the
    /// store cannot produce one. That is the whole of §B6 task 4's "applied to a lot's stored
    /// state, never to the active lot's live tick", and it is structural rather than promised.
    /// </para>
    ///
    /// <para>
    /// <b>Which lots.</b> The ones the store knows about - resident or hollow - minus the active
    /// lot. Lots the store has never heard of are not simulated, and that is §B6 task 1's word
    /// ("inactive <em>resident</em> lots") rather than a shortcut: a lot with no marshal has no
    /// contents in memory to move, and inventing contents for it would be a second save format
    /// beside the per-lot house file. The active lot is skipped even in the one case where belt
    /// and braces matters - a store that somehow held the lot the player is standing in - because
    /// writing into that lot would be a race with its own tick even though the VM behind the
    /// marshal is not this type's to see.
    /// </para>
    ///
    /// <para>
    /// <b>Determinism, and what the promise is about.</b> Every draw comes from
    /// <see cref="NeighbourhoodStreams.Simulation"/> - the stream Part A §15 reserved for exactly
    /// this, and the only one §B6 may touch. Two derivations off it, both by label: one per lot,
    /// and one more per <em>absolute</em> step index taken from <see cref="WorldClock.Ticks"/>. Each
    /// lot's own stream then hands out three more labels under it - the seed, the room model and
    /// the income model - so a model a caller switched on cannot shift a decay draw either. The
    /// consequence of the step-index derivation is the one worth having:
    /// <list type="bullet">
    ///   <item><description>retuning map generation cannot move an off-screen motive (§8);</description></item>
    ///   <item><description>adding a second lot to a neighbourhood cannot move the first lot's numbers;</description></item>
    ///   <item><description>and because the step index is derived from the clock rather than
    ///     counted, one <c>Tick(1440)</c> and 1440 <c>Tick(1)</c> calls reach <em>the same</em>
    ///     state. That is a far stronger claim than "same seed, same call sequence", and it is the
    ///     one that matters, because a frame rate is not something a save file records.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// <b>The decay is The Sims 1's own table.</b> Rates are read from
    /// <c>VMTS1MotiveDecay.Constants</c> and the motive order from
    /// <c>VMTS1MotiveDecay.DecrementMotives</c> at every step rather than copied into a private
    /// array here. Copying would be a second tuning table to keep in step with the first, and the
    /// failure mode of forgetting is an off-screen Sim who is quietly no longer hungry. What is
    /// <em>not</em> reused is the engine's <c>Tick</c>: it needs a <c>VMAvatar</c> and a
    /// <c>VMContext</c> - a room, a tile, a tuning cache - and a lot with no VM has none of those.
    /// So the arithmetic is restated here against the same constants, including the engine's own
    /// truncations, because reproducing a truncation is fidelity and reproducing a rounding
    /// artefact as if it were a decision is not.
    /// </para>
    ///
    /// <para><b>What this cannot model, stated plainly.</b> Four things, all of them because a VM
    /// would have to exist, and one it adds deliberately because a VM's answer is not available
    /// off-screen at all:</para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>The room a Sim is actually standing in.</b>
    ///     <c>VMTS1MotiveDecay</c> reads <c>VMMotive.Room</c> from
    ///     <c>context.GetRoomScore(context.GetRoomAt(...))</c> - the room table and the Sim's tile
    ///     within it, both of which live in a running lot. A record has one score for a whole lot
    ///     where the engine has one per Sim, and no marshal carries the table a room table is built
    ///     from. So <see cref="OffscreenLotRecord.RoomScore"/> is a <em>modelled</em> score when
    ///     <see cref="ModelsRoomScore"/> is on, and with it off the value is left exactly as it was
    ///     found: untouched in a marshal, and a stated constant of zero in a coarse record. Mood is
    ///     recomputed either way and still includes Room, so it stays consistent with a number
    ///     nothing is maintaining.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Who is asleep, and on what.</b> A Sim's own <c>SleepState</c> is in the marshal and
    ///     is read as the starting truth, but nothing off-screen can put a Sim to bed. A clock
    ///     rule stands in - asleep outside 07:00..22:00, with the engine's own "wake at the wake
    ///     hour if rested enough" rule kept, because that rule is what makes the guess
    ///     self-correcting rather than merely plausible. It is a guess, and
    ///     <see cref="SleepsOffScreen"/> turns it off.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Personality, at <see cref="OffscreenFidelity.Coarse"/>.</b> Active and outgoing
    ///     personality are in the marshal, and <see cref="OffscreenFidelity.Full"/> reads them, but
    ///     a coarse tick uses the population midpoint instead - which is the value that makes the
    ///     engine's own <c>666</c> personality test come out neutral.
    ///   </description></item>
    ///   <item><description>
    ///     <b>A family's money, and any object's decay.</b> The first half is deliberate and is set
    ///     out under <see cref="EstimatesHouseholdIncome"/>. The second is the rest of task 1's
    ///     optional list and it is not possible here at all, which is worth the space because the
    ///     reason is not "a VM would be needed" but "there is nowhere to put the answer". In The
    ///     Sims 1 a plant's condition is not a number a save carries: it is a function of the room
    ///     the object stands in, and a room's score is <c>VMRoomScore.Compute</c> over the summed
    ///     <c>VMStackObjectVariable.RoomImpact</c> of the objects in it - a <em>tuning</em> value,
    ///     read through a catalog this type structurally cannot reach. Three things are missing and
    ///     all three are needed: the catalog, without which no stored GUID can be identified as a
    ///     plant at all; the room table, which is built when a lot loads and is in no marshal
    ///     (<c>VMContextMarshal</c> carries a clock, an architecture, an ambience and a seed); and
    ///     somewhere to write to, because <c>VMHollowGameObjectMarshal</c> does not carry a
    ///     per-object variable table in the first place. A guessed "wilt" written into an object's
    ///     data would not be a plant dying - it would be a number the engine goes on to read as the
    ///     object's price, its stack count or its sale state. So the room score this file
    ///     <em>does</em> model is exactly the input such a rule would consume, and nothing consumes
    ///     it yet on purpose: a model with a plausible output and no engine behind it is how object
    ///     decay becomes corruption.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Anything anybody did.</b> This is the fifth item and the only invented one, so it is
    ///     named as an invention: the engine's decay rates assume a lot where Sims eat, sleep, use
    ///     the bathroom and get bored, and applying the bare rate to a day of nobody describes a
    ///     coma. What an off-screen lot cannot do is <em>act</em>, so instead each step's rate is
    ///     scaled by a bounded per-lot, per-step factor drawn from the seed stream
    ///     (<see cref="MaxDecayJitter"/>, ±20%). It never changes a rate's sign and never leaves the
    ///     engine's range, and it is the reason two households in the same world do not decay in
    ///     lockstep - which is also why the world seed is visible in a lot nobody ever opened.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>And the two models that are off rather than on: the room score and the household
    /// income.</b> Both are instance properties, both default to off, and both being off leaves
    /// every number this type produces exactly as it was before they were written. The same seam as
    /// B8's visitors, and for the same reason - the milestone that asked for them is this one and
    /// the callers that would switch them on are a later one, so the gate is a flag on the instance
    /// rather than a dependency in the constructor.
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b><see cref="ModelsRoomScore"/>.</b> The engine's own arithmetic -
    ///     <c>VMRoomScore.Compute</c>, on the engine's own <c>-100..100</c> bound - over one real
    ///     input, the lot's footprint from the layout, and one guessed one, the objects' room impact
    ///     drawn once per lot and offset by how the lot is zoned. The narrow band that produces is
    ///     the finding rather than a bug: a room score is impact <em>per tile</em>, and a store
    ///     knows no tiles' worth of objects, so the modelled score sits near the engine's floor
    ///     unless the lot is small. What it feeds is mood, which is all the engine feeds it to.
    ///   </description></item>
    ///   <item><description>
    ///     <b><see cref="EstimatesHouseholdIncome"/>.</b> A rate and never a balance: what a
    ///     household is modelled as earning per day, from the job type and promotion level its
    ///     stored Sims record, paid on a payday hour drawn from the lot's own stream, on a cadence
    ///     keyed on the absolute step index so that a day is one payday at every fidelity. It is
    ///     keyed on the family those Sims name, and a lot with no family gets nothing at all, so
    ///     the number is always attributable and never floating free of an owner.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>B8's community visitors, and what they do here.</b> <see cref="VisitorLots"/> is the
    /// whole of the connection, and it is null by default, which is the entire gating story: no
    /// source, no company, and every number this type produces is the number it produced before
    /// §B8 existed. The same draw comes out of the same stream either way, because
    /// <see cref="ScaleFraction"/> is handed the head count as the <em>centre</em> of its
    /// jitter window rather than as a second multiplier on top of one - see that method for why
    /// the two are the same expression.
    /// <para>
    /// The effect, when a source is wired, is that a lot somebody is in decays more slowly, and
    /// the size of the effect is bounded by construction: <see cref="CompanyScaleFor"/> is
    /// monotone in the head count, never above
    /// <see cref="NoCompanyScale"/>, and never below
    /// <see cref="MinCompanyScale"/>. A crowded lot's Sims are worse off than an empty one's by a
    /// fixed fraction and never by a random one, so the change is a model of company rather than
    /// a second lottery layered on the first.
    /// </para>
    /// <para>
    /// <b>Three things this deliberately does not do.</b> It does not decide who visits - that is
    /// the schedule's answer, taken as given. It does not model what a visitor <em>does</em>: no
    /// conversation, no meal, no trip to the bathroom, because all three need objects and a person
    /// in a lot, and this type reaches neither. And it does not let a visitor enter a lot's
    /// <see cref="VMAvatarMarshal"/>: the visitors are not Sims this build has any record of, and
    /// writing them into a motive table would leave a lot that loads with Sims who were never
    /// saved.
    /// </para>
    /// </summary>
    ///
    /// <para>
    /// <b>Thread affinity: the game thread, only</b> (§7, and the same rule
    /// <see cref="LotStore"/> and <see cref="SimitoneNeighbourhoodWorld"/> state). One world, one
    /// writer, and no locking - a second thread reaching a lot's marshal would be a design error
    /// this type declines to accommodate rather than a race to paper over with a lock that hid it.
    /// </para>
    /// </summary>
    public sealed class OffscreenSimulator
    {
        /// <summary>
        /// The fidelity a simulator runs at until somebody says otherwise: the handoff's word, and
        /// a default rather than a constant read from a settings file, because a dial nobody has
        /// turned should be a middle one rather than an expensive one.
        /// </summary>
        public const OffscreenFidelity DefaultFidelity = OffscreenFidelity.Coarse;

        /// <summary>
        /// Game minutes per decay step under <see cref="OffscreenFidelity.Coarse"/>. Half an hour:
        /// long enough that a four-hour absence is eight steps, and short enough that the
        /// <c>short</c> accumulator never has to hold more than a few thousand 1000ths.
        /// </summary>
        public const int CoarseStepGameMinutes = 30;

        /// <summary>
        /// Game minutes per decay step under <see cref="OffscreenFidelity.Full"/>. Two, because
        /// that is what <c>VMTS1MotiveDecay</c> uses - it guards on <c>minutes/2 == LastMinute</c>,
        /// so the engine itself decays every other clock minute.
        /// </summary>
        public const int FullStepGameMinutes = 2;

        /// <summary>
        /// The lowest motive this type will ever write: the engine's own floor, the
        /// <c>motive &lt; -100</c> clamp in <c>VMTS1MotiveDecay</c> and the bottom of
        /// <c>VMAvatar.SetMotiveData</c>'s range.
        /// </summary>
        public const int MinMotive = -100;

        /// <summary>
        /// The highest motive this type will ever write. <c>VMAvatar.SetMotiveData</c> clamps to
        /// the tuning table's limit for the motive and falls back to 100 when there is no tuning
        /// cache - which is the situation a stored marshal is always in, having no VM to ask. So
        /// 100 is not a simplification here; it is the number the engine itself would use.
        /// </summary>
        public const int MaxMotive = 100;

        /// <summary>
        /// Real seconds of game time in a game minute, for TS1: one to one. A lot ticks
        /// <see cref="WorldClock.TicksPerMinuteTs1"/> sub-minute ticks to the minute and a minute
        /// passes per real second, so a second of play is a minute of the neighbourhood's clock
        /// whether or not anybody is watching.
        /// </summary>
        public const double GameMinutesPerRealSecond = 1.0;

        /// <summary>
        /// How far one step's decay rate may be scaled by chance, in 1000ths of a unit: 200 is
        /// ±20%. The reason this exists is that an off-screen lot is not a lot whose Sims sit still,
        /// and the reason it is this small is that a lot nobody visited must not be visibly
        /// better or worse off than a lot whose saved file says otherwise.
        /// </summary>
        public const int MaxDecayJitter = 200;

        /// <summary>The hour a stored Sim gets up, off-screen. A guess, and a named one.</summary>
        public const int WakeHour = 7;

        /// <summary>The hour a stored Sim goes to bed, off-screen. A guess, and a named one.</summary>
        public const int BedHour = 22;

        /// <summary>The root label every per-lot stream is derived under.</summary>
        public const string StreamLabel = "offscreen";

        /// <summary>The label of the per-lot stream, before the lot number.</summary>
        public const string LotStreamLabel = StreamLabel + "/lot/";

        /// <summary>The label of a single decay step's stream, before the step index.</summary>
        public const string StepStreamLabel = "/step/";

        // ---- the room model -------------------------------------------------------------------------

        /// <summary>
        /// The label the room model's one draw is derived under, beneath the lot's own stream.
        /// Separate from <see cref="StepStreamLabel"/> and from the seed's label for the same
        /// reason those are separate from each other: a draw made for a model nobody can see must
        /// not move a draw a decay depends on.
        /// </summary>
        public const string RoomStreamLabel = "room";

        /// <summary>The income model's two draws' label, beneath the lot's own stream. See
        /// <see cref="RoomStreamLabel"/>.</summary>
        public const string IncomeStreamLabel = "income";

        /// <summary>
        /// The most tiles a modelled room may cover: the largest lot
        /// <see cref="NeighbourhoodLayout"/> will describe, squared. A bound rather than a
        /// policy - the engine divides a room's impact by its area, so an unbounded area would
        /// make the divisor unbounded and a corrupt size would silently flatten the score to the
        /// penalty.
        /// </summary>
        public const int MaxRoomAreaTiles = NeighbourhoodLayout.MaxLotSize * NeighbourhoodLayout.MaxLotSize;

        /// <summary>
        /// The most a household's rooms are credited with, in the room-impact units
        /// <c>VMRoomScore.Compute</c> divides by area. A guess, and a named one: the real number is
        /// the summed <c>VMStackObjectVariable.RoomImpact</c> of objects this type cannot see, and
        /// this is how big that sum is pretended to be.
        /// </summary>
        public const int RoomImpactSpread = 240;

        /// <summary>Room impact credited to a community lot, which the model assumes is cared for.</summary>
        public const int CommunityRoomImpact = 120;

        /// <summary>Room impact credited to a studio lot, which the model assumes is half of that.</summary>
        public const int StudioRoomImpact = 60;

        /// <summary>Room impact credited to a residential lot: none. The default is the default.</summary>
        public const int ResidentialRoomImpact = 0;

        // ---- the income model -----------------------------------------------------------------------

        /// <summary>
        /// "This lot's money belongs to no family", which is also what a zero
        /// <c>PersonData[TS1FamilyNumber]</c> means. TS1's family number is a <c>FAMI</c> chunk id
        /// and 0 is not one: <c>SimitoneGlobalLink.ResolveFamily</c> reads the same word and treats
        /// an avatar with no family as Maxis paying, which is the same answer.
        /// </summary>
        public const short NoFamilyNumber = 0;

        /// <summary>Game hours in a game day. TS1's own, and the modulus a payday is keyed on.</summary>
        public const int HoursPerGameDay = 24;

        /// <summary>Game minutes in a game hour.</summary>
        public const int MinutesPerGameHour = 60;

        /// <summary>Game minutes between modelled paydays: one day.</summary>
        public const int PaydayIntervalGameMinutes = MinutesPerGameHour * HoursPerGameDay;

        /// <summary>"This household is never paid", for a lot with no income estimate.</summary>
        public const int NoPaydayHour = -1;

        /// <summary>
        /// The furthest step index the payday arithmetic will look at, as a bound on
        /// <c>(step + 1) * minutes</c> rather than on elapsed time. A week at the finest fidelity
        /// is 5,040 steps, so 2^40 is beyond any absence a caller can describe and keeps the
        /// multiplication away from <see cref="long"/>'s ceiling.
        /// </summary>
        public const long MaxStepIndex = 1L << 40;

        /// <summary>
        /// The lowest modelled daily pay for a household member with a job and no promotion: twenty
        /// simoleons a day. The unit is real; the number is this model's own, because TS1's is a
        /// content value - a job level's <c>Salary</c> comes out of <c>work.iff</c> through
        /// <c>CARR.GetJobData</c> and not out of code, so a marshal cannot carry the engine's number
        /// and this one is not claiming to be it.
        /// </summary>
        public const int MinEarnerDailyIncome = 20;

        /// <summary>What one step of a job's promotion ladder is worth a day. A guess, and a named one.</summary>
        public const int IncomePerPromotionLevel = 8;

        /// <summary>
        /// The spread of the one per-household term in the income model, in simoleons a day: a
        /// household's own luck, drawn once. Bounded like every other draw here, and drawn once for
        /// the household rather than once per earner so the rate stays one number.
        /// </summary>
        public const int EarnerIncomeSpread = 60;

        /// <summary>How many earners a household is allowed to have, whatever the marshal says.</summary>
        public const int MaxEarnersPerLot = 8;

        /// <summary>The top of a job's promotion ladder, and the clamp on a corrupt promotion word.</summary>
        public const int MaxPromotionLevel = 10;

        /// <summary>
        /// The most any household may be modelled as earning in a day, which is exactly
        /// <see cref="MaxEarnersPerLot"/> earners at the top of the ladder with the best draw -
        /// so the ceiling is a bound that cannot be reached by accident and not a separate number
        /// to keep in step.
        /// </summary>
        public const int MaxEstimatedDailyIncome =
            MaxEarnersPerLot * (MinEarnerDailyIncome + MaxPromotionLevel * IncomePerPromotionLevel
                + EarnerIncomeSpread);

        /// <summary>
        /// "Nobody", in every unit this type uses for a head count. Not a bound on a count - a
        /// count is clamped up to
        /// <see cref="NeighbourhoodWorldFeatures.MinVisitorsPerLot"/> - but the answer for a lot no
        /// source named, and the only value that means "do not touch the rate".
        /// </summary>
        public const int NoCompany = 0;

        /// <summary>
        /// The decay multiplier for a lot with nobody in it: 1000, which is "unchanged". The unit
        /// is 1000ths so a multiplier never touches floating point.
        /// </summary>
        public const int NoCompanyScale = 1000;

        /// <summary>
        /// How much each visitor slows a lot's decay, in 1000ths. Seventy-five: a lot with one
        /// person in it decays 92.5% as fast as an empty one, and a full house 70% as fast. It is
        /// a guess, and it is a named one - the engine has no rate for "somebody came round",
        /// because in a real lot somebody coming round is an interaction this code cannot see.
        /// </summary>
        public const int CompanyScalePerVisitor = 75;

        /// <summary>
        /// The slowest decay company can make a lot: one hundred minus every visitor the model
        /// admits, so a source that claims two hundred people cannot drive a lot below this. A
        /// bound and not a target - <see cref="NoCompanyScale"/> is still the answer for an empty
        /// house, and the difference is the whole size of the effect.
        /// </summary>
        public const int MinCompanyScale =
            NoCompanyScale - NeighbourhoodWorldFeatures.MaxVisitorsPerLot * CompanyScalePerVisitor;

        /// <summary>
        /// The head count a lot is assumed to hold when a source names it but says nothing about
        /// how many are in it. The model's own minimum, deliberately: a source that has only
        /// answered "is anybody there" should get the smallest effect that is still an effect, and
        /// a caller that knows the number should hand over
        /// <see cref="VisitorCount"/>.
        /// </summary>
        public const int DefaultVisitorsWhenUncounted = NeighbourhoodWorldFeatures.MinVisitorsPerLot;

        /// <summary>
        /// One game day, and the default <see cref="MaxSecondsPerTick"/>: 1,440 real seconds,
        /// because <see cref="GameMinutesPerRealSecond"/> makes a real second a game minute and a
        /// TS1 day is 24 game hours. That is 43,200 clock ticks.
        /// </summary>
        public const int DefaultMaxSecondsPerTick = 60 * 24;

        /// <summary>
        /// A game day at <see cref="OffscreenFidelity.Full"/>'s cadence, and the default
        /// <see cref="MaxDecayStepsPerLot"/>. 720 steps, which lines up with the cap above so the
        /// step budget never bites before the time budget does.
        /// </summary>
        public const int DefaultMaxDecayStepsPerLot = 60 * 24 / FullStepGameMinutes;

        private readonly Dictionary<short, OffscreenLotRecord> _records = new Dictionary<short, OffscreenLotRecord>();
        private readonly NeighbourhoodStreams _streams;
        private readonly NeighbourhoodRng _lotRoot;
        private int _maxSecondsPerTick = DefaultMaxSecondsPerTick;
        private int _maxDecayStepsPerLot = DefaultMaxDecayStepsPerLot;

        /// <summary>The B8 connection. Both null until a caller asks for one; see the class remarks.</summary>
        private OffscreenVisitorLots _visitorLots;
        private OffscreenVisitorCount _visitorCount;

        //Per-step company multipliers, reused between lots and between ticks. Grown, never shrunk,
        //and always fully rewritten before use - a stale entry read as a rate would be a
        //nondeterminism of exactly the kind the step index exists to prevent.
        private int[] _companyScales;

        //The one-entry memo for "what does the visitor schedule say at this instant", plus the
        //projection clock it was read through. Both are cleared at the start of every
        //SimulateLots, because the world's calendar can change between ticks and a memo that
        //outlived one would answer with yesterday's date.
        private WorldClock _stepClock;
        private long _stepClockTick;
        private IReadOnlyList<short> _snapshotLots;
        private int[] _snapshotCounts;
        private long _snapshotTick;
        private static readonly IReadOnlyList<short> NoVisitorLots = new short[0];

        /// <summary>
        /// A simulator over a world seed. The seed is the layout's own
        /// (<see cref="NeighbourhoodLayout.Seed"/>), so a neighbourhood's off-screen behaviour is
        /// a property of the neighbourhood rather than of the session that happened to load it.
        /// </summary>
        /// <param name="worldSeed">The seed in the layout sidecar.</param>
        public OffscreenSimulator(long worldSeed)
        {
            WorldSeed = worldSeed;
            _streams = NeighbourhoodStreams.FromSeed(worldSeed);
            _lotRoot = _streams.Simulation.ForLabel(StreamLabel);
        }

        /// <summary>A simulator seeded from a layout's own seed.</summary>
        /// <param name="layout">The layout to read <see cref="NeighbourhoodLayout.Seed"/> from. Required.</param>
        public OffscreenSimulator(NeighbourhoodLayout layout)
            : this(RequireLayout(layout).Seed)
        {
        }

        /// <summary>A simulator at the world seed, at <see cref="DefaultFidelity"/>.</summary>
        public OffscreenSimulator()
            : this(0L)
        {
        }

        /// <summary>The world seed this simulator was built over.</summary>
        public long WorldSeed { get; private set; }

        /// <summary>
        /// The three streams the seed fans out into, exposed so the derivation can be asserted
        /// rather than believed. The one this type draws from is
        /// <see cref="NeighbourhoodStreams.Simulation"/>; <see cref="NeighbourhoodStreams.Generation"/>
        /// is here only so a test can check that it is a different stream.
        /// </summary>
        public NeighbourhoodStreams Streams
        {
            get { return _streams; }
        }

        /// <summary>How much happens off-screen. Defaults to <see cref="DefaultFidelity"/>.</summary>
        /// <remarks>
        /// Initialised in the field rather than left to the CLR, because
        /// <see cref="OffscreenFidelity.Paused"/> is zero and a plain auto-property would come up
        /// paused - the exact opposite of the documented default, and a world that silently did
        /// nothing for want of a settings file.
        /// </remarks>
        public OffscreenFidelity Fidelity { get; set; } = DefaultFidelity;

        /// <summary>
        /// The lot the player is standing in, which is never touched. 0 for "in no lot", the same
        /// convention as <see cref="INeighbourhoodWorld.ActiveLotNumber"/> and
        /// <see cref="NeighbourhoodWorldState.ActiveLot"/>.
        ///
        /// <para>
        /// <see cref="Tick"/> falls back to <see cref="NeighbourhoodWorldState.ActiveLot"/> when
        /// this is 0 and the layout carries a world state, so a caller that already stamps the
        /// world state on every lot switch does not have to also remember this.
        /// </para>
        /// </summary>
        public int ActiveLot { get; set; }

        /// <summary>
        /// Does a stored Sim go to bed and get up on a clock while off-screen? <c>true</c> by
        /// default, because a lot whose Sims never sleep is not plausible and this is the guess
        /// that makes it so. <c>false</c> freezes each Sim in whatever sleep state its marshal
        /// already had.
        /// </summary>
        /// <remarks>
        /// Initialised in the field rather than left to the CLR: <c>false</c> is zero, and a plain
        /// auto-property would default to "nobody ever sleeps", which is the one answer this
        /// property exists to be able to give deliberately.
        /// </remarks>
        public bool SleepsOffScreen { get; set; } = true;

        /// <summary>
        /// Does an off-screen lot get a modelled room score? <c>false</c> by default, and the
        /// default is the point: with it off, <see cref="OffscreenLotRecord.RoomScore"/> is the
        /// value a marshal already held or the zero a coarse record has always reported, and every
        /// other number this type produces is the number it produced before the model existed.
        ///
        /// <para>
        /// <b>What turning it on actually does</b>, and it is a smaller thing than the name
        /// suggests: the engine uses a room score for exactly one thing in the motive decay - it
        /// adds it to the mood sum - so this writes a modelled score into the place the engine
        /// reads it from, on the coarse lot's scratch and on each stored Sim's own motive table.
        /// No rate changes. A stored Sim's room slot is overwritten by the live lot's own
        /// <c>VMTS1MotiveDecay.Tick</c> the moment the player goes back in, because that call sets
        /// it unconditionally, so nothing this writes survives a visit to the lot.
        /// </para>
        /// </summary>
        public bool ModelsRoomScore { get; set; }

        /// <summary>
        /// Does an off-screen household get a modelled income? <c>false</c> by default, and the
        /// default is the point: with it off there is no family key, no rate, no payday hour and no
        /// counted payday anywhere in a record.
        ///
        /// <para>
        /// <b>What this is and is not.</b> It is an <em>estimate of a rate</em>, held on the
        /// record, for a caller that wants to say "this neighbour is about eight hundred simoleons
        /// a poorer than that one". It is not a balance, not a transaction, and not a second
        /// ledger: a lot's money is the family's, and the family authority is
        /// <c>SimitoneGlobalLink</c> over a <c>FAMI</c> chunk, which resolves the same
        /// <c>PersonData[TS1FamilyNumber]</c> word this model reads and moves it only through
        /// <c>TransactBudgetForFamily</c>. There is no path from here to that link, to a <c>FAMI</c>
        /// or to a budget, and the running total is
        /// <see cref="OffscreenLotRecord.EstimatedIncomePaid"/> - a product of the rate and the
        /// payday count, computed on read - so there is no stored balance to disagree with
        /// anything.
        /// </para>
        /// <para>
        /// <b>What it is keyed on.</b> The family its stored Sims name, and its members' job type
        /// and promotion level. A lot with no marshal, a marshal with no Sims, or a person whose
        /// family was never assigned gets nothing - see
        /// <see cref="OffscreenLotRecord.HasIncomeEstimate"/>.
        /// </para>
        /// </summary>
        public bool EstimatesHouseholdIncome { get; set; }

        /// <summary>
        /// Which lots have company, as a function of the clock. <c>null</c> by default, and
        /// <c>null</c> is the whole of the B8 gate: with no source this type does not know that
        /// community visitors exist, and every number it produces is the number it produced before
        /// the connection existed - the same stream, the same draws, the same records.
        ///
        /// <para>
        /// The natural value is <see cref="NeighbourhoodWorldFeatures.VisitorsAt"/> over the
        /// world's own seed and the layout passed to <see cref="Tick"/>, which is already a pure
        /// function of exactly the three things this type can offer it. A caller holding a
        /// <see cref="NeighbourhoodWorldFeatures"/> should hand over that rather than the
        /// instance's cached <c>Visitors</c> list: the cache is a function of when the features
        /// object last refreshed, and a gate that depends on that is a gate that makes the
        /// off-screen result depend on the frame rate.
        /// </para>
        /// </summary>
        public OffscreenVisitorLots VisitorLots
        {
            get { return _visitorLots; }
            set { _visitorLots = value; }
        }

        /// <summary>
        /// How many are in each lot the <see cref="VisitorLots"/> source named. <c>null</c> by
        /// default, which means one - <see cref="DefaultVisitorsWhenUncounted"/> - and therefore
        /// the smallest effect the model has. A count of zero or less means the lot is treated as
        /// empty for that step.
        /// </summary>
        public OffscreenVisitorCount VisitorCount
        {
            get { return _visitorCount; }
            set { _visitorCount = value; }
        }

        /// <summary>
        /// The bounded decay multiplier, in 1000ths, for a lot holding <paramref name="visitors"/>
        /// people. Pure, and the one place the B8 model turns into a number.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Monotone, clamped, and never above <see cref="NoCompanyScale"/>: one more person in
        /// the lot always means the same or less decay, four is the worst case at
        /// <see cref="MinCompanyScale"/>, and a source claiming a hundred people gets the same
        /// answer as one claiming four. The bound is the point - a visitor schedule is a model too,
        /// and letting one model's numbers set the scale of another's would leave the off-screen
        /// decay as trustworthy as the loosest input anywhere in the neighbourhood.
        /// </para>
        /// </remarks>
        /// <param name="visitors">How many are in it. Zero or less gives <see cref="NoCompanyScale"/>.</param>
        public static int CompanyScaleFor(int visitors)
        {
            if (visitors <= NoCompany) return NoCompanyScale;
            var clamped = Math.Min(visitors, NeighbourhoodWorldFeatures.MaxVisitorsPerLot);
            return Math.Max(MinCompanyScale, NoCompanyScale - clamped * CompanyScalePerVisitor);
        }

        /// <summary>
        /// The room impact a lot's zoning is credited with, before the one per-lot draw. Pure, and
        /// the whole of what the model's room score knows about a lot that is not its size.
        /// </summary>
        /// <remarks>
        /// A guess, and the smallest one in this file: a community lot's rooms are a venue somebody
        /// looks after and a studio's are somebody's workplace, while a house is whoever lives in
        /// it. Anything else - including the zone numbers a later build might add - gets
        /// <see cref="ResidentialRoomImpact"/>, because an unknown zone is a residential one as far
        /// as this claim goes.
        /// </remarks>
        /// <param name="zone">The lot's <see cref="LotZoning"/> zone.</param>
        public static int RoomImpactFor(short zone)
        {
            if (zone == LotZoning.ZoneCommunity) return CommunityRoomImpact;
            if (zone == LotZoning.ZoneStudio) return StudioRoomImpact;
            return ResidentialRoomImpact;
        }

        /// <summary>
        /// What a household of <paramref name="earners"/> earners is modelled as earning per day,
        /// when the busiest of them is <paramref name="promotion"/> steps up their job's ladder and
        /// the household's own drawn term is <paramref name="householdTerm"/>. Pure, and bounded at
        /// both ends.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>What each argument is worth.</b> A job's real salary is a content value, so
        /// <see cref="MinEarnerDailyIncome"/> plus
        /// <see cref="IncomePerPromotionLevel"/> per level of a ladder is the model's own ladder; the
        /// drawn term is one number for the whole household, not one per earner, so the rate is a
        /// single readable figure. Earners are clamped to <see cref="MaxEarnersPerLot"/>, the
        /// promotion word to <see cref="MaxPromotionLevel"/> and the result to
        /// <see cref="MaxEstimatedDailyIncome"/>, so a corrupt marshal cannot produce an income
        /// outside the model's own range.
        /// </para>
        /// <para>
        /// <b>Zero earners is zero, and that is an answer.</b> A family with nobody in a job earns
        /// nothing off-screen, which is a modelled fact about a family rather than a gap in the
        /// model - the difference between that and "we do not know" is
        /// <see cref="OffscreenLotRecord.HasIncomeEstimate"/>.
        /// </para>
        /// </remarks>
        /// <param name="earners">How many of the lot's stored Sims have a job type.</param>
        /// <param name="promotion">The highest promotion level among them.</param>
        /// <param name="householdTerm">The household's own term, in simoleons a day.</param>
        public static int DailyIncomeFor(int earners, int promotion, int householdTerm)
        {
            if (earners <= 0) return 0;
            var head = Math.Min(earners, MaxEarnersPerLot);
            var ladder = Math.Min(Math.Max(0, promotion), MaxPromotionLevel);
            var term = householdTerm > 0 ? householdTerm : 0;
            var perEarner = MinEarnerDailyIncome + ladder * IncomePerPromotionLevel + term;
            var total = head * perEarner;
            return total > MaxEstimatedDailyIncome ? MaxEstimatedDailyIncome : total;
        }

        /// <summary>
        /// How many of the decay steps from <paramref name="firstStep"/> to
        /// <paramref name="firstStep"/> + <paramref name="steps"/> - 1 fall in an hour
        /// <paramref name="paydayHour"/> of the day. Pure, and the whole of the payout cadence.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Counted in hours rather than in steps</b>, because a step is not an hour at
        /// <see cref="OffscreenFidelity.Coarse"/> - two of them are - and a per-step test would pay
        /// a household twice on some steps and not at all on others depending on which fidelity
        /// happened to be running. Counting distinct hours makes a game day exactly one payday at
        /// every fidelity, which is a property this can be tested against rather than a claim.
        /// </para>
        /// <para>
        /// <b>Keyed on the absolute step index</b>, like every other draw in this file, so the
        /// answer is a property of the steps themselves: one long tick and many short ones pay the
        /// same number of times, and a lot whose backlog is deferred pays for those steps when they
        /// run rather than not at all.
        /// </para>
        /// <para>
        /// The hour is an absolute one, counted from the world's tick zero rather than from whatever
        /// hour the clock read when the record was made, because a household's payday cannot depend
        /// on when the player first looked at it. It is the hour a step <em>begins</em> in, so a
        /// step that ends exactly on midnight belongs to the day that is ending and a game day is
        /// counted once rather than twice.
        /// </para>
        /// </remarks>
        /// <param name="firstStep">The absolute index of the first step in the range.</param>
        /// <param name="steps">How many steps are in the range.</param>
        /// <param name="stepGameMinutes">This fidelity's step size, in game minutes.</param>
        /// <param name="paydayHour">The household's hour, 0..23.</param>
        public static int PaydaysBetween(long firstStep, int steps, int stepGameMinutes, int paydayHour)
        {
            if (steps <= 0 || stepGameMinutes <= 0) return 0;
            if (paydayHour < 0 || paydayHour >= HoursPerGameDay) return 0;
            if (firstStep < 0 || firstStep > MaxStepIndex) return 0;
            if (firstStep + steps - 1 > MaxStepIndex) return 0;

            var firstHour = HourIndexOfStep(firstStep, stepGameMinutes);
            var lastHour = HourIndexOfStep(firstStep + steps - 1, stepGameMinutes);
            return (int)CountResidues(firstHour, lastHour, paydayHour, HoursPerGameDay);
        }

        /// <summary>
        /// The absolute game hour a step begins in. Named by the hour it starts in rather than the
        /// one it ends on, so a day is counted once: a step ending exactly on midnight belongs to the
        /// hour before it, and counting that step as the next day's hour would pay every household in
        /// the neighbourhood twice a day.
        /// </summary>
        private static long HourIndexOfStep(long stepIndex, int stepGameMinutes)
        {
            return stepIndex * stepGameMinutes / MinutesPerGameHour;
        }

        /// <summary>
        /// How many of the integers from <paramref name="first"/> to <paramref name="last"/> are
        /// congruent to <paramref name="phase"/> modulo <paramref name="modulus"/>.
        /// </summary>
        /// <remarks>
        /// The hours a range of steps covers are consecutive with repeats - two coarse steps to an
        /// hour, thirty fine ones - so the answer is a subtraction of two divisions rather than a walk
        /// over steps, and a step range nobody walked costs nothing. The divisions are floor ones
        /// because C#'s truncate towards zero: with the absolute hour index a valid range never goes
        /// negative, so today that is belt to the braces, and it is here rather than assumed because
        /// the alternative is a payday that silently disappears in the one configuration nobody tests.
        /// </remarks>
        private static long CountResidues(long first, long last, long phase, long modulus)
        {
            if (last < first || modulus <= 0) return 0;
            return FloorDiv(last - phase, modulus) - FloorDiv(first - 1 - phase, modulus);
        }

        private static long FloorDiv(long value, long divisor)
        {
            var quotient = value / divisor;
            if (value % divisor != 0 && ((value < 0) != (divisor < 0))) quotient--;
            return quotient;
        }

        /// <summary>
        /// The lots that have company at <paramref name="clock"/>, as this type sees them: the
        /// source's answer, normalised. Never null; empty for no source, a null source answer, or
        /// an hour when nothing is open anywhere.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Normalised, in three specific ways</b>, and all of them because the answer is used as
        /// a set of keys into a dictionary of records. Lot <c>0</c> is dropped - it means "in no
        /// lot", and a visitor in no lot is not a thing. A number outside <see cref="short"/>
        /// is dropped for the same reason a record cannot be created for one. And the result is
        /// ascending and duplicate-free, so a source that lists a lot twice cannot count it twice
        /// and a caller that compares two lists is comparing the same thing twice.
        /// </para>
        /// <para>
        /// Pure: it reads <see cref="VisitorLots"/> and nothing else, holds no state, and gives the
        /// same answer for the same clock every time it is asked.
        /// </para>
        /// </remarks>
        /// <param name="clock">The instant to answer for. Null is a caller bug and throws.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
        public IReadOnlyList<short> LotsWithVisitors(INeighbourhoodClock clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (_visitorLots == null) return NoVisitorLots;

            var reported = _visitorLots(clock);
            if (reported == null || reported.Count == 0) return NoVisitorLots;

            var kept = new List<short>(reported.Count);
            for (int i = 0; i < reported.Count; i++)
            {
                var lot = reported[i];
                if (lot == 0 || kept.Contains(lot)) continue;
                kept.Add(lot);
            }
            if (kept.Count == 0) return NoVisitorLots;
            kept.Sort();
            return kept;
        }

        /// <summary>
        /// How many are in <paramref name="lot"/> at <paramref name="clock"/>, or
        /// <see cref="NoCompany"/>. The pure function of clock, seed and lot that the decay's
        /// per-step answer is built from, and exposed so a caller can ask the question without
        /// running a tick.
        /// </summary>
        /// <param name="clock">The instant to answer for. Null is a caller bug and throws.</param>
        /// <param name="lot">The lot to ask about.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
        public int VisitorsFor(INeighbourhoodClock clock, short lot)
        {
            var lots = LotsWithVisitors(clock);
            for (int i = 0; i < lots.Count; i++)
            {
                if (lots[i] != lot) continue;
                return CountIn(clock, lot);
            }
            return NoCompany;
        }

        /// <summary>
        /// The head count for a lot the gate has already named, clamped into the model's own
        /// bounds. <see cref="OffscreenVisitorCount"/> null means
        /// <see cref="DefaultVisitorsWhenUncounted"/>; anything the source reports is clamped, so
        /// a count source cannot widen the range the decay runs in.
        /// </summary>
        private int CountIn(INeighbourhoodClock clock, short lot)
        {
            var reported = _visitorCount == null
                ? DefaultVisitorsWhenUncounted
                : _visitorCount(clock, lot);
            if (reported <= NoCompany) return NoCompany;
            return Math.Min(reported, NeighbourhoodWorldFeatures.MaxVisitorsPerLot);
        }

        /// <summary>
        /// The most real time one <see cref="Tick"/> will simulate, in seconds: a day by default,
        /// which is 43,200 clock ticks and about a millisecond of work.
        /// </summary>
        /// <remarks>
        /// The cap exists because the alternative is a first frame after a month away trying to
        /// tick 77 million times. It truncates rather than defers, so a truncated absence costs
        /// the player the tail of it; that is reported in
        /// <see cref="OffscreenTickResult.TruncatedSeconds"/> rather than absorbed, because a
        /// silent truncation reads as a bug and a loud one reads as a decision.
        /// </remarks>
        public int MaxSecondsPerTick
        {
            get { return _maxSecondsPerTick; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        "A tick that simulates no time is a paused tick; use OffscreenFidelity.Paused.");
                }
                _maxSecondsPerTick = value;
            }
        }

        /// <summary>
        /// The most decay steps one lot may be given in a single <see cref="Tick"/>. A game day at
        /// <see cref="OffscreenFidelity.Full"/>'s cadence by default, so the cap never bites at the
        /// default <see cref="MaxSecondsPerTick"/> and is here as a backstop against a caller who
        /// raises that one.
        ///
        /// <para>
        /// Backlog is <b>deferred, not dropped</b>: a lot that is refused simply does not advance
        /// its <see cref="OffscreenLotRecord.LastSimulatedTick"/>, and catches up on a later tick.
        /// That is what keeps the frame-invariance property true under the cap as well as above
        /// it - the steps applied are always a prefix of the absolute step sequence.
        /// </para>
        /// </summary>
        public int MaxDecayStepsPerLot
        {
            get { return _maxDecayStepsPerLot; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        "A lot that may never be given a decay step is a paused world.");
                }
                _maxDecayStepsPerLot = value;
            }
        }

        /// <summary>How many lots have a record. Hollow lots count once one has been made for them.</summary>
        public int RecordCount
        {
            get { return _records.Count; }
        }

        /// <summary>
        /// Advance the world by <paramref name="deltaSeconds"/> of real time.
        ///
        /// <para>
        /// Two things happen, in this order, both under the fidelity gate. The clock is advanced -
        /// not conditional on there being anything to simulate, because a neighbourhood whose
        /// time stops when the last lot leaves it is the bug B6 exists to fix. Then every stored
        /// lot that is not the active one is decayed, at this fidelity's cadence, stepping through
        /// the grid implied by <see cref="WorldClock.Ticks"/>.
        ///
        /// <para>
        /// A lot this tick sees for the first time is treated as having been in the store since
        /// the window opened, so it decays for the whole of this tick and not for nothing. That is
        /// the difference between "the world moves on while the player is elsewhere" working and
        /// not working: anchoring a new lot at "now" would quietly discard eight hours of a
        /// neighbour's decay on the single frame that first noticed it.
        /// </para>
        ///
        /// <para>
        /// <b>Paused freezes everything.</b> Under <see cref="OffscreenFidelity.Paused"/> this
        /// returns <see cref="OffscreenTickResult.PausedResult"/> before reading the clock: no
        /// tick, no motive write, not even a record for a lot being seen for the first time. See
        /// the remarks on <see cref="OffscreenFidelity"/>.
        /// </para>
        /// </summary>
        /// <param name="deltaSeconds">Real seconds since the last tick. Must not be negative or NaN.</param>
        /// <param name="clock">The neighbourhood's clock. Required; this is what time is written to.</param>
        /// <param name="store">The stored lots. Required; this is the only route to lot contents.</param>
        /// <param name="layout">
        /// The layout, read for the active lot when <see cref="ActiveLot"/> is 0. Required, and
        /// required to be non-null even though almost nothing else is read from it: a world that
        /// ticks off-screen without saying which lot the player is in is a world this type would
        /// have to guess at.
        /// </param>
        public OffscreenTickResult Tick(double deltaSeconds, WorldClock clock, LotStore store,
            NeighbourhoodLayout layout)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (store == null) throw new ArgumentNullException(nameof(store));
            if (layout == null) throw new ArgumentNullException(nameof(layout));

            if (Fidelity == OffscreenFidelity.Paused) return OffscreenTickResult.PausedResult();

            var result = OffscreenTickResult.At(Fidelity);

            var seconds = ClampDelta(deltaSeconds, result);
            if (seconds <= 0) return result;

            //The remainder is banked: a per-frame caller hands in half a tick at 60 fps, and rounding
            //each call on its own made the clock run at 0x or 2x depending on frame jitter.
            var exact = seconds * GameMinutesPerRealSecond * TicksPerMinute(clock) + _tickRemainder;
            var whole = Math.Floor(exact);
            _tickRemainder = exact - whole;
            var ticks = (int)whole;
            if (ticks <= 0) return result;

            //Where the clock was before this tick's ticks went on it. The window a first sighting
            //owes decay for - a lot has been in the store for this long, so this long is its due.
            var windowStartTick = clock.Ticks;

            //Anchor the tick-to-hour mapping on the first tick, from the clock's own reading. After
            //this the hour of any tick is arithmetic, so a step's hour is a property of the step.
            if (!_anchored)
            {
                _anchorTick = windowStartTick;
                _anchorHour = clock.Hours;
                _anchored = true;
            }

            for (int i = 0; i < ticks; i++) clock.Tick();
            result.AddClock(ticks);

            SimulateLots(result, clock, store, layout, windowStartTick);
            return result;
        }

        private static int TicksPerMinute(WorldClock clock)
        {
            //WorldClock divides its own Seconds unguarded above zero, so a clock that has never
            //been given a time is not necessarily one that has a rate. The fallback is TS1's,
            //which is what WorldClock's own constructor starts it at.
            return clock.TicksPerMinute > 0 ? clock.TicksPerMinute : WorldClock.TicksPerMinuteTs1;
        }

        private double ClampDelta(double deltaSeconds, OffscreenTickResult result)
        {
            if (double.IsNaN(deltaSeconds))
            {
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds), deltaSeconds,
                    "Elapsed time is a number of seconds; NaN is a bug in the caller, not a pause.");
            }
            if (deltaSeconds < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(deltaSeconds), deltaSeconds,
                    "Time does not run backwards in a neighbourhood.");
            }
            if (deltaSeconds <= _maxSecondsPerTick) return deltaSeconds;

            //+Infinity lands here too, which is the right answer for it: an infinite frame is a
            //very long frame, and the cap is exactly the statement of what this type does with one.
            var excess = deltaSeconds >= int.MaxValue ? int.MaxValue : (int)deltaSeconds;
            result.AddTruncation(excess - _maxSecondsPerTick);
            return _maxSecondsPerTick;
        }

        private void SimulateLots(OffscreenTickResult result, WorldClock clock, LotStore store,
            NeighbourhoodLayout layout, long windowStartTick)
        {
            var activeLot = ResolveActiveLot(layout);
            var stepMinutes = StepGameMinutes();
            var ticksPerStep = (long)TicksPerMinute(clock) * stepMinutes;
            if (ticksPerStep <= 0) return;

            //The visitor memo is scoped to this window, not to this object. The world's calendar
            //can have rolled over since the last tick, and a memo that outlived one would answer
            //today's question with last night's date - so it is dropped here rather than trusted
            //by tick, which is the only thing the memo is keyed on.
            ForgetVisitorMemo();

            //LoadedLots is a snapshot in ascending lot order, so lots are decayed in the order of
            //their numbers rather than of whatever order a dictionary was iterated in. The draws
            //are keyed by lot anyway; this is about the log line being reproducible.
            var lots = store.LoadedLots;
            for (int i = 0; i < lots.Count; i++)
            {
                var lot = lots[i];
                if (lot == activeLot) continue;

                VMMarshal marshal;
                var resident = store.TryGetActive(lot, out marshal);
                if (!resident) marshal = null;

                //The layout's own record for this lot, or null when the layout has never heard of
                //it. It is the only source of a lot's footprint, which is the one real input the
                //room model has - and a null here is not an error, it is "we know nothing about
                //this lot's inside", which the room model acts on by modelling nothing.
                var layoutLot = layout.FindLot(lot);

                //The creating overload, deliberately not the public RecordFor: a lot the store knows
                //about is a lot the simulator has an opinion about, whether or not a caller has ever
                //asked for its record. A null here would be an NRE on the first frame a stored lot
                //was seen, which is exactly the frame that matters.
                var record = RecordFor(lot, true);
                SimulateLot(result, clock, lot, marshal, resident, layoutLot, record, stepMinutes,
                    ticksPerStep, windowStartTick);
            }
        }

        private void SimulateLot(OffscreenTickResult result, WorldClock clock, short lot,
            VMMarshal marshal, bool resident, NeighbourhoodLot layoutLot, OffscreenLotRecord record,
            int stepMinutes, long ticksPerStep, long windowStartTick)
        {
            var rng = LotStream(lot);
            //The seed draw must not advance the stream the per-step decay is derived from:
            //NeighbourhoodRng.ForLabel derives from the stream's *current* state, so seeding a lot
            //on the same tick that first decays it would shift every step stream after it and make
            //one long tick differ from many short ones. A labelled sub-stream keeps the two apart.
            var seedRng = rng.ForLabel("seed");
            record.SeedIfNew(ref seedRng);

            //The two opt-in models, drawn from labels of their own for the same reason: a room
            //model that shifted a decay draw would make the decay depend on whether the caller had
            //asked for a room score. Neither is gated on anything the decay is gated on, so with
            //both off nothing below runs and the lot behaves exactly as it did before they existed.
            if (ModelsRoomScore)
            {
                var roomRng = rng.ForLabel(RoomStreamLabel);
                record.ModelRoomIfNew(layoutLot, ref roomRng);
            }
            if (EstimatesHouseholdIncome)
            {
                short family;
                int earners, promotion;
                if (!HouseholdOf(marshal, resident, out family, out earners, out promotion))
                {
                    family = OffscreenSimulator.NoFamilyNumber;
                    earners = 0;
                    promotion = 0;
                }
                var incomeRng = rng.ForLabel(IncomeStreamLabel);
                record.ModelIncomeIfNew(family, earners, promotion, ref incomeRng);
            }

            if (!record.HasSimulated)
            {
                //A lot seen for the first time starts from the beginning of the window that just
                //elapsed, not from its end - and then decays normally. Those two choices are the
                //difference between "the world moves on while the player is elsewhere" working and
                //not: a lot that has been in the store for eight hours owes eight hours of decay,
                //and anchoring it at "now" would silently discard all of it on the one tick that
                //first saw it. The aggregate is taken by that same call, so a resident lot's record
                //also learns how many Sims it has from the moment it is seen.
                record.MarkSimulated(windowStartTick, 0);
            }

            var targetStep = clock.Ticks / ticksPerStep;
            var lastStep = record.LastSimulatedTick / ticksPerStep;
            var due = targetStep - lastStep;
            if (due <= 0)
            {
                //Nothing on this lot's grid: a coarser fidelity than the last tick, or a tick too
                //short to cross a step boundary. The stamp still moves, or a switch back to the
                //finer grid would replay the whole gap.
                record.MarkSimulated(clock.Ticks, 0);
                return;
            }

            var steps = due;
            var deferred = false;
            if (steps > _maxDecayStepsPerLot)
            {
                steps = _maxDecayStepsPerLot;
                deferred = true;
            }

            //B8's company, resolved per step and before anything is written, so the multipliers a
            //lot's decay runs under are a property of the steps rather than of the frame that
            //carried them. Null - and therefore the pre-B8 rates exactly - when no source is set.
            int companySteps, lastVisitors;
            var company = CompanyScales(lot, lastStep, steps, ticksPerStep, clock, out companySteps,
                out lastVisitors);

            var applied = resident
                ? DecayResidentLot(record, marshal, rng, clock, lastStep, steps, ticksPerStep, company)
                : DecayCoarseLot(record, rng, clock, lastStep, steps, ticksPerStep, company);

            //The stamp lands on the end of the range actually applied, not on "now". That is what
            //makes the deferral above a deferral: the unapplied steps are still ahead of the lot,
            //in order, and no regrouping of the ticks can change which ones they are.
            record.MarkSimulated((lastStep + steps) * ticksPerStep, applied.steps);
            StampLot(lot, record.LastSimulatedTick);

            if (deferred) result.AddBacklog();
            if (applied.steps > 0)
            {
                result.AddDecay(applied.steps, 1, applied.avatars);
                //Counted only where decay actually ran. A lot whose marshal holds no Sims is not
                //a lot somebody was visiting, and saying so would be a claim the record's own
                //AvatarCount -1 already contradicts.
                if (companySteps > 0)
                {
                    record.AddCompany(companySteps, lastVisitors);
                    result.AddCompany(1, companySteps);
                }

                //The payday, counted over the steps that actually ran and nowhere else - so a
                //deferred backlog is paid for when it runs, and a lot that was owed no steps is
                //not paid for steps it never had. Keyed on the same absolute step range the decay
                //just used, which is what makes the count independent of how the absence was
                //chopped into frames.
                if (record.HasIncomeEstimate)
                {
                    var paydays = PaydaysBetween(lastStep, (int)applied.steps, stepMinutes, record.PaydayHour);
                    if (paydays > 0)
                    {
                        record.AddPaydays(paydays);
                        result.AddPaydays(1, paydays);
                    }
                }
            }
        }

        /// <summary>
        /// The two things about money a stored marshal carries: which family the lot's Sims belong
        /// to, and how many of them have a job. <c>false</c> when the lot has no family, which is
        /// every hollow lot and every marshal with no people in it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The family is the <c>FAMI</c> chunk id in
        /// <see cref="VMPersonDataVariable.TS1FamilyNumber"/> - the word
        /// <c>SimitoneGlobalLink.ResolveFamily</c> reads to find the authority that owns the money.
        /// The first stored Sim that names a family is the lot's family; a lot with two is a lot
        /// with a question TS1 has no answer to, and one family is the honest reading of it.
        /// </para>
        /// <para>
        /// An earner is a Sim whose <see cref="VMPersonDataVariable.JobType"/> is not zero, which
        /// is the same test <c>VMSuitProvider</c> makes when it goes looking for a job's uniform, and
        /// the highest <see cref="VMPersonDataVariable.JobPromotionLevel"/> among them stands for
        /// the household's rung. What the job <em>pays</em> is not here: a job level's salary and
        /// working hours come out of <c>work.iff</c>, which is content.
        /// </para>
        /// </remarks>
        private static bool HouseholdOf(VMMarshal marshal, bool resident, out short family,
            out int earners, out int promotion)
        {
            family = OffscreenSimulator.NoFamilyNumber;
            earners = 0;
            promotion = 0;
            if (!resident || marshal == null || marshal.Entities == null) return false;

            var topPromotion = 0;
            for (int e = 0; e < marshal.Entities.Length; e++)
            {
                var avatar = marshal.Entities[e] as VMAvatarMarshal;
                if (avatar == null) continue;
                var personData = avatar.PersonData;
                if (personData == null) continue;

                if (family == OffscreenSimulator.NoFamilyNumber
                    && personData.Length > (int)VMPersonDataVariable.TS1FamilyNumber)
                {
                    family = personData[(int)VMPersonDataVariable.TS1FamilyNumber];
                }
                if (personData.Length <= (int)VMPersonDataVariable.JobPromotionLevel) continue;
                if (personData[(int)VMPersonDataVariable.JobType] == 0) continue;

                earners++;
                var level = personData[(int)VMPersonDataVariable.JobPromotionLevel];
                if (level > topPromotion) topPromotion = level;
            }

            promotion = topPromotion;
            return family > OffscreenSimulator.NoFamilyNumber;
        }

        /// <summary>
        /// One clock per decay step, pinned to the step's own boundary, for the visitor source to
        /// be asked about. Reused: it is the same instant for every lot on the same step index, and
        /// a walk of the layout is not something to redo per lot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a projection rather than the world's clock.</b> The world clock has already been
        /// advanced to the end of the window by the time any decay runs, so handing it over would
        /// make every step in a long tick read the same hour - and the whole point of the absolute
        /// step index is that a step means the same thing however many steps were applied with it.
        /// This is the same argument as <see cref="HourOfTick"/>, applied to a whole clock.
        /// </para>
        /// <para>
        /// <b>What it is exact about.</b> The hour, and the tick count. Those are the two fields
        /// <see cref="NeighbourhoodWorldFeatures"/>'s schedule reads - <c>SecondOfDay</c> off the
        /// hour and its day index off the tick count - so for the one source this is written
        /// against the projection answers exactly what a real clock at that instant would.
        /// </para>
        /// <para>
        /// <b>What it is not.</b> The calendar fields are copied from the world's clock as it reads
        /// at the end of the window, and the minutes and seconds are zero. A source that wants
        /// "who is here at 20:30" gets 20:00; one that wants the date gets the end of the window's
        /// date. Both are stated rather than hidden because the alternative - stepping a scratch
        /// clock forward to each boundary - would be a few thousand <c>VMClock</c> ticks per step
        /// to answer a question the model asks once an hour.
        /// </para>
        /// </remarks>
        private WorldClock StepClock(WorldClock clock, long tick, int ticksPerMinute)
        {
            if (_stepClock != null && _stepClockTick == tick) return _stepClock;

            var projection = new WorldClock();
            projection.PullFrom(new VMClock
            {
                Hours = HourOfTick(tick, ticksPerMinute),
                Minutes = 0,
                MinuteFractions = 0,
                TicksPerMinute = ticksPerMinute,
                DayOfMonth = clock.DayOfMonth,
                Month = clock.Month,
                Year = clock.Year,
                Ticks = tick
            });
            _stepClock = projection;
            _stepClockTick = tick;
            return projection;
        }

        /// <summary>
        /// This lot's per-step decay multipliers, in step order, or <c>null</c> when there is no
        /// visitor source - the null answer being the whole of the "byte-identical without B8"
        /// promise. A source that names nobody is not the same as no source: it returns an array
        /// of <see cref="NoCompanyScale"/>, which
        /// <see cref="ScaleFraction"/> turns into the identical draw, and that equivalence is
        /// asserted rather than assumed.
        /// </summary>
        private int[] CompanyScales(short lot, long lastStep, long steps, long ticksPerStep,
            WorldClock clock, out int companySteps, out int lastVisitors)
        {
            companySteps = 0;
            lastVisitors = NoCompany;
            if (_visitorLots == null || steps <= 0) return null;

            var count = (int)steps;
            if (_companyScales == null || _companyScales.Length < count) _companyScales = new int[count];
            var scales = _companyScales;
            //Rewritten in full, every time. A stale entry left from a longer range of steps would
            //be read as this lot's rate, and nothing about that failure would look wrong.
            for (int i = 0; i < count; i++) scales[i] = NoCompanyScale;

            var ticksPerMinute = clock.TicksPerMinute > 0 ? clock.TicksPerMinute : WorldClock.TicksPerMinuteTs1;
            for (long s = 0; s < steps; s++)
            {
                //The same boundary DecayStep will read its own hour from - one step further on
                //again, because a step belongs to the instant it ends at. Two expressions of the
                //same instant is the thing most likely to drift apart, so this is stated rather
                //than left as a coincidence.
                var step = lastStep + s + 1;
                ReadVisitors(lot, (step + 1) * ticksPerStep, ticksPerMinute, clock, out var visitors);
                if (visitors <= NoCompany) continue;
                scales[(int)s] = CompanyScaleFor(visitors);
                companySteps++;
                lastVisitors = visitors;
            }
            return scales;
        }

        /// <summary>
        /// The memoised answer to "who is in this lot at this instant": the normalised lot list
        /// and the head count for each, read once per distinct instant and shared by every lot and
        /// every step that lands on it.
        /// </summary>
        private void ReadVisitors(short lot, long tick, int ticksPerMinute, WorldClock clock, out int visitors)
        {
            visitors = NoCompany;
            if (_snapshotLots == null || _snapshotTick != tick)
            {
                var projection = StepClock(clock, tick, ticksPerMinute);
                var lots = LotsWithVisitors(projection);
                var counts = new int[lots.Count];
                for (int i = 0; i < lots.Count; i++) counts[i] = CountIn(projection, lots[i]);
                _snapshotLots = lots;
                _snapshotCounts = counts;
                _snapshotTick = tick;
            }

            for (int i = 0; i < _snapshotLots.Count; i++)
            {
                if (_snapshotLots[i] != lot) continue;
                visitors = _snapshotCounts[i];
                return;
            }
        }

        private void ForgetVisitorMemo()
        {
            _stepClock = null;
            _stepClockTick = 0L;
            _snapshotLots = null;
            _snapshotCounts = null;
            _snapshotTick = long.MinValue;
        }

        private struct LotDecay
        {
            public int steps;
            public int avatars;
        }

        /// <summary>
        /// Decay a lot whose stored motive data is reachable: every <see cref="VMAvatarMarshal"/>
        /// in its marshal, writing each one's <c>MotiveData</c> in place. This is B6's real case -
        /// a resident lot has a Sim table and there is nothing coarse about writing it.
        /// </summary>
        private LotDecay DecayResidentLot(OffscreenLotRecord record, VMMarshal marshal, NeighbourhoodRng rng,
            WorldClock clock, long lastStep, long steps, long ticksPerStep, int[] company)
        {
            var applied = new LotDecay();
            if (marshal == null || marshal.Entities == null)
            {
                //A marshal with no entity array is a lot with no Sims in it - an empty plot, or a
                //hand-built one. Leaving the aggregate as seeded keeps "no Sims here" and "one
                //invisible Sim here" distinguishable, which SetAggregate(0, ...) would not.
                return applied;
            }

            var sum = new int[OffscreenLotRecord.MotiveCount * 2];
            var avatars = 0;
            for (int e = 0; e < marshal.Entities.Length; e++)
            {
                var avatar = marshal.Entities[e] as VMAvatarMarshal;
                if (avatar == null) continue;

                var motiveData = avatar.MotiveData;
                if (motiveData == null || motiveData.Length <= (int)VMMotive.Fun) continue;

                //The modelled room score goes where the engine reads one from, so DecayStep's mood
                //sum includes it exactly as the engine's own does. It is written only when the
                //caller asked for the model: off, the slot keeps whatever the last live VM put
                //there, which is the pre-existing behaviour and the one the engine overwrites on
                //the lot's very first tick in a running VM.
                if (record.HasRoomScore) motiveData[(int)VMMotive.Room] = record.RoomScore;

                avatars++;
                var accumulator = AccumulatorFor(avatar, record);
                var personality = PersonalityFor(avatar);
                for (long s = 0; s < steps; s++)
                {
                    DecayStep(motiveData, accumulator, lastStep + s + 1, personality, record.Lot, clock, rng,
                        ticksPerStep, CompanyAt(company, (int)s));
                    applied.steps++;
                }
                SumAvatar(sum, motiveData, avatars);
            }

            if (avatars == 0) return applied;

            record.SetAggregate(avatars, Mean(sum, avatars), (short)ClampMotive(sum[1] / avatars));
            applied.avatars = avatars;
            return applied;
        }

        /// <summary>
        /// Decay a lot with no reachable motive data - hollow, or never entered - into its own
        /// record. This is the documented coarse model: the same TS1 constants, the same
        /// accumulator arithmetic, the same bounds, applied to one aggregate vector instead of to
        /// a Sim's table.
        ///
        /// <para>
        /// The aggregate is presented to the decay through a full sixteen-slot motive array, with
        /// a room score of zero, so that the coarse path runs the <em>same</em> code as the
        /// resident path. A second implementation would be a second thing to be wrong, and the
        /// only thing the coarse model actually differs by is which array it is handed.
        /// </para>
        ///
        /// <para>
        /// <paramref name="company"/> is the per-step B8 multiplier and is treated exactly as the
        /// resident path treats it: a hollow lot is the lot most likely to be a community lot, and
        /// a company effect that applied only to lots with marshals would be a company effect
        /// that stopped exactly where the model is thinnest.
        /// </para>
        /// </summary>
        private LotDecay DecayCoarseLot(OffscreenLotRecord record, NeighbourhoodRng rng, WorldClock clock,
            long lastStep, long steps, long ticksPerStep, int[] company)
        {
            var applied = new LotDecay();
            var scratch = CoarseScratch(record);
            for (long s = 0; s < steps; s++)
            {
                DecayStep(scratch, record.Fractions, lastStep + s + 1, PopulationAveragePersonality(),
                    record.Lot, clock, rng, ticksPerStep, CompanyAt(company, (int)s));
                applied.steps++;
            }

            //Hand the aggregate back. Room is the modelled room score, or the stated zero it has
            //always been with ModelsRoomScore off, and it is the one number the coarse model has to
            // make up - which is why IsCoarseModel is a question worth asking of any record before
            //its motives are believed, and why the room model is opt-in.
            var motives = VMTS1MotiveDecay.DecrementMotives;
            var aggregate = record.MotivesBuffer;
            for (int i = 0; i < OffscreenLotRecord.MotiveCount && i < motives.Length; i++)
            {
                aggregate[i] = scratch[(int)motives[i]];
            }
            record.SetMood(record.MoodOfBuffer(scratch[(int)VMMotive.Room]));
            return applied;
        }

        /// <summary>
        /// A sixteen-slot motive array carrying one lot's aggregate and nothing else, reused
        /// between steps so the coarse path allocates once per tick rather than once per step.
        /// </summary>
        private short[] CoarseScratch(OffscreenLotRecord record)
        {
            if (_scratch == null) _scratch = new short[16];
            var motives = VMTS1MotiveDecay.DecrementMotives;
            for (int i = 0; i < OffscreenLotRecord.MotiveCount && i < motives.Length; i++)
            {
                _scratch[(int)motives[i]] = record.MotivesBuffer[i];
            }
            //The modelled room score, or the stated zero a coarse lot has always reported. Both are
            //folded into the mood by the same line of the same DecayStep the resident path runs, so
            //turning the model on cannot make the coarse path a second implementation of the decay.
            _scratch[(int)VMMotive.Room] = record.RoomScore;
            _scratch[(int)VMMotive.Mood] = 0;
            return _scratch;
        }

        private short[] _scratch;

        /// <summary>
        /// The accumulator a Sim's decay should carry: the engine's own, when the marshal still
        /// has one, so that loading the lot resumes mid-accumulation exactly where this left it.
        /// Otherwise a per-Sim table in the record, keyed by object id.
        /// </summary>
        private static short[] AccumulatorFor(VMAvatarMarshal avatar, OffscreenLotRecord record)
        {
            var decay = avatar.MotiveDecay as VMTS1MotiveDecay;
            if (decay != null && decay.MotiveFractions != null
                && decay.MotiveFractions.Length == OffscreenLotRecord.MotiveCount)
            {
                return decay.MotiveFractions;
            }
            return record.FractionsFor(avatar.ObjectID);
        }

        /// <summary>This lot's stream, derived once per call and re-derived per step beneath it.</summary>
        private NeighbourhoodRng LotStream(short lot)
        {
            return _lotRoot.ForLabel(lot.ToString(CultureInfo.InvariantCulture));
        }

        /// <summary>
        /// The hour of the day a step <em>belongs to</em>, derived from the tick the step ends on.
        ///
        /// <para>
        /// This exists because the sleep guess and the engine's wake-at-seven rule both read the
        /// hour, and reading <c>clock.Hours</c> directly makes them a function of when the frame
        /// landed rather than of when the step happened. That is invisible at a steady frame rate
        /// and a real divergence at any other one: eight hours in one tick and eight hours in 480
        /// ticks would decay the same lot differently, which would break the frame-invariance the
        /// absolute step index was introduced to guarantee.
        /// </para>
        ///
        /// <para>
        /// The mapping from tick to hour is anchored once, on the first tick, from the clock's own
        /// reading - and a clock advances exactly one hour per
        /// <c>TicksPerMinute * 60</c> ticks, so the relation is exact arithmetic rather than a
        /// second calendar to keep in step with <c>VMClock</c>. Anchoring rather than reading
        /// <c>clock.Hours</c> each step is what makes the answer a function of the step alone: a
        /// step's hour no longer depends on how many steps the frame that applied it happened to
        /// carry.
        /// </para>
        /// </summary>
        private int HourOfTick(long tick, int ticksPerMinute)
        {
            if (!_anchored)
            {
                //No tick has anchored it yet - only reachable if a step is somehow applied before
                //the first Tick, which the type does not do. Anchoring at zero rather than throwing
                //keeps the failure a wrong hour instead of a crash on the game thread.
                _anchorTick = tick;
                _anchorHour = 0;
                _anchored = true;
            }
            var ticksPerHour = (long)ticksPerMinute * 60;
            if (ticksPerHour <= 0) return 0;
            var hours = (tick - _anchorTick) / ticksPerHour;
            return (int)(((_anchorHour + hours) % 24 + 24) % 24);
        }

        private double _tickRemainder;
        private bool _anchored;
        private long _anchorTick;
        private int _anchorHour;

        /// <summary>One step's company multiplier, or none for a lot that had no visitor source.</summary>
        private static int CompanyAt(int[] company, int offset)
        {
            if (company == null || offset < 0 || offset >= company.Length) return NoCompanyScale;
            return company[offset];
        }

        /// <summary>
        /// One decay step, restated from <c>VMTS1MotiveDecay.Tick</c> against the same constants.
        ///
        /// <para>
        /// The two branches it cannot take are named on <see cref="OffscreenSimulator"/>: the room
        /// a given Sim is standing in, which needs a room table - so the room slot carries either the
        /// caller's modelled score or whatever the last live VM wrote - and a Sim's actual sleeping,
        /// which needs a bed. Everything else is the engine's arithmetic, including the truncations
        /// inside <c>ToFixed1000</c> and the two post-hoc accumulator corrections, because those
        /// corrections are where The Sims 1's "half a point at a time" feel actually lives.
        /// </para>
        ///
        /// <para>
        /// <paramref name="companyScale"/> is B8's head count, already reduced to a multiplier by
        /// <see cref="CompanyScaleFor"/>, and it goes into the same single scale the seed jitter
        /// does rather than into a second one. That is what keeps the B6/B8 boundary honest: a
        /// step's rate is multiplied exactly once, and a lot with nobody in it multiplies by
        /// exactly what it multiplied by before the visitors existed.
        /// </para>
        /// </summary>
        private void DecayStep(short[] motiveData, short[] fractions, long stepIndex,
            int[] personality, short lot, WorldClock clock, NeighbourhoodRng lotRng, long ticksPerStep,
            int companyScale)
        {
            var constants = VMTS1MotiveDecay.Constants;
            var motives = VMTS1MotiveDecay.DecrementMotives;
            if (constants == null || constants.Length < 17) return;
            if (motives == null || motives.Length != OffscreenLotRecord.MotiveCount) return;
            if (fractions == null || fractions.Length != OffscreenLotRecord.MotiveCount) return;

            //The step's own sub-stream. Keyed on the absolute step index, which is what makes the
            //result independent of how the elapsed time was chopped into frames.
            var rng = lotRng.ForLabel(StepStreamLabel + stepIndex.ToString(CultureInfo.InvariantCulture));
            //The hour this step belongs to, not the hour the frame is running at. The step's own
            //boundary tick, so a step means the same thing however many steps were applied with it.
            var ticksPerMinute = clock.TicksPerMinute > 0 ? clock.TicksPerMinute : WorldClock.TicksPerMinuteTs1;
            var hours = HourOfTick((stepIndex + 1) * ticksPerStep, ticksPerMinute);
            var asleep = SleepStateFor(motiveData, hours);

            var rHunger = ToFixed1000(constants[5]) * (100 + motiveData[(int)VMMotive.Hunger]);
            var moodSum = 0;

            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++)
            {
                var motive = motiveData[(int)motives[i]];
                int frac;
                switch (i)
                {
                    case 0:
                        frac = rHunger;
                        break;
                    case 1:
                        //Personality-driven comfort. At Full the engine's own 666 test picks one
                        //of three constants; at Coarse the neutral one is used directly.
                        frac = personality[0] > 666 ? ToFixed1000(constants[14])
                            : personality[0] < 666 ? ToFixed1000(constants[15])
                            : ToFixed1000(constants[16]);
                        break;
                    case 2:
                        frac = ToFixed1000(asleep ? constants[11] : constants[10]);
                        break;
                    case 3:
                        frac = ToFixed1000(asleep ? constants[13] : constants[12])
                            + FracMul(rHunger, ToFixed1000(constants[4]));
                        break;
                    case 4:
                        if (asleep)
                        {
                            frac = hours >= (int)constants[2] ? ToFixed1000(constants[3]) : 0;
                        }
                        else
                        {
                            frac = ToFixed1000(constants[0]) / (30 * (int)constants[1]);
                        }
                        break;
                    case 5:
                        frac = asleep ? 0 : ToFixed1000(constants[8]);
                        break;
                    default:
                        frac = ToFixed1000(constants[6]) + FracMul(ToFixed1000(constants[7]), personality[1]);
                        break;
                }

                //The one place this type is deliberately not the engine. A lot the player has left
                //is not a lot whose Sims sit still for eight hours: the engine would have had them
                //eating, using the bathroom and finding something to do, and a model that applies
                //the bare decay rate to a whole day of nobody is a model of a coma rather than of
                //an absence. So each step's rate is scaled by a bounded per-lot, per-step factor,
                //which is the stream's real job - it is what stops every household in a
                //neighbourhood decaying in lockstep and makes the world seed visible in a lot that
                //was never opened.
                //
                //Bounded on purpose: the scale is 1 ± MaxDecayJitter, so no combination of steps
                //can leave the engine's -100..100 range, and the sign is never touched - decay still
                //decays. One draw per step per motive is 7 per step, cheap and derived from the same
                //labelled sub-stream, so the frame-invariance the step index buys is not spent here.
                frac = ScaleFraction(frac, rng, companyScale);

                //The engine's two corrections, not one. The second runs even when the first has
                //just run, because the engine writes them as two ifs and a negative accumulator is
                //the one case where the order matters.
                fractions[i] = (short)(fractions[i] + frac);
                if (fractions[i] >= 1000)
                {
                    motive -= (short)(fractions[i] / 1000);
                    fractions[i] %= 1000;
                    motiveData[(int)motives[i]] = (short)ClampMotive(motive);
                }
                if (fractions[i] < 0)
                {
                    motive += (short)(1 - fractions[i] / 1000);
                    fractions[i] += (short)((1 - fractions[i] / 1000) * 1000);
                    motiveData[(int)motives[i]] = (short)ClampMotive(motive);
                }
                moodSum += motiveData[(int)motives[i]];
            }

            moodSum += motiveData[(int)VMMotive.Room];
            motiveData[(int)VMMotive.Mood] = (short)ClampMotive(moodSum / 8);
        }

        /// <summary>
        /// Whether this Sim is asleep for this step. Its own <c>SleepState</c> is the starting
        /// truth; a clock rule is the whole of what changes it, and the engine's own "wake at the
        /// wake hour if rested enough" rule is kept because that is what makes the guess
        /// self-correcting rather than merely plausible.
        /// </summary>
        private bool SleepStateFor(short[] motiveData, int hours)
        {
            if (!SleepsOffScreen) return motiveData[(int)VMMotive.SleepState] != 0;

            var constants = VMTS1MotiveDecay.Constants;
            var bedded = hours >= BedHour || hours < WakeHour;
            if (hours == WakeHour && constants != null && constants.Length > 2)
            {
                //The engine's own wake condition, on its own constants: awake at the wake hour
                //when rested to within 100 of the energy span.
                if (motiveData[(int)VMMotive.Energy] >= constants[0] - 100) bedded = false;
            }
            //0 rather than -1 deliberately. The engine writes -1 for "passed out" and charges a
            //heavy energy penalty for it; a Sim this code has no business putting to bed should
            //not also be recorded as unconscious.
            motiveData[(int)VMMotive.SleepState] = (short)(bedded ? 1 : 0);
            return bedded;
        }

        /// <summary>
        /// Active and outgoing personality for one Sim. At <see cref="OffscreenFidelity.Full"/>
        /// these are read out of the marshal's person data, which is where the engine reads them
        /// from; at <see cref="OffscreenFidelity.Coarse"/> the population midpoint stands in, and
        /// that value is the one which makes the engine's own 666 test come out neutral.
        /// </summary>
        private int[] PersonalityFor(VMAvatarMarshal avatar)
        {
            if (Fidelity != OffscreenFidelity.Full) return PopulationAveragePersonality();
            var personData = avatar.PersonData;
            if (personData == null || personData.Length <= (int)VMPersonDataVariable.OutgoingPersonality)
            {
                return PopulationAveragePersonality();
            }
            return new int[]
            {
                personData[(int)VMPersonDataVariable.ActivePersonality],
                personData[(int)VMPersonDataVariable.OutgoingPersonality]
            };
        }

        /// <summary>
        /// Active and outgoing at TS1's neutral values: active exactly 666, so the engine's own
        /// <c>&gt; 666</c> / <c>&lt; 666</c> test takes neither branch and the plain
        /// <c>Constants[16]</c> is used; outgoing 500, the midpoint of the 0..1000 scale.
        /// </summary>
        private static int[] PopulationAveragePersonality()
        {
            return new[] { NeutralActivePersonality, NeutralOutgoingPersonality };
        }

        /// <summary>Active personality that the engine's 666 test treats as neither extreme.</summary>
        public const int NeutralActivePersonality = 666;

        /// <summary>Outgoing personality at the midpoint of TS1's 0..1000 scale.</summary>
        public const int NeutralOutgoingPersonality = 500;

        private static int ToFixed1000(float input)
        {
            return (int)(input * 1000);
        }

        /// <summary>
        /// Scale one step's 1000ths by a bounded random factor, preserving the sign and the zero.
        ///
        /// <para>
        /// A zero stays a zero: a sleeping Sim does not start decaying energy at random, and a rate
        /// the engine computed as nothing is a statement about the Sim's state rather than about the
        /// frame. The sign is preserved for the same reason - this models an absence, and an
        /// absence does not feed anybody.
        /// </para>
        ///
        /// <para>
        /// <b>One multiplication, and the B8 head count is the middle of the window rather than a
        /// second factor on top of one.</b> That is a design decision with a testable consequence
        /// and the test is the point: <see cref="NeighbourhoodRng.NextInt"/> is
        /// <c>min + raw % span</c>, so a window of the same width moved down by <c>n</c> draws the
        /// same raw value and hands back the same result. At <see cref="NoCompanyScale"/> the
        /// window is <c>800..1200</c> and the multiplier that comes out is <c>800 + raw%401</c>,
        /// which is the pre-B8 expression's <c>1000 + (-200 + raw%401)</c> - the same number, not
        /// a similar one. A lot nobody is visiting therefore decays by precisely the rate it
        /// decayed at before the visitors existed, and "no source" and "a source that names
        /// nobody" are the same numbers rather than two near-misses.
        /// </para>
        /// </summary>
        /// <param name="frac">The step's rate, in 1000ths.</param>
        /// <param name="rng">The step's own sub-stream.</param>
        /// <param name="companyScale">
        /// The centre of the window, in 1000ths of a unit: <see cref="NoCompanyScale"/> for an
        /// empty house, <see cref="MinCompanyScale"/> for a full one, and anything in between for
        /// the head counts between. Clamped into that range rather than trusted.
        /// </param>
        private static int ScaleFraction(int frac, NeighbourhoodRng rng, int companyScale)
        {
            if (frac == 0) return 0;

            //Defensive, and defensive in the only direction that can matter. A company scale is
            //always at or below NoCompanyScale, so a value above it is not a faster decay anybody
            //asked for, it is a bug - and a lot's decay must not be made to run faster by one. The
            //lower clamp is belt to the braces for the same reason: MinCompanyScale is the bound
            //CompanyScaleFor already guarantees, and this method is the last thing between a
            //caller's arithmetic and a rate.
            var centre = companyScale > NoCompanyScale
                ? NoCompanyScale
                : (companyScale < MinCompanyScale ? MinCompanyScale : companyScale);
            //The draw *is* the multiplier, in 1000ths of a unit: centre ± MaxDecayJitter about the
            //head count, without touching floating point. Bounded at both ends, so the lowest any
            //lot can reach is MinCompanyScale - MaxDecayJitter - which is 0.5 - and the highest is
            //NoCompanyScale + MaxDecayJitter, which is 1.2. Both bounds are the same as the
            //pre-B8 ones at the top, which is the point of the argument above.
            var scale = rng.NextInt(centre - MaxDecayJitter, centre + MaxDecayJitter + 1);
            var scaled = (long)frac * scale / 1000;

            return (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, scaled));
        }

        private static int FracMul(int input, int frac)
        {
            return (int)((long)input * frac) / 1000;
        }

        internal static int ClampMotive(int value)
        {
            if (value > MaxMotive) return MaxMotive;
            if (value < MinMotive) return MinMotive;
            return value;
        }

        private static void SumAvatar(int[] accumulator, short[] motiveData, int index)
        {
            var motives = VMTS1MotiveDecay.DecrementMotives;
            for (int i = 0; i < OffscreenLotRecord.MotiveCount && i < motives.Length; i++)
            {
                accumulator[i * 2] += motiveData[(int)motives[i]];
            }
            accumulator[1] += motiveData[(int)VMMotive.Mood];
        }

        private static short[] Mean(int[] accumulator, int count)
        {
            var result = new short[OffscreenLotRecord.MotiveCount];
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++)
            {
                result[i] = (short)(accumulator[i * 2] / count);
            }
            return result;
        }

        /// <summary>
        /// This lot's record, or <c>null</c>. Never creates one, so a caller that wants to know
        /// whether a lot has been simulated can ask without simulating it.
        /// </summary>
        public OffscreenLotRecord RecordFor(short lot)
        {
            OffscreenLotRecord record;
            return _records.TryGetValue(lot, out record) ? record : null;
        }

        private OffscreenLotRecord RecordFor(short lot, bool create)
        {
            OffscreenLotRecord record;
            if (_records.TryGetValue(lot, out record)) return record;
            if (!create) return null;
            record = new OffscreenLotRecord(lot);
            _records.Add(lot, record);
            return record;
        }

        /// <summary>
        /// Forget one lot's record. What a lot switch should call once the house file holds
        /// whatever the marshal held, so that the next time the lot is off-screen it is simulated
        /// from what was saved rather than from what was modelled.
        /// </summary>
        public bool Forget(short lot)
        {
            return _records.Remove(lot);
        }

        /// <summary>Forget every lot. What leaving a neighbourhood does to the world it leaves.</summary>
        public void Clear()
        {
            _records.Clear();
        }

        private void StampLot(short lot, long tick)
        {
            //The layout's own per-lot forward field, and the only half of "this lot moved" that has
            //a home in a save today. B7's schema is where the motive numbers go next; until then a
            //resumed world knows the lot is stale and not how stale.
            //Not reached for through LotStore: the store is a marshal index, and a lot's session
            //state belongs to the lot, which travels with it through a layout patch.
            _pendingStamps[lot] = tick;
        }

        private readonly Dictionary<short, long> _pendingStamps = new Dictionary<short, long>();

        /// <summary>
        /// Copy the simulated-tick stamps this simulator has accumulated onto the layout's lot
        /// records, and clear them.
        ///
        /// <para>
        /// Separate from <see cref="Tick"/> on purpose. <see cref="Tick"/> is called every frame
        /// and the layout is the caller's to rebuild; stamping a lot record is a change to the
        /// sidecar-bound part of the world, so it happens once, when the caller says the world is
        /// about to be saved or drawn. A caller that never calls this simply keeps the stamps in
        /// memory, which is a truthful loss and the default.
        /// </para>
        /// </summary>
        public int ApplyStamps(NeighbourhoodLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (_pendingStamps.Count == 0) return 0;

            var applied = 0;
            foreach (var pair in _pendingStamps)
            {
                var lot = layout.FindLot(pair.Key);
                if (lot == null) continue;
                //Only ever forwards. A patch that rebuilt a lot from its sidecar read leaves
                //LastSimulated at whatever the file said, and a tick this world has already
                //applied must not be undone by a save that happened to be older.
                if (pair.Value > lot.LastSimulated)
                {
                    lot.LastSimulated = pair.Value;
                    applied++;
                }
            }
            _pendingStamps.Clear();
            return applied;
        }

        private static NeighbourhoodLayout RequireLayout(NeighbourhoodLayout layout)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            return layout;
        }

        private short ResolveActiveLot(NeighbourhoodLayout layout)
        {
            if (ActiveLot != 0) return (short)ActiveLot;
            var state = layout.WorldState;
            if (state == null || state.ActiveLot < short.MinValue || state.ActiveLot > short.MaxValue) return 0;
            return (short)state.ActiveLot;
        }

        private int StepGameMinutes()
        {
            return Fidelity == OffscreenFidelity.Full ? FullStepGameMinutes : CoarseStepGameMinutes;
        }

        public override string ToString()
        {
            return "offscreen seed " + WorldSeed + " " + Fidelity
                + (ActiveLot == 0 ? string.Empty : " active lot " + ActiveLot)
                + " records " + _records.Count
                + (_visitorLots == null ? " (no visitors)" : " (visitors wired)")
                + (ModelsRoomScore ? " room model" : string.Empty)
                + (EstimatesHouseholdIncome ? " income model" : string.Empty);
        }
    }
}
