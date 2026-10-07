/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Reflection;
using FSO.SimAntics;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Part B's one authority for time: the seed that has to agree with <c>VMTS1Activator</c>, the
    /// round-trip that has to be exact, and the rollover that has to be the engine's.
    ///
    /// <para>
    /// The claim §5.2 makes is narrow and testable - a lot loaded from a <c>HouseNN.iff</c> and a
    /// world seeded from the same globals show the same time, and with Part B off the round trip
    /// changes nothing - so these tests pin exactly that: the six global indices, the fraction
    /// conversion that is easy to get wrong (the global is a fraction, the field is a tick count),
    /// the guard on a short array, and the minute-to-year cascade.
    /// </para>
    /// </summary>
    public sealed class WorldClockTests
    {
        /// <summary>
        /// A world clock sitting at a named moment, at the TS1 tick rate, built the way a real one
        /// would be: pulled out of a lot's clock rather than assigned field by field, so the tests
        /// cannot accidentally rely on a state the type does not actually reach.
        /// </summary>
        private static WorldClock At(int hours, int minutes, int fractions, int day, int month, int year)
        {
            var clock = new WorldClock();
            clock.PullFrom(new VMClock
            {
                Hours = hours,
                Minutes = minutes,
                MinuteFractions = fractions,
                TicksPerMinute = WorldClock.TicksPerMinuteTs1,
                DayOfMonth = day,
                Month = month,
                Year = year
            });
            return clock;
        }

        /// <summary>A <c>GlobalState</c> array with the six time words the activator reads set.</summary>
        private static short[] Globals(int hours, int dayOfMonth, int minutes, int fraction, int month, int year)
        {
            var globals = new short[16];
            globals[WorldClock.HoursGlobal] = (short)hours;
            globals[WorldClock.DayOfMonthGlobal] = (short)dayOfMonth;
            globals[WorldClock.MinutesGlobal] = (short)minutes;
            globals[WorldClock.MinuteFractionsGlobal] = (short)fraction;
            globals[WorldClock.MonthGlobal] = (short)month;
            globals[WorldClock.YearGlobal] = (short)year;
            return globals;
        }

        private static void Tick(WorldClock clock, int times)
        {
            for (int i = 0; i < times; i++) clock.Tick();
        }

        // ---- the seed ------------------------------------------------------------------------------

        [Fact]
        public void TheTimeGlobalsAreTheSixWordsTheActivatorReads()
        {
            //These are VMTS1Activator.LoadFromIff's indices, verbatim. If one of them moves, time
            //moves by an hour - so the numbers are pinned rather than derived, and named so the
            //seed method reads as the same table.
            Assert.Equal(0, WorldClock.HoursGlobal);
            Assert.Equal(1, WorldClock.DayOfMonthGlobal);
            Assert.Equal(5, WorldClock.MinutesGlobal);
            Assert.Equal(6, WorldClock.MinuteFractionsGlobal);
            Assert.Equal(7, WorldClock.MonthGlobal);
            Assert.Equal(8, WorldClock.YearGlobal);
            Assert.Equal(30, WorldClock.TicksPerMinuteTs1);
        }

        [Fact]
        public void SeedingReadsEveryTimeWordTheActivatorWrites()
        {
            var clock = new WorldClock();
            clock.SeedFromGlobalState(Globals(21, 14, 45, 20, 7, 2004));

            Assert.Equal(21, clock.Hours);
            Assert.Equal(14, clock.DayOfMonth);
            Assert.Equal(45, clock.Minutes);
            Assert.Equal(7, clock.Month);
            Assert.Equal(2004, clock.Year);
        }

        [Fact]
        public void TheSubMinuteGlobalIsAFractionAndTheFieldIsATickCount()
        {
            //VMTS1Activator line 245: MinuteFractions = GlobalState[6] * Clock.TicksPerMinute. The
            //global is a fraction of a minute and the field counts sub-minute ticks, so copying it
            //straight across would be off by a factor of thirty - and off silently, because the
            //number is still a plausible tick count.
            var clock = new WorldClock();
            clock.SeedFromGlobalState(Globals(0, 1, 0, 20, 1, 1997));

            Assert.Equal(WorldClock.TicksPerMinuteTs1, clock.TicksPerMinute);
            Assert.Equal(20 * WorldClock.TicksPerMinuteTs1, clock.MinuteFractions);

            //Seconds is deliberately not asserted as "forty seconds past the minute". INeighbourhoodClock
            //defines it as MinuteFractions * 60 / TicksPerMinute over a field it documents as "not
            //normalised", and a seeded field is a fraction times the rate - so it reads 1200 until
            //the clock has ticked once and the field is back inside 0..TicksPerMinute. That is the
            //engine's shape and the lot's, and the world reproducing it is the point.
        }

        [Fact]
        public void ASeededWorldAndTheLotItSeedsDescribeTheSameMoment()
        {
            //The round trip of §5.2, stated as the activator's own result: seed a world, push the
            //time into a lot, and the lot must hold precisely what VMTS1Activator would have built
            //from the same six globals.
            var globals = Globals(17, 9, 30, 11, 3, 1999);
            var world = new WorldClock();
            world.SeedFromGlobalState(globals);

            var lot = new VMClock();
            world.PushTo(lot);

            Assert.Equal(globals[0], lot.Hours);
            Assert.Equal(globals[1], lot.DayOfMonth);
            Assert.Equal(globals[5], lot.Minutes);
            Assert.Equal(globals[6] * WorldClock.TicksPerMinuteTs1, lot.MinuteFractions);
            Assert.Equal(globals[7], lot.Month);
            Assert.Equal(globals[8], lot.Year);
        }

        [Fact]
        public void AShortGlobalStateArrayIsReadOnlyAsFarAsItGoes()
        {
            //A truncated SIMI, a corrupt save, or a caller passing a scratch array it meant to fill
            //with one word. Indices 0 and 1 are there and are used; 5..8 are not, and the fields
            //they would have written keep the values they already had rather than going to zero -
            //which is what "read only what the array has" has to mean to be of any use.
            var clock = new WorldClock();
            clock.SeedFromGlobalState(Globals(3, 4, 5, 6, 8, 1999));

            var truncated = new short[3];
            truncated[WorldClock.HoursGlobal] = 22;
            truncated[WorldClock.DayOfMonthGlobal] = 27;
            clock.SeedFromGlobalState(truncated);

            Assert.Equal(22, clock.Hours);
            Assert.Equal(27, clock.DayOfMonth);
            Assert.Equal(5, clock.Minutes);
            Assert.Equal(8, clock.Month);
            Assert.Equal(1999, clock.Year);
        }

        [Fact]
        public void AnArrayOfExactlyTheRightLengthIsCompleteEnough()
        {
            //YearGlobal + 1 is the smallest array that carries every time word, so the guard is a
            //>= rather than a > off by one.
            var clock = new WorldClock();
            clock.SeedFromGlobalState(Globals(6, 30, 15, 1, 12, 2021));
            var trimmed = new short[WorldClock.YearGlobal + 1];
            for (int i = 0; i < trimmed.Length; i++) trimmed[i] = 1;
            trimmed[WorldClock.HoursGlobal] = 6;
            trimmed[WorldClock.DayOfMonthGlobal] = 30;
            trimmed[WorldClock.MinutesGlobal] = 15;
            trimmed[WorldClock.MinuteFractionsGlobal] = 1;
            trimmed[WorldClock.MonthGlobal] = 12;
            trimmed[WorldClock.YearGlobal] = 2021;
            clock.SeedFromGlobalState(trimmed);

            Assert.Equal(6, clock.Hours);
            Assert.Equal(30, clock.DayOfMonth);
            Assert.Equal(15, clock.Minutes);
            Assert.Equal(12, clock.Month);
            Assert.Equal(2021, clock.Year);
        }

        [Fact]
        public void ANullGlobalStateArrayIsNoSeedAtAll()
        {
            var clock = new WorldClock();
            clock.SeedFromGlobalState(Globals(1, 2, 3, 4, 5, 1997));

            clock.SeedFromGlobalState(null);

            //a caller with nothing to seed is not an error, and it must not wipe the time either
            Assert.Equal(1, clock.Hours);
            Assert.Equal(1997, clock.Year);
        }

        [Fact]
        public void SeedingNeverWritesToTheArrayItRead()
        {
            //The globals are the engine's, and VMTS1Activator hands the same array to
            //VM.TS1State.SimulationInfo, which is written back out on save. A clock that normalised
            //what it read would be editing a save file's contents by way of reading the time.
            var globals = Globals(40, 99, 99, 99, 99, 1997);
            var before = (short[])globals.Clone();

            new WorldClock().SeedFromGlobalState(globals);

            Assert.Equal(before, globals);
        }

        // ---- pull / push ----------------------------------------------------------------------------

        [Fact]
        public void PullingTakesEveryTimeField()
        {
            var clock = new WorldClock();
            clock.PullFrom(new VMClock
            {
                Hours = 17,
                Minutes = 30,
                MinuteFractions = 15,
                TicksPerMinute = 30,
                DayOfMonth = 14,
                Month = 7,
                Year = 2001,
                Ticks = 4242
            });

            Assert.Equal(17, clock.Hours);
            Assert.Equal(30, clock.Minutes);
            Assert.Equal(15, clock.MinuteFractions);
            Assert.Equal(30, clock.TicksPerMinute);
            Assert.Equal(14, clock.DayOfMonth);
            Assert.Equal(7, clock.Month);
            Assert.Equal(2001, clock.Year);
            Assert.Equal(4242L, clock.Ticks);
        }

        [Fact]
        public void PushingWritesEveryTimeField()
        {
            var world = new WorldClock();
            world.PullFrom(new VMClock
            {
                Hours = 4, Minutes = 5, MinuteFractions = 6, TicksPerMinute = 30,
                DayOfMonth = 7, Month = 8, Year = 1999, Ticks = 100L
            });

            var lot = new VMClock();
            world.PushTo(lot);

            Assert.Equal(4, lot.Hours);
            Assert.Equal(5, lot.Minutes);
            Assert.Equal(6, lot.MinuteFractions);
            Assert.Equal(30, lot.TicksPerMinute);
            Assert.Equal(7, lot.DayOfMonth);
            Assert.Equal(8, lot.Month);
            Assert.Equal(1999, lot.Year);
            Assert.Equal(100L, lot.Ticks);
        }

        [Fact]
        public void PullFromOneLotAndPushToAnotherIsFieldForFieldIdentical()
        {
            var source = new VMClock
            {
                Hours = 23, Minutes = 59, MinuteFractions = 29, TicksPerMinute = 30,
                DayOfMonth = 30, Month = 12, Year = 1997, Ticks = 987654L
            };

            var world = new WorldClock();
            world.PullFrom(source);
            var destination = new VMClock();
            world.PushTo(destination);

            Assert.Equal(source.Hours, destination.Hours);
            Assert.Equal(source.Minutes, destination.Minutes);
            Assert.Equal(source.MinuteFractions, destination.MinuteFractions);
            Assert.Equal(source.TicksPerMinute, destination.TicksPerMinute);
            Assert.Equal(source.DayOfMonth, destination.DayOfMonth);
            Assert.Equal(source.Month, destination.Month);
            Assert.Equal(source.Year, destination.Year);
            Assert.Equal(source.Ticks, destination.Ticks);
        }

        [Fact]
        public void FirePercentIsTheLotsOwnAndThisClockNeverTouchesIt()
        {
            //A lot with almost no fire budget left, pulled into the world and pushed to another
            //lot. If the world kept FirePercent anywhere, the second lot would inherit a value that
            //was never its own - and the lot's fire primitives would be reading the neighbourhood's
            //memory of a different lot's fire.
            var source = new VMClock { FirePercent = 3 };
            var world = new WorldClock();
            world.PullFrom(source);

            var destination = new VMClock();
            world.PushTo(destination);

            Assert.Equal(20000, destination.FirePercent);   //its own default, untouched
            Assert.Equal(3, source.FirePercent);           //and the source is untouched too
        }

        [Fact]
        public void ThisClockHasNowhereToKeepALotFireBudgetOrAUtcAnchor()
        {
            //The structural half of the rule above: nothing named for the lot's fire or its UTC
            //anchor exists on the type, so there is nothing for a future change to start writing.
            foreach (var member in typeof(WorldClock).GetMembers(
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                Assert.DoesNotContain("FirePercent", member.Name, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("UTC", member.Name, StringComparison.OrdinalIgnoreCase);
            }
        }

        [Fact]
        public void NothingOnThisClockCanReachAVm_AndSoNothingCanWriteAGlobal()
        {
            //"Never writes VM.GlobalState" is enforced by shape rather than by review: the seed
            //reads an array, the other methods read and write a VMClock, and no member here takes
            //the one type that owns the globals.
            var names = new List<string>();
            foreach (var method in typeof(WorldClock).GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (method.IsSpecialName) continue;
                names.Add(method.Name);
                foreach (var parameter in method.GetParameters())
                {
                    Assert.True(parameter.ParameterType != typeof(VM),
                        "WorldClock." + method.Name + " takes a VM, and so can reach VM.GlobalState");
                }
            }

            names.Sort();
            Assert.Equal(
                new[] { "PullFrom", "PushTo", "SeedFromGlobalState", "Tick", "ToString" },
                names);   //the whole writable surface: seed, pull, push, tick. Nothing else.
        }

        [Fact]
        public void ANullLotClockIsAProgrammingErrorRatherThanMidnight()
        {
            //A caller with no lot is a bug on the lot-switch path, and silently reading as midnight
            //would move a neighbourhood's time by up to a day every time it happened.
            var world = new WorldClock();

            Assert.Throws<ArgumentNullException>(() => world.PullFrom(null));
            Assert.Throws<ArgumentNullException>(() => world.PushTo(null));
        }

        // ---- ticking -------------------------------------------------------------------------------
        //
        // The tick arithmetic these use, stated once because getting it wrong is the easy mistake:
        // TicksPerMinute is 30, so one tick is one-thirtieth of a minute - two seconds - and a
        // minute is 30 ticks, an hour is 1800, a day is 43200 and a TS1 year (360 days) is
        // 15,552,000.
        //
        // The cascade is also nested rather than parallel: the hour rolls into the day, the day
        // into the month and the month into the year, so a day cannot reach 31 without the hour
        // having rolled first. Every test below that moves the calendar therefore starts at 23:59,
        // and none of them can isolate one level of the cascade from the ones above it - which is
        // the other reason this clock delegates rather than re-implementing it.

        [Fact]
        public void AWorldClockThatHasNotBeenGivenATimeSaysSo()
        {
            //"Midnight" and "no clock" are different statements, and only the second one is true
            //before the world has read a time from anywhere.
            var clock = new WorldClock();
            Assert.False(clock.IsAvailable);

            clock.SeedFromGlobalState(Globals(0, 1, 0, 0, 1, 1997));
            Assert.True(clock.IsAvailable);
            Assert.Equal(0, clock.Hours);   //a clock that is available may legitimately read midnight
        }

        [Fact]
        public void OneTickCarriesTheMinuteOver()
        {
            var clock = At(5, 58, 29, 6, 6, 1997);

            clock.Tick();

            Assert.Equal(5, clock.Hours);
            Assert.Equal(59, clock.Minutes);
            Assert.Equal(0, clock.MinuteFractions);
        }

        [Fact]
        public void ThirtyTicksMakeAMinute()
        {
            var clock = At(5, 41, 0, 6, 6, 1997);

            Tick(clock, 30);

            Assert.Equal(5, clock.Hours);
            Assert.Equal(42, clock.Minutes);
            Assert.Equal(0, clock.MinuteFractions);
        }

        [Fact]
        public void EighteenHundredTicksMakeAnHour()
        {
            var clock = At(5, 0, 0, 6, 6, 1997);

            Tick(clock, 60 * 30);

            Assert.Equal(6, clock.Hours);
            Assert.Equal(0, clock.Minutes);
            Assert.Equal(0, clock.MinuteFractions);
        }

        [Fact]
        public void TwentyFourHoursCarryTheDay()
        {
            //A whole day on, at the same time of day - which is only true because the minute and
            //the hour divide the day evenly.
            var clock = At(12, 30, 0, 6, 6, 1997);

            Tick(clock, 24 * 60 * 30);

            Assert.Equal(12, clock.Hours);
            Assert.Equal(30, clock.Minutes);
            Assert.Equal(0, clock.MinuteFractions);
            Assert.Equal(7, clock.DayOfMonth);
            Assert.Equal(6, clock.Month);
            Assert.Equal(1997, clock.Year);
        }

        [Fact]
        public void TheLastMinuteOfTheDayCarriesTheHour()
        {
            var clock = At(23, 59, 29, 6, 6, 1997);

            clock.Tick();

            Assert.Equal(0, clock.Hours);
            Assert.Equal(0, clock.Minutes);
            Assert.Equal(7, clock.DayOfMonth);
            Assert.Equal(6, clock.Month);
            Assert.Equal(1997, clock.Year);
        }

        [Fact]
        public void ThirtyDaysMakeAMonth()
        {
            //Not 28, not 31: TS1's calendar is a flat thirty-day month and every month is the same
            //length. A world that rolled on a real calendar would disagree with every lot.
            var clock = At(23, 59, 29, 30, 6, 1997);

            clock.Tick();

            Assert.Equal(1, clock.DayOfMonth);
            Assert.Equal(7, clock.Month);
            Assert.Equal(1997, clock.Year);
        }

        [Fact]
        public void ThirtyDaysOfTicksMakeAMonth()
        {
            var clock = At(12, 30, 0, 1, 6, 1997);

            Tick(clock, 30 * 24 * 60 * 30);

            Assert.Equal(12, clock.Hours);
            Assert.Equal(30, clock.Minutes);
            Assert.Equal(1, clock.DayOfMonth);
            Assert.Equal(7, clock.Month);
            Assert.Equal(1997, clock.Year);
        }

        [Fact]
        public void TheLastDayOfTheLastMonthCarriesTheYear()
        {
            var clock = At(23, 59, 29, 30, 12, 1997);

            clock.Tick();

            Assert.Equal(0, clock.Hours);
            Assert.Equal(1, clock.DayOfMonth);
            Assert.Equal(1, clock.Month);
            Assert.Equal(1998, clock.Year);
        }

        [Fact]
        public void ThreeHundredAndSixtyDaysMakeAYear()
        {
            //The flat month again, at length: a leap-year rule or a 28-day February anywhere in
            //here would leave this world out of step with every lot after a year.
            var clock = At(13, 22, 7, 17, 9, 1997);

            Tick(clock, 360 * 24 * 60 * 30);

            Assert.Equal(13, clock.Hours);
            Assert.Equal(22, clock.Minutes);
            Assert.Equal(7, clock.MinuteFractions);
            Assert.Equal(17, clock.DayOfMonth);
            Assert.Equal(9, clock.Month);
            Assert.Equal(1998, clock.Year);
        }

        [Fact]
        public void TheLastTickOfTheLastDayOfTheYearCarriesEverythingAtOnce()
        {
            //The cascade in one tick: minute into hour into day into month into year, which is the
            //case a hand-rolled version of VMClock.Tick gets wrong most easily.
            var clock = At(23, 59, 29, 30, 12, 1997);

            clock.Tick();

            Assert.Equal(0, clock.Hours);
            Assert.Equal(0, clock.Minutes);
            Assert.Equal(0, clock.MinuteFractions);
            Assert.Equal(1, clock.DayOfMonth);
            Assert.Equal(1, clock.Month);
            Assert.Equal(1998, clock.Year);
        }

        [Fact]
        public void EveryTickIsCounted()
        {
            var clock = At(13, 22, 7, 17, 9, 1997);

            Tick(clock, 360 * 24 * 60 * 30);

            Assert.Equal(360L * 24L * 60L * 30L, clock.Ticks);
        }

        [Fact]
        public void EveryTickCountsEvenWhenTheMinuteDoesNotMove()
        {
            var clock = At(9, 15, 0, 6, 6, 1997);

            Tick(clock, 10);

            Assert.Equal(9, clock.Hours);
            Assert.Equal(15, clock.Minutes);
            Assert.Equal(10L, clock.Ticks);
        }

        [Fact]
        public void TickingTheWorldAndTickingTheLotGiveTheSameAnswer()
        {
            //The reason Tick delegates rather than carrying its own cascade: the world and a lot
            //advanced the same number of times have to be at the same time, everywhere.
            var world = At(23, 59, 29, 30, 12, 1997);
            var lot = new VMClock();
            world.PushTo(lot);

            Tick(world, 4321);
            for (int i = 0; i < 4321; i++) lot.Tick();

            Assert.Equal(lot.Hours, world.Hours);
            Assert.Equal(lot.Minutes, world.Minutes);
            Assert.Equal(lot.MinuteFractions, world.MinuteFractions);
            Assert.Equal(lot.DayOfMonth, world.DayOfMonth);
            Assert.Equal(lot.Month, world.Month);
            Assert.Equal(lot.Year, world.Year);
            Assert.Equal(lot.Ticks, world.Ticks);
        }

        [Fact]
        public void TickingTheWorldDoesNotTouchTheLotItWasTickedFrom()
        {
            var lot = new VMClock
            {
                Hours = 23, Minutes = 59, MinuteFractions = 29, TicksPerMinute = 30,
                DayOfMonth = 30, Month = 12, Year = 1997, Ticks = 5L
            };
            var world = new WorldClock();
            world.PullFrom(lot);

            Tick(world, 3);

            Assert.Equal(23, lot.Hours);
            Assert.Equal(59, lot.Minutes);
            Assert.Equal(29, lot.MinuteFractions);
            Assert.Equal(30, lot.DayOfMonth);
            Assert.Equal(12, lot.Month);
            Assert.Equal(1997, lot.Year);
            Assert.Equal(5L, lot.Ticks);
        }

        // ---- the derived numbers --------------------------------------------------------------------

        [Fact]
        public void SecondsAreTheSubMinutePositionAtTheLotsOwnRate()
        {
            //Halfway through a minute at the TS1 rate is thirty seconds, and the field is not
            //stored as seconds because the rate is the lot's own - see the next case.
            var clock = At(0, 0, 15, 6, 6, 1997);
            Assert.Equal(30, clock.Seconds);   //15 * 60 / 30

            //a lot running five ticks faster - the TSO rate VMContext assigns when Content does not
            //report TS1 - reaches the same point with three times the fraction, which is exactly
            //why Seconds is derived from TicksPerMinute rather than kept as a field
            var fast = new WorldClock();
            fast.PullFrom(new VMClock { TicksPerMinute = 150, MinuteFractions = 75 });
            Assert.Equal(75, fast.MinuteFractions);
            Assert.Equal(30, fast.Seconds);    //75 * 60 / 150
        }

        [Fact]
        public void ALotWithNoSubMinuteRateDoesNotDivideByZero()
        {
            //VMClock.Seconds would throw on this lot; the neighbourhood must not be what surfaces it
            var clock = new WorldClock();
            clock.PullFrom(new VMClock { Hours = 3, TicksPerMinute = 0, MinuteFractions = 9 });

            Assert.Equal(3, clock.Hours);
            Assert.Equal(0, clock.Seconds);
        }

        [Fact]
        public void AWorldClockThatHasNeverTickedReadsAsTheDateAVLotClockWould()
        {
            //A world that has never been told anything must read the same as a freshly constructed
            //VMClock, or "with Part B off nothing changes" would be a claim about a clock that
            //starts somewhere the lots do not.
            var world = new WorldClock();
            var lot = new VMClock();

            Assert.Equal(lot.DayOfMonth, world.DayOfMonth);
            Assert.Equal(lot.Month, world.Month);
            Assert.Equal(lot.Year, world.Year);
            Assert.Equal(0, lot.Hours);
            Assert.Equal(0, world.Hours);
            Assert.Equal(0L, world.Ticks);

            //the rate is the one difference, and it is the TS1 one: a context that has asked
            //Content for its TS1 flag has already assigned this, and a world that did not would
            //seed its sub-minute position against a rate of zero
            Assert.Equal(30, world.TicksPerMinute);
        }
    }
}
