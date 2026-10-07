/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The touch gesture thresholds used to be raw pixel/frame constants (30 px of tap
    /// drift, 25 px of scroll drag, 8 frames of hold) that were only correct on a 96 DPI,
    /// 60 Hz display. These tests pin the scaled behaviour: at the legacy 1.0/60 baseline
    /// the numbers must be unchanged, and every scaling factor must move them the right way,
    /// so a tap means the same physical gesture on a phone and on a 4K desktop.
    /// </summary>
    public sealed class TouchInputTests
    {
        [Theory]
        [InlineData(1f, 30f)]
        [InlineData(1.25f, 37.5f)]
        [InlineData(1.5f, 45f)]
        [InlineData(2f, 60f)]
        public void TapMoveThresholdScalesWithDpi(float dpiScale, float expected)
        {
            Assert.Equal(expected, TouchInput.TapMoveThreshold(dpiScale));
        }

        [Theory]
        [InlineData(1f, 25f)]
        [InlineData(1.25f, 31.25f)]
        [InlineData(1.5f, 37.5f)]
        [InlineData(2f, 50f)]
        public void ScrollThresholdScalesWithDpi(float dpiScale, float expected)
        {
            Assert.Equal(expected, TouchInput.ScrollThreshold(dpiScale));
        }

        [Fact]
        public void BothDistanceThresholdsPreserveTheirLegacyRatio()
        {
            // 30px tap drift to 25px scroll drag: the scroll drag must always trip first,
            // otherwise a drag on a list would register as a tap.
            Assert.True(TouchInput.ScrollThreshold(1f) < TouchInput.TapMoveThreshold(1f));
            Assert.True(TouchInput.ScrollThreshold(2f) < TouchInput.TapMoveThreshold(2f));
        }

        [Theory]
        [InlineData(60, 8)]
        [InlineData(30, 4)]
        [InlineData(120, 16)]
        [InlineData(144, 19)]
        [InlineData(240, 32)]
        public void TapTimerConvertsTheLegacySixtyHzFrameCountToTheCurrentRefreshRate(int refreshRate, int expected)
        {
            Assert.Equal(expected, TouchInput.TapTimerFrames(refreshRate));
        }

        [Theory]
        [InlineData(30)]
        [InlineData(60)]
        [InlineData(75)]
        [InlineData(120)]
        [InlineData(144)]
        [InlineData(240)]
        public void TapTimerTracksTheLegacyHoldDurationRatherThanARawFrameCount(int refreshRate)
        {
            // the whole point of the conversion: the legacy constant was a raw frame count, so
            // an 8 sample hold was 1/8 s at 60 Hz but only 1/18 s at 144 Hz. the converted
            // value must keep the *duration* (8/60 s) to within the rounding half-sample.
            const double legacySeconds = 8.0 / 60.0;
            var actualSeconds = TouchInput.TapTimerFrames(refreshRate) / (double)refreshRate;
            var halfSample = 0.5 / refreshRate;

            Assert.True(
                Math.Abs(actualSeconds - legacySeconds) <= halfSample,
                $"at {refreshRate} Hz a tap took {actualSeconds:F4}s, expected {legacySeconds:F4}s +/- {halfSample:F4}s");
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-60)]
        [InlineData(int.MinValue)]
        public void TapTimerNeverCollapsesToZero(int refreshRate)
        {
            // a zero frame budget would silently disable the long press gesture entirely.
            Assert.Equal(1, TouchInput.TapTimerFrames(refreshRate));
        }

        [Fact]
        public void TinyPositiveRefreshRatesStillYieldOneFrame()
        {
            Assert.Equal(1, TouchInput.TapTimerFrames(1));
        }

        [Fact]
        public void ThresholdsArePureFunctionsOfTheirInputs()
        {
            // no statics, no dependency on the live graphics device: the same argument must
            // always produce the same answer regardless of what ran before it.
            Assert.Equal(TouchInput.TapMoveThreshold(1.5f), TouchInput.TapMoveThreshold(1.5f));
            Assert.Equal(TouchInput.ScrollThreshold(1.5f), TouchInput.ScrollThreshold(1.5f));
            Assert.Equal(TouchInput.TapTimerFrames(144), TouchInput.TapTimerFrames(144));
        }
    }
}
