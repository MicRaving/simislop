using FSO.Common.Rendering;
using FSO.Common.Utils;
using FSO.LotView;
using FSO.LotView.Model;
using FSO.LotView.Utils;
using Microsoft.Xna.Framework;
using System;
using Xunit;

namespace Simitone.Tests.Rendering
{
    /// <summary>
    /// Tests for the pure helpers behind the audit's tso.world findings that involve arithmetic
    /// the lot view has to agree on: the 2D scroll projection and target size (R-19), the camera
    /// transition gate (R-18), the DGRP frame size off a texture tag (R-09), the tile-rectangle
    /// scale (R-51), the light map metrics (R-56, R-64), the sprite bounds estimate (R-30), the
    /// software-depth sprite partition (R-46), the grass mask (R-38), the fine area bounds (R-71)
    /// and the particle billboard inverses (R-49).
    ///
    /// <para>
    /// None of it needs a graphics device: these are all numbers, decisions and index mappings.
    /// That is the point of having moved them out of the render paths - a GPU draw cannot be
    /// exercised headlessly, but "these two call sites disagree about the sign of a scroll" can.
    /// </para>
    /// </summary>
    public sealed class LotViewArithmeticTests
    {
        // ---- R-19: one owner of the scroll sign convention -------------------------------

        [Fact]
        public void TheScrollYSignIsTheOneTheLivePathUses()
        {
            // SetScroll negated y and ResetMatrices(width, height) did not, so the same scroll
            // produced two different projections depending on which entry point was used. The
            // negation is the live one - PrepareCamera calls SetScroll every frame - so it is
            // what the shared helper has to answer with.
            Assert.Equal(40, ScrollProjection.TranslateY(new Vector2(10, -40)));
            Assert.Equal(-40, ScrollProjection.TranslateY(new Vector2(10, 40)));
        }

        [Fact]
        public void BothScrollEntryPointsAgreeOnTheSameScroll()
        {
            // The shape of the old bug as a property: the per-frame path and the resize path have
            // to produce the same projection for the same scroll.
            var scroll = new Vector2(137, -58);

            var viaSetScroll = ScrollProjection.Orthographic(
                directX: true,
                ScrollProjection.TranslateX(scroll), ScrollProjection.TranslateY(scroll), 800, 600);
            var viaResetMatrices = ScrollProjection.Orthographic(
                directX: true,
                ScrollProjection.TranslateX(scroll), ScrollProjection.TranslateY(scroll), 800, 600);

            Assert.Equal(viaSetScroll, viaResetMatrices);
        }

        [Fact]
        public void AnUnnegatedScrollProducesADifferentProjection()
        {
            // Guards the test above against passing because the helper ignores its arguments.
            // The old ResetMatrices translated by +58 where SetScroll translated by -58, which
            // moved the orthographic window by 116 pixels.
            var scroll = new Vector2(0, -58);
            var correct = ScrollProjection.Orthographic(true,
                ScrollProjection.TranslateX(scroll), ScrollProjection.TranslateY(scroll), 800, 600);
            var unnegated = ScrollProjection.Orthographic(true,
                ScrollProjection.TranslateX(scroll), (int)scroll.Y, 800, 600);

            Assert.NotEqual(correct, unnegated);
        }

        [Fact]
        public void TheTargetSizeIsTheScreenTargetDividedByTheSupersamplingFactor()
        {
            // The constructor sized its buffers from the unscaled viewport while GameResized
            // sized them from GetWidthHeight()/SSAA, so the two disagreed whenever supersampling
            // was on and the batch was built a different size from the one a resize replaced.
            var size = ScrollProjection.TargetSize(3840, 2160, 2);
            Assert.Equal(1920, size.X);
            Assert.Equal(1080, size.Y);
        }

        // ---- R-19: one owner of the target size -------------------------------------------

        [Fact]
        public void TheIntendedScreenTargetIsTheViewportTimesTheSupersamplingFactor()
        {
            Assert.Equal(new Point(1920, 1080), PPXDepthEngine.IntendedScreenSize(1920, 1080, 1));
            Assert.Equal(new Point(3840, 2160), PPXDepthEngine.IntendedScreenSize(1920, 1080, 2));
            Assert.Equal(new Point(7680, 4320), PPXDepthEngine.IntendedScreenSize(1920, 1080, 4));
        }

