using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for <see cref="PPXDepthEngine.ResolveSnapshotSize"/>, the sizing rule behind the
    /// picture-in-picture window's copy of the lot view.
    ///
    /// <para>
    /// The lot lives in the engine's backbuffer, which <c>World.ChangeAAMode</c> creates with 4x
    /// or 8x MSAA for every anti-aliasing setting except "off", so the window copies it into a
    /// small, permanently non-multisampled render target and crops out of that. The copy's size is
    /// the only part of that which is arithmetic, and it is arithmetic with three ways to be
    /// wrong that a screenshot would not explain: too big (the copy costs what it was meant to
    /// save), too small (a magnification above 1 has nothing to magnify), or the wrong shape (the
    /// lot view comes out stretched).
    /// </para>
    ///
    /// <para>
    /// Pure, so none of this needs a graphics device - which matters, because the rule has to hold
    /// on a machine with a 4096-pixel render target cap and on one with a 16384 one, and those
    /// are not the machine the tests run on.
    /// </para>
    /// </summary>
    public sealed class LotSnapshotSizingTests
    {
        /// <summary>Stands in for a device that will allocate anything.</summary>
        private static readonly Point NoLimit = new Point(16384, 16384);

        // ---- fitting the request -----------------------------------------------------------

        [Fact]
        public void ASnapshotIsNeverLargerThanTheLotItCopies()
        {
            // A 64x48 lot asked to copy itself into a 416x312 box. Enlarging would cost the copy
            // and show nothing, so the source is a hard ceiling even though the box allows it.
            var size = PPXDepthEngine.ResolveSnapshotSize(64, 48, 416, 312, NoLimit);

            Assert.Equal(64, size.X);
            Assert.Equal(48, size.Y);
        }

        [Fact]
        public void ASnapshotFitsInsideTheRequestedBox()
        {
            // 1920x1080 into 416x312: the width binds, since 416/1920 is the shorter fit.
            var size = PPXDepthEngine.ResolveSnapshotSize(1920, 1080, 416, 312, NoLimit);

            Assert.True(size.X <= 416, "width " + size.X + " is past the 416 asked for");
            Assert.True(size.Y <= 312, "height " + size.Y + " is past the 312 asked for");
        }

        [Fact]
        public void ASnapshotKeepsTheLotsShape()
        {
            // The copy is stretched back over the whole picture area, so a shape that does not
            // match the lot's is a lot view that comes out distorted. A texel of rounding on each
            // axis is as close as integer sizes get.
            var size = PPXDepthEngine.ResolveSnapshotSize(1920, 1080, 416, 312, NoLimit);

            var expected = 1080 / 1920f;
            var actual = size.Y / (float)size.X;
            Assert.True(System.Math.Abs(actual - expected) < 0.01f,
                size.X + "x" + size.Y + " is a shape of " + actual + " against a lot of " + expected);
        }

        [Fact]
        public void ASnapshotUsesTheWholeBoxWhenTheLotsShapeMatchesIt()
        {
            // A 4:3 lot into a 4:3 box is the case where the fit should be exact rather than
            // leaving a band of unused box on one axis.
            var size = PPXDepthEngine.ResolveSnapshotSize(800, 600, 416, 312, NoLimit);

            Assert.Equal(416, size.X);
            Assert.Equal(312, size.Y);
        }

        [Theory]
        [InlineData(0, 0)]     // "I do not care about resolution"
        [InlineData(-1, -1)]
        public void ARequestOfNothingMeansTheLotsOwnSize(int w, int h)
        {
            var size = PPXDepthEngine.ResolveSnapshotSize(1280, 720, w, h, NoLimit);

            Assert.Equal(1280, size.X);
            Assert.Equal(720, size.Y);
        }

        [Fact]
        public void EachAxisIsAnsweredOnItsOwnTerms()
        {
            // A caller that wants a fixed width but no opinion on the height still gets the lot's
            // aspect rather than a squashed copy.
            var size = PPXDepthEngine.ResolveSnapshotSize(1280, 720, 320, 0, NoLimit);

            Assert.Equal(320, size.X);
            Assert.Equal(180, size.Y);
        }

        // ---- the device's limits ------------------------------------------------------------

        [Fact]
        public void ASnapshotIsBroughtInsideWhatTheDeviceWillAllocate()
        {
            // The same cap the engine already applies to the screen target and the 2D world
            // buffers. 1920x1080 into 416x312 already fits, so the cap has to be the thing under
            // test: a device that only allocates 256.
            var size = PPXDepthEngine.ResolveSnapshotSize(1920, 1080, 416, 312, new Point(256, 256));

            Assert.True(size.X <= 256, "width " + size.X + " is past the device's 256");
            Assert.True(size.Y <= 256, "height " + size.Y + " is past the device's 256");
        }

        [Fact]
        public void AClampedSnapshotStillKeepsTheLotsShape()
        {
            // The cap changes the size; it must not change the shape, or a device with a small
            // limit would show a stretched lot view and a large one would not.
            var size = PPXDepthEngine.ResolveSnapshotSize(1920, 1080, 416, 312, new Point(256, 256));

            var expected = 1080 / 1920f;
            var actual = size.Y / (float)size.X;
            Assert.True(System.Math.Abs(actual - expected) < 0.01f,
                size.X + "x" + size.Y + " is a shape of " + actual + " against a lot of " + expected);
        }

        [Fact]
        public void ADeviceThatAllocatesNothingInPartIsOnlyHeldToWhatItCan()
        {
            // width and height limits are separate caps. A device that caps height at 64 but takes
            // any width is held to the height, not to a box it never promised.
            var size = PPXDepthEngine.ResolveSnapshotSize(1920, 1080, 416, 312, new Point(16384, 64));

            Assert.True(size.Y <= 64, "height " + size.Y + " is past the device's 64");
            Assert.True(size.X <= 416, "width " + size.X + " is past the 416 asked for");
        }

        [Fact]
        public void ASizeOfAtLeastAPixelPerAxisSurvivesAnything()
        {
            // A render target of zero on either axis is rejected outright, and a 1xN lot through
            // any clamp has to stay allocatable. The smallest answer is the one that works.
            var narrow = PPXDepthEngine.ResolveSnapshotSize(1, 4096, 416, 312, new Point(1, 1));
            var flat = PPXDepthEngine.ResolveSnapshotSize(4096, 1, 416, 312, new Point(1, 1));

            Assert.True(narrow.X >= 1 && narrow.Y >= 1, narrow + " is not allocatable");
            Assert.True(flat.X >= 1 && flat.Y >= 1, flat + " is not allocatable");
        }

        // ---- nothing to copy ----------------------------------------------------------------

        [Theory]
        [InlineData(0, 1080)]
        [InlineData(1920, 0)]
        [InlineData(-1, -1)]
        public void ThereIsNoSnapshotOfALotWithNoArea(int w, int h)
        {
            // A zero-sized answer is the "do not build anything" signal; the capture turns it into
            // a null rather than asking the device for a target that cannot exist.
            Assert.Equal(Point.Zero, PPXDepthEngine.ResolveSnapshotSize(w, h, 416, 312, NoLimit));
        }

        [Fact]
        public void AnUnprobedDeviceIsNotTreatedAsAOnePixelOne()
        {
            // maxSize arrives from a probe of the device that has not run yet, which is what a
            // zero means. Reading that as a limit would collapse every snapshot to 1x1 on the
            // first frame and then resize on the next.
            var size = PPXDepthEngine.ResolveSnapshotSize(1920, 1080, 416, 312, Point.Zero);

            Assert.Equal(416, size.X);
            Assert.Equal(234, size.Y);
        }

        // ---- the downscale chain ------------------------------------------------------------

        [Fact]
        public void ASmallReductionIsAHalvingAndALanding()
        {
            // 900 wide into 416 is 2.2x - just over the 2x2 box a single bilinear blit is, so the
            // chain halves once and then lands exactly on the requested size rather than
            // undersampling by 2.2x in one step.
            var chain = PPXDepthEngine.ResolveSnapshotChain(900, 506, 416, 234);

            Assert.Equal(2, chain.Count);
            Assert.Equal(450, chain[0].X);
            Assert.Equal(253, chain[0].Y);
            Assert.Equal(416, chain[1].X);
            Assert.Equal(234, chain[1].Y);
        }

        [Fact]
        public void ASourceThatNeedsNoReductionAtAllIsStillOneStep()
        {
            // Smaller than the snapshot, so the copy is a stretch. Still a copy - a plain texture
            // the window can sample - and still one step, because there is nothing to reduce.
            var chain = PPXDepthEngine.ResolveSnapshotChain(320, 180, 416, 234);

            Assert.Single(chain);
            Assert.Equal(416, chain[0].X);
        }

        [Fact]
        public void ABigReductionIsBrokenIntoHalvingsEndingAtTheSnapshot()
        {
            // 1920x1080 into 416x234 is 4.6x. One blit at that ratio samples about one source
            // pixel in twenty-one and the result crawls with aliasing as the camera scrolls, so
            // the chain halves twice and then lands exactly on the requested size.
            var chain = PPXDepthEngine.ResolveSnapshotChain(1920, 1080, 416, 234);

            Assert.Equal(3, chain.Count);
            Assert.Equal(960, chain[0].X);
            Assert.Equal(540, chain[0].Y);
            Assert.Equal(480, chain[1].X);
            Assert.Equal(270, chain[1].Y);
            // the last step is always exactly what was asked for, not a rounded halving of it
            Assert.Equal(416, chain[2].X);
            Assert.Equal(234, chain[2].Y);
        }

        [Fact]
        public void EveryReductionOfAVeryWideLotIsHalvedRightDown()
        {
            // 4K into the plain window size is 18x, which no single blit can filter. The chain
            // keeps halving rather than stopping at some arbitrary step count, because stopping
            // early is what puts the point sampling back in - the whole reason the chain is here.
            var chain = PPXDepthEngine.ResolveSnapshotChain(3840, 2160, 208, 156);

            var from = new Point(3840, 2160);
            foreach (var step in chain)
            {
                Assert.True(from.X <= step.X * 2 || from.Y <= step.Y * 2,
                    "3840x2160 -> " + step + " minifies by more than 2x in one step");
                from = step;
            }
            Assert.Equal(new Point(208, 156), chain[chain.Count - 1]);
        }

        [Fact]
        public void TheChainStopsGrowingOnASourceNoThisGameWouldEverUse()
        {
            // A 16K lot would otherwise halve into a 7680x4320 intermediate - 132 megabytes of
            // colour buffer for a cosmetic window. The guard is a runaway stop, not a quality
            // setting, so it is asserted to be far above anything real: a 4K source sails under
            // it, and only a 16K one trips it.
            var fourK = PPXDepthEngine.ResolveSnapshotChain(3840, 2160, 416, 234);
            var sixteenK = PPXDepthEngine.ResolveSnapshotChain(15360, 8640, 416, 234);

            Assert.Equal(4, fourK.Count);
            Assert.Equal(1920, fourK[0].X);

            // Bounded: it does not keep halving a source that big all the way down.
            Assert.True(sixteenK.Count < 6, "a 16K source produced " + sixteenK.Count + " steps");
            Assert.True((long)sixteenK[0].X * sixteenK[0].Y <= 4L * 1024 * 1024,
                sixteenK[0] + " is past the runaway guard");
            // The quality trade is explicit rather than silent: the landing blit is the one that
            // minifies by more than 2x, and only that one.
            Assert.Equal(new Point(416, 234), sixteenK[sixteenK.Count - 1]);
        }

        [Fact]
        public void NoStepOfTheChainMinifiesByMoreThanAFactorOfTwo()
        {
            // The property the chain exists for, asserted over the lot and window sizes that occur
            // rather than over one example: every blit, including the one that resolves the
            // multisampled backbuffer, is at most a 2x2 box filter.
            var lots = new[]
            {
                new Point(1920, 1080), new Point(2560, 1440),
                new Point(3840, 2160), new Point(1280, 720)
            };
            var requests = new[]
            {
                new Point(208, 156), new Point(416, 312), new Point(624, 468)
            };

            foreach (var lot in lots)
            {
                foreach (var request in requests)
                {
                    var chain = PPXDepthEngine.ResolveSnapshotChain(lot.X, lot.Y, request.X, request.Y);
                    Assert.NotEmpty(chain);

                    var from = lot;
                    foreach (var step in chain)
                    {
                        Assert.True(step.X <= from.X && step.Y <= from.Y,
                            lot + " -> " + step + " grows somewhere");
                        Assert.True(from.X <= step.X * 2 || from.Y <= step.Y * 2,
                            lot + " -> " + step + " minifies by more than 2x in one step");
                        from = step;
                    }
                }
            }
        }

        [Fact]
        public void TheChainIsBoundedSoAHugeLotDoesNotAddScratchTargetsForever()
        {
            // A 4K lot is 8.3x the snapshot, more than twice the reduction a 1080p one is. The
            // chain follows the reduction down rather than stopping at a step count, so it costs
            // four blits instead of three - and the extra one is a 480x270 target, not a
            // 2048x1152 one, because the intermediates halve with the reduction.
            var chain = PPXDepthEngine.ResolveSnapshotChain(3840, 2160, 416, 234);

            Assert.Equal(4, chain.Count);
            Assert.Equal(1920, chain[0].X);
            Assert.Equal(1080, chain[0].Y);
            Assert.Equal(960, chain[1].X);
            Assert.Equal(540, chain[1].Y);
            Assert.Equal(480, chain[2].X);
            Assert.Equal(270, chain[2].Y);
            Assert.Equal(416, chain[3].X);
            Assert.Equal(234, chain[3].Y);
        }

        [Fact]
        public void TheChainNeverStepsBelowTheSizeItWasAskedFor()
        {
            // A source just over twice the snapshot: halving it would land under the target, and
            // the last step would have to stretch it back up - the opposite of a reduction.
            var chain = PPXDepthEngine.ResolveSnapshotChain(900, 506, 416, 234);

            foreach (var step in chain)
            {
                Assert.True(step.X >= 416 && step.Y >= 234, step + " is under the requested size");
            }
        }

        [Theory]
        [InlineData(0, 1080, 416, 234)]
        [InlineData(1920, 0, 416, 234)]
        [InlineData(1920, 1080, 0, 234)]
        [InlineData(1920, 1080, 416, 0)]
        [InlineData(-1, -1, -1, -1)]
        public void ThereIsNoChainForAnythingWithoutArea(int sw, int sh, int tw, int th)
        {
            Assert.Empty(PPXDepthEngine.ResolveSnapshotChain(sw, sh, tw, th));
        }
    }
}
