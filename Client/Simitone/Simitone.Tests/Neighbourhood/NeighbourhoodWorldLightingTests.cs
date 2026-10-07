/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using FSO.SimAntics;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Part B8's wiring: the lot view's lighting adapter, and the world that owns B8's features.
    ///
    /// <para>
    /// Three promises, and each is the one a plausible-looking change breaks first.
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>The adapter is a pure function of the features' own state.</b> The same
    ///     <see cref="WorldLightingState"/> and <see cref="WorldWeather"/> give the same
    ///     <see cref="NeighbourhoodWorldLighting"/>, field for field, however many times and in
    ///     whatever order they are asked for. That is the whole basis of "one sun for the
    ///     neighbourhood": two lot views showing the same afternoon are the same numbers twice, not
    ///     two derivations that happen to agree.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Deterministic by seed and by frame rate.</b> Two worlds over one seed reach the same
    ///     weather and the same lighting, and so does one world ticked in a single long frame as
    ///     another ticked in ten short ones - because everything here is a function of the
    ///     neighbourhood clock and not of how the ticks were delivered.
    ///   </description></item>
    ///   <item><description>
    ///     <b>With the capability off, B8 does not exist.</b> No features object is constructed, no
    ///     feature tick is counted, the reading properties answer their defaults without building
    ///     anything, and the world's own line for the log is unchanged. §7's "off ⇒ the previous
    ///     stage's behaviour" as a property of the branch rather than a promise about it.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>No VM, no content, no graphics device and no renderer.</b> The adapter produces plain
    /// numbers - which is the reason it lives in this namespace and not in <c>tso.world</c> - so
    /// every assertion below is arithmetic over a <see cref="WorldClock"/> and a layout. Nothing
    /// here would still pass with a <c>GraphicsDevice</c> in the picture, and that is the point: the
    /// screen applies these values, and the screen is what needs a device.
    /// </para>
    ///
    /// <para>
    /// <b>How the weather is kept out of the way.</b> B8's weather is drawn from the seed, so a test
    /// cannot simply assert "at noon the sun's strength is one" - unless it goes looking for a seed
    /// whose noon is clear. Two helpers do that: <see cref="ClearestAt"/> and
    /// <see cref="CloudiestAt"/> search a run of seeds for the least and most cloud at a stated
    /// hour. Everything that compares two hours uses <em>one</em> seed, so the weather cancels in
    /// the comparison instead of being searched for.
    /// </para>
    ///
    /// <para>
    /// <b>Why this class is in the non-parallel collection.</b> Same reason as every other test that
    /// moves a capability switch: they are process-wide and cached in a static, and this class turns
    /// <see cref="NeighbourhoodWorldFlag.World"/> on. The constructor pins all seven off rather
    /// than reading the machine's <c>simitone.ini</c>, and <see cref="Dispose"/> drops the cache
    /// entirely.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class NeighbourhoodWorldLightingTests : IDisposable
    {
        private const int Slot = 5;
        private const int Seed = 13579;
        private const int OtherSeed = 24680;

        /// <summary>A small map, for the same reason the world tests use a small one.</summary>
        private const int MapSize = 96;

        /// <summary>
        /// One game hour in real seconds. Sixty, not 3600: at TS1's rate a real second is a game
        /// minute, so getting this wrong is silent - the numbers still move, at sixtieth the rate.
        /// </summary>
        private const double OneGameHour = 60 * OffscreenSimulator.GameMinutesPerRealSecond;

        /// <summary>How many seeds <see cref="ClearestAt"/> and <see cref="CloudiestAt"/> look at.</summary>
        private const int SeedSearch = 128;

        public NeighbourhoodWorldLightingTests()
        {
            NeighbourhoodWorldFlags.AllOff();
        }

        public void Dispose()
        {
            NeighbourhoodWorldFlags.Reset();
        }

        // ---------------------------------------------------------------- fixtures

        /// <summary>
        /// A clock at a stated hour, built the way a real one is - pulled out of a lot's clock
        /// rather than assigned field by field, so these tests cannot lean on a state the type does
        /// not reach.
        /// </summary>
        private static WorldClock ClockAt(int hour, int minute = 0, int dayOfMonth = 1, int month = 6)
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
                Ticks = 0L
            });
            return clock;
        }

        private static NeighbourhoodLayout Layout(int seed = Seed)
        {
            return NeighbourhoodLayout.Empty(Slot, seed, NeighbourhoodLayoutBuilder.CurrentGeneratorVersion);
        }

        private static SimitoneNeighbourhoodWorld World(bool enabled = true, int seed = Seed)
        {
            return new SimitoneNeighbourhoodWorld(Layout(seed), NeighbourhoodMap.Create(MapSize), enabled);
        }

        private static void Capability(NeighbourhoodWorldFlag flag, bool on)
        {
            NeighbourhoodWorldFlags.Set(flag, on, save: false);
        }

        /// <summary>
        /// B8's features over a stated hour and seed, with no lots. The three-argument form is the
        /// one that takes a seed, and these tests are about the weather rather than the visitors.
        /// </summary>
        private static NeighbourhoodWorldFeatures FeaturesAt(int hour, int minute, long seed)
        {
            return new NeighbourhoodWorldFeatures(ClockAt(hour, minute), seed, null);
        }

        /// <summary>The adapter's answer for a stated hour, over one seed.</summary>
        private static NeighbourhoodWorldLighting At(int hour, int minute = 0, long seed = Seed)
        {
            var features = FeaturesAt(hour, minute, seed);
            return NeighbourhoodWorldLighting.From(features.Lighting, features.Weather);
        }

        /// <summary>
        /// The least cloud of <see cref="SeedSearch"/> seeds at a stated hour, and the most. The
        /// weather is drawn per segment from the seed, so a run of seeds is a run of independent
        /// draws and the extremes of it are close enough to clear and to solid overcast for a test
        /// to say something definite.
        /// </summary>
        private static NeighbourhoodWorldLighting ClearestAt(int hour, int minute = 0)
        {
            return ExtremeAt(hour, minute, lowest: true);
        }

        /// <summary>The most cloud of <see cref="SeedSearch"/> seeds at a stated hour.</summary>
        private static NeighbourhoodWorldLighting CloudiestAt(int hour, int minute = 0)
        {
            return ExtremeAt(hour, minute, lowest: false);
        }

        private static NeighbourhoodWorldLighting ExtremeAt(int hour, int minute, bool lowest)
        {
            var best = At(hour, minute, Seed);
            for (var i = 1; i < SeedSearch; i++)
            {
                var candidate = At(hour, minute, Seed + i);
                if (lowest ? candidate.Overcast < best.Overcast : candidate.Overcast > best.Overcast)
                {
                    best = candidate;
                }
            }
            return best;
        }

        private static double Length(double x, double y, double z)
        {
            return Math.Sqrt(x * x + y * y + z * z);
        }

        // ---------------------------------------------------------------- the adapter is a pure function

        [Fact]
        public void TheAdapterIsAPureFunctionOfTheFeaturesState()
        {
            var features = FeaturesAt(17, 20, Seed);
            var afternoon = NeighbourhoodWorldLighting.From(features.Lighting, features.Weather);

            //the same two values, three times, with other work in between to catch anything that
            //cached or accumulated between calls
            var first = NeighbourhoodWorldLighting.From(features.Lighting, features.Weather);
            var night = At(1, seed: OtherSeed);
            var second = NeighbourhoodWorldLighting.From(features.Lighting, features.Weather);
            var third = NeighbourhoodWorldLighting.From(features.Lighting, features.Weather);

            Assert.Equal(afternoon, first);
            Assert.Equal(afternoon, second);
            Assert.Equal(afternoon, third);
            Assert.Equal(afternoon.GetHashCode(), second.GetHashCode());
            Assert.NotEqual(afternoon, night);
        }

        [Fact]
        public void TwoIndependentlyDerivedStatesGiveTheSameLighting()
        {
            //The claim "centralised" actually rests on: two objects built separately, which know
            //nothing about each other, must agree - or two lot views are two opinions.
            var here = FeaturesAt(9, 30, Seed);
            var there = FeaturesAt(9, 30, Seed);

            Assert.NotSame(here, there);
            Assert.Equal(here.Lighting, there.Lighting);
            Assert.Equal(here.Weather, there.Weather);
            Assert.Equal(
                NeighbourhoodWorldLighting.From(here.Lighting, here.Weather),
                NeighbourhoodWorldLighting.From(there.Lighting, there.Weather));
        }

        [Fact]
        public void TheAdapterAnswersForDefaultStateRatherThanThrowing()
        {
            //A world whose features were never built hands over default for both arguments, and the
            //first frame of a lot load must not be the frame that throws.
            var lighting = NeighbourhoodWorldLighting.From(default, default);

            //default reads as solar midnight with no cloud: a moon at its peak, the night's own sky
            //tint, and a clear horizon. All of that is a decision, and this is where it is pinned.
            Assert.False(lighting.IsSunUp);
            Assert.Equal(NeighbourhoodWorldLighting.MaxMoonElevationDegrees, lighting.SunElevationDegrees);
            Assert.Equal(NeighbourhoodWorldLighting.MoonIntensityScale, lighting.SunIntensity);
            Assert.Equal(0.0, lighting.AmbientIntensity);
            Assert.Equal(0.0, lighting.Overcast);
            Assert.Equal(NeighbourhoodWorldLighting.NightSkyTint, lighting.SkyTint);
            Assert.Equal(NeighbourhoodWorldLighting.ClearFogDistance, lighting.FogDistance);
            Assert.Equal(1.0, Length(lighting.SunDirectionX, lighting.SunDirectionY, lighting.SunDirectionZ), 9);
        }

        [Fact]
        public void TheWorldReportsTheAdapterOverItsOwnFeatures()
        {
            Capability(NeighbourhoodWorldFlag.World, on: true);
            var world = World();
            world.Tick(OneGameHour * 5);

            Assert.Equal(
                NeighbourhoodWorldLighting.From(world.Lighting, world.Weather),
                world.CurrentLighting);
        }

        // ---------------------------------------------------------------- the mapping itself

        [Fact]
        public void NoonIsTheBrightestAndTheHighestOfTheDay()
        {
            //One seed throughout, so the weather is the same number in all three and cancels.
            var morning = At(9);
            var noon = At(12);
            var evening = At(15);

            //the noon elevation is the features' own 70 degrees, lifted towards the zenith by cloud
            Assert.InRange(noon.SunElevationDegrees,
                NeighbourhoodWorldFeatures.MaxSunElevationDegrees,
                NeighbourhoodWorldLighting.OvercastPeakElevationDegrees);
            Assert.True(morning.SunElevationDegrees < noon.SunElevationDegrees);
            Assert.True(evening.SunElevationDegrees < noon.SunElevationDegrees);

            //The sun's own strength is FLAT across the middle of the day, and that is the claim: the
            //disc's height term saturates once the sun is a few degrees up, so a model that dipped
            //at noon would be a model dimming the day in the middle. The elevation is what carries
            //the hour, and the fill light is what ramps the whole day.
            Assert.Equal(morning.SunIntensity, noon.SunIntensity);
            Assert.Equal(noon.SunIntensity, evening.SunIntensity);
            Assert.True(noon.AmbientIntensity > morning.AmbientIntensity);
            Assert.True(noon.AmbientIntensity > evening.AmbientIntensity);

            //and the noon fill is the features' own maximum, less whatever the cloud took off it
            Assert.InRange(noon.AmbientIntensity,
                WorldLightingState.MaxAmbientIntensity * (1.0 - NeighbourhoodWorldLighting.OvercastAmbientCut),
                WorldLightingState.MaxAmbientIntensity);
        }

        [Fact]
        public void TheSunContributesNothingAtAllWhileItIsOnTheHorizon()
        {
            //The disc's height term is read from the sun's own elevation and not from the one cloud
            //lifts it to, so an overcast sunrise is still a sunrise. This is the assertion that
            //would fail first if the two were ever collapsed back into one number.
            var onTheHorizon = At(NeighbourhoodWorldFeatures.SunriseHour);
            var quarterPast = At(NeighbourhoodWorldFeatures.SunriseHour, minute: 15);

            Assert.True(onTheHorizon.SunIntensity < 1e-9, "a disc on the horizon writes no light");
            Assert.True(quarterPast.SunIntensity > onTheHorizon.SunIntensity, "and a rising one does");
            Assert.True(ClearestAt(7).SunIntensity > 0.9, "by seven it is a full-strength sun");
        }

        [Fact]
        public void TheSunIsEastAtSunriseSouthAtNoonAndWestAtSunset()
        {
            //The lot view's frame: +X east, +Z south. A compass bearing of 90 becomes an azimuth of
            //0, and the whole of that is LotViewAzimuthOffsetDegrees.
            var dawn = At(NeighbourhoodWorldFeatures.SunriseHour);
            var noon = At(12);
            var dusk = At(NeighbourhoodWorldFeatures.SunsetHour);

            Assert.Equal(0.0, dawn.SunAzimuthDegrees, 6);
            Assert.Equal(90.0, noon.SunAzimuthDegrees, 6);
            Assert.Equal(180.0, dusk.SunAzimuthDegrees, 6);

            //The components, which hold at any elevation the model can produce - the sign of an
            //east/west or a north/south bearing does not depend on how high the disc is, and a
            //test that wanted it to would be testing the cloud cover of a particular seed.
            Assert.True(dawn.SunDirectionX > 0.0, "the sun rises in the east");
            Assert.True(dusk.SunDirectionX < 0.0, "and sets in the west");
            Assert.True(noon.SunDirectionZ > 0.0, "and is south at noon");
            Assert.True(dawn.SunDirectionZ <= 0.0, "and rises due east, not south of it");
        }

        [Fact]
        public void TheSunDirectionIsAlwaysAUnitVectorPointingUp()
        {
            for (var hour = 0; hour < 24; hour++)
            {
                var lighting = At(hour);

                Assert.Equal(1.0,
                    Length(lighting.SunDirectionX, lighting.SunDirectionY, lighting.SunDirectionZ), 9);
                Assert.True(lighting.SunDirectionY >= 0.0, "the disc is never below the skyline at " + hour);
                Assert.InRange(lighting.SunAzimuthDegrees, 0.0, 360.0);
                Assert.InRange(lighting.SunIntensity, 0.0, 1.0);
                Assert.InRange(lighting.AmbientIntensity, 0.0, 1.0);
                Assert.InRange(lighting.Overcast, 0.0, 1.0);
                Assert.InRange(lighting.Haze, 0.0, 1.0);
                Assert.InRange(lighting.Visibility, 0.0, 1.0);
                Assert.InRange(lighting.FogDistance, NeighbourhoodWorldLighting.ObscuredFogDistance,
                    NeighbourhoodWorldLighting.ClearFogDistance);
            }
        }

        [Fact]
        public void NightIsAMoonAndNotASun()
        {
            //The features type's own sun crosses the horizon at 06:00 and 18:00, so everything
            //between those is a moon - including B8's dusk band, which the features type calls dark
            //from 18:00 while its trigonometry calls the sun down from 18:00 too.
            var midnight = At(0);
            var dusk = At(19);

            Assert.False(midnight.IsSunUp);
            Assert.True(midnight.IsNight);
            Assert.False(dusk.IsSunUp);
            Assert.True(dusk.IsNight);

            //The two questions part in the dawn band, and this is the hour they part in: the sun
            //is still down at five in the morning, but the lamps have not been on since four.
            var dawn = At(5);
            Assert.False(dawn.IsSunUp);
            Assert.False(dawn.IsNight);
            Assert.Equal(WorldDayPhase.Dawn, dawn.Phase);
        }

        [Fact]
        public void TheMoonStaysBelowTheSunAndLightsLessThanIt()
        {
            var midnight = ClearestAt(0);
            var noon = ClearestAt(12);

            Assert.True(midnight.SunElevationDegrees <= NeighbourhoodWorldLighting.MaxMoonElevationDegrees);
            Assert.True(noon.SunElevationDegrees > midnight.SunElevationDegrees);
            Assert.True(noon.SunIntensity > midnight.SunIntensity);
            //a dimmer disc of its own colour, not the sun's colour at a smaller number
            Assert.NotEqual(noon.SunColour, midnight.SunColour);
        }

        [Fact]
        public void TheSkyIsWarmAtTheHorizonsAndNeutralAtNoonAndBlueAtNight()
        {
            //The clearest sky the model produces at each of four hours. Even that is not a
            //cloudless one, so the tints are compared rather than pinned to the constants: what is
            //being claimed is the shape of the day, not one seed's weather. The two horizon hours
            //are seven and seventeen rather than five and nineteen because those are the hours the
            //sun is actually *up* and low - at five the sun is still below the horizon and the model
            //is already drawing a night sky.
            var dawn = ClearestAt(7);
            var noon = ClearestAt(12);
            var dusk = ClearestAt(17);
            var midnight = ClearestAt(0);

            Assert.True(dawn.SkyTint.R > dawn.SkyTint.B, "a sunrise is warmer than it is blue");
            Assert.True(dusk.SkyTint.R > dusk.SkyTint.B, "a sunset too");
            Assert.True(midnight.SkyTint.B > midnight.SkyTint.R, "and the night is the other way round");

            //brightest at noon, the two horizons in between, darkest over the moonlit value
            Assert.True(Luminance(noon.SkyTint) > Luminance(dusk.SkyTint));
            Assert.True(Luminance(dusk.SkyTint) > Luminance(midnight.SkyTint));
            Assert.True(Luminance(noon.SkyTint) > Luminance(dawn.SkyTint));

            //and the night one is the model's own constant, within a hair of it
            Assert.InRange(Luminance(midnight.SkyTint),
                Luminance(NeighbourhoodWorldLighting.NightSkyTint) * 0.5,
                Luminance(NeighbourhoodWorldLighting.NightSkyTint) * 1.5);
        }

        [Fact]
        public void TheSkyStripRunsSunriseToSunsetAndThenOnIntoTheNight()
        {
            var dawn = At(NeighbourhoodWorldFeatures.SunriseHour);
            var noon = At(12);
            var dusk = At(NeighbourhoodWorldFeatures.SunsetHour);
            var midnight = At(0);

            Assert.InRange(dawn.SkyStrip, NeighbourhoodWorldLighting.SunriseStrip,
                NeighbourhoodWorldLighting.SunsetStrip);
            Assert.True(dawn.SkyStrip < noon.SkyStrip);
            Assert.True(noon.SkyStrip < dusk.SkyStrip);
            Assert.InRange(midnight.SkyStrip, NeighbourhoodWorldLighting.SunsetStrip,
                NeighbourhoodWorldLighting.NightStrip);

            //and never off either end of the texture, at any hour of any seed
            for (var hour = 0; hour < 24; hour++)
            {
                Assert.InRange(At(hour).SkyStrip, NeighbourhoodWorldLighting.SunriseStrip,
                    NeighbourhoodWorldLighting.NightStrip);
            }
        }

        [Fact]
        public void ACloudyNoonIsDimmerThanAClearNoonInEveryWay()
        {
            var clear = ClearestAt(12);
            var cloudy = CloudiestAt(12);

            Assert.True(cloudy.Overcast > clear.Overcast, "the search found two different skies");
            Assert.True(cloudy.SunIntensity < clear.SunIntensity);
            Assert.True(cloudy.AmbientIntensity < clear.AmbientIntensity);
            Assert.True(cloudy.Haze > clear.Haze);

            //Cloud cuts the sun much harder than the fill, because a cloudy sky is a lid over the
            //sun and a lid over the whole dome. Pinning the ordering is the claim; the numbers are
            //the features type's to move.
            Assert.True(NeighbourhoodWorldLighting.OvercastSunCut > NeighbourhoodWorldLighting.OvercastAmbientCut);
        }

        [Fact]
        public void ACloudySkyIsGreyedRatherThanBrightened()
        {
            //The level-preserving shift, stated as a test because getting it wrong is invisible at
            //noon and glaring at midnight: a plain lerp towards the grey would make a solid overcast
            //night several times brighter than a clear one.
            var clearNight = ClearestAt(0);
            var cloudyNight = CloudiestAt(0);

            Assert.True(Luminance(cloudyNight.AmbientColour) <= Luminance(clearNight.AmbientColour) + 0.02,
                "cloud must not raise the fill light's level");
            Assert.True(Luminance(cloudyNight.SkyTint) <= Luminance(clearNight.SkyTint) + 0.02,
                "nor the sky's");
        }

        [Fact]
        public void DescribeIsLocaleIndependentAndNamesEveryPart()
        {
            var text = At(18, 30).Describe();

            Assert.Contains("lot view lighting", text);
            Assert.Contains("disc:", text);
            Assert.Contains("fill:", text);
            Assert.Contains("air:", text);
            Assert.Contains("sky:", text);
            //Built from fields and invariant formats: two machines asked the same question produce
            //the same text, which is what makes it worth diffing.
            Assert.Equal(text, At(18, 30).Describe());
            Assert.Contains("elev", At(18, 30).ToString());
        }

        private static double Luminance(WorldColour colour)
        {
            return 0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B;
        }

        // ---------------------------------------------------------------- determinism by seed

        [Fact]
        public void TheSameSeedAndTimeGiveTheSameWeatherAndTheSameLighting()
        {
            var a = FeaturesAt(14, 0, Seed);
            var b = FeaturesAt(14, 0, Seed);

            Assert.Equal(a.Weather, b.Weather);
            Assert.Equal(
                NeighbourhoodWorldLighting.From(a.Lighting, a.Weather),
                NeighbourhoodWorldLighting.From(b.Lighting, b.Weather));
        }

        [Fact]
        public void AWorldReachesTheSameLightingHoweverItsTicksWereDelivered()
        {
            //One game hour in one frame, then one game hour in ten frames. The deltas divide exactly
            //into whole sub-minute ticks, so this is the frame-rate claim and not a claim about
            //truncation - and the features read the clock rather than keeping a count, so it is a
            //property of the wiring rather than of the arithmetic.
            Capability(NeighbourhoodWorldFlag.World, on: true);

            var oneCall = World();
            oneCall.Tick(OneGameHour);

            var tenCalls = World();
            for (var i = 0; i < 10; i++) tenCalls.Tick(OneGameHour / 10.0);

            Assert.Equal(oneCall.Clock.Ticks, tenCalls.Clock.Ticks);
            Assert.Equal(oneCall.Weather, tenCalls.Weather);
            Assert.Equal(oneCall.CurrentLighting, tenCalls.CurrentLighting);
        }

        [Fact]
        public void TwoWorldsOverOneSeedAgreeAndTheSeedIsActuallyRead()
        {
            Capability(NeighbourhoodWorldFlag.World, on: true);

            var a = World(seed: Seed);
            var b = World(seed: Seed);
            var different = World(seed: OtherSeed);

            a.Tick(OneGameHour * 8);
            b.Tick(OneGameHour * 8);
            different.Tick(OneGameHour * 8);

            Assert.Equal(a.Weather, b.Weather);
            Assert.Equal(a.CurrentLighting, b.CurrentLighting);
            //otherwise the seed is not read at all, and the two assertions above prove nothing
            Assert.NotEqual(a.Weather, different.Weather);
        }

        [Fact]
        public void TheClockIsAdvancedOnceAndNotOncePerObject()
        {
            //The regression this wiring could plausibly ship: the features type has a Tick that
            //advances the clock, and calling that from a tick which has already advanced the same
            //clock would run the neighbourhood at double time the moment the flag went on.
            Capability(NeighbourhoodWorldFlag.World, on: true);
            var world = World();
            world.Tick(OneGameHour);

            var expected = (long)OneGameHour * WorldClock.TicksPerMinuteTs1;
            Assert.Equal(expected, world.Clock.Ticks);
            Assert.True(world.HasFeatures);
            Assert.Equal(1L, world.FeatureTicks);
        }

        [Fact]
        public void TheFeaturesShareTheWorldsOwnClock()
        {
            Capability(NeighbourhoodWorldFlag.World, on: true);
            var world = World();
            world.Tick(OneGameHour);

            //Same object, not an equal one: a second clock is how a neighbourhood ends up with a
            //weather and a set of Sims on different times.
            Assert.Same(world.Clock, world.Features.Clock);
        }

        [Fact]
        public void ALotSwitchRereadsTheFeaturesRatherThanLeavingThemAtTheOldHour()
        {
            Capability(NeighbourhoodWorldFlag.World, on: true);
            var world = World();
            world.Tick(OneGameHour * 3);
            var before = world.CurrentLighting;

            //A lot that spent the afternoon hands its clock over on the way out; the neighbourhood
            //takes it, and the sky has to belong to the new time.
            world.PullClockFrom(new VMClock
            {
                Hours = 21,
                Minutes = 15,
                MinuteFractions = 0,
                TicksPerMinute = WorldClock.TicksPerMinuteTs1,
                DayOfMonth = 1,
                Month = 6,
                Year = 1997,
                Ticks = 0L
            });

            Assert.NotEqual(before, world.CurrentLighting);
            Assert.Equal(21, world.Lighting.TimeOfDay.Hour);
            Assert.True(world.CurrentLighting.IsNight);
        }

        [Fact]
        public void ADisabledWorldKeepsNoFeaturesAtAll()
        {
            //The world's own switch rather than the capability's: a world that is off must not have
            //derived anything, whatever the ini says.
            Capability(NeighbourhoodWorldFlag.World, on: true);
            var world = World(enabled: false);

            Assert.False(world.Tick(OneGameHour));
            Assert.False(world.HasFeatures);
            Assert.Equal(0L, world.FeatureTicks);
        }

        // ---------------------------------------------------------------- the flag is the gate

        [Fact]
        public void WithTheCapabilityOffNoFeaturesAreConstructedOrTicked()
        {
            NeighbourhoodWorldFlags.AllOff();
            var world = World();

            Assert.True(world.Tick(OneGameHour * 4), "the world itself still ticks");
            Assert.True(world.Clock.Ticks > 0, "and the clock still moves");

            Assert.False(world.HasFeatures);
            Assert.Equal(0L, world.FeatureTicks);
        }

        [Fact]
        public void WithTheCapabilityOffTheReadingPropertiesAnswerDefaultsWithoutBuildingAnything()
        {
            NeighbourhoodWorldFlags.AllOff();
            var world = World();
            world.Tick(OneGameHour * 4);

            Assert.Equal(default(WorldLightingState), world.Lighting);
            Assert.Equal(default(WorldWeather), world.Weather);
            Assert.NotNull(world.Visitors);
            Assert.Empty(world.Visitors);
            Assert.Equal(default(NeighbourhoodWorldLighting), world.CurrentLighting);

            //and asking did not build them
            Assert.False(world.HasFeatures);
            Assert.Equal(0L, world.FeatureTicks);
        }

        [Fact]
        public void TheWorldsOwnLogLineCarriesB8OnlyWhenB8Ran()
        {
            //The other half of "off ⇒ the previous stage's behaviour": not merely that nothing
            //moved, but that the line the world prints about itself is the line it printed before
            //B8. B8 is added to ToString() behind a null check for exactly this, the same shape
            //the off-screen simulator uses.
            NeighbourhoodWorldFlags.AllOff();
            var off = World();
            off.Tick(OneGameHour * 2);

            Assert.DoesNotContain("world features", off.ToString());

            Capability(NeighbourhoodWorldFlag.World, on: true);
            var on = World();
            on.Tick(OneGameHour * 2);

            Assert.Contains("world features", on.ToString());
        }

        [Fact]
        public void WithTheCapabilityOnTheWorldReportsTheFeatures()
        {
            Capability(NeighbourhoodWorldFlag.World, on: true);
            var world = World();

            Assert.False(world.HasFeatures);
            Assert.True(world.Tick(OneGameHour * 8));
            Assert.True(world.HasFeatures);
            Assert.True(world.FeatureTicks > 0);
            Assert.Equal(8, world.Lighting.TimeOfDay.Hour);
            Assert.True(world.Lighting.SunIntensity > 0.0);
            Assert.NotNull(world.Visitors);
        }

        [Fact]
        public void ATickThatMovesNothingDoesNotRereadTheFeatures()
        {
            //A paused frame and a frame shorter than one sub-minute tick. Neither has anything new
            //for B8 to read, and a per-frame re-read for a world whose time did not move is the
            //kind of cost that only shows up on a slow machine.
            Capability(NeighbourhoodWorldFlag.World, on: true);
            var world = World();

            Assert.False(world.Tick(0.0));
            Assert.False(world.Tick(0.001));
            Assert.Equal(0L, world.FeatureTicks);
            Assert.False(world.HasFeatures);

            Assert.True(world.Tick(OneGameHour));
            Assert.Equal(1L, world.FeatureTicks);
        }

        [Fact]
        public void AskingForTheFeaturesBuildsThemEvenWithTheCapabilityOff()
        {
            //The honest other half of the gating: the flag decides who may *cause* the features, and
            //a caller reaching for them has decided to. What must not happen is a world nobody asks
            //about constructing one.
            NeighbourhoodWorldFlags.AllOff();
            var world = World();

            var features = world.Features;

            Assert.NotNull(features);
            Assert.Same(features, world.Features);
            Assert.True(world.HasFeatures);
            Assert.Same(world.Clock, features.Clock);
            Assert.Equal(features.Lighting, world.Lighting);
            //and the flag still governs the tick: asking did not switch the world on
            Assert.Equal(0L, world.FeatureTicks);
        }
    }
}
