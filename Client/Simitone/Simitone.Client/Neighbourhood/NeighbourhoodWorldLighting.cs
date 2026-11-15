/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// What a lot view draws from B8's <see cref="WorldLightingState"/> and
    /// <see cref="WorldWeather"/>: whether the sun is up, and the lot's background colour.
    ///
    /// <para>
    /// <b>Cut back to what is read (2026-11).</b> This type used to carry a sun direction, sun and
    /// fill intensities, fog distance, haze, visibility, sky tint and a sky-texture strip - about
    /// 770 lines and the tests that pinned them. The only production reader is
    /// <c>TS1GameScreen.UpdateNeighbourhoodWorldLighting</c>, which takes <see cref="OutsideColour"/>
    /// and <see cref="IsSunUp"/>; nothing drew fog, sky or a sun from the rest. Wiring them into the
    /// 3D view was weighed and not done (it is more than a trivial change: that view has its own
    /// <c>NeighbourhoodCityLighting</c>, and a second opinion about the same sun would have to be
    /// reconciled with it first). If the 3D view ever takes its sky from the world clock, the
    /// removed fields are in git history before this change.
    /// </para>
    ///
    /// <para>
    /// <b>It is a pure function of two values.</b> No graphics device, no clock, no state between
    /// calls, nothing from <c>Microsoft.Xna.Framework</c>. A world whose features were never built
    /// hands over <c>default</c> for both and gets a <c>default</c>-shaped answer, so a caller gates
    /// on the capability and not on whether the value looks plausible.
    /// </para>
    ///
    /// <para>
    /// <b>The mapping.</b> <see cref="IsSunUp"/> is the sign of the sun's angle, not the hour-based
    /// <see cref="WorldLightingState.IsNight"/>: a renderer picking a sun or a moon asks the
    /// continuous question. <see cref="OutsideColour"/> is the features' fill colour, grey-shifted
    /// towards <see cref="OvercastGrey"/> by cloud (level-preserving, so a white-out at midnight
    /// darkens nothing), and then pulled onto the fog colour by the amount of fog, because fog is
    /// what the background becomes. With a clear sky it <em>is</em> the fill colour, because the
    /// engine derives <c>WorldState.OutsideColor</c> and its ambient from one ramp.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodWorldLighting : IEquatable<NeighbourhoodWorldLighting>
    {
        /// <summary>
        /// The colour overcast sky and fog are shifted towards. Level-preserving, so a rainy lot is
        /// all one grey rather than a sun tinted one way and the rest of the lot another.
        /// </summary>
        public static readonly WorldColour OvercastGrey = WorldColour.FromBytes(159, 164, 181);

        /// <summary>
        /// The cold blue a clear sky's fog is tinted towards: the engine's own 0x80C0FF fog
        /// multiplier, read as a colour rather than as a product.
        /// </summary>
        public static readonly WorldColour ClearFogTint = WorldColour.FromBytes(0x80, 0xC0, 0xFF);

        /// <summary>
        /// How far the fill colour moves towards <see cref="ClearFogTint"/> to make the fog colour
        /// before cloud greys it; 0.6 is about as blue as a fog actually looks.
        /// </summary>
        public const double ClearFogShare = 0.60;

        /// <summary>
        /// Whether the disc being drawn is the sun: the sun's angle is above the horizon. By the
        /// trigonometry, not by <see cref="WorldLightingState.IsNight"/>, which is a statement about
        /// two hours.
        /// </summary>
        public bool IsSunUp { get; }

        /// <summary>
        /// The lot's background colour: the fill colour, greyed by cloud, and pulled onto the fog
        /// colour by fog.
        /// </summary>
        public WorldColour OutsideColour { get; }

        private NeighbourhoodWorldLighting(bool isSunUp, WorldColour outsideColour)
        {
            IsSunUp = isSunUp;
            OutsideColour = outsideColour;
        }

        /// <summary>The lot view's lighting for a world's lighting and weather.</summary>
        /// <param name="lighting">
        /// The world's lighting state. <c>default</c> is accepted and means "nothing has been
        /// derived yet".
        /// </param>
        /// <param name="weather">The world's weather. <c>default</c> is accepted: no cloud, no rain.</param>
        /// <returns>The values a lot view draws with.</returns>
        public static NeighbourhoodWorldLighting From(WorldLightingState lighting, WorldWeather weather)
        {
            var overcast = Clamp01(weather.CloudCover);
            var fog = FogAmount(weather);

            // The test is the sign of the angle and not the hour: Math.Cos of a quarter turn is
            // 6.1e-17 rather than exactly zero, so at 06:00:00.000 and 18:00:00.000 the sun counts
            // as up with an elevation of 4e-15 degrees - the same answer the other branch would
            // give, which is why nothing has to be done about it.
            var sunUp = lighting.SunAngleDegrees > 0.0;

            // Grey-shifted rather than lerped towards the grey, so a white-out at midnight darkens
            // nothing and brightens nothing: the shift keeps the level and changes the hue.
            var ambientColour = GreyShift(lighting.AmbientColour, OvercastGrey, overcast);

            // Two numbers elsewhere, but only "how grey is it" reaches the background.
            var haze = Math.Max(overcast, fog);
            var clearFog = WorldColour.Lerp(ambientColour, ClearFogTint, ClearFogShare);
            var fogColour = GreyShift(clearFog, OvercastGrey, haze);

            // Fog is the one weather that replaces the background outright rather than tinting it.
            return new NeighbourhoodWorldLighting(sunUp, WorldColour.Lerp(ambientColour, fogColour, fog));
        }

        /// <summary>
        /// How foggy it is, 0..1, as a continuous reading of the two conditions the features type
        /// itself uses to classify a segment as <see cref="WorldWeatherKind.Fog"/>: enough cloud,
        /// and not too much wind. A product of two ramps that are each zero below the features' own
        /// thresholds, so exactly zero for any weather the features type would not have called fog,
        /// and a fog that arrives gradually rather than in one frame.
        /// </summary>
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
        /// <see cref="WorldColour"/> clamps the product.
        /// </summary>
        private static WorldColour Scale(WorldColour colour, double factor)
        {
            var k = (float)factor;
            return new WorldColour(colour.R * k, colour.G * k, colour.B * k);
        }

        private static double Clamp01(double value)
        {
            if (double.IsNaN(value)) return 0.0;
            if (value < 0.0) return 0.0;
            return value > 1.0 ? 1.0 : value;
        }

        /// <summary>Two answers are equal when both fields are.</summary>
        public bool Equals(NeighbourhoodWorldLighting other)
        {
            return IsSunUp == other.IsSunUp && OutsideColour.Equals(other.OutsideColour);
        }

        /// <inheritdoc />
        public override bool Equals(object obj)
        {
            return obj is NeighbourhoodWorldLighting && Equals((NeighbourhoodWorldLighting)obj);
        }

        /// <inheritdoc />
        public override int GetHashCode()
        {
            unchecked
            {
                return (IsSunUp ? 1 : 0) * 397 ^ OutsideColour.GetHashCode();
            }
        }

        /// <summary>Value equality.</summary>
        public static bool operator ==(NeighbourhoodWorldLighting a, NeighbourhoodWorldLighting b)
        {
            return a.Equals(b);
        }

        /// <summary>Value inequality.</summary>
        public static bool operator !=(NeighbourhoodWorldLighting a, NeighbourhoodWorldLighting b)
        {
            return !a.Equals(b);
        }
    }
}
