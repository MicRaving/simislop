/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.LotView;
using System;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The zoom ladder used by the 2D lot camera, extracted out of
    /// <c>UILotControl.SetTargetZoom</c> and <c>UILotControl.Update</c> so the thresholds have
    /// a regression test.
    ///
    /// There are only ever three camera states, and the continuous "TargetZoom" value is
    /// snapped down to one of them before it reaches the world:
    ///
    ///     target &lt; 0.5  -> WorldZoom.Far,    base scale 0.25
    ///     target &lt; 1    -> WorldZoom.Medium, base scale 0.5
    ///     otherwise       -> WorldZoom.Near,   base scale 1
    ///
    /// The snap is one-way (a fractional TargetZoom is a smoothed animation value; the
    /// WorldZoom enum and the backbuffer scale are the snapped result), so this is pure static
    /// math - no graphics, no content, no statics.
    ///
    /// The UI also speaks in whole zoom "levels" - 1 = near, 2 = medium, 3 = far - because that
    /// is how <c>TS1GameScreen.ZoomLevel</c> and the desktop zoom buttons are addressed (the
    /// buttons step a level at a time and grey themselves out at either end of the ladder).
    /// <see cref="DiscreteLevelFor"/> and <see cref="TargetZoomForLevel"/> translate between the
    /// two, so the level is always derived from the single zoom authority (TargetZoom) and never
    /// stored as a second, independently mutable copy.
    /// </summary>
    public static class LotZoomMapping
    {
        /// <summary>
        /// The canonical TargetZoom value for a camera zoom state. Inverse of
        /// <see cref="Resolve2D"/> at the three canonical zooms.
        /// </summary>
        public static float TargetZoomFor(WorldZoom zoom)
        {
            switch (zoom)
            {
                case WorldZoom.Near:
                    return 1f;
                case WorldZoom.Medium:
                    return 0.5f;
                case WorldZoom.Far:
                    return 0.25f;
                default:
                    return 1f;
            }
        }

        /// <summary>
        /// Snap a continuous TargetZoom down to the nearest camera zoom state below it, and
        /// report the base scale of that state.
        /// </summary>
        /// <param name="targetZoom">The continuous, possibly fractional zoom target.</param>
        /// <param name="zoom">The snapped <see cref="WorldZoom"/>.</param>
        /// <param name="baseScale">The backbuffer scale of the snapped zoom state.</param>
        public static void Resolve2D(float targetZoom, out WorldZoom zoom, out float baseScale)
        {
            if (targetZoom < 0.5f)
            {
                zoom = WorldZoom.Far;
                baseScale = 0.25f;
            }
            else if (targetZoom < 1f)
            {
                zoom = WorldZoom.Medium;
                baseScale = 0.5f;
            }
            else
            {
                zoom = WorldZoom.Near;
                baseScale = 1f;
            }
        }

        /// <summary>
        /// The backbuffer scale to apply for a continuous target zoom: the leftover fraction
        /// on top of the snapped state's base scale. At a canonical zoom this is always 1
        /// (nothing is oversampled), and it is what gives the in-between frames of the
        /// smoothed zoom animation their partial scale.
        /// </summary>
        public static float BackbufferScale(float targetZoom, float baseScale)
        {
            return targetZoom / baseScale;
        }

        /// <summary>
        /// The whole zoom level a continuous target zoom currently sits at: 1 = near,
        /// 2 = medium, 3 = far. This is the same snap <see cref="Resolve2D"/> applies, read out
        /// as the level numbering the UI uses, so a level can never disagree with the camera
        /// state the world is actually in.
        ///
        /// The result is always in 1..3 (a NaN target resolves to Near, as it does in
        /// <see cref="Resolve2D"/>) - a level is a real camera zoom, never a sentinel.
        /// </summary>
        public static int DiscreteLevelFor(float targetZoom)
        {
            Resolve2D(targetZoom, out WorldZoom zoom, out float _);

            switch (zoom)
            {
                case WorldZoom.Near:
                    return 1;
                case WorldZoom.Medium:
                    return 2;
                default:
                    return 3;
            }
        }

        /// <summary>
        /// The canonical TargetZoom for a whole zoom level - the inverse of
        /// <see cref="DiscreteLevelFor"/>. Levels outside 1..3 are clamped to the ends of the
        /// ladder, so a button that steps past the end of the range zooms to the end rather
        /// than off it.
        /// </summary>
        public static float TargetZoomForLevel(int level)
        {
            level = Math.Max(1, Math.Min(3, level));

            switch (level)
            {
                case 1:
                    return TargetZoomFor(WorldZoom.Near);
                case 2:
                    return TargetZoomFor(WorldZoom.Medium);
                default:
                    return TargetZoomFor(WorldZoom.Far);
            }
        }
    }
}