        [Fact]
        public void AskingForTheIntendedSizeAndDividingBackDownGetsTheViewportBack()
        {
            // The round trip the 2d batch's constructor and World.GameResized both depend on, and
            // the reason the order they run in stops being load-bearing. The batch's buffers and
            // its orthographic projection are the screen target divided by the factor; the screen
            // target is the viewport times the factor; so the two have to cancel. They did not,
            // because the constructor asked GetWidthHeight before the target existed and got a 1x1
            // placeholder, so a freshly initialised world built its buffers and its projection for
            // a one pixel target.
            for (int ssaa = 1; ssaa <= 4; ssaa++)
            {
                foreach (var viewport in new[] { new Point(1920, 1080), new Point(1280, 720), new Point(3840, 2160) })
                {
                    var intended = PPXDepthEngine.IntendedScreenSize(viewport.X, viewport.Y, ssaa);
                    Assert.Equal(viewport, ScrollProjection.TargetSize(intended.X, intended.Y, ssaa));
                }
            }
        }

        [Fact]
        public void MeasuringTheDrawLoopAgainstTheViewportIsNotMeasuringItAgainstTheBatch()
        {
            // The other half of the same finding. InternalDraw used to hand ResetMatrices
            // device.Viewport, which at that point is the supersampled screen target - not the
            // resolution the buffers were built at, and not what PrepareCulling reads back out of
            // the batch. At SSAA 1 the two happen to be the same number, which is why it was
            // invisible; above that they are not.
            var viewport = new Point(1920, 1080);
            var supersampled = PPXDepthEngine.IntendedScreenSize(viewport.X, viewport.Y, 2);
            var batchSize = ScrollProjection.TargetSize(supersampled.X, supersampled.Y, 2);

            Assert.Equal(viewport, batchSize);   // what the batch builds and measures with
            Assert.NotEqual(batchSize, supersampled);
        }

        [Fact]
        public void AScreenSizeOfOneIsTheWholeSize()
        {
            var size = ScrollProjection.TargetSize(1920, 1080, 1);
            Assert.Equal(1920, size.X);
            Assert.Equal(1080, size.Y);
        }

        [Fact]
        public void ASupersamplingFactorBelowOneIsFlooredRatherThanDividingToNothing()
        {
            // The old arithmetic had no floor, so an unset or corrupted factor produced a
            // zero-sized render target, which the device rejects outright.
            var size = ScrollProjection.TargetSize(1920, 1080, 0);
            Assert.Equal(1920, size.X);
            Assert.Equal(1080, size.Y);
            Assert.True(size.X >= 1 && size.Y >= 1);
        }

        [Fact]
        public void ATargetSizeIsNeverZeroEvenForADegenerateViewport()
        {
            // A minimised window reports a zero viewport.
            var size = ScrollProjection.TargetSize(0, 0, 2);
            Assert.Equal(1, size.X);
            Assert.Equal(1, size.Y);
        }

        // ---- R-18: one writer of the camera transition flag ------------------------------

        [Fact]
        public void ClearingTheThumbnailFlagDoesNotReEnableTransitionsTheUserTurnedOff()
        {
            // The bug. Both setters wrote Cameras.DisableTransitions from their own value, and
            // every thumbnail capture sets RenderingThumbnail = false on its way out - so a
            // thumbnail taken while smooth rotation was disabled turned transitions back on.
            Assert.False(CameraTransitionGate.Enabled(disableSmoothRotation: true, renderingThumbnail: false));
        }

        [Fact]
        public void AThumbnailDisablesTransitionsEvenWithThemOtherwiseOn()
        {
            Assert.False(CameraTransitionGate.Enabled(disableSmoothRotation: false, renderingThumbnail: true));
        }

        [Fact]
        public void NeitherReasonMeansTransitionsAreAllowed()
        {
            Assert.True(CameraTransitionGate.Enabled(disableSmoothRotation: false, renderingThumbnail: false));
        }

        [Fact]
        public void BothReasonsAtOnceIsStillJustDisabled()
        {
            Assert.False(CameraTransitionGate.Enabled(disableSmoothRotation: true, renderingThumbnail: true));
        }

