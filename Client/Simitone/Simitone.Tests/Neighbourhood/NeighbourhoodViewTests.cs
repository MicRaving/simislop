/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using FSO.Content.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The map-to-world correspondence, the camera that consumes it, and the decisions that pick
    /// between the 2D bitmap and the generated 3D world. §11's <c>NeighbourhoodViewTests</c>.
    ///
    /// <para>
    /// All of this is arithmetic, which is the point: the 2D and 3D views have to agree about
    /// where a lot is, and the only way to know they do without a screen is to derive both from
    /// the same numbers. Nothing here touches a graphics device, a content provider or a file.
    /// </para>
    /// </summary>
    public class NeighbourhoodViewTests
    {
        // ---- the setting ----------------------------------------------------------------------

        [Fact]
        public void TheDefaultIsThePaintedMap()
        {
            // The connected-mechanics rework moved this default to 3D and broke the working modes;
            // the revert puts the painted map back as what a player who has changed nothing gets.
            // The 3D world is still the third step of the same three-way row.
            Assert.Equal(NeighbourhoodView.Bitmap2D, NeighbourhoodViewSetting.Default);
            Assert.Equal(NeighbourhoodView.Bitmap2D, NeighbourhoodViewSetting.Resolve(null));

            var selection = NeighbourhoodViewSelection.ForSetting(NeighbourhoodViewSetting.Default);
            Assert.False(selection.Use3D);
            Assert.True(selection.UseBitmap2D);
        }

        [Fact]
        public void EveryStepIsStillReachableFromThe2DDefault()
        {
            // Moving the default must not have taken a step away. All three still resolve to
            // themselves, and nothing in the store is special-cased on which one is the default.
            foreach (var view in NeighbourhoodViewSetting.All)
            {
                Assert.Equal(view, NeighbourhoodViewSetting.Resolve(view));
                Assert.Equal(view, NeighbourhoodViewSetting.Resolve(NeighbourhoodViewSetting.TokenFor(view)));
            }

            Assert.NotEqual(NeighbourhoodViewSetting.Default, NeighbourhoodView.Off);
            Assert.NotEqual(NeighbourhoodViewSetting.Default, NeighbourhoodView.World3D);

            // and the panel's answer changes with it: picking 3D from a 2D install really does
            // turn the generated world on, rather than being ignored as "the default"
            Assert.False(NeighbourhoodViewSelection.ForSetting(NeighbourhoodViewSetting.Default).Use3D);
            Assert.True(NeighbourhoodViewSelection.ForSetting(NeighbourhoodView.World3D).Use3D);
            Assert.False(NeighbourhoodViewSelection.ForSetting(NeighbourhoodView.Off).Use3D);
        }

        [Theory]
        [InlineData("off", NeighbourhoodView.Off)]
        [InlineData("OFF", NeighbourhoodView.Off)]
        [InlineData("  off  ", NeighbourhoodView.Off)]
        [InlineData("2d", NeighbourhoodView.Bitmap2D)]
        [InlineData("2D", NeighbourhoodView.Bitmap2D)]
        [InlineData("3d", NeighbourhoodView.World3D)]
        [InlineData("3D", NeighbourhoodView.World3D)]
        public void AStoredTokenResolvesToItsView(string stored, NeighbourhoodView expected)
        {
            Assert.Equal(expected, NeighbourhoodViewSetting.Resolve(stored));
            Assert.Equal(stored.Trim().ToLowerInvariant(), NeighbourhoodViewSetting.TokenFor(expected));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("maybe")]
        [InlineData("4")]
        [InlineData("-1")]
        [InlineData("true")]
        public void AStoredValueNobodyChoseIsTheDefault(string stored)
        {
            // This is user input in a hand-editable ini, so garbage has to be a known-good answer
            // rather than an exception at startup - and "the known-good answer" is the default
            // rather than a step named literally, so the rule stays true whichever step the
            // default happens to be.
            Assert.Equal(NeighbourhoodViewSetting.Default, NeighbourhoodViewSetting.Resolve(stored));
        }

        [Fact]
        public void ANumericStoredValueResolvesLikeItsEnum()
        {
            // A player who has read the enum will type a number. Being forgiving about a closed
            // three-value set costs nothing and saves a confused support question.
            Assert.Equal(NeighbourhoodView.Off, NeighbourhoodViewSetting.Resolve("0"));
            Assert.Equal(NeighbourhoodView.Bitmap2D, NeighbourhoodViewSetting.Resolve("1"));
            Assert.Equal(NeighbourhoodView.World3D, NeighbourhoodViewSetting.Resolve("2"));
        }

        [Fact]
        public void AnOutOfRangeEnumFallsBackToTheDefault()
        {
            // So a cast of arbitrary memory cannot reach the renderer as a view nothing handles.
            Assert.Equal(NeighbourhoodViewSetting.Default,
                NeighbourhoodViewSetting.Resolve((NeighbourhoodView)77));
            Assert.Equal(NeighbourhoodViewSetting.Default,
                NeighbourhoodViewSetting.Resolve((NeighbourhoodView)(-3)));
        }

        [Fact]
        public void TheSettingRoundTripsThroughItsToken()
        {
            foreach (var view in NeighbourhoodViewSetting.All)
            {
                Assert.Equal(view, NeighbourhoodViewSetting.Resolve(NeighbourhoodViewSetting.TokenFor(view)));
            }
            Assert.Equal(3, NeighbourhoodViewSetting.All.Count);
        }

        [Fact]
        public void EveryViewHasALabelAndTheSettingHasAnExplanation()
        {
            foreach (var view in NeighbourhoodViewSetting.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(NeighbourhoodViewSetting.Describe(view)));
            }
            // the tooltip has to name both the default and the step that is still there to pick,
            // or a player reading it cannot tell which one they are currently on
            Assert.Contains("3D", NeighbourhoodViewSetting.DescribeEffect());
            Assert.Contains("2D", NeighbourhoodViewSetting.DescribeEffect());
            Assert.True(NeighbourhoodViewSetting.Describes("NeighbourhoodView"));
            Assert.False(NeighbourhoodViewSetting.Describes("SunCycle"));
            Assert.False(NeighbourhoodViewSetting.Describes(null));
        }

        [Fact]
        public void TheSettingTravelsOnTheGraphicsPreferencesSnapshot()
        {
            // The same rule every other graphics option follows: what the player chose reaches the
            // renderer through GraphicsPreferences.From, not by being read off the ini at the point
            // of use. This is the test that keeps that true as options are added.
            var preferences = GraphicsPreferences.From(null, true, NeighbourhoodView.World3D);
            Assert.Equal(NeighbourhoodView.World3D, preferences.NeighbourhoodView);
            Assert.True(preferences.SunCycle);

            Assert.Equal(NeighbourhoodView.Off, preferences.WithNeighbourhoodView(NeighbourhoodView.Off).NeighbourhoodView);
            Assert.Equal(NeighbourhoodView.Off, GraphicsPreferences.From(null, true, NeighbourhoodView.Off).NeighbourhoodView);

            // and WithSunCycle must not disturb it, which is the mistake a new field invites
            var kept = preferences.WithSunCycle(false);
            var keptView = kept.NeighbourhoodView;
            Assert.Equal(NeighbourhoodView.World3D, keptView);
            Assert.False(kept.SunCycle);
            Assert.False(kept.SunCycle);
        }

        [Fact]
        public void TheDefaultsSnapshotIsThePaintedMap()
        {
            // GraphicsOptions.Defaults is "what an untouched install gets", so it is the last place
            // a 3D-by-default regression would hide - and, just as much, the place a
            // back-to-2D regression would hide.
            Assert.Equal(NeighbourhoodViewSetting.Default, GraphicsOptions.Defaults.NeighbourhoodView);
            Assert.Equal(NeighbourhoodView.Bitmap2D, GraphicsOptions.Defaults.NeighbourhoodView);
        }

        // ---- which view gets drawn -------------------------------------------------------------

        [Fact]
        public void TheSettingOffMeansTheBitmapAndNothingElseHappens()
        {
            var selection = NeighbourhoodViewSelection.ForSetting(NeighbourhoodView.Off);

            Assert.False(selection.Use3D);
            Assert.True(selection.UseBitmap2D);
            Assert.Equal(NeighbourhoodView.Off, selection.Requested);
            Assert.Contains("switched off", selection.Reason);
        }

        [Fact]
        public void TheSettingTwoDMeansTheBitmap()
        {
            var selection = NeighbourhoodViewSelection.ForSetting(NeighbourhoodView.Bitmap2D);

            Assert.False(selection.Use3D);
            Assert.Contains("2D map is selected", selection.Reason);
        }

        [Fact]
        public void ThreeDRequestedAndBuiltIsThreeD()
        {
            var map = NeighbourhoodMap.Create(64);
            var selection = NeighbourhoodViewSelection.AfterGeneration(
                NeighbourhoodView.World3D, map, null);

            Assert.True(selection.Use3D);
            Assert.False(selection.UseBitmap2D);
            Assert.Equal(NeighbourhoodView.World3D, selection.Requested);
            Assert.Contains("64x64", selection.Reason);
        }

        [Fact]
        public void ThreeDRequestedAndFailedIsTheBitmapWithTheReason()
        {
            // The plan's non-negotiable: a neighbourhood that cannot be generated still opens, as
            // the map it has always opened as. The reason has to survive, because a player who
            // asked for 3D and got 2D needs to be able to find out why from the log.
            var failed = NeighbourhoodViewSelection.AfterGeneration(
                NeighbourhoodView.World3D, null, "the road graph could not be made connected");
            Assert.True(failed.UseBitmap2D);
            Assert.Contains("road graph", failed.Reason);

            var noMap = NeighbourhoodViewSelection.AfterGeneration(NeighbourhoodView.World3D, null, null);
            Assert.True(noMap.UseBitmap2D);
            Assert.Contains("no map was produced", noMap.Reason);
        }

        [Fact]
        public void AFailureIsIrrelevantWhen3DWasNotAskedFor()
        {
            // A 2D neighbourhood that happens to have a generation failure recorded must still be
            // reported as 2D for the reason the player chose, not the reason something went wrong
            // behind their back.
            foreach (var view in new[] { NeighbourhoodView.Off, NeighbourhoodView.Bitmap2D })
            {
                var selection = NeighbourhoodViewSelection.AfterGeneration(view, null, "something went wrong");
                Assert.True(selection.UseBitmap2D);
                Assert.DoesNotContain("something went wrong", selection.Reason);
            }
        }

        [Fact]
        public void EverySelectionExplainsItself()
        {
            // A fallback with no stated reason is the case worth chasing, so there is no path
            // through this type that produces one.
            var selections = new[]
            {
                NeighbourhoodViewSelection.ForSetting(NeighbourhoodView.Off),
                NeighbourhoodViewSelection.ForSetting(NeighbourhoodView.Bitmap2D),
                NeighbourhoodViewSelection.AfterGeneration(NeighbourhoodView.World3D, null, "nope"),
                NeighbourhoodViewSelection.ThreeD(NeighbourhoodView.World3D, "built")
            };
            foreach (var selection in selections)
            {
                Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
                Assert.False(string.IsNullOrWhiteSpace(selection.ToString()));
            }
        }

        // ---- the map to world mapping --------------------------------------------------------

        /// <summary>Four lots with a 2x2 block of bitmap positions, placed by the real builder.</summary>
        /// <remarks>
        /// The bitmap coordinates are chosen so the block's centre is the map's centre, which is
        /// what lets the full-screen camera test below frame the whole map. Since Part 8 a lot's
        /// tile position is its bitmap position scaled by the map size, so the arrangement is
        /// absolute: (300, 200) and (500, 400) are the four corners of a block centred on the
        /// bitmap's own centre, and the builder puts their rectangles either side of the map's
        /// centre in both axes.
        /// </remarks>
        private static NeighbourhoodLayout PackedLayout()
        {
            var reads = new[]
            {
                Read(11, 300, 200, 16),
                Read(12, 500, 200, 16),
                Read(13, 300, 400, 16),
                Read(14, 500, 400, 16)
            };
            return NeighbourhoodLayoutBuilder.Pack(reads, 4, 12345);
        }

        private static NeighbourhoodLotRead Read(short number, int mapX, int mapY, int size)
        {
            return new NeighbourhoodLotRead
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
                MapX = mapX,
                MapY = mapY,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = 0x1000u + (uint)number,
                HasHouse = true
            };
        }

        [Fact]
        public void TheMappingAgreesWithTheLayoutOnWhereALotLands()
        {
            // This is §11's "map↔world mapping agrees with the layout", and it is the test that
            // makes the 3D view a picture of the same neighbourhood the 2D view is. Since Part 8 the
            // mapping IS the transform the layout builder places lots with, so this is no longer
            // "equal within the slack the packer was allowed" - it is exact, to the half tile that
            // separates a rectangle's centre from its origin.
            var layout = PackedLayout();
            var mapping = NeighbourhoodWorldMapping.For(layout);

            foreach (var lot in layout.Lots)
            {
                var mapped = mapping.BitmapToWorldTile(lot.MapX, lot.MapY);

                //the lot's own rectangle centre, which is what the camera aims at and what the
                //bitmap position means; the layout's origin is half a lot back from it
                Assert.InRange(mapped.X, lot.WorldX, lot.WorldX + lot.Size);
                Assert.InRange(mapped.Y, lot.WorldY, lot.WorldY + lot.Size);
                Assert.Equal(lot.WorldX + lot.Size / 2, mapped.X);
                Assert.Equal(lot.WorldY + lot.Size / 2, mapped.Y);
            }
        }

        [Fact]
        public void TheMappingIsExactForASingleLot()
        {
            // One lot, centred on the bitmap, placed away from the edges: the case with nothing to
            // clamp and nothing to adjust. This is the one that would catch a change to the
            // transform itself.
            var layout = NeighbourhoodLayoutBuilder.Pack(new[] { Read(21, 400, 300, 16) }, 4, 7);
            var mapping = NeighbourhoodWorldMapping.For(layout);
            var lot = layout.FindLot(21);

            var mapped = mapping.BitmapToWorldTile(400, 300);
            Assert.Equal(lot.WorldX + lot.Size / 2, mapped.X);
            Assert.Equal(lot.WorldY + lot.Size / 2, mapped.Y);
            //The bitmap's centre is the middle of the drawn region, which is half a tile above the middle
            //of the square. CHANGED by the Part 9 TSO-alignment rework, which put the bitmap's own
            //isometric projection back; this used to assert the square's centre on both axes.
            Assert.Equal(layout.MapSize / 2, mapped.X);
            Assert.Equal((int)Math.Floor(
                NeighbourhoodPixelTransform.CentreFor(layout.MapSize).Y), mapped.Y);
        }

        [Fact]
        public void TheMappingAgreesWithTheLayoutForOverlappingLots()
        {
            // The case the packer's relaxation used to answer for: several lots whose bitmap
            // positions are close enough that their rectangles overlap. Since Part 8 that is not a
            // problem to be resolved - it is TSO's own Size-2 pitch - and the mapping has to agree
            // with the layout for every one of them anyway, which is why this is a test at all.
            var reads = new[]
            {
                Read(11, 380, 290, 48),
                Read(12, 390, 295, 48),
                Read(13, 420, 320, 48)
            };
            var layout = NeighbourhoodLayoutBuilder.Pack(reads, 4, 999);
            var mapping = NeighbourhoodWorldMapping.For(layout);

            foreach (var lot in layout.Lots)
            {
                var mapped = mapping.BitmapToWorldTile(lot.MapX, lot.MapY);
                Assert.Equal(lot.WorldX + lot.Size / 2, mapped.X);
                Assert.Equal(lot.WorldY + lot.Size / 2, mapped.Y);
            }
        }

        [Fact]
        public void TheMappingIsExactlyReversible()
        {
            // Bitmap to world and back. A view that pans by a bitmap delta and draws by a world
            // position cannot afford an approximation here, or the world and the buttons drift
            // apart as the player moves.
            var mapping = NeighbourhoodWorldMapping.For(PackedLayout());

            foreach (var point in new[] { new Vector2(0, 0), new Vector2(20, 20), new Vector2(63, 71), new Vector2(127, 127) })
            {
                var world = mapping.BitmapToWorld((int)point.X, (int)point.Y);
                var back = mapping.WorldToBitmap(world.X, world.Y);
                Assert.Equal(point.X, back.X, 2);
                Assert.Equal(point.Y, back.Y, 2);
            }
        }

        [Fact]
        public void TheMappingGrowsTheRightWayRound()
        {
            // Growing the bitmap x must grow world x and growing the bitmap y must grow world y.
            // Getting a sign wrong here mirrors the neighbourhood, which is the kind of bug that
            // looks like a layout problem and is not.
            //
            // The shear is back (CHANGED by the Part 9 TSO-alignment rework, which put the
            // bitmap's own isometric projection back because the map's tile grid is drawn at 45
            // degrees): a step right on the bitmap is a step right-and-up in the world and a step
            // down it is a step right-and-down. This used to assert the opposite - that the two
            // axes were separable - which is what an axis-aligned square mapping gives, and what
            // put lots in the diamond's four cut corners with no ground under them.
            var mapping = NeighbourhoodWorldMapping.For(PackedLayout());

            var origin = mapping.BitmapToWorld(300, 300);
            var right = mapping.BitmapToWorld(320, 300);
            var down = mapping.BitmapToWorld(300, 320);

            Assert.True(right.X > origin.X);
            Assert.True(right.Y < origin.Y);
            Assert.True(down.X > origin.X);
            Assert.True(down.Y > origin.Y);
        }

        [Fact]
        public void AnEmptyLayoutStillMaps()
        {
            // A neighbourhood whose location table named no lots has to produce a usable camera
            // rather than a divide by zero, because "no lots" is a thing a player can do.
            var mapping = NeighbourhoodWorldMapping.For(NeighbourhoodLayout.Empty(4, 1, 1));

            var world = mapping.BitmapToWorld(400, 300);
            Assert.False(float.IsNaN(world.X));
            Assert.False(float.IsInfinity(world.X));
            //With no layout to size it from, the mapping is the default map's, and the bitmap's
            //centre is that map's drawn region's centre - the square's centre in x and half a tile
            //above it in y, because the isometric diamond's two diagonals are 411 and 613 rather
            //than equal. CHANGED by the Part 9 TSO-alignment rework; this used to assert the
            //square's centre on both axes.
            var drawn = NeighbourhoodPixelTransform.CentreFor(NeighbourhoodLayout.DefaultMapSize);
            Assert.Equal(drawn.X, world.X, 3);
            Assert.Equal(drawn.Y, world.Y, 3);
            Assert.Equal(NeighbourhoodLayout.DefaultMapSize / 2f, world.X, 3);
        }

        [Fact]
        public void ASingleLotLandsWhereItsBitmapPositionSays()
        {
            // The degenerate cloud: one lot, and nothing to compare it against but the transform.
            // There is no fit and no degenerate span any more, so the lot simply goes where
            // LotLocations.iff put it - which on a centred bitmap position is the map's centre.
            var layout = NeighbourhoodLayoutBuilder.Pack(new[] { Read(21, 400, 300, 16) }, 4, 7);
            var mapping = NeighbourhoodWorldMapping.For(layout);
            var lot = layout.FindLot(21);

            var mapped = mapping.BitmapToWorldTile(400, 300);
            Assert.Equal(lot.WorldX + lot.Size / 2, mapped.X);
            Assert.Equal(lot.WorldY + lot.Size / 2, mapped.Y);
            Assert.Equal(layout.MapSize / 2, mapped.X);
        }

        // ---- the camera -----------------------------------------------------------------------

        [Fact]
        public void FullScreenFramesTheWholeNeighbourhood()
        {
            var layout = PackedLayout();
            var mapping = NeighbourhoodWorldMapping.For(layout);

            var placement = NeighbourhoodCameraPlacement.For(
                NeighCameraMachine.FullScreen(), mapping, layout, layout.MapSize,
                NeighbourhoodCameraPlacement.DefaultAspect, 4f);

            //the camera is above and behind its target, looking down at it
            Assert.True(placement.Position.Y > placement.Target.Y);
            Assert.NotEqual(Vector3.Zero, placement.Position);

            //and the view is wide enough to hold the map: the diamond's *diagonal* along the
            //screen-x axis, which is what the 45-degree yaw puts there
            Assert.Equal(NeighbourhoodCameraPlacement.VisibleWidthFor(layout.MapSize), placement.TilesAcross, 3);
            var expected = NeighbourhoodCameraPlacement.DistanceFor(
                NeighbourhoodCameraPlacement.VisibleWidthFor(layout.MapSize),
                NeighbourhoodCameraPlacement.DefaultAspect,
                NeighbourhoodCameraPlacement.FieldOfViewRadians);
            Assert.Equal(expected, placement.Distance, 3);

            //and "wide enough" is not a claim, it is the property: every tile corner of the real
            //map is inside this camera's frustum. Framing the map's *side* instead of its diagonal
            //puts a few thousand of them outside and culls the four points of the diamond, which is
            //how a full-screen view of the neighbourhood ends up showing most of it.
            var frustum = placement.Frustum(1f, 8000f, NeighbourhoodCameraPlacement.DefaultAspect);
            var outside = 0;
            var corners = 0;
            for (int y = 0; y < layout.MapSize; y += 8)
            {
                var left = Math.Max(NeighbourhoodCityMeshBuilder.DiamondLeft(y)
                    - NeighbourhoodCityMeshBuilder.FadeRange, 0);
                var right = Math.Min(NeighbourhoodCityMeshBuilder.DiamondRight(y)
                    + NeighbourhoodCityMeshBuilder.FadeRange, layout.MapSize);
                for (var x = left; x <= right; x += 16)
                {
                    corners++;
                    if (frustum.Contains(new Vector3(x, 0f, y)) == ContainmentType.Disjoint) outside++;
                    if (frustum.Contains(new Vector3(x, 0f, y + 1)) == ContainmentType.Disjoint) outside++;
                }
            }
            Assert.Equal(0, outside);
            Assert.True(corners > 1000, "the corner sample is too sparse to mean anything");

            //and so the renderer's cull keeps the whole map
            var visible = new List<int>();
            NeighbourhoodCityMeshBuilder.ChunksInFrustum(frustum, visible);
            Assert.Equal(NeighbourhoodCityMeshBuilder.ChunkCount, visible.Count);
        }

        [Fact]
        public void ZoomingInBringsTheCameraCloserAndNothingElse()
        {
            // The zoom scalar is carried across from NeighCameraMachine untouched and spent on
            // distance. If it were reinterpreted - re-scaled, or applied to a target instead - the
            // 2D and 3D zooms would not be the same zoom, which is the whole point of driving the
            // 3D view off the same machine.
            var layout = PackedLayout();
            var mapping = NeighbourhoodWorldMapping.For(layout);

            var wide = NeighbourhoodCameraPlacement.For(NeighCameraMachine.FullScreen(), mapping, layout,
                layout.MapSize, NeighbourhoodCameraPlacement.DefaultAspect, 4f);
            var close = NeighbourhoodCameraPlacement.For(
                NeighCameraMachine.HouseFocus(11, new Vector2(20, 20), 1), mapping, layout,
                layout.MapSize, NeighbourhoodCameraPlacement.DefaultAspect, 4f);

            Assert.True(close.Distance < wide.Distance);
            Assert.Equal(wide.Distance / NeighCameraMachine.HouseFocusZoom, close.Distance, 3);
        }

        [Fact]
        public void UnleashedsBiggerZoomIsBiggerInThreeDToo()
        {
            // Mode 4 draws its map at half size, so its house-focus zoom is 3 rather than 1.5. If
            // the 3D view ignored that it would zoom in less far on Unleashed than the 2D view
            // does, and the two would disagree on the map that most players see.
            var layout = PackedLayout();
            var mapping = NeighbourhoodWorldMapping.For(layout);

            var normal = NeighbourhoodCameraPlacement.For(
                NeighCameraMachine.HouseFocus(11, new Vector2(20, 20), 1), mapping, layout,
                layout.MapSize, NeighbourhoodCameraPlacement.DefaultAspect, 4f);
            var unleashed = NeighbourhoodCameraPlacement.For(
                NeighCameraMachine.HouseFocus(11, new Vector2(20, 20), NeighCameraMachine.UnleashedMode),
                mapping, layout, layout.MapSize, NeighbourhoodCameraPlacement.DefaultAspect, 4f);

            Assert.True(unleashed.Distance < normal.Distance);
            Assert.Equal(normal.Distance / (NeighCameraMachine.UnleashedHouseFocusZoom / NeighCameraMachine.HouseFocusZoom),
                unleashed.Distance, 2);
        }

        [Fact]
        public void FocusingALotLooksAtWhereItWasActuallyPacked()
        {
            // Not at where the projection says it should be. The packer moves lots to resolve
            // overlaps, so the projection is what lines the two views up and the packed rectangle
            // is what the camera looks at. A camera that followed the projection would point at
            // bare ground whenever the relaxation had done anything.
            var layout = PackedLayout();
            var mapping = NeighbourhoodWorldMapping.For(layout);
            var lot = layout.FindLot(13);

            var focus = NeighbourhoodCameraPlacement.ResolveFocus(13, mapping, layout, layout.MapSize, null);
            var packed = NeighbourhoodWorldMapping.PackedCentre(lot);
            Assert.Equal(packed.X, focus.X);
            Assert.Equal(packed.Y, focus.Y);
        }

        [Fact]
        public void FocusingALotTheLayoutDoesNotKnowUsesItsBitmapPosition()
        {
            // A lot the player can select but this map's location table did not name. There is no
            // packed rectangle to aim at, and the projection is still a far better guess than the
            // middle of the map - it at least lands inside the neighbourhood.
            var layout = PackedLayout();
            var mapping = NeighbourhoodWorldMapping.For(layout);

            var focus = NeighbourhoodCameraPlacement.ResolveFocus(99, mapping, layout, layout.MapSize, new Vector2(20, 20));
            var projected = mapping.BitmapToWorld(20, 20);
            Assert.Equal(projected.X, focus.X, 3);
            Assert.Equal(projected.Y, focus.Y, 3);
        }

        [Fact]
        public void FocusingWithNothingToGoOnLooksAtTheNeighbourhoodsCentre()
        {
            var layout = PackedLayout();
            var mapping = NeighbourhoodWorldMapping.For(layout);

            var focus = NeighbourhoodCameraPlacement.ResolveFocus(null, mapping, layout, layout.MapSize, null);
            Assert.Equal(layout.Extent.CenterX, focus.X);
            Assert.Equal(layout.Extent.CenterY, focus.Y);
        }

        [Fact]
        public void TheDistanceMathsFramesWhatItIsGiven()
        {
            // Twice the width is half the distance, and a wider viewport is closer: both are what
            // "fit this many tiles on screen" has to mean, and both are cheap to get wrong in a
            // way that only shows on one aspect ratio.
            var one = NeighbourhoodCameraPlacement.DistanceFor(512, 4f / 3f, NeighbourhoodCameraPlacement.FieldOfViewRadians);
            var two = NeighbourhoodCameraPlacement.DistanceFor(1024, 4f / 3f, NeighbourhoodCameraPlacement.FieldOfViewRadians);
            // twice as much to fit is twice as far back, and that linearity is the whole reason
            // the zoom can be spent on distance
            Assert.Equal(one * 2f, two, 2);

            var wide = NeighbourhoodCameraPlacement.DistanceFor(512, 16f / 9f, NeighbourhoodCameraPlacement.FieldOfViewRadians);
            var square = NeighbourhoodCameraPlacement.DistanceFor(512, 1f, NeighbourhoodCameraPlacement.FieldOfViewRadians);
            Assert.True(wide < square);
        }

        [Fact]
        public void TheDistanceMathsSurvivesNonsense()
        {
            // A zero width, a zero aspect and a zero field of view all have to give a usable
            // camera, because they all arrive from a graphics device that has just been reset.
            Assert.True(NeighbourhoodCameraPlacement.DistanceFor(0, 4f / 3f, 0.7f) > 0);
            Assert.True(NeighbourhoodCameraPlacement.DistanceFor(512, 0, 0.7f) > 0);
            Assert.True(NeighbourhoodCameraPlacement.DistanceFor(512, 4f / 3f, 0) > 0);
            Assert.True(NeighbourhoodCameraPlacement.DistanceFor(512, 4f / 3f, 3.5f) > 0);

            var placement = NeighbourhoodCameraPlacement.Framing(Vector3.Zero, 0f, 0f);
            Assert.True(placement.Distance > 0);
        }

        [Fact]
        public void TheCameraOrbitIsTheOneItSaysItIs()
        {
            var target = new Vector3(100, 4, 200);
            var placement = NeighbourhoodCameraPlacement.Framing(target, 512, 4f / 3f);

            var offset = placement.Position - target;
            Assert.Equal(placement.Distance, offset.Length(), 3);
            Assert.Equal(placement.Distance * (float)Math.Sin(placement.Pitch), Math.Abs(offset.Y), 3);
            // yaw of 45 degrees puts the camera on the diagonal, which is what makes the isometric
            // diamond read as a square rather than a lozenge
            Assert.Equal(Math.Abs(offset.X), Math.Abs(offset.Z), 3);
            Assert.Equal(target, placement.Target);
        }

        [Fact]
        public void TheCameraLooksAtTheGroundItWasGiven()
        {
            // A hilly neighbourhood viewed from below its own horizon is the failure mode here, and
            // the ground height is the only thing that stops it.
            var layout = PackedLayout();
            var mapping = NeighbourhoodWorldMapping.For(layout);

            var flat = NeighbourhoodCameraPlacement.For(NeighCameraMachine.FullScreen(), mapping, layout,
                layout.MapSize, 4f / 3f, 0f);
            var raised = NeighbourhoodCameraPlacement.For(NeighCameraMachine.FullScreen(), mapping, layout,
                layout.MapSize, 4f / 3f, 8f);

            Assert.Equal(0f, flat.Target.Y, 3);
            Assert.Equal(8f, raised.Target.Y, 3);
            Assert.Equal(raised.Distance, flat.Distance, 3);
        }

        // ---- the layout to map seam -----------------------------------------------------------

        [Fact]
        public void AltitudesAreQuantisedOntoTheCityScale()
        {
            // The seam multiplies by nine quarters of an elevation byte per ARRY(0) unit, and the
            // arithmetic that gets there is three separate facts:
            //   * a tile is twelve elevation bytes, because the city mesh divides ElevationData by
            //     twelve (CityGeometry.RegenMeshVerts, Terrain's GetElevationVert);
            //   * Blueprint.TerrainFactor is 3/160, so one blueprint altitude unit is 3/160 of a
            //     tile - not 1/12, which is the whole trap;
            //   * VMTS1Activator sets Blueprint.Altitude = ARRY(0) * 10, so one ARRY(0) unit is
            //     ten altitude units and therefore 10 * 3/160 = 3/16 of a tile.
            // 12 bytes/tile * 3/16 tile/unit = 12 * 0.1875 = 2.25 bytes per ARRY(0) unit. Twelve is
            // the *tile* scale and is still the right answer for ElevationUnitsPerTile; it is only
            // wrong as the per-unit multiplier, where it is 12 / 2.25 = 16/3 = 5.33x too large and
            // every lot's ground ends up stamped five and a third times too high.
            Assert.Equal(12, NeighbourhoodMapAdapter.ElevationUnitsPerTile);
            // the two spellings have to compose: 12 bytes/tile / 2.25 bytes per unit = 16/3 units
            // in a tile, and at 3/16 of a tile each that is exactly one tile
            Assert.Equal(16f / 3f, NeighbourhoodMapAdapter.ElevationUnitsPerTile / 2.25f, 4);

            Assert.Equal(0, NeighbourhoodMapAdapter.QuantiseAltitude(0));
            // 4 ARRY units = 4 * 2.25 = 9 bytes, not 4 * 12 = 48
            Assert.Equal(9, NeighbourhoodMapAdapter.QuantiseAltitude(4));
            // 20 ARRY units = 20 * 2.25 = 45 bytes, not 240
            Assert.Equal(45, NeighbourhoodMapAdapter.QuantiseAltitude(20));
            // and it is the mesh builder's own function, so there is no second spelling to drift
            Assert.Equal(NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(4),
                NeighbourhoodMapAdapter.QuantiseAltitude(4));
        }

        [Fact]
        public void AnImpossibleAltitudeClampsRatherThanWrapping()
        {
            // A byte cannot hold a 40-tile cliff, and a wrapped value would be a cliff on the
            // other side of the world - which is a much worse bug than a flattened one.
            // At 2.25 bytes per unit the ceiling is 255 / 2.25 = 113.3 ARRY units, so 40 is now a
            // perfectly ordinary altitude (40 * 2.25 = 90) and the clamp starts above 113.
            Assert.Equal(0, NeighbourhoodMapAdapter.QuantiseAltitude(-5));
            Assert.Equal(90, NeighbourhoodMapAdapter.QuantiseAltitude(40));
            Assert.Equal(255, NeighbourhoodMapAdapter.QuantiseAltitude(120));
            Assert.Equal(255, NeighbourhoodMapAdapter.QuantiseAltitude(short.MaxValue));
        }

        [Fact]
        public void QuantisationRoundTripsForRealAltitudes()
        {
            // Not exactly, and it cannot be: 2.25 bytes per ARRY(0) unit is not a whole number of
            // bytes, so quantisation rounds. The only honest assertion is that the round trip lands
            // within the height the byte cannot express - half an elevation byte, 1/24 of a tile.
            // (Under the old *12 conversion the round trip was lossless up to 21 and this test
            // asserted exact equality, which is what pinned the bug: it could only be exact for
            // the altitudes that are whole numbers of tiles.) Negative altitudes are excluded
            // because they clamp, which the other test covers.
            var tolerance = 0.5f / NeighbourhoodMapAdapter.ElevationUnitsPerTile;
            for (short altitude = 0; altitude <= 100; altitude++)
            {
                var back = NeighbourhoodMapAdapter.DequantiseAltitude(NeighbourhoodMapAdapter.QuantiseAltitude(altitude));
                Assert.True(Math.Abs(back - altitude * (3f / 16f)) <= tolerance + 1e-4f,
                    "ARRY(0) " + altitude + " came back as " + back + " tiles");
            }
        }

        [Theory]
        [InlineData(TerrainType.GRASS, NeighbourhoodTerrain.Grass)]
        [InlineData(TerrainType.SAND, NeighbourhoodTerrain.Sand)]
        [InlineData(TerrainType.ROCK, NeighbourhoodTerrain.Rock)]
        [InlineData(TerrainType.SNOW, NeighbourhoodTerrain.Snow)]
        [InlineData(TerrainType.WATER, NeighbourhoodTerrain.Water)]
        public void TheFiveTerrainTypesCarryOverUnchanged(TerrainType type, byte expected)
        {
            // FSO.Content.Model.TerrainType is declared GRASS=0, SAND=1, ROCK=2, SNOW=3, WATER=4,
            // which is NeighbourhoodTerrain's five drawable indices in the same order. A lot's own
            // ground and the ground drawn around it are the same ground because of that.
            Assert.Equal(expected, NeighbourhoodMapAdapter.TerrainIndexFor(type));
        }

        [Fact]
        public void ATerrainTypeWithNoTextureBecomesGrass()
        {
            // The enum has three members a generated map has no texture for: NULL, and the two
            // TS1-only grass variants. They are grass.
            Assert.Equal(NeighbourhoodTerrain.Grass, NeighbourhoodMapAdapter.TerrainIndexFor(TerrainType.NULL));
            Assert.Equal(NeighbourhoodTerrain.Grass, NeighbourhoodMapAdapter.TerrainIndexFor(TerrainType.TS1DarkGrass));
            Assert.Equal(NeighbourhoodTerrain.Grass, NeighbourhoodMapAdapter.TerrainIndexFor(TerrainType.TS1AutumnGrass));
            Assert.Equal(NeighbourhoodTerrain.Grass, NeighbourhoodMapAdapter.TerrainIndexFor((TerrainType)99));
        }

        [Fact]
        public void ALayoutBecomesAGenerationInputWithoutLosingAnything()
        {
            var layout = PackedLayout();
            var input = NeighbourhoodMapAdapter.InputFor(layout);

            Assert.Equal(layout.MapSize, input.MapSize);
            Assert.Equal(layout.Seed, input.Seed);
            Assert.Equal(layout.LotCount, input.Lots.Count);
            Assert.Equal(layout.LotCount, input.UsableLots.Count);

            foreach (var lot in layout.Lots)
            {
                var mapped = input.Lots[lot.Number - 11];
                Assert.Equal(lot.Number, mapped.Number);
                Assert.Equal(lot.WorldX, mapped.WorldX);
                Assert.Equal(lot.WorldY, mapped.WorldY);
                Assert.Equal(lot.Size, mapped.Size);
                Assert.Equal(lot.Zone, mapped.Zone);
                Assert.Equal(lot.HasHouse, mapped.HasHouse);
                Assert.Equal(NeighbourhoodMapAdapter.QuantiseAltitude(lot.BaseHeight), mapped.BaseHeight);
                Assert.Equal(NeighbourhoodMapAdapter.TerrainIndexFor(lot.TerrainType), mapped.TerrainType);
            }
        }

        [Fact]
        public void AnEmptyLayoutBecomesAnEmptyInput()
        {
            var input = NeighbourhoodMapAdapter.InputFor(NeighbourhoodLayout.Empty(4, 5, 1));
            Assert.Empty(input.Lots);
        }

        [Fact]
        public void AGeneratedMapFromARealLayoutIsMeshable()
        {
            // The whole chain, end to end, on real data: pack, project, generate, probe. This is
            // the "a neighbourhood can actually be turned into a world" assertion, and it is the
            // one that would fail first if M1 and M2 stopped agreeing about what a lot is.
            var layout = PackedLayout();
            var input = NeighbourhoodMapAdapter.InputFor(layout);
            var map = NeighbourhoodMapGenerator.Generate(input);

            string reason;
            Assert.True(NeighbourhoodCityMeshBuilder.CanMesh(map, out reason), reason);

            var probe = NeighbourhoodCityMeshProbe.Probe(map);
            Assert.True(probe.IsMeshable, probe.ToString());
            Assert.Equal(0, probe.OutOfAtlasCells);
            Assert.True(probe.RoadQuads > 0, "a generated neighbourhood should have roads in it");
        }

        [Fact]
        public void TheGeneratedGroundIsWhereTheLotsAre()
        {
            // The facade placement reads the ground height out of the map, and the facade is what
            // a player looks at to judge whether the world is right. So the map has to have put
            // the lot's height on the lot's tiles.
            var layout = PackedLayout();
            var map = NeighbourhoodMapGenerator.Generate(NeighbourhoodMapAdapter.InputFor(layout));

            foreach (var lot in layout.Lots)
            {
                var expected = NeighbourhoodMapAdapter.QuantiseAltitude(lot.BaseHeight);
                var actual = map.ElevationData[map.Index(lot.WorldX, lot.WorldY)];
                Assert.Equal(expected, actual);
            }
        }

        [Fact]
        public void ALotsOwnAltitudeAndItsGeneratedGroundAgreeWithinHalfAnElevationByte()
        {
            // The end-to-end property the seam exists for. A lot's BaseHeight is in ARRY(0) units,
            // the map stores a byte, and the mesh reads that byte back as world height. Taking the
            // lot's altitude all the way round through the generated map has to land where the lot
            // view itself would put it - 3/16 of a tile per unit, from WorldHeightForArryUnit -
            // to within the half a byte that quantisation can cost, 1/24 of a tile. Before the
            // conversion was 9/4 this was out by 16/3: every house stood on ground five and a
            // third times the height its own walls assume.
            var halfByte = 0.5f / NeighbourhoodMapAdapter.ElevationUnitsPerTile;
            for (short arry = 0; arry <= 100; arry++)
            {
                var inLot = NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(arry);
                var byte_ = NeighbourhoodMapAdapter.QuantiseAltitude(arry);
                var inMap = NeighbourhoodCityMeshBuilder.WorldHeightFor(byte_);

                Assert.Equal(NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit(arry), byte_);
                Assert.True(Math.Abs(inMap - inLot) <= halfByte + 1e-4f,
                    "ARRY(0) " + arry + " is " + inLot + " tiles in the lot view and " + inMap
                    + " tiles through the generated map");

                // and the two ends are within a byte of each other, not within a tile
                Assert.True(Math.Abs(NeighbourhoodMapAdapter.DequantiseAltitude(byte_) - inMap) <= 1e-4f);
            }
        }
    }
}
