/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System.Collections.Generic;
using FSO.Content.Model;
using FSO.LotView;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// The in-lot surroundings: where the generated world goes relative to the lot the player is
    /// standing in, and whether it is drawn at all. §7.4, and §11's two guard cases - "with the
    /// setting off, <c>World.Surroundings</c> is null and nothing changes" and "generation
    /// failure is the lot as it is".
    ///
    /// <para>
    /// Both halves of the gate are on by default now, so a fresh install shows the neighbourhood
    /// around the lot. Every way of saying no - the painted map, the Off step, terrain detail off,
    /// a generation that fails, a lot the layout does not contain - is still a refusal, and all of
    /// them are asserted here rather than only the two that happen to be the default.
    /// </para>
    ///
    /// <para>
    /// All of this is arithmetic and decisions, which is the point. Standing on a lot and looking
    /// at a neighbourhood map have to agree about where a lot is, and the only way to know that
    /// without a screen and a graphics device is to derive both from the same numbers - so the
    /// placement is a struct with three properties, and the gate is a value with a reason.
    /// </para>
    ///
    /// <para>
    /// What is deliberately <em>not</em> here: anything that draws. The renderer itself needs a
    /// graphics device, and a test that stood one up would be testing MonoGame. Its inputs - the
    /// lot's own light, the lot's fog, the camera matrices - are all set from values these tests
    /// do check, so the only untested part is the part that cannot be.
    /// </para>
    /// </summary>
    public class NeighbourhoodSurroundingsTests
    {
        private const float Tolerance = 0.001f;

        /// <summary>
        /// FreeSO's surrounding-lot detail as a Simitone install ships it: the lowest step that is
        /// still <em>on</em> (see <c>SurroundingLotModeRow.Default</c>), which is the only thing
        /// the gate asks about. Spelled out here rather than taken from the row because the gate is
        /// about the player's setting, not about what a particular install happens to have.
        /// </summary>
        private const int DefaultSurroundingLotDetail = SurroundingLotModeRow.Default;

        private static NeighbourhoodLot Lot(
            short number, int worldX, int worldY, int size = 32, bool hasHouse = true)
        {
            return new NeighbourhoodLot
            {
                Number = number,
                Zone = LotZoning.ZoneResidential,
                Size = size,
                WorldX = worldX,
                WorldY = worldY,
                TerrainType = TerrainType.GRASS,
                BaseHeight = 4,
                HouseHash = 0xABCD,
                HasHouse = hasHouse
            };
        }

        private static NeighbourhoodLayout LayoutOf(params NeighbourhoodLot[] lots)
        {
            return new NeighbourhoodLayout(4, 1, 1, NeighbourhoodLayout.CurrentSchemaVersion, 512, lots);
        }

        /// <summary>A map whose every tile is at the same height, so a lot's ground is knowable.</summary>
        private static NeighbourhoodMap FlatMap(int size, byte elevation)
        {
            var map = NeighbourhoodMap.Create(size);
            for (int i = 0; i < map.ElevationData.Length; i++) map.ElevationData[i] = elevation;
            return map;
        }

        // ---- the lot-origin translation ---------------------------------------------------------

        [Fact]
        public void TheActiveLotsOwnCornerIsTheLotViewsOrigin()
        {
            // The whole of §7.4's "translated so the active lot's rectangle aligns with the lot's
            // own origin": one point of the generated map is the lot view's (0, 0, 0), and that
            // point is the lot's packed rectangle's minimum corner.
            var map = FlatMap(512, 48);
            var lot = Lot(21, 200, 300);
            var placement = SimitoneSurroundingsPlacement.For(map, lot);

            Assert.True(placement.IsValid);
            Assert.Same(lot, placement.Lot);
            Assert.Equal(Vector3.Zero, placement.ToLotView(placement.MapTileOrigin));
        }

        [Fact]
        public void ALotsRectangleLandsOnTheLotViewsOwnRectangle()
        {
            // A TS1 lot is 64x64 tiles and a lot in the generated world is Size x Size tiles, so a
            // generated tile IS a lot tile. The far corner of the packed rectangle therefore lands
            // on the far corner of the lot the player is standing on - which is the property that
            // makes the surround line up with the lot rather than merely be near it.
            var map = FlatMap(512, 48);
            var lot = Lot(21, 200, 300, 64);
            var placement = SimitoneSurroundingsPlacement.For(map, lot);
            var far = new Vector3(lot.WorldX + lot.Size, placement.MapTileOrigin.Y, lot.WorldY + lot.Size);

            var mapped = placement.ToLotView(far);
            Assert.Equal(lot.Size * SimitoneSurroundingsPlacement.LotViewUnitsPerTile, mapped.X, 3);
            Assert.Equal(lot.Size * SimitoneSurroundingsPlacement.LotViewUnitsPerTile, mapped.Z, 3);
        }

        [Fact]
        public void TheScaleIsTheLotViewsOwnUnitsPerTile()
        {
            // Not a tuning constant: the lot camera's target and every wall, floor and object in
            // the lot are measured in FSO.LotView.WorldSpace.WorldUnitsPerTile. If this is ever
            // wrong the surround is the right shape at the wrong size, which is the one failure
            // that would look plausible on screen.
            Assert.Equal(3f, SimitoneSurroundingsPlacement.LotViewUnitsPerTile);
        }

        [Fact]
        public void TheWorldMatrixAgreesWithThePointMapping()
        {
            // Two spellings of the same arithmetic - the one the shaders take and the one a test
            // can reason about. They must not be allowed to drift.
            var map = FlatMap(512, 36);
            var lot = Lot(21, 130, 270, 64);
            var placement = SimitoneSurroundingsPlacement.For(map, lot);
            var matrix = placement.WorldMatrix;

            foreach (var tile in new[]
            {
                new Vector3(0, 0, 0),
                placement.MapTileOrigin,
                new Vector3(511, 20f, 511),
                new Vector3(lot.WorldX + 8, placement.MapTileOrigin.Y, lot.WorldY + 8)
            })
            {
                var byPoint = placement.ToLotView(tile);
                var byMatrix = Vector3.Transform(tile, matrix);
                Assert.InRange(byMatrix.X, byPoint.X - Tolerance, byPoint.X + Tolerance);
                Assert.InRange(byMatrix.Y, byPoint.Y - Tolerance, byPoint.Y + Tolerance);
                Assert.InRange(byMatrix.Z, byPoint.Z - Tolerance, byPoint.Z + Tolerance);
            }
        }

        [Fact]
        public void TheGeneratedGroundUnderTheLotIsAtTheLotViewsGroundLevel()
        {
            // The lot view's own ground is elevation * TerrainFactor * 3 minus the lot's base
            // altitude, which is zero at the base altitude (TerrainComponent). So putting the
            // generated ground under the active lot at zero is what makes the two grounds meet
            // rather than one of them being a step above the other.
            var map = FlatMap(512, 48);
            var lot = Lot(21, 200, 300, 64);
            var placement = SimitoneSurroundingsPlacement.For(map, lot);

            var ground = NeighbourhoodFacadeTransform.GroundHeightFor(map, lot);
            Assert.Equal(ground, placement.MapTileOrigin.Y, 4);
            Assert.Equal(4f, ground);
            // a tile of the generated ground at that height, one lot tile to the right
            var next = placement.ToLotView(new Vector3(lot.WorldX + 1, ground, lot.WorldY));
            Assert.Equal(0f, next.Y, 4);
        }

        [Fact]
        public void TheGroundIsTheSameNumberTheNeighbouringFacadesStandOn()
        {
            // Not a second definition of the same height. The surround and the houses standing in
            // it have to be standing on the same ground, and the only way to guarantee that is for
            // both to ask NeighbourhoodFacadeTransform one question.
            var map = FlatMap(512, 60);
            var lot = Lot(21, 200, 300);

            Assert.Equal(NeighbourhoodFacadeTransform.GroundHeightFor(map, lot),
                SimitoneSurroundingsPlacement.For(map, lot).MapTileOrigin.Y, 5);
        }

        [Fact]
        public void EveryLotInAGeneratedNeighbourhoodCanBePlaced()
        {
            // The real shape of the question, on a real generated world rather than a hand-made
            // one: for every lot the packer placed, the surround is a placement whose own corner
            // is the lot view's origin.
            var input = NeighbourhoodTestInput.FourFlatLots();
            var map = NeighbourhoodMapGenerator.Generate(input);
            var layout = LayoutFrom(input);

            for (int i = 0; i < layout.LotCount; i++)
            {
                var lot = layout.Lots[i];
                SimitoneSurroundingsPlacement placement;
                string reason;
                Assert.True(SimitoneSurroundingsPlacement.TryFor(layout, map, lot.Number, out placement, out reason),
                    "lot " + lot.Number + " should be placeable: " + reason);
                Assert.Equal(Vector3.Zero, placement.ToLotView(placement.MapTileOrigin));
                Assert.Contains("lot " + lot.Number, placement.ToString());
            }
        }

        [Fact]
        public void NeighbouringLotsShareOneGeneratedWorldAndOnePlacementEach()
        {
            // The player's lot and its neighbour are two placements into the same map, so the
            // neighbour's facade lands next to the player rather than at the origin.
            var map = FlatMap(512, 48);
            var here = Lot(21, 200, 300, 64);
            var nextDoor = Lot(22, 300, 300, 64);
            var layout = LayoutOf(here, nextDoor);

            SimitoneSurroundingsPlacement atHome, nextTo;
            string reason;
            Assert.True(SimitoneSurroundingsPlacement.TryFor(layout, map, 21, out atHome, out reason), reason);
            Assert.True(SimitoneSurroundingsPlacement.TryFor(layout, map, 22, out nextTo, out reason), reason);

            // the neighbour is 100 generated tiles east, which is 300 lot-view units, and it is
            // seen through the player's own placement - which is the point: one world, one
            // placement per lot, and the neighbour lands beside the player rather than on top of
            // them or at the origin
            var neighbourCorner = atHome.ToLotView(nextTo.MapTileOrigin);
            Assert.Equal(100f * SimitoneSurroundingsPlacement.LotViewUnitsPerTile, neighbourCorner.X, 3);
            Assert.Equal(0f, neighbourCorner.Y, 4);
            Assert.Equal(0f, neighbourCorner.Z, 4);

            // and the neighbour's own placement still puts its own corner on the lot's origin
            Assert.Equal(Vector3.Zero, nextTo.ToLotView(nextTo.MapTileOrigin));
        }

        [Fact]
        public void ALotThatIsNotOnTheGeneratedMapIsRefusedRatherThanGuessed()
        {
            // A guess at where an unknown lot went would put the whole neighbourhood somewhere
            // else on the map, which is worse than not drawing it. This is the difference between
            // "the setting is off" and "this lot is not on the generated map" in the log.
            var map = FlatMap(512, 48);
            var layout = LayoutOf(Lot(21, 200, 300));

            SimitoneSurroundingsPlacement placement;
            string reason;
            Assert.False(SimitoneSurroundingsPlacement.TryFor(layout, map, 99, out placement, out reason));
            Assert.False(placement.IsValid);
            Assert.Contains("99", reason);
            Assert.False(string.IsNullOrWhiteSpace(reason));

            Assert.False(SimitoneSurroundingsPlacement.TryFor(null, map, 21, out placement, out reason));
            Assert.Contains("layout", reason);

            Assert.False(SimitoneSurroundingsPlacement.TryFor(layout, null, 21, out placement, out reason));
            Assert.Contains("map", reason);
        }

        [Fact]
        public void APlacementWithNoLotIsNotAPlacement()
        {
            var placement = SimitoneSurroundingsPlacement.For(FlatMap(512, 48), null);

            Assert.False(placement.IsValid);
            Assert.Null(placement.Lot);
            Assert.Contains("no in-lot surroundings placement", placement.ToString());
        }

        // ---- the gate ---------------------------------------------------------------------------

        [Fact]
        public void TheDefaultsDrawThePaintedMapAndNotTheWorldAroundTheLot()
        {
            // The revert put the painted map back as the default, and the in-lot surround is only
            // drawn when the generated 3D world is asked for - so a fresh install gets the lot as it
            // was, not a lot standing in a generated neighbourhood.
            var selection = SimitoneSurroundingsSelection.For(
                NeighbourhoodViewSetting.Default, DefaultSurroundingLotDetail);

            Assert.Equal(NeighbourhoodView.Bitmap2D, NeighbourhoodViewSetting.Default);
            Assert.False(selection.Enabled, selection.Reason);
            Assert.Equal(NeighbourhoodViewSetting.Default, selection.Requested);
            Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
        }

        [Fact]
        public void TheDefaultsWouldBeDrawnAroundTheLotOnlyWhenTheGeneratedWorldIsAskedFor()
        {
            // The gate reads what GraphicsOptions.Apply actually pushed, not the settings object,
            // so the ini default and the engine flag are asserted to agree.
            var preferences = GraphicsOptions.Defaults;
            var config = new WorldConfig();
            GraphicsOptions.Apply(config, preferences);

            Assert.Equal(preferences.SurroundingLots, config.SurroundingLots);
            Assert.True(config.SurroundingLots > 0);
            //the default view is 2D, so no surround; asking for 3D is what turns it on
            Assert.False(SimitoneSurroundingsSelection.For(preferences.NeighbourhoodView, config.SurroundingLots).Enabled);
            Assert.True(SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, config.SurroundingLots).Enabled);
        }

        [Fact]
        public void EitherGateClosingStillTurnsTheSurroundingsOff()
        {
            // A player who picks the painted map, or who turns terrain detail off, gets the lot
            // exactly as it was - the refusal is still there, it is just not the default any more.
            Assert.False(SimitoneSurroundingsSelection.For(NeighbourhoodView.Bitmap2D, DefaultSurroundingLotDetail).Enabled);
            Assert.False(SimitoneSurroundingsSelection.For(NeighbourhoodView.Off, DefaultSurroundingLotDetail).Enabled);
            Assert.False(SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, SurroundingLotModeRow.Off).Enabled);
        }

        [Theory]
        [InlineData(NeighbourhoodView.Off)]
        [InlineData(NeighbourhoodView.Bitmap2D)]
        public void NeitherTheOffStepNorTheBitmapIsDrawnAroundTheLot(NeighbourhoodView view)
        {
            foreach (var detail in new[] { 0, 1, 2 })
            {
                var selection = SimitoneSurroundingsSelection.For(view, detail);
                Assert.False(selection.Enabled, view + " at detail " + detail);
                Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
            }
        }

        [Fact]
        public void AnOutOfRangeViewIsTheDefault()
        {
            // The same rule as everywhere else a hand-editable ini reaches the renderer: an
            // unknown value is a known-good one rather than a view nothing handles. Which known-good
            // one follows the default, so this is the statement that garbage cannot switch the
            // player off the generated world behind their back.
            var selection = SimitoneSurroundingsSelection.For((NeighbourhoodView)77, DefaultSurroundingLotDetail);

            Assert.Equal(NeighbourhoodViewSetting.Default, selection.Requested);
            Assert.Equal(SimitoneSurroundingsSelection.For(
                    NeighbourhoodViewSetting.Default, DefaultSurroundingLotDetail).Enabled,
                selection.Enabled);
        }

        [Fact]
        public void The3DNeighbourhoodIsOnlyDrawnAroundTheLotWhenTheEngineIsToldToDrawSurroundingLots()
        {
            // GlobalSettings.SurroundingLotMode is the engine's own "surrounding lots" detail and
            // its 0 step is TSO's "none" - the city terrain reads it as
            // houseRange = mode == 0 ? 0 : 1. Turning terrain detail down to Low is a player's
            // existing way of saying not, and this honours it rather than overriding it. The
            // shipped default is the lowest of the two on values, so the out-of-the-box lot is in
            // a neighbourhood and the Off step is still one click away.
            Assert.False(SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, SurroundingLotModeRow.Off).Enabled);
            Assert.True(SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, SurroundingLotModeRow.Low).Enabled);
            Assert.True(SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, SurroundingLotModeRow.High).Enabled);

            Assert.Equal(SurroundingLotModeRow.Low, SurroundingLotModeRow.Default);
            Assert.True(SurroundingLotModeRow.Default > SurroundingLotModeRow.Off);
        }

        [Fact]
        public void ASurroundingsSelectionAlwaysSaysWhy()
        {
            // A refusal with no stated reason is the case worth chasing, and §11's whole reason
            // for making the decision a value rather than a branch.
            var off = SimitoneSurroundingsSelection.For(NeighbourhoodView.Off, 2);
            var on = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, 2);
            var built = SimitoneSurroundingsSelection.AfterGeneration(on, NeighbourhoodMap.Create(96), null);
            var failed = SimitoneSurroundingsSelection.AfterGeneration(on, null, "the mesh would not upload");

            foreach (var selection in new[] { off, on, built, failed })
            {
                Assert.False(string.IsNullOrWhiteSpace(selection.Reason));
                Assert.Contains(NeighbourhoodViewSetting.Describe(selection.Requested), selection.ToString());
                Assert.Contains(selection.SurroundingLotMode.ToString(), selection.ToString());
            }
        }

        // ---- generation failure is the lot as it is --------------------------------------------

        [Fact]
        public void AFailedGenerationIsNotDrawnAroundTheLotAndSaysWhatFailed()
        {
            var requested = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, 2);
            Assert.True(requested.Enabled);

            var failed = SimitoneSurroundingsSelection.AfterGeneration(
                requested, null, "the neighbourhood provider is not up yet");

            Assert.False(failed.Enabled);
            Assert.Contains("could not be built", failed.Reason);
            Assert.Contains("the neighbourhood provider is not up yet", failed.Reason);
        }

        [Fact]
        public void AGeneratedWorldWithNoMapIsNotDrawnAroundTheLot()
        {
            var requested = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, 2);
            var nothing = SimitoneSurroundingsSelection.AfterGeneration(requested, null, null);

            Assert.False(nothing.Enabled);
            Assert.Contains("no map was produced", nothing.Reason);
        }

        [Fact]
        public void AGeneratedWorldThatBuiltIsDrawnAroundTheLot()
        {
            var requested = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, 2);
            var built = SimitoneSurroundingsSelection.AfterGeneration(
                requested, NeighbourhoodMap.Create(128), null);

            Assert.True(built.Enabled);
            Assert.Contains("128x128", built.Reason);
        }

        [Fact]
        public void ASettingThatIsOffIsNotDrawnAroundTheLotWhateverGenerationDid()
        {
            // The acceptance criterion as a chain rather than a single step: once the setting has
            // said no, no later step can say yes, whatever the generator managed to produce.
            foreach (var view in new[] { NeighbourhoodView.Off, NeighbourhoodView.Bitmap2D })
            {
                var off = SimitoneSurroundingsSelection.For(view, 2);
                var generated = SimitoneSurroundingsSelection.AfterGeneration(
                    off, NeighbourhoodMap.Create(96), null);
                var placed = SimitoneSurroundingsSelection.AfterPlacement(generated,
                    SimitoneSurroundingsPlacement.For(FlatMap(96, 48), Lot(21, 8, 8)), null);

                Assert.False(generated.Enabled);
                Assert.False(placed.Enabled);
                Assert.Equal(off.Reason, placed.Reason);
            }
        }

        [Fact]
        public void ALotWithNoPlacementIsNotDrawnAroundTheLot()
        {
            var requested = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, 2);
            var placed = SimitoneSurroundingsSelection.AfterPlacement(requested,
                default(SimitoneSurroundingsPlacement), "lot 99 is not in this neighbourhood's generated map");

            Assert.False(placed.Enabled);
            Assert.Contains("lot 99", placed.Reason);
        }

        [Fact]
        public void APlacementIsWhatMakesTheSurroundWorthDrawing()
        {
            var requested = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, 2);
            var placement = SimitoneSurroundingsPlacement.For(FlatMap(512, 48), Lot(21, 200, 300));
            var placed = SimitoneSurroundingsSelection.AfterPlacement(requested, placement, null);

            Assert.True(placed.Enabled);
            Assert.Contains("lot 21", placed.Reason);
        }

        [Fact]
        public void AFailedBuildIsNullWithAReasonAndNothingToUnwind()
        {
            // The whole of the failure contract, without a graphics device: every refusal is a
            // null and a sentence, so the caller has one thing to do - leave World.Surroundings
            // null and log the reason. There is no half-built surround to dispose and no exception
            // to catch, which is what makes "the lot is byte-for-byte today's" achievable.
            string reason;
            Assert.Null(SimitoneSurroundings.TryCreate(null, null, 21, out reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));

            //no device is reported first, because nothing else can be recovered without one
            Assert.Contains("device", reason);

            var world = new NeighbourhoodSceneSource(LayoutOf(Lot(21, 200, 300)), "the road graph would not connect");
            Assert.Null(SimitoneSurroundings.TryCreate(null, world, 21, out reason));
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }

        [Fact]
        public void AFailedBuildStillEndsInALoggableSentence()
        {
            // What the screen does with a refusal: hand the reason to the decision, and the
            // decision is what produces the line a player can find in the log. Composed here so the
            // two halves cannot drift apart.
            var requested = SimitoneSurroundingsSelection.For(NeighbourhoodView.World3D, 2);
            string reason;
            SimitoneSurroundings.TryCreate(null, null, 21, out reason);

            var selection = SimitoneSurroundingsSelection.AfterGeneration(requested, null, reason);

            Assert.False(selection.Enabled);
            Assert.Contains(reason, selection.Reason);
        }

        // ---- the lot's own light ----------------------------------------------------------------

        [Fact]
        public void WithNoLotLightTheSurroundIsLitLikeTheNeighbourhoodMap()
        {
            // The first frames of a lot have no light batch yet, and a NaN reaching a shader is
            // worse than a fixed afternoon sun. It has to be the same sun the map uses, or the
            // player sees the same neighbourhood change colour as they walk onto a lot.
            Assert.Equal(Neighbourhood3DView.LightDirection,
                SimitoneSurroundingsPlacement.LightDirectionFor(null));
            Assert.Equal(Neighbourhood3DView.LightColor,
                SimitoneSurroundingsPlacement.LightColorFor((Color?)null));
        }

        [Fact]
        public void ADegenerateLotLightIsTheFixedSunRatherThanAUnitVectorOfNowhere()
        {
            Assert.Equal(Neighbourhood3DView.LightDirection,
                SimitoneSurroundingsPlacement.LightDirectionFor(Vector3.Zero));
            Assert.Equal(Neighbourhood3DView.LightDirection,
                SimitoneSurroundingsPlacement.LightDirectionFor(new Vector3(float.NaN, 1, 0)));
        }

        [Fact]
        public void TheLotSunVectorBecomesAUnitVectorPointingAtTheSameSun()
        {
            var lotSun = new Vector3(0.4f, 2f, -1.5f);

            var direction = SimitoneSurroundingsPlacement.LightDirectionFor(lotSun);

            Assert.Equal(1f, direction.Length(), 4);
            var expected = lotSun;
            expected.Normalize();
            Assert.Equal(expected.X, direction.X, 4);
            Assert.Equal(expected.Y, direction.Y, 4);
            Assert.Equal(expected.Z, direction.Z, 4);
        }

        [Fact]
        public void TheLotOutdoorColourBecomesTheSurroundsLightColour()
        {
            // WorldState.OutsideColor is what the lot clears its background to and what
            // VMArchitecture.SetTimeOfDay writes, so it is the lot's own outdoor light - and the
            // surround following it is what makes a sunset on the map a sunset on the lot.
            var light = SimitoneSurroundingsPlacement.LightColorFor(new Color(200, 180, 140));

            Assert.Equal(200 / 255f * SimitoneSurroundingsPlacement.LightStrength, light.X, 3);
            Assert.Equal(180 / 255f * SimitoneSurroundingsPlacement.LightStrength, light.Y, 3);
            Assert.Equal(140 / 255f * SimitoneSurroundingsPlacement.LightStrength, light.Z, 3);
            Assert.Equal(1f, light.W, 4);
        }

        [Fact]
        public void MidnightIsDimRatherThanInvisible()
        {
            // The lot's outdoor colour really does go to near-black at night. A light of zero
            // would leave the neighbourhood invisible, which is a worse answer than a dim one and
            // a much worse one than the sky the lot itself is drawing.
            var light = SimitoneSurroundingsPlacement.LightColorFor(Color.Black);
            var floor = SimitoneSurroundingsPlacement.MinLightChannel
                * SimitoneSurroundingsPlacement.LightStrength;

            Assert.InRange(light.X, floor, 1f);
            Assert.InRange(light.Y, floor, 1f);
            Assert.InRange(light.Z, floor, 1f);
            Assert.Equal(1f, light.W, 4);
        }

        [Fact]
        public void AFullWhiteOutdoorColourIsBoundedRatherThanBlownOut()
        {
            // Color.White is the lot's outdoor colour before any time of day has been set, so it
            // is the first value a lot ever reports and it must not be able to push a lit surface
            // past white.
            var light = SimitoneSurroundingsPlacement.LightColorFor(Color.White);

            Assert.Equal(SimitoneSurroundingsPlacement.LightStrength, light.X, 4);
            Assert.Equal(SimitoneSurroundingsPlacement.LightStrength, light.Y, 4);
            Assert.Equal(SimitoneSurroundingsPlacement.LightStrength, light.Z, 4);
        }

        // ---- the numbers a draw depends on -------------------------------------------------------

        [Fact]
        public void TheBackdropKeepsTheEngineDepthSquash()
        {
            // TSO's own city backdrop is drawn with this, and the depth buffer it goes into is
            // cleared immediately afterwards, so the arrangement is the engine's and this type's
            // job is to use it rather than to invent a second one.
            Assert.Equal(0.3f, SimitoneSurroundings.BackdropDepthSquash);
            Assert.True(SimitoneSurroundings.DefaultFogDistance > 1f,
                "a lot with no weather yet needs a fog distance, or every vertex lands on the near plane");
        }

        [Fact]
        public void EveryLotNumberResolvesToANeighbourhoodTheGeneratedWorldCanBeGeneratedFor()
        {
            // Standing on a lot and looking at its map have to be two views of one neighbourhood.
            // This is the join: the lot number chooses the sidecar slot the generated world is
            // generated from, so every lot number has to land on a slot the store recognises - a
            // slot the store refuses is a slot whose layout would be regenerated from nothing on
            // every single lot load, and a lot with no layout has no placement to draw around.
            var seen = new HashSet<int>();
            for (int house = 1; house < 120; house++)
            {
                var slot = SimitoneSurroundings.SlotForLot(house);
                Assert.True(NeighbourhoodLayoutStore.IsKnownSlot(slot),
                    "lot " + house + " resolved to slot " + slot + ", which is not a sidecar slot");
                seen.Add(slot);
            }
            //and it is a real partition, not one bucket for everything
            Assert.True(seen.Count > 1, "the four lot ranges should not all resolve to one slot");
        }

        /// <summary>
        /// A layout over the lots a <see cref="NeighbourhoodMapInput"/> describes, so a test can
        /// generate a map and then ask placement questions about the same lots.
        /// </summary>
        private static NeighbourhoodLayout LayoutFrom(NeighbourhoodMapInput input)
        {
            var lots = new NeighbourhoodLot[input.UsableLots.Count];
            for (int i = 0; i < lots.Length; i++)
            {
                var read = input.UsableLots[i];
                lots[i] = new NeighbourhoodLot
                {
                    Number = read.Number,
                    Zone = read.Zone,
                    Size = read.Size,
                    MapX = 0,
                    MapY = 0,
                    WorldX = read.WorldX,
                    WorldY = read.WorldY,
                    //every canned lot in these tests is grass, and the placement does not read the
                    //terrain type - the enum's numbering is not the map's, so it is stated rather
                    //than cast
                    TerrainType = TerrainType.GRASS,
                    BaseHeight = read.BaseHeight,
                    HouseHash = 0x1234,
                    HasHouse = read.HasHouse
                };
            }
            return new NeighbourhoodLayout(4, (int)input.Seed, 1,
                NeighbourhoodLayout.CurrentSchemaVersion, input.MapSize, lots);
        }
    }
}
