/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Which part of the world's day it is, as four bands rather than as a boolean.
    ///
    /// <para>
    /// B8 asks for centralised day/night, and a boolean is the obvious way to answer it. It is the
    /// wrong one: a lot that switches its lights at exactly 18:00 and a lot that switches them at
    /// 20:00 are both doing "day/night" and are visibly different neighbourhoods, and a renderer
    /// that needs a dusk tint or a lamp-lit street has nothing to ask a boolean. So the bands are
    /// named, and the two that straddle the horizon are separate from the two that do not.
    /// </para>
    ///
    /// <para>
    /// The order is the day's order, so <c>DayPhase &lt; WorldDayPhase.Dusk</c> means "earlier in
    /// the day than dusk" and the enum sorts the way time does. That is a convenience, not a
    /// promise: nothing should compare phases to decide whether it is dark - that question is
    /// <see cref="WorldTimeOfDay.IsNight"/>, which is defined from the hours rather than from the
    /// order of these values.
    /// </para>
    /// </summary>
    public enum WorldDayPhase
    {
        /// <summary>First light: <see cref="NeighbourhoodWorldFeatures.DawnStartHour"/> up to sunrise.</summary>
        Dawn = 0,

        /// <summary>Full daylight: sunrise to <see cref="NeighbourhoodWorldFeatures.DuskStartHour"/>.</summary>
        Day = 1,

        /// <summary>Sunset through the last of the light: dusk, and already night by this type's reckoning.</summary>
        Dusk = 2,

        /// <summary>Dark: the rest of the day.</summary>
        Night = 3
    }

    /// <summary>
    /// The six conditions the world can be in, as a closed set. A later milestone that wants a
    /// seventh has to come and add it here rather than inventing one on the side.
    /// </summary>
    public enum WorldWeatherKind
    {
        /// <summary>Dry and mostly clear.</summary>
        Clear = 0,

        /// <summary>Overcast but dry.</summary>
        Cloudy = 1,

        /// <summary>Low cloud, little wind, no rain.</summary>
        Fog = 2,

        /// <summary>Precipitating above freezing.</summary>
        Rain = 3,

        /// <summary>Precipitating hard, with thunder.</summary>
        Storm = 4,

        /// <summary>Precipitating below freezing.</summary>
        Snow = 5
    }

    /// <summary>
    /// What kind of company a lot is having: a community lot's townies, a studio lot's, or a
    /// residential lot's visitors. Derived from the lot's zone, never drawn.
    /// </summary>
    public enum NeighbourhoodVisitorKind
    {
        /// <summary>A community lot, open to the neighbourhood and staffed by the townies.</summary>
        Community = 0,

        /// <summary>A studio lot, open for a shorter day than a community lot.</summary>
        Studio = 1,

        /// <summary>A residential lot that is having visitors in the evening.</summary>
        Neighbour = 2
    }

    /// <summary>
    /// A linear RGB colour with no alpha, used for the two things B8's lighting produces: the sun's
    /// colour and the world's ambient colour.
    ///
    /// <para>
    /// Three floats rather than a <c>System.Drawing.Color</c> on purpose. A colour is the one place
    /// where "pure" is easiest to break by accident, and the drawing assembly is the sort of
    /// reference that arrives later, with a graphics initialisation order attached to it. This type
    /// has no reference at all, and its channels are clamped to <c>[0, 1]</c> on the way in, so a
    /// lighting curve that overshoots produces a saturated colour rather than a negative one.
    /// </para>
    ///
    /// <para>
    /// <b>It is not a colour space.</b> The three channels are an abstract brightness in three
    /// directions; what a renderer does with them - gamma, tone mapping, a sky dome gradient - is
    /// the renderer's business, and a conversion done here would be a second opinion that the lot
    /// view and the 3D view could disagree about.
    /// </para>
    /// </summary>
    public readonly struct WorldColour : IEquatable<WorldColour>
    {
        /// <summary>Red, 0..1.</summary>
        public float R { get; }

        /// <summary>Green, 0..1.</summary>
        public float G { get; }

        /// <summary>Blue, 0..1.</summary>
        public float B { get; }

        /// <summary>
        /// Creates a colour. Every channel is clamped into <c>[0, 1]</c>, so an overshooting
        /// lighting curve saturates rather than going negative or above white.
        /// </summary>
        /// <param name="r">Red.</param>
        /// <param name="g">Green.</param>
        /// <param name="b">Blue.</param>
        public WorldColour(float r, float g, float b)
        {
            R = Math.Clamp(r, 0f, 1f);
            G = Math.Clamp(g, 0f, 1f);
            B = Math.Clamp(b, 0f, 1f);
        }

        /// <summary>A colour from three 8-bit channels, for stating a palette constant readably.</summary>
        /// <param name="r">Red, 0..255.</param>
        /// <param name="g">Green, 0..255.</param>
        /// <param name="b">Blue, 0..255.</param>
        public static WorldColour FromBytes(byte r, byte g, byte b)
        {
            return new WorldColour(r / 255f, g / 255f, b / 255f);
        }

        /// <summary>
        /// The colour <paramref name="t"/> of the way from <paramref name="a"/> to
        /// <paramref name="b"/>. <paramref name="t"/> is clamped first, so a curve that runs
        /// slightly past its endpoint comes back to it.
        /// </summary>
        /// <param name="a">The colour at zero.</param>
        /// <param name="b">The colour at one.</param>
        /// <param name="t">How far along.</param>
        public static WorldColour Lerp(WorldColour a, WorldColour b, double t)
        {
            var k = (float)Math.Clamp(t, 0.0, 1.0);
            return new WorldColour(
                a.R + (b.R - a.R) * k,
                a.G + (b.G - a.G) * k,
                a.B + (b.B - a.B) * k);
        }

        /// <summary>Red as an 8-bit channel, for a log line that wants <c>#RRGGBB</c>.</summary>
        public byte R8 { get { return (byte)Math.Round(R * 255f); } }

        /// <summary>Green as an 8-bit channel. See <see cref="R8"/>.</summary>
        public byte G8 { get { return (byte)Math.Round(G * 255f); } }

        /// <summary>Blue as an 8-bit channel. See <see cref="R8"/>.</summary>
        public byte B8 { get { return (byte)Math.Round(B * 255f); } }

        /// <summary>Two colours are equal when all three channels are.</summary>
        /// <param name="other">The colour to compare with.</param>
        public bool Equals(WorldColour other)
        {
            return R == other.R && G == other.G && B == other.B;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is WorldColour && Equals((WorldColour)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)R8;
                hash = (hash * 397) ^ G8;
                hash = (hash * 397) ^ B8;
                return hash;
            }
        }

        /// <summary>Two colours are equal when all three channels are.</summary>
        /// <param name="a">One colour.</param>
        /// <param name="b">The other.</param>
        public static bool operator ==(WorldColour a, WorldColour b)
        {
            return a.Equals(b);
        }

        /// <summary>The negation of <see cref="op_Equality"/>.</summary>
        /// <param name="a">One colour.</param>
        /// <param name="b">The other.</param>
        public static bool operator !=(WorldColour a, WorldColour b)
        {
            return !a.Equals(b);
        }

        /// <summary>
        /// <c>#RRGGBB</c>, upper case. Locale-independent by construction, because a log line that
        /// reads differently on a machine with a different culture is a log line that cannot be
        /// compared against another.
        /// </summary>
        public override string ToString()
        {
            return "#" + R8.ToString("X2", CultureInfo.InvariantCulture)
                + G8.ToString("X2", CultureInfo.InvariantCulture)
                + B8.ToString("X2", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The world's reading of its own clock, reduced to the four or five numbers a lighting model
    /// and a "is it dark" question both want.
    ///
    /// <para>
    /// <b>It is a reduction, and the reduction is the point.</b> A <see cref="WorldClock"/> can
    /// hold an hour of 47 or a minute of 90 after a corrupt save, and a lighting model asked to
    /// draw a sun for those numbers is a lighting model that has to defend itself twice. So the
    /// normalisation happens here, once, and everything downstream reads integers that are in
    /// range: <see cref="Hour"/> is 0..23 and <see cref="Minute"/> is 0..59 whatever the clock
    /// says, and the same normalised reading feeds the phase, the darkness and the sun's angle, so
    /// the three cannot disagree about what time it is.
    /// </para>
    ///
    /// <para>
    /// <b>IsNight is defined by the hours, not by the trigonometry.</b> That is deliberate, and it
    /// is the difference between a boundary a test can pin and a boundary that depends on whether
    /// <c>Math.Cos</c> rounds <c>6.1e-17</c> up or down at 06:00:00. The sun's
    /// <see cref="SunAngleDegrees"/> is the continuous quantity and is documented as crossing zero
    /// at <see cref="NeighbourhoodWorldFeatures.SunriseHour"/> and
    /// <see cref="NeighbourhoodWorldFeatures.SunsetHour"/>; <see cref="IsNight"/> is the
    /// statement about those two hours, and it is the one callers should test.
    /// </para>
    /// </summary>
    public readonly struct WorldTimeOfDay : IEquatable<WorldTimeOfDay>
    {
        /// <summary>The hour, always 0..23 however the clock stored it.</summary>
        public int Hour { get; }

        /// <summary>The minute within the hour, always 0..59 however the clock stored it.</summary>
        public int Minute { get; }

        /// <summary>
        /// How far through the day it is, 0..1. Midnight is exactly 0 and the last second before
        /// the next midnight is just under 1.
        /// </summary>
        public double DayFraction { get; }

        /// <summary>Which band of the day this is. See <see cref="WorldDayPhase"/>.</summary>
        public WorldDayPhase Phase { get; }

        /// <summary>
        /// Is it dark? True from <see cref="NeighbourhoodWorldFeatures.SunsetHour"/> until
        /// <see cref="NeighbourhoodWorldFeatures.SunriseHour"/>, which is to say it is exactly
        /// "<see cref="Phase"/> is <see cref="WorldDayPhase.Dusk"/> or
        /// <see cref="WorldDayPhase.Night"/>".
        /// </summary>
        public bool IsNight { get; }

        /// <summary>
        /// The sun's elevation above the horizon in degrees: positive above, negative below, and
        /// <see cref="NeighbourhoodWorldFeatures.MaxSunElevationDegrees"/> at solar noon.
        /// </summary>
        public double SunAngleDegrees { get; }

        /// <summary>
        /// Where the sun is, in degrees clockwise from north: 90 at sunrise, 180 at solar noon,
        /// 270 at sunset. Always 0..360.
        /// </summary>
        public double SunAzimuthDegrees { get; }

        /// <summary>
        /// Is the sun above the horizon? The exact negation of <see cref="IsNight"/> - the two are
        /// read from the same normalised hours - so a caller never has to wonder which of them to
        /// test.
        /// </summary>
        public bool SunIsUp { get; }

        private WorldTimeOfDay(int hour, int minute, double dayFraction, WorldDayPhase phase,
            bool isNight, double sunAngleDegrees, double sunAzimuthDegrees, bool sunIsUp)
        {
            Hour = hour;
            Minute = minute;
            DayFraction = dayFraction;
            Phase = phase;
            IsNight = isNight;
            SunAngleDegrees = sunAngleDegrees;
            SunAzimuthDegrees = sunAzimuthDegrees;
            SunIsUp = sunIsUp;
        }

        /// <summary>
        /// The world's reading of <paramref name="clock"/>, normalised.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The normalisation is <c>hours * 3600 + minutes * 60 + seconds</c> folded into
        /// <c>[0, 86400)</c>. Folding rather than clamping is the right reading of a clock that says
        /// 47:10: it is 23:10 of some day, not a broken value, and a day/night model that threw at
        /// it would be the only part of the neighbourhood that cannot draw a corrupt save.
        /// </para>
        /// <para>
        /// <see cref="INeighbourhoodClock.Seconds"/> is read rather than
        /// <see cref="INeighbourhoodClock.MinuteFractions"/>, because the interface already
        /// documents that its own <c>Seconds</c> is the guarded derivation of the fraction - a
        /// clock whose rate has been zeroed answers 0 there instead of dividing.
        /// </para>
        /// </remarks>
        /// <param name="clock">The neighbourhood's clock. Required; null is a caller bug and throws.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
        public static WorldTimeOfDay FromClock(INeighbourhoodClock clock)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            var secondOfDay = NeighbourhoodWorldFeatures.SecondOfDay(clock);
            var fraction = secondOfDay / (double)NeighbourhoodWorldFeatures.SecondsPerDay;
            var hour = (int)(secondOfDay / 3600L);
            var minute = (int)((secondOfDay % 3600L) / 60L);
            var phase = NeighbourhoodWorldFeatures.PhaseForHour(hour);
            var isNight = phase == WorldDayPhase.Dusk || phase == WorldDayPhase.Night;

            //The arc: a cosine about solar noon, amplitude the peak elevation. It crosses zero
            //exactly a quarter day either side of noon, which is what makes 06:00 and 18:00 the
            //right constants to name for sunrise and sunset.
            var angle = NeighbourhoodWorldFeatures.MaxSunElevationDegrees
                * Math.Cos(2.0 * Math.PI * (fraction - 0.5));

            //East at sunrise, south at noon, west at sunset, folded into one turn.
            var azimuth = 90.0 + (fraction - 0.25) * 360.0;
            azimuth %= 360.0;
            if (azimuth < 0) azimuth += 360.0;

            return new WorldTimeOfDay(hour, minute, fraction, phase, isNight, angle, azimuth, !isNight);
        }

        /// <summary>Two readings are equal when every field is.</summary>
        /// <param name="other">The reading to compare with.</param>
        public bool Equals(WorldTimeOfDay other)
        {
            return Hour == other.Hour
                && Minute == other.Minute
                && DayFraction == other.DayFraction
                && Phase == other.Phase
                && IsNight == other.IsNight
                && SunAngleDegrees == other.SunAngleDegrees
                && SunAzimuthDegrees == other.SunAzimuthDegrees
                && SunIsUp == other.SunIsUp;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is WorldTimeOfDay && Equals((WorldTimeOfDay)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Hour;
                hash = (hash * 397) ^ Minute;
                hash = (hash * 397) ^ DayFraction.GetHashCode();
                hash = (hash * 397) ^ (int)Phase;
                hash = (hash * 397) ^ (IsNight ? 1 : 0);
                return hash;
            }
        }

        /// <summary>Two readings are equal when every field is.</summary>
        /// <param name="a">One reading.</param>
        /// <param name="b">The other.</param>
        public static bool operator ==(WorldTimeOfDay a, WorldTimeOfDay b)
        {
            return a.Equals(b);
        }

        /// <summary>The negation of <see cref="op_Equality"/>.</summary>
        /// <param name="a">One reading.</param>
        /// <param name="b">The other.</param>
        public static bool operator !=(WorldTimeOfDay a, WorldTimeOfDay b)
        {
            return !a.Equals(b);
        }

        /// <summary>
        /// One line: the time, the band, whether it is dark, and the sun's elevation. Written for a
        /// log, so it is built from the normalised fields and never touches the environment.
        /// </summary>
        public override string ToString()
        {
            return Hour.ToString("00", CultureInfo.InvariantCulture) + ":"
                + Minute.ToString("00", CultureInfo.InvariantCulture)
                + " " + Phase + (IsNight ? " (night)" : string.Empty)
                + " sun " + SunAngleDegrees.ToString("F1", CultureInfo.InvariantCulture) + "deg";
        }
    }

    /// <summary>
    /// What the world looks like lit, as a function of nothing but the clock.
    ///
    /// <para>
    /// <b>Purely a function of the time.</b> That is the load-bearing claim, and it is why the
    /// weather is not an input: two lots in the same neighbourhood at the same instant must be lit
    /// identically, and the only way to guarantee that is for the answer not to depend on anything
    /// that could differ between the two of them - not on the lot, not on the renderer, not on the
    /// cloud cover. Combining the two is the wiring step's job, and it is a multiply at the call
    /// site rather than a hidden term in here.
    /// </para>
    ///
    /// <para>
    /// <b>Everything is bounded and nothing is negative.</b> Intensities are 0..1 and colours are
    /// 0..1 per channel, which is what lets a caller use them as a straight multiplier and get a
    /// dark night rather than an inverted one.
    /// </para>
    /// </summary>
    public readonly struct WorldLightingState : IEquatable<WorldLightingState>
    {
        /// <summary>
        /// How far below the horizon the sun's contribution still reaches, in degrees. Roughly civil
        /// twilight, and the reason the ramp does not stop at the horizon.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Clipping the sun at zero elevation is the obvious thing to do and it is wrong in a way a
        /// player sees: every hour below the horizon would render identically, so 18:30 (which this
        /// model calls <see cref="WorldDayPhase.Dusk"/>) would be lit exactly like 03:00, and the
        /// dusk band the phase model exists to name would be invisible. Carrying the ramp twelve
        /// degrees past the horizon makes the two distinguishable while still reaching exactly zero
        /// at about half past six - and it does not touch <see cref="WorldTimeOfDay.IsNight"/>,
        /// which is the hour the lamps come on, not the hour the light stops.
        /// </para>
        /// <para>
        /// Twelve is a choice, not a measurement. It is a quarter of
        /// <see cref="NeighbourhoodWorldFeatures.MaxSunElevationDegrees"/>, which keeps the curve's
        /// shape simple and puts the fade-out inside the two hours the dusk band spans.
        /// </para>
        /// </remarks>
        public const double TwilightDegrees = 12.0;

        /// <summary>
        /// The ambient contribution at solar midnight, before any weather is folded in. A world at
        /// night is not black: there is moon and starlight and, in TS1, an indoor lot's own lamps.
        /// </summary>
        public const double MinAmbientIntensity = 0.12;

        /// <summary>The ambient contribution at solar noon. See <see cref="MinAmbientIntensity"/>.</summary>
        public const double MaxAmbientIntensity = 0.55;

        /// <summary>The sun's own colour at solar noon: very slightly warm white.</summary>
        public static readonly WorldColour NoonSunColour = WorldColour.FromBytes(255, 250, 235);

        /// <summary>
        /// The sun's colour when it is not out. Nothing is reading it for lighting - the intensity
        /// is zero - and it is here so the colour ramp has an endpoint that is not the noon colour
        /// clamped, which is what would make a dawn that starts from white look like a lightbulb.
        /// </summary>
        public static readonly WorldColour NightSunColour = WorldColour.FromBytes(90, 107, 158);

        /// <summary>The ambient colour at solar noon: a cool sky fill rather than a sun tint.</summary>
        public static readonly WorldColour DayAmbientColour = WorldColour.FromBytes(158, 173, 199);

        /// <summary>The ambient colour at solar midnight: blue, because moonlight is.</summary>
        public static readonly WorldColour NightAmbientColour = WorldColour.FromBytes(41, 51, 87);

        /// <summary>The clock's reading, normalised. Everything else here is derived from it.</summary>
        public WorldTimeOfDay TimeOfDay { get; }

        /// <summary>The sun's colour. Meaningless to lighting while <see cref="SunIntensity"/> is zero.</summary>
        public WorldColour SunColour { get; }

        /// <summary>
        /// The sun's contribution, 0..1. Ramped across the horizon rather than clipped at it - see
        /// <see cref="TwilightDegrees"/> - so the hour after sunset is not lit exactly like the
        /// small hours. Monotone across the day, so a lighting ramp cannot be non-monotone.
        /// </summary>
        public double SunIntensity { get; }

        /// <summary>The world's fill light colour, ramped from night to day.</summary>
        public WorldColour AmbientColour { get; }

        /// <summary>The world's fill light strength, from <see cref="MinAmbientIntensity"/> to <see cref="MaxAmbientIntensity"/>.</summary>
        public double AmbientIntensity { get; }

        /// <summary>Is it dark? The same answer as <see cref="WorldTimeOfDay.IsNight"/>, restated here.</summary>
        public bool IsNight { get; }

        /// <summary>The sun's elevation in degrees. See <see cref="WorldTimeOfDay.SunAngleDegrees"/>.</summary>
        public double SunAngleDegrees { get; }

        private WorldLightingState(WorldTimeOfDay timeOfDay, WorldColour sunColour, double sunIntensity,
            WorldColour ambientColour, double ambientIntensity)
        {
            TimeOfDay = timeOfDay;
            SunColour = sunColour;
            SunIntensity = sunIntensity;
            AmbientColour = ambientColour;
            AmbientIntensity = ambientIntensity;
            IsNight = timeOfDay.IsNight;
            SunAngleDegrees = timeOfDay.SunAngleDegrees;
        }

        /// <summary>
        /// The world's lighting at <paramref name="clock"/>, and a pure function of it: the same
        /// clock yields the same state, field for field, on any machine, in any frame, whatever
        /// the world seed is.
        /// </summary>
        /// <param name="clock">The neighbourhood's clock. Required; null throws.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
        public static WorldLightingState FromClock(INeighbourhoodClock clock)
        {
            var time = WorldTimeOfDay.FromClock(clock);

            //How much of the day the sun is managing: 0 once it is a twilight's depth below the
            //horizon, 1 at the peak. One number drives every ramp below, which is what keeps them
            //from disagreeing - see TwilightDegrees for why the bottom of the ramp is not zero.
            var daylight = Math.Clamp(
                (time.SunAngleDegrees + TwilightDegrees)
                    / (NeighbourhoodWorldFeatures.MaxSunElevationDegrees + TwilightDegrees),
                0.0, 1.0);

            return new WorldLightingState(
                time,
                WorldColour.Lerp(NightSunColour, NoonSunColour, daylight),
                daylight,
                WorldColour.Lerp(NightAmbientColour, DayAmbientColour, daylight),
                MinAmbientIntensity + (MaxAmbientIntensity - MinAmbientIntensity) * daylight);
        }

        /// <summary>Two states are equal when every field is.</summary>
        /// <param name="other">The state to compare with.</param>
        public bool Equals(WorldLightingState other)
        {
            return TimeOfDay == other.TimeOfDay
                && SunColour == other.SunColour
                && SunIntensity == other.SunIntensity
                && AmbientColour == other.AmbientColour
                && AmbientIntensity == other.AmbientIntensity;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is WorldLightingState && Equals((WorldLightingState)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = TimeOfDay.GetHashCode();
                hash = (hash * 397) ^ SunColour.GetHashCode();
                hash = (hash * 397) ^ SunIntensity.GetHashCode();
                hash = (hash * 397) ^ AmbientColour.GetHashCode();
                hash = (hash * 397) ^ AmbientIntensity.GetHashCode();
                return hash;
            }
        }

        /// <summary>Two states are equal when every field is.</summary>
        /// <param name="a">One state.</param>
        /// <param name="b">The other.</param>
        public static bool operator ==(WorldLightingState a, WorldLightingState b)
        {
            return a.Equals(b);
        }

        /// <summary>The negation of <see cref="op_Equality"/>.</summary>
        /// <param name="a">One state.</param>
        /// <param name="b">The other.</param>
        public static bool operator !=(WorldLightingState a, WorldLightingState b)
        {
            return !a.Equals(b);
        }

        /// <summary>One line for a log: the phase, the sun and the fill light.</summary>
        public override string ToString()
        {
            return TimeOfDay + " sun " + SunColour + " x"
                + SunIntensity.ToString("F3", CultureInfo.InvariantCulture)
                + " ambient " + AmbientColour + " x"
                + AmbientIntensity.ToString("F3", CultureInfo.InvariantCulture);
        }
    }

    /// <summary>
    /// The world's weather, shared by every lot in it, as a pure function of the seed and the
    /// clock.
    ///
    /// <para>
    /// <b>"Shared by every lot" is structural, not a convention.</b> A weather struct cannot be
    /// per-lot: there is one of it, it is derived from the world seed and the world clock, and
    /// nothing in the derivation reads a lot, a renderer, or a VM. A lot view that wants rain
    /// outside its window draws the same rain as the lot view next door, and the only way it could
    /// not is if somebody wrote a second derivation - which is the thing the type existing at all
    /// is meant to prevent.
    /// </para>
    ///
    /// <para>
    /// <b>It is a function of the time, not a thing that accumulates.</b> Weather is drawn once per
    /// <see cref="SegmentGameMinutes"/> of neighbourhood time, from a stream labelled by the
    /// segment's own index, and the reported numbers are a blend of this segment's draw and the
    /// previous one's. Nothing is stored between calls, so a world that was not ticked for an hour
    /// and a world that was ticked an hour's worth of times arrive at the same weather - the same
    /// frame-invariance <see cref="OffscreenSimulator"/> gets from absolute step indices, arrived
    /// at here from absolute segment indices.
    /// </para>
    ///
    /// <para>
    /// <b>Every number is bounded and the blend cannot break that.</b> Cloud, precipitation, wind
    /// and thunder are fractions; the temperature is clamped into
    /// <see cref="MinTemperatureC"/>..<see cref="MaxTemperatureC"/> after every term is added, and
    /// the blend is a lerp between two values that are each already in range.
    /// </para>
    /// </summary>
    public readonly struct WorldWeather : IEquatable<WorldWeather>
    {
        /// <summary>Game minutes in one weather segment: an hour. See the type remarks.</summary>
        public const int DefaultSegmentGameMinutes = 60;

        /// <summary>The lowest temperature this model can report, in degrees Celsius.</summary>
        public const double MinTemperatureC = -20.0;

        /// <summary>The highest temperature this model can report, in degrees Celsius.</summary>
        public const double MaxTemperatureC = 45.0;

        /// <summary>The annual mean, in degrees Celsius. Everything seasonal is an offset from this.</summary>
        public const double MidTemperatureC = 12.0;

        /// <summary>How far either side of the mean the seasons reach, in degrees Celsius.</summary>
        public const double SeasonAmplitudeC = 10.0;

        /// <summary>How far either side of the daily mean the hours reach, in degrees Celsius.</summary>
        public const double DiurnalAmplitudeC = 5.0;

        /// <summary>How far a single segment's draw may move the temperature, in degrees Celsius.</summary>
        public const double SegmentJitterC = 4.0;

        /// <summary>The month at the top of the seasonal curve. July, for a northern hemisphere.</summary>
        public const int WarmestMonth = 7;

        /// <summary>
        /// At or below this temperature, precipitation is snow rather than rain. The number rather
        /// than a flag because the boundary is a rendering question - a white ground and a grey one
        /// - and a model that made it a decision would be a model a renderer could not ask about.
        /// </summary>
        public const double SnowTemperatureC = 1.0;

        /// <summary>Precipitation at or above this is a storm.</summary>
        public const double StormPrecipitation = 0.55;

        /// <summary>Precipitation at or above this is rain; below it and above zero, a shower.</summary>
        public const double RainPrecipitation = 0.15;

        /// <summary>Precipitation below this is dry. Also the bar <see cref="IsRaining"/> is read at.</summary>
        public const double WetPrecipitation = 0.05;

        /// <summary>
        /// The most likely a strike is, in a downpour from a severe segment. The ceiling on
        /// <see cref="ThunderChance"/>, and a cap rather than a scale because a probability above
        /// one is not a probability.
        /// </summary>
        public const double MaxThunderChance = 0.8;

        /// <summary>Cloud at or above this is overcast rather than clear.</summary>
        public const double CloudyCover = 0.5;

        /// <summary>Fog needs at least this much cloud - fog is not a clear sky.</summary>
        public const double FogCover = 0.35;

        /// <summary>...and no more wind than this, or it is blowing the fog away.</summary>
        public const double FogMaxWind = 0.25;

        /// <summary>Which condition this is, as the segment's own draw classifies it.</summary>
        public WorldWeatherKind Kind { get; }

        /// <summary>Cloud cover, 0..1.</summary>
        public double CloudCover { get; }

        /// <summary>How much is falling, 0..1. Not a probability: a rate, and the same unit as the cover.</summary>
        public double Precipitation { get; }

        /// <summary>Wind strength, 0..1.</summary>
        public double WindSpeed { get; }

        /// <summary>
        /// How likely a strike is, 0..<see cref="MaxThunderChance"/>. Derived from the
        /// precipitation, so it is exactly zero whenever nothing is falling.
        /// </summary>
        public double ThunderChance { get; }

        /// <summary>
        /// The temperature, in degrees Celsius, always within
        /// <see cref="MinTemperatureC"/>..<see cref="MaxTemperatureC"/>.
        /// </summary>
        public double TemperatureC { get; }

        /// <summary>
        /// The clock tick this segment began at, in the same units as
        /// <see cref="INeighbourhoodClock.Ticks"/>.
        /// </summary>
        public long SegmentStartTick { get; }

        /// <summary>Game minutes in a segment, which is the granularity of the draws.</summary>
        public int SegmentGameMinutes { get; }

        /// <summary>Is anything falling? Read at <see cref="WetPrecipitation"/>.</summary>
        public bool IsRaining
        {
            get { return Precipitation >= WetPrecipitation; }
        }

        /// <summary>Is anything frozen falling? True for <see cref="WorldWeatherKind.Snow"/>.</summary>
        public bool IsSnowing
        {
            get { return Kind == WorldWeatherKind.Snow; }
        }

        private WorldWeather(WorldWeatherKind kind, double cloudCover, double precipitation,
            double windSpeed, double thunderChance, double temperatureC,
            long segmentStartTick, int segmentGameMinutes)
        {
            Kind = kind;
            CloudCover = cloudCover;
            Precipitation = precipitation;
            WindSpeed = windSpeed;
            ThunderChance = thunderChance;
            TemperatureC = temperatureC;
            SegmentStartTick = segmentStartTick;
            SegmentGameMinutes = segmentGameMinutes;
        }

        /// <summary>
        /// The world's weather at <paramref name="clock"/>, drawn from
        /// <paramref name="worldSeed"/>. Pure: the same seed and the same clock give the same
        /// weather, whatever order the ticks arrived in.
        /// </summary>
        /// <param name="worldSeed">The seed in the layout sidecar. Same seed, same weather.</param>
        /// <param name="clock">The neighbourhood's clock. Required; null throws.</param>
        /// <param name="segmentGameMinutes">
        /// Game minutes per weather segment. Defaults to <see cref="DefaultSegmentGameMinutes"/>;
        /// a coarser segment is a slower-varying weather and nothing else.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
        /// <exception cref="ArgumentOutOfRangeException"><paramref name="segmentGameMinutes"/> is not positive.</exception>
        public static WorldWeather At(long worldSeed, INeighbourhoodClock clock,
            int segmentGameMinutes = DefaultSegmentGameMinutes)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (segmentGameMinutes <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(segmentGameMinutes), segmentGameMinutes,
                    "A weather segment that contains no time is not a segment.");
            }

            var ticksPerSegment = NeighbourhoodWorldFeatures.TicksPerSegment(clock, segmentGameMinutes);
            var segment = clock.Ticks / ticksPerSegment;
            var progress = (clock.Ticks % ticksPerSegment) / (double)ticksPerSegment;

            //Smoothstep across the segment, so the weather eases into each new draw instead of
            //stepping to it. A second segment is still a hard change - see Kind - but the numbers
            //either side of the boundary are continuous, which is the part a renderer can see.
            var blend = progress * progress * (3.0 - 2.0 * progress);

            var current = Draw(worldSeed, segment);
            var previous = Draw(worldSeed, segment - 1);

            var cloud = Lerp(previous.CloudCover, current.CloudCover, blend);
            var precipitation = Lerp(previous.Precipitation, current.Precipitation, blend);
            var wind = Lerp(previous.WindSpeed, current.WindSpeed, blend);

            var secondOfDay = SecondOfDay(clock);
            var dayFraction = secondOfDay / (double)NeighbourhoodWorldFeatures.SecondsPerDay;
            var month = clock.Month >= 1 && clock.Month <= 12 ? clock.Month : 1;

            //Cosine about the warmest month: -1 in the middle of winter, +1 in the middle of
            //summer. The diurnal term is the negative of a cosine about midnight, so the coldest
            //hour is 00:00 and the warmest is noon.
            var season = Math.Cos(2.0 * Math.PI * (month - WarmestMonth) / 12.0);
            var diurnal = -Math.Cos(2.0 * Math.PI * dayFraction);
            var jitter = Lerp(previous.TemperatureC, current.TemperatureC, blend) - MidTemperatureC;
            var temperature = Math.Clamp(
                MidTemperatureC
                    + SeasonAmplitudeC * season
                    + DiurnalAmplitudeC * diurnal
                    + jitter,
                MinTemperatureC, MaxTemperatureC);

            return new WorldWeather(
                Classify(current, temperature),
                cloud,
                precipitation,
                wind,
                //Thunder is derived from the blended precipitation rather than blended itself. That
                //is the whole reason the two are not independent draws blended in parallel: an
                //independent blend can report a storm's worth of thunder over a cloudless sky in
                //the first minutes of a segment, and a sound engine would believe it.
                ThunderFor(precipitation, Lerp(previous.ThunderChance, current.ThunderChance, blend)),
                temperature,
                segment * ticksPerSegment,
                segmentGameMinutes);
        }

        /// <summary>
        /// How likely a strike is, given how much is falling and how severe the segment is.
        /// </summary>
        /// <remarks>
        /// Zero below <see cref="RainPrecipitation"/> and scaled to
        /// <see cref="MaxThunderChance"/> at <see cref="StormPrecipitation"/>. Written as a function
        /// of the blended precipitation and the blended severity rather than as a draw in its own
        /// right, which is what makes "there is thunder only when it is raining" true by
        /// construction rather than by luck.
        /// </remarks>
        private static double ThunderFor(double precipitation, double severity)
        {
            if (precipitation < RainPrecipitation) return 0.0;
            var span = StormPrecipitation - RainPrecipitation;
            var over = (precipitation - RainPrecipitation) / span;
            return MaxThunderChance * Math.Clamp(over, 0.0, 1.0) * Math.Clamp(severity, 0.0, 1.0);
        }

        /// <summary>
        /// One segment's own draw: the five numbers a segment has, before the seasonal and diurnal
        /// terms and before the blend.
        /// </summary>
        /// <remarks>
        /// <b>The draw order is part of the model.</b> Five values come out of one stream, so
        /// inserting a sixth or moving one changes every number after it. That is why
        /// <see cref="NeighbourhoodWorldFeatures.WeatherModelVersion"/> is in the stream's label:
        /// a retune lands on a new set of numbers rather than silently moving a neighbourhood's
        /// weather, which is the same contract
        /// <see cref="NeighbourhoodMapGenerator.GeneratorVersion"/> has.
        /// </remarks>
        private static SegmentDraw Draw(long worldSeed, long segment)
        {
            var rng = NeighbourhoodWorldFeatures.WeatherRoot(worldSeed)
                .ForLabel("segment/" + segment.ToString(CultureInfo.InvariantCulture));
            return new SegmentDraw(
                rng.NextDouble(),
                rng.NextDouble(),
                rng.NextDouble(),
                rng.NextDouble(),
                MidTemperatureC + SegmentJitterC * (rng.NextDouble() * 2.0 - 1.0));
        }

        /// <summary>
        /// The condition, read off this segment's own draw and this moment's temperature.
        /// </summary>
        /// <remarks>
        /// <b>Stepped, on purpose.</b> The continuous numbers blend across a segment boundary and
        /// the discrete condition does not, because "it is raining" is a statement a caller makes
        /// once and a renderer may act on - a particle effect, a sound - and a condition that faded
        /// in would mean rendering half a rain. So the number ramps and the word changes, and the
        /// test for which is <see cref="Precipitation"/> rather than <see cref="Kind"/>.
        /// </remarks>
        private static WorldWeatherKind Classify(SegmentDraw segment, double temperatureC)
        {
            if (temperatureC <= SnowTemperatureC && segment.Precipitation > RainPrecipitation)
            {
                return WorldWeatherKind.Snow;
            }
            if (segment.Precipitation >= StormPrecipitation) return WorldWeatherKind.Storm;
            if (segment.Precipitation >= RainPrecipitation) return WorldWeatherKind.Rain;
            if (segment.CloudCover >= FogCover && segment.WindSpeed <= FogMaxWind) return WorldWeatherKind.Fog;
            return segment.CloudCover >= CloudyCover ? WorldWeatherKind.Cloudy : WorldWeatherKind.Clear;
        }

        private static double Lerp(double a, double b, double t)
        {
            var k = Math.Clamp(t, 0.0, 1.0);
            return a + (b - a) * k;
        }

        private static long SecondOfDay(INeighbourhoodClock clock)
        {
            return NeighbourhoodWorldFeatures.SecondOfDay(clock);
        }
        /// <summary>One segment's five draws, before any term is added to them.</summary>
        private struct SegmentDraw
        {
            public readonly double CloudCover;
            public readonly double Precipitation;
            public readonly double WindSpeed;
            public readonly double ThunderChance;

            /// <summary>
            /// This segment's contribution to the temperature: the annual mean plus a bounded
            /// offset. The seasonal and diurnal terms are added later and are not part of the
            /// stream, so they cannot be perturbed by a retune of the draw order.
            /// </summary>
            public readonly double TemperatureC;

            public SegmentDraw(double cloudCover, double precipitation, double windSpeed,
                double thunderChance, double temperatureC)
            {
                CloudCover = cloudCover;
                Precipitation = precipitation;
                WindSpeed = windSpeed;
                ThunderChance = thunderChance;
                TemperatureC = temperatureC;
            }
        }

        /// <summary>Two weathers are equal when every field is.</summary>
        /// <param name="other">The weather to compare with.</param>
        public bool Equals(WorldWeather other)
        {
            return Kind == other.Kind
                && CloudCover == other.CloudCover
                && Precipitation == other.Precipitation
                && WindSpeed == other.WindSpeed
                && ThunderChance == other.ThunderChance
                && TemperatureC == other.TemperatureC
                && SegmentStartTick == other.SegmentStartTick
                && SegmentGameMinutes == other.SegmentGameMinutes;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is WorldWeather && Equals((WorldWeather)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = (int)Kind;
                hash = (hash * 397) ^ CloudCover.GetHashCode();
                hash = (hash * 397) ^ Precipitation.GetHashCode();
                hash = (hash * 397) ^ WindSpeed.GetHashCode();
                hash = (hash * 397) ^ TemperatureC.GetHashCode();
                hash = (hash * 397) ^ SegmentStartTick.GetHashCode();
                return hash;
            }
        }

        /// <summary>Two weathers are equal when every field is.</summary>
        /// <param name="a">One weather.</param>
        /// <param name="b">The other.</param>
        public static bool operator ==(WorldWeather a, WorldWeather b)
        {
            return a.Equals(b);
        }

        /// <summary>The negation of <see cref="op_Equality"/>.</summary>
        /// <param name="a">One weather.</param>
        /// <param name="b">The other.</param>
        public static bool operator !=(WorldWeather a, WorldWeather b)
        {
            return !a.Equals(b);
        }

        /// <summary>One line for a log: the condition and the five numbers behind it.</summary>
        public override string ToString()
        {
            return Kind
                + " cloud " + CloudCover.ToString("F2", CultureInfo.InvariantCulture)
                + " precip " + Precipitation.ToString("F2", CultureInfo.InvariantCulture)
                + " wind " + WindSpeed.ToString("F2", CultureInfo.InvariantCulture)
                + " " + TemperatureC.ToString("F1", CultureInfo.InvariantCulture) + "C";
        }
    }

    /// <summary>
    /// One lot that has company right now: which lot, what kind of company, how many, and the hours
    /// the schedule gave it.
    ///
    /// <para>
    /// A record rather than a lot number, because the two facts a caller cannot get back out of a
    /// number are how many people are in it and what kind of place it is - and both are the whole
    /// of what the off-screen simulator and a facade builder would ask for. A pure predicate that
    /// returned a bare <c>bool</c> for every lot in the neighbourhood, followed by a second walk to
    /// find out how many of them, is the shape this replaces.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodCommunityVisitor : IEquatable<NeighbourhoodCommunityVisitor>
    {
        /// <summary>The lot that has company, in the same numbering as <see cref="NeighbourhoodLot.Number"/>.</summary>
        public int LotNumber { get; }

        /// <summary>The lot's zone, copied from the layout rather than guessed from the kind.</summary>
        public short Zone { get; }

        /// <summary>What kind of company it is, derived from <see cref="Zone"/>.</summary>
        public NeighbourhoodVisitorKind Kind { get; }

        /// <summary>How many, from <see cref="NeighbourhoodWorldFeatures.MinVisitorsPerLot"/> to
        /// <see cref="NeighbourhoodWorldFeatures.MaxVisitorsPerLot"/>.</summary>
        public int HeadCount { get; }

        /// <summary>The hour this kind of lot starts being busy.</summary>
        public int OpensAtHour { get; }

        /// <summary>The hour it stops. Exclusive, like every window in this file.</summary>
        public int ClosesAtHour { get; }

        /// <summary>Creates a record. Clamps the head count into the model's bounds.</summary>
        /// <param name="lotNumber">The lot.</param>
        /// <param name="zone">Its zone.</param>
        /// <param name="kind">What kind of company it is.</param>
        /// <param name="headCount">How many, clamped.</param>
        /// <param name="opensAtHour">The hour it opens.</param>
        /// <param name="closesAtHour">The hour it closes.</param>
        public NeighbourhoodCommunityVisitor(int lotNumber, short zone, NeighbourhoodVisitorKind kind,
            int headCount, int opensAtHour, int closesAtHour)
        {
            LotNumber = lotNumber;
            Zone = zone;
            Kind = kind;
            HeadCount = Math.Clamp(headCount,
                NeighbourhoodWorldFeatures.MinVisitorsPerLot,
                NeighbourhoodWorldFeatures.MaxVisitorsPerLot);
            OpensAtHour = opensAtHour;
            ClosesAtHour = closesAtHour;
        }

        /// <summary>Two records are equal when every field is.</summary>
        /// <param name="other">The record to compare with.</param>
        public bool Equals(NeighbourhoodCommunityVisitor other)
        {
            return LotNumber == other.LotNumber
                && Zone == other.Zone
                && Kind == other.Kind
                && HeadCount == other.HeadCount
                && OpensAtHour == other.OpensAtHour
                && ClosesAtHour == other.ClosesAtHour;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is NeighbourhoodCommunityVisitor && Equals((NeighbourhoodCommunityVisitor)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = LotNumber;
                hash = (hash * 397) ^ Zone;
                hash = (hash * 397) ^ (int)Kind;
                hash = (hash * 397) ^ HeadCount;
                hash = (hash * 397) ^ OpensAtHour;
                hash = (hash * 397) ^ ClosesAtHour;
                return hash;
            }
        }

        /// <summary>Two records are equal when every field is.</summary>
        /// <param name="a">One record.</param>
        /// <param name="b">The other.</param>
        public static bool operator ==(NeighbourhoodCommunityVisitor a, NeighbourhoodCommunityVisitor b)
        {
            return a.Equals(b);
        }

        /// <summary>The negation of <see cref="op_Equality"/>.</summary>
        /// <param name="a">One record.</param>
        /// <param name="b">The other.</param>
        public static bool operator !=(NeighbourhoodCommunityVisitor a, NeighbourhoodCommunityVisitor b)
        {
            return !a.Equals(b);
        }

        /// <summary>One line for a log.</summary>
        public override string ToString()
        {
            return "lot " + LotNumber + " " + Kind + " x" + HeadCount
                + " " + OpensAtHour + "-" + ClosesAtHour;
        }
    }

    /// <summary>
    /// What one <see cref="NeighbourhoodWorldFeatures.Tick"/> actually did, so a caller can redraw
    /// on a change rather than every frame, and so a truncated fast-forward is visible rather than
    /// silent.
    /// </summary>
    public readonly struct WorldFeatureTickResult : IEquatable<WorldFeatureTickResult>
    {
        /// <summary>Real seconds of neighbourhood time this tick was allowed to advance.</summary>
        public double Seconds { get; }

        /// <summary>
        /// The seconds of the delta that did not fit and were dropped. Zero for an ordinary frame;
        /// positive for a caller that asked for a month at once, and the reason it is a field
        /// rather than a silently clamped number.
        /// </summary>
        public double TruncatedSeconds { get; }

        /// <summary>Clock ticks advanced, after the caps.</summary>
        public long TicksAdvanced { get; }

        /// <summary>How many weather segments the world crossed. Zero for a frame.</summary>
        public int WeatherSegmentsEntered { get; }

        /// <summary>Did the day phase change? The redraw trigger a lot view cares about.</summary>
        public bool PhaseChanged { get; }

        /// <summary>The day phase after the tick.</summary>
        public WorldDayPhase DayPhase { get; }

        /// <summary>Is it dark after the tick?</summary>
        public bool IsNight { get; }

        /// <summary>Did anything happen at all?</summary>
        public bool Changed
        {
            get { return TicksAdvanced > 0; }
        }

        /// <summary>
        /// A result with every field stated. <c>internal</c> because the only producer is
        /// <see cref="NeighbourhoodWorldFeatures.Tick"/>, and a public constructor here would be a
        /// second way to invent a tick report.
        /// </summary>
        internal WorldFeatureTickResult(double seconds, double truncatedSeconds, long ticksAdvanced,
            int weatherSegmentsEntered, bool phaseChanged, WorldDayPhase dayPhase, bool isNight)
        {
            Seconds = seconds;
            TruncatedSeconds = truncatedSeconds;
            TicksAdvanced = ticksAdvanced;
            WeatherSegmentsEntered = weatherSegmentsEntered;
            PhaseChanged = phaseChanged;
            DayPhase = dayPhase;
            IsNight = isNight;
        }

        /// <summary>
        /// A result for a tick that did nothing - a paused frame, a delta shorter than one
        /// sub-minute tick, or a world with no clock to advance.
        /// </summary>
        /// <param name="dayPhase">The phase the world is in, which has not changed.</param>
        /// <param name="isNight">Whether it is dark, which has not changed.</param>
        public static WorldFeatureTickResult NoChange(WorldDayPhase dayPhase, bool isNight)
        {
            return new WorldFeatureTickResult(0.0, 0.0, 0L, 0, false, dayPhase, isNight);
        }

        /// <summary>Two results are equal when every field is.</summary>
        /// <param name="other">The result to compare with.</param>
        public bool Equals(WorldFeatureTickResult other)
        {
            return Seconds == other.Seconds
                && TruncatedSeconds == other.TruncatedSeconds
                && TicksAdvanced == other.TicksAdvanced
                && WeatherSegmentsEntered == other.WeatherSegmentsEntered
                && PhaseChanged == other.PhaseChanged
                && DayPhase == other.DayPhase
                && IsNight == other.IsNight;
        }

        /// <inheritdoc/>
        public override bool Equals(object obj)
        {
            return obj is WorldFeatureTickResult && Equals((WorldFeatureTickResult)obj);
        }

        /// <inheritdoc/>
        public override int GetHashCode()
        {
            unchecked
            {
                var hash = Seconds.GetHashCode();
                hash = (hash * 397) ^ TruncatedSeconds.GetHashCode();
                hash = (hash * 397) ^ TicksAdvanced.GetHashCode();
                hash = (hash * 397) ^ WeatherSegmentsEntered;
                hash = (hash * 397) ^ (PhaseChanged ? 1 : 0);
                return hash;
            }
        }

        /// <summary>Two results are equal when every field is.</summary>
        /// <param name="a">One result.</param>
        /// <param name="b">The other.</param>
        public static bool operator ==(WorldFeatureTickResult a, WorldFeatureTickResult b)
        {
            return a.Equals(b);
        }

        /// <summary>The negation of <see cref="op_Equality"/>.</summary>
        /// <param name="a">One result.</param>
        /// <param name="b">The other.</param>
        public static bool operator !=(WorldFeatureTickResult a, WorldFeatureTickResult b)
        {
            return !a.Equals(b);
        }

        /// <summary>One line for a log.</summary>
        public override string ToString()
        {
            return TicksAdvanced + " tick(s) over " + Seconds.ToString("F2", CultureInfo.InvariantCulture)
                + "s, " + WeatherSegmentsEntered + " weather segment(s), "
                + DayPhase + (PhaseChanged ? " (changed)" : string.Empty)
                + (TruncatedSeconds > 0
                    ? " [truncated " + TruncatedSeconds.ToString("F0", CultureInfo.InvariantCulture) + "s]"
                    : string.Empty);
        }
    }

    /// <summary>
    /// B8: the world features that belong to the neighbourhood rather than to a lot - one sun,
    /// one sky, one weather, one idea of who is visiting - as a single pure object.
    ///
    /// <para>
    /// <b>One authority, and it is this object.</b> Time is <see cref="WorldClock"/>'s, the space
    /// is <see cref="NeighbourhoodQuery"/>'s, and the seed is the layout's; what lives here is the
    /// three things that would otherwise be derived once per lot view and quietly disagree between
    /// them. Two neighbours showing the same afternoon are the point, and the way to guarantee it
    /// is to have nothing per-lot to disagree about.
    /// </para>
    ///
    /// <para>
    /// <b>Pure, deterministic, and free of anything to break.</b> No graphics device, no
    /// <c>VM</c>, no content manager, no file, no wall clock, no <c>DateTime.Now</c>, no
    /// <c>string.GetHashCode</c>. Every number here is a function of the world seed and the
    /// neighbourhood clock, and a given frame rate cannot change any of them: <see cref="Tick"/>
    /// advances the clock and re-reads it, and nothing accumulates between calls. That is why
    /// this is testable at all, and why the tests can state the day/night boundaries as hours
    /// rather than as tolerances.
    /// </para>
    ///
    /// <para>
    /// <b>Which streams it draws from.</b> <see cref="NeighbourhoodStreams.Simulation"/>, and two
    /// labelled sub-streams off it - <see cref="WeatherStream"/> and <see cref="VisitorStream"/> -
    /// so retuning map generation cannot move the weather (§8's rule) and a second lot in the
    /// neighbourhood cannot move the first lot's visitors (the same labelled-sub-stream discipline
    /// <see cref="OffscreenSimulator"/> uses for its per-lot streams). Both are derived, not
    /// stored: two objects over the same seed hold the same states, and a test can assert that
    /// rather than trust it.
    /// </para>
    ///
    /// <para>
    /// <b>What it deliberately does not do.</b> It does not own time - <see cref="Clock"/> is the
    /// world's clock, and <see cref="Tick"/> advances it because the alternative is a features
    /// object whose weather runs on a different clock from the Sims' moods. It does not route a
    /// single Sim, spawn an avatar, or touch a lot: NPC routing is a B4/B5 concern that needs a
    /// <c>VM</c>, and B8's job is to answer "who is here and what is the weather like" well enough
    /// that a later step can wire it up without changing any of it. And it is not behind a flag -
    /// B8 is additive, and an object nobody constructs changes nothing.
    /// </para>
    ///
    /// <para>
    /// <b>Thread affinity: the game thread, only</b> (§8), for the same reason
    /// <see cref="SimitoneNeighbourhoodWorld"/> and <see cref="OffscreenSimulator"/> say it. One
    /// world, one writer, no locking.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldFeatures
    {
        /// <summary>
        /// The root label every stream in this file is derived under, below
        /// <see cref="NeighbourhoodStreams.Simulation"/>.
        /// </summary>
        public const string StreamLabel = "worldfeatures";

        /// <summary>The label of the weather stream, before the model version.</summary>
        public const string WeatherStreamLabel = StreamLabel + "/weather";

        /// <summary>The label of the visitor stream, before the model version.</summary>
        public const string VisitorStreamLabel = StreamLabel + "/visitors";

        /// <summary>
        /// Bumped whenever a change here would produce different weather for the same seed and the
        /// same clock. It is in the stream's label, so a retune lands on fresh numbers instead of
        /// moving a neighbourhood's weather under a player who saved mid-afternoon - the same
        /// contract <see cref="NeighbourhoodMapGenerator.GeneratorVersion"/> has.
        /// </summary>
        public const int WeatherModelVersion = 1;

        /// <summary>Bumped on the same terms as <see cref="WeatherModelVersion"/>, for the visitors.</summary>
        public const int VisitorModelVersion = 1;

        /// <summary>
        /// The weather stream's full key: its label and the model version. Not a
        /// <c>const</c> - a constant expression cannot concatenate an <c>int</c> - but it is a
        /// compile-time-shaped value that is built once.
        /// </summary>
        public static readonly string WeatherStreamKey = WeatherStreamLabel + "/v"
            + WeatherModelVersion.ToString(CultureInfo.InvariantCulture);

        /// <summary>The visitor stream's full key. See <see cref="WeatherStreamKey"/>.</summary>
        public static readonly string VisitorStreamKey = VisitorStreamLabel + "/v"
            + VisitorModelVersion.ToString(CultureInfo.InvariantCulture);

        /// <summary>Seconds in a day. The unit every time-of-day normalisation is folded into.</summary>
        public const int SecondsPerDay = 24 * 60 * 60;

        /// <summary>
        /// When the sun comes up. Chosen so the <see cref="WorldTimeOfDay.SunAngleDegrees"/> arc
        /// crosses zero here exactly, which is a consequence of the arc's shape rather than a
        /// coincidence - see <see cref="MaxSunElevationDegrees"/>.
        /// </summary>
        public const int SunriseHour = 6;

        /// <summary>When the sun goes down. See <see cref="SunriseHour"/>.</summary>
        public const int SunsetHour = 18;

        /// <summary>The hour <see cref="WorldDayPhase.Dawn"/> starts.</summary>
        public const int DawnStartHour = 4;

        /// <summary>The hour <see cref="WorldDayPhase.Day"/> starts, which is sunrise.</summary>
        public const int DayStartHour = SunriseHour;

        /// <summary>The hour <see cref="WorldDayPhase.Dusk"/> starts, which is sunset.</summary>
        public const int DuskStartHour = SunsetHour;

        /// <summary>The hour <see cref="WorldDayPhase.Night"/> starts. An hour after sunset, so the
        /// two phases that report dark are not the same hour.</summary>
        public const int NightStartHour = 20;

        /// <summary>
        /// The sun's elevation at solar noon, in degrees. Flat, not 90: a real sun at a real
        /// latitude never stands straight overhead, and 70 degrees keeps the noon highlight from
        /// being a spot light pointed at the camera.
        /// </summary>
        public const double MaxSunElevationDegrees = 70.0;

        /// <summary>The hour a community lot opens to the neighbourhood.</summary>
        public const int CommunityOpenHour = 9;

        /// <summary>The hour a community lot closes. Exclusive, like every window in this file.</summary>
        public const int CommunityCloseHour = 22;

        /// <summary>The hour a studio lot opens. A shorter day than a community lot's.</summary>
        public const int StudioOpenHour = 10;

        /// <summary>The hour a studio lot closes.</summary>
        public const int StudioCloseHour = 20;

        /// <summary>The first hour a residential lot may have visitors.</summary>
        public const int NeighbourFirstHour = 16;

        /// <summary>The last hour a residential lot may have visitors. Exclusive.</summary>
        public const int NeighbourLastHour = 23;

        /// <summary>The hour a residential neighbourhood is busiest. A guess, and a named one.</summary>
        public const int PeakVisitorHour = 20;

        /// <summary>How far either side of the peak the visiting hump reaches, in hours.</summary>
        public const int NeighbourWindowHalfWidthHours = 7;

        /// <summary>
        /// The chance a residential lot is having visitors at the peak hour. Low enough that most
        /// of a street is not having a party and high enough that some of it usually is.
        /// </summary>
        public const double MaxNeighbourProbability = 0.6;

        /// <summary>Fewest visitors a lot with company ever has.</summary>
        public const int MinVisitorsPerLot = 1;

        /// <summary>Most visitors a lot with company ever has.</summary>
        public const int MaxVisitorsPerLot = 4;

        /// <summary>
        /// How many lots may be listed as having company at once. A cap and not a probability
        /// because a caller that acts on this list - spawning a visitor, holding a door open -
        /// must be able to budget it, and "at most four" is a budget.
        /// </summary>
        public const int MaxActiveVisitorLots = 4;

        /// <summary>
        /// The most real time one <see cref="Tick"/> will advance, in seconds: a game day, which at
        /// TS1's rate is 43,200 clock ticks. Same rationale as
        /// <see cref="OffscreenSimulator.MaxSecondsPerTick"/> - a caller that has not ticked for a
        /// month is asking for a lot of work, and the excess is reported rather than absorbed.
        /// </summary>
        public const int DefaultMaxSecondsPerTick = 60 * 24;

        private static readonly ReadOnlyCollection<NeighbourhoodCommunityVisitor> NoVisitors =
            new ReadOnlyCollection<NeighbourhoodCommunityVisitor>(new NeighbourhoodCommunityVisitor[0]);

        private readonly WorldClock _clock;
        private readonly NeighbourhoodLayout _layout;
        private int _maxSecondsPerTick = DefaultMaxSecondsPerTick;
        private WorldLightingState _lighting;
        private WorldWeather _weather;
        private IReadOnlyList<NeighbourhoodCommunityVisitor> _visitors = NoVisitors;
        private long _ticksAdvanced;
        private double _elapsedSeconds;

        /// <summary>
        /// The world's features over a clock and a layout, at the layout's own seed.
        /// </summary>
        /// <param name="clock">
        /// The neighbourhood's clock. Required, and it is the world's - this type advances it
        /// rather than keeping a second one, so a neighbourhood's Sims and its weather cannot end
        /// up on different clocks.
        /// </param>
        /// <param name="layout">The packed layout. A null layout becomes an empty one.</param>
        public NeighbourhoodWorldFeatures(WorldClock clock, NeighbourhoodLayout layout)
            : this(clock, layout?.Seed ?? 0L, layout)
        {
        }

        /// <summary>The world's features over a clock, with no lots - the seed is 0.</summary>
        /// <param name="clock">The neighbourhood's clock. Required; null throws.</param>
        public NeighbourhoodWorldFeatures(WorldClock clock)
            : this(clock, 0L, null)
        {
        }

        /// <summary>
        /// The world's features over a clock, a seed and a layout, all three stated.
        /// </summary>
        /// <param name="clock">The neighbourhood's clock. Required; null throws.</param>
        /// <param name="worldSeed">
        /// The seed the weather and the visitors are drawn from. Normally the layout's own
        /// <see cref="NeighbourhoodLayout.Seed"/>, which is what makes a neighbourhood's weather a
        /// property of the neighbourhood rather than of the session that loaded it.
        /// </param>
        /// <param name="layout">The packed layout, or null for no lots.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> is null.</exception>
        public NeighbourhoodWorldFeatures(WorldClock clock, long worldSeed, NeighbourhoodLayout layout)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));

            _clock = clock;
            _layout = layout ?? NeighbourhoodLayout.Empty(0, (int)worldSeed, 0);
            WorldSeed = worldSeed;

            //Read the clock once here rather than trusting a cached copy: every derived value
            //below is a function of it, and a features object constructed against a clock that had
            //not been told its sub-minute rate yet would draw its weather on a different clock.
            Refresh();
        }

        /// <summary>
        /// The seed the weather and the visitors are drawn from. Kept beside the layout's own so a
        /// caller that seeds a features object explicitly can be seen to have done so.
        /// </summary>
        public long WorldSeed { get; }

        /// <summary>The neighbourhood's clock - the world's, not a second one.</summary>
        public WorldClock Clock
        {
            get { return _clock; }
        }

        /// <summary>The layout. Never null; an empty one when the constructor was given none.</summary>
        public NeighbourhoodLayout Layout
        {
            get { return _layout; }
        }

        /// <summary>How many lots this world's features can put company in.</summary>
        public int LotCount
        {
            get { return _layout.LotCount; }
        }

        /// <summary>
        /// The three streams the seed fans out into, exposed so the derivation can be asserted.
        /// The ones this type draws from are the two labelled sub-streams below, never
        /// <see cref="NeighbourhoodStreams.Generation"/>.
        /// </summary>
        public NeighbourhoodStreams Streams
        {
            get { return NeighbourhoodStreams.FromSeed(WorldSeed); }
        }

        /// <summary>
        /// The root of the weather's draws: <see cref="NeighbourhoodStreams.Simulation"/> under
        /// <see cref="WeatherStreamKey"/>. Derived, so two objects over one seed hold the same
        /// stream and a test can check it.
        /// </summary>
        public NeighbourhoodRng WeatherStream
        {
            get { return Streams.Simulation.ForLabel(WeatherStreamKey); }
        }

        /// <summary>
        /// The root of the visitors' draws: <see cref="NeighbourhoodStreams.Simulation"/> under
        /// <see cref="VisitorStreamKey"/>.
        /// </summary>
        public NeighbourhoodRng VisitorStream
        {
            get { return Streams.Simulation.ForLabel(VisitorStreamKey); }
        }

        /// <summary>
        /// Game minutes per weather segment. Defaults to
        /// <see cref="WorldWeather.DefaultSegmentGameMinutes"/>; changing it makes the weather vary
        /// faster or slower and nothing else.
        /// </summary>
        /// <remarks>
        /// Read every time a weather segment index is computed rather than cached, because the
        /// clock's own sub-minute rate can change underneath it when a lot is pushed into it
        /// (§5.2's handshake). A features object that had cached the rate at construction would
        /// quietly run its weather on a different clock from the Sims.
        /// </remarks>
        public int WeatherSegmentGameMinutes { get; set; } = WorldWeather.DefaultSegmentGameMinutes;

        /// <summary>
        /// The most real time one <see cref="Tick"/> will advance, in seconds. Defaults to
        /// <see cref="DefaultMaxSecondsPerTick"/>; positive, and no smaller than one frame.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A value that is not positive.</exception>
        public int MaxSecondsPerTick
        {
            get { return _maxSecondsPerTick; }
            set
            {
                if (value <= 0)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value,
                        "A tick that advances no time is a paused world, not a small one.");
                }
                _maxSecondsPerTick = value;
            }
        }

        /// <summary>
        /// The world's lighting right now. A pure function of <see cref="Clock"/>, cached at
        /// construction and re-read by every <see cref="Tick"/> and by <see cref="Refresh"/>.
        /// </summary>
        public WorldLightingState Lighting
        {
            get { return _lighting; }
        }

        /// <summary>
        /// The world's weather right now, shared by every lot in it. A pure function of
        /// <see cref="WorldSeed"/> and <see cref="Clock"/>.
        /// </summary>
        public WorldWeather Weather
        {
            get { return _weather; }
        }

        /// <summary>
        /// The lots with company right now, at most
        /// <see cref="MaxActiveVisitorLots"/> of them, in ascending lot-number order. Never null;
        /// empty for a world with no lots, and empty at an hour when nothing is open.
        /// </summary>
        /// <remarks>
        /// <b>The list is a bounded projection of the predicate, not the same thing.</b>
        /// <see cref="IsLotActive(int)"/> is the honest answer for any one lot and can be true for
        /// more lots than this list has room for on a busy evening in a residential neighbourhood.
        /// The list is what a caller may act on without budgeting, and it takes the first lots in
        /// ascending order, so "which four" is a function of the lot numbering rather than of a
        /// dictionary's iteration.
        /// </remarks>
        public IReadOnlyList<NeighbourhoodCommunityVisitor> Visitors
        {
            get { return _visitors; }
        }

        /// <summary>Is it dark right now? Shorthand for <see cref="Lighting"/>.</summary>
        public bool IsNight
        {
            get { return _lighting.IsNight; }
        }

        /// <summary>Which band of the day it is. Shorthand for <see cref="Lighting"/>.</summary>
        public WorldDayPhase DayPhase
        {
            get { return _lighting.TimeOfDay.Phase; }
        }

        /// <summary>Clock ticks this object has advanced, since construction or <see cref="Reset"/>.</summary>
        public long TicksAdvanced
        {
            get { return _ticksAdvanced; }
        }

        /// <summary>
        /// Real seconds this object has been told about, after the
        /// <see cref="MaxSecondsPerTick"/> cap. A counter for a log line, not a time source -
        /// the clock is the time source.
        /// </summary>
        public double ElapsedSeconds
        {
            get { return _elapsedSeconds; }
        }

        /// <summary>
        /// Is <paramref name="lotNumber"/> open or having visitors at <paramref name="clock"/>?
        /// A pure predicate over the world seed, the clock and the layout: no state, no order of
        /// calls, and the same answer every time for the same three.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The rules, in the order they are tested:
        /// </para>
        /// <list type="bullet">
        ///   <item><description>
        ///     A lot that is not in the layout is never active, and neither is a bulldozed plot -
        ///     there is no door for a visitor to arrive at, which is the same reason
        ///     <see cref="NeighbourhoodQuery"/> gives a plot no entrance.
        ///   </description></item>
        ///   <item><description>
        ///     A community lot (<see cref="Simitone.Client.Utils.LotZoning.ZoneCommunity"/>) is
        ///     open from <see cref="CommunityOpenHour"/> to <see cref="CommunityCloseHour"/>. It is
        ///     open or it is not; the draw does not enter into it, because a park that is sometimes
        ///     shut is a bug rather than a mood.
        ///   </description></item>
        ///   <item><description>
        ///     A studio lot is open from <see cref="StudioOpenHour"/> to
        ///     <see cref="StudioCloseHour"/> - a shorter day, because a studio is a workplace.
        ///   </description></item>
        ///   <item><description>
        ///     Everything else - residential lots, and any zone a build that is not this one wrote -
        ///     is a residential lot having visitors: the hour must be inside
        ///     <see cref="NeighbourFirstHour"/>..<see cref="NeighbourLastHour"/>, <em>and</em> the
        ///     lot's own draw for today must fall under the hour's visiting probability.
        ///   </description></item>
        /// </list>
        /// <para>
        /// The per-lot draw is keyed by the lot number and by the <em>day</em>, not by a position in
        /// a shared sequence. That is what makes a street's evening reproducible, and what keeps
        /// bulldozing one lot from moving who is visiting the next one.
        /// </para>
        /// </remarks>
        /// <param name="lotNumber">The lot to ask about.</param>
        /// <param name="clock">The neighbourhood's clock. Required; null throws.</param>
        /// <param name="worldSeed">The seed the draw comes from.</param>
        /// <param name="layout">The layout. Required; null throws.</param>
        /// <returns>Whether the lot has company now.</returns>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> or <paramref name="layout"/> is null.</exception>
        public static bool IsLotActive(int lotNumber, INeighbourhoodClock clock, long worldSeed,
            NeighbourhoodLayout layout)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (layout == null) throw new ArgumentNullException(nameof(layout));

            var lot = layout.FindLot(lotNumber);
            if (lot == null || !lot.HasHouse) return false;

            var hour = (int)(SecondOfDay(clock) / 3600L);
            if (lot.Zone == LotZoning.ZoneCommunity)
            {
                return hour >= CommunityOpenHour && hour < CommunityCloseHour;
            }
            if (lot.Zone == LotZoning.ZoneStudio)
            {
                return hour >= StudioOpenHour && hour < StudioCloseHour;
            }
            if (hour < NeighbourFirstHour || hour >= NeighbourLastHour) return false;

            return NeighbourDraw(worldSeed, DayIndex(clock), lotNumber) < NeighbourProbabilityForHour(hour);
        }

        /// <summary>
        /// <see cref="IsLotActive(int,INeighbourhoodClock,long,NeighbourhoodLayout)"/> for this
        /// world's own seed, layout and clock.
        /// </summary>
        /// <param name="lotNumber">The lot to ask about.</param>
        public bool IsLotActive(int lotNumber)
        {
            return IsLotActive(lotNumber, _clock, WorldSeed, _layout);
        }

        /// <summary>
        /// The lots with company at <paramref name="clock"/>, at most
        /// <see cref="MaxActiveVisitorLots"/> of them, ascending by lot number. Pure, and never
        /// null.
        /// </summary>
        /// <remarks>
        /// The walk is in the layout's own order, which is ascending by lot number
        /// (<see cref="NeighbourhoodLayout"/>'s canonical order), so two objects over the same
        /// facts return the same list in the same order and the cap takes the same four lots.
        /// </remarks>
        /// <param name="clock">The neighbourhood's clock. Required; null throws.</param>
        /// <param name="worldSeed">The seed the draws come from.</param>
        /// <param name="layout">The layout. Required; null throws.</param>
        /// <exception cref="ArgumentNullException"><paramref name="clock"/> or <paramref name="layout"/> is null.</exception>
        public static IReadOnlyList<NeighbourhoodCommunityVisitor> VisitorsAt(
            INeighbourhoodClock clock, long worldSeed, NeighbourhoodLayout layout)
        {
            if (clock == null) throw new ArgumentNullException(nameof(clock));
            if (layout == null) throw new ArgumentNullException(nameof(layout));

            var lots = layout.Lots;
            if (lots.Count == 0) return NoVisitors;

            var hour = (int)(SecondOfDay(clock) / 3600L);
            var day = DayIndex(clock);
            var found = new List<NeighbourhoodCommunityVisitor>(MaxActiveVisitorLots);
            for (int i = 0; i < lots.Count && found.Count < MaxActiveVisitorLots; i++)
            {
                var lot = lots[i];
                if (!IsLotActive(lot.Number, clock, worldSeed, layout)) continue;
                found.Add(Describe(lot, worldSeed, day, hour));
            }
            return found.Count == 0 ? NoVisitors : new ReadOnlyCollection<NeighbourhoodCommunityVisitor>(found);
        }

        /// <summary>
        /// Re-read the clock and recompute the lighting, the weather and the visitor list, without
        /// advancing anything. A caller that moved the clock itself - a lot switch, a load, a
        /// fast-forward by somebody else - calls this so the cached state is not stale.
        /// </summary>
        public void Refresh()
        {
            _lighting = WorldLightingState.FromClock(_clock);
            _weather = WorldWeather.At(WorldSeed, _clock, WeatherSegmentGameMinutes);
            _visitors = VisitorsAt(_clock, WorldSeed, _layout);
        }

        /// <summary>
        /// Advance the world by <paramref name="deltaSeconds"/> of real time and re-read
        /// everything, reporting what the tick did.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The clock is advanced here, not shadowed.</b> The neighbourhood's time is one
        /// authority (§5.2), and a features object that kept a second one would put the weather and
        /// the weather in the next lot on different clocks - which is precisely the bug centralised
        /// world features exist to prevent. So this calls the world's clock's own tick, and the
        /// counts the sub-minute ticks it spent rather than guessing at one.
        /// </para>
        /// <para>
        /// <b>One game minute per real second, as everywhere else in Part B.</b> The count is
        /// <c>seconds * TicksPerMinute</c>, which is <see cref="OffscreenSimulator"/>'s arithmetic
        /// and inherits its assumption: TS1 runs a game minute per real second, and a clock at some
        /// other rate is carried by the same expression rather than by a second opinion about how
        /// long a minute is.
        /// </para>
        /// <para>
        /// <b>Whole ticks, truncated, and the frame's remainder dropped</b> - the same truncation
        /// a lot's own <c>VMClock</c> does, and why this takes a delta instead of assuming a frame
        /// length. It is also why nothing here accumulates between calls: a world ticked in 10ms
        /// steps and one ticked once a second end a game day in the same state, because the state
        /// is a function of <see cref="INeighbourhoodClock.Ticks"/> and not of how the ticks were
        /// delivered.
        /// </para>
        /// <para>
        /// <b>Capped, and the excess reported.</b> A delta longer than
        /// <see cref="MaxSecondsPerTick"/> is clamped and the remainder lands in
        /// <see cref="WorldFeatureTickResult.TruncatedSeconds"/>, because a silent truncation reads
        /// as a bug and a loud one reads as a decision. A negative delta or NaN is a caller bug and
        /// throws, exactly as <see cref="OffscreenSimulator.Tick"/> does: a paused frame is a
        /// delta of zero, and a clock that has been rewound is somebody re-seeding from a save.
        /// </para>
        /// </remarks>
        /// <param name="deltaSeconds">Real seconds elapsed. Zero is a valid no-op.</param>
        /// <returns>What the tick did; see <see cref="WorldFeatureTickResult"/>.</returns>
        /// <exception cref="ArgumentOutOfRangeException">
        /// <paramref name="deltaSeconds"/> is negative or NaN.
        /// </exception>
        public WorldFeatureTickResult Tick(double deltaSeconds)
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

            var before = _lighting.TimeOfDay.Phase;
            var segmentBefore = SegmentIndex();

            //+Infinity lands in this branch too, which is the right answer for it: an infinite
            //frame is a very long frame, and the cap is exactly the statement of what this type
            //does with one. The comparison is written as "not <= " so it catches it.
            var seconds = deltaSeconds;
            var truncated = 0.0;
            if (!(seconds <= _maxSecondsPerTick))
            {
                truncated = seconds - _maxSecondsPerTick;
                seconds = _maxSecondsPerTick;
            }

            var ticks = (long)Math.Floor(seconds * TicksPerMinute(_clock));
            if (ticks > 0)
            {
                for (long i = 0; i < ticks; i++) _clock.Tick();
                _ticksAdvanced += ticks;
                _elapsedSeconds += seconds;
                Refresh();
            }

            var after = _lighting.TimeOfDay.Phase;
            return new WorldFeatureTickResult(seconds, truncated, ticks,
                (int)(SegmentIndex() - segmentBefore), after != before, after, _lighting.IsNight);
        }

        /// <summary>
        /// Zero this object's own counters - <see cref="TicksAdvanced"/> and
        /// <see cref="ElapsedSeconds"/> - and re-read the clock. The clock itself is not touched:
        /// this type does not own time and cannot put it back.
        /// </summary>
        public void Reset()
        {
            _ticksAdvanced = 0L;
            _elapsedSeconds = 0.0;
            Refresh();
        }

        /// <summary>
        /// Everything worth putting in a log about the world's features, in four lines: the
        /// lighting, the weather, the visitors and the counters.
        /// </summary>
        /// <remarks>
        /// <b>Locale-independent and environment-free</b>, on purpose: the line is built from the
        /// normalised clock reading and invariant number formats, so two machines asked for the
        /// same neighbourhood at the same minute produce the same text - which is what makes it
        /// worth diffing when somebody reports that it is always raining in one save.
        /// </remarks>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("world features seed ").Append(WorldSeed)
              .Append(", ").Append(LotCount).Append(" lot(s)")
              .Append('\n');

            sb.Append("  lighting: ").Append(_lighting.TimeOfDay.Hour).Append(':')
              .Append(_lighting.TimeOfDay.Minute)
              .Append(' ').Append(DayPhase).Append(IsNight ? " (night)" : string.Empty)
              .Append(", sun ").Append(_lighting.SunAngleDegrees.ToString("F1", CultureInfo.InvariantCulture))
              .Append("deg @").Append(_lighting.TimeOfDay.SunAzimuthDegrees.ToString("F0", CultureInfo.InvariantCulture))
              .Append(" ").Append(_lighting.SunColour)
              .Append(" x").Append(_lighting.SunIntensity.ToString("F3", CultureInfo.InvariantCulture))
              .Append(", ambient ").Append(_lighting.AmbientColour)
              .Append(" x").Append(_lighting.AmbientIntensity.ToString("F3", CultureInfo.InvariantCulture))
              .Append('\n');

            sb.Append("  weather: ").Append(_weather)
              .Append(", segment ").Append(_weather.SegmentStartTick)
              .Append(" (").Append(_weather.SegmentGameMinutes).Append(" game min)")
              .Append('\n');

            sb.Append("  visitors: ").Append(_visitors.Count).Append(" of ")
              .Append(MaxActiveVisitorLots).Append(" slot(s)\n");
            for (int i = 0; i < _visitors.Count; i++)
            {
                sb.Append("    ").Append(_visitors[i]).Append('\n');
            }
            if (_visitors.Count == 0)
            {
                sb.Append("    nobody is about\n");
            }

            sb.Append("  advanced ").Append(_ticksAdvanced).Append(" clock tick(s) over ")
              .Append(_elapsedSeconds.ToString("F1", CultureInfo.InvariantCulture)).Append("s\n");
            return sb.ToString();
        }

        public override string ToString()
        {
            return "world features seed " + WorldSeed + " " + _lighting + " " + _weather;
        }

        // ---------------------------------------------------------------- shared arithmetic

        /// <summary>
        /// Seconds since midnight, folded into one day. The one time normalisation, shared with
        /// the lighting and the visitor schedule so the three cannot read the clock differently.
        /// </summary>
        internal static long SecondOfDay(INeighbourhoodClock clock)
        {
            var second = (long)clock.Hours * 3600L + (long)clock.Minutes * 60L + clock.Seconds;
            second %= SecondsPerDay;
            if (second < 0) second += SecondsPerDay;
            return second;
        }

        /// <summary>
        /// The band's hour falls in. A 0..23 hour, folded first, because a clock that has been
        /// given an hour of 47 has not invented a new band.
        /// </summary>
        /// <param name="hour">The hour, in any range.</param>
        public static WorldDayPhase PhaseForHour(int hour)
        {
            var h = hour % HoursPerDay;
            if (h < 0) h += HoursPerDay;

            //The two hours that straddle the horizon come first, so the arithmetic below reads as
            //the day does: dark, first light, full day, last light, dark again.
            if (h < DawnStartHour || h >= NightStartHour) return WorldDayPhase.Night;
            if (h < DayStartHour) return WorldDayPhase.Dawn;
            if (h < DuskStartHour) return WorldDayPhase.Day;
            return WorldDayPhase.Dusk;
        }

        /// <summary>Hours in a day, restated so the phase arithmetic above reads in the same units.</summary>
        public const int HoursPerDay = 24;

        /// <summary>Game minutes in a day. The unit a day index is counted in.</summary>
        public const int GameMinutesPerDay = 24 * 60;

        /// <summary>
        /// The clock's sub-minute rate, or TS1's if the clock has none. <c>internal</c> because
        /// <see cref="WorldWeather"/> derives its segment width from the same rate, and the two
        /// must not be able to disagree about it.
        /// </summary>
        internal static int TicksPerMinute(INeighbourhoodClock clock)
        {
            //A clock that has never been given a time is not necessarily one that has a rate, and
            //WorldClock divides by its own unguarded. The fallback is TS1's, which is what
            //WorldClock's constructor starts it at.
            return clock.TicksPerMinute > 0 ? clock.TicksPerMinute : WorldClock.TicksPerMinuteTs1;
        }

        /// <summary>Ticks in one weather segment, from the clock's own sub-minute rate.</summary>
        internal static long TicksPerSegment(INeighbourhoodClock clock, int segmentGameMinutes)
        {
            return (long)TicksPerMinute(clock) * segmentGameMinutes;
        }

        /// <summary>
        /// Which day of the world's run this is, counted in whole game days from the clock's own
        /// tick count. Absolute and derived, which is what makes yesterday's evening reproducible.
        /// </summary>
        private static long DayIndex(INeighbourhoodClock clock)
        {
            var perDay = TicksPerMinute(clock) * (long)GameMinutesPerDay;
            if (perDay <= 0) return 0L;
            return clock.Ticks / perDay;
        }

        /// <summary>
        /// The root every weather draw comes off. <c>internal</c> for the same reason as
        /// <see cref="TicksPerSegment"/>: <see cref="WorldWeather"/> must draw from exactly the
        /// stream <see cref="NeighbourhoodWorldFeatures.WeatherStream"/> hands out.
        /// </summary>
        internal static NeighbourhoodRng WeatherRoot(long worldSeed)
        {
            return NeighbourhoodStreams.FromSeed(worldSeed).Simulation.ForLabel(WeatherStreamKey);
        }

        private static NeighbourhoodRng VisitorRoot(long worldSeed)
        {
            return NeighbourhoodStreams.FromSeed(worldSeed).Simulation.ForLabel(VisitorStreamKey);
        }

        /// <summary>One lot's own draw for one day: a value in <c>[0, 1)</c>.</summary>
        private static double NeighbourDraw(long worldSeed, long dayIndex, int lotNumber)
        {
            var rng = VisitorRoot(worldSeed)
                .ForLabel("day/" + dayIndex.ToString(CultureInfo.InvariantCulture))
                .ForLabel("lot/" + lotNumber.ToString(CultureInfo.InvariantCulture));
            return rng.NextDouble();
        }

        /// <summary>
        /// How likely a residential lot is to be having visitors at this hour: a hump falling off
        /// from <see cref="PeakVisitorHour"/>, and zero outside
        /// <see cref="NeighbourFirstHour"/>..<see cref="NeighbourLastHour"/>.
        /// </summary>
        private static double NeighbourProbabilityForHour(int hour)
        {
            var off = Math.Abs(hour - PeakVisitorHour);
            if (off >= NeighbourWindowHalfWidthHours) return 0.0;
            return MaxNeighbourProbability * (1.0 - off / (double)NeighbourWindowHalfWidthHours);
        }

        /// <summary>The record for a lot the predicate has already accepted.</summary>
        private static NeighbourhoodCommunityVisitor Describe(NeighbourhoodLot lot, long worldSeed,
            long dayIndex, int hour)
        {
            var kind = KindForZone(lot.Zone);
            var opens = kind == NeighbourhoodVisitorKind.Studio ? StudioOpenHour
                : kind == NeighbourhoodVisitorKind.Community ? CommunityOpenHour
                : NeighbourFirstHour;
            var closes = kind == NeighbourhoodVisitorKind.Studio ? StudioCloseHour
                : kind == NeighbourhoodVisitorKind.Community ? CommunityCloseHour
                : NeighbourLastHour;

            return new NeighbourhoodCommunityVisitor(lot.Number, lot.Zone, kind,
                HeadCountFor(worldSeed, dayIndex, lot.Number, hour), opens, closes);
        }

        /// <summary>How many are in the house. A draw per lot per day, bounded by the model.</summary>
        private static int HeadCountFor(long worldSeed, long dayIndex, int lotNumber, int hour)
        {
            var rng = VisitorRoot(worldSeed)
                .ForLabel("day/" + dayIndex.ToString(CultureInfo.InvariantCulture))
                .ForLabel("head/" + lotNumber.ToString(CultureInfo.InvariantCulture));
            return MinVisitorsPerLot + rng.NextInt(0, MaxVisitorsPerLot);
        }

        /// <summary>What kind of company a zone has. An unknown zone is treated as residential.</summary>
        private static NeighbourhoodVisitorKind KindForZone(short zone)
        {
            if (zone == LotZoning.ZoneCommunity) return NeighbourhoodVisitorKind.Community;
            if (zone == LotZoning.ZoneStudio) return NeighbourhoodVisitorKind.Studio;
            return NeighbourhoodVisitorKind.Neighbour;
        }

        /// <summary>Which weather segment the clock is in right now.</summary>
        private long SegmentIndex()
        {
            var perSegment = (long)TicksPerMinute(_clock) * WeatherSegmentGameMinutes;
            if (perSegment <= 0) return 0L;
            return _clock.Ticks / perSegment;
        }
    }
}
