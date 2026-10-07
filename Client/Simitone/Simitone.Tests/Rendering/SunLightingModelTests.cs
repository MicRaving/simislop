using FSO.LotView;
using FSO.LotView.Model;
using Microsoft.Xna.Framework;
using System;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for <see cref="SunLightingModel"/> - the lot view's sun as a pure function of the
    /// time of day and the weather.
    ///
    /// <para>
    /// The load-bearing test is <see cref="LegacyModeMatchesTheOriginalMatrixStack"/>. The
    /// feature's whole safety argument is "with the option off, the lot is lit exactly as it
    /// was", and the way that is made checkable rather than merely claimed is that
    /// <c>SunLightingModel.Legacy</c> expands a matrix stack into arithmetic - and this file
    /// re-derives the same numbers from the <em>original</em> stack, using XNA's own
    /// <c>Matrix.CreateRotationY</c> and friends, exactly as the code before the refactor did.
    /// That test earned its keep: it caught the expansion being applied in the wrong order
    /// (XNA is row-vector, so the first factor written is the first one applied), which had
    /// turned a real half-sine elevation cycle into a constant height.
    /// </para>
    ///
    /// <para>
    /// The second thing worth reading is <see cref="LegacyShadowLengthIsUnbounded"/> and
    /// <see cref="LegacyNightShadowsAreAShadeTooStrong"/>. The daylight arc itself is
    /// correct and continuous - the model keeps it bit for bit, and
    /// <see cref="ModelDayIsTheLegacyDay"/> checks that to the minute - so what is left is the
    /// weather (which the legacy sun knows nothing about), the two shadow numbers (one of which is
    /// a division by the sun's height with no floor, so it is sixteen million at sunrise), and the
    /// fact that the sun and the sky have no colours of their own.
    /// </para>
    /// </summary>
    public sealed class SunLightingModelTests
    {
        private const float Tolerance = 1e-4f;

        /// <summary>Solar noon: the middle of the day window the game has always used.</summary>
        private const double Noon = 0.55;

        /// <summary>Sunrise and sunset, from the game's own day window.</summary>
        private const double Sunrise = SunLightingModel.DayOffset;
        private const double Sunset = SunLightingModel.Sunset;

        /// <summary>
        /// The middle of the night, which is not midnight: the night runs from 20:24 to 06:00, so
        /// its halfway point is 01:12.
        /// </summary>
        private const double DeepNight = SunLightingModel.DeepNight;

        /// <summary>A clear sky. The default value of the struct, named so the tests read as prose.</summary>
        private static readonly SunWeather Clear = SunWeather.Clear;

        /// <summary>Heavy rain, which is the weather the engine darkens the outdoor light for.</summary>
        private static readonly SunWeather Rain = new SunWeather(1f, 1f, isSnow: false, isThunder: false);

        /// <summary>Heavy snow, which the engine does <em>not</em> darken the outdoor light for.</summary>
        private static readonly SunWeather Snow = new SunWeather(1f, 0f, isSnow: true, isThunder: false);

        /// <summary>Every minute of a day, so "across 24h" really is across 24h.</summary>
        private static double[] EveryMinuteOfTheDay()
        {
            var times = new double[24 * 60];
            for (int minute = 0; minute < times.Length; minute++) times[minute] = minute / (24.0 * 60.0);
            return times;
        }

        // ---- the reference implementation of the code this replaced ---------------------------

        /// <summary>
        /// <c>LMapBatch.BuildOutdoorsLight</c> as it was before the sun model existed, verbatim,
        /// using the real XNA matrices rather than an expansion of them. This is the oracle.
        /// </summary>
        private static (Vector3 sunVector, Vector3 lightVec, Vector2 lightPos, Vector2 lightDir,
            float falloff, float shadow, bool night) Reference(double timeOfDay)
        {
            float DayOffset = 0.25f;
            float DayDuration = 0.60f;

            double tod = timeOfDay;

            bool night = false;
            double modTime;
            var offStart = 1 - (DayOffset + DayDuration);
            if (tod < DayOffset)
            {
                modTime = (offStart + tod) * 0.5 / (1 - DayDuration);
                night = true;
            }
            else if (tod > DayOffset + DayDuration)
            {
                modTime = (tod - (1 - offStart)) * 0.5 / (1 - DayDuration);
                night = true;
            }
            else
            {
                modTime = (tod - DayOffset) * 0.5 / DayDuration;
            }

            Matrix Transform = Matrix.Identity;
            Transform *= Matrix.CreateRotationY((float)((modTime + 0.5) * Math.PI * 2.0));
            Transform *= Matrix.CreateRotationZ((float)(Math.PI * (45.0 / 180.0)));
            Transform *= Matrix.CreateRotationY((float)(Math.PI * 0.3));

            var lightPos3 = new Vector3(0, 0, -3000);
            lightPos3 = Vector3.Transform(lightPos3, Transform);
            var z = lightPos3.Z;
            if (lightPos3.Y < 0) lightPos3.Y *= -1;

            var sunVector = lightPos3;
            sunVector.Normalize();

            var lightPos = new Vector2(lightPos3.Z, -lightPos3.X);
            var lightDir = -lightPos;
            lightDir.Normalize();
            lightPos3.Normalize();

            var lightVec = new Vector3(lightPos3.Z, 1f, -lightPos3.X);
            var falloff = (float)Math.Sqrt(lightPos3.X * lightPos3.X + lightPos3.Z * lightPos3.Z) / lightPos3.Y;

            if (modTime > 0.25) modTime = 0.5 - modTime;
            var shadow = Math.Abs(modTime) < 0.05 ? (float)(Math.Abs(modTime) * 20) * 1f : 1f;
            if (night) shadow *= 1.33f;

            return (sunVector, lightVec, lightPos, lightDir, falloff, shadow, night);
        }

        // ---- the option: off means unchanged ----------------------------------------------------

        [Fact]
        public void LegacyModeMatchesTheOriginalMatrixStack()
        {
            foreach (var tod in EveryMinuteOfTheDay())
            {
                var expected = Reference(tod);
                var actual = SunLightingModel.Evaluate(tod, Clear, SunLightMode.Legacy);

                AssertVector(expected.sunVector, actual.Direction, "SunVector at " + tod);
                AssertVector(expected.lightVec, actual.ShadingVector, "LightVec at " + tod);
                AssertVector(expected.lightPos, actual.LightMapPosition, "LightPos at " + tod);
                AssertVector(expected.lightDir, actual.GroundDirection, "LightDir at " + tod);
                Assert.Equal(expected.falloff, actual.ShadowLength, 5);
                Assert.Equal(expected.shadow, actual.ShadowStrength, 5);
                Assert.Equal(expected.night, actual.IsNight);
            }
        }

        [Fact]
        public void LegacyModeIgnoresTheWeatherEntirely()
        {
            // The legacy sun does not know the weather exists. This is the reason the weather
            // coupling is a separate model rather than a term bolted onto the old one: there is
            // nothing in the old derivation for it to modulate.
            foreach (var tod in EveryMinuteOfTheDay())
            {
                var clear = SunLightingModel.Evaluate(tod, Clear, SunLightMode.Legacy);
                foreach (var weather in new[] { Rain, Snow, new SunWeather(0.5f, 0.5f, false, true) })
                {
                    var wet = SunLightingModel.Evaluate(tod, weather, SunLightMode.Legacy);
                    AssertVector(clear.Direction, wet.Direction, "SunVector at " + tod);
                    Assert.Equal(clear.ShadowLength, wet.ShadowLength, 6);
                    Assert.Equal(clear.ShadowStrength, wet.ShadowStrength, 6);
                }
            }
        }

        [Fact]
        public void TheConfigDefaultsToTheLegacySun()
        {
            var config = new WorldConfig();

            // Off by default, and off means the legacy model. This is the assertion the whole
            // "nothing changes unless you ask" claim rests on, so it is stated as the default
            // rather than as a comment.
            Assert.False(config.SunCycle);
            Assert.Equal(SunLightMode.Legacy, config.SunMode);

            config.SunCycle = true;
            Assert.Equal(SunLightMode.Weather, config.SunMode);
        }

        [Fact]
        public void GodraysAreNotAdvertisedAsAvailable()
        {
            // There is no setting for them and there must not be one: a switch that switches
            // nothing is worse than no switch. See SunLightingModel.GodRaysSupported.
            Assert.False(SunLightingModel.GodRaysSupported);
            Assert.False(WorldConfig.GodRaysAvailable);
        }

        // ---- what the legacy sun actually did --------------------------------------------------

        [Fact]
        public void LegacyDaylightIsARealSolarArc()
        {
            // Stated so the rest of the file's "the model keeps the day arc" claim is not just
            // assertion: elevation 0 at both horizons, 45 degrees at noon, azimuth a half turn.
            Assert.Equal(0f, SunLightingModel.Legacy(Sunrise).Elevation, 1e-3f);
            Assert.Equal(0f, SunLightingModel.Legacy(Sunset).Elevation, 1e-3f);
            Assert.Equal(0.7071068f, SunLightingModel.Legacy(Noon).Elevation, 1e-5f);

            var degrees = Math.Asin(SunLightingModel.LegacyPeakElevationSin) * 180.0 / Math.PI;
            Assert.InRange(degrees, 44.5, 45.5);
        }

        [Fact]
        public void LegacyNightShadowsAreAShadeTooStrong()
        {
            // The original multiplies the sun's shadow strength by 1.33 at night and applies that
            // for the whole night, so the outdoor light map's sun contribution is a third stronger
            // before dawn than it is at noon - and it is a flat factor, so it does not care that
            // the moon is at the horizon at either end of the night and overhead in the middle.
            foreach (var tod in new[] { 0.0, SunLightingModel.DeepNight, Sunset + 0.05 })
            {
                Assert.Equal(1.33f, SunLightingModel.Legacy(tod).ShadowStrength, 0.001f);
            }
            Assert.Equal(1f, SunLightingModel.Legacy(Noon).ShadowStrength, 0.001f);

            // The original ramps it in over the first half hour of the night and then leaves it
            // there, so the whole night is lit as if by a sun a third stronger than the real one.
            // The model's version is a function of elevation and capped, so the moon is at its
            // weakest where it is lowest and never stronger than the sun.
            var low = SunLightingModel.Model(Sunset + 0.001, Clear);
            var high = SunLightingModel.Model(SunLightingModel.DeepNight, Clear);
            Assert.True(high.ShadowStrength > low.ShadowStrength,
                "the moon should cast a stronger shadow overhead than on the horizon");
            Assert.InRange(high.ShadowStrength, 0f, SunLightingModel.MaxShadowStrength);
        }

        [Fact]
        public void LegacyShadowLengthIsUnbounded()
        {
            // FalloffMultiplier is |xz| / y of the sun's direction, and y is zero exactly at
            // sunrise. The model floors and caps it; the legacy code does neither, and this is
            // the division by zero the model exists to avoid.
            var atSunrise = SunLightingModel.Legacy(Sunrise).ShadowLength;

            Assert.True(float.IsInfinity(atSunrise) || atSunrise > 1e6f,
                "expected the legacy shadow length to blow up at sunrise, got " + atSunrise);

            // ...and it is fine in the middle of the day, so it is specifically the horizons.
            Assert.Equal(1f, SunLightingModel.Legacy(Noon).ShadowLength, 0.01f);
        }

        [Fact]
        public void LegacyShadowStrengthGoesAboveOneAtNight()
        {
            var worst = 0f;
            foreach (var tod in EveryMinuteOfTheDay())
            {
                worst = MathF.Max(worst, SunLightingModel.Legacy(tod).ShadowStrength);
            }

            // 1.33, from the night boost: the outdoor light map's sun contribution is stronger
            // after dark than it is at noon. The model's cap is what stops that happening again.
            Assert.True(worst > 1.3f, "expected a night shadow strength above 1.3, got " + worst);
            Assert.InRange(SunLightingModel.Model(SunLightingModel.DeepNight, Clear).ShadowStrength,
                0f, SunLightingModel.MaxShadowStrength);
        }

        [Fact]
        public void LegacyFlatnessIsTheCosineOfTheElevation()
        {
            // The 3d normal shading vector is (dir.z, 1, -dir.x), so its horizontal magnitude is
            // the cosine of the sun's elevation: 0.707 at noon, towards 1 on the horizon.
            foreach (var tod in EveryMinuteOfTheDay())
            {
                var sun = SunLightingModel.Legacy(tod);
                var cosine = MathF.Sqrt(Math.Max(0f, 1f - sun.Elevation * sun.Elevation));
                Assert.True(Math.Abs(cosine - sun.Flatness) < 1e-5,
                    "flatness " + sun.Flatness + " against cos(elevation) " + cosine + " at " + tod);
            }
            Assert.Equal(SunLightingModel.LegacyNoonFlatness, SunLightingModel.Legacy(Noon).Flatness, Tolerance);
        }

        // ---- the model keeps the day arc ---------------------------------------------------------

        [Fact]
        public void ModelDayIsTheLegacyDay()
        {
            // The point of <see cref="SunLightingModel.DayArc"/> being shared rather than
            // reimplemented: turning the option on must not swing the lot's lighting sideways or
            // raise the noon sun. Checked to the last minute of the day.
            for (int minute = 0; minute < 24 * 60; minute++)
            {
                var tod = minute / (24.0 * 60.0);
                if (tod < Sunrise || tod > Sunset) continue;

                var legacy = SunLightingModel.Legacy(tod);
                var model = SunLightingModel.Model(tod, Clear);

                AssertVector(legacy.Direction, model.Direction, "the clear day arc at " + tod);
                Assert.Equal(legacy.Flatness, model.Flatness, Tolerance);
            }
        }

        [Fact]
        public void ModelSunIsOnTheHorizonAtSunriseAndSunset()
        {
            Assert.Equal(0f, SunLightingModel.Model(Sunrise, Clear).Elevation, Tolerance);
            Assert.Equal(0f, SunLightingModel.Model(Sunset, Clear).Elevation, Tolerance);
        }

        [Fact]
        public void ModelSunPeaksAtSolarNoon()
        {
            var noon = SunLightingModel.Model(Noon, Clear).Elevation;
            Assert.Equal(SunLightingModel.LegacyPeakElevationSin, noon, Tolerance);

            var best = 0f;
            foreach (var tod in EveryMinuteOfTheDay())
            {
                var day = SunLightingModel.Model(tod, Clear);
                if (day.IsNight) continue;
                Assert.True(day.Elevation <= noon + Tolerance,
                    "elevation " + day.Elevation + " at " + tod + " beat solar noon");
                if (day.Elevation > best) best = day.Elevation;
            }
            Assert.Equal(noon, best, 0.01f);
        }

        [Fact]
        public void ModelAzimuthSweepsTheSameHalfTurnTheLegacyOneDid()
        {
            // The magnitude, not the sign: atan2 wraps, so sunset's azimuth reads as a negative
            // angle and the raw difference is -pi rather than +pi. The sweep is clockwise in this
            // coordinate frame, which is what the arc's azimuth terms say.
            SunLightingModel.DayArc(0, out _, out var rise);
            SunLightingModel.DayArc(1, out _, out var set);
            Assert.Equal(Math.PI, Math.Abs(set - rise), 0.001);
            Assert.True(set < rise, "the legacy sweep is clockwise in this frame: " + rise + " to " + set);

            Assert.Equal(rise, SunLightingModel.SunriseAzimuth, Tolerance);
            Assert.Equal(set, SunLightingModel.SunsetAzimuth, Tolerance);

            foreach (var moment in new[] { Sunrise, Noon, Sunset })
            {
                var legacy = Math.Atan2(
                    SunLightingModel.Legacy(moment).Direction.Z,
                    SunLightingModel.Legacy(moment).Direction.X);
                Assert.Equal(legacy, SunLightingModel.Model(moment, Clear).Azimuth, 0.01);
            }
        }

        // ---- the model: day and night -----------------------------------------------------------

        [Fact]
        public void ModelKnowsWhenItIsNight()
        {
            foreach (var tod in EveryMinuteOfTheDay())
            {
                var sun = SunLightingModel.Model(tod, Clear);
                var expected = tod < Sunrise || tod > Sunset;
                Assert.Equal(expected, sun.IsNight);
                if (sun.IsNight) Assert.Equal(0f, sun.Daylight, Tolerance);
                else Assert.InRange(sun.Daylight, 0f, 1f);
            }
        }

        [Fact]
        public void DeepNightIsWhereTheMoonPeaks()
        {
            // The middle of this game's night is 01:12, not midnight: the day runs 06:00 to 20:24.
            // Getting that wrong is what put the moon's high point at the wrong hour.
            var deep = SunLightingModel.Model(DeepNight, Clear);
            var midnight = SunLightingModel.Model(0.0, Clear);

            Assert.True(deep.IsNight && midnight.IsNight);
            Assert.True(deep.Elevation > midnight.Elevation,
                "01:12 should be higher than midnight: " + deep.Elevation + " vs " + midnight.Elevation);
            Assert.Equal(MathF.Sin(SunLightingModel.MoonPeakElevation), deep.Elevation, Tolerance);
        }

        [Fact]
        public void TheModelNightIsContinuous()
        {
            // The thing the legacy night got wrong. Sampled a minute either side of both
            // boundaries, where the sun becomes the moon.
            AssertContinuousAcross(Sunset);
            AssertContinuousAcross(Sunrise);
            AssertContinuousAcross(0.0);

            var before = SunLightingModel.Model(0.9999, Clear);
            var after = SunLightingModel.Model(0.0001, Clear);
            Assert.True(Math.Abs(before.Azimuth - after.Azimuth) < 0.05,
                "the moon should not jump across midnight: " + before.Azimuth + " then " + after.Azimuth);
        }

        private static void AssertContinuousAcross(double boundary)
        {
            var before = SunLightingModel.Model(boundary - 0.0002, Clear);
            var after = SunLightingModel.Model(boundary + 0.0002, Clear);
            var azimuthJump = Math.Abs(before.Azimuth - after.Azimuth);
            Assert.True(azimuthJump < 0.05,
                "the sun/moon jumped " + azimuthJump + " rad across " + boundary);
            Assert.True(Math.Abs(before.Elevation - after.Elevation) < 0.01f,
                "elevation jumped from " + before.Elevation + " to " + after.Elevation + " across " + boundary);
            Assert.True(Math.Abs(before.ShadowLength - after.ShadowLength) < 0.5f,
                "shadow length jumped from " + before.ShadowLength + " to " + after.ShadowLength + " across " + boundary);
        }

        [Fact]
        public void TheMoonKeepsGoingRoundTheSunsCompassSweep()
        {
            // Not the anti-solar point: the disc rises where the sun set and sets where the sun
            // rises, which is what "the same sweep continued" means and what the legacy night was
            // reaching for before its modTime wrapped. A moon opposite the sun would rise in the
            // morning.
            var dusk = SunLightingModel.Model(Sunset + 0.0001, Clear);
            var deep = SunLightingModel.Model(DeepNight, Clear);
            var dawn = SunLightingModel.Model(Sunrise - 0.0001, Clear);

            Assert.True(dusk.IsNight && deep.IsNight && dawn.IsNight);
            Assert.True(deep.Elevation > 0f, "the moon should be up in the middle of the night");
            Assert.True(deep.Elevation > dusk.Elevation, "the moon should climb after dusk");
            Assert.True(deep.Elevation > dawn.Elevation, "the moon should be higher at 01:12 than at 05:59");
        }

        [Fact]
        public void ModelMoonIsFainterThanTheSun()
        {
            var noon = SunLightingModel.Model(Noon, Clear);
            var deep = SunLightingModel.Model(DeepNight, Clear);

            Assert.Equal(1f, noon.Intensity, 0.01f);
            Assert.True(deep.Intensity < noon.Intensity,
                "the moon at " + deep.Intensity + " should not be as strong as the sun at " + noon.Intensity);
        }

        // ---- the model: weather -----------------------------------------------------------------

        [Fact]
        public void RainLiftsTheSunAndShortensItsShadows()
        {
            // Overcast light is scattered by the whole sky dome rather than coming from a point,
            // so the direction the lot is shaded from moves towards the zenith. That is the whole
            // of "rain flattens the shadows".
            var clear = SunLightingModel.Model(Noon, Clear);
            var wet = SunLightingModel.Model(Noon, Rain);

            Assert.True(wet.Elevation > clear.Elevation,
                "rain should lift the sun: " + wet.Elevation + " vs " + clear.Elevation);
            Assert.True(wet.ShadowLength < clear.ShadowLength,
                "rain should shorten shadows: " + wet.ShadowLength + " vs " + clear.ShadowLength);
            Assert.True(wet.Flatness < clear.Flatness,
                "a flatter sun should shade walls facing it less hard");
        }

        [Fact]
        public void RainDimsTheSunAndSoftensItsShadows()
        {
            var clear = SunLightingModel.Model(Noon, Clear);
            var wet = SunLightingModel.Model(Noon, Rain);

            Assert.True(wet.Intensity < clear.Intensity,
                "rain should dim the sun: " + wet.Intensity + " vs " + clear.Intensity);
            Assert.True(wet.ShadowStrength < clear.ShadowStrength,
                "rain should soften shadows: " + wet.ShadowStrength + " vs " + clear.ShadowStrength);
        }

        [Fact]
        public void RainDesaturatesTheSunTowardsTheEngineWeatherGrey()
        {
            var wet = SunLightingModel.Model(Noon, Rain);
            var clear = SunLightingModel.Model(Noon, Clear);

            // The sun under cloud is the same grey the rest of the lot is already being tinted,
            // not a second unrelated colour: 159, 164, 181 is WeatherController's own.
            var expected = SunLightingModel.OvercastTint * wet.Intensity;
            AssertVector(expected, wet.SunColor, "the sun's colour under heavy rain");

            // So it is bluer relative to its red than the clear sun is. A straight channel
            // comparison would not do: the clear sun is simply brighter.
            var wetRatio = wet.SunColor.Z / wet.SunColor.X;
            var clearRatio = clear.SunColor.Z / clear.SunColor.X;
            Assert.True(wetRatio > clearRatio, "heavy rain should push the sun towards blue-grey");
        }

        [Fact]
        public void TheSkyGoesGreyUnderCloud()
        {
            var clear = SunLightingModel.Model(Noon, Clear);
            var wet = SunLightingModel.Model(Noon, Rain);

            AssertVector(SunLightingModel.DaySkyTint, clear.SkyTint, "a clear midday sky is untinted");
            AssertVector(SunLightingModel.OvercastTint, wet.SkyTint, "a heavy-rain midday sky");
        }

        [Fact]
        public void SnowCoversTheSkyWithoutDarkeningTheLot()
        {
            // The engine does not darken the outdoor light for snow - WeatherController.Darken
            // stays 0 for it - so the sun should be dimmed and flattened but not blacked out.
            var clear = SunLightingModel.Model(Noon, Clear);
            var snowy = SunLightingModel.Model(Noon, Snow);

            Assert.InRange(Snow.Overcast, 0.01f, 1f);
            Assert.True(snowy.Elevation > clear.Elevation, "snow should lift the sun");
            Assert.True(snowy.Intensity < clear.Intensity, "snow should dim the sun");
            Assert.True(snowy.Intensity > 0.3f, "snow should not black the sun out");
        }

        [Fact]
        public void MoreWeatherIsNeverLessWeather()
        {
            // Monotonicity: a heavier shower cannot make the lot brighter or the shadows harder.
            var previousIntensity = float.MaxValue;
            var previousElevation = -1f;
            for (int percent = 0; percent <= 100; percent += 5)
            {
                var weather = new SunWeather(percent / 100f, percent / 100f, false, false);
                var sun = SunLightingModel.Model(Noon, weather);

                Assert.True(sun.Intensity <= previousIntensity + Tolerance,
                    "intensity rose at " + percent + "% rain");
                Assert.True(sun.Elevation >= previousElevation - Tolerance,
                    "elevation fell at " + percent + "% rain");
                previousIntensity = sun.Intensity;
                previousElevation = sun.Elevation;
            }
        }

        [Fact]
        public void WeatherDoesNotMoveTheSun()
        {
            // Cloud does not know which way the sun was, so overcast lifts the elevation and dims
            // the light but must leave the azimuth alone.
            var clear = SunLightingModel.Model(Noon, Clear);
            var wet = SunLightingModel.Model(Noon, Rain);

            Assert.Equal(clear.Azimuth, wet.Azimuth, 0.001);
        }

        [Fact]
        public void OvercastIsClampedAndScaledByKind()
        {
            Assert.Equal(0f, SunWeather.Clear.Overcast);
            Assert.Equal(1f, Rain.Overcast);
            Assert.Equal(SunWeather.SnowCoverFactor, Snow.Overcast, Tolerance);

            // Garbage in is not a NaN out: a weather controller is not a settings file, but its
            // intensity is a blend of two integer severities and there is no reason to trust it
            // to stay in range.
            var overcast = new SunWeather(float.NaN, float.PositiveInfinity, true, true);
            Assert.False(float.IsNaN(overcast.Overcast));
            Assert.InRange(overcast.Overcast, 0f, 1f);

            var negative = new SunWeather(-5f, -5f, false, false);
            Assert.Equal(0f, negative.Overcast);
        }

        [Fact]
        public void ThunderAddsCoverOnTopOfItsRain()
        {
            var rain = new SunWeather(0.5f, 0.5f, false, false);
            var storm = new SunWeather(0.5f, 0.5f, false, true);

            Assert.Equal(rain.Overcast + SunWeather.ThunderCoverBoost, storm.Overcast, Tolerance);
        }

        // ---- the model: bounds ------------------------------------------------------------------

        [Fact]
        public void ModelShadowLengthIsBoundedAtBothEnds()
        {
            foreach (var tod in EveryMinuteOfTheDay())
            {
                foreach (var weather in new[] { Clear, Rain, Snow })
                {
                    var sun = SunLightingModel.Model(tod, weather);
                    Assert.InRange(sun.ShadowLength, 0f, SunLightingModel.MaxShadowLength + Tolerance);
                }
            }
        }

        [Fact]
        public void ModelShadowStrengthIsBoundedAndFadesAtTheHorizon()
        {
            foreach (var tod in EveryMinuteOfTheDay())
            {
                foreach (var weather in new[] { Clear, Rain, Snow })
                {
                    var sun = SunLightingModel.Model(tod, weather);
                    Assert.InRange(sun.ShadowStrength, 0f, SunLightingModel.MaxShadowStrength + Tolerance);
                    if (sun.Elevation <= 0f) Assert.Equal(0f, sun.ShadowStrength, Tolerance);
                }
            }
        }

        [Fact]
        public void ModelShadowLengthFallsAsTheSunRises()
        {
            // The thing the whole feature is for, stated as one assertion: a longer shadow in the
            // morning than at noon, and a longer one still at the very edge of dawn.
            var dawn = SunLightingModel.Model(Sunrise + 0.01, Clear);
            var morning = SunLightingModel.Model(Sunrise + 0.15, Clear);
            var noon = SunLightingModel.Model(Noon, Clear);
            var afternoon = SunLightingModel.Model(Sunset - 0.15, Clear);
            var dusk = SunLightingModel.Model(Sunset - 0.01, Clear);

            Assert.True(dawn.ShadowLength > morning.ShadowLength, "dawn vs morning");
            Assert.True(morning.ShadowLength > noon.ShadowLength, "morning vs noon");
            Assert.True(noon.ShadowLength < afternoon.ShadowLength, "noon vs afternoon");
            Assert.True(afternoon.ShadowLength < dusk.ShadowLength, "afternoon vs dusk");
        }

        [Fact]
        public void ModelShadingVectorCanNeverOverdriveTheShader()
        {
            // GrassShader.fx's LightDot is dot(LightVec, normal) * 0.5 + 0.5 with no clamp, so a
            // horizontal magnitude over 1 would push a sunlit wall past white. Every minute of
            // the day, in every weather, the horizontal part must stay at or below 1.
            foreach (var tod in EveryMinuteOfTheDay())
            {
                foreach (var weather in new[] { Clear, Rain, Snow })
                {
                    var sun = SunLightingModel.Model(tod, weather);
                    var horizontal = new Vector2(sun.ShadingVector.X, sun.ShadingVector.Z).Length();
                    Assert.True(horizontal <= SunLightingModel.MaxFlatness + Tolerance,
                        "LightVec's horizontal part was " + horizontal + " at " + tod);
                    Assert.Equal(1f, sun.ShadingVector.Y, Tolerance);
                }
            }
        }

        [Fact]
        public void ModelIsFiniteEverywhere()
        {
            // The legacy model returns Infinity for the shadow length at sunrise. Whatever else
            // changes, nothing this produces may be a NaN or an infinity, because a NaN reaching
            // a render target does not come back out.
            foreach (var tod in EveryMinuteOfTheDay())
            {
                foreach (var weather in new[] { Clear, Rain, Snow, new SunWeather(1f, 1f, true, true) })
                {
                    var sun = SunLightingModel.Evaluate(tod, weather, SunLightMode.Weather);
                    AssertFinite(sun.Direction, "Direction at " + tod);
                    AssertFinite(sun.ShadingVector, "ShadingVector at " + tod);
                    AssertFinite(sun.LightMapDirection, "LightMapDirection at " + tod);
                    AssertFinite(sun.LightMapPosition, "LightMapPosition at " + tod);
                    AssertFinite(sun.GroundDirection, "GroundDirection at " + tod);
                    AssertFinite(sun.SunColor, "SunColor at " + tod);
                    AssertFinite(sun.SkyTint, "SkyTint at " + tod);
                    Assert.True(float.IsFinite(sun.ShadowLength), "ShadowLength at " + tod);
                    Assert.True(float.IsFinite(sun.ShadowStrength), "ShadowStrength at " + tod);
                    Assert.True(float.IsFinite(sun.Elevation), "Elevation at " + tod);
                    Assert.True(float.IsFinite(sun.Flatness), "Flatness at " + tod);
                    Assert.True(float.IsFinite(sun.Daylight), "Daylight at " + tod);
                    Assert.True(float.IsFinite(sun.Intensity), "Intensity at " + tod);
                    Assert.True(float.IsFinite(sun.SkyStrip), "SkyStrip at " + tod);
                    Assert.True(float.IsFinite(sun.DiscScale), "DiscScale at " + tod);
                }
            }
        }

        [Fact]
        public void ModelDirectionsAreUnitLength()
        {
            foreach (var tod in EveryMinuteOfTheDay())
            {
                var sun = SunLightingModel.Model(tod, Clear);
                Assert.Equal(1f, sun.Direction.Length(), Tolerance);
                Assert.Equal(1f, sun.GroundDirection.Length(), Tolerance);
            }
        }

        [Fact]
        public void ModelOutOfRangeTimesWrapRatherThanThrow()
        {
            // The clock is not user input, but a caller can be, and Wrap is public.
            Assert.Equal(0.75, SunLightingModel.Wrap(-0.25), 1e-9);
            Assert.Equal(0.75, SunLightingModel.Wrap(1.75), 1e-9);
            Assert.Equal(0.25, SunLightingModel.Wrap(0.25), 1e-9);
            Assert.Equal(0d, SunLightingModel.Wrap(double.NaN));

            // And a wrapped time gives the same sun as its wrapped twin.
            var wrapped = SunLightingModel.Model(1.5, Clear);
            var direct = SunLightingModel.Model(0.5, Clear);
            AssertVector(direct.Direction, wrapped.Direction, "a wrapped time of day");
        }

        // ---- the sky strip ----------------------------------------------------------------------

        [Fact]
        public void TheSkyStopTableIsTheOneTheGameHasAlwaysUsed()
        {
            // Written out as literals on purpose: this table is duplicated in three places in the
            // engine (AbstractSkyDome, TimeOfDayConfig, and now here) and it is the sky texture's
            // layout, so a change to it is a change to the art and has to be a human decision.
            var expected = new float[]
            {
                4f/8f, 4f/8f, 4f/8f, 5f/8f, 6f/8f, 7f/8f, 8f/8f,
                0f/8f, 0f/8f, 0f/8f, 1f/8f, 2f/8f, 3f/8f, 4f/8f
            };

            Assert.Equal(expected, SunLightingModel.SkyStops);
            Assert.Equal(13f, SunLightingModel.NightStrip);
        }

        [Fact]
        public void StripValueReproducesTheOriginalOutsideSkyP()
        {
            // AbstractSkyDome.OutsideSkyP blended two adjacent stops with a "(time * 13) mod 1"
            // progress and refused to interpolate across the 8/8 -> 0/8 seam. Same formula, now
            // over a wrapping index so the model can hand it a phase rather than a time. The
            // tolerance is loose because the original worked in double and this takes a float.
            foreach (var time in EveryMinuteOfTheDay())
            {
                if (time >= 1.0) break;
                double progress = (time * (SunLightingModel.SkyStops.Length - 1)) % 1;
                var sky1 = SunLightingModel.SkyStops[(int)Math.Floor(time * (SunLightingModel.SkyStops.Length - 1))];
                var sky2 = SunLightingModel.SkyStops[(int)Math.Floor(time * (SunLightingModel.SkyStops.Length - 1)) + 1];
                if (sky1 == 1f && sky2 == 0f) progress = 0;
                var expected = (float)progress * sky2 + (1 - (float)progress) * sky1;
                var actual = SunLightingModel.StripValue((float)(time * 13));

                Assert.True(Math.Abs(expected - actual) < 1e-5,
                    "at " + time + ": expected " + expected + ", got " + actual);
            }
        }

        [Fact]
        public void StripValueDoesNotInterpolateAcrossThePeakSeam()
        {
            // Stop 6 is 8/8 and stop 7 is 0/8: the same column of the texture, so interpolating
            // between them would sweep the whole sky. The ramp holds at 1 all the way up to the
            // seam and then starts again from 0, which under LinearWrap is the same texel.
            Assert.Equal(1f, SunLightingModel.StripValue(6.5f), Tolerance);
            Assert.Equal(1f, SunLightingModel.StripValue(6.999f), Tolerance);
            Assert.Equal(0f, SunLightingModel.StripValue(7f), Tolerance);
            Assert.Equal(0f, SunLightingModel.StripValue(7.5f), Tolerance);
        }

        [Fact]
        public void StripValueWraps()
        {
            // Stop 13 and stop 0 are the same column too, so the strip closes after thirteen
            // steps and anything past that has to come back round.
            Assert.Equal(SunLightingModel.StripValue(0f), SunLightingModel.StripValue(13f), Tolerance);
            Assert.Equal(SunLightingModel.StripValue(2f), SunLightingModel.StripValue(15f), Tolerance);
            Assert.Equal(SunLightingModel.StripValue(2f), SunLightingModel.StripValue(-11f), Tolerance);
        }

        [Fact]
        public void TheModelPutsTheSkyStripSunriseWhereTheSunActuallyRises()
        {
            // The bug this fixes: the strip's own sunrise column is stop 4, which the raw clock
            // reaches at 4/13 = 30.8% of a day, while the sun rises at 25%. So the sky was showing
            // its sunrise colour an hour and a half after sunrise. The model runs its phase
            // across the strip's sunrise, peak and sunset columns instead.
            Assert.Equal(SunLightingModel.SunriseStrip, SunLightingModel.Model(Sunrise, Clear).SkyStrip, Tolerance);
            Assert.Equal(SunLightingModel.SunsetStrip, SunLightingModel.Model(Sunset, Clear).SkyStrip, Tolerance);
            Assert.Equal(SunLightingModel.PeakStrip, SunLightingModel.Model(Noon, Clear).SkyStrip, Tolerance);
            Assert.Equal((SunLightingModel.SunsetStrip + SunLightingModel.NightStrip) / 2f,
                SunLightingModel.Model(DeepNight, Clear).SkyStrip, Tolerance);
            // And the strip arrives at its deep-night column exactly as the night ends, which is
            // the same column the sun's own sunrise column wraps to.
            Assert.Equal(SunLightingModel.NightStrip,
                SunLightingModel.Model(Sunrise - 0.0001, Clear).SkyStrip, 0.01f);

            // And the legacy strip, for contrast, was not.
            var legacyAtSunrise = SunLightingModel.Legacy(Sunrise).SkyStrip;
            Assert.NotEqual(SunLightingModel.SunriseStrip, legacyAtSunrise, 0.5f);
        }

        [Fact]
        public void TheSkyStripNeverLeavesTheStrip()
        {
            foreach (var tod in EveryMinuteOfTheDay())
            {
                var strip = SunLightingModel.Model(tod, Clear).SkyStrip;
                Assert.InRange(strip, 0f, SunLightingModel.NightStrip);
                Assert.True(float.IsFinite(SunLightingModel.StripValue(strip)));
            }
        }

        // ---- the sun and moon disc -----------------------------------------------------------------

        [Fact]
        public void TheDiscSwellsAtTheHorizonAndShrinksUnderCloud()
        {
            var noon = SunLightingModel.Model(Noon, Clear);
            var dawn = SunLightingModel.Model(Sunrise + 0.02, Clear);
            var wetNoon = SunLightingModel.Model(Noon, Rain);

            Assert.Equal(1f, noon.DiscScale, Tolerance);
            Assert.True(dawn.DiscScale > noon.DiscScale, "the sun should swell near the horizon");
            Assert.True(wetNoon.DiscScale < noon.DiscScale, "the sun should shrink under cloud");
            Assert.True(wetNoon.DiscScale > 0f, "the sun should not vanish entirely under cloud");
        }

        [Fact]
        public void TheSunIsWarmAtTheHorizonsAndWhiteAtNoon()
        {
            var dawn = SunLightingModel.Model(Sunrise + 0.02, Clear);
            var dusk = SunLightingModel.Model(Sunset - 0.02, Clear);
            var noon = SunLightingModel.Model(Noon, Clear);

            Assert.True(dawn.SunColor.X > dawn.SunColor.Z, "a low sun should be warmer than blue");
            Assert.True(dusk.SunColor.X > dusk.SunColor.Z, "a low sun should be warmer than blue");
            AssertVector(SunLightingModel.ZenithSunColor * noon.Intensity, noon.SunColor, "a noon sun");
        }

        [Fact]
        public void TheSkyIsWarmAtDawnAndDuskAndNeutralAtNoon()
        {
            var dawn = SunLightingModel.Model(Sunrise + 0.02, Clear);
            var noon = SunLightingModel.Model(Noon, Clear);
            var dusk = SunLightingModel.Model(Sunset - 0.02, Clear);
            var deep = SunLightingModel.Model(DeepNight, Clear);

            Assert.True(dawn.SkyTint.X > dawn.SkyTint.Z, "dawn sky");
            Assert.True(dusk.SkyTint.X > dusk.SkyTint.Z, "dusk sky");
            AssertVector(SunLightingModel.DaySkyTint, noon.SkyTint, "a midday sky is untinted");
            AssertVector(SunLightingModel.NightSkyTint, deep.SkyTint, "the night sky");
        }

        [Fact]
        public void TheSunIsBoundedSoAnAdditiveDiscCannotClip()
        {
            // The sky dome draws the disc with BlendState.Additive, so every channel has to stay
            // at or below one in every weather or the disc blows out to white.
            foreach (var tod in EveryMinuteOfTheDay())
            {
                foreach (var weather in new[] { Clear, Rain, Snow })
                {
                    var sun = SunLightingModel.Model(tod, weather);
                    Assert.InRange(sun.SunColor.X, 0f, 1f);
                    Assert.InRange(sun.SunColor.Y, 0f, 1f);
                    Assert.InRange(sun.SunColor.Z, 0f, 1f);
                    Assert.InRange(sun.SkyTint.X, 0f, 1f);
                    Assert.InRange(sun.SkyTint.Y, 0f, 1f);
                    Assert.InRange(sun.SkyTint.Z, 0f, 1f);
                }
            }
        }

        // ---- helpers ------------------------------------------------------------------------------

        private static void AssertVector(Vector3 expected, Vector3 actual, string what)
        {
            Assert.True(Math.Abs(expected.X - actual.X) < 1e-5 && Math.Abs(expected.Y - actual.Y) < 1e-5
                && Math.Abs(expected.Z - actual.Z) < 1e-5,
                what + ": expected " + expected + ", got " + actual);
        }

        private static void AssertVector(Vector2 expected, Vector2 actual, string what)
        {
            Assert.True(Math.Abs(expected.X - actual.X) < 1e-5 && Math.Abs(expected.Y - actual.Y) < 1e-5,
                what + ": expected " + expected + ", got " + actual);
        }

        private static void AssertFinite(Vector3 v, string what)
        {
            Assert.True(float.IsFinite(v.X), what + " X is " + v.X);
            Assert.True(float.IsFinite(v.Y), what + " Y is " + v.Y);
            Assert.True(float.IsFinite(v.Z), what + " Z is " + v.Z);
        }

        private static void AssertFinite(Vector2 v, string what)
        {
            Assert.True(float.IsFinite(v.X), what + " X is " + v.X);
            Assert.True(float.IsFinite(v.Y), what + " Y is " + v.Y);
        }
    }
}