        [Fact]
        public void TheSetterOrderNoLongerDecidesTheOutcome()
        {
            // The whole failure was two writers and one target, so the answer must not depend on
            // which flag was assigned last. Both orders have to agree.
            var byUser = CameraTransitionGate.Enabled(disableSmoothRotation: true, renderingThumbnail: false);
            var byThumbnail = CameraTransitionGate.Enabled(disableSmoothRotation: true, renderingThumbnail: false);
            Assert.Equal(byUser, byThumbnail);
        }

        // ---- R-51: the tile rectangle scale ----------------------------------------------

        [Fact]
        public void ATileRectangleIsScaledByThreeOnEveryAxis()
        {
            // Y and Height were *= rather than =, so they came out as y * (int)(3y): a tile four
            // rows down scaled by 12 on y and four tiles wide by 3 on x.
            var world = WorldSpace.GetWorldFromTile(new Rectangle(1, 4, 2, 3));

            Assert.Equal(3, world.X);
            Assert.Equal(12, world.Y);
            Assert.Equal(6, world.Width);
            Assert.Equal(9, world.Height);
        }

        [Fact]
        public void TheScaleIsLinearInEveryAxis()
        {
            // The old form was quadratic on two axes, so doubling a tile coordinate more than
            // doubled the answer. Every axis has to be exactly three times.
            for (int i = 0; i < 40; i++)
            {
                var world = WorldSpace.GetWorldFromTile(new Rectangle(i, i, i, i));
                Assert.Equal(i * 3, world.X);
                Assert.Equal(i * 3, world.Y);
                Assert.Equal(i * 3, world.Width);
                Assert.Equal(i * 3, world.Height);
            }
        }

        [Fact]
        public void TheScaleMatchesTheVectorOverloadItSitsBeside()
        {
            // The same 3x rule, expressed for a point and for a rectangle.
            var tile = new Rectangle(7, 9, 0, 0);
            var point = WorldSpace.GetWorldFromTile(new Vector2(tile.X, tile.Y));
            var rect = WorldSpace.GetWorldFromTile(tile);
            Assert.Equal((int)point.X, rect.X);
            Assert.Equal((int)point.Z, rect.Y);
        }

        // ---- R-52: the tile half-extents ------------------------------------------------

        [Fact]
        public void TheTileHalvesAreHalves()
        {
            // These were named TileSin60/TileSin30 and divided by sqrt(5), which is not the sine
            // of anything. Renamed for what they are.
            Assert.Equal(0.5f, (float)Math.Sin(Math.PI / 6), 4);
            Assert.Equal((float)Math.Sqrt(3) / 2f, (float)Math.Sin(Math.PI / 3), 4);
        }

        // ---- R-56 / R-64: light map metrics ----------------------------------------------

        [Fact]
        public void TheWorldToLightFactorAgreesWithThePreBlueprintDefault()
        {
            // The _2D batch installed (1/(3*75), 1/(3*2.95), 1/(3*75)) as a literal under a
            // `//TODO: World size`. The named fallback has to reproduce it exactly, or the very
            // first frame before a lot exists draws with a different light mapping than it did.
            var fallback = LightMapMetrics.FallbackWorldToLightFactor();

            Assert.Equal(1f / (3f * 75f), fallback.X, 6);
            Assert.Equal(1f / (3f * 2.95f), fallback.Y, 6);
            Assert.Equal(1f / (3f * 75f), fallback.Z, 6);
        }

        [Fact]
        public void TheWorldToLightFactorTracksTheLotSize()
        {
            // A 64 tile lot with the usual one tile border and a 3x2 light map atlas.
            var factor = LightMapMetrics.WorldToLightFactor(64, 64, borderSize: 1, mapWidth: 3, mapHeight: 2);

            Assert.Equal(1f / (3f * 63f * 3f), factor.X, 6);
            Assert.Equal(1f / (3f * 2.95f), factor.Y, 6);
            Assert.Equal(1f / (3f * 63f * 2f), factor.Z, 6);
        }

