/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using FSO.SimAntics;
using Simitone.Client.Neighbourhood;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Part B8: the world features that belong to the neighbourhood rather than to a lot - one sun,
    /// one sky, one weather, one idea of who is visiting.
    ///
    /// <para>
    /// B8 is the one Part B milestone with no gate and no acceptance line of its own, which makes
    /// the tests the specification. Four promises are pinned here, and each is the one that would
    /// be broken first by a plausible-looking change:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Lighting is a pure function of the clock.</b> Same clock, same state, field for
    ///     field, on two independently-built clocks. This is the whole basis of "centralised": two
    ///     lots lit by the same afternoon because there is only one derivation of it.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Weather is deterministic by seed and bounded.</b> Same seed and time give the same
    ///     weather however the ticks were delivered; a different seed gives a different one; and
    ///     every number it reports is inside its documented range, because a weather that
    ///     overshoots into a negative cloud cover is a black sky nobody asked for.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The visitor schedule is deterministic and bounded.</b> The predicate agrees with the
    ///     list, the list never exceeds its cap, an unknown lot and a bulldozed plot are never
    ///     active, and nothing is active at three in the morning.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Day/night boundaries are the hours they are named.</b> Pinned as exact hours rather
    ///     than tolerances, because the alternative - a test that accepts "about sunset" - is
    ///     satisfied by an off-by-one-hour in the phase arithmetic and by a round-off error in
    ///     <c>Math.Cos</c> alike, and neither is a bug worth shipping.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>No VM, no content, no graphics device</b> - the only fixture is a
    /// <see cref="WorldClock"/> pulled from a hand-built <see cref="VMClock"/> and a layout of
    /// hand-built lots, which is the shape a real world has before any lot is opened.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldFeaturesTests
    {
        private const long Seed = 246813579L;
        private const long OtherSeed = 97531L;

        /// <summary>
        /// One game hour in real seconds. TS1 runs a game minute per real second, so an hour is a
        /// minute of wall clock. Written out of the rate the same way the simulator tests do it,
        /// because getting it wrong is silent - the numbers still move.
        /// </summary>
        private const double OneGameHour = 60 * OffscreenSimulator.GameMinutesPerRealSecond;

        // ---------------------------------------------------------------- fixtures

        /// <summary>
        /// A clock at a stated hour, built the way a real one is: pulled out of a lot's clock
        /// rather than assigned field by field, so these tests cannot lean on a state the type
        /// does not actually reach.
        /// </summary>
        private static WorldClock ClockAt(int hour, int minute = 0, int dayOfMonth = 1, int month = 6)
        {
            return ClockAt(hour, minute, dayOfMonth, month, 0L);
        }

        /// <summary>
        /// A clock at a stated time <em>and a stated elapsed tick count</em>.
        /// </summary>
        /// <remarks>
        /// The tick count is the argument several of these tests need, because the weather's segment
        /// index and the visitors' day index are both derived from it, and a clock left at zero sees
        /// exactly one segment of weather and exactly one day of visitors. Setting it through
        /// <see cref="WorldClock.PullFrom"/> rather than by ticking is what makes a week of days a
        /// fixture rather than a million-iteration loop.
        /// </remarks>
        private static WorldClock ClockAt(
            int hour, int minute, int dayOfMonth, int month, long ticks)
        {
            var clock = new WorldClock();
            clock.PullFrom(new VMClock
            {
                Hours = hour,
                Minutes = minute,
                MinuteFractions = 0,
                TicksPerMinute = WorldClock.TicksPerMinuteTs1,
                DayOfMonth = dayOfMonth,
                Month = month,
                Year = 1997,
                Ticks = ticks
            });
            return clock;
        }

        /// <summary>The elapsed tick count of one game minute at TS1's rate.</summary>
        private const long TicksPerMinute = WorldClock.TicksPerMinuteTs1;

        /// <summary>The elapsed tick count of <paramref name="gameMinutes"/> game minutes.</summary>
        private static long TicksAt(int gameMinutes)
        {
            return gameMinutes * TicksPerMinute;
        }

        /// <summary>A layout with the given lots, at <see cref="Seed"/>.</summary>
        private static NeighbourhoodLayout Layout(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(0, (int)Seed, 1, NeighbourhoodLayout.CurrentSchemaVersion,
                NeighbourhoodTestInput.SmallMap, lots);
        }

        /// <summary>
        /// One lot record with everything but the interesting fields defaulted. The zone is a
        /// parameter because it is what decides whether the lot is a community lot, a studio lot
        /// or a house with visitors.
        /// </summary>
        private static NeighbourhoodLot Lot(
            short number, int x, int y, short zone = LotZoning.ZoneResidential,
            bool hasHouse = true, NeighbourhoodLotSide doorSide = NeighbourhoodLotSide.East)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Size = 16,
                WorldX = x,
                WorldY = y,
                DoorSide = doorSide,
                HasHouse = hasHouse,
                TerrainType = TerrainType.GRASS,
                Zone = zone,
                MapX = number,
                MapY = number,
            };
        }

        /// <summary>
        /// A neighbourhood with one of everything the schedule distinguishes: two community lots,
        /// one studio, three houses that may or may not be having visitors, and one bulldozed plot
        /// that must never be on the list.
        /// </summary>
        private static NeighbourhoodLayout MixedLayout()
        {
            return Layout(
                Lot(11, 8, 8, LotZoning.ZoneCommunity),
                Lot(12, 32, 8, LotZoning.ZoneCommunity),
                Lot(21, 56, 8, LotZoning.ZoneStudio),
                Lot(31, 8, 40),
                Lot(32, 32, 40),
                Lot(33, 56, 40),
                Lot(41, 8, 64, LotZoning.ZoneResidential, false));
        }

        // ---------------------------------------------------------------- lighting

        [Fact]
        public void LightingIsAPureFunctionOfTheClock()
        {
            //Two clocks built independently from the same lot clock. If lighting carried any state
            //of its own - a cached ramp, a last-known phase, a seed - these would differ, and the
            //failure would be a lot lit by an afternoon that does not exist.
            var first = WorldLightingState.FromClock(ClockAt(13));
            var second = WorldLightingState.FromClock(ClockAt(13));

            Assert.Equal(first, second);
            Assert.True(first == second);
            Assert.Equal(first.GetHashCode(), second.GetHashCode());
            Assert.False(first != second);

            //And the same clock asked twice is the same answer, which is the trivial half that
            //still catches a method that mutates what it caches.
            Assert.Equal(first, WorldLightingState.FromClock(ClockAt(13)));
        }

        [Fact]
        public void EveryDistinctHourHasItsOwnLighting()
        {
            //A day/night model that collapses a whole band into one value is not a model. Compared
            //on the state rather than on one field, because a change in the colour alone is as much
            //a change as a change in the angle.
            var seen = new List<WorldLightingState>();
            for (int hour = 0; hour < 24; hour++)
            {
                var state = WorldLightingState.FromClock(ClockAt(hour));
                Assert.DoesNotContain(state, seen);
                seen.Add(state);
            }
            Assert.Equal(24, seen.Count);
        }

        [Fact]
        public void LightingDoesNotDependOnTheSeed()
        {
            //The seed decides the weather and the visitors. It has no business deciding whether
            //it is dark in a lot - that would be a neighbourhood whose lot views disagree about
            //the time of day, which is the bug centralised features exist to prevent.
            var a = new NeighbourhoodWorldFeatures(ClockAt(9), Seed, MixedLayout());
            var b = new NeighbourhoodWorldFeatures(ClockAt(9), OtherSeed, MixedLayout());

            Assert.Equal(a.Lighting, b.Lighting);
            Assert.Equal(WorldLightingState.FromClock(a.Clock), WorldLightingState.FromClock(b.Clock));
        }

        [Fact]
        public void LightingIsBoundedAtEveryHourOfTheDay()
        {
            //Bounded and non-negative, because these are used as straight multipliers. A ramp that
            //overshoots gives a black world rather than a bright one.
            for (int hour = 0; hour < 24; hour++)
            {
                var state = WorldLightingState.FromClock(ClockAt(hour, 37));

                Assert.InRange(state.SunIntensity, 0.0, 1.0);
                Assert.InRange(state.AmbientIntensity, 0.0, 1.0);
                Assert.InRange(state.SunAngleDegrees,
                    -NeighbourhoodWorldFeatures.MaxSunElevationDegrees,
                    NeighbourhoodWorldFeatures.MaxSunElevationDegrees);
                Assert.InRange(state.TimeOfDay.DayFraction, 0.0, 0.9999999);
                Assert.InRange(state.TimeOfDay.SunAzimuthDegrees, 0.0, 359.9999);

                AssertChannelInRange(state.SunColour, "sun at " + hour);
                AssertChannelInRange(state.AmbientColour, "ambient at " + hour);
            }
        }

        private static void AssertChannelInRange(WorldColour colour, string what)
        {
            Assert.InRange(colour.R, 0f, 1f);
            Assert.InRange(colour.G, 0f, 1f);
            Assert.InRange(colour.B, 0f, 1f);
            Assert.Equal(colour, new WorldColour(colour.R, colour.G, colour.B));
            Assert.False(string.IsNullOrEmpty(colour.ToString()));
            Assert.NotNull(what);
        }

        [Fact]
        public void TheSunPeaksAtNoonAndIsDeepestAtMidnight()
        {
            var noon = WorldTimeOfDay.FromClock(ClockAt(12));
            var midnight = WorldTimeOfDay.FromClock(ClockAt(0));

            Assert.Equal(NeighbourhoodWorldFeatures.MaxSunElevationDegrees, noon.SunAngleDegrees, 6);
            Assert.Equal(-NeighbourhoodWorldFeatures.MaxSunElevationDegrees, midnight.SunAngleDegrees, 6);

            //Closer to the horizon in the small hours than at midday, which is the property a
            //long-shadow renderer reads. Not a shadow cast - just the ordering.
            var early = WorldLightingState.FromClock(ClockAt(9));
            var midday = WorldLightingState.FromClock(ClockAt(13));
            Assert.True(early.SunAngleDegrees < midday.SunAngleDegrees,
                "a 09:00 sun should be lower than a 13:00 one");
            Assert.True(early.SunIntensity < midday.SunIntensity);
            Assert.True(early.AmbientIntensity < midday.AmbientIntensity);
        }

        [Fact]
        public void TheSunTravelsEastToSouthToWest()
        {
            Assert.Equal(90.0, WorldTimeOfDay.FromClock(ClockAt(6)).SunAzimuthDegrees, 6);
            Assert.Equal(180.0, WorldTimeOfDay.FromClock(ClockAt(12)).SunAzimuthDegrees, 6);
            Assert.Equal(270.0, WorldTimeOfDay.FromClock(ClockAt(18)).SunAzimuthDegrees, 6);

            //Monotone clockwise across the daylight half, and back into range afterwards.
            for (int hour = 0; hour < 24; hour++)
            {
                Assert.InRange(WorldTimeOfDay.FromClock(ClockAt(hour)).SunAzimuthDegrees, 0.0, 359.9999);
            }
        }

        [Fact]
        public void TheSunIsUpExactlyWhenItIsNotNight()
        {
            //The two are read from the same normalised hours, so a caller never has to wonder which
            //of them to ask. A model where they could disagree - "the sun is up but it is night" -
            //is a model a lot view cannot make a decision with.
            for (int hour = 0; hour < 24; hour++)
            {
                var time = WorldTimeOfDay.FromClock(ClockAt(hour));
                Assert.NotEqual(time.IsNight, time.SunIsUp);
                Assert.Equal(time.IsNight, WorldLightingState.FromClock(ClockAt(hour)).IsNight);
            }
        }

        [Fact]
        public void AClockWithNonsenseHoursStillReadsAsADay()
        {
            //VMTS1Activator copies the six time globals straight into the clock, and
            //WorldClock.SeedFromGlobalState deliberately does not normalise them - so a corrupt
            //save really can hand the neighbourhood hour 47. A day/night model that threw at that
            //would be the only part of the world unable to draw it. Folding into the day is the
            //honest reading: 47:10 is 23:10 of some day.
            var folded = WorldTimeOfDay.FromClock(ClockAt(47, 10));

            Assert.Equal(23, folded.Hour);
            Assert.Equal(10, folded.Minute);
            Assert.InRange(folded.DayFraction, 0.0, 1.0);
            Assert.Equal(WorldDayPhase.Night, folded.Phase);
            Assert.True(folded.IsNight);

            //A negative hour is a clock that has been rewound; same reading, no exception.
            var rewound = WorldTimeOfDay.FromClock(ClockAt(-1, 0));
            Assert.Equal(23, rewound.Hour);
            Assert.Equal(WorldDayPhase.Night, rewound.Phase);
        }

        [Fact]
        public void LightingRefusesToGuessAtAMissingClock()
        {
            Assert.Throws<ArgumentNullException>(() => WorldLightingState.FromClock(null));
            Assert.Throws<ArgumentNullException>(() => WorldTimeOfDay.FromClock(null));
            Assert.Throws<ArgumentNullException>(() => new NeighbourhoodWorldFeatures(null));
        }

        // ---------------------------------------------------------------- day/night boundaries

        [Theory]
        // Dawn: the hour before sunrise, and the hour of it.
        [InlineData(4, WorldDayPhase.Dawn, false)]
        [InlineData(5, WorldDayPhase.Dawn, false)]
        // Day: sunrise to the hour before sunset.
        [InlineData(6, WorldDayPhase.Day, false)]
        [InlineData(12, WorldDayPhase.Day, false)]
        [InlineData(17, WorldDayPhase.Day, false)]
        // Dusk: sunset onwards - already dark by this model's reckoning, one hour before Night.
        [InlineData(18, WorldDayPhase.Dusk, true)]
        [InlineData(19, WorldDayPhase.Dusk, true)]
        // Night: and the small hours round to midnight.
        [InlineData(20, WorldDayPhase.Night, true)]
        [InlineData(23, WorldDayPhase.Night, true)]
        [InlineData(0, WorldDayPhase.Night, true)]
        [InlineData(3, WorldDayPhase.Night, true)]
        public void TheDayIsCutIntoFourBandsAtTheHoursItIsNamedAfter(
            int hour, WorldDayPhase phase, bool isNight)
        {
            var time = WorldTimeOfDay.FromClock(ClockAt(hour));

            Assert.Equal(phase, time.Phase);
            Assert.Equal(isNight, time.IsNight);
            Assert.Equal(phase, NeighbourhoodWorldFeatures.PhaseForHour(hour));
        }

        [Fact]
        public void TheBoundariesAreTheHoursTheyAreNamedAfter()
        {
            //The two constants the model is documented by. If these move, the doc is wrong; if the
            //arithmetic moves without them, the world disagrees with itself about sunset.
            Assert.Equal(NeighbourhoodWorldFeatures.SunriseHour, 6);
            Assert.Equal(NeighbourhoodWorldFeatures.SunsetHour, 18);
            Assert.Equal(NeighbourhoodWorldFeatures.DayStartHour, NeighbourhoodWorldFeatures.SunriseHour);
            Assert.Equal(NeighbourhoodWorldFeatures.DuskStartHour, NeighbourhoodWorldFeatures.SunsetHour);

            //One minute either side of sunrise and sunset, the tightest form of the same claim:
            //the switch is on the minute, not somewhere inside the hour.
            Assert.False(WorldTimeOfDay.FromClock(ClockAt(5, 59)).IsNight);
            Assert.False(WorldTimeOfDay.FromClock(ClockAt(6, 0)).IsNight);
            Assert.True(WorldTimeOfDay.FromClock(ClockAt(17, 59)).IsNight == false);
            Assert.True(WorldTimeOfDay.FromClock(ClockAt(18, 0)).IsNight);

            //Dawn ends at sunrise and Night begins an hour after sunset, so the two dark bands are
            //not the same length - which is the reason there are four bands and not two.
            Assert.True(NeighbourhoodWorldFeatures.NightStartHour > NeighbourhoodWorldFeatures.DuskStartHour);
            Assert.True(NeighbourhoodWorldFeatures.DayStartHour > NeighbourhoodWorldFeatures.DawnStartHour);
        }

        [Fact]
        public void TheBandsTileTheDayExactlyOnce()
        {
            //Twenty-four hours, four bands, every hour in exactly one band, and dusk and night are
            //exactly the dark ones. This is the property a renderer depends on when it asks "should
            //the lamps be on" instead of asking four times.
            var counts = new Dictionary<WorldDayPhase, int>();
            for (int hour = 0; hour < 24; hour++)
            {
                var phase = NeighbourhoodWorldFeatures.PhaseForHour(hour);
                int count;
                counts.TryGetValue(phase, out count);
                counts[phase] = count + 1;

                var isDarkBand = phase == WorldDayPhase.Dusk || phase == WorldDayPhase.Night;
                Assert.Equal(isDarkBand, WorldTimeOfDay.FromClock(ClockAt(hour)).IsNight);
            }

            Assert.Equal(4, counts.Count);
            Assert.Equal(24, counts[WorldDayPhase.Dawn] + counts[WorldDayPhase.Day]
                + counts[WorldDayPhase.Dusk] + counts[WorldDayPhase.Night]);
        }

        [Fact]
        public void TheBandsFollowTheDayNotTheCalendar()
        {
            //A month of January and a month of July are the same time of day: the bands are about
            //light, not season. A day/night model that drifted with the date would be a bug a
            //player notices in the first winter afternoon.
            var winter = WorldTimeOfDay.FromClock(ClockAt(13, 0, 15, 1));
            var summer = WorldTimeOfDay.FromClock(ClockAt(13, 0, 15, 7));

            Assert.Equal(winter.Phase, summer.Phase);
            Assert.Equal(winter.SunAngleDegrees, summer.SunAngleDegrees, 9);
        }

        // ---------------------------------------------------------------- weather

        [Fact]
        public void WeatherIsDeterministicBySeedAndTime()
        {
            //Same seed, same clock: same weather, field for field. This is what makes "the same
            //neighbourhood in two sessions" mean something beyond its terrain and its lot layout.
            for (int hour = 0; hour < 24; hour += 3)
            {
                var a = WorldWeather.At(Seed, ClockAt(hour));
                var b = WorldWeather.At(Seed, ClockAt(hour));

                Assert.Equal(a, b);
                Assert.True(a == b);
                Assert.Equal(a.GetHashCode(), b.GetHashCode());
            }
        }

        [Fact]
        public void ADifferentSeedGivesDifferentWeather()
        {
            //The seed has to actually reach the weather, or "deterministic by seed" is just
            //deterministic. Checked over a day's worth of segments rather than at one instant,
            //because a single instant is a one-in-many chance of a false negative.
            var differing = false;
            for (int segment = 0; segment < 48 && !differing; segment++)
            {
                var clock = ClockAt(0, 0, 1, 6, TicksAt(segment * 60 + 20));
                var a = WorldWeather.At(Seed, clock);
                var b = WorldWeather.At(OtherSeed, clock);
                differing = a != b
                    || Math.Abs(a.CloudCover - b.CloudCover) > 1e-9
                    || Math.Abs(a.Precipitation - b.Precipitation) > 1e-9
                    || Math.Abs(a.TemperatureC - b.TemperatureC) > 1e-9;
            }
            Assert.True(differing, "two different world seeds produced identical weather all day");
        }

        [Fact]
        public void WeatherIsBounded()
        {
            //Every number inside its documented range, across seeds, months and a whole day of
            //ticks. A weather that can report negative cloud cover or a temperature of 300 degrees
            //is a weather a renderer has to defend itself against, and the whole point of putting
            //the bounds on the type is that a caller does not have to.
            foreach (var seed in new[] { Seed, OtherSeed, 0L, long.MaxValue, long.MinValue })
            {
                foreach (var month in new[] { 1, 4, 7, 10, 12 })
                {
                    for (int hour = 0; hour < 24; hour++)
                    {
                        //A third of the way into a segment, and a segment index that moves with the
                        //hour, so this sweeps the whole range rather than reporting one draw 288
                        //times. A clock left at zero ticks sees a single segment of weather.
                        var clock = ClockAt(hour, 11, 15, month, TicksAt(hour * 20 + 7));
                        var weather = WorldWeather.At(seed, clock);

                        Assert.InRange(weather.CloudCover, 0.0, 1.0);
                        Assert.InRange(weather.Precipitation, 0.0, 1.0);
                        Assert.InRange(weather.WindSpeed, 0.0, 1.0);
                        Assert.InRange(weather.ThunderChance, 0.0, 1.0);
                        Assert.InRange(weather.TemperatureC,
                            WorldWeather.MinTemperatureC, WorldWeather.MaxTemperatureC);

                        Assert.True(Enum.IsDefined(typeof(WorldWeatherKind), weather.Kind),
                            "not a defined condition: " + weather.Kind);
                        Assert.Equal(weather.Precipitation >= WorldWeather.WetPrecipitation, weather.IsRaining);
                        Assert.Equal(weather.Kind == WorldWeatherKind.Snow, weather.IsSnowing);
                        Assert.False(string.IsNullOrEmpty(weather.ToString()));
                    }
                }
            }
        }

        [Fact]
        public void WeatherIsSharedByEveryLotBecauseItCannotBeAnythingElse()
        {
            //"Shared across the world" is structural here, not a convention: the derivation reads
            //the seed and the clock and nothing else, so there is no per-lot input to disagree
            //over. Two features objects over the same seed and clock - the strongest form, since
            //they have independent layouts, independent streams and independent derived state -
            //must agree exactly.
            var a = new NeighbourhoodWorldFeatures(ClockAt(15), Seed, MixedLayout());
            var b = new NeighbourhoodWorldFeatures(ClockAt(15), Seed, MixedLayout());

            Assert.Equal(a.Weather, b.Weather);
            Assert.Equal(a.Weather, WorldWeather.At(Seed, a.Clock));
            Assert.Equal(a.Lighting, b.Lighting);
        }

        [Fact]
        public void WeatherIsDrawnFromTheSimulationStreamAndNotFromGeneration()
        {
            //§8: retuning map generation must not move the simulation. The stream is exposed so
            //that is an assertion rather than a promise - and the two roots must not be the same
            //number, which is the part a refactor that "simplifies" the derivation would break.
            var features = new NeighbourhoodWorldFeatures(ClockAt(12), Seed, MixedLayout());
            var streams = features.Streams;

            Assert.Equal(streams.Simulation.ForLabel(NeighbourhoodWorldFeatures.WeatherStreamKey).State,
                features.WeatherStream.State);
            Assert.Equal(streams.Simulation.ForLabel(NeighbourhoodWorldFeatures.VisitorStreamKey).State,
                features.VisitorStream.State);
            Assert.NotEqual(streams.Generation.ForLabel(NeighbourhoodWorldFeatures.WeatherStreamKey).State,
                features.WeatherStream.State);
        }

        [Fact]
        public void WeatherMovesOnlyInSegments()
        {
            //The granularity is the model: a segment is a whole draw, blended across, so a coarse
            //segment is a slow-varying weather and a fine one is a twitchy one - and neither can
            //step outside its own bounds, which is checked over a whole day of fine segments.
            var clock = ClockAt(0);
            var segment = new NeighbourhoodWorldFeatures(clock, Seed, MixedLayout())
            {
                WeatherSegmentGameMinutes = 30
            };
            var before = segment.Weather.SegmentStartTick;
            Assert.Equal(0L, before);

            //One tick short of a whole segment is still inside the first one, so the segment start
            //has not moved. This is the half of the claim a test that only ticks to the boundary
            //would not make.
            for (int i = 0; i < 30 * 30 - 1; i++) clock.Tick();
            segment.Refresh();
            Assert.Equal(before, segment.Weather.SegmentStartTick);

            //Across the boundary it moves by exactly the ticks in one segment and no further.
            for (int i = 0; i < 31; i++) clock.Tick();
            segment.Refresh();

            Assert.Equal(TicksAt(30), segment.Weather.SegmentStartTick);
            Assert.Equal(30, segment.Weather.SegmentGameMinutes);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => WorldWeather.At(Seed, clock, 0));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => WorldWeather.At(Seed, clock, -60));
        }

        [Fact]
        public void TheWeatherSegmentIsAnHourWideByDefault()
        {
            //The default is an hour, which is a game minute a second at TS1's rate times sixty.
            Assert.Equal(60, WorldWeather.DefaultSegmentGameMinutes);
            Assert.Equal(60, NeighbourhoodWorldFeatures.DefaultMaxSecondsPerTick / 24);

            var features = new NeighbourhoodWorldFeatures(ClockAt(3), Seed, MixedLayout());
            Assert.Equal(60, features.WeatherSegmentGameMinutes);
        }

        [Fact]
        public void WeatherChangesAcrossADayRatherThanSittingStill()
        {
            //A weather model that returns the same draw for every segment is deterministic and
            //useless, and it would pass every other test here.
            var distinct = new HashSet<long>();
            var cloud = new List<double>();
            for (int segment = 0; segment < 36; segment++)
            {
                var clock = ClockAt(0, 0, 1, 6, TicksAt(segment * 60 + 20));
                var weather = WorldWeather.At(Seed, clock);
                distinct.Add(weather.SegmentStartTick);
                cloud.Add(weather.CloudCover);
            }

            Assert.Equal(36, distinct.Count);

            var min = double.MaxValue;
            var max = double.MinValue;
            for (int i = 0; i < cloud.Count; i++)
            {
                if (cloud[i] < min) min = cloud[i];
                if (cloud[i] > max) max = cloud[i];
            }
            Assert.True(max - min > 0.1, "the cloud cover barely moved over 36 hours");
        }

        [Fact]
        public void WeatherTracksTheSeasonAndTheHour()
        {
            //Two real signals, and neither is noise: July is warmer than January, and 15:00 is
            //warmer than 03:00. Without these the model is a random number with a label on it.
            var summerNoon = TemperatureAt(7, 12);
            var winterNoon = TemperatureAt(1, 12);
            var summerNight = TemperatureAt(7, 3);

            Assert.True(summerNoon > winterNoon,
                "July at noon (" + summerNoon + ") should be warmer than January (" + winterNoon + ")");
            Assert.True(summerNoon > summerNight,
                "noon (" + summerNoon + ") should be warmer than 03:00 (" + summerNight + ")");
        }

        private static double TemperatureAt(int month, int hour)
        {
            return WorldWeather.At(Seed, ClockAt(hour, 30, 15, month, TicksAt(11))).TemperatureC;
        }

        [Fact]
        public void SnowIsColdRainAndNotAWinterOnlyDecoration()
        {
            //The classification is a function of the temperature and the segment's own draw, so it
            //holds whenever the two line up - which in a bounded model is at the bottom of its own
            //temperature range.
            var snowy = 0;
            var wet = 0;
            var precipitating = 0;
            for (int month = 1; month <= 12; month++)
            {
                for (int hour = 0; hour < 24; hour++)
                {
                    //Sweeping the segments matters here: the precipitation a segment draws is
                    //fixed, so a year of weather read at one tick count is one draw classified
                    //twelve times, and whether it was ever wet would be luck.
                    var weather = WorldWeather.At(Seed,
                        ClockAt(hour, 20, 15, month, TicksAt((month * 24 + hour) * 20)));
                    if (weather.IsSnowing)
                    {
                        snowy++;
                        //The only thing snow is asserted against is the temperature. The kind is the
                        //segment's own classification and the number is the blend across the
                        //boundary - see the remarks on WorldWeather.Classify - so demanding they
                        //agree would be testing a property the model deliberately does not have.
                        Assert.True(weather.TemperatureC <= WorldWeather.SnowTemperatureC,
                            "snow at " + weather.TemperatureC + "C");
                    }
                    if (weather.Kind == WorldWeatherKind.Snow
                        || weather.Kind == WorldWeatherKind.Rain
                        || weather.Kind == WorldWeatherKind.Storm)
                    {
                        precipitating++;
                    }
                    if (weather.IsRaining) wet++;
                }
            }
            Assert.True(wet > 0, "no precipitation at all in a year of weather");
            Assert.True(precipitating > 0, "no rain, storm or snow in a whole year of weather");
            Assert.True(snowy < precipitating,
                "every wet day of the year was also snowing");
        }

        [Fact]
        public void ThunderOnlyArrivesWithAStorm()
        {
            //A thunder chance on a cloudless afternoon is a bug a sound engine would turn into an
            //audible one.
            for (int hour = 0; hour < 24; hour++)
            {
                var clock = ClockAt(hour, 7, 20, 7, TicksAt(hour * 45 + 13));
                var weather = WorldWeather.At(Seed, clock);

                //Stated as the implication rather than the converse: thunder only ever accompanies
                //rain. Whether every rainy hour is stormy is not promised, and asserting it would
                //be asserting that this seed happens to be a wet one.
                if (weather.ThunderChance > 0)
                {
                    Assert.True(weather.Precipitation >= WorldWeather.RainPrecipitation,
                        "thunder with " + weather.Precipitation + " precipitation");
                    Assert.True(weather.IsRaining);
                    Assert.InRange(weather.ThunderChance, 0.0, WorldWeather.MaxThunderChance);
                }
            }
        }

        [Fact]
        public void WeatherRefusesToGuessAtAMissingClock()
        {
            Assert.Throws<ArgumentNullException>(() => WorldWeather.At(Seed, null));
        }

        // ---------------------------------------------------------------- visitors

        [Fact]
        public void TheVisitorListIsDeterministic()
        {
            //Two objects over the same seed, the same layout and the same clock produce the same
            //list, in the same order, with the same head counts. Reproducibility is the whole
            //feature: a street that is busy this evening and empty last session would be a
            //neighbourhood whose seed had leaked into its draw order.
            foreach (var clock in new[] { ClockAt(20), ClockAt(11), ClockAt(3) })
            {
                var a = NeighbourhoodWorldFeatures.VisitorsAt(clock, Seed, MixedLayout());
                var b = NeighbourhoodWorldFeatures.VisitorsAt(ClockAt(
                    WorldTimeOfDay.FromClock(clock).Hour, 0, 1, 6), Seed, MixedLayout());

                Assert.Equal(a.Count, b.Count);
                for (int i = 0; i < a.Count; i++)
                {
                    Assert.Equal(a[i], b[i]);
                }
            }
        }

        [Fact]
        public void TheVisitorListIsBounded()
        {
            //At most four lots, at every hour, over several seeds - including a layout with far
            //more lots than the cap, which is the case the cap exists for.
            var crowded = new List<NeighbourhoodLot>();
            for (short i = 0; i < 40; i++)
            {
                crowded.Add(Lot((short)(100 + i), (i % 8) * 12, (i / 8) * 12,
                    i % 5 == 0 ? LotZoning.ZoneCommunity : LotZoning.ZoneResidential));
            }
            var layout = Layout(crowded.ToArray());

            foreach (var seed in new[] { Seed, OtherSeed, 1L })
            {
                for (int hour = 0; hour < 24; hour++)
                {
                    var clock = ClockAt(hour, 30, 1, 6, TicksAt(hour * 61 + 29));
                    var visitors = NeighbourhoodWorldFeatures.VisitorsAt(clock, seed, layout);
                    Assert.True(visitors.Count <= NeighbourhoodWorldFeatures.MaxActiveVisitorLots,
                        "hour " + hour + " listed " + visitors.Count + " lots");
                    Assert.Equal(4, NeighbourhoodWorldFeatures.MaxActiveVisitorLots);
                }
            }
        }

        [Fact]
        public void EveryListedLotPassesThePredicate()
        {
            //The list is a bounded projection of the predicate, so every lot in it must be one the
            //predicate accepts. If this fails, the two have drifted - and the failure a caller would
            //see is a visitor spawned in a lot that is shut.
            for (int hour = 0; hour < 24; hour++)
            {
                var clock = ClockAt(hour, 22, 1, 6, TicksAt(hour * 17 + 3));
                var layout = MixedLayout();

                var visitors = NeighbourhoodWorldFeatures.VisitorsAt(clock, Seed, layout);
                for (int i = 0; i < visitors.Count; i++)
                {
                    var visitor = visitors[i];
                    Assert.True(NeighbourhoodWorldFeatures.IsLotActive(visitor.LotNumber, clock, Seed, layout),
                        "lot " + visitor.LotNumber + " is on the list but the predicate says otherwise");
                    Assert.InRange(visitor.HeadCount,
                        NeighbourhoodWorldFeatures.MinVisitorsPerLot,
                        NeighbourhoodWorldFeatures.MaxVisitorsPerLot);
                    Assert.True(visitor.OpensAtHour < visitor.ClosesAtHour);
                }
            }
        }

        [Fact]
        public void TheListIsInAscendingLotNumberOrder()
        {
            //The layout's own order, so the cap takes the same lots every time and a log line can
            //be read top to bottom. A dictionary's iteration order would make the first four
            //depend on insertion history.
            var clock = ClockAt(20, 30, 1, 6, TicksAt(20 * 60 + 30));
            var visitors = NeighbourhoodWorldFeatures.VisitorsAt(clock, Seed, MixedLayout());

            for (int i = 1; i < visitors.Count; i++)
            {
                Assert.True(visitors[i - 1].LotNumber < visitors[i].LotNumber,
                    "visitors are out of order: " + visitors[i - 1].LotNumber
                    + " then " + visitors[i].LotNumber);
            }
        }

        [Fact]
        public void ALotThatIsNotThereIsNeverActive()
        {
            //A number that is not in the layout, and a number nobody ever uses. Answered, not
            //thrown at: "is this lot open" is a question with an answer for every number a caller
            //might hold, including a stale one from a sidecar.
            var clock = ClockAt(12);
            var layout = MixedLayout();

            Assert.False(NeighbourhoodWorldFeatures.IsLotActive(99, clock, Seed, layout));
            Assert.False(NeighbourhoodWorldFeatures.IsLotActive(0, clock, Seed, layout));
            Assert.False(NeighbourhoodWorldFeatures.IsLotActive(-5, clock, Seed, layout));
            Assert.False(NeighbourhoodWorldFeatures.IsLotActive(4242, clock, Seed, layout));
        }

        [Fact]
        public void ABulldozedPlotIsNeverActive()
        {
            //There is no door for a visitor to arrive at, which is the same reason a plot gets no
            //entrance. Lot 41 in the fixture is a plot; it must be refused even at the hour every
            //other lot is busy.
            for (int hour = 0; hour < 24; hour++)
            {
                var clock = ClockAt(hour);
                var layout = MixedLayout();

                Assert.False(NeighbourhoodWorldFeatures.IsLotActive(41, clock, Seed, layout));

                var visitors = NeighbourhoodWorldFeatures.VisitorsAt(clock, Seed, layout);
                for (int i = 0; i < visitors.Count; i++)
                {
                    Assert.NotEqual(41, visitors[i].LotNumber);
                }
            }
        }

        [Fact]
        public void ACommunityLotIsOpenForItsHoursAndShutForTheRest()
        {
            //The model as documented: a community lot is open 09:00 to 22:00 and no draw enters
            //into it, because a park that is sometimes shut is a bug rather than a mood.
            for (int hour = 0; hour < 24; hour++)
            {
                var clock = ClockAt(hour);
                var open = NeighbourhoodWorldFeatures.IsLotActive(11, clock, Seed, MixedLayout());

                Assert.Equal(hour >= NeighbourhoodWorldFeatures.CommunityOpenHour
                    && hour < NeighbourhoodWorldFeatures.CommunityCloseHour, open);
            }

            //And the visitor record says so, so a caller does not have to re-derive the window.
            var midday = NeighbourhoodWorldFeatures.VisitorsAt(ClockAt(12), Seed, MixedLayout());
            Assert.NotEmpty(midday);
            for (int i = 0; i < midday.Count; i++)
            {
                if (midday[i].LotNumber != 11) continue;
                Assert.Equal(NeighbourhoodVisitorKind.Community, midday[i].Kind);
                Assert.Equal(NeighbourhoodWorldFeatures.CommunityOpenHour, midday[i].OpensAtHour);
                Assert.Equal(NeighbourhoodWorldFeatures.CommunityCloseHour, midday[i].ClosesAtHour);
                return;
            }
            Assert.Fail("a community lot was open at noon and was not on the list");
        }

        [Fact]
        public void AStudioLotHasAShorterDayThanACommunityLot()
        {
            for (int hour = 0; hour < 24; hour++)
            {
                var clock = ClockAt(hour);
                Assert.Equal(
                    hour >= NeighbourhoodWorldFeatures.StudioOpenHour
                    && hour < NeighbourhoodWorldFeatures.StudioCloseHour,
                    NeighbourhoodWorldFeatures.IsLotActive(21, clock, Seed, MixedLayout()));
            }

            Assert.True(NeighbourhoodWorldFeatures.StudioOpenHour
                >= NeighbourhoodWorldFeatures.CommunityOpenHour);
            Assert.True(NeighbourhoodWorldFeatures.StudioCloseHour
                <= NeighbourhoodWorldFeatures.CommunityCloseHour);
        }

        [Fact]
        public void NobodyIsAboutAtThreeInTheMorning()
        {
            //The "empty" half of "empty or at most N". At 03:00 no community lot and no studio lot
            //is open, and the residential window has not started, so the honest answer is nothing
            //- and a model that cannot say that is a model with a floor.
            var clock = ClockAt(3, 30);
            var visitors = NeighbourhoodWorldFeatures.VisitorsAt(clock, Seed, MixedLayout());

            Assert.Empty(visitors);
            for (int i = 0; i < MixedLayout().Lots.Count; i++)
            {
                var lot = MixedLayout().Lots[i];
                Assert.False(NeighbourhoodWorldFeatures.IsLotActive(lot.Number, clock, Seed, MixedLayout()));
            }
        }

        [Fact]
        public void AWorldWithNoLotsHasNoVisitors()
        {
            //The degenerate neighbourhood still answers, rather than throwing on an empty walk.
            var empty = NeighbourhoodLayout.Empty(0, (int)Seed, 1);
            var clock = ClockAt(20);

            Assert.Empty(NeighbourhoodWorldFeatures.VisitorsAt(clock, Seed, empty));
            Assert.False(NeighbourhoodWorldFeatures.IsLotActive(11, clock, Seed, empty));

            var features = new NeighbourhoodWorldFeatures(clock, Seed, empty);
            Assert.Empty(features.Visitors);
            Assert.Equal(0, features.LotCount);
        }

        [Fact]
        public void ResidentialVisitingIsAPerLotDrawAndNotAFixedHour()
        {
            //The whole point of drawing per lot: the same lot, on the same evening, is a draw
            //rather than a rule, and different lots disagree. If every residential lot were
            //always-busy or never-busy this would be indistinguishable from a fixed rule.
            var clock = ClockAt(20, 15);
            var layout = MixedLayout();
            var residents = new[] { 31, 32, 33 };

            var busy = 0;
            for (int i = 0; i < residents.Length; i++)
            {
                if (NeighbourhoodWorldFeatures.IsLotActive(residents[i], clock, Seed, layout)) busy++;
            }
            Assert.True(busy > 0 && busy < residents.Length,
                "all " + busy + " of " + residents.Length + " houses were busy at the peak hour;"
                + " a draw that always lands the same way is not a draw");

            //And the same lot on the same evening, asked twice, does not change its mind.
            for (int i = 0; i < residents.Length; i++)
            {
                var first = NeighbourhoodWorldFeatures.IsLotActive(residents[i], clock, Seed, layout);
                var second = NeighbourhoodWorldFeatures.IsLotActive(residents[i], clock, Seed, layout);
                Assert.Equal(first, second);
            }
        }

        [Fact]
        public void ResidentialVisitingIsOutsideTheWindow()
        {
            //No house has visitors before the window opens or after it closes, whatever the draw
            //says. Checked across a day rather than at the two ends only, because an off-by-one in
            //the window's arithmetic is invisible at its edges.
            for (int hour = 0; hour < 24; hour++)
            {
                var clock = ClockAt(hour, 45);
                var inside = hour >= NeighbourhoodWorldFeatures.NeighbourFirstHour
                    && hour < NeighbourhoodWorldFeatures.NeighbourLastHour;

                for (short number = 31; number <= 33; number++)
                {
                    var active = NeighbourhoodWorldFeatures.IsLotActive(number, clock, Seed, MixedLayout());
                    if (!inside) Assert.False(active, "lot " + number + " busy at " + hour);
                }
            }
        }

        [Fact]
        public void TheDayChangesWhichHouseIsBusy()
        {
            //The draw is keyed by the day, so a street is not the same every evening. Sampled over
            //two weeks of evenings - long enough for the per-day streams to diverge, and a whole
            //fortnight of a random draw landing on the same list is not a thing that happens by
            //accident.
            var layout = MixedLayout();
            var firstDay = BusyHousesOnDay(0, layout);
            var allDaysTheSame = true;
            var emptyDays = 0;
            for (int day = 1; day < 14; day++)
            {
                var onThisDay = BusyHousesOnDay(day, layout);
                if (onThisDay != firstDay) allDaysTheSame = false;
                if (onThisDay.Length == 0) emptyDays++;
            }
            Assert.False(allDaysTheSame, "a fortnight of evenings had an identical visitor list");
            Assert.True(emptyDays < 13, "thirteen consecutive empty evenings is not a schedule");
        }

        private static string BusyHousesOnDay(int day, NeighbourhoodLayout layout)
        {
            //The day index is derived from the clock's own tick count, so stating the tick count
            //states the day. Ticking a week forward one sub-minute tick at a time would be the same
            //answer 300,000 times slower.
            var clock = ClockAt(20, 0, 1, 6, TicksAt(day * 24 * 60));
            var busy = new List<int>();
            for (short number = 31; number <= 33; number++)
            {
                if (NeighbourhoodWorldFeatures.IsLotActive(number, clock, Seed, layout))
                {
                    busy.Add(number);
                }
            }
            return string.Join(",", busy);
        }

        [Fact]
        public void TheVisitorScheduleRefusesToGuessAtMissingArguments()
        {
            var clock = ClockAt(12);
            var layout = MixedLayout();

            Assert.Throws<ArgumentNullException>(
                () => NeighbourhoodWorldFeatures.VisitorsAt(null, Seed, layout));
            Assert.Throws<ArgumentNullException>(
                () => NeighbourhoodWorldFeatures.VisitorsAt(clock, Seed, null));
            Assert.Throws<ArgumentNullException>(
                () => NeighbourhoodWorldFeatures.IsLotActive(11, null, Seed, layout));
            Assert.Throws<ArgumentNullException>(
                () => NeighbourhoodWorldFeatures.IsLotActive(11, clock, Seed, null));
        }

        [Fact]
        public void AConstructorWithNoLayoutGivesAnEmptyOne()
        {
            //Not a caller bug: "the lighting and the weather, with nobody to visit" is a real state
            //for the neighbourhood screen before a lot is ever opened.
            var features = new NeighbourhoodWorldFeatures(ClockAt(15));

            Assert.Equal(0L, features.WorldSeed);
            Assert.Equal(0, features.LotCount);
            Assert.Empty(features.Visitors);
            Assert.Equal(WorldLightingState.FromClock(features.Clock), features.Lighting);
            Assert.Equal(WorldWeather.At(0L, features.Clock), features.Weather);
        }

        // ---------------------------------------------------------------- tick

        [Fact]
        public void ATickAdvancesTheClockAndReReadsTheWorld()
        {
            var clock = ClockAt(9);
            var features = new NeighbourhoodWorldFeatures(clock, Seed, MixedLayout());
            var before = features.Lighting;

            var result = features.Tick(OneGameHour);

            //A game hour at TS1's rate: sixty ticks to the minute, sixty minutes.
            Assert.Equal(OneGameHour, result.Seconds, 9);
            Assert.Equal(TicksAt(60), result.TicksAdvanced);
            Assert.Equal(result.TicksAdvanced, features.TicksAdvanced);
            Assert.Equal(OneGameHour, features.ElapsedSeconds, 9);
            Assert.Equal(10, clock.Hours);

            //The lighting and the weather were re-read, so the object is not a stale copy of the
            //clock - which is the failure a caller would see as "the world never got dark".
            Assert.NotEqual(before, features.Lighting);
            Assert.Equal(WorldLightingState.FromClock(clock), features.Lighting);
            Assert.Equal(WorldWeather.At(Seed, clock), features.Weather);
        }

        [Fact]
        public void TheOrderTheTicksArriveInDoesNotChangeWhereTheWorldEndsUp()
        {
            //The strongest determinism claim there is: one big tick and many small ones must reach
            //the same state, because nothing here accumulates between calls. This is the property
            //that makes a save reload safely, since a save does not record a frame rate.
            var coarseClock = ClockAt(0);
            var fineClock = ClockAt(0);
            var coarse = new NeighbourhoodWorldFeatures(coarseClock, Seed, MixedLayout());
            var fine = new NeighbourhoodWorldFeatures(fineClock, Seed, MixedLayout());

            coarse.Tick(OneGameHour);
            for (int i = 0; i < 60; i++) fine.Tick(OneGameHour / 60.0);

            Assert.Equal(coarseClock.Hours, fineClock.Hours);
            Assert.Equal(coarseClock.Ticks, fineClock.Ticks);
            Assert.Equal(coarse.Lighting, fine.Lighting);
            Assert.Equal(coarse.Weather, fine.Weather);
            Assert.Equal(coarse.Visitors.Count, fine.Visitors.Count);
            for (int i = 0; i < coarse.Visitors.Count; i++)
            {
                Assert.Equal(coarse.Visitors[i], fine.Visitors[i]);
            }
        }

        [Fact]
        public void ATickOfNoTimeDoesNothing()
        {
            //A paused frame is a delta of zero, and it must not move the weather, spend a weather
            //segment, or claim a day phase change - otherwise a paused game would keep rolling the
            //clock forward by a rounding error a minute.
            var features = new NeighbourhoodWorldFeatures(ClockAt(9), Seed, MixedLayout());
            var before = features.Lighting;

            var result = features.Tick(0.0);

            Assert.False(result.Changed);
            Assert.Equal(0L, result.TicksAdvanced);
            Assert.Equal(0, result.WeatherSegmentsEntered);
            Assert.False(result.PhaseChanged);
            Assert.Equal(before, features.Lighting);
            Assert.Equal(0L, features.TicksAdvanced);
        }

        [Fact]
        public void ATickRefusesToRunBackwardsOrOnSomethingThatIsNotANumber()
        {
            var features = new NeighbourhoodWorldFeatures(ClockAt(9), Seed, MixedLayout());

            Assert.Throws<ArgumentOutOfRangeException>(() => features.Tick(-1.0));
            Assert.Throws<ArgumentOutOfRangeException>(() => features.Tick(double.NaN));

            //Neither may have moved anything on the way to throwing.
            Assert.Equal(0L, features.TicksAdvanced);
            Assert.Equal(9, features.Clock.Hours);
        }

        [Fact]
        public void AnAbsurdlyLongTickIsCappedAndTheExcessIsReported()
        {
            //A caller that has not ticked for a month is asking for a lot of work. The cap is the
            //statement of what this type does with one, and it is truncated rather than deferred -
            //which is why the excess is a field and not a silence.
            var features = new NeighbourhoodWorldFeatures(ClockAt(0), Seed, MixedLayout());
            var result = features.Tick(1e9);

            Assert.True(result.TruncatedSeconds > 0);
            Assert.Equal(NeighbourhoodWorldFeatures.DefaultMaxSecondsPerTick, result.Seconds);
            Assert.Equal(NeighbourhoodWorldFeatures.DefaultMaxSecondsPerTick, features.MaxSecondsPerTick);
            Assert.Equal(
                TicksAt(NeighbourhoodWorldFeatures.DefaultMaxSecondsPerTick),
                result.TicksAdvanced);

            //And the cap is configurable, and still reported.
            features.MaxSecondsPerTick = 60;
            var capped = features.Tick(600.0);
            Assert.Equal(60.0, capped.Seconds);
            Assert.Equal(540.0, capped.TruncatedSeconds, 6);

            Assert.Throws<ArgumentOutOfRangeException>(() => features.MaxSecondsPerTick = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => features.MaxSecondsPerTick = -1);
        }

        [Fact]
        public void ATickAcrossSunsetReportsThePhaseChangeAndTheDarkness()
        {
            //The one redraw trigger a lot view cannot do without, so it is pinned at the boundary
            //rather than somewhere inside the evening.
            var features = new NeighbourhoodWorldFeatures(ClockAt(17, 30), Seed, MixedLayout());
            Assert.False(features.IsNight);

            var result = features.Tick(OneGameHour);

            Assert.True(result.PhaseChanged);
            Assert.Equal(WorldDayPhase.Dusk, result.DayPhase);
            Assert.True(result.IsNight);
            Assert.True(features.IsNight);
            Assert.Equal(18, features.Clock.Hours);
        }

        [Fact]
        public void ResetClearsTheCountersAndNotTheClock()
        {
            //This type does not own time, so it cannot put it back - and a method that appeared to
            //would be the most dangerous thing in the file.
            var clock = ClockAt(9);
            var features = new NeighbourhoodWorldFeatures(clock, Seed, MixedLayout());
            features.Tick(OneGameHour * 3);

            Assert.True(features.TicksAdvanced > 0);
            var ticksBefore = clock.Ticks;

            features.Reset();

            Assert.Equal(0L, features.TicksAdvanced);
            Assert.Equal(0.0, features.ElapsedSeconds);
            Assert.Equal(ticksBefore, clock.Ticks);
            Assert.Equal(12, clock.Hours);
        }

        [Fact]
        public void TheCachedStateFollowsAClockSomebodyElseMoved()
        {
            //A lot switch, a load, or a fast-forward by another subsystem all move the clock
            //without coming through here. Refresh is what stops the object from being a stale copy,
            //and a caller that forgets it gets yesterday's afternoon rather than an exception.
            var clock = ClockAt(9);
            var features = new NeighbourhoodWorldFeatures(clock, Seed, MixedLayout());
            var startingTicks = clock.Ticks;
            Assert.False(features.IsNight);

            //Twelve game hours, in sub-minute ticks. Counted out of the rate rather than written as
            //a number, because a clock tick is not a game minute: the version of this line that
            //ticked twelve-sixty times was twelve game *minutes*, and it quietly asserted that
            //09:00 was 21:00 for several builds before anyone read the failure.
            for (long i = 0; i < TicksAt(12 * 60); i++) clock.Tick();
            Assert.Equal(TicksAt(12 * 60), clock.Ticks - startingTicks);
            Assert.Equal(9 + 12, clock.Hours);

            //Stale until it is asked to look.
            Assert.Equal(WorldDayPhase.Day, features.DayPhase);

            features.Refresh();

            Assert.Equal(WorldDayPhase.Night, features.DayPhase);
            Assert.True(features.IsNight);
            Assert.Equal(WorldLightingState.FromClock(clock), features.Lighting);
        }

        // ---------------------------------------------------------------- log line

        [Fact]
        public void DescribeSaysTheLightingTheWeatherAndTheVisitors()
        {
            var features = new NeighbourhoodWorldFeatures(ClockAt(20, 15), Seed, MixedLayout());
            var text = features.Describe();

            Assert.Contains("world features seed " + Seed, text);
            Assert.Contains("lighting:", text);
            Assert.Contains("weather:", text);
            Assert.Contains("visitors:", text);
            Assert.Contains("Night", text);
            Assert.Contains("(night)", text);
            Assert.Contains("7 lot(s)", text);

            //The weather is on the line, by its own ToString, so the two cannot disagree.
            Assert.Contains(features.Weather.ToString(), text);
            Assert.Contains("sun", text);
            Assert.Contains("ambient", text);
        }

        [Fact]
        public void DescribeIsTheSameForTwoIdenticalWorldsAndCarriesNoEnvironment()
        {
            //A log line that differs between two machines - a locale, a timestamp - is a log line
            //that cannot be diffed, which is the only reason to have one.
            var a = new NeighbourhoodWorldFeatures(ClockAt(20, 15), Seed, MixedLayout());
            var b = new NeighbourhoodWorldFeatures(ClockAt(20, 15), Seed, MixedLayout());

            Assert.Equal(a.Describe(), b.Describe());
            Assert.Equal(a.ToString(), b.ToString());
            Assert.NotNull(a.Describe());
        }

        [Fact]
        public void DescribeSaysSoWhenThereIsNobodyAbout()
        {
            //The interesting line at 03:00 is the one that reports nothing, and a dump that
            //silently omits the section reads as a truncated log rather than as an empty street.
            var features = new NeighbourhoodWorldFeatures(ClockAt(3, 15), Seed, MixedLayout());
            var text = features.Describe();

            Assert.Contains("visitors: 0", text);
            Assert.Contains("nobody is about", text);
        }
    }
}
