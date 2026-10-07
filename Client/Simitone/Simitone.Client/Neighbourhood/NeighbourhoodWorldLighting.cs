/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Globalization;
using System.Text;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// B8's <see cref="WorldLightingState"/> and <see cref="WorldWeather"/> turned into the
    /// numbers a lot view actually draws with: where the light comes from, what colour it is, what
    /// the lot's background is, how far you can see, and how the sky is tinted.
    ///
    /// <para>
    /// <b>It is a pure function of two values.</b> No graphics device, no textures, no clock, no
    /// seed, no state between calls, and nothing from <c>Microsoft.Xna.Framework</c> - the direction
    /// is three doubles and the colours are <see cref="WorldColour"/>, so the whole of this is
    /// testable with no lot, no content and no renderer, and the screen that applies it is one
    /// <c>new Vector3(...)</c> and three <c>Color</c> channels away. A type that carried a
    /// <c>Vector3</c> would have been one line shorter here and one assembly reference that the
    /// headless tests would have had to load to say anything about the arithmetic.
    /// </para>
    ///
    /// <para>
    /// <b>Why the weather is folded in here, when
    /// <see cref="WorldLightingState"/> deliberately excludes it.</b> The features type is "what
    /// the world looks like lit, as a function of nothing but the clock", and that is the right
    /// boundary: two lots in the same neighbourhood at the same instant are lit identically, and
    /// the way to guarantee that is for the answer not to depend on a lot. This type is the other
    /// half - the *combination*, which the features file says is "a multiply at the call site rather
    /// than a hidden term in here". Putting it in one pure function rather than in ten call sites
    /// is what keeps the lot view and the 3D view from disagreeing about what overcast noon looks
    /// like; it is still a function of the same two values, and it still reads neither a lot nor a
    /// renderer.
    /// </para>
    ///
    /// <para>
    /// <b>The mapping, in the order it is applied.</b>
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Which disc is being drawn.</b> By the trigonometry, not by
    ///     <see cref="WorldLightingState.IsNight"/>. The sun's angle crosses zero at
    ///     <see cref="NeighbourhoodWorldFeatures.SunriseHour"/> and
    ///     <see cref="NeighbourhoodWorldFeatures.SunsetHour"/>, and that - not the hour-based
    ///     "is it dark" - is the question a renderer asks when it picks the sun texture. The
    ///     features file says so itself: <see cref="WorldTimeOfDay.IsNight"/> is a statement about
    ///     two hours, and the angle is the continuous quantity. Below the horizon the light is a
    ///     moon on its own arc - a half sine from the horizon at sunset, up to
    ///     <see cref="MaxMoonElevationDegrees"/> in the middle of the night and back down to the
    ///     horizon at sunrise - which is the same construction the engine's own sun model uses and
    ///     which joins the day's arc without a step at either end.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Direction.</b> The compass azimuth from the features, moved into the lot view's own
    ///     frame by <see cref="LotViewAzimuthOffsetDegrees"/>: <c>+X</c> is east, <c>+Z</c> is
    ///     south, <c>+Y</c> is up. The result is a unit vector pointing from the lot towards the
    ///     light, which is what <c>LMapBatch.SunVector</c> and the sky dome's billboard want. The
    ///     two other shapes the lot view asks of a sun are the engine's own one-liners off these
    ///     three numbers and are not duplicated here: the grass shader's
    ///     <c>LightVec</c> is <c>(z, 1, -x)</c> and the flat light map's <c>LightDirection</c> is
    ///     <c>(-z, -y, x)</c>.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Sun strength and colour.</b> The disc's own height term, smoothstepped over
    ///     <see cref="HorizonFadeSin"/> so a sun on the horizon contributes nothing rather than
    ///     dividing by its own height; cut by cloud; and scaled by
    ///     <see cref="MoonIntensityScale"/> when the disc is a moon. The height term is read from
    ///     the sun's <em>own</em> elevation and not from the one cloud lifts it to, so a cloudy
    ///     sunrise is still a sunrise rather than a half-strength sun straight overhead. The
    ///     <em>colour</em> is the features' own <see cref="WorldLightingState.SunColour"/>, which
    ///     already ramps night to noon and so already has a dawn and a dusk in it, multiplied by
    ///     the strength. Note that
    ///     <see cref="WorldLightingState.SunIntensity"/> is deliberately <em>not</em> used: it is a
    ///     daylight figure that reaches zero twelve degrees below the horizon and stays there all
    ///     night, and the lot view draws a moon. The two are derived from the same angle, so they
    ///     cannot disagree about where the sun is - only about what they are being asked.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Fill light.</b> The features' ambient colour and strength, with the colour pushed
    ///     towards <see cref="OvercastGrey"/> by the cloud and the strength cut by
    ///     <see cref="OvercastAmbientCut"/>. "Pushed towards" is the level-preserving
    ///     <see cref="GreyShift"/>, not a lerp to the grey: a straight lerp would make a fully
    ///     overcast midnight three times brighter than a clear one, which is the opposite of what
    ///     cloud does to a fill light.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Air.</b> Two numbers, because they answer two different questions.
    ///     <see cref="Haze"/> is how grey the air is - cloud or fog - and it greys the fog colour.
    ///     <see cref="Visibility"/> is how much of the view is cut, and only rain and fog do that
    ///     (an overcast sky is dim, not opaque), so it alone moves
    ///     <see cref="FogDistance"/> between <see cref="ClearFogDistance"/> and
    ///     <see cref="ObscuredFogDistance"/>. Both endpoints are the engine's own: a clear lot's
    ///     fog reaches 300 lots out and a wet one's 15.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Background and sky.</b> <see cref="OutsideColour"/> is the fill colour - with a clear
    ///     sky it <em>is</em> the fill colour, because the engine derives
    ///     <c>WorldState.OutsideColor</c> and its ambient from one ramp and pretending otherwise
    ///     would be a second opinion - pulled all the way onto the fog colour when there is fog,
    ///     because fog is what the background becomes.
    ///     <see cref="SkyTint"/> is the multiplier for the sky dome: neutral at midday, warm at the
    ///     two horizons (warmer on the sunset side, as the engine's own table has it), blue over
    ///     moonlit value at night, and grey-shifted under cloud. <see cref="SkyStrip"/> is the
    ///     position along the same fourteen-stop sky texture the dome reads, running
    ///     <see cref="SunriseStrip"/> to <see cref="SunsetStrip"/> across the day and on to
    ///     <see cref="NightStrip"/> across the night.
    ///   </description></item>
    /// </list>
    ///
    /// <para>
    /// <b>Every number is bounded.</b> Directions are unit vectors, distances and intensities are
    /// in range by construction, and the colours are <see cref="WorldColour"/>, which clamps each
    /// channel - so a curve that overshoots saturates rather than producing a negative light.
    /// </para>
    ///
    /// <para>
    /// <b>Thread affinity: none, because there is nothing to share.</b> A value type with a
    /// function that reads two arguments has no state to race on, which is a stronger statement
    /// than the game-thread rule every mutable type in this namespace makes.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodWorldLighting : IEquatable<NeighbourhoodWorldLighting>
    {
        // ---- where the disc is ------------------------------------------------------------------------

        /// <summary>
        /// How high the moon gets in the middle of the night, in degrees. Lower than the sun's own
        /// <see cref="NeighbourhoodWorldFeatures.MaxSunElevationDegrees"/>, because a moon that
        /// stands as high as the noon sun is not a moon.
        /// </summary>
        public const double MaxMoonElevationDegrees = 60.0;

        /// <summary>
        /// The elevation an overcast sky's light comes from, in degrees: 75, or 5π/12. Under cloud
        /// the light is scattered by the whole dome rather than arriving from a point, so the
        /// brightest direction is close to the zenith, and pulling the disc's elevation towards this
        /// is what flattens the shadows. Not applied at night, for the same reason the engine does
        /// not: there is no sun to be behind the cloud.
        /// </summary>
        public const double OvercastPeakElevationDegrees = 75.0;

        /// <summary>
        /// The sine of the disc's elevation at which it is fully up. Below this the contribution
        /// ramps to zero, which is what stops a sun sitting exactly on the horizon from being either
        /// a black disc or a division by its own height component. 0.12 is the engine's own
        /// <c>HorizonFade</c>, so the two models fade at the same rate.
        /// </summary>
        public const double HorizonFadeSin = 0.12;

        /// <summary>
        /// How much of the sun's own light the moon gets. The moon's arc is its own and its colour
        /// is the features' night colour, but the sky dome draws the disc additively, so a
        /// moonlight-strength disc has to be brought down to a moonlight strength. The engine's own
        /// <c>NightIntensity</c>.
        /// </summary>
        public const double MoonIntensityScale = 0.45;

        /// <summary>
        /// How much cloud cover cuts the sun's own strength, at full cover. Not a total eclipse: a
        /// cloudy sky is not a dark one, and a lot lit by cloud alone should still read as daytime.
        /// </summary>
        public const double OvercastSunCut = 0.75;

        /// <summary>
        /// How much cloud cover cuts the fill light's strength, at full cover. Much gentler than
        /// <see cref="OvercastSunCut"/>, and deliberately so: cloud raises the floor and lowers the
        /// ceiling, it does not turn the sun off.
        /// </summary>
        public const double OvercastAmbientCut = 0.30;

        // ---- the lot view's frame ----------------------------------------------------------------------

        /// <summary>
        /// The turn from a compass bearing to the lot view's azimuth, in degrees. The features
        /// measure azimuth clockwise from north; the lot view measures from <c>+X</c> towards
        /// <c>+Z</c>, and <c>+X</c> is east. So a compass bearing of 90 (east) is a lot-view
        /// azimuth of 0, and the two differ by exactly this.
        /// </summary>
        public const double LotViewAzimuthOffsetDegrees = 90.0;

        /// <summary>
        /// Where sunrise sits on the sky texture's strip, as a fractional index. The engine's own
        /// fourteen-stop table runs sunrise at 4, the peak at the 6/7 seam and sunset at 10.
        /// </summary>
        public const double SunriseStrip = 4.0;

        /// <summary>Where sunset sits on the strip. See <see cref="SunriseStrip"/>.</summary>
        public const double SunsetStrip = 10.0;

        /// <summary>
        /// Where the strip's deep-night stop is, as a fractional index. The strip closes after
        /// thirteen steps, because stop 13 and stop 0 are the same column of the texture.
        /// </summary>
        public const double NightStrip = 13.0;

        // ---- the air -----------------------------------------------------------------------------------

        /// <summary>
        /// How far a clear lot's fog reaches, in lot-view world units: 300 lots of 75. The engine's
        /// own clear-weather figure, taken from the alpha it writes into its fog colour.
        /// </summary>
        public const double ClearFogDistance = 300.0 * 75.0;

        /// <summary>
        /// How far the fog reaches when something is actually obscuring the view: 15 lots of 75.
        /// The engine's own wet-weather figure.
        /// </summary>
        public const double ObscuredFogDistance = 15.0 * 75.0;

        /// <summary>
        /// How far a clear sky's fog is pulled towards <see cref="ClearFogTint"/>. The engine tints
        /// its fog towards a fixed cold blue rather than reusing the sky colour, and 0.6 is about
        /// as blue as a fog actually looks.
        /// </summary>
        public const double ClearFogShare = 0.60;

        // ---- colours -----------------------------------------------------------------------------------

        /// <summary>
        /// The grey the engine already darkens outdoor light towards when it rains
        /// (<c>WeatherController</c>'s 159,164,181, and the sun model's overcast tint). Named here
        /// so that a rainy lot is all one grey rather than a sun tinted one way and the rest of the
        /// lot another.
        /// </summary>
        public static readonly WorldColour OvercastGrey = WorldColour.FromBytes(159, 164, 181);

        /// <summary>
        /// The cold blue a clear sky's fog is tinted towards: the engine's own 0x80C0FF fog
        /// multiplier, read as a colour rather than as a product.
        /// </summary>
        public static readonly WorldColour ClearFogTint = WorldColour.FromBytes(0x80, 0xC0, 0xFF);

        /// <summary>The sky at midday: neutral, so the sky texture's own gradient is all you see.</summary>
        public static readonly WorldColour DaySkyTint = WorldColour.FromBytes(255, 255, 255);

        /// <summary>The sky at sunrise: warm, and a little paler than the sunset's.</summary>
        public static readonly WorldColour DawnSkyTint = WorldColour.FromBytes(255, 199, 158);

        /// <summary>The sky at sunset: warmer than the dawn's, because the sun is going down.</summary>
        public static readonly WorldColour DuskSkyTint = WorldColour.FromBytes(255, 173, 133);

        /// <summary>The sky at night: a deep blue over the moonlit value.</summary>
        public static readonly WorldColour NightSkyTint = WorldColour.FromBytes(87, 102, 153);

        // ---- derived inputs ----------------------------------------------------------------------------

        /// <summary>Sunrise as a fraction of a day, read from the features' own hour.</summary>
        public static readonly double SunriseDayFraction =
            NeighbourhoodWorldFeatures.SunriseHour / (double)NeighbourhoodWorldFeatures.HoursPerDay;

        /// <summary>Sunset as a fraction of a day, read from the features' own hour.</summary>
        public static readonly double SunsetDayFraction =
            NeighbourhoodWorldFeatures.SunsetHour / (double)NeighbourhoodWorldFeatures.HoursPerDay;

        // ---- the answer ---------------------------------------------------------------------------------

        /// <summary>Which band of the day this is. The features' own answer, restated.</summary>
        public WorldDayPhase Phase { get; }

        /// <summary>
        /// Is it dark? The features' <em>hour-based</em> answer - from sunset until dawn - which is
        /// the question "are the lamps on", and not the question the mapping above asked.
        /// </summary>
        public bool IsNight { get; }

        /// <summary>
        /// The unit vector from the lot towards the light source, in the lot view's frame:
        /// <c>+X</c> east, <c>+Z</c> south, <c>+Y</c> up. This is a moon's direction when
        /// <see cref="IsSunUp"/> is false. The three components are below and their squares sum to
        /// one; they are fields rather than one value type so that nothing in this file has to
        /// reference the drawing assembly to say where the sun is.
        /// </summary>
        public double SunDirectionX { get; }

        /// <summary>
        /// The height of the light, positive up. Always at or above zero: the disc is above the
        /// skyline, which is the fold the engine's own light map has always applied to a sun that
        /// has gone under.
        /// </summary>
        public double SunDirectionY { get; }

        /// <summary>The north-south part of the direction towards the light. Positive is south.</summary>
        public double SunDirectionZ { get; }

        /// <summary>
        /// The disc's elevation above the horizon in degrees, after the moon's arc and the overcast
        /// lift. The restated form of the <c>Y</c> component, for a log and for a test.
        /// </summary>
        public double SunElevationDegrees { get; }

        /// <summary>
        /// Where the disc is, in the lot view's azimuth: degrees from <c>+X</c> towards <c>+Z</c>,
        /// always 0..360. The features' compass bearing moved by
        /// <see cref="LotViewAzimuthOffsetDegrees"/>.
        /// </summary>
        public double SunAzimuthDegrees { get; }

        /// <summary>
        /// Is the sun above the horizon? The trigonometric question, asked of the features' own
        /// angle, and the one that chooses between the sun and the moon. Distinct from
        /// <see cref="IsNight"/>, which is an hour-based question about the lamps.
        /// </summary>
        public bool IsSunUp { get; }

        /// <summary>The sun's own colour, already scaled by <see cref="SunIntensity"/>, 0..1 a channel.</summary>
        public WorldColour SunColour { get; }

        /// <summary>
        /// How strongly the sun writes light, 0..1 - zero at the horizon, one at the top of the arc
        /// under a clear sky, <see cref="MoonIntensityScale"/> of that for the moon and
        /// <see cref="OvercastSunCut"/> off it at full cloud.
        /// </summary>
        public double SunIntensity { get; }

        /// <summary>The world's fill light colour, grey-shifted towards <see cref="OvercastGrey"/> by cloud.</summary>
        public WorldColour AmbientColour { get; }

        /// <summary>The world's fill light strength, 0..1, cut by cloud.</summary>
        public double AmbientIntensity { get; }

        /// <summary>
        /// What the lot's background is: the fill colour under a clear sky, and the fog's colour
        /// where there is fog to swallow the distance.
        /// </summary>
        public WorldColour OutsideColour { get; }

        /// <summary>The colour the view fades into, and how grey the air has made it.</summary>
        public WorldColour FogColour { get; }

        /// <summary>
        /// How far the view fades out, in lot-view world units, between
        /// <see cref="ClearFogDistance"/> and <see cref="ObscuredFogDistance"/>.
        /// </summary>
        public double FogDistance { get; }

        /// <summary>How grey the air is, 0..1: cloud or fog. Not the same as how far you can see.</summary>
        public double Haze { get; }

        /// <summary>
        /// How much of the view is cut, 0..1: rain or fog. Cloud does not appear in this one,
        /// because an overcast sky is dim rather than opaque.
        /// </summary>
        public double Visibility { get; }

        /// <summary>
        /// The cloud cover the whole mapping is coupled through, 0..1. Straight off
        /// <see cref="WorldWeather.CloudCover"/>: B8's weather has no separate dimming term, so the
        /// cover is the one coupling and inventing a second one here would be a second opinion
        /// about weather the features type already drew.
        /// </summary>
        public double Overcast { get; }

        /// <summary>
        /// The multiplier for the sky dome, 0..1 a channel. Warm at the two horizons, blue at night,
        /// grey-shifted under cloud.
        /// </summary>
        public WorldColour SkyTint { get; }

        /// <summary>
        /// Where on the sky texture's fourteen stops this is, as a fractional index between
        /// <see cref="SunriseStrip"/> and <see cref="NightStrip"/>. Feed it to
        /// <c>AbstractSkyDome.OutsideSkyStrip</c> to get the texture column, which is what the dome
        /// does with a sun state of its own.
        /// </summary>
        public double SkyStrip { get; }

        private NeighbourhoodWorldLighting(WorldDayPhase phase, bool isNight, double sunDirectionX,
            double sunDirectionY, double sunDirectionZ, double sunElevationDegrees,
            double sunAzimuthDegrees, bool isSunUp, WorldColour sunColour, double sunIntensity,
            WorldColour ambientColour, double ambientIntensity, WorldColour outsideColour,
            WorldColour fogColour, double fogDistance, double haze, double visibility,
            double overcast, WorldColour skyTint, double skyStrip)
        {
            Phase = phase;
            IsNight = isNight;
            SunDirectionX = sunDirectionX;
            SunDirectionY = sunDirectionY;
            SunDirectionZ = sunDirectionZ;
            SunElevationDegrees = sunElevationDegrees;
            SunAzimuthDegrees = sunAzimuthDegrees;
            IsSunUp = isSunUp;
            SunColour = sunColour;
            SunIntensity = sunIntensity;
            AmbientColour = ambientColour;
            AmbientIntensity = ambientIntensity;
            OutsideColour = outsideColour;
            FogColour = fogColour;
            FogDistance = fogDistance;
            Haze = haze;
            Visibility = visibility;
            Overcast = overcast;
            SkyTint = skyTint;
            SkyStrip = skyStrip;
        }

        /// <summary>
        /// The lot view's lighting for a world's lighting and weather.
        ///
        /// <para>
        /// <b>Pure, and that is the whole claim.</b> The same two values give the same answer, field
        /// for field, in any frame, in any order, on any machine, and nothing here is cached or
        /// accumulated - which is what lets the world recompute it from its features on demand
        /// rather than keep a second copy that could go stale.
        /// </para>
        ///
        /// <para>
        /// <b>Both arguments are optional in the only sense that matters</b>: a world whose features
        /// have never been built hands over <c>default</c> for each, and gets a
        /// <c>default</c>-shaped answer rather than an exception. That is deliberate - a screen that
        /// asks a world with the capability off should get "no lighting" and gate on the flag, not a
        /// throw on the first frame of a lot load.
        /// </para>
        /// </summary>
        /// <param name="lighting">
        /// The world's lighting state. <c>default</c> is accepted and means "nothing has been
        /// derived yet".
        /// </param>
        /// <param name="weather">The world's weather. <c>default</c> is accepted: no cloud, no rain.</param>
        /// <returns>The values a lot view draws with.</returns>
        public static NeighbourhoodWorldLighting From(WorldLightingState lighting, WorldWeather weather)
        {
            var time = lighting.TimeOfDay;
            var overcast = Clamp01(weather.CloudCover);
            var wetness = Clamp01(weather.Precipitation);
            var fog = FogAmount(weather);

            // 1. Which disc, and how high. Below the horizon there is no sun to draw, so the light
            // is a moon on its own half sine - which starts and ends at the horizon, at exactly the
            // two instants the sun's own arc does, so the two join up with nothing to see at the
            // join. The test is the sign of the angle and not the hour: Math.Cos of a quarter turn
            // is 6.1e-17 rather than exactly zero, so at 06:00:00.000 and 18:00:00.000 the sun
            // counts as up with an elevation of 4e-15 degrees - which is the same answer the other
            // branch would give, and is why nothing has to be done about it.
            var sunUp = lighting.SunAngleDegrees > 0.0;
            var elevation = sunUp
                ? lighting.SunAngleDegrees
                : Math.Sin(NightArc(time.DayFraction) * Math.PI) * MaxMoonElevationDegrees;

            // 2. Where the light comes *from*, and how much of the sun is actually above the
            //horizon. Two different numbers, and keeping them apart is the whole of the dawn: cloud
            //lifts the direction towards the zenith, because a lit dome has no single source, but
            //it must not lift the sun's own height, because a sun on the horizon at 06:00
            //contributes nothing under a clear sky and nothing under an overcast one either.
            var litElevation = elevation;
            if (sunUp)
            {
                litElevation += (OvercastPeakElevationDegrees - litElevation) * overcast;
            }

            // 3. Into the lot view's frame. sin of the elevation for the height, cosine of it for
            //the horizontal part, and the features' compass bearing turned by the frame's offset.
            var elevationRadians = litElevation * DegreesToRadians;
            var height = Math.Sin(elevationRadians);
            var horizontal = Math.Cos(elevationRadians);
            var azimuthRadians = (time.SunAzimuthDegrees - LotViewAzimuthOffsetDegrees) * DegreesToRadians;

            // 4. The disc's own strength, from the sun's OWN height rather than the lifted one:
            //up off the horizon, cut by cloud, a moon's share after dark. A plain ramp in the sine
            //of the elevation, smoothstepped so it has no corner at the horizon, and never a
            //division - which is the whole reason it is a ramp and not the engine's unbounded shadow
            //length. The ramp is about twenty-five minutes wide at this latitude, which is about as
            //long as a real sunrise takes to stop mattering.
            var up = SmoothStep(Clamp01(Math.Sin(elevation * DegreesToRadians) / HorizonFadeSin));
            var sunIntensity = up
                * (1.0 - OvercastSunCut * overcast)
                * (sunUp ? 1.0 : MoonIntensityScale);

            // 5. The fill light. The colour is grey-shifted rather than lerped towards the grey so
            //that a white-out at midnight darkens nothing and brightens nothing: the shift keeps
            //the level and changes the hue, which is what cloud does to a light that is already
            //moonlit.
            var ambientColour = GreyShift(lighting.AmbientColour, OvercastGrey, overcast);
            var ambientIntensity = lighting.AmbientIntensity * (1.0 - OvercastAmbientCut * overcast);

            // 6. The air. Two numbers rather than one, because "how grey is it" and "how far can I
            //see" are different questions and an overcast sky only ever answers the first.
            var haze = Math.Max(overcast, fog);
            var visibility = Math.Max(fog, wetness);
            var clearFog = WorldColour.Lerp(ambientColour, ClearFogTint, ClearFogShare);
            var fogColour = GreyShift(clearFog, OvercastGrey, haze);
            var fogDistance = ClearFogDistance
                + (ObscuredFogDistance - ClearFogDistance) * visibility;

            //Fog is the one weather that replaces the background outright rather than tinting it.
            var outsideColour = WorldColour.Lerp(ambientColour, fogColour, fog);

            // 7. The sky's own multiplier. Warmth comes from the features' daylight ramp, which
            //reaches zero twelve degrees below the horizon and so is zero for the whole night; the
            //step to the night blue is at the horizon crossing, which is the same step the engine's
            //own sun model has and is not a defect in either.
            var warmth = 1.0 - Clamp01(lighting.SunIntensity);
            var horizonTint = time.DayFraction < 0.5 ? DawnSkyTint : DuskSkyTint;
            var tint = sunUp
                ? WorldColour.Lerp(DaySkyTint, horizonTint, warmth)
                : NightSkyTint;
            var skyTint = GreyShift(tint, OvercastGrey, overcast);

            // 8. Where on the sky texture. Sunrise to sunset across the day, sunset on round to the
            //night stop across the night, both clamped so a caller cannot index off the end of the
            //strip however odd its clock.
            var strip = sunUp
                ? SunriseStrip + DayArc(time.DayFraction) * (SunsetStrip - SunriseStrip)
                : SunsetStrip + NightArc(time.DayFraction) * (NightStrip - SunsetStrip);

            return new NeighbourhoodWorldLighting(
                time.Phase,
                time.IsNight,
                Math.Cos(azimuthRadians) * horizontal,
                height,
                Math.Sin(azimuthRadians) * horizontal,
                litElevation,
                (time.SunAzimuthDegrees - LotViewAzimuthOffsetDegrees + 360.0) % 360.0,
                sunUp,
                Scale(lighting.SunColour, sunIntensity),
                sunIntensity,
                ambientColour,
                ambientIntensity,
                outsideColour,
                fogColour,
                fogDistance,
                haze,
                visibility,
                overcast,
                skyTint,
                strip);
        }

        /// <summary>
        /// How foggy it is, 0..1, as a continuous reading of the two conditions the features type
        /// itself uses to classify a segment as <see cref="WorldWeatherKind.Fog"/>: enough cloud,
        /// and not too much wind.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A product of two ramps that are each zero below the features' own thresholds, so this is
        /// exactly zero for any weather the features type would <em>not</em> have called fog, and
        /// rises above their thresholds as it goes further past them. That is deliberate: reading
        /// <see cref="WorldWeatherKind.Fog"/> directly would give a stepped answer that jumps from
        /// "no fog at all" to "as much fog as the model has" the moment one threshold was crossed,
        /// and a fog that arrives in one frame is a visible pop in the distance.
        /// </para>
        /// <para>
        /// It is a reading of the same two numbers the classifier reads, not a second classifier.
        /// </para>
        /// </remarks>
        private static double FogAmount(WorldWeather weather)
        {
            var coverSpan = 1.0 - WorldWeather.FogCover;
            if (coverSpan <= 0.0) return 0.0;
            var cloud = Clamp01((weather.CloudCover - WorldWeather.FogCover) / coverSpan);

            var windSpan = WorldWeather.FogMaxWind;
            if (windSpan <= 0.0) return 0.0;
            var still = Clamp01((WorldWeather.FogMaxWind - weather.WindSpeed) / windSpan);

            return cloud * still;
        }

        /// <summary>How far through the night it is: 0 at sunset, 1 at sunrise. Always 0..1.</summary>
        private static double NightArc(double dayFraction)
        {
            var length = 1.0 - SunsetDayFraction + SunriseDayFraction;
            if (length <= 0.0) return 0.0;
            var since = dayFraction >= SunsetDayFraction
                ? dayFraction - SunsetDayFraction
                : dayFraction + 1.0 - SunsetDayFraction;
            return Clamp01(since / length);
        }

        /// <summary>How far through the day it is: 0 at sunrise, 1 at sunset. Always 0..1.</summary>
        private static double DayArc(double dayFraction)
        {
            var span = SunsetDayFraction - SunriseDayFraction;
            if (span <= 0.0) return 0.0;
            return Clamp01((dayFraction - SunriseDayFraction) / span);
        }

        /// <summary>
        /// <paramref name="from"/> moved <paramref name="t"/> of the way towards
        /// <paramref name="towards"/>, with the target's brightness rescaled to
        /// <paramref name="from"/>'s first. A level-preserving grey shift: the hue moves and the
        /// exposure does not, which is the difference between a cloudy noon and a bright midnight.
        /// </summary>
        private static WorldColour GreyShift(WorldColour from, WorldColour towards, double t)
        {
            if (t <= 0.0) return from;
            var fromLum = Luminance(from);
            var towardsLum = Luminance(towards);
            var target = towardsLum <= 0.0
                ? towards
                : Scale(towards, fromLum / towardsLum);
            return WorldColour.Lerp(from, target, t);
        }

        /// <summary>Perceptual brightness, 0..1. The one colour-space opinion in this file.</summary>
        private static double Luminance(WorldColour colour)
        {
            return 0.2126 * colour.R + 0.7152 * colour.G + 0.0722 * colour.B;
        }

        /// <summary>
        /// A colour multiplied by a factor. The multiplier is not clamped, because
        /// <see cref="WorldColour"/> clamps the product - which is what makes an overshooting curve
        /// saturate rather than go negative or above white.
        /// </summary>
        private static WorldColour Scale(WorldColour colour, double factor)
        {
            var k = (float)factor;
            return new WorldColour(colour.R * k, colour.G * k, colour.B * k);
        }

        private static double SmoothStep(double t)
        {
            return t * t * (3.0 - 2.0 * t);
        }

        private const double DegreesToRadians = Math.PI / 180.0;

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value)) return 0.0;
            if (value < 0.0) return 0.0;
            return value > 1.0 ? 1.0 : value;
        }

        /// <summary>Two answers are equal when every field is.</summary>
        /// <param name="other">The other answer.</param>
        public bool Equals(NeighbourhoodWorldLighting other)
        {
            return Phase == other.Phase
                && IsNight == other.IsNight
                && SunDirectionX == other.SunDirectionX
                && SunDirectionY == other.SunDirectionY
                && SunDirectionZ == other.SunDirectionZ
                && SunElevationDegrees == other.SunElevationDegrees
                && SunAzimuthDegrees == other.SunAzimuthDegrees
                && IsSunUp == other.IsSunUp
                && SunColour == other.SunColour
                && SunIntensity == other.SunIntensity
                && AmbientColour == other.AmbientColour
                && AmbientIntensity == other.AmbientIntensity
                && OutsideColour == other.OutsideColour
                && FogColour == other.FogColour
                && FogDistance == other.FogDistance
                && Haze == other.Haze
                && Visibility == other.Visibility
                && Overcast == other.Overcast
                && SkyTint == other.SkyTint
                && SkyStrip == other.SkyStrip;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is NeighbourhoodWorldLighting && Equals((NeighbourhoodWorldLighting)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Phase;
                hash = (hash * 397) ^ (IsNight ? 1 : 0);
                hash = (hash * 397) ^ SunDirectionX.GetHashCode();
                hash = (hash * 397) ^ SunDirectionY.GetHashCode();
                hash = (hash * 397) ^ SunDirectionZ.GetHashCode();
                hash = (hash * 397) ^ SunElevationDegrees.GetHashCode();
                hash = (hash * 397) ^ SunAzimuthDegrees.GetHashCode();
                hash = (hash * 397) ^ (IsSunUp ? 1 : 0);
                hash = (hash * 397) ^ SunColour.GetHashCode();
                hash = (hash * 397) ^ SunIntensity.GetHashCode();
                hash = (hash * 397) ^ AmbientColour.GetHashCode();
                hash = (hash * 397) ^ AmbientIntensity.GetHashCode();
                hash = (hash * 397) ^ OutsideColour.GetHashCode();
                hash = (hash * 397) ^ FogColour.GetHashCode();
                hash = (hash * 397) ^ FogDistance.GetHashCode();
                hash = (hash * 397) ^ Haze.GetHashCode();
                hash = (hash * 397) ^ Visibility.GetHashCode();
                hash = (hash * 397) ^ Overcast.GetHashCode();
                hash = (hash * 397) ^ SkyTint.GetHashCode();
                hash = (hash * 397) ^ SkyStrip.GetHashCode();
                return hash;
            }
        }

        /// <summary>Two answers are equal when every field is.</summary>
        /// <param name="a">One answer.</param>
        /// <param name="b">The other.</param>
        public static bool operator ==(NeighbourhoodWorldLighting a, NeighbourhoodWorldLighting b)
        {
            return a.Equals(b);
        }

        /// <summary>The negation of <see cref="op_Equality"/>.</summary>
        /// <param name="a">One answer.</param>
        /// <param name="b">The other.</param>
        public static bool operator !=(NeighbourhoodWorldLighting a, NeighbourhoodWorldLighting b)
        {
            return !a.Equals(b);
        }

        /// <summary>
        /// Five lines for a log: the day, the disc, the fill, the air and the sky.
        ///
        /// <remarks>
        /// <b>Locale-independent</b>, for the reason every other <c>Describe</c> in this namespace
        /// is: the line is built from fields and invariant formats and reads the environment for
        /// nothing, so two machines asked for the same neighbourhood at the same minute produce the
        /// same text - which is what makes it worth diffing when somebody reports that one save is
        /// always in the rain.
        /// </remarks>
        /// </summary>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("lot view lighting: ").Append(Phase)
              .Append(IsNight ? " (night)" : string.Empty)
              .Append(IsSunUp ? ", sun" : ", moon")
              .Append('\n');

            sb.Append("  disc: elev ").Append(F(SunElevationDegrees, 1))
              .Append("deg az ").Append(F(SunAzimuthDegrees, 1))
              .Append("deg (").Append(F(SunDirectionX, 3)).Append(", ")
              .Append(F(SunDirectionY, 3)).Append(", ").Append(F(SunDirectionZ, 3))
              .Append("), ").Append(SunColour)
              .Append(" x").Append(F(SunIntensity, 3))
              .Append('\n');

            sb.Append("  fill: ").Append(AmbientColour)
              .Append(" x").Append(F(AmbientIntensity, 3))
              .Append(", background ").Append(OutsideColour)
              .Append('\n');

            sb.Append("  air: fog ").Append(FogColour)
              .Append(" over ").Append(F(FogDistance, 0))
              .Append(" (haze ").Append(F(Haze, 2))
              .Append(", visibility ").Append(F(Visibility, 2))
              .Append(", overcast ").Append(F(Overcast, 2))
              .Append(")\n");

            sb.Append("  sky: tint ").Append(SkyTint)
              .Append(" at strip ").Append(F(SkyStrip, 2))
              .Append('\n');
            return sb.ToString();
        }

        /// <summary>One line for a log: the phase, the disc and the fill.</summary>
        public override string ToString()
        {
            return Phase + (IsNight ? " (night)" : string.Empty)
                + (IsSunUp ? " sun" : " moon")
                + " elev " + F(SunElevationDegrees, 1) + "deg"
                + " " + SunColour + " x" + F(SunIntensity, 3)
                + " fill " + AmbientColour + " x" + F(AmbientIntensity, 3)
                + " fog " + F(FogDistance, 0)
                + " sky " + SkyTint;
        }

        private static string F(double value, int digits)
        {
            return value.ToString("F" + digits.ToString(CultureInfo.InvariantCulture),
                CultureInfo.InvariantCulture);
        }
    }
}