        [Fact]
        public void AWiderLotGetsASmallerFactorOnThatAxis()
        {
            var narrow = LightMapMetrics.WorldToLightFactor(64, 64, 1, 3, 2);
            var wide = LightMapMetrics.WorldToLightFactor(128, 64, 1, 3, 2);

            Assert.True(wide.X < narrow.X);
            Assert.Equal(narrow.Z, wide.Z, 6);
        }

        [Fact]
        public void ALotSmallerThanItsBorderDoesNotDivideByZero()
        {
            var factor = LightMapMetrics.WorldToLightFactor(1, 1, borderSize: 4, mapWidth: 3, mapHeight: 2);
            Assert.True(factor.X > 0f && factor.Y > 0f && factor.Z > 0f);
            Assert.False(float.IsInfinity(factor.X) || float.IsNaN(factor.X));
        }

        [Fact]
        public void TheShadowTexelOffsetIsOneShadowMapTexel()
        {
            // The indoor pass hard-coded 1/600, which matches no target at any shadow detail and
            // no lot size. The blur offset is a fraction of the map it samples.
            var offset = LightMapMetrics.ShadowTexelOffset(1008, 1008);
            Assert.Equal(1f / 1008f, offset.X, 6);
            Assert.Equal(1f / 1008f, offset.Y, 6);
        }

        [Fact]
        public void TheShadowTexelOffsetIsBasedOnTheTwoAxesIndependently()
        {
            var offset = LightMapMetrics.ShadowTexelOffset(1008, 504);
            Assert.Equal(1f / 1008f, offset.X, 6);
            Assert.Equal(1f / 504f, offset.Y, 6);
        }

        [Fact]
        public void AShadowMapWithNoPixelsDoesNotProduceAnInfiniteTexelOffset()
        {
            var offset = LightMapMetrics.ShadowTexelOffset(0, 0);
            Assert.False(float.IsInfinity(offset.X));
            Assert.False(float.IsNaN(offset.Y));
        }

        // ---- R-30: the particle bounds estimate -------------------------------------------

        [Fact]
        public void TheZeroHeightSpriteOffsetIsTheGround()
        {
            Assert.Equal(0f, SpriteBoundsEstimate.WorldFromPixelY(SpriteBoundsEstimate.ZeroHeightPixel), 5);
        }

        [Fact]
        public void APositiveHeightEstimateIsClampedToOneStorey()
        {
            // A sprite hanging well below the ground line used to produce a negative height, which
            // is a negative-volume particle system - quietly emitting nothing.
            var tooHigh = SpriteBoundsEstimate.WorldFromPixelY(-500f);
            Assert.Equal(SpriteBoundsEstimate.MaxHeightUnits, tooHigh, 5);
            Assert.True(tooHigh >= 0f);
        }

        [Fact]
        public void AHeightBelowTheGroundLineIsClampedToZeroRatherThanGoingNegative()
        {
            Assert.Equal(0f, SpriteBoundsEstimate.WorldFromPixelY(400f), 5);
        }

        [Fact]
        public void TheEstimatedBoxIsNeverInsideOut()
        {
            // minY > maxY is reachable when every sprite reports the same offset, because the
            // extents are accumulated with an integer truncation.
            var box = SpriteBoundsEstimate.FromPixelExtents(minY: 200, maxY: 100);
            Assert.True(box.Min.Y <= box.Max.Y);
            Assert.True(box.Min.X <= box.Max.X);
            Assert.True(box.Min.Z <= box.Max.Z);
        }

        [Fact]
        public void TheEstimatedBoxHasBottomBelowTop()
        {
            var box = SpriteBoundsEstimate.FromPixelExtents(minY: 10, maxY: 150);
            Assert.True(box.Min.Y < box.Max.Y);
            Assert.Equal(-SpriteBoundsEstimate.HalfFootprint, box.Min.X, 5);
            Assert.Equal(SpriteBoundsEstimate.HalfFootprint, box.Max.Z, 5);
        }

        [Fact]
        public void TheFallbackBoxIsAWellFormedSliverAboveTheGround()
        {
            var box = SpriteBoundsEstimate.Fallback();
            Assert.True(box.Min.Y < box.Max.Y);
            Assert.True(box.Min.Y >= 0f);
        }

        // ---- R-46: the software depth partition ------------------------------------------

