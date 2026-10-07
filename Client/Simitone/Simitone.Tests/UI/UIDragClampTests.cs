/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client.UI.Framework;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="UIDragClamp"/> - the clamp <c>UIDialog.Update</c> drags against.
    ///
    /// <para>
    /// The finding (R-26) was that the two sides of the comparison were in different units and
    /// that the clamp was asymmetric. <c>GetMousePosition</c> inverts the parent's matrix, so the
    /// mouse position - and <c>UIDialog.X</c>, which lives in the same parent-relative space - is
    /// in the units of the parent. A top level dialog's parent is a <c>UIScreen</c>, and
    /// <c>UIScreen</c>'s constructor is <c>ScaleX = ScaleY = FSOEnvironment.DPIScaleFactor</c>, so
    /// those units are scaled. <c>GlobalSettings.Default.GraphicsWidth</c> is unscaled device
    /// pixels. At any ratio but 1 the bound was in the wrong place. And the low side was a bare
    /// <c>&gt; 0</c> where the high side allowed <c>m_DragTolerance</c> of overhang.
    /// </para>
    /// <para>
    /// What is testable is the rule: symmetric, one unit, applied to both axes.
    /// </para>
    /// </summary>
    public sealed class UIDragClampTests
    {
        private const float Tolerance = 20f;

        [Fact]
        public void TheToleranceIsAllowedOnBothSides()
        {
            // The asymmetry, in one test. The old low bound was `> 0`, so a dialog could not be
            // dragged so that 1px of it was off the left or top edge even though the high side
            // allowed 20px of overhang there.
            Assert.Equal(-Tolerance, UIDragClamp.Clamp(-1000f, 1920f, Tolerance));
            Assert.Equal(-Tolerance, UIDragClamp.Clamp(-1000f, 1080f, Tolerance));
            Assert.Equal(1920f + Tolerance, UIDragClamp.Clamp(99999f, 1920f, Tolerance));
            Assert.Equal(1080f + Tolerance, UIDragClamp.Clamp(99999f, 1080f, Tolerance));
        }

        [Fact]
        public void APositionInsideTheScreenIsNotTouched()
        {
            // A drag that is anywhere near the middle of the screen must land exactly where the
            // mouse is. This is the property the "sticky" old form also had, and the one that
            // would break first if the clamp were ever applied to the wrong value.
            for (var x = 0f; x <= 1920f; x += 137f)
            {
                Assert.Equal(x, UIDragClamp.Clamp(x, 1920f, Tolerance));
            }
        }

        [Fact]
        public void TheBoundIsTheFullExtentPlusTheTolerance()
        {
            // Exactly on the limit is inside, one ULP past it is not. The old form used a strict
            // `<` for the high side and a strict `>` for the low one, so the legal range was open
            // at both ends for no reason.
            Assert.Equal(1940f, UIDragClamp.Clamp(1940f, 1920f, Tolerance));
            Assert.Equal(1940f, UIDragClamp.Clamp(1941f, 1920f, Tolerance));
            Assert.Equal(-20f, UIDragClamp.Clamp(-20f, 1920f, Tolerance));
        }

        [Fact]
        public void TheClampIsIdempotentAndMonotonic()
        {
            // The dialog's X is whatever the last frame set, so applying the same clamp to the
            // same input twice has to be the same answer, and dragging further right can never
            // move it left.
            float previous = float.MinValue;
            for (var x = -500f; x <= 3000f; x += 7f)
            {
                var once = UIDragClamp.Clamp(x, 1920f, Tolerance);
                Assert.Equal(once, UIDragClamp.Clamp(once, 1920f, Tolerance));
                Assert.True(once >= previous, "the clamp must be monotonic in the drag position");
                previous = once;
            }
        }

        [Fact]
        public void AWindowDimensionIsConvertedIntoDialogUnits()
        {
            // This is the unit fix. 1920 device pixels is 1920 dialog units at a ratio of 1 and
            // 3840 at a ratio of 2, because the screen scales its contents by the ratio.
            Assert.Equal(1920f, UIDragClamp.Scaled(1920, 1f));
            Assert.Equal(3840f, UIDragClamp.Scaled(1920, 2f));
            Assert.Equal(5760f, UIDragClamp.Scaled(1920, 3f));
            Assert.Equal(960f, UIDragClamp.Scaled(1920, 0.5f));
        }

        [Fact]
        public void TheConvertedExtentIsWhatTheClampComparesAgainst()
        {
            // The two halves together, at the DPI ratio that broke it. At 2x the old code bounded
            // the drag at 1900 while the screen ran to 3840, so a dialog could never be dragged
            // into the right half of the window.
            var at2x = UIDragClamp.Scaled(1920, 2f);
            var at1x = UIDragClamp.Scaled(1920, 1f);

            Assert.Equal(3840f + Tolerance, at2x + Tolerance);
            Assert.Equal(1920f - Tolerance, at1x - Tolerance);

            // 3000 is past the 1x bound and well inside the 2x one.
            Assert.Equal(1920f + Tolerance, UIDragClamp.Clamp(3000f, at1x, Tolerance));
            Assert.Equal(3000f, UIDragClamp.Clamp(3000f, at2x, Tolerance));
            Assert.Equal(3840f + Tolerance, UIDragClamp.Clamp(99999f, at2x, Tolerance));
        }

        [Fact]
        public void ADegenerateScreenSizeStillProducesAFinitePosition()
        {
            // GraphicsWidth is 0 before the window is sized. The bound must not become an
            // overflowed int and the position must not become NaN, or the dialog jumps to a
            // corner it cannot be dragged back from.
            foreach (var pixels in new[] { 0, -1, -1920 })
            {
                foreach (var ratio in new[] { 0f, -1f, float.NaN, float.PositiveInfinity })
                {
                    var extent = UIDragClamp.Scaled(pixels, ratio);
                    var clamped = UIDragClamp.Clamp(12345f, extent, Tolerance);
                    Assert.True(float.IsFinite(clamped), "clamped position must be finite");
                }
            }
        }

        [Fact]
        public void ANotANumberDragPositionDoesNotEscapeTheClamp()
        {
            // A mouse position can be NaN if a matrix is degenerate. The result is still clamped,
            // so one bad frame cannot strand the dialog at a nonsense coordinate.
            Assert.Equal(-Tolerance, UIDragClamp.Clamp(float.NaN, 1920f, Tolerance));
        }

        [Fact]
        public void ANegativeToleranceIsTreatedAsNoTolerance()
        {
            // m_DragTolerance is a static int a subclass could set to something odd. A negative
            // one must not invert the range and pin the dialog to the far edge; it is read as
            // "no overhang allowed", so the range collapses to the screen itself.
            Assert.Equal(0f, UIDragClamp.Clamp(-500f, 1920f, -20f));
            Assert.Equal(1920f, UIDragClamp.Clamp(9999f, 1920f, -20f));
            Assert.Equal(500f, UIDragClamp.Clamp(500f, 1920f, -20f));
        }
    }
}
