using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for <see cref="PictureInPictureFraming"/>, the pure half of
    /// <see cref="UIPictureInPicture"/>.
    ///
    /// <para>
    /// The picture-in-picture window draws a live crop of the lot view. Three things about that
    /// are easy to get subtly wrong and impossible to notice without a screenshot:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <description>
    /// The crop has to keep the source's aspect ratio, or the lot view is squashed into the
    /// window. It also has to stay inside the source, or the sprite batch reads past the edge
    /// of the texture.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// The focus point arrives in logical viewport coordinates but the texture being cropped is
    /// <c>sourceScale x viewport</c>. With 2x SSAA selected, forgetting to convert puts the window
    /// two thirds of the way off the lot - which reads as "the PIP is not following the sim"
    /// rather than as an arithmetic slip. And the scale is not only the supersample any more: the
    /// window crops a small non-multisampled copy of the lot, so the copy's own downscale is a
    /// second factor, and leaving it out silently changes the window's zoom.
    /// </description>
    /// </item>
    /// <item>
    /// <description>
    /// Which of the two textures to crop - the copy, the backbuffer, or neither - is settled
    /// before the crop is asked for, and the order is the difference between a live view at
    /// every anti-aliasing setting and one that only works with AA off.
    /// </description>
    /// </item>
    /// </list>
    ///
    /// <para>
    /// The visibility machine is here for the same reason. A hold timer that restarts on every
    /// notification can be pinned open forever by an event that fires every few frames, and one
    /// that restarts on none closes the window before the player has read the caption.
    /// </para>
    ///
    /// <para>
    /// Everything under test here is integer and float arithmetic over its arguments - no
    /// graphics device, no content, no VM, no statics. The sim being spotlit is a
    /// <c>VMEntity</c>, which the tests pass as null: "no named subject" is a real case (the
    /// window follows the player's selection), and it keeps these tests from needing a VM.
    /// </para>
    /// </remarks>
    public sealed class PictureInPictureFramingTests
    {
        // ---- frame placement ---------------------------------------------------------------

        [Fact]
        public void TotalSize_AddsTheBorderTwiceAndTheCaptionStripOnce()
        {
            var withCaption = PictureInPictureFraming.TotalSize(true);
            var without = PictureInPictureFraming.TotalSize(false);

            Assert.Equal(PictureInPictureFraming.ViewWidth + 2 * PictureInPictureFraming.Border, withCaption.X);
            Assert.Equal(
                PictureInPictureFraming.TitleHeight + PictureInPictureFraming.ViewHeight
                    + PictureInPictureFraming.CaptionHeight + 2 * PictureInPictureFraming.Border,
                withCaption.Y);
            // the width does not depend on whether there is a caption
            Assert.Equal(withCaption.X, without.X);
            Assert.Equal(withCaption.Y - PictureInPictureFraming.CaptionHeight, without.Y);
        }

        [Theory]
        [InlineData(PipCorner.TopLeft, 15f, 15f)]
        [InlineData(PipCorner.TopRight, 1053f, 15f)]
        [InlineData(PipCorner.BottomLeft, 15f, 815f)]
        [InlineData(PipCorner.BottomRight, 1053f, 815f)]
        public void Place_SitsTheRequestedMarginOffTheChosenCorner(PipCorner corner, float x, float y)
        {
            // 1280x1024, and the window is 212x194 (a caption strip and a title strip around a
            // 208x156 picture), so a right-hand corner is 1280-212-15 across and a bottom one
            // is 1024-194-15 down.
            var size = PictureInPictureFraming.TotalSize(true);

            var pos = PictureInPictureFraming.Place(1280, 1024, size, corner, PictureInPictureFraming.ScreenMargin);

            Assert.Equal(x, pos.X);
            Assert.Equal(y, pos.Y);
        }

        [Fact]
        public void Place_PullsTheWindowBackOnScreenAfterAShrink()
        {
            // 100x50 screen, window is 212 wide: the right-hand corner is off screen, so the
            // window has to move rather than hang off the right edge.
            var size = PictureInPictureFraming.TotalSize(true);

            var pos = PictureInPictureFraming.Place(100, 50, size, PipCorner.BottomRight, PictureInPictureFraming.ScreenMargin);

            Assert.Equal(0f, pos.X);
            Assert.Equal(0f, pos.Y);
        }

        [Fact]
        public void Place_KeepsTheWindowInsideAScreenItBarelyFits()
        {
            // big enough to fit, but not with the margin: the margin is given up, the window
            // is not pushed off the edge.
            var size = PictureInPictureFraming.TotalSize(true);
            var screenW = (int)size.X + 10;
            var screenH = (int)size.Y + 10;

            var pos = PictureInPictureFraming.Place(screenW, screenH, size, PipCorner.BottomRight, PictureInPictureFraming.ScreenMargin);

            Assert.Equal(0f, pos.X);
            Assert.Equal(0f, pos.Y);
        }

        // ---- reading the lot backbuffer as a texture ---------------------------------------

        [Theory]
        [InlineData(true, 0, true)]     // anti-aliasing off, or 2x SSAA (MSAA 0)
        [InlineData(true, 4, false)]    // 4x MSAA - a multisampled target is not a texture
        [InlineData(true, 8, false)]
        [InlineData(false, 0, false)]   // no world yet
        public void CanSample_RejectsAMultisampledOrMissingBackbuffer(bool exists, int samples, bool expected)
        {
            Assert.Equal(expected, PictureInPictureFraming.CanSample(exists, samples));
        }

        // ---- choosing the source ------------------------------------------------------------

        [Fact]
        public void ResolveLotSource_PrefersTheSnapshotEvenWhenTheBackbufferWouldAlsoWork()
        {
            // The point of the copy: a plain texture whatever the player's anti-aliasing is.
            // Reading the backbuffer directly is cheaper, but it is the path that used to need AA
            // off, and a window that preferred it whenever it was legal would be one graphics
            // setting away from behaving differently.
            Assert.Equal(PipLotSource.Snapshot,
                PictureInPictureFraming.ResolveLotSource(snapshotUsable: true, backbufferSampleable: true));
        }

        [Fact]
        public void ResolveLotSource_FallsBackToTheBackbufferWhenTheCopyCouldNotBeMade()
        {
            // AA off, or a 3D camera's supersampled mode: no copy, but the backbuffer is a plain
            // texture, which is exactly what this window used as its only source.
            Assert.Equal(PipLotSource.Backbuffer,
                PictureInPictureFraming.ResolveLotSource(snapshotUsable: false, backbufferSampleable: true));
        }

        [Fact]
        public void ResolveLotSource_IsNoneWhenNeitherIsReadable()
        {
            // 4x or 8x MSAA and no copy. This is the state the window used to call "the live view
            // needs AA off"; it is now a state it should only be in if the copy failed.
            Assert.Equal(PipLotSource.None,
                PictureInPictureFraming.ResolveLotSource(snapshotUsable: false, backbufferSampleable: false));
        }

        // ---- from the viewport into the source's pixels ---------------------------------------

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        public void LotSourceScale_IsTheSupersampleFactorWhenTheSourceIsTheBackbuffer(int ssaa)
        {
            // The backbuffer is ssaa x the viewport (InitScreenTargets scales it) and the crop
            // source is the backbuffer itself, so the downscale factor cancels and the supersample
            // is all that is left.
            Assert.Equal(ssaa, PictureInPictureFraming.LotSourceScale(1920, 1080, 1920, 1080, ssaa));
        }

        [Fact]
        public void LotSourceScale_FoldsInTheSnapshotsOwnDownscale()
        {
            // The snapshot is a proportional copy of the backbuffer, so it has fewer source pixels
            // per pixel of lot. Without this the focus point lands in the wrong part of the lot -
            // and because the crop is a magnification, "the wrong part" is not a nudge, it is a
            // different building.
            var scale = PictureInPictureFraming.LotSourceScale(1920, 1080, 416, 234, 1);

            Assert.Equal(416 / 1920f, scale, 5);
        }

        [Fact]
        public void LotSourceScale_ComposesTheSupersampleAndTheSnapshotDownscale()
        {
            // 2x SSAA in a 3D camera: the backbuffer is twice the viewport and the snapshot is a
            // further copy of that. Both factors are real and they multiply.
            var scale = PictureInPictureFraming.LotSourceScale(3840, 2160, 416, 234, 2);

            Assert.Equal(2 * (416 / 3840f), scale, 5);
        }

        [Theory]
        [InlineData(0, 0, 0, 0)]                 // no backbuffer to compare against
        [InlineData(1920, 1080, 0, 0)]           // no source
        [InlineData(1920, 1080, -4, -1)]         // nonsense sizes
        public void LotSourceScale_DegradesToTheSupersampleRatherThanToZero(
            int backbufferWidth, int backbufferHeight, int sourceWidth, int sourceHeight)
        {
            // A factor of zero would mean a zero-area crop; the viewport-sized one is the honest
            // "not scaled" answer, and it is the one a degenerate source can still draw.
            Assert.Equal(2, PictureInPictureFraming.LotSourceScale(
                backbufferWidth, backbufferHeight, sourceWidth, sourceHeight, 2));
        }

        [Fact]
        public void LotSourceScale_IgnoresANonPositiveSupersampleFactor()
        {
            // PPXDepthEngine.SSAA is 1 or 2, but a zero read as a factor of zero would make every
            // crop empty rather than merely unscaled.
            Assert.Equal(1, PictureInPictureFraming.LotSourceScale(1920, 1080, 1920, 1080, 0));
            Assert.Equal(1, PictureInPictureFraming.LotSourceScale(1920, 1080, 1920, 1080, -3));
        }

        [Fact]
        public void CroppingASnapshotShowsTheSameMagnificationAsCroppingTheBackbuffer()
        {
            // The load-bearing consequence of LotSourceScale. A 416-wide snapshot of a 1920-wide
            // backbuffer holds 0.2167 of its pixels, so the crop out of it has to be 0.2167 of the
            // size - stretched back over the same 208-unit window, the player sees exactly what
            // they would have seen from the full-resolution source. If this drifts, the window's
            // zoom depends on its own internal resolution, which nobody would notice until they
            // compared two magnifications.
            var backbuffer = CropFraction(1920, 1080, 1f, 2f, 960f, 540f);
            var snapshot = CropFraction(416, 234,
                PictureInPictureFraming.LotSourceScale(1920, 1080, 416, 234, 1), 2f, 960f, 540f);

            // The same fraction of the lot in each axis, to within a texel of the smaller source:
            // a texel in a 416-wide crop is 0.24% of it, and integer rounding is all that is left
            // once the scale is right.
            AssertSameFraction(backbuffer.Width, snapshot.Width);
            AssertSameFraction(backbuffer.Height, snapshot.Height);

            // And the focus point sits in the same place inside the crop, which is the whole
            // reason it is scaled at all: a sim at the centre of the view has to be at the centre
            // of the window whichever texture it was cropped from.
            Assert.Equal(0.5f, snapshot.FocusX, 2);
            Assert.Equal(0.5f, snapshot.FocusY, 2);
            Assert.Equal(backbuffer.FocusX, snapshot.FocusX, 2);
            Assert.Equal(backbuffer.FocusY, snapshot.FocusY, 2);
        }

        /// <summary>
        /// Asserts two readings agree to within a texel of the smaller texture. Anything looser
        /// would not notice the scale being wrong by a rounding step, and anything tighter would
        /// fail on the rounding itself.
        /// </summary>
        private static void AssertSameFraction(float fromBigSource, float fromSmallSource)
        {
            var tolerance = System.Math.Max(fromBigSource, fromSmallSource) * 0.01f;
            Assert.True(System.Math.Abs(fromBigSource - fromSmallSource) <= tolerance,
                fromSmallSource + " is not within a texel of " + fromBigSource);
        }

        private struct CropReading
        {
            public float Width;
            public float Height;
            public float FocusX;
            public float FocusY;
        }

        /// <summary>
        /// A crop read as the things a player would notice about it: how much of the lot it shows
        /// (as a fraction of the source, so two sources of different sizes are comparable) and
        /// where the focus point ended up inside it.
        /// </summary>
        private static CropReading CropFraction(int sourceWidth, int sourceHeight,
            float sourceScale, float magnification, float focusX, float focusY)
        {
            var crop = PictureInPictureFraming.CropRect(
                sourceWidth, sourceHeight,
                PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                focusX, focusY, sourceScale, magnification);

            return new CropReading
            {
                Width = crop.Width / (float)sourceWidth,
                Height = crop.Height / (float)sourceHeight,
                FocusX = (focusX * sourceScale - crop.X) / crop.Width,
                FocusY = (focusY * sourceScale - crop.Y) / crop.Height
            };
        }

        [Fact]
        public void CroppingASnapshotFollowsTheSimTheSameWayTheFullLotDoes()
        {
            // A sim in the top-left quarter, cropped from a quarter-scale snapshot. The crop out of
            // a quarter-scale source is a quarter of the area in each axis, and - the part that
            // matters - its top-left corner is in the same place relative to the lot, because that
            // is what says whether the window is following the sim.
            var fromBackbuffer = PictureInPictureFraming.CropRect(
                1920, 1080, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                240f, 135f, 1f, 3f);
            var fromSnapshot = PictureInPictureFraming.CropRect(
                480, 270, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                240f, 135f, 0.25f, 3f);

            Assert.Equal(fromBackbuffer.Width / 4, fromSnapshot.Width);
            Assert.Equal(fromBackbuffer.Height / 4, fromSnapshot.Height);
            // Both are slid in against the top-left corner by the clamp, and the focus point is
            // off to one side, so the corners agree at 0 - the assertion is that neither one grew
            // a clamp error, not that the sim is in the middle.
            Assert.Equal(fromBackbuffer.X / 4, fromSnapshot.X);
            Assert.Equal(fromBackbuffer.Y / 4, fromSnapshot.Y);
        }

        [Fact]
        public void CroppingASnapshotClampsAtItsOwnEdgeRatherThanTheLots()
        {
            // The same two crops again, with the sim hard against the corner so the clamp is what
            // decides the origin. The snapshot is a quarter of the lot, so its clamp lands a
            // quarter of the way along - a copy that clamped against the lot's size would be
            // sampling outside its own texture.
            var fromBackbuffer = PictureInPictureFraming.CropRect(
                1920, 1080, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                0f, 0f, 1f, 3f);
            var fromSnapshot = PictureInPictureFraming.CropRect(
                480, 270, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                0f, 0f, 0.25f, 3f);

            Assert.Equal(0, fromBackbuffer.X);
            Assert.Equal(0, fromSnapshot.X);
            Assert.True(fromSnapshot.Right <= 480, fromSnapshot + " reaches outside the snapshot");
            Assert.True(fromSnapshot.Bottom <= 270, fromSnapshot + " reaches outside the snapshot");
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-2f)]
        [InlineData(float.NaN)]
        public void CropRect_TreatsANonPositiveSourceScaleAsNotScaled(float scale)
        {
            // The supersample factor used to be the only thing in this slot, and a zero there
            // meant "not supersampled". A snapshot scale can legitimately be below 1, so the floor
            // is gone - but zero and NaN are not scales and have to land on the same answer.
            var asked = PictureInPictureFraming.CropRect(
                800, 600, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                400f, 300f, scale, 1f);
            var one = PictureInPictureFraming.CropRect(
                800, 600, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                400f, 300f, 1f, 1f);

            Assert.Equal(one, asked);
        }

        // ---- the crop ---------------------------------------------------------------------

        [Fact]
        public void CropRect_IsCentredOnTheFocusAndKeepsTheSourceAspect()
        {
            // 1920x1080 backbuffer, focus dead middle at (960, 540) in logical coordinates
            // with no supersampling.
            var crop = PictureInPictureFraming.CropRect(
                1920, 1080,
                PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                960f, 540f, 1, 1f);

            // 208x156 is 4:3 against a 16:9 source, so the crop widens to 16:9 before it is
            // placed. Magnification 1 means the height stays at 156.
            Assert.Equal(156, crop.Height);
            Assert.Equal(277, crop.Width);           // 156 * 16/9 = 277.33
            Assert.Equal(821, crop.X);                // 960 - 277.33/2 = 821.33
            Assert.Equal(462, crop.Y);                // 540 - 156/2
        }

        [Fact]
        public void CropRect_ScalesTheFocusByTheSupersampleFactor()
        {
            // 2x SSAA: the backbuffer is 3840x2160 but GetScreenPos still reports logical
            // viewport coordinates. The crop has to follow the point into texture space or the
            // window ends up in the wrong part of the lot.
            var crop = PictureInPictureFraming.CropRect(
                3840, 2160,
                PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                960f, 540f, 2, 1f);

            // 2x the sample area at the same magnification, and centred on 1920, 1080.
            Assert.Equal(312, crop.Height);
            Assert.Equal(555, crop.Width);
            Assert.Equal(1920 - 555 / 2, crop.X);
            Assert.Equal(1080 - 156, crop.Y);
        }

        [Fact]
        public void CropRect_NeverReachesOutsideTheBackbuffer()
        {
            // a sim hard against the top-left corner: the crop slides in rather than sampling
            // negative texture coordinates.
            var crop = PictureInPictureFraming.CropRect(
                800, 600,
                PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                0f, 0f, 1, 1f);

            Assert.Equal(0, crop.X);
            Assert.Equal(0, crop.Y);
            Assert.True(crop.Right <= 800);
            Assert.True(crop.Bottom <= 600);
        }

        [Fact]
        public void CropRect_SlidesInAtTheFarEdgesToo()
        {
            var crop = PictureInPictureFraming.CropRect(
                800, 600,
                PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                800f, 600f, 1, 1f);

            Assert.Equal(800 - crop.Width, crop.X);
            Assert.Equal(600 - crop.Height, crop.Y);
        }

        [Fact]
        public void CropRect_StaysInsideEvenWhenTheMagnificationIsAbsurd()
        {
            // magnification 500 would ask for a crop 500x the window, which is bigger than the
            // whole backbuffer. It has to come back as the largest legal crop, not as an
            // out-of-range rectangle.
            var crop = PictureInPictureFraming.CropRect(
                800, 600,
                PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                400f, 300f, 1, 500f);

            Assert.Equal(800, crop.Width);
            Assert.Equal(600, crop.Height);
            Assert.Equal(0, crop.X);
            Assert.Equal(0, crop.Y);
        }

        [Theory]
        [InlineData(1f, 1f)]
        [InlineData(2.5f, 2.5f)]
        [InlineData(0f, PictureInPictureFraming.MinMagnification)]
        [InlineData(-4f, PictureInPictureFraming.MinMagnification)]
        [InlineData(0.01f, PictureInPictureFraming.MinMagnification)]
        [InlineData(99f, PictureInPictureFraming.MaxMagnification)]
        public void ClampMagnification_KeepsTheRequestInsideTheBand(float asked, float expected)
        {
            Assert.Equal(expected, PictureInPictureFraming.ClampMagnification(asked));
        }

        [Fact]
        public void CropRect_TreatsANaNMagnificationAsTheDefault()
        {
            var asked = PictureInPictureFraming.CropRect(
                800, 600, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                400f, 300f, 1, float.NaN);
            var one = PictureInPictureFraming.CropRect(
                800, 600, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                400f, 300f, 1, 1f);

            Assert.Equal(one, asked);
        }

        [Fact]
        public void CropRect_ToleratesASsaaOfZero()
        {
            // Math.Max(1, ssaa) means a zero (or a negative) supersample factor degenerates to
            // "not supersampled" rather than producing a zero-area crop.
            var zero = PictureInPictureFraming.CropRect(
                800, 600, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                400f, 300f, 0, 1f);
            var one = PictureInPictureFraming.CropRect(
                800, 600, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                400f, 300f, 1, 1f);

            Assert.Equal(one, zero);
        }

        [Theory]
        [InlineData(0, 600, 208, 156)]
        [InlineData(800, 0, 208, 156)]
        [InlineData(800, 600, 0, 156)]
        [InlineData(800, 600, 208, 0)]
        public void CropRect_IsEmptyRatherThanThrowingOnADegenerateSize(int w, int h, int vw, int vh)
        {
            Assert.Equal(Rectangle.Empty,
                PictureInPictureFraming.CropRect(w, h, vw, vh, 10f, 10f, 1, 1f));
        }

        [Fact]
        public void CropRect_SurvivesANaNFocusPoint()
        {
            // a projection that did not come out finite lands here rather than in an
            // ArgumentException inside the draw.
            var crop = PictureInPictureFraming.CropRect(
                800, 600, PictureInPictureFraming.ViewWidth, PictureInPictureFraming.ViewHeight,
                float.NaN, float.NaN, 1, 1f);

            Assert.Equal(0, crop.X);
            Assert.Equal(0, crop.Y);
            Assert.True(crop.Width > 0);
        }

        // ---- the headline feed ------------------------------------------------------------

        [Fact]
        public void FitRectangle_ShrinksAndCentresAWideTexture()
        {
            // a 160x49 skill readout inside a 208x156 window: full width, letterboxed
            // vertically, and centred on the leftover height.
            var fit = PictureInPictureFraming.FitRectangle(160, 49, 208, 156);

            Assert.Equal(208, fit.Width);
            Assert.Equal(64, fit.Height);        // 49 * 208/160 = 63.7
            Assert.Equal(0, fit.X);
            Assert.Equal((156 - 64) / 2, fit.Y);
        }

        [Fact]
        public void FitRectangle_ShrinksAndCentresATallTexture()
        {
            var fit = PictureInPictureFraming.FitRectangle(100, 400, 208, 156);

            Assert.Equal(39, fit.Width);         // 100 * 156/400 = 39
            Assert.Equal(156, fit.Height);
            Assert.Equal((208 - 39) / 2, fit.X);
            Assert.Equal(0, fit.Y);
        }

        [Fact]
        public void FitRectangle_ScalesASmallHeadlineUpToTheWidthOfTheWindow()
        {
            // the balloon sprites come in small, and a 20x10 sprite drawn at native size in a
            // 208x156 window is a speck. Scaling it up to the window keeps the aspect and fills
            // the picture area, which is the point of a fallback feed.
            var fit = PictureInPictureFraming.FitRectangle(20, 10, 208, 156);

            Assert.Equal(208, fit.Width);
            Assert.Equal(104, fit.Height);       // 10 * 208/20
            Assert.Equal(0, fit.X);
            Assert.Equal((156 - 104) / 2, fit.Y);
        }

        [Fact]
        public void FitRectangle_IsEmptyForADegenerateTexture()
        {
            Assert.Equal(Rectangle.Empty, PictureInPictureFraming.FitRectangle(0, 40, 208, 156));
            Assert.Equal(Rectangle.Empty, PictureInPictureFraming.FitRectangle(40, 40, 0, 156));
        }

        // ---- which feed wins --------------------------------------------------------------

        [Fact]
        public void ResolveFeed_PrefersTheLiveLotView()
        {
            Assert.Equal(PipFeed.LotView, PictureInPictureFraming.ResolveFeed(PipFeed.Auto, true, true));
            Assert.Equal(PipFeed.LotView, PictureInPictureFraming.ResolveFeed(PipFeed.Headline, true, true));
        }

        [Fact]
        public void ResolveFeed_FallsBackToTheHeadlineWhenTheBackbufferCannotBeRead()
        {
            // 4x MSAA: the backbuffer is multisampled, so the lot feed is not available and
            // the window shows the headline rather than going blank.
            Assert.Equal(PipFeed.Headline, PictureInPictureFraming.ResolveFeed(PipFeed.Auto, false, true));
            Assert.Equal(PipFeed.Headline, PictureInPictureFraming.ResolveFeed(PipFeed.LotView, false, true));
        }

        [Fact]
        public void ResolveFeed_IsAutoWhenThereIsNothingToShow()
        {
            // Auto is the "no signal" sentinel: the panel reads it as a flat panel and a NO
            // SIGNAL title rather than as a feed it should try to draw.
            Assert.Equal(PipFeed.Auto, PictureInPictureFraming.ResolveFeed(PipFeed.Auto, false, false));
            Assert.Equal(PipFeed.Auto, PictureInPictureFraming.ResolveFeed(PipFeed.LotView, false, false));
        }

        // ---- captions ---------------------------------------------------------------------

        [Fact]
        public void NormalizeCaption_TrimsAndCollapsesWhitespace()
        {
            Assert.Equal("Bella is cooking",
                PictureInPictureFraming.NormalizeCaption("  Bella\n\tis   cooking  "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\r\n\t")]
        public void NormalizeCaption_IsNullWhenThereIsNothingLeft(string raw)
        {
            // null is what makes TotalSize drop the caption strip, so a whitespace-only caption
            // must not leave an empty bar under the picture.
            Assert.Null(PictureInPictureFraming.NormalizeCaption(raw));
        }

        [Fact]
        public void NormalizeCaption_TruncatesWithAnEllipsisRatherThanLettingTheLabelClip()
        {
            var long_ = new string('x', PictureInPictureFraming.MaxCaptionLength + 40);

            var caption = PictureInPictureFraming.NormalizeCaption(long_);

            Assert.Equal(PictureInPictureFraming.MaxCaptionLength, caption.Length);
            Assert.EndsWith("\u2026", caption);
        }

        [Fact]
        public void NormalizeCaption_LeavesACaptionAtTheLimitAlone()
        {
            var exact = new string('x', PictureInPictureFraming.MaxCaptionLength);

            var caption = PictureInPictureFraming.NormalizeCaption(exact);

            Assert.Equal(exact, caption);
        }

        // ---- the visibility machine -------------------------------------------------------

        [Fact]
        public void State_StartsClosed()
        {
            var state = new PictureInPictureFraming.PictureInPictureState();

            Assert.False(state.IsVisible);
            Assert.Null(state.Caption);
            Assert.Null(state.Subject);
            Assert.Equal(0f, state.Remaining);
        }

        [Fact]
        public void State_ShowOpensAndTidiesTheCaption()
        {
            var state = new PictureInPictureFraming.PictureInPictureState();

            Assert.True(state.Show(null, "  Bella  is  painting "));

            Assert.True(state.IsVisible);
            Assert.Equal("Bella is painting", state.Caption);
        }

        [Fact]
        public void State_RepeatedShowOfTheSameEventIsANoOp()
        {
            // the whole point: an event that fires every few frames must not keep resetting the
            // hold timer, or the window can never be taken down.
            var state = new PictureInPictureFraming.PictureInPictureState();
            state.Show(null, "Bella is cooking", 5f);
            state.Tick(4f);

            Assert.False(state.Show(null, "Bella is cooking", 5f));

            // the countdown was not restarted, so the next tick closes it
            Assert.True(state.Tick(1f));
            Assert.False(state.IsVisible);
        }

        [Fact]
        public void State_AShowWithADifferentCaptionRetargetsTheWindow()
        {
            var state = new PictureInPictureFraming.PictureInPictureState();
            state.Show(null, "First", 5f);

            Assert.True(state.Show(null, "Second", 5f));

            Assert.Equal("Second", state.Caption);
            Assert.Equal(5f, state.Remaining);
        }

        [Fact]
        public void State_HideOnlyReportsATransition()
        {
            var state = new PictureInPictureFraming.PictureInPictureState();

            Assert.False(state.Hide());

            state.Show(null, "anything");
            Assert.True(state.Hide());
            Assert.False(state.Hide());
        }

        [Fact]
        public void State_HideClearsTheHoldCountdown()
        {
            var state = new PictureInPictureFraming.PictureInPictureState();
            state.Show(null, "anything", 5f);

            state.Hide();

            Assert.Equal(0f, state.Remaining);
        }

        [Fact]
        public void State_TickClosesTheWindowWhenTheHoldRunsOut()
        {
            var state = new PictureInPictureFraming.PictureInPictureState();
            state.Show(null, "Bella is cooking", 2f);

            Assert.False(state.Tick(1.5f));
            Assert.True(state.IsVisible);
            Assert.Equal(0.5f, state.Remaining, 3);

            Assert.True(state.Tick(0.5f));
            Assert.False(state.IsVisible);
        }

        [Fact]
        public void State_AStickyShowNeverClosesItself()
        {
            // hold 0 means "until something hides it", which is what a manual toggle wants.
            var state = new PictureInPictureFraming.PictureInPictureState();
            state.Show(null, "Bella is cooking", 0f);

            for (var i = 0; i < 1000; i++) Assert.False(state.Tick(1f / 60f));

            Assert.True(state.IsVisible);
        }

        [Fact]
        public void State_AnOversizedDeltaClosesTheWindowRatherThanGoingNegative()
        {
            var state = new PictureInPictureFraming.PictureInPictureState();
            state.Show(null, "Bella is cooking", 1f);

            // a stalled frame, or the window being opened on a frame the clock had already
            // run far ahead - the caller's clamp is the real answer to this, but the state
            // machine must not throw or leave itself in a stuck state either.
            Assert.True(state.Tick(30f));
            Assert.False(state.IsVisible);
            Assert.Equal(0f, state.Remaining);
        }

        [Fact]
        public void State_TickOnAClosedWindowDoesNothing()
        {
            var state = new PictureInPictureFraming.PictureInPictureState();

            Assert.False(state.Tick(1f));
            Assert.False(state.IsVisible);
        }

        [Theory]
        [InlineData(0f)]
        [InlineData(-1f)]
        [InlineData(float.NaN)]
        public void State_TickIgnoresANonPositiveDelta(float delta)
        {
            // otherwise a fixed-timestep frame with a zero delta would burn the hold timer
            // without any time having passed.
            var state = new PictureInPictureFraming.PictureInPictureState();
            state.Show(null, "Bella is cooking", 5f);

            Assert.False(state.Tick(delta));

            Assert.True(state.IsVisible);
            Assert.Equal(5f, state.Remaining);
        }
    }
}