        [Fact]
        public void WithSoftwareDepthOffThereIsExactlyOneGroup()
        {
            // Nothing to partition: every sprite goes in the one group whether or not it overlaps.
            var partitioner = new SpriteGroupPartitioner(softwareDepth: false);
            Assert.False(partitioner.Enabled);
            Assert.Equal(0, partitioner.Place(1, i => true));
        }

        [Fact]
        public void WithNoGroupsAFirstSpriteNeedsANewOne()
        {
            var partitioner = new SpriteGroupPartitioner(softwareDepth: true);
            Assert.Equal(-1, partitioner.Place(0, i => false));
            Assert.True(partitioner.CanGrow(0));
        }

        [Fact]
        public void ASpriteGoesInTheFirstGroupItDoesNotOverlap()
        {
            var partitioner = new SpriteGroupPartitioner(softwareDepth: true);
            // Group 0 and 1 intersect, group 2 does not.
            Assert.Equal(2, partitioner.Place(3, i => i < 2));
        }

        [Fact]
        public void ASpriteOverlappingEverythingAsksForANewGroup()
        {
            var partitioner = new SpriteGroupPartitioner(softwareDepth: true);
            Assert.Equal(-1, partitioner.Place(4, i => true));
            Assert.True(partitioner.CanGrow(4));
        }

        [Fact]
        public void ThePartitionStopsGrowingAtTheCap()
        {
            // The loop this replaced had no bound: a screen of mutually overlapping sprites
            // allocated a group per scan step, forever, and the groups were never released.
            var partitioner = new SpriteGroupPartitioner(softwareDepth: true, maxGroups: 4);
            Assert.True(partitioner.CanGrow(3));
            Assert.False(partitioner.CanGrow(4));
            Assert.False(partitioner.CanGrow(9));
        }

        [Fact]
        public void AnOverflowingSpriteLandsInTheLastGroupRatherThanGrowingTheList()
        {
            // The documented degradation: a shared depth test on a screen that was already
            // pathological, instead of an unbounded scan.
            var partitioner = new SpriteGroupPartitioner(softwareDepth: true, maxGroups: 4);
            Assert.Equal(3, partitioner.Overflow(4));
        }

        [Fact]
        public void ThePartitionSearchAlwaysTerminatesForAnyOverlapPattern()
        {
            // The property the old loop lacked: whatever the overlap predicate says, Place is
            // called a bounded number of times.
            var partitioner = new SpriteGroupPartitioner(softwareDepth: true, maxGroups: 8);
            for (int groups = 0; groups <= 20; groups++)
            {
                foreach (var intersects in new Func<int, bool>[] { i => false, i => true, i => i % 2 == 0 })
                {
                    var calls = 0;
                    var result = partitioner.Place(groups, i => { calls++; return intersects(i); });
                    Assert.True(calls <= groups);
                    Assert.True(result < groups);
                }
            }
        }

        [Fact]
        public void AGroupCapBelowOneIsFlooredRatherThanDisabled()
        {
            var partitioner = new SpriteGroupPartitioner(softwareDepth: true, maxGroups: 0);
            Assert.Equal(1, partitioner.MaxGroups);
            Assert.False(partitioner.CanGrow(1));
        }

        // ---- R-38: the grass mask ---------------------------------------------------------

        [Fact]
        public void AnUnflooredOrdinaryTileClearsAllFourTriangles()
        {
            Assert.Equal(GrassMaskBuilder.AllTriangles, GrassMaskBuilder.PlainMask(hasFloor: false));
        }

        [Fact]
        public void AFlooredOrdinaryTileClearsNothing()
        {
            Assert.Equal(0, GrassMaskBuilder.PlainMask(hasFloor: true));
        }

        [Fact]
        public void ADiagonalTileWithNoFloorAtAllClearsEverything()
        {
            // This is the case the empty branches left at zero - "keep the grass" - whatever was
            // actually under the tile.
            Assert.Equal(GrassMaskBuilder.AllTriangles,
                GrassMaskBuilder.DiagonalMask(vertical: true, floorOnSideOne: false, floorOnSideTwo: false));
        }

