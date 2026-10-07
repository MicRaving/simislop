using FSO.LotView;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Boundary tests for the 2D zoom ladder in <see cref="LotZoomMapping"/>.
    ///
    /// <c>UILotControl</c> keeps a continuous, fractional "TargetZoom" so zoom in/out can be
    /// animated, then snaps it down to one of the three real camera states every frame:
    ///
    ///     TargetZoom &lt; 0.5 -&gt; Far (0.25)   TargetZoom &lt; 1 -&gt; Medium (0.5)   else Near (1)
    ///
    /// The two threshold comparisons are exclusive, so 0.5 is Medium (not Far) and 1.0 is Near
    /// (not Medium). Those two comparisons are what the world actually sees, and both the
    /// <see cref="WorldZoom"/> handed to the world state and the backbuffer scale derive from
    /// them, so an off-by-one silently zooms the whole lot one notch out on every frame.
    ///
    /// At a canonical zoom the backbuffer scale must be exactly 1 - the world is not
    /// oversampled - and Resolve2D(TargetZoomFor(z)) must return z, or zoom in/out would drift
    /// and never settle.
    ///
    /// On top of the continuous target the UI addresses zoom in whole levels (1 = near,
    /// 2 = medium, 3 = far), because the desktop zoom buttons step a notch at a time and grey
    /// themselves out at either end. Those tests matter for the same reason: the level is
    /// derived from TargetZoom every frame, so a level that drifts while the wheel is
    /// animating would make the zoom buttons flicker, and a level that disagreed with the
    /// camera's own snap would grey out at the wrong zoom.
    ///
    /// LotZoomMapping is pure static float math: no graphics device, no content, no audio.
    /// </summary>
    public sealed class LotZoomMappingTests
    {
        // ---- Resolve2D: the exclusive thresholds ------------------------------------------

        [Theory]
        [InlineData(0f, WorldZoom.Far, 0.25f)]        // clamped low, still Far
        [InlineData(0.249f, WorldZoom.Far, 0.25f)]    // just under the Medium threshold
        [InlineData(0.25f, WorldZoom.Far, 0.25f)]     // exactly a canonical Far
        [InlineData(0.4999f, WorldZoom.Far, 0.25f)]
        [InlineData(0.5f, WorldZoom.Medium, 0.5f)]    // exclusive: 0.5 is Medium, NOT Far
        [InlineData(0.999f, WorldZoom.Medium, 0.5f)]  // just under the Near threshold
        [InlineData(1f, WorldZoom.Near, 1f)]          // exclusive: 1.0 is Near, NOT Medium
        [InlineData(2f, WorldZoom.Near, 1f)]          // zoom-in overshoot snaps down to Near
        public void Resolve2D_SnapsTargetZoomDownToTheNearestStateBelowIt(
            float targetZoom, WorldZoom expectedZoom, float expectedBaseScale)
        {
            LotZoomMapping.Resolve2D(targetZoom, out WorldZoom zoom, out float baseScale);

            Assert.Equal(expectedZoom, zoom);
            Assert.Equal(expectedBaseScale, baseScale, 6);
        }

        [Fact]
        public void Resolve2D_IsIdempotent_ForEveryTargetZoomFromZeroToFour()
        {
            // whatever state a target resolves to, feeding the resolved base scale back in as the
            // target must land on the same state - the animation can never oscillate or drift.
            for (float t = 0f; t <= 4f; t += 0.05f)
            {
                LotZoomMapping.Resolve2D(t, out WorldZoom resolved, out float baseScale);
                LotZoomMapping.Resolve2D(baseScale, out WorldZoom again, out float _);

                Assert.Equal(resolved, again);
                Assert.Equal(LotZoomMapping.TargetZoomFor(resolved), baseScale, 6);
            }
        }

        // ---- TargetZoomFor: the inverse mapping -------------------------------------------

        [Theory]
        [InlineData(WorldZoom.Near, 1f)]
        [InlineData(WorldZoom.Medium, 0.5f)]
        [InlineData(WorldZoom.Far, 0.25f)]
        public void TargetZoomFor_MapsEachCameraStateToItsCanonicalTarget(WorldZoom zoom, float expected)
        {
            Assert.Equal(expected, LotZoomMapping.TargetZoomFor(zoom), 6);
        }

        // ---- BackbufferScale ---------------------------------------------------------------

        [Theory]
        [InlineData(0.25f, 0.25f, 1f)]   // canonical Far: no oversampling
        [InlineData(0.5f, 0.5f, 1f)]     // canonical Medium: no oversampling
        [InlineData(1f, 1f, 1f)]         // canonical Near: no oversampling
        [InlineData(0.375f, 0.25f, 1.5f)]// mid-animation Far: 1.5x the Far backbuffer
        [InlineData(0.75f, 0.5f, 1.5f)]  // mid-animation Medium: 1.5x the Medium backbuffer
        [InlineData(2f, 1f, 2f)]         // clamped? no: extra Near scale is honoured verbatim
        public void BackbufferScale_IsTheTargetDividedByTheSnappedBaseScale(
            float targetZoom, float baseScale, float expected)
        {
            Assert.Equal(expected, LotZoomMapping.BackbufferScale(targetZoom, baseScale), 6);
        }

        // ---- The two halves have to agree --------------------------------------------------

        [Theory]
        [InlineData(WorldZoom.Near)]
        [InlineData(WorldZoom.Medium)]
        [InlineData(WorldZoom.Far)]
        public void Resolve2D_RoundTripsTargetZoomFor_AtTheThreeCanonicalZooms(WorldZoom zoom)
        {
            float target = LotZoomMapping.TargetZoomFor(zoom);

            LotZoomMapping.Resolve2D(target, out WorldZoom resolved, out float baseScale);

            Assert.Equal(zoom, resolved);
            // and the backbuffer scale it implies is a no-op, i.e. the zoom has settled
            Assert.Equal(1f, LotZoomMapping.BackbufferScale(target, baseScale), 6);
        }

        [Fact]
        public void TargetZoomFor_ProducesTargetsThatResolve2D_ReportsTheSameBaseScale()
        {
            // The world gets both values in the same frame. If they disagreed - say Near mapped
            // to 0.9 - the zoom would never settle: it would report 1x scale while the
            // animation value kept creeping.
            foreach (WorldZoom zoom in new[] { WorldZoom.Near, WorldZoom.Medium, WorldZoom.Far })
            {
                float target = LotZoomMapping.TargetZoomFor(zoom);
                LotZoomMapping.Resolve2D(target, out WorldZoom resolved, out float baseScale);

                Assert.Equal(zoom, resolved);
                Assert.Equal(target, baseScale, 6);
            }
        }

        // ---- The whole-level ladder the desktop zoom buttons walk -----------------------
        //
        // Zoom has one authority, the lot control's continuous TargetZoom, but the desktop
        // zoom buttons (and the + / - keys) address it in whole steps - "one notch nearer" -
        // and grey themselves out at the two ends of the ladder. DiscreteLevelFor /
        // TargetZoomForLevel are the translation between those two vocabularies, so the UI
        // never keeps its own copy of the zoom.

        [Theory]
        [InlineData(1, 1f)]        // near
        [InlineData(2, 0.5f)]      // medium
        [InlineData(3, 0.25f)]     // far
        [InlineData(0, 1f)]        // clamped to near
        [InlineData(-7, 1f)]       // clamped to near
        [InlineData(4, 0.25f)]     // clamped to far
        [InlineData(int.MaxValue, 0.25f)]
        public void TargetZoomForLevel_ClampsToTheThreeCameraZooms(int level, float expected)
        {
            // A button that steps past the end of the ladder must zoom to the end, not off it -
            // a level outside 1..3 has no camera zoom to name.
            Assert.Equal(expected, LotZoomMapping.TargetZoomForLevel(level), 6);
        }

        [Theory]
        [InlineData(0f, 3)]        // below the range still names a real zoom (far)
        [InlineData(0.25f, 3)]
        [InlineData(0.49f, 3)]     // exclusive: 0.5 is medium, NOT far
        [InlineData(0.5f, 2)]
        [InlineData(0.999f, 2)]    // exclusive: 1.0 is near, NOT medium
        [InlineData(1f, 1)]
        [InlineData(2f, 1)]        // the wheel overshoots near; still near
        [InlineData(float.NaN, 1)] // resolves to Near, exactly as Resolve2D does
        public void DiscreteLevelFor_ReportsTheLevelOfTheZoomTheWorldIsSnappedTo(
            float targetZoom, int expectedLevel)
        {
            int level = LotZoomMapping.DiscreteLevelFor(targetZoom);
            Assert.Equal(expectedLevel, level);

            // the level has to be the same snap the camera gets, or the desktop zoom buttons
            // would grey out while the lot was showing a different zoom.
            LotZoomMapping.Resolve2D(targetZoom, out WorldZoom zoom, out float _);
            LotZoomMapping.Resolve2D(LotZoomMapping.TargetZoomForLevel(level), out WorldZoom fromLevel, out float _);
            Assert.Equal(zoom, fromLevel);
        }

        [Theory]
        [InlineData(1, WorldZoom.Near)]
        [InlineData(2, WorldZoom.Medium)]
        [InlineData(3, WorldZoom.Far)]
        public void TargetZoomForLevel_AgreesWithTheCameraStateItResolves(int level, WorldZoom expected)
        {
            // TS1GameScreen.ZoomLevel used to name the camera state with the arithmetic
            // (WorldZoom)(4 - value). The level -> WorldZoom pairing is what keeps 1 = near and
            // 3 = far, and it is now derived, so pin it.
            LotZoomMapping.Resolve2D(LotZoomMapping.TargetZoomForLevel(level), out WorldZoom zoom, out float _);

            Assert.Equal(expected, zoom);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public void LevelLadder_RoundTripsInBothDirections(int level)
        {
            float target = LotZoomMapping.TargetZoomForLevel(level);

            Assert.Equal(level, LotZoomMapping.DiscreteLevelFor(target));
            // and the round trip is a fixed point: reading the level back and asking for it
            // again must not drift the zoom one notch.
            Assert.Equal(target, LotZoomMapping.TargetZoomForLevel(LotZoomMapping.DiscreteLevelFor(target)), 6);
        }

        [Fact]
        public void DiscreteLevelFor_IsStable_ForEveryTargetZoomFromQuarterToTwo()
        {
            // A wheel or pinch zoom leaves TargetZoom fractional, and TS1GameScreen.ZoomLevel
            // is read every frame to decide which zoom buttons to grey out. If deriving a level
            // from a fractional target and then rendering that level back moved the zoom, the
            // buttons would flicker between two states mid-animation.
            for (float t = 0.25f; t <= 2f; t += 0.01f)
            {
                int level = LotZoomMapping.DiscreteLevelFor(t);
                float canonical = LotZoomMapping.TargetZoomForLevel(level);

                Assert.InRange(level, 1, 3);
                Assert.Equal(level, LotZoomMapping.DiscreteLevelFor(canonical));
                // the canonical target is a resting zoom: the world gets it with no oversampling
                Assert.Equal(1f, LotZoomMapping.BackbufferScale(canonical, canonical), 6);
            }
        }
    }
}
