/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// How far the neighbourhood clock has to move before the live lot's outside colour is written
    /// again, and the latch that remembers when it last was.
    ///
    /// <remarks>
    /// <para>
    /// <b>The step.</b> A quarter of an hour of world time. The engine's own seam -
    /// <c>VMArchitecture.SetTimeOfDay</c> - rebuilds the lot's outdoor light data, so applying on
    /// every frame would be sixty light rebuilds a second to produce a difference nobody can see. A
    /// quarter of an hour is well inside the finest step any of the light's own curves has (the
    /// sun's horizon ramp is about twenty-five minutes wide), so nothing pops by being held back. A
    /// night crossing is <em>not</em> held back: the sun-up test applies the moment it flips, because
    /// that is the one change a player sees as a step rather than as a drift.
    /// </para>
    ///
    /// <para>
    /// <b>Why this is a type and not two fields on the screen.</b> The rule used to live as
    /// <c>LightingAppliedDayFraction</c> and <c>LightingAppliedSunUp</c> fields on
    /// <c>TS1GameScreen</c>, which meant two things at once: it could not be tested at all, because
    /// the screen needs a graphics device, and it could not be reset, because nothing held a handle
    /// to it. So the second lot of a session could arrive within a quarter hour of the hour the
    /// first lot was written at and simply never be written at all - and there was nothing in the log
    /// to tell that from a lot that was written and happened to look the same. A struct with a
    /// <see cref="NeighbourhoodWorldLightApplyGate.Reset"/> fixes the second half; being its own type
    /// fixes the first.
    /// </para>
    /// </remarks>
    public struct NeighbourhoodWorldLightApplyGate
    {
        /// <summary>
        /// How far the fraction may move before it counts, as a fraction of a day. A quarter of an
        /// hour of neighbourhood time.
        /// </summary>
        public const double WorldLightApplyStep = 1.0 / 96.0;

        /// <summary>
        /// The day fraction a latch that has never applied anything is left holding. Only ever
        /// written by <see cref="Reset"/>; <see cref="HasApplied"/> reads the flag beside it rather
        /// than this number, because a default-constructed struct holds <c>0.0</c> here and midnight
        /// is a perfectly ordinary hour to have been written at.
        /// </summary>
        public const double NeverAppliedDayFraction = -1.0;

        /// <summary>The day fraction this gate last applied at.</summary>
        public double AppliedDayFraction;

        /// <summary>Whether this gate last applied with the sun up.</summary>
        public bool AppliedSunUp;

        /// <summary>
        /// Whether anything has ever been applied. Its own field rather than a test on
        /// <see cref="AppliedDayFraction"/>, so that a gate nobody has touched - which is what
        /// <c>default</c> is - answers "never" rather than "at midnight", and a lot's first frame
        /// always writes.
        /// </summary>
        private bool hasApplied;

        /// <summary>Whether this gate has ever applied anything.</summary>
        public bool HasApplied
        {
            get { return hasApplied; }
        }

        /// <summary>
        /// Whether the lot's outside colour should be written for <paramref name="dayFraction"/>.
        /// </summary>
        /// <remarks>
        /// The two tests are independent, and each alone is enough. The step test is the "nobody can
        /// see a difference" one; the sun-up test is the "a player can see this as a step" one.
        /// Taking the larger of the two would hold a sunrise back by up to a quarter of an hour,
        /// which is the one thing a player would notice.
        /// </remarks>
        /// <param name="dayFraction">The time of day, as a fraction of a day.</param>
        /// <param name="sunUp">Whether the sun is up at that time.</param>
        /// <param name="step">
        /// How far the fraction may move before it counts. A non-positive value means "any change
        /// applies", and is honoured rather than clamped, because a caller that wants every change
        /// written should get what it asked for rather than a number it did not.
        /// </param>
        public bool ShouldApply(double dayFraction, bool sunUp, double step = WorldLightApplyStep)
        {
            if (!IsFinite(dayFraction)) return false;
            //A fraction outside a day is not a time of day. VMArchitecture.SetTimeOfDay indexes its
            //own sky ramp with the value it is given, and WorldClock.SeedFromGlobalState
            //deliberately does not normalise - a corrupt or hand-edited global can put hour 47 in
            //it - so this is reachable. Refusing is the only safe answer, and it is also the one the
            //caller's try/catch used to have to produce several times a second.
            if (dayFraction < 0.0 || dayFraction >= 1.0) return false;

            //Never applied for this lot: always apply.
            if (!hasApplied) return true;

            //A step of zero or less means "any change at all", honoured rather than clamped: a
            //caller that wants every change written should get what it asked for.
            if (step <= 0) return true;

            //The move a player cannot see.
            if (Math.Abs(dayFraction - AppliedDayFraction) >= step) return true;

            //The move a player sees as a step: sunrise and sunset are never held back.
            return sunUp != AppliedSunUp;
        }

        /// <summary>
        /// Record that the lot has now been written for this time, so the next call can decide
        /// whether anything has moved far enough to write it again.
        /// </summary>
        /// <param name="dayFraction">The time of day it was written at.</param>
        /// <param name="sunUp">Whether the sun was up at that time.</param>
        public void Apply(double dayFraction, bool sunUp)
        {
            AppliedDayFraction = dayFraction;
            AppliedSunUp = sunUp;
            hasApplied = true;
        }

        /// <summary>
        /// Forget everything, so the next call applies. Called on every lot load: a latch that
        /// survived one is a latch that could refuse to light a lot that had never been lit at that
        /// hour.
        /// </summary>
        public void Reset()
        {
            AppliedDayFraction = NeverAppliedDayFraction;
            AppliedSunUp = false;
            hasApplied = false;
        }

        private static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }
    }
}