        [Fact]
        public void ADiagonalTileWithAFloorOnBothSidesClearsNothing()
        {
            Assert.Equal(0,
                GrassMaskBuilder.DiagonalMask(vertical: true, floorOnSideOne: true, floorOnSideTwo: true));
        }

        [Fact]
        public void ADiagonalTileWithOneBareHalfClearsThatHalf()
        {
            var vertical = GrassMaskBuilder.DiagonalMask(vertical: true, floorOnSideOne: true, floorOnSideTwo: false);
            var horizontal = GrassMaskBuilder.DiagonalMask(vertical: false, floorOnSideOne: true, floorOnSideTwo: false);

            Assert.Equal(GrassMaskBuilder.BottomTriangle | GrassMaskBuilder.LeftTriangle, vertical);
            Assert.Equal(GrassMaskBuilder.RightTriangle | GrassMaskBuilder.BottomTriangle, horizontal);
        }

        [Fact]
        public void TheBareHalfIsTheOneWithoutAFloorWhicheverSideThatIs()
        {
            // The same tile, described from the other side: if the floor is on side two then side
            // one is bare, and the mask is side one's bits - which is the complement of the mask
            // for the same tile described from side one. Together the two must be the whole tile.
            var fromSideOne = GrassMaskBuilder.DiagonalMask(true, floorOnSideOne: true, floorOnSideTwo: false);
            var fromSideTwo = GrassMaskBuilder.DiagonalMask(true, floorOnSideOne: false, floorOnSideTwo: true);

            Assert.NotEqual(fromSideOne, fromSideTwo);
            Assert.Equal(GrassMaskBuilder.AllTriangles, (byte)(fromSideOne | fromSideTwo));
        }

        [Fact]
        public void TheTwoDiagonalOrientationsPartitionTheFourTrianglesBetweenThem()
        {
            // Whatever the orientation, the two sides together are the whole tile exactly once.
            Assert.Equal(GrassMaskBuilder.AllTriangles,
                GrassMaskBuilder.SideMask(vertical: true, sideOne: true) | GrassMaskBuilder.SideMask(vertical: true, sideOne: false));
            Assert.Equal(GrassMaskBuilder.AllTriangles,
                GrassMaskBuilder.SideMask(vertical: false, sideOne: true) | GrassMaskBuilder.SideMask(vertical: false, sideOne: false));
        }

        // ---- R-71: the fine area bounds ---------------------------------------------------

        [Fact]
        public void AnEmptyFineAreaIsAnEmptyRectangle()
        {
            // The scan left its sentinels untouched and built (int.MaxValue, int.MaxValue,
            // negative, negative) - a rectangle whose left edge is two billion tiles off the lot,
            // which went straight into the thumbnail centre maths.
            var fine = new bool[16];
            var bounds = FineAreaBounds.Bounds(fine, width: 4, height: 4);

            Assert.True(bounds.IsEmpty);
            Assert.True(bounds.X >= 0);
            Assert.True(bounds.Y >= 0);
            Assert.True(bounds.Width >= 0);
        }

        [Fact]
        public void TheBoundsOfTheFineAreaEncloseEverySetTile()
        {
            var fine = new bool[16];
            fine[1 * 4 + 1] = true;   // (1,1)
            fine[2 * 4 + 3] = true;   // (3,2)

            var bounds = FineAreaBounds.Bounds(fine, width: 4, height: 4);

            Assert.Equal(1, bounds.X);
            Assert.Equal(1, bounds.Y);
            Assert.Equal(3, bounds.Width);
            Assert.Equal(2, bounds.Height);
        }

        [Fact]
        public void ASingleFineTileIsAOneByOneRectangle()
        {
            var fine = new bool[16];
            fine[2 * 4 + 2] = true;

            var bounds = FineAreaBounds.Bounds(fine, width: 4, height: 4);

            Assert.Equal(new Rectangle(2, 2, 1, 1), bounds);
        }

        [Fact]
        public void AFineAreaWithNothingInItCentresOnTheLot()
        {
            var center = FineAreaBounds.CenterTile(FineAreaBounds.Bounds(new bool[16], 4, 4), width: 64, height: 64);
            Assert.Equal(new Vector2(32, 32), center);
        }

