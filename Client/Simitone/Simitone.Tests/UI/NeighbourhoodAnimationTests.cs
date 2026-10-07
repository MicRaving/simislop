using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the neighbourhood-selection-screen animation table.
    ///
    /// Two things are worth pinning down here, because both fail silently rather than
    /// loudly:
    ///
    /// <list type="bullet">
    /// <item><b>Frame names must match the install exactly.</b> The panel resolves every frame
    /// through <c>TS1Provider</c>, which is a case-sensitive dictionary lookup over FAR entry
    /// names. A typo, a wrong zero-pad width or a wrong folder is not an exception - the frame
    /// is dropped and the layer quietly animates one frame short (or not at all). These tests
    /// check the generated names against the literal names in the shipped install.</item>
    /// <item><b>Positions have to stay on the background.</b> An overlay drawn past 800x600 is
    /// clipped without complaint, and one drawn over the painted-in art of the lot screen just
    /// looks wrong; there is no error either way, so the bounds are asserted.</item>
    /// </list>
    ///
    /// Everything under test is pure static data and string building - no graphics device, no
    /// content, no audio - so these run headless.
    /// </summary>
    public sealed class NeighbourhoodAnimationTests
    {
        // ------------------------------------------------------- NumberedFrameSeries: the shape

        [Fact]
        public void NumberedFrameSeries_BuildsFolderStemIndexExtension_InAscendingOrder()
        {
            var frames = NeighbourhoodAnimations.NumberedFrameSeries("VIsland", "visland_waves", ".bmp", 1, 5, 3);

            Assert.Equal(
                new[]
                {
                    "VIsland\\visland_waves001.bmp",
                    "VIsland\\visland_waves002.bmp",
                    "VIsland\\visland_waves003.bmp",
                    "VIsland\\visland_waves004.bmp",
                    "VIsland\\visland_waves005.bmp",
                },
                frames);
        }

        [Fact]
        public void NumberedFrameSeries_WithZeroDigits_DoesNotPad_WhichIsWhatTheCarAndBlimpSeriesDo()
        {
            // Nhood_superstar_car0.bmp .. car19.bmp and Nhood_balloon1.bmp .. balloon16.bmp are
            // stored unpadded; padding them would produce names the provider cannot resolve.
            var frames = NeighbourhoodAnimations.NumberedFrameSeries("Studiotown", "Nhood_superstar_car", ".bmp", 0, 19);

            Assert.Equal("Studiotown\\Nhood_superstar_car0.bmp", frames[0]);
            Assert.Equal("Studiotown\\Nhood_superstar_car1.bmp", frames[1]);
            Assert.Equal("Studiotown\\Nhood_superstar_car19.bmp", frames[19]);
            // nothing gained a leading zero
            Assert.All(frames, name => Assert.DoesNotContain("_car0", name.Substring("Studiotown\\Nhood_superstar_car".Length)));
        }

        [Fact]
        public void NumberedFrameSeries_WithPadding_PadsToAtLeastTheRequestedWidth()
        {
            // digits: 2 is a minimum, not a maximum - 1..16 come out as "01".."16": the single
            // digit indices are padded up, the two digit ones are left alone and never truncated.
            var frames = NeighbourhoodAnimations.NumberedFrameSeries("Magicland", "Nhood_balloon", ".bmp", 1, 16, 2);

            Assert.Equal("Magicland\\Nhood_balloon01.bmp", frames[0]);
            Assert.Equal("Magicland\\Nhood_balloon09.bmp", frames[8]);
            Assert.Equal("Magicland\\Nhood_balloon10.bmp", frames[9]);
            Assert.Equal("Magicland\\Nhood_balloon16.bmp", frames[15]);
        }

        [Fact]
        public void NumberedFrameSeries_PaddingIsOptIn_SoTheUnpaddedSeriesStayUnpadded()
        {
            // The shipped balloon and car files are stored without a zero pad, so their
            // generators must not ask for one - otherwise every frame silently fails to load.
            Assert.DoesNotContain("_balloon01.bmp", NeighbourhoodAnimations.MagicBlimpFrames()[0]);
            Assert.DoesNotContain("_car00.bmp", NeighbourhoodAnimations.StudioCarFrames()[0]);
        }

        [Fact]
        public void NumberedFrameSeries_WithASingleIndex_ProducesOneFrame()
        {
            var frames = NeighbourhoodAnimations.NumberedFrameSeries("Nbhd", "ness", ".bmp", 3, 3, 2);

            Assert.Equal(new[] { "Nbhd\\ness03.bmp" }, frames);
        }

        [Fact]
        public void NumberedFrameSeries_IncludesBothEnds_Inclusive()
        {
            // Off-by-one on either end is the classic way a numbered series silently loses a
            // frame, so both bounds are pinned.
            var frames = NeighbourhoodAnimations.NumberedFrameSeries("Nbhd", "ness", ".bmp", 1, 3, 2);

            Assert.Equal(3, frames.Length);
            Assert.Equal("Nbhd\\ness01.bmp", frames.First());
            Assert.Equal("Nbhd\\ness03.bmp", frames.Last());
        }

        [Fact]
        public void NumberedFrameSeries_ToleratesATrailingSeparatorOnTheFolder()
        {
            // Callers write either "VIsland" or "VIsland\\"; both must give the same name, or
            // the provider lookup fails on a doubled separator.
            var withSeparator = NeighbourhoodAnimations.NumberedFrameSeries("Magicland\\", "Nhood_balloon", ".bmp", 1, 2);
            var without = NeighbourhoodAnimations.NumberedFrameSeries("Magicland", "Nhood_balloon", ".bmp", 1, 2);

            Assert.Equal(without, withSeparator);
            Assert.Equal("Magicland\\Nhood_balloon1.bmp", withSeparator[0]);
        }

        [Fact]
        public void NumberedFrameSeries_DoesNotDependOnTheCurrentCulture()
        {
            // The index is formatted with InvariantCulture: under a culture with a different
            // negative sign or digit substitution the produced name would not match the file
            // on disk. Only positive indices are produced today, so this is a guard, not a fix.
            var previous = Thread.CurrentThread.CurrentCulture;
            try
            {
                Thread.CurrentThread.CurrentCulture = new CultureInfo("ar-SA");
                var frames = NeighbourhoodAnimations.NumberedFrameSeries("Nbhd", "ness", ".bmp", 1, 3, 2);

                Assert.Equal(new[] { "Nbhd\\ness01.bmp", "Nbhd\\ness02.bmp", "Nbhd\\ness03.bmp" }, frames);
            }
            finally
            {
                Thread.CurrentThread.CurrentCulture = previous;
            }
        }

        [Fact]
        public void NumberedFrameSeries_WithAnInvertedRange_Throws()
        {
            // Silently returning an empty (or reversed) list would produce a layer that draws
            // nothing, which is exactly the failure this whole change exists to fix.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => NeighbourhoodAnimations.NumberedFrameSeries("Nbhd", "ness", ".bmp", 3, 1, 2));
        }

        [Fact]
        public void NumberedFrameSeries_WithNegativePadding_Throws()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => NeighbourhoodAnimations.NumberedFrameSeries("Nbhd", "ness", ".bmp", 1, 3, -1));
        }

        [Theory]
        [InlineData(null, "ness", ".bmp")]
        [InlineData("Nbhd", null, ".bmp")]
        [InlineData("Nbhd", "ness", null)]
        public void NumberedFrameSeries_WithAMissingPart_Throws(string folder, string stem, string extension)
        {
            Assert.Throws<ArgumentNullException>(
                () => NeighbourhoodAnimations.NumberedFrameSeries(folder, stem, extension, 1, 3, 2));
        }

        [Fact]
        public void NumberedFrameSeries_ReturnsAFreshArray_EachCall()
        {
            // The config table calls these during its own static initialiser; if the arrays
            // were shared, one caller sorting or clearing one in place would corrupt the rest.
            var first = NeighbourhoodAnimations.NessieFrames();
            var second = NeighbourhoodAnimations.NessieFrames();

            Assert.NotSame(first, second);
            Assert.Equal(first, second);
        }

        // -------------------------------------------------- the three added layers: exact names

        [Fact]
        public void NessieFrames_AreTheThreeCommunitySeaSerpentFramesInSurfacingOrder()
        {
            // The install ships these under Nbhd\ and Community\ (byte-identical); VIsland\ has
            // no copy of its own, which is why the layer is attached to the Community scene.
            Assert.Equal(
                new[] { "Community\\ness01.bmp", "Community\\ness02.bmp", "Community\\ness03.bmp" },
                NeighbourhoodAnimations.NessieFrames());
        }

        [Fact]
        public void StudioCarFrames_AreASingleCarWithItsTwoFramesInIndexOrder()
        {
            // The pack's twenty frames are ten cars of two frames each; the panel can only place
            // one car per layer, so exactly car0/car1 are used (see StudioCarFrames' docs).
            var frames = NeighbourhoodAnimations.StudioCarFrames();

            Assert.Equal(2, frames.Length);
            Assert.Equal("Studiotown\\Nhood_superstar_car0.bmp", frames[0]);
            Assert.Equal("Studiotown\\Nhood_superstar_car1.bmp", frames[1]);
        }

        [Fact]
        public void MagicBlimpFrames_AreTheSixteenAirshipFramesInRotationOrder()
        {
            var frames = NeighbourhoodAnimations.MagicBlimpFrames();

            Assert.Equal(16, frames.Length);
            Assert.Equal("Magicland\\Nhood_balloon1.bmp", frames[0]);
            Assert.Equal("Magicland\\Nhood_balloon16.bmp", frames[15]);
        }

        [Fact]
        public void EveryGeneratedFrameName_IsFolderQualifiedWithABackslash()
        {
            // A bare file name would miss the provider entirely: TS1Provider keys the FAR
            // entries by their full in-archive path.
            foreach (var frames in new[]
            {
                NeighbourhoodAnimations.NessieFrames(),
                NeighbourhoodAnimations.StudioCarFrames(),
                NeighbourhoodAnimations.MagicBlimpFrames(),
            })
            {
                Assert.All(frames, name =>
                {
                    Assert.Contains("\\", name);
                    Assert.DoesNotContain("/", name);
                });
            }
        }

        [Fact]
        public void EveryGeneratedFrameName_EndsInADecodableExtension()
        {
            // SmartCodec only maps .bmp/.tga/.jpg to a texture codec; anything else decodes to
            // null and the frame is dropped.
            var decodable = new[] { ".bmp", ".tga", ".jpg" };

            foreach (var frames in new[]
            {
                NeighbourhoodAnimations.NessieFrames(),
                NeighbourhoodAnimations.StudioCarFrames(),
                NeighbourhoodAnimations.MagicBlimpFrames(),
            })
            {
                Assert.All(frames, name =>
                    Assert.Contains(System.IO.Path.GetExtension(name).ToLowerInvariant(), decodable));
            }
        }

        // ------------------------------------------------------ the config table: layers present

        [Fact]
        public void VacationIsland_KeepsItsThreeLayersAndHasNoNessie()
        {
            // Nessie does not belong here: the art only ships under Community\/Nbhd\, and the
            // Vacation scene has no VIsland\ copy (see NeighbourhoodAnimations.NessiePosition).
            var config = NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 3);

            Assert.Equal("VIsland\\visland.bmp", config.Graphic);
            var frames = config.FullImageAnimations.SelectMany(NeighbourhoodAnimations.UsableFrames).ToArray();

            Assert.Equal("VIsland\\visland_waves001.bmp", frames[0]);
            Assert.Equal("VIsland\\visland_waves005.bmp", frames[4]);
            Assert.Equal("VIsland\\visland_port.bmp", frames[5]);
            Assert.Equal("VIsland\\visland_trees.bmp", frames[6]);
            Assert.Equal(7, frames.Length);
        }

        [Fact]
        public void Community_HasTheNessieLayerAfterItsWaves()
        {
            var config = NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 4);

            Assert.Equal("Community\\NScreen_unleashed.bmp", config.Graphic);
            var layers = config.FullImageAnimations;

            Assert.Equal(2, layers.Length);
            Assert.Equal(6, NeighbourhoodAnimations.UsableFrames(layers[0]).Length); // the six wave frames are untouched
            Assert.Equal(NeighbourhoodAnimations.NessieFrames(), NeighbourhoodAnimations.UsableFrames(layers[1]));
        }

        [Fact]
        public void StudioTown_HasTheCarLayerAfterItsTopLayer()
        {
            var config = NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 5);

            Assert.Equal("Studiotown\\DScreen.bmp", config.Graphic);
            var layers = config.FullImageAnimations;

            Assert.Equal(2, layers.Length);
            Assert.Equal(new[] { "Studiotown\\DScreen_top_layer.bmp" }, NeighbourhoodAnimations.UsableFrames(layers[0]));
            Assert.Equal(NeighbourhoodAnimations.StudioCarFrames(), NeighbourhoodAnimations.UsableFrames(layers[1]));
        }

        [Fact]
        public void MagicTown_HasTheBlimpLayerAfterItsWaves()
        {
            var config = NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 7);

            Assert.Equal("Magicland\\DScreen.bmp", config.Graphic);
            var layers = config.FullImageAnimations;

            Assert.Equal(2, layers.Length);
            // the pre-existing river layer keeps its (0, 62) offset, untouched
            Assert.Equal(new Vector2(0, 62), layers[0].Position);
            Assert.Equal(
                new[]
                {
                    "Magicland\\DScreen_waves1.bmp", "Magicland\\DScreen_waves2.bmp", "Magicland\\DScreen_waves3.bmp",
                    "Magicland\\DScreen_waves4.bmp", "Magicland\\DScreen_waves5.bmp", "Magicland\\DScreen_waves6.bmp",
                },
                NeighbourhoodAnimations.UsableFrames(layers[0]));
            Assert.Equal(NeighbourhoodAnimations.MagicBlimpFrames(), NeighbourhoodAnimations.UsableFrames(layers[1]));
        }

        [Fact]
        public void EveryConfigWithALayer_KeepsItsOriginalLayerCount()
        {
            // Regression guard for "add a layer, do not rewrite the table". Modes 4, 5 and 7
            // gained exactly one layer each (nessie / cars / blimp); the rest are untouched.
            Assert.Single(NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 1).FullImageAnimations);
            Assert.Single(NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 2).FullImageAnimations);
            Assert.Equal(3, NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 3).FullImageAnimations.Length);
            Assert.Equal(2, NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 4).FullImageAnimations.Length);
            Assert.Equal(2, NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 5).FullImageAnimations.Length);
            Assert.Equal(2, NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 7).FullImageAnimations.Length);
        }

        [Fact]
        public void EveryDeclaredFrameNameInTheTable_IsNonEmpty()
        {
            // A null or blank frame name would reach provider.Get("") and be dropped; asserting
            // it here means a typo fails the suite instead of a screenshot.
            for (var mode = 1; mode <= UINeighborhoodSelectionPanel.Neighborhoods.Length; mode++)
            {
                var declared = NeighbourhoodAnimations.DeclaredFrames(
                    NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, mode));
                Assert.All(declared, name => Assert.False(string.IsNullOrWhiteSpace(name)));
            }
        }

        [Fact]
        public void NoTwoLayersInTheSameConfig_DeclareTheSameFrameName()
        {
            // The renderer keeps one texture per declared name; a duplicate across layers would
            // be two independent ping-pong cycles over the same art.
            for (var mode = 1; mode <= UINeighborhoodSelectionPanel.Neighborhoods.Length; mode++)
            {
                var config = NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, mode);
                var declared = NeighbourhoodAnimations.DeclaredFrames(config);
                Assert.Equal(declared.Length, declared.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
        }

        // --------------------------------------------------------------- animated vs static layer

        [Fact]
        public void AThreeFrameLayer_IsAnimated()
        {
            var layer = new NeighborhoodImageAnim(NeighbourhoodAnimations.NessieFrames());

            Assert.Equal(3, NeighbourhoodAnimations.UsableFrameCount(layer));
            Assert.True(NeighbourhoodAnimations.IsAnimated(layer));
        }

        [Fact]
        public void ASingleFrameLayer_IsAStaticOverlay()
        {
            // The waves/port/trees layers are one frame each; the panel draws them as a
            // non-animating overlay, and IsAnimated must agree.
            var layer = new NeighborhoodImageAnim("VIsland\\visland_port.bmp");

            Assert.Equal(1, NeighbourhoodAnimations.UsableFrameCount(layer));
            Assert.False(NeighbourhoodAnimations.IsAnimated(layer));
        }

        [Fact]
        public void UnusableFrameNames_AreNotCounted()
        {
            // UINeighborhoodAnimationLayer drops whatever the provider cannot resolve, so a
            // layer declared as three frames but resolvable as one is not an animation.
            var layer = new NeighborhoodImageAnim("VIsland\\visland_port.bmp", null, "   ");

            Assert.Equal(1, NeighbourhoodAnimations.UsableFrameCount(layer));
            Assert.False(NeighbourhoodAnimations.IsAnimated(layer));
            Assert.Equal(new[] { "VIsland\\visland_port.bmp" }, NeighbourhoodAnimations.UsableFrames(layer));
        }

        [Fact]
        public void ANullLayer_ReportsNothingRatherThanThrowing()
        {
            Assert.Equal(0, NeighbourhoodAnimations.UsableFrameCount(null));
            Assert.False(NeighbourhoodAnimations.IsAnimated(null));
            Assert.Empty(NeighbourhoodAnimations.UsableFrames(null));
            Assert.Empty(NeighbourhoodAnimations.DeclaredFrames(null));
        }

        // ------------------------------------------------------------------------ config lookup

        [Fact]
        public void ConfigForMode_IsOneBased_LikePopulateScreen()
        {
            var configs = UINeighborhoodSelectionPanel.Neighborhoods;

            Assert.Same(configs[0], NeighbourhoodAnimations.ConfigForMode(configs, 1));
            Assert.Same(configs[2], NeighbourhoodAnimations.ConfigForMode(configs, 3));
            Assert.Same(configs[6], NeighbourhoodAnimations.ConfigForMode(configs, 7));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(8)]      // one past the end of the seven-config table
        [InlineData(int.MaxValue)]
        public void ConfigForMode_OutsideTheTable_IsNullRatherThanThrowing(int mode)
        {
            Assert.Null(NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, mode));
        }

        [Fact]
        public void ConfigForMode_WithNoTable_IsNull()
        {
            Assert.Null(NeighbourhoodAnimations.ConfigForMode(null, 1));
        }

        [Fact]
        public void ConfigForMode_ReturnsTheEmptySlotSix_NotAFallback()
        {
            // Slot 6 is a deliberately empty config (Graphic is null). The lookup reports the
            // truth; PopulateScreen is what decides to fall back to the base view.
            var config = NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 6);

            Assert.NotNull(config);
            Assert.Null(config.Graphic);
            Assert.Empty(config.FullImageAnimations);
        }

        // ---------------------------------------------------------------------------- positions

        [Fact]
        public void EveryAddedLayer_IsPositionedInsideTheBackground()
        {
            // 800x600 is the unscaled size of every *Screen background the panel draws. An
            // overlay hanging off the edge is clipped silently, so assert the bounds.
            AssertOnBackground(NeighbourhoodAnimations.NessiePosition, 106, 75);      // Nbhd\ness0*.bmp
            AssertOnBackground(NeighbourhoodAnimations.StudioCarPosition, 26, 19);   // widest car frame
            AssertOnBackground(NeighbourhoodAnimations.MagicBlimpPosition, 64, 64); // Magicland\Nhood_balloon*.bmp
        }

        [Fact]
        public void TheAddedLayers_AreNotStackedOnTheSameSpot()
        {
            // The car layer sits in the same config as the static top layer at the default
            // (0, 0); if the car slot ever regressed to the origin it would draw the car in
            // the top-left corner of the studio screen, on top of the soundstage.
            Assert.NotEqual(Vector2.Zero, NeighbourhoodAnimations.StudioCarPosition);
            Assert.NotEqual(Vector2.Zero, NeighbourhoodAnimations.MagicBlimpPosition);
            Assert.NotEqual(Vector2.Zero, NeighbourhoodAnimations.NessiePosition);
        }

        private static void AssertOnBackground(Vector2 position, int width, int height)
        {
            Assert.InRange(position.X, 0, NeighbourhoodAnimations.BackgroundWidth - width);
            Assert.InRange(position.Y, 0, NeighbourhoodAnimations.BackgroundHeight - height);
        }
    }
}
