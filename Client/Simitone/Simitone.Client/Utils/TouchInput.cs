/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// DPI- and refresh-rate-aware touch gesture thresholds, shared by the touch helpers
    /// (<c>UILotControlTouchHelper</c>, <c>UITouchScroll</c>) and <c>UIArchTouchHelper</c>.
    ///
    /// The gesture logic used to carry hard-coded pixel/frame constants that were only ever
    /// correct on a 96 DPI, 60 Hz display:
    ///
    ///     TAP_TIMER       = 8   frames (~1/8 s of a hold at 60 Hz)
    ///     TAP_POINT_DIST  = 30  px of finger travel before a tap becomes a scroll
    ///     scroll start    = 25  px of travel before a drag becomes a scroll
    ///
    /// On a high-DPI screen the client is scaled up by <c>FSOEnvironment.DPIScaleFactor</c>,
    /// so a finger travelling 30 logical pixels covers twice as many device pixels: the same
    /// gesture was read as "scrolled" far more eagerly, and taps became unreliable. On a
    /// high-refresh display the frame count is also a duration, not a time, so an 8 frame hold
    /// shrank to a third of its intended length at 144 Hz.
    ///
    /// These helpers keep the legacy 96 DPI / 60 Hz behaviour as the 1.0 baseline and scale it
    /// by whatever the current environment reports. Pure static math — no graphics device, no
    /// content, no statics — so it is fully covered by headless tests.
    /// </summary>
    public static class TouchInput
    {
        /// <summary>
        /// Base finger travel, in logical (pre-DPI-scale) pixels, that a press may drift
        /// before it stops being a tap and is reclassified as a scroll.
        /// </summary>
        private const float BaseTapMoveThreshold = 30f;

        /// <summary>
        /// Base finger travel, in logical (pre-DPI-scale) pixels, that promotes a drag on a
        /// scrolling list into a scroll rather than a click.
        /// </summary>
        private const float BaseScrollThreshold = 25f;

        /// <summary>
        /// Base number of update samples a press may be held before it registers as a
        /// long press. Legacy value was 8 frames measured at 60 Hz.
        /// </summary>
        private const double BaseTapTimerFrames = 8.0;

        /// <summary>
        /// The display refresh rate the <see cref="BaseTapTimerFrames"/> constant was
        /// originally authored against.
        /// </summary>
        private const double BaseRefreshRate = 60.0;

        /// <summary>
        /// How far a finger may travel, in device pixels, before a press stops counting as a
        /// tap. Scales the legacy 30 logical pixel allowance by the current DPI scale factor.
        /// </summary>
        /// <param name="dpiScale">The UI/DPI scale factor, e.g. <c>FSOEnvironment.DPIScaleFactor</c>.</param>
        public static float TapMoveThreshold(float dpiScale)
        {
            return BaseTapMoveThreshold * dpiScale;
        }

        /// <summary>
        /// How far a finger may travel, in device pixels, before a drag on a list is treated as
        /// a scroll instead of a click. Scales the legacy 25 logical pixel allowance by the
        /// current DPI scale factor.
        /// </summary>
        /// <param name="dpiScale">The UI/DPI scale factor, e.g. <c>FSOEnvironment.DPIScaleFactor</c>.</param>
        public static float ScrollThreshold(float dpiScale)
        {
            return BaseScrollThreshold * dpiScale;
        }

        /// <summary>
        /// The number of update samples a press may be held before it registers as a long
        /// press. The legacy constant was a raw frame count, which silently changed the gesture
        /// duration on faster displays; this converts the original 8-frames-at-60 Hz duration
        /// into a sample count for the current refresh rate.
        ///
        /// Rounded to the nearest sample, and never less than 1 so a nonsensical (zero or
        /// negative) refresh rate cannot disable the long press entirely.
        /// </summary>
        /// <param name="refreshRate">The display refresh rate, e.g. <c>FSOEnvironment.RefreshRate</c>.</param>
        public static int TapTimerFrames(int refreshRate)
        {
            return Math.Max(1, (int)Math.Round(BaseTapTimerFrames * refreshRate / BaseRefreshRate));
        }
    }
}