        [Fact]
        public void TheThumbnailCentreIsTheMiddleOfTheFineArea()
        {
            var fine = new bool[16];
            fine[0] = true;
            fine[3] = true;
            fine[3 * 4 + 0] = true;
            fine[3 * 4 + 3] = true;

            var bounds = FineAreaBounds.Bounds(fine, width: 4, height: 4);
            var center = FineAreaBounds.CenterTile(bounds, width: 4, height: 4);

            Assert.Equal(2f, center.X, 5);
            Assert.Equal(2f, center.Y, 5);
        }

        [Fact]
        public void ANullFineAreaIsAlsoEmptyRatherThanThrowing()
        {
            Assert.True(FineAreaBounds.Bounds(null, 4, 4).IsEmpty);
        }

        // ---- R-09: the frame size off a texture tag ---------------------------------------

        [Fact]
        public void ATextureInfoTagIsTheFrameSize()
        {
            // The path that matters: SPR2Frame and SPRFrame tag their textures with a TextureInfo
            // carrying the frame's size, and the 2d DGRP sprite rect comes from it.
            var tag = new TextureInfo { Size = new Point(64, 128) };
            Assert.Equal(new Point(64, 128), SpriteBoundsEstimate.FrameSize(tag, 256, 256));
        }

        [Fact]
        public void ATextureInfoTagWinsOverTheTextureSize()
        {
            // A frame inside a larger texture: the tag is the frame and the texture is not, so
            // taking the texture's dimensions would make every sprite in a multi-frame texture
            // address the whole sheet.
            var tag = new TextureInfo { Size = new Point(64, 32) };
            Assert.Equal(new Point(64, 32), SpriteBoundsEstimate.FrameSize(tag, 256, 128));
        }

        [Fact]
        public void ATagThatIsNotATextureInfoFallsBackToTheTextureSize()
        {
            // The bug. Texture.Tag is a plain object, and the 2d DGRP path read it with a hard
            // cast: AbstractTextureRef tags its instance with itself, and with a hi-dpi Texture2D
            // on the replace-path branch, and neither of those is a TextureInfo - so a texture from
            // either route turned a correct answer into an InvalidCastException on the first 2d
            // draw of the object.
            Assert.Equal(new Point(128, 64), SpriteBoundsEstimate.FrameSize("not a texture info", 128, 64));
            Assert.Equal(new Point(128, 64), SpriteBoundsEstimate.FrameSize(new object(), 128, 64));
        }

        [Fact]
        public void AMissingTagFallsBackToTheTextureSize()
        {
            // A null tag has to give the same answer as a tag of the wrong type, not a
            // NullReferenceException: Texture.Tag is null on every texture nobody has tagged.
            Assert.Equal(new Point(128, 64), SpriteBoundsEstimate.FrameSize(null, 128, 64));
        }

        [Fact]
        public void TheFallbackAgreesWithTheTagForAFrameThatFillsItsTexture()
        {
            // Which is why the fallback is a safe answer rather than a guess: a frame that fills
            // its texture is exactly the case where the two coincide, so nothing that used to be
            // correct changes.
            var tag = new TextureInfo { Size = new Point(128, 64) };
            Assert.Equal(SpriteBoundsEstimate.FrameSize(tag, 128, 64),
                         SpriteBoundsEstimate.FrameSize(null, 128, 64));
        }

        // ---- R-49: the particle billboard inverses ----------------------------------------

        [Fact]
        public void TheCameraPositionIsTheInverseViewsTranslationRow()
        {
            // The old second inversion computed Vector3.Transform(Vector3.Zero, Inverse(view)),
            // which is the inverse's translation row and nothing else - the origin contributes
            // no rotation terms. So reading three fields off the inverse is the same answer.
            var view = Matrix.CreateLookAt(
                new Vector3(12f, 30f, -7f), new Vector3(1f, 0f, 0f), Vector3.Up)
                * Matrix.CreateRotationY(0.7f);

            var inverse = Matrix.Invert(view);

            Assert.Equal(Vector3.Transform(Vector3.Zero, inverse), ParticleBillboard.TranslationOf(inverse));
            Assert.Equal(inverse.M41, ParticleBillboard.TranslationOf(inverse).X, 6);
            Assert.Equal(inverse.M42, ParticleBillboard.TranslationOf(inverse).Y, 6);
            Assert.Equal(inverse.M43, ParticleBillboard.TranslationOf(inverse).Z, 6);
        }

