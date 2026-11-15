/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.IO;
using FSO.Files.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Neighbourhood;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The neighbourhood's sky, light and fog, and the trees that stand on it.
    ///
    /// <para><b>What is being pinned.</b> The scene used to clear the frame to a fixed fog colour,
    /// draw no dome at all, and light itself from three constants somebody chose - a beige light, a
    /// blue-grey haze and a hand-picked afternoon sun direction. That is what made the map read as a
    /// void with houses floating in it. What replaced them is TSO's own
    /// <c>Terrain.SetTimeOfDay</c> (<c>Terrain.cs:1273-1327</c>), <c>Terrain.Draw</c>'s shader
    /// setup (<c>:1481-1519</c>), the dome at <c>:1556</c>, and the city's own fog numbers, all
    /// lifted into pure values so that "the sun rises in the east" is a thing a test can say.
    /// </para>
    ///
    /// <para><b>And on the foliage</b> that the scatter has to be inside the drawn region, that the
    /// density table is the city's, that the ground under a tree is the interpolated ground rather
    /// than the height of its tile's corner, and that a chunk's box contains a whole tree.</para>
    /// </summary>
    public class NeighbourhoodCitySkyTests
    {
        // ---- the light, as a function of the clock ----------------------------------------------------

        [Fact]
        public void TheSunIsUpForTheDayAndDownForTheNight()
        {
            // DayOffset 0.25 to DayOffset + DayDuration 0.85 is the city's day
            // (Terrain.cs:149-150), and inside it the arm's y is positive - which is the whole
            // reason this could not be a constant: a fixed direction is either a noon at midnight or
            // a midnight at noon.
            for (double tod = NeighbourhoodCityLighting.DayOffset + 0.02;
                tod < NeighbourhoodCityLighting.DayOffset + NeighbourhoodCityLighting.DayDuration - 0.02;
                tod += 0.05)
            {
                var lighting = NeighbourhoodCityLighting.At(tod);
                Assert.False(lighting.IsNight, "t=" + tod + " is inside the day and reads as night");
                Assert.True(lighting.LightDirection.Y > 0f,
                    "the sun is below the horizon at t=" + tod + ": " + lighting.LightDirection);
            }

            Assert.True(NeighbourhoodCityLighting.At(0.0).IsNight);
            Assert.True(NeighbourhoodCityLighting.At(0.24).IsNight);
            Assert.True(NeighbourhoodCityLighting.At(0.86).IsNight);
            Assert.True(NeighbourhoodCityLighting.At(0.5).LightDirection.Y > 0f);
        }

        [Fact]
        public void TheLightDirectionIsTheCitiesOwnArcAndAlwaysAUnitVector()
        {
            for (double tod = 0.0; tod < 1.0; tod += 0.01)
            {
                var lighting = NeighbourhoodCityLighting.At(tod);
                Assert.Equal(1f, lighting.LightDirection.Length(), 4);
                Assert.False(float.IsNaN(lighting.LightDirection.X), "NaN at t=" + tod);
                Assert.False(float.IsNaN(lighting.LightDirection.Y), "NaN at t=" + tod);
                Assert.False(float.IsNaN(lighting.LightDirection.Z), "NaN at t=" + tod);

                // the arm is 263 tiles long and the rotations preserve that, so the position is on a
                // sphere of that radius about the middle of the map and the direction is the radius
                var position = NeighbourhoodCityLighting.LightPositionFor(lighting.SkyStripU * 0f
                    + NeighbourhoodCityLighting.ModTimeFor(tod));
                Assert.Equal(NeighbourhoodCityLighting.LightArmLength,
                    (position - NeighbourhoodCityLighting.MapCentre).Length(), 2);
                Assert.Equal(lighting.LightDirection,
                    NeighbourhoodCityLighting.DirectionTowards(position));
            }
        }

        [Fact]
        public void TheSunTracksAcrossTheSkyRatherThanStandingStill()
        {
            // The point of a time-driven sun, and it is worth being exact about the shape: TSO's
            // arc is a half turn over the day and a half turn back over the night, so the sun is on
            // the horizon at both ends of the day and the two ends of the day are the same
            // direction. What it does not do is stand still, and what a constant cannot do is have
            // its height depend on the clock at all.
            var rise = NeighbourhoodCityLighting.At(NeighbourhoodCityLighting.DayOffset);
            var noon = NeighbourhoodCityLighting.At(0.5);
            var set = NeighbourhoodCityLighting.At(
                NeighbourhoodCityLighting.DayOffset + NeighbourhoodCityLighting.DayDuration);

            Assert.InRange(rise.LightDirection.Y, -0.01f, 0.01f);
            Assert.InRange(set.LightDirection.Y, -0.01f, 0.01f);
            Assert.True(noon.LightDirection.Y > 0.6f, "the sun should be high at noon: " + noon);

            // the moon takes the same arc at night, so the height is a function of the clock the
            // whole way round rather than a day-only one
            Assert.True(NeighbourhoodCityLighting.At(0.0).LightDirection.Y > 0.6f,
                "the moon should be high in the middle of the night: "
                    + NeighbourhoodCityLighting.At(0.0));

            // Each arc is continuous - which is the property that matters, because it is what stops
            // a clock that ticks from making the sun jump. TSO's day arc and night arc do NOT join
            // at the horizons: modTime is 0.5 at the end of the night and 0 at the start of the day,
            // so the two arcs start 135 degrees apart in the rotation. That is the reference's own
            // behaviour and it is pinned here as such - "smooth the seam" is the kind of change that
            // moves the city off its own sun without anybody noticing until a screenshot.
            AssertArcIsContinuous(NeighbourhoodCityLighting.DayOffset + 0.02,
                NeighbourhoodCityLighting.DayOffset + NeighbourhoodCityLighting.DayDuration - 0.02,
                "the day's arc");
            AssertArcIsContinuous(NeighbourhoodCityLighting.DayOffset
                    + NeighbourhoodCityLighting.DayDuration + 0.02,
                NeighbourhoodCityLighting.DayOffset + 1.02,
                "the night's arc");
        }

        private static void AssertArcIsContinuous(double from, double to, string what)
        {
            var previous = NeighbourhoodCityLighting.At(from).LightDirection;
            for (double tod = from + 0.005; tod < to; tod += 0.005)
            {
                var now = NeighbourhoodCityLighting.At(tod).LightDirection;
                Assert.True(Vector3.Distance(previous, now) < 0.05f,
                    what + " jumped between t=" + (tod - 0.005) + " and t=" + tod);
                previous = now;
            }
        }

        [Fact]
        public void TheTintAndTheShadowStrengthAreTheCitiesTables()
        {
            // TimeOfDayConfig's table: white across the middle of the day (its peak stops), warm at
            // the two horizons and a dim blue at midnight.
            Assert.Equal(new Color(255, 255, 255), NeighbourhoodCityLighting.At(0.5).TintColour);
            var dusk = NeighbourhoodCityLighting.At(0.8).TintColour;
            Assert.True(dusk.R > dusk.B, "the sunset stop is warm: " + dusk);
            var midnight = NeighbourhoodCityLighting.At(0.0).TintColour;
            Assert.True(midnight.R < dusk.R, "midnight should be darker than sunset");
            Assert.True(midnight.B > midnight.R, "midnight should be blue: " + midnight);

            // ShadowMult is 0.25 across the day and crosses over at the horizon
            // (Terrain.cs:1317-1326); it never leaves [0.25, 1].
            for (double tod = 0.0; tod < 1.0; tod += 0.005)
            {
                var strength = NeighbourhoodCityLighting.At(tod).ShadowStrength;
                Assert.InRange(strength, 0.25f, 1f);
            }
            Assert.Equal(0.25f, NeighbourhoodCityLighting.At(0.5).ShadowStrength);
            Assert.True(NeighbourhoodCityLighting.At(NeighbourhoodCityLighting.DayOffset)
                    .ShadowStrength > 0.25f,
                "the shadow should cross over at the horizon rather than step");
        }

        [Fact]
        public void LightColIsTheTintTimesTheCitiesStrength()
        {
            // Terrain.cs:1489: the tint's channels over 255, times 1.25 - and the multiplier goes on the alpha
            // as well, because it multiplies the whole Vector4. The shader reads LightCol.xyz, so the
            // 1.25 on W is inert; it is pinned because a "tidied up" version would be a different
            // vector and the tidying is the kind of thing that goes in a diff and comes back out.
            var noon = NeighbourhoodCityLighting.At(0.5);
            Assert.Equal(1f * NeighbourhoodCityLighting.LightColourStrength, noon.LightColour.X, 4);
            Assert.Equal(1f * NeighbourhoodCityLighting.LightColourStrength, noon.LightColour.Z, 4);
            Assert.Equal(1f * NeighbourhoodCityLighting.LightColourStrength, noon.LightColour.W, 4);

            var midnight = NeighbourhoodCityLighting.At(0.0);
            Assert.Equal(midnight.TintColour.R / 255f * NeighbourhoodCityLighting.LightColourStrength,
                midnight.LightColour.X, 4);
            Assert.True(midnight.LightColour.X < noon.LightColour.X,
                "a dark tint has to reach the shader as a dim light");
        }

        [Fact]
        public void TheDefaultTimeOfDayIsABrightAfternoonAndIsWhatTheConstantsSay()
        {
            // The three public constants exist because SimitoneSurroundingsPlacement falls back to
            // them before a lot's light batch exists, and a lot lit from a different sun than the map
            // is the visible version of a stale constant. So they must be the city's own answer at
            // the default time, not a number of their own.
            var lighting = NeighbourhoodCityLighting.At(Neighbourhood3DView.DefaultTimeOfDay);

            Assert.Equal(lighting.LightDirection, Neighbourhood3DView.LightDirection);
            Assert.Equal(lighting.LightColour, Neighbourhood3DView.LightColor);
            Assert.Equal(lighting.FogColour, Neighbourhood3DView.FogColor);

            Assert.InRange(Neighbourhood3DView.DefaultTimeOfDay,
                NeighbourhoodCityLighting.DayOffset,
                NeighbourhoodCityLighting.DayOffset + NeighbourhoodCityLighting.DayDuration);
            Assert.True(lighting.LightDirection.Y > 0.5f, "the fixed afternoon sun is too low");
            Assert.False(lighting.IsNight);
        }

        [Fact]
        public void AnAbsurdTimeOfDayIsClampedRatherThanThrown()
        {
            // Terrain clamps the top because TimeOfDayConfig indexes a fourteen-stop table and reads
            // one past it (Terrain.cs:1275). It trusts its caller for the bottom; this seam has no
            // such guarantee, because a caller would be handing over a wall-clock hour.
            Assert.Equal(NeighbourhoodCityLighting.At(0.0).TintColour,
                NeighbourhoodCityLighting.At(-3.5).TintColour);
            Assert.Equal(NeighbourhoodCityLighting.At(0.999999999).TintColour,
                NeighbourhoodCityLighting.At(13.0).TintColour);
            Assert.Equal(NeighbourhoodCityLighting.At(0.0).TintColour,
                NeighbourhoodCityLighting.At(double.NaN).TintColour);

            var absurd = NeighbourhoodCityLighting.At(1e9);
            Assert.Equal(1f, absurd.LightDirection.Length(), 3);
        }

        // ---- the fog -------------------------------------------------------------------------------

        [Fact]
        public void TheFogIsTheCitiesClearSkyDistanceTimesTheCamerasZoomCurve()
        {
            // Terrain.cs:1516: FogMaxDist = FogColor.W * Camera.FogMultiplier, where FogColor.W is
            // 300 * 75 tiles of clear sky (WeatherController.cs:87) and FogMultiplier is
            // 1 - (1 - (Zoom3D + 1) / 21) ^ (1/3) (CityCamera3D.cs:91-100).
            Assert.Equal(300f * 75f, NeighbourhoodCityLighting.ClearFogDistance, 3);
            Assert.Equal(15f * 75f, NeighbourhoodCityLighting.ObscuredFogDistance, 3);

            // the whole map in frame is the city's far view, where Zoom3D is 12 and the fog is at
            // its thinnest - the clear-sky distance with no zoom multiplier on it
            var far = NeighbourhoodCityLighting.FogMaxDistFor(NeighbourhoodCityMeshBuilder.MapSize);
            Assert.Equal(NeighbourhoodCityLighting.ClearFogDistance, far, 3);
            Assert.Equal(far,
                NeighbourhoodCityLighting.ClearFogDistance * NeighbourhoodCityLighting.FogMultiplierFor(
                    NeighbourhoodCityMeshBuilder.MapSize), 3);

            // and zooming in brings the fade in towards the camera, which is what the city's own
            // camera does: its far view has Zoom3D 12 and its near view Zoom3D 0, so the fog
            // distance is largest when the most of the map is in frame
            var near = NeighbourhoodCityLighting.FogMaxDistFor(32f);
            Assert.True(near < far, "zoom should bring the fog in: " + near + " vs " + far);
            Assert.True(near > 1f, "the shader divides by FogMaxDist; " + near);

            // monotonic, and total: as more of the map is framed the fog recedes, and it never
            // closes back in
            var previous = 0f;
            for (float tiles = 0f; tiles <= NeighbourhoodCityMeshBuilder.MapSize; tiles += 4f)
            {
                var distance = NeighbourhoodCityLighting.FogMaxDistFor(tiles);
                Assert.True(distance > 1f, "the shader divides by FogMaxDist; " + distance + " at " + tiles);
                Assert.True(distance >= previous, "the fog must not close in as you zoom out");
                previous = distance;
            }
            foreach (var rubbish in new[] { float.NaN, float.NegativeInfinity, float.PositiveInfinity, -1f, 0f })
            {
                Assert.True(NeighbourhoodCityLighting.FogMaxDistFor(rubbish) > 1f,
                    "FogMaxDistFor(" + rubbish + ") is not usable");
            }
        }

        [Fact]
        public void TheFogColourIsTheCitiesDerivationAndCarriesNoAlpha()
        {
            // WeatherController.cs:82-87. Two sRGB round trips and a subtract/add around mid grey,
            // then a multiply by the sky's own half-blue 0x80C0FF. WeatherController.cs:1517 forces
            // the alpha to one before the shader sees it, and a FogColor with an alpha in it is a
            // different colour on screen.
            var fog = NeighbourhoodCityLighting.FogColourFor(new Color(255, 255, 255));
            Assert.Equal(1f, fog.W, 4);

            // noon fades towards pale blue, not towards white: the blue channel of 0x80C0FF has to
            // survive the derivation
            Assert.True(fog.Z > fog.X, "a clear day's haze is bluer than it is red: " + fog);

            // and it tracks the tint: a warm sunset gives a warm haze and a night gives a dim one
            var sunset = NeighbourhoodCityLighting.FogColourFor(NeighbourhoodCityLighting.At(0.8).TintColour);
            var midnight = NeighbourhoodCityLighting.FogColourFor(NeighbourhoodCityLighting.At(0.0).TintColour);
            Assert.True(sunset.X > midnight.X, "a sunset's haze is warmer than midnight's");
            Assert.True(sunset.X < fog.X, "a sunset's haze is darker than noon's");
            Assert.Equal(1f, sunset.W, 4);
            Assert.Equal(1f, midnight.W, 4);

            // total: a near-black outside colour takes the derivation's contrast pull below zero,
            // and a NaN here is a NaN FogColor and a black screen
            foreach (var black in new[] { Color.Black, new Color(1, 1, 1), new Color(4, 4, 4) })
            {
                var dark = NeighbourhoodCityLighting.FogColourFor(black);
                Assert.False(float.IsNaN(dark.X), "FogColourFor(" + black + ") is NaN");
                Assert.False(float.IsNaN(dark.Z), "FogColourFor(" + black + ") is NaN");
                Assert.InRange(dark.X, 0f, 1f);
                Assert.InRange(dark.Y, 0f, 1f);
                Assert.InRange(dark.Z, 0f, 1f);
            }
            Assert.InRange(fog.X, 0f, 1f);
            Assert.InRange(fog.Y, 0f, 1f);
            Assert.InRange(fog.Z, 0f, 1f);
        }

        // ---- the strip ------------------------------------------------------------------------------

        [Fact]
        public void TheStripCoordinateIsTheDomesOwnAndAlwaysOnTheTexture()
        {
            // AbstractSkyDome.OutsideSkyP: the stop index is time * (SkyStops.Length - 1), the strip
            // value comes out of SunLightingModel.StripValue and the texture is then offset by the
            // half-column the strip was authored with (AbstractSkyDome.cs:92-104).
            for (double tod = 0.0; tod < 1.0; tod += 0.01)
            {
                var u = NeighbourhoodCityLighting.SkyStripUFor(tod);
                // the strip's peak stop is 8/8, i.e. u == 1, and the half-column offset the strip was
                // authored with pushes it past the edge - so the upper bound is 1 + 1/16 and the
                // sampler wraps, which is what the dome's LinearWrap sampler is for
                Assert.InRange(u, 0f, 1f + 1f / 16f);
            }

            // noon is the peak stop, 8/8, which is u == 1 and wraps to the first column; midnight is
            // the stop before it, 4/8, which is the middle. Two different columns, so a background
            // sampled at one is not the background at the other.
            var stopCount = FSO.LotView.Model.SunLightingModel.SkyStops.Length;
            Assert.Equal(1f + 1f / 16f, NeighbourhoodCityLighting.SkyStripUFor(6.0 / (stopCount - 1)), 4);
            Assert.Equal(0.5f + 1f / 16f, NeighbourhoodCityLighting.SkyStripUFor(0.0), 4);
        }

        [Fact]
        public void TheClearColourIsTheHorizonRowAndNotSomethingElse()
        {
            // AbstractSkyDome.cs:193 puts the horizon at tpos 0.9: above it the dome runs to 0 at
            // the zenith, below it is the last tenth of the texture, which no ring reaches. Sampling
            // the wrong row would clear to the zenith's colour and the map edge would show a seam.
            Assert.Equal(0.9f, NeighbourhoodSkyStrip.HorizonRow, 4);
            Assert.Equal("Textures/skycol.png", NeighbourhoodSkyStrip.RelativePath);
            Assert.EndsWith("Textures/skycol.png", NeighbourhoodSkyStrip.Path);
        }

        [Fact]
        public void TheStripSamplesDayAndNightToDifferentColoursWhenItCanBeRead()
        {
            // Skipped rather than failed when the strip is not beside the test assembly: the point of
            // this file is that the game is honest about a missing asset, and a headless runner whose
            // working directory is somewhere else is that situation, not a defect.
            if (!File.Exists(NeighbourhoodSkyStrip.Path))
            {
                Assert.False(NeighbourhoodSkyStrip.Load());
                Assert.False(NeighbourhoodSkyStrip.Available);
                Assert.Null(NeighbourhoodSkyStrip.HorizonColourFor(0.5));
                Assert.Null(NeighbourhoodSkyStrip.At(0.5f, 0.9f));
                return;
            }

            Assert.True(NeighbourhoodSkyStrip.Load());
            Assert.True(NeighbourhoodSkyStrip.Available);
            Assert.True(NeighbourhoodSkyStrip.Width > 1, "the strip is eight columns wide");
            Assert.True(NeighbourhoodSkyStrip.Height > 1);

            var noon = NeighbourhoodSkyStrip.HorizonColourFor(0.5);
            var midnight = NeighbourhoodSkyStrip.HorizonColourFor(0.0);
            Assert.NotNull(noon);
            Assert.NotNull(midnight);
            Assert.True(noon.Value != midnight.Value,
                "a fixed background colour is the void this pass exists to remove");

            // out of range reads are clamped and wrapped, never an index off the end
            Assert.NotNull(NeighbourhoodSkyStrip.At(-1f, 0.9f));
            Assert.NotNull(NeighbourhoodSkyStrip.At(17f, 0.9f));
            Assert.NotNull(NeighbourhoodSkyStrip.At(0.5f, -3f));
            Assert.NotNull(NeighbourhoodSkyStrip.At(0.5f, 99f));
        }

        // ---- the foliage ----------------------------------------------------------------------------

        [Fact]
        public void ADensityBucketGetsTheCitysOwnNumberOfTrees()
        {
            // CityFoliage.cs:101, verbatim: { 1, 4, 7, 15 } indexed by (density * 4) / 255.
            Assert.Equal(new[] { 1, 4, 7, 15 }, NeighbourhoodFoliage.TreesPerDensity);
            // the bucket is (density * 4) / 255, so 64 is the first byte that asks for any tree and the
            // four buckets are 64..127, 128..191, 192..255
            Assert.Equal(0, NeighbourhoodFoliage.TreesForDensity(0));
            Assert.Equal(0, NeighbourhoodFoliage.TreesForDensity(1));
            Assert.Equal(0, NeighbourhoodFoliage.TreesForDensity(63));
            Assert.Equal(1, NeighbourhoodFoliage.TreesForDensity(64));
            Assert.Equal(1, NeighbourhoodFoliage.TreesForDensity(127));
            Assert.Equal(4, NeighbourhoodFoliage.TreesForDensity(128));
            Assert.Equal(4, NeighbourhoodFoliage.TreesForDensity(191));
            Assert.Equal(7, NeighbourhoodFoliage.TreesForDensity(192));
            Assert.Equal(15, NeighbourhoodFoliage.TreesForDensity(255));
        }

        [Fact]
        public void ATreeIsOnlyPlacedWhereTheGroundIsDrawn()
        {
            // The bug the live log kept printing. IsInsideDiamond is the road region and the
            // ground mesh emits no quad at all for a terrain byte at or above LayerCount
            // (NeighbourhoodCityMeshBuilder.cs:2635), so a tree on either is a tree with nothing
            // under it.
            var map = NeighbourhoodMap.Create(NeighbourhoodFoliage.MapSize);
            for (int i = 0; i < map.ForestDensityData.Length; i++) map.ForestDensityData[i] = 255;

            // the middle of the map: inside the diamond, grass, forested, no road
            var centre = 256;
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(centre, centre));
            Assert.True(NeighbourhoodFoliage.IsTreeable(map, centre, centre));

            // outside the diamond, in the top-left cut corner of the square
            Assert.False(NeighbourhoodCityMeshBuilder.IsInsideDiamond(2, 2));
            Assert.False(NeighbourhoodFoliage.IsTreeable(map, 2, 2));

            // inside the diamond but with no ground under it
            var bare = map.Index(centre, centre);
            map.TerrainType[bare] = NeighbourhoodTerrain.Nothing;
            Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(centre, centre));
            Assert.False(NeighbourhoodFoliage.IsTreeable(map, centre, centre));
            map.TerrainType[bare] = NeighbourhoodTerrain.Grass;
            Assert.True(NeighbourhoodFoliage.IsTreeable(map, centre, centre));

            // inside, with ground, but no forest and then a road
            map.ForestDensityData[bare] = 0;
            Assert.False(NeighbourhoodFoliage.IsTreeable(map, centre, centre));
            map.ForestDensityData[bare] = 255;
            map.RoadData[bare] = RoadMask.MaskBits;
            Assert.False(NeighbourhoodFoliage.IsTreeable(map, centre, centre));

            // and the total function
            Assert.False(NeighbourhoodFoliage.IsTreeable(null, centre, centre));
            Assert.False(NeighbourhoodFoliage.IsTreeable(map, -1, centre));
            Assert.False(NeighbourhoodFoliage.IsTreeable(map, centre, NeighbourhoodFoliage.MapSize));
        }

        [Fact]
        public void TheEligibilityWalkAsksTheSameQuestionTheChunkBuildAsks()
        {
            // The divergence the doc on IsTreeable describes, pinned so it cannot come back: the
            // plan and the chunk build must agree tile for tile, because a tile the plan calls
            // treeable is a chunk the budget is spent on and a tile the build rejects is a tree
            // that was going to exist. Any disagreement is trees in a place nothing is drawn.
            var map = NeighbourhoodMap.Create(NeighbourhoodFoliage.MapSize);
            var rng = new Random(12345);
            for (int i = 0; i < map.ForestDensityData.Length; i++)
            {
                map.ForestDensityData[i] = (byte)(rng.Next(0, 4) == 0 ? 0 : rng.Next(64, 256));
                map.TerrainType[i] = (byte)(rng.Next(0, 12) == 0
                    ? NeighbourhoodTerrain.Nothing : NeighbourhoodTerrain.Grass);
                map.RoadData[i] = (byte)(rng.Next(0, 3) == 0 ? RoadMask.MaskBits : 0);
            }

            var plan = NeighbourhoodFoliagePlan.For(map, NeighbourhoodFoliage.TreeBudget);

            //the eligible chunks are exactly the chunks holding at least one treeable tile
            var expected = new bool[NeighbourhoodFoliage.ChunksPerAxis * NeighbourhoodFoliage.ChunksPerAxis];
            var treeable = 0;
            for (int oy = 0; oy < NeighbourhoodFoliage.MapSize; oy++)
            {
                for (int ox = 0; ox < NeighbourhoodFoliage.MapSize; ox++)
                {
                    if (!NeighbourhoodFoliage.IsTreeable(map, ox, oy)) continue;
                    treeable++;
                    expected[(ox / NeighbourhoodFoliage.ChunkSize)
                        + (oy / NeighbourhoodFoliage.ChunkSize) * NeighbourhoodFoliage.ChunksPerAxis] = true;
                }
            }

            var expectedChunks = 0;
            foreach (var e in expected) if (e) expectedChunks++;
            Assert.True(treeable > 0, "the map should have some treeable tiles to be worth testing");
            Assert.Equal(expectedChunks, plan.EligibleChunks);
            for (int chunk = 0; chunk < expected.Length; chunk++)
            {
                Assert.True(expected[chunk] == plan.IsEligible(chunk),
                    "chunk " + chunk + " should " + (expected[chunk] ? "" : "not ") + "be eligible");
            }

            //and no tileable tile is outside the diamond, because that is what the clip is for
            for (int oy = 0; oy < NeighbourhoodFoliage.MapSize; oy++)
            {
                for (int ox = 0; ox < NeighbourhoodFoliage.MapSize; ox++)
                {
                    if (NeighbourhoodFoliage.IsTreeable(map, ox, oy))
                    {
                        Assert.True(NeighbourhoodCityMeshBuilder.IsInsideDiamond(ox, oy),
                            "a tree was placed at " + ox + "," + oy + ", which is off the drawn diamond");
                    }
                }
            }
        }

        [Fact]
        public void ThePlanCountsEveryTileOutsideTheClipRatherThanTheOnesItWalkedPast()
        {
            // The count used to stop at the first treeable tile in each chunk, so the number the log
            // printed was a lower bound presented as exact. On a uniform forest it is now simply
            // "every forested, road-free tile outside the diamond", which is countable by hand.
            var map = NeighbourhoodMap.Create(NeighbourhoodFoliage.MapSize);
            for (int i = 0; i < map.ForestDensityData.Length; i++) map.ForestDensityData[i] = 255;

            var expectedOutside = 0;
            var expectedInside = 0;
            for (int oy = 0; oy < NeighbourhoodFoliage.MapSize; oy++)
            {
                for (int ox = 0; ox < NeighbourhoodFoliage.MapSize; ox++)
                {
                    if (NeighbourhoodCityMeshBuilder.IsInsideDiamond(ox, oy)) expectedInside++;
                    else expectedOutside++;
                }
            }

            var plan = NeighbourhoodFoliagePlan.For(map, NeighbourhoodFoliage.TreeBudget);
            Assert.Equal(expectedOutside, plan.TilesOutsideClip);
            // the clip is the map inset by ClipInset on every side (audit O1), so what is outside it
            // is exactly that border
            var square = NeighbourhoodFoliage.MapSize * NeighbourhoodFoliage.MapSize;
            var inner = NeighbourhoodCityMeshBuilder.DiamondRight(0) - NeighbourhoodCityMeshBuilder.DiamondLeft(0) - 1;
            Assert.Equal(square - inner * inner, expectedOutside);
            Assert.InRange(expectedInside * 100 / square, 85, 95);

            // and the eligible chunks are exactly the ones with at least one drawn, forested tile
            var drawn = new bool[NeighbourhoodFoliage.ChunksPerAxis * NeighbourhoodFoliage.ChunksPerAxis];
            var drawnCount = 0;
            for (int oy = 0; oy < NeighbourhoodFoliage.MapSize; oy++)
            {
                for (int ox = 0; ox < NeighbourhoodFoliage.MapSize; ox++)
                {
                    if (!NeighbourhoodCityMeshBuilder.IsInsideDiamond(ox, oy)) continue;
                    var chunk = (ox / NeighbourhoodFoliage.ChunkSize)
                        + (oy / NeighbourhoodFoliage.ChunkSize) * NeighbourhoodFoliage.ChunksPerAxis;
                    if (drawn[chunk]) continue;
                    drawn[chunk] = true;
                    drawnCount++;
                }
            }
            Assert.Equal(drawnCount, plan.EligibleChunks);
            for (int chunk = 0; chunk < drawn.Length; chunk++)
            {
                Assert.True(drawn[chunk] == plan.IsEligible(chunk),
                    "chunk " + chunk + " (cx=" + chunk % NeighbourhoodFoliage.ChunksPerAxis
                    + ", cy=" + chunk / NeighbourhoodFoliage.ChunksPerAxis + ") should "
                    + (drawn[chunk] ? "" : "not ") + "be eligible");
            }
            // and the list is ascending, which is what the binary search behind IsEligible and TreesFor needs.
            // The tile walk is row-major and comes out ascending only until the diamond's left edge
            // starts retreating: row 11 reaches chunks 18 and 19 and row 20 reaches 17, so 17 is
            // added after 18 - and an unsorted list loses trees without losing a count.
            for (int i = 1; i < plan.Eligible.Length; i++)
            {
                Assert.True(plan.Eligible[i] > plan.Eligible[i - 1],
                    "the eligible chunk list is not ascending at " + i);
            }
            for (int chunk = 0; chunk < drawn.Length; chunk++)
            {
                if (!drawn[chunk]) continue;
                Assert.True(plan.TreesFor(chunk) > 0,
                    "chunk " + chunk + " is eligible but gets no trees, which is what an unsorted"
                    + " eligible list looks like from the outside");
            }
        }

        [Fact]
        public void AChunkWithNoGroundUnderItsForestIsNotEligible()
        {
            // The plan asks the same question the build asks, so a map whose forest sits on
            // NeighbourhoodTerrain.Nothing - which the ground pass skips - has no trees at all rather
            // than a wood floating over nothing.
            var map = NeighbourhoodMap.Create(NeighbourhoodFoliage.MapSize);
            for (int i = 0; i < map.ForestDensityData.Length; i++)
            {
                map.ForestDensityData[i] = 255;
                map.TerrainType[i] = NeighbourhoodTerrain.Nothing;
            }

            var plan = NeighbourhoodFoliagePlan.For(map, NeighbourhoodFoliage.TreeBudget);
            Assert.Equal(0, plan.EligibleChunks);
            Assert.Equal(0, plan.SharePerChunk);
        }

        [Fact]
        public void ATreeStandsOnTheGroundUnderItAndNotOnItsTilesCorner()
        {
            // CityFoliage.GenerateChunk:239-249 interpolates the elevation across the tile twice -
            // four cubics down y and then one across x - rather than reading the tile's own byte. On
            // flat ground the two agree; on a slope they do not, and a tree that read the corner
            // floats above the terrain or sinks into it by up to a tile of fall.
            var map = NeighbourhoodMap.Create(NeighbourhoodFoliage.MapSize);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 60;

            // flat: every point in the tile is the tile's own height
            var flat = 256;
            Assert.Equal(NeighbourhoodCityMeshBuilder.WorldHeightFor(60),
                NeighbourhoodFoliage.GroundHeightAt(map, flat, flat, 0.5f, 0.5f), 4);
            Assert.Equal(NeighbourhoodCityMeshBuilder.WorldHeightFor(60),
                NeighbourhoodFoliage.GroundHeightAt(map, flat, flat, 0f, 0f), 4);

            // sloped along x: the tile's own byte is the sample at its own corner, and the middle of
            // the tile is between two of them. A two-units-per-tile ramp is enough to see the
            // difference - one unit is not, because the byte is quantised and the Catmull-Rom
            // through a ramp of one is the ramp.
            var centre = 120;
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = 0;
            for (int y = 0; y < map.Height; y++)
            {
                for (int x = 0; x < map.Width; x++)
                {
                    map.ElevationData[y * map.Width + x] = (byte)Math.Min(255, 2 * x);
                }
            }

            var corner = NeighbourhoodFoliage.GroundHeightAt(map, centre, centre, 0f, 0f);
            Assert.Equal(NeighbourhoodCityMeshBuilder.WorldHeightFor(
                    map.ElevationData[map.Index(centre, centre)]),
                corner, 4);

            var middle = NeighbourhoodFoliage.GroundHeightAt(map, centre, centre, 0.5f, 0.5f);
            Assert.True(middle > corner,
                "a slope has to raise the middle of the tile above its own corner: " + middle
                + " vs " + corner);
            // and by about half a tile of fall, which is what a two-unit-per-tile ramp is
            Assert.InRange(middle - corner, 0.05f, 0.5f);

            // the interpolation is two-dimensional: along y the map is flat, so it changes nothing
            Assert.Equal(corner, NeighbourhoodFoliage.GroundHeightAt(map, centre, centre, 0f, 1f), 4);
            // and it tracks the ramp as the tile moves up it
            var across = NeighbourhoodFoliage.GroundHeightAt(map, centre + 2, centre, 0.5f, 0.5f);
            Assert.True(across > middle, "two tiles up the slope is higher: " + across);

            Assert.Equal(0f, NeighbourhoodFoliage.GroundHeightAt(null, centre, centre, 0.5f, 0.5f));
        }

        [Fact]
        public void AChunksBoxContainsTheWholeTreeAndNotOnlyTheTileItStandsOn()
        {
            // The defect: the box stopped at the maximum ground height, so a tree was culled at the
            // exact moment its canopy came on screen. TSO's own box (CityFoliage.cs:178) happens to be
            // twice as wide as its chunk on both axes and so dodges the horizontal half of this; the
            // vertical half it does not dodge.
            var model = NeighbourhoodFoliage.ModelBoundsOf(new[]
            {
                new[]
                {
                    new DGRP3DVert(new Vector3(-30f, 0f, -30f), Vector3.Zero, Vector2.Zero),
                    new DGRP3DVert(new Vector3(30f, 0f, 30f), Vector3.Zero, Vector2.Zero),
                    //a 75-unit model is one tile tall at ModelScale - the 1/75f CityFoliage.cs:185 uses
                    new DGRP3DVert(new Vector3(0f, 75f, 0f), Vector3.Zero, Vector2.Zero),
                }
            });
            Assert.NotNull(model);
            // 75 model units at 1/75f is one world tile; the scale is not exactly representable so
            // the assertion is to four places, not to the bit
            Assert.Equal(1f, model.Value.Max.Y, 4);
            Assert.Equal(0.4f, model.Value.Max.X, 4);
            Assert.Equal(-0.4f, model.Value.Min.X, 4);

            var box = NeighbourhoodFoliage.ChunkBoundsFor(4, 5, model);
            // the 16x16 tile square, widened by the model's own sideways reach on all four sides
            Assert.Equal(4 * NeighbourhoodFoliage.ChunkSize - 0.4f, box.Min.X, 4);
            Assert.Equal(5 * NeighbourhoodFoliage.ChunkSize - 0.4f, box.Min.Z, 4);
            Assert.Equal(5 * NeighbourhoodFoliage.ChunkSize + 0.4f, box.Max.X, 4);
            Assert.Equal(6 * NeighbourhoodFoliage.ChunkSize + 0.4f, box.Max.Z, 4);

            // the canopy clears the highest ground on the map
            Assert.True(box.Max.Y > NeighbourhoodCityMeshBuilder.WorldHeightFor(255f),
                "a box that stops at the ground culls the canopy off every tree");
            // and the tallest canopy on the highest tile is inside the box, which is the assertion
            // that the old box failed
            Assert.True(box.Contains(new Vector3(
                    5 * NeighbourhoodFoliage.ChunkSize,
                    NeighbourhoodCityMeshBuilder.WorldHeightFor(255f) + model.Value.Max.Y,
                    6 * NeighbourhoodFoliage.ChunkSize))
                    != ContainmentType.Disjoint,
                "the tallest tree on the highest tile has to be in the box");
            // and so is the trunk of the tree in the corner of the chunk, offset by the canopy's
            // own sideways reach
            Assert.True(box.Contains(new Vector3(
                    4 * NeighbourhoodFoliage.ChunkSize - model.Value.Min.X,
                    model.Value.Min.Y,
                    5 * NeighbourhoodFoliage.ChunkSize + model.Value.Max.Z))
                    != ContainmentType.Disjoint,
                "a tree's canopy hangs over the edge of the chunk it stands in");

            // no models loaded gives the ground alone rather than an empty box
            Assert.Null(NeighbourhoodFoliage.ModelBoundsOf(null));
            Assert.Null(NeighbourhoodFoliage.ModelBoundsOf(new DGRP3DVert[0][]));
            Assert.Null(NeighbourhoodFoliage.ModelBoundsOf(new[] { null, new DGRP3DVert[0] }));
            var bare = NeighbourhoodFoliage.ChunkBoundsFor(0, 0, null);
            Assert.Equal(NeighbourhoodCityMeshBuilder.WorldHeightFor(255f), bare.Max.Y, 4);
            Assert.Equal(0f, bare.Min.X, 4);
        }

        [Fact]
        public void TheDepthBiasIsTheCitysOwnThreeDimensionalOne()
        {
            // CityFoliage.cs:133: -0.12f * DepthBiasScale, and CityCamera3D.cs:83-89 says that scale
            // is 1 for a 3D camera. The neighbourhood view is a 3D camera.
            Assert.Equal(-0.12f, NeighbourhoodFoliage.TreeDepthBias, 4);
        }
    }
}