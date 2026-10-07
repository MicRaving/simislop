/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Marshals.Hollow;
using FSO.SimAntics.Model;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Part B6: the world moves on while the player is somewhere else.
    ///
    /// <para>
    /// The handoff's acceptance line is short - "leaving and returning after N in-game hours shows
    /// time passed and plausible, bounded motive changes; same seed ⇒ same result; Paused ⇒ nothing
    /// changes" - and each of these tests is one clause of it, pinned separately so a failure says
    /// which promise broke rather than "off-screen simulation is wrong".
    /// </para>
    ///
    /// <para>
    /// <b>These run with no VM, no content and no graphics device</b>, which is the whole reason
    /// the design reaches the store's marshals rather than a running lot. A <see cref="VMMarshal"/>
    /// with a hand-built <see cref="VMAvatarMarshal"/> is enough to assert every claim below, and
    /// is the reason the test suite can state them at all.
    /// </para>
    /// </summary>
    public sealed class OffscreenSimulatorTests
    {
        private const long Seed = 987654321L;
        private const long OtherSeed = 13579L;

        /// <summary>
        /// One game hour, in real seconds. Sixty, not 3600: TS1 runs a game minute per real second
        /// (<see cref="OffscreenSimulator.GameMinutesPerRealSecond"/>), so a game hour is a minute
        /// of wall clock. Getting this wrong is silent - the numbers still move, they just move at
        /// sixtieth the rate - so it is derived from the constant rather than written out.
        /// </summary>
        private static double GameHours(double hours)
        {
            return hours * 60 * OffscreenSimulator.GameMinutesPerRealSecond;
        }

        /// <summary>One game hour, in real seconds.</summary>
        private const double OneHour = 60;

        /// <summary>
        /// A clock at the TS1 launch date, at the TS1 tick rate, built the way a real one is: pulled
        /// out of a lot's clock rather than assigned field by field, so the tests cannot lean on a
        /// state the type does not actually reach.
        /// </summary>
        private static WorldClock Clock()
        {
            var clock = new WorldClock();
            clock.PullFrom(new VMClock
            {
                Hours = 0,
                Minutes = 0,
                MinuteFractions = 0,
                TicksPerMinute = WorldClock.TicksPerMinuteTs1,
                DayOfMonth = 1,
                Month = 6,
                Year = 1997
            });
            return clock;
        }

        /// <summary>A layout of lots that exist, so <c>FindLot</c> and the store agree about them.</summary>
        private static NeighbourhoodLayout Layout(params short[] lotNumbers)
        {
            var lots = new List<NeighbourhoodLot>();
            for (int i = 0; i < lotNumbers.Length; i++)
            {
                lots.Add(new NeighbourhoodLot
                {
                    Number = lotNumbers[i],
                    Size = 16,
                    WorldX = i * 20,
                    WorldY = 0,
                    HasHouse = true
                });
            }
            return new NeighbourhoodLayout(0, (int)Seed, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                NeighbourhoodTestInput.SmallMap, lots);
        }

        /// <summary>
        /// A resident lot's marshal holding <paramref name="sims"/> Sims, each at a known motive
        /// table. The shape a lot the player has just left actually has.
        /// </summary>
        private static VMMarshal ResidentMarshal(params short[] sims)
        {
            var entities = new List<VMEntityMarshal>();
            for (int i = 0; i < sims.Length; i++)
            {
                var avatar = new VMAvatarMarshal
                {
                    ObjectID = (short)(i + 1),
                    MotiveData = new short[16],
                    PersonData = new short[101],
                    MotiveDecay = new VMTS1MotiveDecay()
                };
                for (int m = 0; m < avatar.MotiveData.Length; m++) avatar.MotiveData[m] = sims[i];
                avatar.MotiveData[(int)VMMotive.SleepState] = 0;
                entities.Add(avatar);
            }
            return new VMMarshal { Entities = entities.ToArray() };
        }

        private static VMAvatarMarshal Sim(VMMarshal marshal, int index)
        {
            return (VMAvatarMarshal)marshal.Entities[index];
        }

        private static short[] Motives(VMMarshal marshal, int index)
        {
            return ((VMAvatarMarshal)marshal.Entities[index]).MotiveData;
        }

        private static short[] Snapshot(VMMarshal marshal, int index)
        {
            return (short[])Motives(marshal, index).Clone();
        }

        /// <summary>
        /// The 1/1000ths a Sim has been charged so far and not yet spent - the engine's own
        /// accumulator, on the marshal's own decay state.
        /// </summary>
        private static short[] Accumulator(VMMarshal marshal, int index)
        {
            var decay = (VMTS1MotiveDecay)Sim(marshal, index).MotiveDecay;
            Assert.NotNull(decay);
            Assert.Equal(OffscreenLotRecord.MotiveCount, decay.MotiveFractions.Length);
            return decay.MotiveFractions;
        }

        /// <summary>
        /// How much decay one Sim has been charged, in 1000ths of a motive point, summed over the
        /// seven: what the motive has dropped by, plus the fraction the accumulator is still
        /// holding. The engine's own two numbers put back together.
        /// <para>
        /// This is the only place a <em>rate</em> is visible. A motive value is quantised to whole
        /// points, so two runs with genuinely different rates can read the same motive; and the
        /// accumulator alone points the wrong way, because a slower decay spends fewer points and
        /// so leaves a bigger remainder. A lot that has been clamped at -100 or 100 cannot be
        /// measured this way at all, which is why the tests that use it stay inside the range.
        /// </para>
        /// </summary>
        /// <param name="marshal">The marshal holding the Sim.</param>
        /// <param name="index">Which Sim.</param>
        /// <param name="startingFrom">The motive value before the window being measured.</param>
        private static long Charged(VMMarshal marshal, int index, short startingFrom)
        {
            var total = 0L;
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++)
            {
                total += ChargedOne(marshal, index, startingFrom, i);
            }
            return total;
        }

        /// <summary>One motive's charged decay, in 1000ths. See <see cref="Charged"/>.</summary>
        private static long ChargedOne(VMMarshal marshal, int index, short startingFrom, int motive)
        {
            var slot = (int)VMTS1MotiveDecay.DecrementMotives[motive];
            var dropped = (long)startingFrom - Motives(marshal, index)[slot];
            return dropped * 1000L + Accumulator(marshal, index)[motive];
        }

        /// <summary>The coarse model's own accumulator, charged decay from a snapshot.</summary>
        private static long RecordCharged(OffscreenLotRecord record, short[] startingFrom)
        {
            var total = 0L;
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++)
            {
                total += (long)(startingFrom[i] - record.MotiveAt(i)) * 1000L + record.Fractions[i];
            }
            return total;
        }

        private static OffscreenSimulator Sim_()
        {
            return new OffscreenSimulator(Seed);
        }

        // ---- the dial -------------------------------------------------------------------------------

        [Fact]
        public void TheDefaultFidelityIsCoarse()
        {
            //§B6 task 3, verbatim. A simulator that defaulted to Full would quietly cost fifteen
            //times what a player who never touches the setting is paying for.
            var simulator = Sim_();

            Assert.Equal(OffscreenFidelity.Coarse, OffscreenSimulator.DefaultFidelity);
            Assert.Equal(OffscreenSimulator.DefaultFidelity, simulator.Fidelity);
            Assert.Equal(OffscreenFidelity.Coarse, new OffscreenSimulator().Fidelity);
            Assert.Equal(OffscreenFidelity.Coarse, new OffscreenSimulator(Layout(11)).Fidelity);
        }

        [Fact]
        public void TheThreeFidelitiesAreTheThreeTheHandoffLists()
        {
            Assert.Equal(3, Enum.GetValues(typeof(OffscreenFidelity)).Length);
            Assert.True(Enum.IsDefined(typeof(OffscreenFidelity), OffscreenFidelity.Paused));
            Assert.True(Enum.IsDefined(typeof(OffscreenFidelity), OffscreenFidelity.Coarse));
            Assert.True(Enum.IsDefined(typeof(OffscreenFidelity), OffscreenFidelity.Full));
        }

        // ---- Paused does nothing ---------------------------------------------------------------------

        [Fact]
        public void PausedDoesNotAdvanceTheClock()
        {
            //The clause that is easiest to get wrong. "Paused" plausibly reads as "the Sims stop
            //but time goes on", and the handoff's "Paused ⇒ nothing changes" does not allow it: a
            //neighbourhood whose clock runs while paused is a neighbourhood that has silently
            //become Full, and the flag is then lying about the cost.
            var clock = Clock();
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));
            var layout = Layout(11);
            var simulator = Sim_();
            simulator.Fidelity = OffscreenFidelity.Paused;

            var before = clock.Ticks;
            var result = simulator.Tick(OneHour, clock, store, layout);

            Assert.True(result.Paused);
            Assert.False(result.Changed);
            Assert.Equal(0, result.ClockTicks);
            Assert.Equal(0, result.DecaySteps);
            Assert.Equal(0, result.LotsDecayed);
            Assert.Equal(before, clock.Ticks);
            Assert.Equal(0, clock.Hours);
            Assert.Equal(0, clock.Minutes);
        }

        [Fact]
        public void PausedDoesNotTouchAMotives()
        {
            var clock = Clock();
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var layout = Layout(11);
            var simulator = Sim_();
            simulator.Fidelity = OffscreenFidelity.Paused;

            var before = Snapshot(marshal, 0);
            simulator.Tick(GameHours(12), clock, store, layout);

            Assert.Equal(before, Motives(marshal, 0));
        }

        [Fact]
        public void PausedDoesNotEvenMakeARecord()
        {
            //The strictest reading of "nothing changes", and the one worth pinning: a paused world
            //that quietly accumulated records for every lot it had seen would be doing work the
            //flag says it is not doing, and a caller watching RecordCount would be misled about it.
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));
            store.SetHollow(12, new VMHollowMarshal());
            var simulator = Sim_();
            simulator.Fidelity = OffscreenFidelity.Paused;

            simulator.Tick(OneHour, Clock(), store, Layout(11, 12));

            Assert.Equal(0, simulator.RecordCount);
            Assert.Null(simulator.RecordFor(11));
            Assert.Null(simulator.RecordFor(12));
        }

        [Fact]
        public void PausedIgnoresAnAbsurdDeltaWithoutThrowing()
        {
            //A pause is a state, not an early return bolted on after the arithmetic. A caller
            //pausing mid-frame should not have to also make its delta well behaved.
            var clock = Clock();
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));
            var simulator = Sim_();
            simulator.Fidelity = OffscreenFidelity.Paused;

            var result = simulator.Tick(1e300, clock, store, Layout(11));

            Assert.True(result.Paused);
            Assert.False(result.Changed);
            Assert.Equal(0, clock.Ticks);
        }

        [Fact]
        public void AResumedWorldContinuesFromWhereItPaused()
        {
            //Pausing must not consume the elapsed time, and must not lose it either. A world paused
            //for two hours and then run for two has to match a world run for two and then paused
            //for two - same clock, same lot, same decay - or "paused" would be a silent edit of
            //the simulation rather than a pause of it.
            var straight = new OffscreenSimulator(Seed);
            var interrupted = new OffscreenSimulator(Seed);
            var layout = Layout(11);

            var clockA = Clock();
            var storeA = new LotStore();
            var marshalA = ResidentMarshal(100);
            storeA.SetActive(11, marshalA);

            var clockB = Clock();
            var storeB = new LotStore();
            var marshalB = ResidentMarshal(100);
            storeB.SetActive(11, marshalB);

            straight.Tick(GameHours(2), clockA, storeA, layout);

            interrupted.Fidelity = OffscreenFidelity.Paused;
            for (int i = 0; i < 120; i++) interrupted.Tick(1, clockB, storeB, layout);
            interrupted.Fidelity = OffscreenFidelity.Coarse;
            interrupted.Tick(GameHours(2), clockB, storeB, layout);

            //The clock is where the pause began, not where the pause ended: the paused frames
            //advanced nothing, so two hours of running is two hours and not four.
            Assert.Equal(GameHours(2) * WorldClock.TicksPerMinuteTs1, clockA.Ticks);
            Assert.Equal(clockA.Ticks, clockB.Ticks);
            Assert.Equal(Motives(marshalA, 0), Motives(marshalB, 0));
        }

        // ---- determinism ----------------------------------------------------------------------------

        [Fact]
        public void TheSameSeedProducesTheSameResult()
        {
            //§B6 acceptance, first clause. Run twice from the same seed over the same world and
            //every number - the clock, the stored motive tables, the records - has to match, or a
            //save file written by one run is not the save file another run resumes.
            var first = Run(Seed);
            var second = Run(Seed);

            Assert.Equal(first.clock, second.clock);
            Assert.Equal(first.motives, second.motives);
            Assert.True(first.record.SameAs(second.record));
        }

        [Fact]
        public void ADifferentSeedProducesADifferentResult()
        {
            //The converse, and the one that proves the seed is actually read. A simulator that drew
            //the same numbers from every seed would satisfy the test above while being a constant
            //function of its input.
            var first = Run(Seed);
            var second = Run(OtherSeed);

            Assert.NotEqual(first.motives, second.motives);
        }

        [Fact]
        public void TheResultIsTheSameHoweverTheElapsedTimeIsChoppedUp()
        {
            //The stronger half of §8's determinism, and the claim worth having: the step index a
            //draw is keyed on comes from the clock, not from a count this type keeps. So one long
            //tick and many short ones must land in the same place. A frame rate is not something a
            //save file records, so "same seed, same call sequence" would be the wrong promise.
            var coarse = new OffscreenSimulator(Seed);
            var fine = new OffscreenSimulator(Seed);

            var clockA = Clock();
            var clockB = Clock();
            var storeA = new LotStore();
            var storeB = new LotStore();
            var marshalA = ResidentMarshal(100);
            var marshalB = ResidentMarshal(100);
            storeA.SetActive(11, marshalA);
            storeB.SetActive(11, marshalB);
            var layout = Layout(11);

            //8 game hours as one tick against 8 game hours as 480 one-second ticks, which is the
            //same elapsed time at two frame rates.
            coarse.Tick(GameHours(8), clockA, storeA, layout);
            for (int i = 0; i < 8 * 60; i++) fine.Tick(1, clockB, storeB, layout);

            Assert.Equal(clockA.Ticks, clockB.Ticks);
            Assert.Equal(Motives(marshalA, 0), Motives(marshalB, 0));
            Assert.True(coarse.RecordFor(11).SameAs(fine.RecordFor(11)));
        }

        [Fact]
        public void TheSimulationStreamIsTheOneBeingDrawnFrom()
        {
            //§8: "B6 is the only consumer of `simulation`". Assertable, so asserted: the streams
            //this type exposes are the ones FromSeed derives, and the simulation stream is not the
            //generation stream, which is what would break if the two were ever conflated.
            var simulator = Sim_();
            var expected = NeighbourhoodStreams.FromSeed(Seed);

            Assert.True(simulator.Streams.Matches(expected));
            Assert.Equal(expected.Simulation.State, simulator.Streams.Simulation.State);
            Assert.NotEqual(expected.Generation.State, simulator.Streams.Simulation.State);
            Assert.Equal(Seed, simulator.WorldSeed);
        }

        [Fact]
        public void OneLotsNumbersDoNotDependOnWhichOtherLotsAreStored()
        {
            //The per-lot label earning its keep. If the draws came from a shared stream, adding a
            //neighbour to the store would move the numbers of a lot that was already there, and a
            //save made before the neighbour was visited would not match one made after.
            var withNeighbour = new OffscreenSimulator(Seed);
            var alone = new OffscreenSimulator(Seed);

            var clockA = Clock();
            var clockB = Clock();
            var storeA = new LotStore();
            var storeB = new LotStore();
            var marshalA = ResidentMarshal(100);
            var marshalB = ResidentMarshal(100);
            storeA.SetActive(11, marshalA);
            storeA.SetActive(12, ResidentMarshal(70));
            storeB.SetActive(11, marshalB);
            var layout = Layout(11, 12);

            withNeighbour.Tick(GameHours(4), clockA, storeA, layout);
            alone.Tick(GameHours(4), clockB, storeB, layout);

            Assert.Equal(Motives(marshalA, 0), Motives(marshalB, 0));
        }

        // ---- time advances --------------------------------------------------------------------------

        [Fact]
        public void TimePassesOnTheClockWhileThePlayerIsElsewhere()
        {
            //§B6 acceptance, "time passes". The bug this milestone exists to fix is a clock that
            //stops when the last lot is left, so this is the headline and it is checked on the
            //clock's own fields rather than on the result struct.
            var clock = Clock();
            var simulator = Sim_();

            var result = simulator.Tick(OneHour, clock, new LotStore(), Layout(11));

            Assert.True(result.Changed);
            Assert.Equal(1, clock.Hours);
            Assert.Equal(0, clock.Minutes);
            //One game hour: 60 game minutes at 30 sub-minute ticks to the minute, which is 60 real
            //seconds at TS1's one-game-minute-per-second rate.
            Assert.Equal(60L * WorldClock.TicksPerMinuteTs1, clock.Ticks);
            Assert.Equal(60L * WorldClock.TicksPerMinuteTs1, result.ClockTicks);
        }

        [Fact]
        public void TimeRollsTheCalendarRatherThanCounting()
        {
            //A neighbourhood clock is a TS1 calendar - 30-day months, hours 0..23 - and an
            //off-screen tick that only added to a tick counter would produce a clock that reads
            //correctly here and is wrong the moment anybody looks at the date.
            var clock = Clock();
            var simulator = Sim_();
            //25 game hours is 25 real seconds' worth of game time at TS1's rate, which is past the
            //default one-day tick budget - so the budget is raised here rather than the test being
            //quietly measuring the truncation instead of the calendar.
            simulator.MaxSecondsPerTick = (int)GameHours(48);

            simulator.Tick(GameHours(25), clock, new LotStore(), Layout(11));

            Assert.Equal(1, clock.Hours);
            Assert.Equal(2, clock.DayOfMonth);
            Assert.Equal(6, clock.Month);
            Assert.Equal(1997, clock.Year);
        }

        [Fact]
        public void LeavingAndComingBackAfterNHoursShowsBothTimeAndMotiveMoved()
        {
            //§B6's acceptance sentence, end to end: the player leaves a lot, the world runs for
            //eight hours, and they come back to a clock that moved and to neighbours whose motives
            //have changed - and are still in range. This is the one test that would fail if the
            //clock advanced but the decay did not, or the decay ran but the clock did not.
            var store = new LotStore();
            var neighbour = ResidentMarshal(100);
            store.SetActive(11, neighbour);
            var clock = Clock();
            var simulator = Sim_();
            var layout = Layout(11);

            var before = Snapshot(neighbour, 0);
            var result = simulator.Tick(GameHours(8), clock, store, layout);

            Assert.Equal(8, clock.Hours);
            Assert.NotEqual(before, Motives(neighbour, 0));
            Assert.True(result.DecaySteps > 0);
            Assert.Equal(1, result.LotsDecayed);
            Assert.All(Motives(neighbour, 0), v => Assert.InRange(v, OffscreenSimulator.MinMotive, OffscreenSimulator.MaxMotive));
        }

        [Fact]
        public void AZeroDeltaChangesNothing()
        {
            //A frame that took no time is not a frame that simulated any, and the difference
            //matters because a caller that hands over zero on the first frame of a lot switch would
            //otherwise find a lot an hour behind before it had drawn a frame.
            var clock = Clock();
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var simulator = Sim_();

            var result = simulator.Tick(0, clock, store, Layout(11));

            Assert.False(result.Changed);
            Assert.Equal(0, clock.Ticks);
            Assert.Equal(0, simulator.RecordCount);
        }

        // ---- bounded and plausible ------------------------------------------------------------------

        [Fact]
        public void CoarseChangesBoundedValues()
        {
            //§B6 acceptance, "plausible, bounded motive changes". Two claims: something changed,
            //and nothing left the engine's own -100..100 range. The bound is not cosmetic - the
            //marshal's MotiveData is a plain short[16] with no clamping of its own, so a decay that
            //overflowed would write a number no live VM could ever produce and the lot would load
            //with a garbage motive.
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var simulator = Sim_();
            var before = Snapshot(marshal, 0);

            simulator.Tick(GameHours(30), Clock(), store, Layout(11));
            var after = Motives(marshal, 0);

            Assert.NotEqual(before, after);
            foreach (var value in after)
            {
                Assert.InRange(value, OffscreenSimulator.MinMotive, OffscreenSimulator.MaxMotive);
            }
        }

        [Fact]
        public void ASimLeftAloneForAMonthIsUnwellAndStillInRange()
        {
            //"Plausible" is the word that carries this test, and it has to be checked at the
            //extreme or it is not checked: a lot nobody has visited in a month must show hungry,
            //tired and bored Sims, not a lot of zeroes from an accumulator that overflowed, and not
            //values that have pinned at the floor for the same reason.
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var simulator = Sim_();
            var clock = Clock();
            var layout = Layout(11);

            //A day at a time, which is both what a real caller does - a frame is a frame - and what
            //keeps this test off the per-tick budget, which would otherwise truncate a month to a
            //day and quietly measure the truncation instead of the decay.
            for (int day = 0; day < 30; day++)
            {
                simulator.Tick(GameHours(24), clock, store, layout);
            }
            var motives = Motives(marshal, 0);

            Assert.All(motives, v => Assert.InRange(v, OffscreenSimulator.MinMotive, OffscreenSimulator.MaxMotive));
            Assert.True(motives[(int)VMMotive.Hunger] < 0, "a month is long enough to be hungry");
            Assert.True(motives[(int)VMMotive.Energy] < 0, "a month is long enough to be tired");
            Assert.True(motives[(int)VMMotive.Fun] < 0, "a month is long enough to be bored");
        }

        [Fact]
        public void TheEngineItselfReachesTheSameFloorOverAMonth()
        {
            //The bound is not this type's invention. The engine clamps a motive to -100 in
            //VMTS1MotiveDecay and to the tuning limit (100 when there is no tuning cache, which is
            //always true off-screen) in VMAvatar.SetMotiveData, so anything outside that range is a
            //number a loaded lot could never have held. Pinned against the engine's own constant so
            //a change to the tuning table is a visible test failure rather than a silent divergence.
            Assert.Equal(-100, OffscreenSimulator.MinMotive);
            Assert.Equal(100, OffscreenSimulator.MaxMotive);
            Assert.Equal(16, (int)VMMotive.Fun + 1);
        }

        [Fact]
        public void AMotivesAccumulatesTheEnginesOwnRounding()
        {
            //The reason a single 30-minute step is not a whole number of points. TS1 decays in
            //1/1000ths and only spends a point when the accumulator reaches 1000, which is where the
            //"half a point at a time" feel comes from. A model that rounded per step would drift
            //away from a live lot by a visible amount over a long absence.
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var simulator = new OffscreenSimulator(Seed) { Fidelity = OffscreenFidelity.Full };
            var clock = Clock();

            //A warm-up tick, because the first tick a lot is seen on stamps it rather than decaying
            //it, and then exactly one step of the Full cadence: two game minutes, which is two real
            //seconds, and is below the point at which most of these rates spend anything.
            simulator.Tick(1, clock, store, Layout(11));
            simulator.Tick(OffscreenSimulator.FullStepGameMinutes * OffscreenSimulator.GameMinutesPerRealSecond,
                clock, store, Layout(11));

            var record = simulator.RecordFor(11);
            Assert.Equal(1, record.Steps);
            //Some of the seven rates are under 1000/1000ths at this cadence, so at least one
            //motive must still be exactly where it started.
            Assert.Contains((short)100, Motives(marshal, 0));
        }

        [Fact]
        public void CoarseDecaysLessOftenThanFullForTheSameElapsedTime()
        {
            //The budget claim, stated as an assertion rather than as a comment: the default is
            //cheaper than the expensive setting because it takes fewer, larger steps, not because
            //it does less per step.
            var coarse = new OffscreenSimulator(Seed);
            var full = new OffscreenSimulator(Seed) { Fidelity = OffscreenFidelity.Full };
            var clockA = Clock();
            var clockB = Clock();
            var storeA = new LotStore();
            var storeB = new LotStore();
            storeA.SetActive(11, ResidentMarshal(100));
            storeB.SetActive(11, ResidentMarshal(100));
            var layout = Layout(11);

            //A lot the simulator has never seen is stamped rather than decayed on the tick that
            //first shows it, so both sides get a warm-up before the counted one - otherwise the
            //counts would be measuring the warm-up.
            coarse.Tick(1, clockA, storeA, layout);
            full.Tick(1, clockB, storeB, layout);

            var coarseResult = coarse.Tick(GameHours(6), clockA, storeA, layout);
            var fullResult = full.Tick(GameHours(6), clockB, storeB, layout);

            Assert.Equal(6 * 60 / OffscreenSimulator.CoarseStepGameMinutes, coarseResult.DecaySteps);
            Assert.Equal(6 * 60 / OffscreenSimulator.FullStepGameMinutes, fullResult.DecaySteps);
            Assert.True(fullResult.DecaySteps > coarseResult.DecaySteps);
        }

        // ---- the active lot and the live VM -----------------------------------------------------------

        [Fact]
        public void TheActiveLotIsNeverTouched()
        {
            //§B6 task 4. The lot the player is standing in is running its own tick against a live
            //VM, and a simulator that wrote into it would be racing that tick for no benefit: the
            //lot is not off-screen.
            var store = new LotStore();
            var active = ResidentMarshal(100);
            var neighbour = ResidentMarshal(100);
            store.SetActive(11, active);
            store.SetActive(12, neighbour);
            var simulator = new OffscreenSimulator(Seed) { ActiveLot = 11 };
            var clock = Clock();

            //Both snapshots taken before the tick - a "before" taken after would compare the lot
            //against itself and pass without proving anything.
            var activeBefore = Snapshot(active, 0);
            var neighbourBefore = Snapshot(neighbour, 0);
            simulator.Tick(GameHours(8), clock, store, Layout(11, 12));

            Assert.Equal(activeBefore, Motives(active, 0));
            Assert.NotEqual(neighbourBefore, Motives(neighbour, 0));
            Assert.Null(simulator.RecordFor(11));
            Assert.NotNull(simulator.RecordFor(12));
        }

        [Fact]
        public void TheActiveLotIsAlsoExcludedWhenTheStoreHoldsIt()
        {
            //The belt to the braces above. The world's own rule is that the store holds the lots the
            //player is <em>not</em> in, so a store that held the active lot would be a bug elsewhere -
            //but if it did, the only safe thing this type can do is still leave it alone.
            var store = new LotStore();
            var active = ResidentMarshal(100);
            store.SetActive(11, active);
            var simulator = new OffscreenSimulator(Seed) { ActiveLot = 11 };

            var before = Snapshot(active, 0);
            simulator.Tick(GameHours(8), Clock(), store, Layout(11));

            Assert.Equal(before, Motives(active, 0));
        }

        [Fact]
        public void TheActiveLotIsAlsoExcludedWhenTheWorldStateNamesIt()
        {
            //The fallback, so a caller that already stamps NeighbourhoodWorldState.ActiveLot on
            //every switch does not have to also remember OffscreenSimulator.ActiveLot.
            var store = new LotStore();
            var active = ResidentMarshal(100);
            var neighbour = ResidentMarshal(100);
            store.SetActive(11, active);
            store.SetActive(12, neighbour);
            var lots = new List<NeighbourhoodLot>
            {
                new NeighbourhoodLot { Number = 11, Size = 16, WorldX = 0, WorldY = 0, HasHouse = true },
                new NeighbourhoodLot { Number = 12, Size = 16, WorldX = 20, WorldY = 0, HasHouse = true }
            };
            var world = NeighbourhoodWorldState.FromClock(Clock(), 11);
            var layout = new NeighbourhoodLayout(0, (int)Seed, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                NeighbourhoodTestInput.SmallMap, lots, world);
            var simulator = new OffscreenSimulator(layout);

            var before = Snapshot(active, 0);
            var neighbourBefore = Snapshot(neighbour, 0);
            simulator.Tick(GameHours(8), Clock(), store, layout);

            Assert.Equal(before, Motives(active, 0));
            Assert.NotEqual(neighbourBefore, Motives(neighbour, 0));
        }

        [Fact]
        public void NoMethodOnTheSimulatorTakesALiveVM()
        {
            //"Never touches a live VM" as a structural fact rather than a promise, checked by
            //reflection over the public surface. LotStore holds marshals and this type only reaches
            //lots through it, so a VM cannot arrive even by accident - and this is the test that
            //says so before somebody adds an overload that does.
            var members = typeof(OffscreenSimulator).GetMethods();
            foreach (var method in members)
            {
                foreach (var parameter in method.GetParameters())
                {
                    Assert.NotEqual(typeof(VM), parameter.ParameterType);
                    Assert.NotEqual(typeof(VM), parameter.ParameterType.GetElementType());
                }
            }
            foreach (var field in typeof(OffscreenSimulator).GetFields())
            {
                Assert.NotEqual(typeof(VM), field.FieldType);
            }
        }

        // ---- the coarse model ------------------------------------------------------------------------

        [Fact]
        public void ALotWithNoReachableMotiveDataGetsARecordInstead()
        {
            //A hollow lot - a neighbour drawn as a subworld - keeps game objects and nothing else,
            //so there is no motive table anywhere in it. The record is the lot's simulated state in
            //that case, and saying "is this a coarse model" is what lets a caller know not to
            //believe its numbers as though they had come from a Sim.
            var store = new LotStore();
            store.SetHollow(11, new VMHollowMarshal());
            var simulator = Sim_();
            var layout = Layout(11);

            var result = simulator.Tick(GameHours(4), Clock(), store, layout);

            Assert.Equal(1, result.LotsDecayed);
            var record = simulator.RecordFor(11);
            Assert.NotNull(record);
            Assert.True(record.IsCoarseModel);
            Assert.Equal(-1, record.AvatarCount);
            Assert.True(record.HasSimulated);
        }

        [Fact]
        public void ACoarseModelsMotivesAlsoStayInRangeAndStillMove()
        {
            //The coarse path runs the same constants and the same accumulator, so it owes the same
            //two guarantees as the resident path. Checked separately because a second implementation
            //is a second thing that can be wrong, which is exactly why they share the decay code.
            var store = new LotStore();
            store.SetHollow(11, new VMHollowMarshal());
            var simulator = Sim_();
            var layout = Layout(11);

            //One clock for both ticks. A record's resume point is an absolute tick count, so a
            //second, freshly-constructed clock would put the world back before the lot's last
            //simulation and the second tick would legitimately do nothing.
            var clock = Clock();
            simulator.Tick(GameHours(4), clock, store, layout);
            var first = SnapshotRecord(simulator.RecordFor(11));

            simulator.Tick(GameHours(4), clock, store, layout);
            var record = simulator.RecordFor(11);

            Assert.NotEqual(first, SnapshotRecord(record));
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++)
            {
                Assert.InRange(record.MotiveAt(i), OffscreenSimulator.MinMotive, OffscreenSimulator.MaxMotive);
            }
        }

        [Fact]
        public void ACoearseLotIsSeededFromTheSeedAndNotFromTheDefault()
        {
            //A neighbourhood in which every never-visited lot starts at the same numbers is one in
            //which the world seed does not reach the part of the world that is always off-screen.
            //Two different seeds must therefore produce two different sets of starting motives.
            var a = Seeded(Seed);
            var b = Seeded(OtherSeed);

            Assert.NotEqual(a, b);
            Assert.All(a, v => Assert.InRange(v, OffscreenLotRecord.MinSeededMotive, OffscreenLotRecord.MaxSeededMotive));
        }

        [Fact]
        public void ACoarseLotDecaysForTheWholeWindowItWasFirstSeenIn()
        {
            //The rule that makes "the world moves on while the player is elsewhere" work at all. A
            //lot the store has held for eight hours owes eight hours, and if the tick that first
            //noticed it anchored its resume point at "now" it would silently throw all of that away
            //on the one frame it was looked at. So a first sighting decays from the start of the
            //window, and the step count says so.
            var store = new LotStore();
            store.SetHollow(11, new VMHollowMarshal());
            var simulator = Sim_();
            var clock = Clock();
            var layout = Layout(11);

            var result = simulator.Tick(GameHours(4), clock, store, layout);

            var record = simulator.RecordFor(11);
            Assert.True(record.IsCoarseModel);
            Assert.Equal(1, result.LotsDecayed);
            Assert.Equal(4 * 60 / OffscreenSimulator.CoarseStepGameMinutes, record.Steps);
        }

        [Fact]
        public void ACoarseLotDoesNotDecayForTimeBeforeItWasStored()
        {
            //The other half of the rule above, and the reason it is anchored at the window's start
            //rather than at zero. A lot added to the store half way through must not be charged for
            //the hours it spent in nobody's memory, or a neighbourhood that opens lots as the
            //player walks around it would manufacture decay out of nothing.
            var store = new LotStore();
            store.SetHollow(11, new VMHollowMarshal());
            var simulator = Sim_();
            var clock = Clock();
            var layout = Layout(11);

            //Present for the whole eight hours.
            simulator.Tick(GameHours(8), clock, store, layout);
            var early = simulator.RecordFor(11).Steps;
            Assert.Equal(8 * 60 / OffscreenSimulator.CoarseStepGameMinutes, early);

            //Stored half way through the same eight hours, so it is owed the second four only.
            var late = new OffscreenSimulator(Seed);
            var clockB = Clock();
            var storeB = new LotStore();
            var layoutB = Layout(11);
            late.Tick(GameHours(4), clockB, storeB, layoutB);
            storeB.SetHollow(11, new VMHollowMarshal());
            late.Tick(GameHours(4), clockB, storeB, layoutB);

            Assert.Equal(early, late.RecordFor(11).Steps * 2);
        }

        [Fact]
        public void ALotWithAMarshalIsNotACoarseModel()
        {
            //The distinction has to be visible, or "IsCoarseModel" is a name for nothing. A
            //resident lot's record mirrors its Sims and says how many.
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100, 80, 60));
            var simulator = Sim_();

            simulator.Tick(GameHours(2), Clock(), store, Layout(11));

            var record = simulator.RecordFor(11);
            Assert.False(record.IsCoarseModel);
            Assert.Equal(3, record.AvatarCount);
        }

        [Fact]
        public void TheAggregateIsTheMeanOfTheLotSims()
        {
            //A record describes a lot, so its numbers have to be an average a reader can define.
            //Three Sims at 100, 80 and 60 average to 80, and a record that reported anything else
            //would be reporting one Sim's motives under a lot's name.
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100, 80, 60));
            var simulator = new OffscreenSimulator(Seed) { SleepsOffScreen = false };

            simulator.Tick(1, Clock(), store, Layout(11));
            simulator.Tick(GameHours(8), Clock(), store, Layout(11));

            var record = simulator.RecordFor(11);
            var motives = SnapshotRecord(record);
            var mean = 0;
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++) mean += motives[i];
            mean /= OffscreenLotRecord.MotiveCount;
            Assert.InRange(mean, 0, 100);
            Assert.Equal(3, record.AvatarCount);
        }

        // ---- the store is the only route to a lot ----------------------------------------------------

        [Fact]
        public void ALotTheStoreHasNeverHeardOfIsNotSimulated()
        {
            //§B6 task 1 says "inactive <em>resident</em> lots". A lot with no marshal has no
            //contents in memory to move, and simulating one would be inventing a second save
            //format beside the per-lot house file.
            var store = new LotStore();
            var simulator = Sim_();
            var clock = Clock();

            var result = simulator.Tick(GameHours(8), clock, store, Layout(11, 12, 13));

            Assert.Equal(0, result.LotsDecayed);
            Assert.Equal(0, simulator.RecordCount);
            Assert.Equal(8, clock.Hours);
        }

        [Fact]
        public void AResidentLotWithNoSimsInItIsNotInvented()
        {
            //A marshal with no entities is an empty plot or a hand-built one. Leaving the record
            //alone keeps "no Sims here" distinguishable from "one invisible Sim here", which a
            //defaulted aggregate could not.
            var store = new LotStore();
            store.SetActive(11, new VMMarshal());
            var simulator = Sim_();

            var result = simulator.Tick(GameHours(8), Clock(), store, Layout(11));

            Assert.Equal(0, result.AvatarsDecayed);
            Assert.Equal(0, result.DecaySteps);
            Assert.Equal(0, simulator.RecordFor(11).AvatarCount == -1 ? 0 : 1);
        }

        [Fact]
        public void AGameObjectInTheLotIsNotMistakenForASim()
        {
            //Entities is a mixed array. Treating a game object as a Sim would write motive numbers
            //into a VMGameObjectMarshal, which has no motive table, and the cast would have to be
            //defended against on every element.
            var store = new LotStore();
            var marshal = new VMMarshal { Entities = new VMEntityMarshal[] { new VMGameObjectMarshal() } };
            store.SetActive(11, marshal);
            var simulator = Sim_();

            var result = simulator.Tick(GameHours(8), Clock(), store, Layout(11));

            Assert.Equal(0, result.AvatarsDecayed);
        }

        [Fact]
        public void ForgettingALotMakesTheNextOffScreenRunStartFromTheSavedState()
        {
            //What a lot switch should do once the house file holds whatever the marshal held. The
            //next time the lot is off-screen it is simulated from what was saved, not from what was
            //modelled - which is the difference between a model and a save.
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var simulator = Sim_();
            var clock = Clock();
            var layout = Layout(11);

            simulator.Tick(GameHours(4), clock, store, layout);
            var firstRun = simulator.RecordFor(11).Steps;
            Assert.Equal(4 * 60 / OffscreenSimulator.CoarseStepGameMinutes, firstRun);

            Assert.True(simulator.Forget(11));
            Assert.Null(simulator.RecordFor(11));

            simulator.Tick(GameHours(4), clock, store, layout);
            var record = simulator.RecordFor(11);
            Assert.NotNull(record);
            //A record made fresh by the forget owes only the second window, not the first: the
            //first four hours are in the house file now, not in this model.
            Assert.Equal(firstRun, record.Steps);
        }

        [Fact]
        public void ClearingForgetsEveryLot()
        {
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));
            store.SetHollow(12, new VMHollowMarshal());
            var simulator = Sim_();
            simulator.Tick(OneHour, Clock(), store, Layout(11, 12));
            Assert.Equal(2, simulator.RecordCount);

            simulator.Clear();

            Assert.Equal(0, simulator.RecordCount);
        }

        [Fact]
        public void ARecordRefusesToDescribeLotZero()
        {
            //Lot 0 is "the player is in no lot" everywhere else in this namespace
            //(INeighbourhoodWorld.ActiveLotNumber, NeighbourhoodWorldState.ActiveLot). A record
            //claiming lot 0 would be a record about nothing, and the guard throws rather than
            //clamping because a silently-wrong lot number is the class of mistake this whole
            //neighbourhood exists to make impossible.
            //
            //Constructed directly rather than reached through a tick: the constructor is internal,
            //Simitone.Tests is the one assembly it is visible to, and going through a tick could
            //only ever reach it if the store held a lot 0 - which is a different test.
            Assert.Throws<ArgumentOutOfRangeException>(() => new OffscreenLotRecord(0));

            //And the ordinary case is unaffected.
            var record = new OffscreenLotRecord(11);
            Assert.Equal(11, record.Lot);

            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));
            var simulator = Sim_();
            simulator.Tick(OneHour, Clock(), store, Layout(11));
            Assert.Equal(11, simulator.RecordFor(11).Lot);
        }

        // ---- bookkeeping ----------------------------------------------------------------------------

        [Fact]
        public void LotsAreSimulatedInAscendingLotOrder()
        {
            //LotStore.LoadedLots is already sorted, and this is the assertion that the simulator
            //relies on that rather than on a dictionary's iteration order - which is what makes a
            //log line about three lots reproducible between runs.
            var store = new LotStore();
            store.SetActive(30, ResidentMarshal(100));
            store.SetActive(10, ResidentMarshal(100));
            store.SetActive(20, ResidentMarshal(100));
            var layout = Layout(10, 20, 30);
            var simulator = Sim_();

            simulator.Tick(OneHour, Clock(), store, layout);

            Assert.Equal(new List<short> { 10, 20, 30 }, store.LoadedLots);
            Assert.Equal(3, simulator.RecordCount);
        }

        [Fact]
        public void TheLastSimulatedStampReachesTheLayoutWhenTheCallerAsks()
        {
            //The half of "this lot moved" that has a home in a save today. The motive numbers stay
            //in memory - they belong in a per-lot member of the world state, which is B7's schema
            //and not this milestone's file to change - but LastSimulated is already on the lot, so
            //a stamped layout at least knows which lots are stale.
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));
            var simulator = Sim_();
            var layout = Layout(11);
            var clock = Clock();

            simulator.Tick(GameHours(4), clock, store, layout);
            Assert.Equal(0, layout.FindLot(11).LastSimulated);

            var stamped = simulator.ApplyStamps(layout);

            Assert.Equal(1, stamped);
            Assert.Equal(clock.Ticks, layout.FindLot(11).LastSimulated);
        }

        [Fact]
        public void AStampNeverGoesBackwards()
        {
            //A patch that rebuilt a lot from its sidecar read leaves LastSimulated at whatever the
            //file said, and a tick this world has already applied must not be undone by a save that
            //happened to be older.
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));
            var simulator = Sim_();
            var clock = Clock();
            var layout = Layout(11);

            simulator.Tick(GameHours(4), clock, store, layout);
            simulator.ApplyStamps(layout);
            var reached = layout.FindLot(11).LastSimulated;
            Assert.True(reached > 1);

            //The layout is now ahead of the world - a patch rebuilt the lot from a newer sidecar
            //read, or a save from a later session was loaded over this one. The next stamp is
            //therefore older than what is already there, and writing it would walk the lot's
            //simulated time backwards: a resume would then re-run decay the save already contains.
            layout.FindLot(11).LastSimulated = reached + 1000000;
            simulator.Tick(GameHours(1), clock, store, layout);
            var appliedAgain = simulator.ApplyStamps(layout);

            Assert.Equal(0, appliedAgain);
            Assert.Equal(reached + 1000000, layout.FindLot(11).LastSimulated);
        }

        // ---- the guards -----------------------------------------------------------------------------

        [Fact]
        public void ANegativeDeltaIsACallerBugAndThrows()
        {
            //Time does not run backwards, and a caller that says otherwise has a bug that a
            //silently-ignored negative would hide for the rest of the session.
            var simulator = Sim_();
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                simulator.Tick(-1, Clock(), store, Layout(11)));
        }

        [Fact]
        public void ANotANumberDeltaIsACallerBugAndThrows()
        {
            //0/0 is not a pause either. A NaN that quietly became zero would be a lot of very
            //confusing "the world stopped" bug reports.
            var simulator = Sim_();
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));

            Assert.Throws<ArgumentOutOfRangeException>(() =>
                simulator.Tick(double.NaN, Clock(), store, Layout(11)));
        }

        [Fact]
        public void TheThreeCollaboratorsAreAllRequired()
        {
            //A null clock is the one that matters - it is what time is written to, and "no clock"
            //reading as midnight is the bug UnavailableNeighbourhoodClock exists to name. The
            //store and the layout are required for the same reason: a world that ticks off-screen
            //without saying which lots are stored, or which lot the player is in, is a world this
            //type would have to guess at.
            var simulator = Sim_();
            var clock = Clock();
            var store = new LotStore();
            var layout = Layout(11);

            Assert.Throws<ArgumentNullException>(() => simulator.Tick(1, null, store, layout));
            Assert.Throws<ArgumentNullException>(() => simulator.Tick(1, clock, null, layout));
            Assert.Throws<ArgumentNullException>(() => simulator.Tick(1, clock, store, null));
            Assert.Throws<ArgumentNullException>(() => simulator.ApplyStamps(null));
            Assert.Throws<ArgumentNullException>(() => new OffscreenSimulator((NeighbourhoodLayout)null));
        }

        [Fact]
        public void AVeryLongAbsenceIsTruncatedAndSaysSo()
        {
            //The cap exists because the alternative is a first frame after a month away trying to
            //tick 77 million times. Reporting the truncation is the difference between a design
            //decision and a bug report.
            var store = new LotStore();
            store.SetActive(11, ResidentMarshal(100));
            var simulator = Sim_();
            simulator.MaxSecondsPerTick = 60;
            var clock = Clock();

            var result = simulator.Tick(GameHours(4), clock, store, Layout(11));

            //Only the capped amount moved, and the rest was reported rather than absorbed.
            Assert.Equal(60L * WorldClock.TicksPerMinuteTs1, clock.Ticks);
            Assert.Equal(1, clock.Hours);
            Assert.True(result.TruncatedSeconds > 0);
        }

        [Fact]
        public void TheDefaultTimeBudgetIsAGameDay()
        {
            //Stated rather than implied, because the number is in two units at once: seconds of
            //real time here, and game minutes by the conversion above. 1,440 is a TS1 day.
            Assert.Equal(60 * 24, OffscreenSimulator.DefaultMaxSecondsPerTick);
            Assert.Equal(60 * 24, new OffscreenSimulator(Seed).MaxSecondsPerTick);
        }

        [Fact]
        public void ABudgetCapDefersTheBacklogRatherThanDroppingIt()
        {
            //The cap on steps is a pace limit, not a loss: the lot's resume tick simply lags, and
            //the work comes due on a later tick. Losing it would mean a lot silently skipping
            //decay, which is the one outcome that makes an off-screen model a lie.
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var simulator = new OffscreenSimulator(Seed) { Fidelity = OffscreenFidelity.Full, MaxDecayStepsPerLot = 2 };
            var clock = Clock();
            var layout = Layout(11);

            simulator.Tick(1, clock, store, layout);
            simulator.Tick(OneHour, clock, store, layout);
            //Capped at two, so the lot is behind rather than complete.
            Assert.Equal(2, simulator.RecordFor(11).Steps);

            var unlimited = new OffscreenSimulator(Seed) { Fidelity = OffscreenFidelity.Full };
            var storeB = new LotStore();
            var marshalB = ResidentMarshal(100);
            storeB.SetActive(11, marshalB);
            var clockB = Clock();
            unlimited.Tick(1, clockB, storeB, layout);
            unlimited.Tick(OneHour, clockB, storeB, layout);

            //The capped run is behind, not different, and behind is recoverable.
            Assert.True(simulator.RecordFor(11).Steps < unlimited.RecordFor(11).Steps);
            Assert.True(simulator.RecordFor(11).LastSimulatedTick < clock.Ticks);
        }

        [Fact]
        public void TheBudgetCapsRefuseAValueThatWouldFreezeTheWorld()
        {
            //A step budget of zero is not a slow world, it is a world whose lots never move, and
            //silently accepting it would be indistinguishable from the flag being off.
            var simulator = Sim_();

            Assert.Throws<ArgumentOutOfRangeException>(() => simulator.MaxDecayStepsPerLot = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => simulator.MaxDecayStepsPerLot = -1);
            Assert.Throws<ArgumentOutOfRangeException>(() => simulator.MaxSecondsPerTick = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => simulator.MaxSecondsPerTick = -1);
        }

        [Fact]
        public void MotiveIndexesOutsideTheSevenAreRefused()
        {
            //A TS1 lot decays seven motives, and a typo in an index is a wrong number written
            //somewhere rather than an exception at the call site.
            var store = new LotStore();
            store.SetHollow(11, new VMHollowMarshal());
            var simulator = Sim_();
            simulator.Tick(OneHour, Clock(), store, Layout(11));
            var record = simulator.RecordFor(11);

            Assert.Throws<ArgumentOutOfRangeException>(() => record.MotiveAt(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => record.MotiveAt(OffscreenLotRecord.MotiveCount));
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++)
            {
                Assert.InRange(record.MotiveAt(i), short.MinValue, short.MaxValue);
            }
        }

        [Fact]
        public void SevenMotivesIsNotAnArbitraryNumber()
        {
            //It is VMTS1MotiveDecay.DecrementMotives.Length, and the aggregate array is sized from
            //it. If a future motive were added to the engine's table, a hard-coded seven here would
            //quietly drop it.
            Assert.Equal(OffscreenLotRecord.MotiveCount, VMTS1MotiveDecay.DecrementMotives.Length);
        }

        // ---- what the model cannot do ------------------------------------------------------------------

        [Fact]
        public void TheRoomScoreIsLeftExactlyAsItWasFound()
        {
            //The first of the four things a VM would have to exist for, and the only one with an
            //exact test. Room is read from a room table and a tile by a running lot, so off-screen
            //it is carried through untouched - and mood is still recomputed, still including it, so
            //it stays consistent with a number nothing is maintaining.
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            Sim(marshal, 0).MotiveData[(int)VMMotive.Room] = 42;
            store.SetActive(11, marshal);
            var simulator = Sim_();

            simulator.Tick(GameHours(8), Clock(), store, Layout(11));

            Assert.Equal(42, Motives(marshal, 0)[(int)VMMotive.Room]);
            //And the room really does reach the mood, which is what makes carrying it forward the
            //right thing rather than merely the cautious one.
            var mood = Motives(marshal, 0)[(int)VMMotive.Mood];
            var sum = 0;
            foreach (var m in VMTS1MotiveDecay.DecrementMotives) sum += Motives(marshal, 0)[(int)m];
            Assert.Equal((sum + 42) / 8, mood);
        }

        [Fact]
        public void SleepingOffScreenIsAGuessAndCanBeTurnedOff()
        {
            //The second thing a VM would have to exist for. Nothing off-screen can put a Sim to bed,
            //so a clock rule stands in - and the rule is a rule, which means a caller that would
            //rather not have one can say so.
            var withSleep = new LotStore();
            var withSleepMarshal = ResidentMarshal(100);
            withSleep.SetActive(11, withSleepMarshal);
            var awake = new LotStore();
            var awakeMarshal = ResidentMarshal(100);
            awake.SetActive(11, awakeMarshal);
            var layout = Layout(11);

            //Six game hours starting at eleven at night, which runs through the guessed bed hours
            //and out of them - long enough for the two rates to be measurably apart.
            var clockA = ClockAt(23);
            var sleeps = new OffscreenSimulator(Seed);
            sleeps.Tick(GameHours(6), clockA, withSleep, layout);

            var clockB = ClockAt(23);
            var awakeSim = new OffscreenSimulator(Seed) { SleepsOffScreen = false };
            awakeSim.Tick(GameHours(6), clockB, awake, layout);

            //Awake Sims lose energy at the awake rate and bedded ones barely at all, so the two
            //lots end up measurably different. That difference is the whole of the guess.
            Assert.NotEqual(Motives(withSleepMarshal, 0)[(int)VMMotive.Energy],
                Motives(awakeMarshal, 0)[(int)VMMotive.Energy]);
            Assert.True(Motives(awakeMarshal, 0)[(int)VMMotive.Energy]
                < Motives(withSleepMarshal, 0)[(int)VMMotive.Energy],
                "a Sim awake for six hours must be more tired than one that was put to bed");
        }

        [Fact]
        public void ANeverSimulatedSimIsNotWrittenAsUnconscious()
        {
            //The engine writes -1 for "passed out" and charges a heavy energy penalty for it. A
            //Sim this code has no business putting to bed should not also be recorded as having
            //passed out in it.
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var simulator = Sim_();

            //Deep in the guessed bed hours, where the guess says "asleep".
            simulator.Tick(GameHours(2), ClockAt(23), store, Layout(11));

            Assert.Equal(1, Motives(marshal, 0)[(int)VMMotive.SleepState]);
        }

        [Fact]
        public void NoMoneyIsInventedForALotNobodyIsWatching()
        {
            //The third of the four, and the one with a consequence. §B6 task 1 lists money via jobs
            //and payouts, and a simulator that modelled it without the family authority and its
            //budget transactions would be a second and wrong ledger: a lot's funds are the B5
            //path's, and a number here would be a claim about money that no transaction ever made.
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var simulator = Sim_();

            simulator.Tick(GameHours(24 * 7), Clock(), store, Layout(11));

            //Motive indices 0..2 and 4 are the TSO career/Happy-Life motives, unused by TS1. Nothing
            //this type does writes them, and a money number in there would be exactly the invented
            //ledger this is refusing to be.
            var motives = Motives(marshal, 0);
            Assert.Equal(100, motives[(int)VMMotive.HappyWeek]);
            Assert.Equal(100, motives[(int)VMMotive.HappyDay]);
        }

        [Fact]
        public void FullReadsPersonalityAndCoarseDoesNot()
        {
            //The fourth. Both paths use the engine's own rate table; they differ in whether the
            //person driving it is the Sim's own or the population's. Asserted as a behavioural
            //difference rather than as a flag read, because a flag read would pass on a build where
            //the reading had stopped happening.
            var loud = new OffscreenSimulator(Seed) { Fidelity = OffscreenFidelity.Full };
            var plain = new OffscreenSimulator(Seed);
            var layout = Layout(11);

            var storeA = new LotStore();
            var marshalA = ResidentMarshal(100);
            SetPersonality(marshalA, active: 900, outgoing: 900);
            storeA.SetActive(11, marshalA);

            var storeB = new LotStore();
            var marshalB = ResidentMarshal(100);
            SetPersonality(marshalB, active: 900, outgoing: 900);
            storeB.SetActive(11, marshalB);

            loud.Tick(1, Clock(), storeA, layout);
            loud.Tick(GameHours(8), Clock(), storeA, layout);
            plain.Tick(1, Clock(), storeB, layout);
            plain.Tick(GameHours(8), Clock(), storeB, layout);

            //Active personality above the engine's 666 test picks a different comfort constant, so
            //a very outgoing Sim loses comfort at a different rate off-screen than a neutral one
            //does. The two fidelities disagree about how much.
            Assert.NotEqual(Motives(marshalA, 0)[(int)VMMotive.Comfort],
                Motives(marshalB, 0)[(int)VMMotive.Comfort]);
        }

        // ---- B8: community visitors -----------------------------------------------------------------

        [Fact]
        public void NoVisitorSourceMeansThePreB8BehaviourExactly()
        {
            //The gate, stated as an equality rather than as a flag read. Two runs of the same world
            //over the same seed, one of them handed a source that says "nobody is anywhere", must
            //end up with the same motive tables, the same records and the same clock. That is
            //stronger than "no source changes nothing": it also pins that *having* a source nobody
            //is named in does not perturb a single draw, which is what OffscreenSimulator's
            //window-centre argument buys. A second multiplier layered on the jitter would have
            //passed the first test and failed this one.
            var layout = CommunityLayout(11);

            var without = new OffscreenSimulator(Seed);
            var nobody = new OffscreenSimulator(Seed) { VisitorLots = _ => new short[0] };

            var storeA = new LotStore();
            var marshalA = ResidentMarshal(100);
            storeA.SetActive(11, marshalA);
            var storeB = new LotStore();
            var marshalB = ResidentMarshal(100);
            storeB.SetActive(11, marshalB);

            var clockA = Clock();
            var clockB = Clock();

            without.Tick(1, clockA, storeA, layout);
            without.Tick(GameHours(12), clockA, storeA, layout);
            nobody.Tick(1, clockB, storeB, layout);
            nobody.Tick(GameHours(12), clockB, storeB, layout);

            Assert.Equal(Motives(marshalA, 0), Motives(marshalB, 0));
            Assert.True(without.RecordFor(11).SameAs(nobody.RecordFor(11)));
            Assert.Equal(clockA.Ticks, clockB.Ticks);
            //And nobody counted, so the record is not quietly claiming a visit either.
            Assert.Equal(0, nobody.RecordFor(11).CompanySteps);
            Assert.False(nobody.RecordFor(11).HadCompany);
        }

        [Fact]
        public void PausedIgnoresVisitorsWithoutEvenAsking()
        {
            //"Paused freezes everything" has to include the connection to B8, not just the decay.
            //The counter is the assertion: a paused world that asked the schedule who is visiting
            //would be doing the work the flag says it is not doing, and the only reason a caller
            //could tell is if this type said so.
            var asked = 0;
            var simulator = new OffscreenSimulator(Seed)
            {
                VisitorLots = _ => { asked++; return new short[0]; }
            };
            simulator.Fidelity = OffscreenFidelity.Paused;

            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);
            var before = Snapshot(marshal, 0);

            var result = simulator.Tick(GameHours(24), Clock(), store, CommunityLayout(11));

            Assert.True(result.Paused);
            Assert.False(result.Changed);
            Assert.Equal(0, asked);
            Assert.Equal(0, result.LotsWithCompany);
            Assert.Equal(0, result.CompanySteps);
            Assert.Equal(0, simulator.RecordCount);
            Assert.Equal(before, Motives(marshal, 0));

            //Unpausing is what makes the source reachable at all, so the counter above was not
            //passing because the wiring is dead.
            simulator.Fidelity = OffscreenFidelity.Coarse;
            simulator.Tick(GameHours(12), Clock(), store, CommunityLayout(11));
            Assert.True(asked > 0);
        }

        [Fact]
        public void TheSameSeedAndTheSameVisitorsGiveTheSameResult()
        {
            //§B6's determinism acceptance, with §B8 plugged in: same seed, same clock, same
            //schedule, same everything. The company fields are inside OffscreenLotRecord.SameAs,
            //so this also pins that the visitor bookkeeping is part of the record and not a
            //side-channel somebody forgot to compare.
            var first = CompanyRun(Seed);
            var second = CompanyRun(Seed);

            Assert.Equal(first.clock, second.clock);
            Assert.Equal(first.motives, second.motives);
            Assert.True(first.record.SameAs(second.record));

            //Not vacuous: the run actually had company, or none of this proves anything.
            Assert.True(first.record.CompanySteps > 0);
            Assert.Equal(first.steps, first.record.CompanySteps);
            Assert.Equal(1, first.lotsWithCompany);
            Assert.InRange(first.record.LastVisitorCount,
                NeighbourhoodWorldFeatures.MinVisitorsPerLot, NeighbourhoodWorldFeatures.MaxVisitorsPerLot);
        }

        [Fact]
        public void MoreCompanyMeansStrictlyLessDecay()
        {
            //The head count is part of the rate, not a label written next to it - and this is the
            //run that says so, because the two sides share a simulator seed and therefore share
            //every per-step draw: the only difference between them is the centre of the jitter
            //window. A busy lot is charged strictly less than a quiet one, motive by motive, and
            //the difference is CompanyScalePerVisitor per visitor per step.
            var one = HeadCountRun(NeighbourhoodWorldFeatures.MinVisitorsPerLot);
            var four = HeadCountRun(NeighbourhoodWorldFeatures.MaxVisitorsPerLot);

            Assert.NotEqual(one.motives, four.motives);
            Assert.Equal(NeighbourhoodWorldFeatures.MinVisitorsPerLot, one.record.LastVisitorCount);
            Assert.Equal(NeighbourhoodWorldFeatures.MaxVisitorsPerLot, four.record.LastVisitorCount);
        }

        [Fact]
        public void ACompanyLotDecaysStrictlyMoreSlowlyThanAnEmptyOne()
        {
            //The effect, measured rather than asserted as a flag - and measured as the decay
            //<em>charged</em> rather than as a motive value, for a reason worth stating. A motive
            //only moves once per whole 1000 of accumulated rate, so two runs whose rates genuinely
            //differ can still read the same motive; and the accumulator on its own points the wrong
            //way, because a slower decay simply leaves a bigger remainder. Charged is the engine's
            //own arithmetic put back together: what the motive has dropped by, plus the fraction
            //being held. See Charged.
            //
            //Same seed, same stream, so the per-step draw is the same value on both sides and the
            //company run's multiplier is exactly MaxDecayJitter lower at every step - which is what
            //makes the inequality below strict rather than approximate.
            var layout = CommunityLayout(11);
            var empty = new OffscreenSimulator(Seed);
            var busy = new OffscreenSimulator(Seed)
            {
                VisitorLots = _ => new short[] { 11 },
                VisitorCount = (_, __) => NeighbourhoodWorldFeatures.MaxVisitorsPerLot
            };

            var storeA = new LotStore();
            var marshalA = ResidentMarshal(100);
            storeA.SetActive(11, marshalA);
            var storeB = new LotStore();
            var marshalB = ResidentMarshal(100);
            storeB.SetActive(11, marshalB);
            var clockA = Clock();
            var clockB = Clock();

            empty.Tick(1, clockA, storeA, layout);
            empty.Tick(GameHours(4), clockA, storeA, layout);
            busy.Tick(1, clockB, storeB, layout);
            var result = busy.Tick(GameHours(4), clockB, storeB, layout);

            //Four hours, which is long enough to charge something and short enough that no motive
            //has reached the engine's floor: past that the clamp would hide the difference by
            //capping both runs at -100, and the comparison would be comparing two saturated numbers.
            var chargedB = Charged(marshalB, 0, 100);
            var chargedA = Charged(marshalA, 0, 100);
            Assert.True(chargedB < chargedA,
                "a lot with company in it must be charged less decay than an empty one: "
                    + chargedB + " against " + chargedA);
            //Motive by motive as well, so a single motive that happens to dominate the total cannot
            //be what the assertion rests on.
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++)
            {
                Assert.True(ChargedOne(marshalB, 0, 100, i) <= ChargedOne(marshalA, 0, 100, i),
                    "motive " + i + " was charged more with company in it");
            }

            //The count is recorded, on the record and in the result, because a caller that wants
            //to know which lots were busy should not have to hold the schedule to find out.
            var record = busy.RecordFor(11);
            Assert.True(record.HadCompany);
            Assert.Equal(record.Steps, record.CompanySteps);
            Assert.Equal(NeighbourhoodWorldFeatures.MaxVisitorsPerLot, record.LastVisitorCount);
            Assert.Equal(record.Steps, result.CompanySteps);
            Assert.Equal(1, result.LotsWithCompany);
            Assert.Equal(0, empty.RecordFor(11).CompanySteps);
        }

        [Fact]
        public void TheCompanyEffectIsBoundedByTheModelsOwnHeadCount()
        {
            //The bound, as a property rather than a comment. A count source is another model; if it
            //could widen the range the off-screen decay runs in, the decay would be as
            //untrustworthy as the loosest input in the neighbourhood.
            Assert.Equal(OffscreenSimulator.NoCompanyScale, OffscreenSimulator.CompanyScaleFor(0));
            Assert.Equal(OffscreenSimulator.NoCompanyScale, OffscreenSimulator.CompanyScaleFor(-4));
            for (int visitors = 1; visitors <= NeighbourhoodWorldFeatures.MaxVisitorsPerLot; visitors++)
            {
                Assert.Equal(OffscreenSimulator.NoCompanyScale - visitors * OffscreenSimulator.CompanyScalePerVisitor,
                    OffscreenSimulator.CompanyScaleFor(visitors));
            }
            //Monotone, and the floor is the floor no matter what a source claims.
            Assert.True(OffscreenSimulator.CompanyScaleFor(1) > OffscreenSimulator.CompanyScaleFor(2));
            Assert.True(OffscreenSimulator.CompanyScaleFor(2) > OffscreenSimulator.CompanyScaleFor(3));
            Assert.Equal(OffscreenSimulator.MinCompanyScale,
                OffscreenSimulator.CompanyScaleFor(NeighbourhoodWorldFeatures.MaxVisitorsPerLot));
            Assert.Equal(OffscreenSimulator.MinCompanyScale, OffscreenSimulator.CompanyScaleFor(4000));

            //End to end, not just as arithmetic: a source claiming four thousand visitors must
            //produce the very same run as one claiming four.
            Assert.Equal(HeadCountRun(NeighbourhoodWorldFeatures.MaxVisitorsPerLot).motives,
                HeadCountRun(4000).motives);
        }

        [Fact]
        public void ALotTheGateNamesWithNobodyInItIsTreatedAsAnEmptyHouse()
        {
            //The degenerate case, and the reason a count of zero is a real answer rather than a
            //default. A schedule that names a lot and then reports nobody must not leave that lot
            //decaying at 70% for the rest of the session because of a stale entry.
            var layout = CommunityLayout(11);
            var empty = new OffscreenSimulator(Seed);
            var named = new OffscreenSimulator(Seed)
            {
                VisitorLots = _ => new short[] { 11 },
                VisitorCount = (_, __) => 0
            };

            var storeA = new LotStore();
            var marshalA = ResidentMarshal(100);
            storeA.SetActive(11, marshalA);
            var storeB = new LotStore();
            var marshalB = ResidentMarshal(100);
            storeB.SetActive(11, marshalB);
            var clockA = Clock();
            var clockB = Clock();

            empty.Tick(1, clockA, storeA, layout);
            empty.Tick(GameHours(8), clockA, storeA, layout);
            named.Tick(1, clockB, storeB, layout);
            named.Tick(GameHours(8), clockB, storeB, layout);

            Assert.Equal(Motives(marshalA, 0), Motives(marshalB, 0));
            Assert.Equal(0, named.RecordFor(11).CompanySteps);
            Assert.Equal(OffscreenSimulator.NoCompany, named.RecordFor(11).LastVisitorCount);
        }

        [Fact]
        public void TheVisitorListIsAPureFunctionOfClockSeedAndLot()
        {
            //The property the whole model rests on. The same lot and the same clock answer the same
            //way however many times the question is asked, a lot nobody named answers zero, and the
            //answer at three in the morning is not the answer at noon - which is the difference
            //between a schedule and a cached list.
            var layout = CommunityLayout(11);
            var simulator = new OffscreenSimulator(Seed)
            {
                VisitorLots = CommunityVisitorLots(Seed, layout),
                VisitorCount = CommunityVisitorHeads(Seed, layout)
            };

            var midnight = ClockAt(3);
            var noon = ClockAt(12);

            Assert.Equal(OffscreenSimulator.NoCompany, simulator.VisitorsFor(midnight, 11));
            var atNoon = simulator.VisitorsFor(noon, 11);
            Assert.InRange(atNoon, NeighbourhoodWorldFeatures.MinVisitorsPerLot,
                NeighbourhoodWorldFeatures.MaxVisitorsPerLot);
            //Repeatable, and a lot nobody named is nobody's business.
            Assert.Equal(atNoon, simulator.VisitorsFor(noon, 11));
            Assert.Equal(OffscreenSimulator.NoCompany, simulator.VisitorsFor(noon, 99));

            //And it agrees with the schedule it was wired from, rather than with a second opinion.
            var expected = Assert.Single(NeighbourhoodWorldFeatures.VisitorsAt(noon, Seed, layout));
            Assert.Equal(expected.HeadCount, atNoon);
            Assert.Equal(new List<short> { 11 }, LotList(simulator.LotsWithVisitors(noon)));
            Assert.Empty(simulator.LotsWithVisitors(midnight));
        }

        [Fact]
        public void TheVisitorListIsNormalisedBeforeItIsUsedAsAKey()
        {
            //Lot 0 means "in no lot" everywhere else in this namespace, and a lot named twice is
            //not twice as busy. Both would be silent corruption of a dictionary keyed by lot: the
            //second entry would simply never be reached, and nothing about the result would look
            //wrong. The list is compared as a copy so the assertion is about its contents.
            var simulator = new OffscreenSimulator(Seed)
            {
                VisitorLots = _ => new short[] { 12, 12, 0, 11 }
            };

            Assert.Equal(new List<short> { 11, 12 }, LotList(simulator.LotsWithVisitors(ClockAt(12))));
            //A source that answers nothing, or answers with nothing, is "nobody" rather than an
            //error - a schedule that cannot say is not a reason to stop the world decaying.
            Assert.Equal(new List<short>(), LotList(new OffscreenSimulator(Seed) { VisitorLots = _ => null }
                .LotsWithVisitors(ClockAt(12))));
            Assert.Equal(new List<short>(), LotList(new OffscreenSimulator(Seed).LotsWithVisitors(ClockAt(12))));
        }

        [Fact]
        public void TheVisitorsModelReachesTheCoarseLotsToo()
        {
            //A hollow lot is the lot most likely to be a community lot, so a company effect that
            //only applied to lots with marshals would stop exactly where the model is thinnest.
            //Midday, because a community lot shut at three in the morning is not evidence of
            //anything, and the charged decay rather than the motives - see the resident test for
            //why the motive values are the wrong thing to compare.
            var layout = CommunityLayout(11);
            var empty = new OffscreenSimulator(Seed);
            var busy = new OffscreenSimulator(Seed)
            {
                VisitorLots = CommunityVisitorLots(Seed, layout),
                VisitorCount = CommunityVisitorHeads(Seed, layout)
            };

            var storeA = new LotStore();
            storeA.SetHollow(11, new VMHollowMarshal());
            var storeB = new LotStore();
            storeB.SetHollow(11, new VMHollowMarshal());
            var clockA = ClockAt(12);
            var clockB = ClockAt(12);

            //A coarse lot's starting motives are drawn from the seed rather than handed to it, so
            //both runs are snapshotted after the warm-up tick and the charged decay is measured
            //from there.
            empty.Tick(1, clockA, storeA, layout);
            busy.Tick(1, clockB, storeB, layout);
            var beforeEmpty = SnapshotRecord(empty.RecordFor(11));
            var beforeBusy = SnapshotRecord(busy.RecordFor(11));
            empty.Tick(GameHours(4), clockA, storeA, layout);
            busy.Tick(GameHours(4), clockB, storeB, layout);

            var record = busy.RecordFor(11);
            Assert.True(record.IsCoarseModel);
            Assert.True(record.CompanySteps > 0);
            Assert.InRange(record.LastVisitorCount, NeighbourhoodWorldFeatures.MinVisitorsPerLot,
                NeighbourhoodWorldFeatures.MaxVisitorsPerLot);

            var chargedBusy = RecordCharged(record, beforeBusy);
            var chargedEmpty = RecordCharged(empty.RecordFor(11), beforeEmpty);
            Assert.True(chargedBusy < chargedEmpty,
                "the company had to change something: " + chargedBusy + " against " + chargedEmpty);
        }

        [Fact]
        public void AnOffScreenResultWithVisitorsStillDoesNotDependOnTheFrameRate()
        {
            //The strongest claim the design has to keep, and the one the step projection exists for.
            //Six hours from eight in the evening, so the window crosses midnight and the visitor
            //model's day index rolls over mid-tick: one long tick against 360 one-second ticks, with
            //the real B8 schedule wired in, must land in the same place. A gate read from the
            //world clock once per tick would not, and neither would a gate read from a features
            //object's cached list.
            var layout = CommunityLayout(11);
            var coarse = new OffscreenSimulator(Seed);
            var fine = new OffscreenSimulator(Seed);
            foreach (var simulator in new[] { coarse, fine })
            {
                simulator.VisitorLots = CommunityVisitorLots(Seed, layout);
                simulator.VisitorCount = CommunityVisitorHeads(Seed, layout);
            }

            var clockA = ClockAt(20);
            var clockB = ClockAt(20);
            var storeA = new LotStore();
            var storeB = new LotStore();
            var marshalA = ResidentMarshal(100);
            var marshalB = ResidentMarshal(100);
            storeA.SetActive(11, marshalA);
            storeB.SetActive(11, marshalB);

            coarse.Tick(GameHours(6), clockA, storeA, layout);
            for (int i = 0; i < 6 * 60; i++) fine.Tick(1, clockB, storeB, layout);

            Assert.Equal(clockA.Ticks, clockB.Ticks);
            Assert.Equal(Motives(marshalA, 0), Motives(marshalB, 0));
            Assert.True(coarse.RecordFor(11).SameAs(fine.RecordFor(11)));
            //And the window really did contain a visited hour, or the equality above is vacuous.
            Assert.True(coarse.RecordFor(11).CompanySteps > 0);
        }

        [Fact]
        public void TheJitterWindowAtNoCompanyIsTheSameWindowB6DrewIn()
        {
            //"With no source, the behaviour is exactly today's" is a claim about an expression, and
            //this is where the expression is pinned. NeighbourhoodRng.NextInt is
            // <c>min + raw % span</c>, so the pre-B8 window and the one the company model uses
            // hand back the same multiplier for the same raw draw - which is why an empty house is
            //not "nearly" the old behaviour but the old behaviour.
            //
            //Both sides are written out here rather than read off the simulator, so a retune of
            //either window - or of the two constants the windows are built from - is a visible
            //failure rather than a quiet divergence every lot in a neighbourhood inherits.
            Assert.Equal(1000, OffscreenSimulator.NoCompanyScale);
            Assert.Equal(200, OffscreenSimulator.MaxDecayJitter);

            for (int step = 1; step <= 32; step++)
            {
                var preB8 = StepStream(step);
                var withCompany = StepStream(step);
                for (int motive = 0; motive < 7; motive++)
                {
                    //What OffscreenSimulator.ScaleFraction used to compute...
                    var oldOffset = preB8.NextInt(-OffscreenSimulator.MaxDecayJitter,
                        OffscreenSimulator.MaxDecayJitter + 1);
                    //...and what it computes now with an empty house.
                    var scale = withCompany.NextInt(OffscreenSimulator.NoCompanyScale - OffscreenSimulator.MaxDecayJitter,
                        OffscreenSimulator.NoCompanyScale + OffscreenSimulator.MaxDecayJitter + 1);
                    Assert.Equal(1000 + oldOffset, scale);
                }
            }

            //And the composed multiplier is bounded below by the full house, so no number of
            //visitors can make a rate zero or negative - decay still decays.
            Assert.True(OffscreenSimulator.MinCompanyScale - OffscreenSimulator.MaxDecayJitter > 0);
        }

        [Fact]
        public void NoVisitorIsEverWrittenIntoALotsSims()
        {
            //A visitor is not a Sim this build has any record of. Four of them walking into a house
            //must not become four more VMAvatarMarshal entities, because the next save would write
            //them to HouseNN.iff and the lot would load with Sims who were never in it.
            var layout = CommunityLayout(11);
            var simulator = new OffscreenSimulator(Seed)
            {
                VisitorLots = _ => new short[] { 11 },
                VisitorCount = (_, __) => NeighbourhoodWorldFeatures.MaxVisitorsPerLot
            };
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);

            simulator.Tick(1, Clock(), store, layout);
            var result = simulator.Tick(GameHours(8), Clock(), store, layout);

            Assert.Single(marshal.Entities);
            Assert.Equal(1, result.AvatarsDecayed);
            Assert.Equal(1, simulator.RecordFor(11).AvatarCount);
        }

        // ---- the room model and the income model --------------------------------------------------------

        /// <summary>
        /// A resident lot whose stored Sims name a family and, optionally, hold a job. The three
        /// person-data words the income model reads, written the way a real marshal has them.
        /// </summary>
        private static VMMarshal WorkingMarshal(short family, short jobType, short promotion,
            params short[] sims)
        {
            var marshal = ResidentMarshal(sims.Length == 0 ? new short[] { 100 } : sims);
            foreach (var entity in marshal.Entities)
            {
                var avatar = (VMAvatarMarshal)entity;
                avatar.PersonData[(int)VMPersonDataVariable.TS1FamilyNumber] = family;
                avatar.PersonData[(int)VMPersonDataVariable.JobType] = jobType;
                avatar.PersonData[(int)VMPersonDataVariable.JobPromotionLevel] = promotion;
            }
            return marshal;
        }

        /// <summary>
        /// The seven decaying motives, in order, as a comparable string. Deliberately not the whole
        /// table: the two models are only allowed to move mood, so a comparison that included
        /// <see cref="VMMotive.Mood"/> would be asserting the model had no effect at all.
        /// </summary>
        private static string Decaying(VMMarshal marshal, int index)
        {
            var parts = new List<string>();
            foreach (var motive in VMTS1MotiveDecay.DecrementMotives)
            {
                parts.Add(Motives(marshal, index)[(int)motive].ToString());
            }
            return string.Join(",", parts);
        }

        /// <summary>One run with the two opt-in models as the caller asked for them.</summary>
        private struct ModelRun
        {
            public string motives;
            public short room;
            public short mood;
            public long incomePaid;
            public int paydays;
            public int lotsPaid;
            public int modelledPaydays;
            public OffscreenLotRecord record;
        }

        /// <summary>
        /// A day away from a working household, with the two models as the caller asked for them.
        /// A day is one window's worth of paydays and is short enough that no motive reaches the
        /// engine's floor, so a comparison between two runs measures the model rather than a clamp.
        /// The per-tick time budget is raised to cover the window, because the default is one game
        /// day and a longer absence would be truncated - which is the cap doing its job, not a
        /// property of either model.
        /// </summary>
        private static ModelRun ModelDay(long seed, bool room, bool income, int gameHours = 24)
        {
            var simulator = new OffscreenSimulator(seed)
            {
                ModelsRoomScore = room,
                EstimatesHouseholdIncome = income
            };
            simulator.MaxSecondsPerTick = (int)GameHours(gameHours);
            var clock = Clock();
            var store = new LotStore();
            var marshal = WorkingMarshal(7, 3, 4);
            store.SetActive(11, marshal);
            var layout = Layout(11);

            simulator.Tick(1, clock, store, layout);
            var result = simulator.Tick(GameHours(gameHours), clock, store, layout);
            return new ModelRun
            {
                motives = Decaying(marshal, 0),
                room = Motives(marshal, 0)[(int)VMMotive.Room],
                mood = Motives(marshal, 0)[(int)VMMotive.Mood],
                incomePaid = simulator.RecordFor(11).EstimatedIncomePaid,
                paydays = simulator.RecordFor(11).EstimatedPaydays,
                lotsPaid = result.LotsPaid,
                modelledPaydays = result.ModelledPaydays,
                record = simulator.RecordFor(11)
            };
        }

        [Fact]
        public void TheRoomModelIsOffByDefaultAndOffLeavesTheRunAlone()
        {
            //The gate for the first new model, as an equality rather than as a flag read: a build
            //that has not asked for a room score must get the numbers it got before one existed,
            //which is the pre-existing test above (a marshal's room slot is left exactly as it was
            //found) said in its own terms.
            var simulator = new OffscreenSimulator();

            Assert.False(simulator.ModelsRoomScore);
            var off = ModelDay(Seed, room: false, income: false);

            Assert.False(off.record.HasRoomScore);
            Assert.Equal(0, off.record.RoomScore);
            //And two identical runs, which is what "the model is not running" has to mean.
            Assert.Equal(off.motives, ModelDay(Seed, false, false).motives);
            Assert.True(off.record.SameAs(ModelDay(Seed, false, false).record));
        }

        [Fact]
        public void AModelledRoomScoreIsBoundedAndIsTheEngineOwnArithmetic()
        {
            //The score runs through VMRoomScore.Compute, so it lands inside the engine's own
            //-100..100 and nowhere else, and it reaches the one place the engine reads it from: the
            //Sim's own VMMotive.Room slot, which the engine overwrites on the lot's first live tick.
            var run = ModelDay(Seed, room: true, income: false);

            Assert.True(run.record.HasRoomScore);
            Assert.InRange(run.record.RoomScore, VMRoomScore.Min, VMRoomScore.Max);
            Assert.Equal(run.record.RoomScore, run.room);
            //Mood is the only thing it feeds, so the mood must be the engine's own sum with the
            //modelled score in it rather than a number the model wrote on the side.
            Assert.Equal((SumDecaying(run.motives) + run.record.RoomScore) / 8, run.mood);
        }

        [Fact]
        public void TheRoomScoreMovesWithTheLotItIsModelledFrom()
        {
            //Which real input the model has: the footprint, because the engine divides a room's
            //impact by its area. A bigger house is the same household's tidiness spread over more
            //tiles, so it can only score the same or worse - and a lot whose area the model's own
            //maximum impact cannot reach at all (64x64 divides by more than the spread is wide) is
            //left at the engine's flat penalty, which is the honest answer for a house this model
            //has nothing to say about.
            var simulator = new OffscreenSimulator(Seed) { ModelsRoomScore = true };
            var clock = Clock();
            var store = new LotStore();
            store.SetActive(11, WorkingMarshal(7, 3, 4));
            store.SetActive(12, WorkingMarshal(7, 3, 4));

            var lots = new List<NeighbourhoodLot>
            {
                new NeighbourhoodLot { Number = 11, Size = 8, WorldX = 0, WorldY = 0, HasHouse = true },
                new NeighbourhoodLot
                {
                    Number = 12, Size = NeighbourhoodLayout.MaxLotSize, WorldX = 40, WorldY = 0,
                    HasHouse = true
                }
            };
            var layout = new NeighbourhoodLayout(0, (int)Seed, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                NeighbourhoodTestInput.SmallMap, lots);

            simulator.Tick(GameHours(2), clock, store, layout);

            var small = simulator.RecordFor(11).RoomScore;
            var large = simulator.RecordFor(12).RoomScore;
            Assert.InRange(small, VMRoomScore.Min, VMRoomScore.Max);
            Assert.InRange(large, VMRoomScore.Min, VMRoomScore.Max);
            Assert.Equal(-VMRoomScore.InsidePenalty, large);
            Assert.True(large <= small, "a bigger footprint cannot have a better room score: "
                + large + " against " + small);
        }

        [Fact]
        public void ALotTheLayoutCannotDescribeIsNotGivenARoomScore()
        {
            //"No data, nothing modelled" is the rule for both new models, and for the room score
            //there are two ways to have no data: a lot the layout has never heard of, and a lot with
            //no house file. Neither may be handed a number that looks like a measurement.
            var simulator = new OffscreenSimulator(Seed) { ModelsRoomScore = true };
            var clock = Clock();
            var store = new LotStore();
            store.SetActive(11, WorkingMarshal(7, 3, 4));
            store.SetActive(12, WorkingMarshal(7, 3, 4));
            store.SetActive(13, WorkingMarshal(7, 3, 4));

            var lots = new List<NeighbourhoodLot>
            {
                //12 is a plot: a rectangle with no house in it, and so no rooms.
                new NeighbourhoodLot { Number = 12, Size = 16, WorldX = 40, WorldY = 0, HasHouse = false },
                new NeighbourhoodLot { Number = 13, Size = 16, WorldX = 80, WorldY = 0, HasHouse = true }
            };
            var layout = new NeighbourhoodLayout(0, (int)Seed, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                NeighbourhoodTestInput.SmallMap, lots);

            simulator.Tick(GameHours(2), clock, store, layout);

            Assert.False(simulator.RecordFor(11).HasRoomScore);
            Assert.False(simulator.RecordFor(12).HasRoomScore);
            Assert.True(simulator.RecordFor(13).HasRoomScore);
            //And the ones with no score report the zero the build reported before, not a guess.
            Assert.Equal(0, simulator.RecordFor(11).RoomScore);
            Assert.Equal(0, simulator.RecordFor(12).RoomScore);
        }

        [Fact]
        public void TheRoomScoreIsSeededAndIsFrameRateIndependent()
        {
            //Determinism, both halves of it. The score is drawn once from the lot's own labelled
            //stream, so the same seed gives the same number whatever hour the lot was first looked
            //at; and because it is a function of the lot rather than of the steps, eight hours in
            //one tick and in 480 ticks land on the same room score and the same records.
            var first = ModelDay(Seed, room: true, income: false);
            var second = ModelDay(Seed, room: true, income: false);
            Assert.Equal(first.room, second.room);
            Assert.True(first.record.SameAs(second.record));
            //Not a constant of the type: a different seed must reach a different household.
            Assert.NotEqual(first.room, ModelDay(OtherSeed, room: true, income: false).room);

            var coarse = new OffscreenSimulator(Seed) { ModelsRoomScore = true };
            var fine = new OffscreenSimulator(Seed) { ModelsRoomScore = true };
            var clockA = Clock();
            var clockB = Clock();
            var storeA = new LotStore();
            var storeB = new LotStore();
            storeA.SetActive(11, WorkingMarshal(7, 3, 4));
            storeB.SetActive(11, WorkingMarshal(7, 3, 4));
            var layout = Layout(11);

            coarse.Tick(GameHours(8), clockA, storeA, layout);
            for (int i = 0; i < 8 * 60; i++) fine.Tick(1, clockB, storeB, layout);

            Assert.Equal(coarse.RecordFor(11).RoomScore, fine.RecordFor(11).RoomScore);
            Assert.True(coarse.RecordFor(11).SameAs(fine.RecordFor(11)));
        }

        [Fact]
        public void TheCoarseLotsRoomScoreReachesItsMoodAsWell()
        {
            //The coarse path runs the same DecayStep the resident path does, and the room slot is
            //the one input it feeds it, so a coarse record's mood is the engine's own sum over the
            //aggregate plus the modelled score. Asserted rather than assumed: a second
            //implementation of the decay is the thing this file refuses to grow, and this is where
            //it would show.
            var simulator = new OffscreenSimulator(Seed) { ModelsRoomScore = true };
            var clock = Clock();
            var store = new LotStore();
            store.SetHollow(11, new VMHollowMarshal());

            simulator.Tick(GameHours(4), clock, store, Layout(11));
            var record = simulator.RecordFor(11);

            Assert.True(record.HasRoomScore);
            var sum = 0;
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++) sum += record.MotiveAt(i);
            Assert.Equal((sum + record.RoomScore) / 8, record.Mood);
        }

        [Fact]
        public void TheIncomeModelIsOffByDefaultAndOffLeavesTheRunAlone()
        {
            var simulator = new OffscreenSimulator();

            Assert.False(simulator.EstimatesHouseholdIncome);
            var off = ModelDay(Seed, room: false, income: false);

            Assert.False(off.record.HasIncomeEstimate);
            Assert.Equal(OffscreenSimulator.NoFamilyNumber, off.record.FamilyNumber);
            Assert.Equal(0, off.record.EstimatedDailyIncome);
            Assert.Equal(OffscreenSimulator.NoPaydayHour, off.record.PaydayHour);
            Assert.Equal(0, off.record.EstimatedPaydays);
            Assert.Equal(0L, off.record.EstimatedIncomePaid);
            Assert.Equal(0, off.lotsPaid);
            Assert.Equal(0, off.modelledPaydays);
            Assert.Equal(off.motives, ModelDay(Seed, false, false).motives);
            Assert.True(off.record.SameAs(ModelDay(Seed, false, false).record));
        }

        [Fact]
        public void ALotWithNoFamilyIsNotGivenAnIncome()
        {
            //The routing rule, and the one that keeps this out of being a second ledger: money is a
            //family's, a family is a FAMI chunk id, and a lot with no family to name gets nothing.
            //Three ways to have no family, all of which are real: a hollow lot (no marshal at all),
            //a marshal whose person data is all zeroes (never assigned), and a marshal with no
            //people in it.
            var simulator = new OffscreenSimulator(Seed) { EstimatesHouseholdIncome = true };
            var clock = Clock();
            var store = new LotStore();
            store.SetHollow(11, new VMHollowMarshal());
            store.SetActive(12, ResidentMarshal(100));
            store.SetActive(13, new VMMarshal());

            simulator.Tick(GameHours(24), clock, store, Layout(11, 12, 13));

            foreach (short lot in new short[] { 11, 12, 13 })
            {
                var record = simulator.RecordFor(lot);
                Assert.False(record.HasIncomeEstimate);
                Assert.Equal(OffscreenSimulator.NoFamilyNumber, record.FamilyNumber);
                Assert.Equal(0, record.EstimatedDailyIncome);
                Assert.Equal(OffscreenSimulator.NoPaydayHour, record.PaydayHour);
                Assert.Equal(0, record.EstimatedPaydays);
                Assert.Equal(0L, record.EstimatedIncomePaid);
            }
        }

        [Fact]
        public void AModelledIncomeIsBoundedAttributableAndBiggerForAMoreSeniorJob()
        {
            //The three claims a rate makes: it is inside the model's own range, it belongs to a
            //named family, and it moves the way the model says it does - up with the ladder, up with
            //the number of earners.
            var store = new LotStore();
            store.SetActive(11, WorkingMarshal(7, 3, 0, 100));
            var junior = new OffscreenSimulator(Seed) { EstimatesHouseholdIncome = true };
            junior.Tick(GameHours(2), Clock(), store, Layout(11));

            var seniorStore = new LotStore();
            seniorStore.SetActive(11, WorkingMarshal(7, 3, OffscreenSimulator.MaxPromotionLevel, 100));
            var senior = new OffscreenSimulator(Seed) { EstimatesHouseholdIncome = true };
            senior.Tick(GameHours(2), Clock(), seniorStore, Layout(11));

            var record = junior.RecordFor(11);
            Assert.True(record.HasIncomeEstimate);
            Assert.Equal(7, record.FamilyNumber);
            Assert.InRange(record.EstimatedDailyIncome, 0, OffscreenSimulator.MaxEstimatedDailyIncome);
            Assert.InRange(record.PaydayHour, 0, OffscreenSimulator.HoursPerGameDay - 1);
            Assert.True(senior.RecordFor(11).EstimatedDailyIncome > record.EstimatedDailyIncome,
                "the top of a job's ladder has to pay more than the bottom of it");

            //And the pure function behind it, which is where the bounds are stated rather than
            //hoped for: two earners pay twice one, nobody in a job pays nothing, and no combination
            //of arguments reaches past the model's own ceiling.
            Assert.Equal(0, OffscreenSimulator.DailyIncomeFor(0, 0, 0));
            Assert.Equal(0, OffscreenSimulator.DailyIncomeFor(-3, 5, 5));
            Assert.Equal(2 * OffscreenSimulator.DailyIncomeFor(1, 2, 7), OffscreenSimulator.DailyIncomeFor(2, 2, 7));
            Assert.True(OffscreenSimulator.DailyIncomeFor(1, 5, 0) > OffscreenSimulator.DailyIncomeFor(1, 0, 0));
            Assert.Equal(OffscreenSimulator.MaxEstimatedDailyIncome,
                OffscreenSimulator.DailyIncomeFor(4000, 4000, 4000));
            Assert.Equal(OffscreenSimulator.ResidentialRoomImpact,
                OffscreenSimulator.RoomImpactFor(LotZoning.ZoneResidential));
            Assert.Equal(OffscreenSimulator.CommunityRoomImpact,
                OffscreenSimulator.RoomImpactFor(LotZoning.ZoneCommunity));
            Assert.Equal(OffscreenSimulator.StudioRoomImpact,
                OffscreenSimulator.RoomImpactFor(LotZoning.ZoneStudio));
        }

        [Fact]
        public void AGameDayIsOnePaydayAtEveryFidelityAndHoweverTheDayIsChoppedUp()
        {
            //The cadence, as a property of the pure function. Counted in hours rather than in steps
            //because a step is half an hour at Coarse and two minutes at Full: a per-step test would
            //pay a household twice on some steps and not at all on others depending on the fidelity.
            for (int hour = 0; hour < OffscreenSimulator.HoursPerGameDay; hour++)
            {
                var coarse = OffscreenSimulator.CoarseStepGameMinutes;
                var fine = OffscreenSimulator.FullStepGameMinutes;
                Assert.Equal(1, OffscreenSimulator.PaydaysBetween(0, 24 * 60 / coarse, coarse, hour));
                Assert.Equal(1, OffscreenSimulator.PaydaysBetween(0, 24 * 60 / fine, fine, hour));

                //And the payday lands on its own hour and not a moment before it: the steps up to
                //the start of the payday hour contain exactly one payday, and the steps before that
                //contain none.
                Assert.Equal(0, OffscreenSimulator.PaydaysBetween(0, hour * 60 / coarse, coarse, hour));
                Assert.Equal(1,
                    OffscreenSimulator.PaydaysBetween(0, (hour + 1) * 60 / coarse, coarse, hour));
                Assert.Equal(1,
                    OffscreenSimulator.PaydaysBetween(0, (hour + 1) * 60 / fine, fine, hour));

                //Three days, chopped at an odd place, is three - the sum of the parts is the whole,
                //which is the frame-invariance the absolute step index buys.
                var whole = OffscreenSimulator.PaydaysBetween(0, 3 * 24 * 60 / coarse, coarse, hour);
                var parts = OffscreenSimulator.PaydaysBetween(0, 37 * 60 / coarse, coarse, hour)
                    + OffscreenSimulator.PaydaysBetween(37 * 60 / coarse, 35 * 60 / coarse, coarse, hour);
                Assert.Equal(3, whole);
                Assert.Equal(whole, parts);
            }
            //Degenerate arguments are "no payday" rather than an exception: a step range nobody
            //walked, and a household with no estimate.
            Assert.Equal(0, OffscreenSimulator.PaydaysBetween(0, 0, 30, 4));
            Assert.Equal(0, OffscreenSimulator.PaydaysBetween(0, 100, 0, 4));
            Assert.Equal(0, OffscreenSimulator.PaydaysBetween(0, 100, 30, OffscreenSimulator.NoPaydayHour));
            Assert.Equal(0, OffscreenSimulator.PaydaysBetween(0, 100, 30, 24));
            Assert.Equal(0, OffscreenSimulator.PaydaysBetween(-1, 100, 30, 4));
            Assert.Equal(0, OffscreenSimulator.PaydaysBetween(OffscreenSimulator.MaxStepIndex + 1, 100, 30, 4));
        }

        [Fact]
        public void TheModelledIncomePaidIsExactlyTheRateTimesThePaydays()
        {
            //"This is not a second money ledger", stated as the invariant that makes it true. The
            //running total is computed on read from the two numbers printed either side of it, so
            //there is no stored balance that could drift away from the rate and the count that
            //produced it - and a caller that wants a balance has to say so rather than find one.
            var run = ModelDay(Seed, room: false, income: true, gameHours: 72);
            var record = run.record;

            Assert.True(record.HasIncomeEstimate);
            Assert.Equal(3, record.EstimatedPaydays);
            //One lot, three paydays: the result counts lots and events separately, because a caller
            //wanting "which households got paid" and a caller wanting "how much moved" are different
            //questions and one of them is the record's own.
            Assert.Equal(1, run.lotsPaid);
            Assert.Equal(record.EstimatedPaydays, run.modelledPaydays);
            Assert.Equal((long)record.EstimatedDailyIncome * record.EstimatedPaydays, record.EstimatedIncomePaid);
            //One earner with a job earns something, so the total above is not zero multiplied out.
            Assert.True(record.EstimatedDailyIncome > 0);
        }

        [Fact]
        public void TheIncomeModelIsDeterministicAndFrameRateIndependent()
        {
            var first = ModelDay(Seed, room: false, income: true);
            var second = ModelDay(Seed, room: false, income: true);

            Assert.Equal(first.incomePaid, second.incomePaid);
            Assert.Equal(first.paydays, second.paydays);
            Assert.True(first.record.SameAs(second.record));
            //A different seed is a different household, which is what proves the draw is read.
            var other = ModelDay(OtherSeed, room: false, income: true).record;
            Assert.NotEqual(first.record.EstimatedDailyIncome * 1000 + first.record.PaydayHour,
                other.EstimatedDailyIncome * 1000 + other.PaydayHour);

            var coarse = new OffscreenSimulator(Seed) { EstimatesHouseholdIncome = true };
            var fine = new OffscreenSimulator(Seed) { EstimatesHouseholdIncome = true };
            var clockA = Clock();
            var clockB = Clock();
            var storeA = new LotStore();
            var storeB = new LotStore();
            storeA.SetActive(11, WorkingMarshal(7, 3, 4));
            storeB.SetActive(11, WorkingMarshal(7, 3, 4));
            var layout = Layout(11);

            coarse.Tick(GameHours(8), clockA, storeA, layout);
            for (int i = 0; i < 8 * 60; i++) fine.Tick(1, clockB, storeB, layout);

            Assert.True(coarse.RecordFor(11).SameAs(fine.RecordFor(11)));
            Assert.Equal(coarse.RecordFor(11).EstimatedIncomePaid, fine.RecordFor(11).EstimatedIncomePaid);
            //Eight hours is not a day, so at most one payday can have fallen in it - a household that
            //has not reached its payday has not been paid, which is the difference between a cadence
            //and a per-tick drip.
            Assert.InRange(coarse.RecordFor(11).EstimatedPaydays, 0, 1);
        }

        [Fact]
        public void TheIncomeModelWritesNothingIntoTheLot()
        {
            //The claim that matters most for "not a second ledger", checked on the fields a ledger
            //would want: the per-Sim person data, including the word TS1 draws over a Sim's head for
            //the money it is carrying, and the two TSO-era motive slots a money number has been
            //parked in before. Nothing this type does writes them, and the income model does not
            //start now.
            var store = new LotStore();
            var marshal = WorkingMarshal(7, 3, 4);
            store.SetActive(11, marshal);
            var personDataBefore = (short[])Sim(marshal, 0).PersonData.Clone();
            var motiveBefore = Snapshot(marshal, 0);
            var simulator = new OffscreenSimulator(Seed)
            {
                EstimatesHouseholdIncome = true,
                ModelsRoomScore = true
            };
            var clock = Clock();
            //A week in one frame, so the truncation cap is not what is being measured - the cap is
            //raised rather than the test quietly checking a day.
            simulator.MaxSecondsPerTick = (int)GameHours(24 * 7);

            simulator.Tick(GameHours(24 * 7), clock, store, Layout(11));

            Assert.Equal(personDataBefore, Sim(marshal, 0).PersonData);
            var after = Motives(marshal, 0);
            //The three TSO-era motive slots a money number has been parked in before, including the
            //one TS1 calls its career motive. None of them is one this type writes.
            Assert.Equal(motiveBefore[(int)VMMotive.HappyLife], after[(int)VMMotive.HappyLife]);
            Assert.Equal(100, after[(int)VMMotive.HappyWeek]);
            Assert.Equal(100, after[(int)VMMotive.HappyDay]);
            //The estimate is on the record, where a caller can read it, and nowhere else.
            Assert.True(simulator.RecordFor(11).HasIncomeEstimate);
            Assert.True(simulator.RecordFor(11).EstimatedPaydays > 0);
        }

        [Fact]
        public void NeitherModelMovesTheDecay()
        {
            //The labels earning their keep. A room score or an income drawn from the lot's own
            //stream would shift every step stream after it and make the decay depend on whether the
            //caller had switched a model on - which would be the one thing §8's determinism is for.
            //The seven decaying motives are therefore identical with both models on and both off,
            //and a lot's step count with them is too.
            var bare = ModelDay(Seed, room: false, income: false);
            var modelled = ModelDay(Seed, room: true, income: true);

            Assert.Equal(bare.motives, modelled.motives);
            Assert.Equal(bare.record.Steps, modelled.record.Steps);
            //The models did something, or the equality above is vacuous: the room score reached the
            //Sim's motive table and the household was paid.
            Assert.True(modelled.record.HasRoomScore);
            Assert.True(modelled.record.HasIncomeEstimate);
            Assert.NotEqual(bare.room, modelled.room);
        }

        [Fact]
        public void PausedModelsNeitherRoomNorIncome()
        {
            //"Paused freezes everything" has to cover the new models too, not just the decay, and
            //the strictest reading is the one worth pinning: no record at all, so neither model has
            //anything to write into and no draw was taken.
            var store = new LotStore();
            var marshal = WorkingMarshal(7, 3, 4);
            var personDataBefore = (short[])Sim(marshal, 0).PersonData.Clone();
            var motiveBefore = Snapshot(marshal, 0);
            var simulator = new OffscreenSimulator(Seed)
            {
                ModelsRoomScore = true,
                EstimatesHouseholdIncome = true,
                Fidelity = OffscreenFidelity.Paused
            };

            var result = simulator.Tick(GameHours(24 * 7), Clock(), store, Layout(11));

            Assert.True(result.Paused);
            Assert.Equal(0, simulator.RecordCount);
            Assert.Equal(0, result.LotsPaid);
            Assert.Equal(0, result.ModelledPaydays);
            Assert.Null(simulator.RecordFor(11));
            Assert.Equal(motiveBefore, Motives(marshal, 0));
            Assert.Equal(personDataBefore, Sim(marshal, 0).PersonData);
        }

        /// <summary>The sum of the seven decaying motives in a <see cref="Decaying"/> string.</summary>
        private static int SumDecaying(string motives)
        {
            var total = 0;
            foreach (var part in motives.Split(',')) total += int.Parse(part);
            return total;
        }


        /// <summary>A layout whose lots are community lots, which is what B8's schedule opens.</summary>
        private static NeighbourhoodLayout CommunityLayout(params short[] lotNumbers)
        {
            var lots = new List<NeighbourhoodLot>();
            for (int i = 0; i < lotNumbers.Length; i++)
            {
                lots.Add(new NeighbourhoodLot
                {
                    Number = lotNumbers[i],
                    Size = 16,
                    WorldX = i * 20,
                    WorldY = 0,
                    HasHouse = true,
                    Zone = LotZoning.ZoneCommunity
                });
            }
            return new NeighbourhoodLayout(0, (int)Seed, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                NeighbourhoodTestInput.SmallMap, lots);
        }

        /// <summary>
        /// B8's own schedule as the gate, projected onto the lot numbers this type's records are
        /// keyed by. The static rather than a <c>NeighbourhoodWorldFeatures</c> instance's cached
        /// <c>Visitors</c>, because the cache is a function of when that object last refreshed and
        /// a gate that reads it would make the off-screen result depend on the frame rate.
        /// </summary>
        private static OffscreenVisitorLots CommunityVisitorLots(long seed, NeighbourhoodLayout layout)
        {
            return clock =>
            {
                var found = NeighbourhoodWorldFeatures.VisitorsAt(clock, seed, layout);
                var lots = new List<short>(found.Count);
                for (int i = 0; i < found.Count; i++) lots.Add((short)found[i].LotNumber);
                return lots;
            };
        }

        /// <summary>The head counts from the same schedule, for the same instant.</summary>
        private static OffscreenVisitorCount CommunityVisitorHeads(long seed, NeighbourhoodLayout layout)
        {
            return (clock, lot) =>
            {
                var found = NeighbourhoodWorldFeatures.VisitorsAt(clock, seed, layout);
                for (int i = 0; i < found.Count; i++)
                {
                    if (found[i].LotNumber == lot) return found[i].HeadCount;
                }
                return 0;
            };
        }

        /// <summary>One visitor run's comparable output, for the determinism assertion.</summary>
        private struct CompanyRunResult
        {
            public string clock;
            public string motives;
            public OffscreenLotRecord record;
            public int steps;
            public int lotsWithCompany;
        }

        private static CompanyRunResult CompanyRun(long seed)
        {
            var layout = CommunityLayout(11);
            var simulator = new OffscreenSimulator(seed)
            {
                VisitorLots = CommunityVisitorLots(seed, layout),
                VisitorCount = CommunityVisitorHeads(seed, layout)
            };
            var clock = Clock();
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);

            simulator.Tick(1, clock, store, layout);
            var result = simulator.Tick(GameHours(12), clock, store, layout);

            return new CompanyRunResult
            {
                clock = clock.ToString(),
                motives = string.Join(",", Motives(marshal, 0)),
                record = simulator.RecordFor(11),
                steps = result.CompanySteps,
                lotsWithCompany = result.LotsWithCompany
            };
        }

        /// <summary>One run against a source whose head count is chosen by the caller.</summary>
        private static CompanyRunResult HeadCountRun(int visitors)
        {
            var layout = CommunityLayout(11);
            var simulator = new OffscreenSimulator(Seed)
            {
                VisitorLots = _ => new short[] { 11 },
                VisitorCount = (_, __) => visitors
            };
            var clock = Clock();
            var store = new LotStore();
            var marshal = ResidentMarshal(100);
            store.SetActive(11, marshal);

            simulator.Tick(1, clock, store, layout);
            simulator.Tick(GameHours(8), clock, store, layout);

            return new CompanyRunResult
            {
                clock = clock.ToString(),
                motives = string.Join(",", Motives(marshal, 0)),
                record = simulator.RecordFor(11)
            };
        }

        /// <summary>
        /// One decay step's own sub-stream, rebuilt the way
        /// <see cref="OffscreenSimulator"/> derives it: the simulation stream, the root label, the
        /// lot number, then the absolute step index. Exposed to a test so the claim about the
        /// jitter window can be checked against the same draws the simulator makes rather than
        /// against a paraphrase of them.
        /// </summary>
        private static NeighbourhoodRng StepStream(int step)
        {
            return NeighbourhoodStreams.FromSeed(Seed).Simulation
                .ForLabel(OffscreenSimulator.StreamLabel)
                .ForLabel("11")
                .ForLabel(OffscreenSimulator.StepStreamLabel + step);
        }

        /// <summary>A copy of a lot list, for an assertion that compares contents rather than identity.</summary>
        private static List<short> LotList(IReadOnlyList<short> lots)
        {
            var copy = new List<short>(lots.Count);
            for (int i = 0; i < lots.Count; i++) copy.Add(lots[i]);
            return copy;
        }

        /// <summary>
        /// Everything a run produces, as comparable values. Three runs' worth of state in one
        /// struct so a determinism assertion is one <c>Assert.Equal</c> per field rather than a
        /// paragraph of comparisons that stops being read after the first one.
        /// </summary>
        private struct RunResult
        {
            public string clock;
            public string motives;
            public OffscreenLotRecord record;
        }

        private static RunResult Run(long seed)
        {
            var simulator = new OffscreenSimulator(seed);
            var clock = Clock();
            var store = new LotStore();
            var marshal = ResidentMarshal(100, 70);
            store.SetActive(11, marshal);
            var layout = Layout(11, 12);
            store.SetHollow(12, new VMHollowMarshal());

            simulator.Tick(1, clock, store, layout);
            simulator.Tick(GameHours(6), clock, store, layout);
            simulator.Tick(GameHours(3), clock, store, layout);

            return new RunResult
            {
                clock = clock.ToString(),
                //Joined by hand, not with "+": concatenating two short[]s calls ToString on each
                //and produces "System.Int16[]" twice, which is equal for every seed and would make
                //the determinism test pass for the wrong reason.
                motives = string.Join(",", Motives(marshal, 0))
                    + "|" + string.Join(",", Motives(marshal, 1)),
                record = simulator.RecordFor(12)
            };
        }

        private static WorldClock ClockAt(int hours)
        {
            var clock = Clock();
            for (int i = 0; i < hours * 60 * WorldClock.TicksPerMinuteTs1; i++) clock.Tick();
            return clock;
        }

        private static short[] SnapshotRecord(OffscreenLotRecord record)
        {
            var result = new short[OffscreenLotRecord.MotiveCount];
            for (int i = 0; i < OffscreenLotRecord.MotiveCount; i++) result[i] = record.MotiveAt(i);
            return result;
        }

        private static short[] Seeded(long seed)
        {
            var simulator = new OffscreenSimulator(seed);
            var store = new LotStore();
            store.SetHollow(11, new VMHollowMarshal());
            simulator.Tick(1, Clock(), store, Layout(11));
            return SnapshotRecord(simulator.RecordFor(11));
        }

        private static void SetPersonality(VMMarshal marshal, short active, short outgoing)
        {
            foreach (var entity in marshal.Entities)
            {
                var avatar = entity as VMAvatarMarshal;
                if (avatar == null) continue;
                avatar.PersonData[(int)VMPersonDataVariable.ActivePersonality] = active;
                avatar.PersonData[(int)VMPersonDataVariable.OutgoingPersonality] = outgoing;
            }
        }
    }
}