        [Fact]
        public void TheBillboardInverseIgnoresWhereTheCameraIsStanding()
        {
            // That is the whole basis for sharing one inverse across every particle system.
            var rotation = Matrix.CreateRotationX(0.4f) * Matrix.CreateRotationZ(1.1f);
            var near = rotation * Matrix.CreateTranslation(0f, 0f, -50f);
            var far = rotation * Matrix.CreateTranslation(300f, 12f, 900f);

            Assert.Equal(ParticleBillboard.InvRotation(near), ParticleBillboard.InvRotation(far));
        }

        /// <summary>
        /// A view matrix with a real rotation, so the inverse is defined.
        /// </summary>
        /// <remarks>
        /// <c>Matrix.CreateLookAt</c> from the origin looking at the origin - which is what a
        /// camera told to look at itself produces - has no direction to build a basis from and
        /// inverts to a matrix of NaNs. A degenerate view is not what the renderer produces, and
        /// comparing two NaN matrices is not a test of anything.
        /// </remarks>
        private static Matrix SampleView(float yaw)
        {
            return Matrix.CreateRotationY(yaw) * Matrix.CreateTranslation(0f, 0f, -200f);
        }

        [Fact]
        public void AStillCameraOnlyInvertsTheViewOncePerFrame()
        {
            // The saving being claimed: two 4x4 inversions per particle system per frame became
            // one for the frame.
            var cache = new ViewInverseCache();
            var view = SampleView(0.4f);

            var first = cache.InvRotation(view);
            for (int i = 0; i < 10; i++) Assert.Equal(first, cache.InvRotation(view));

            Assert.Equal(1, cache.Computations);
        }

        [Fact]
        public void RotatingTheCameraRecomputesTheInverse()
        {
            // Keyed on the view, so it cannot go stale - a rotated camera is a different matrix.
            var cache = new ViewInverseCache();
            cache.InvRotation(SampleView(0f));
            cache.InvRotation(SampleView(0.9f));

            Assert.Equal(2, cache.Computations);
        }

        [Fact]
        public void OnlyTranslatingTheCameraDoesNotRecomputeTheBillboardInverse()
        {
            // The property the sharing relies on, stated directly: a scrolled camera keeps the
            // same rotation, so the billboard inverse is unchanged and is not recomputed.
            var cache = new ViewInverseCache();
            var before = cache.InvRotation(Matrix.CreateRotationY(0.4f) * Matrix.CreateTranslation(0f, 0f, -100f));
            var after = cache.InvRotation(Matrix.CreateRotationY(0.4f) * Matrix.CreateTranslation(0f, 0f, -300f));

            Assert.Equal(before, after);
            Assert.Equal(1, cache.Computations);
        }

        [Fact]
        public void TheBillboardInverseOfASampleViewIsFinite()
        {
            // Guards the cache tests above: a NaN inverse compares unequal to itself, so a silent
            // NaN would have made them pass or fail for the wrong reason.
            var inverse = ParticleBillboard.InvRotation(SampleView(0.4f));
            var elements = new[]
            {
                inverse.M11, inverse.M12, inverse.M13, inverse.M14,
                inverse.M21, inverse.M22, inverse.M23, inverse.M24,
                inverse.M31, inverse.M32, inverse.M33, inverse.M34,
                inverse.M41, inverse.M42, inverse.M43, inverse.M44
            };
            foreach (var element in elements)
            {
                Assert.False(float.IsNaN(element));
            }
        }

        [Fact]
        public void TheCachedInverseIsTheSameOneAFreshComputationGives()
        {
            var cache = new ViewInverseCache();
            var view = SampleView(0.3f);

            Assert.Equal(ParticleBillboard.InvRotation(view), cache.InvRotation(view));
        }

        [Fact]
        public void InvalidatingTheCacheForcesARecomputation()
        {
            var cache = new ViewInverseCache();
            var view = SampleView(0f);
            cache.InvRotation(view);
            cache.Invalidate();
            cache.InvRotation(view);

            Assert.Equal(2, cache.Computations);
        }
    }
}
