using System;
using System.Linq;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the Downtown neighbourhood view's overlay layer, and for the per-view
    /// frame-folder filtering that keeps an overlay series on the map it belongs to.
    ///
    /// <para>
    /// <b>Why there is a whole file about something that is not there.</b> The Downtown car
    /// overlay was reported missing. It does not exist, in Simitone or in the game: Downtown
    /// has no car sprite to place. These tests pin that finding down as far as code can, so
    /// the report is not re-opened, and pin down the one overlay Downtown <i>does</i> have.
    /// </para>
    ///
    /// <para>
    /// <b>The evidence.</b> The evidence lives in the game's own files, so the comments here
    /// say which file was read:
    /// <list type="bullet">
    /// <item><description><c>UIGraphics.far</c>'s <c>Downtown\</c> folder is twenty files and not
    /// one of them is a car. Its map background <c>Downtown\DScreen.bmp</c> (800x600) has the
    /// traffic painted into it - a row of white cars and a red car on the upper-right road,
    /// yellow bulldozers by the water tower at the upper left.</description></item>
    /// <item><description><c>Downtown\dscreen00-04.bmp</c> are 800x600, colour-keyed magenta
    /// over the map, and draw the sea and the rock jetty in the lower right. <b>That is
    /// Downtown's animated layer, and it is water, not traffic.</b></description></item>
    /// <item><description><c>Downtown\dscreen05.bmp</c> is 400x250 - not the 800x600 of the
    /// other five - and all 100,000 of its pixels are the colour key, so it is a blank padding
    /// plate and not a sixth frame.</description></item>
    /// <item><description>Searching every <c>*.far</c> in a Complete Collection install for
    /// image files whose name contains "car" returns only
    /// <c>Studiotown\Nhood_superstar_car0-19.bmp</c> plus a base-game <c>toycar3drt.bmp</c>
    /// texture and unrelated accessories and skins. Nothing under <c>Downtown\</c>.</description></item>
    /// <item><description><c>GameData\Objects\Objects.far</c> holds the buyable cars -
    /// <c>CarBentley.iff</c>, <c>CarLimo.iff</c>, <c>CarTown.iff</c> and six siblings - plus
    /// <c>ExpansionPack2.far</c>'s <c>NPCCar.iff</c> narrative prop. Those are lot objects: a
    /// player buys a car by walking onto a Downtown lot, not by looking at the map camera.</description></item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// Everything under test is pure static data and string building - no graphics device, no
    /// content, no audio, no read of the install - so these run headless. The install
    /// observations above are recorded in the remarks on
    /// <see cref="NeighbourhoodAnimations.DowntownWavePosition"/> and repeated where a test
    /// depends on one.
    /// </para>
    /// </summary>
    public sealed class DowntownNeighbourhoodOverlayTests
    {
        private static NeighborhoodViewConfig Downtown()
        {
            return NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 2);
        }

        private static NeighborhoodViewConfig StudioTown()
        {
            return NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 5);
        }

        // ------------------------------------------------------- the layer Downtown does have

        [Fact]
        public void DowntownFrames_AreTheFiveBayWaveFramesInIndexOrder()
        {
            // dscreen00-04: the sea and the jetty in the lower right. Two-digit zero padding,
            // which is the thing that is easy to lose - a one-digit "dscreen0" would be a name
            // the provider cannot resolve and the layer would animate four frames instead of
            // five with no error.
            Assert.Equal(
                new[]
                {
                    "Downtown\\dscreen00.bmp",
                    "Downtown\\dscreen01.bmp",
                    "Downtown\\dscreen02.bmp",
                    "Downtown\\dscreen03.bmp",
                    "Downtown\\dscreen04.bmp",
                },
                NeighbourhoodAnimations.DowntownFrames());
        }

        [Fact]
        public void DowntownFrames_ArePaddedToTwoDigits_SoNoneOfThemLosesALeadingZero()
        {
            var frames = NeighbourhoodAnimations.DowntownFrames();
            const string stem = "Downtown\\dscreen";

            Assert.All(frames, name =>
            {
                var index = name.Substring(stem.Length, name.Length - stem.Length - ".bmp".Length);
                Assert.Equal(2, index.Length);
            });
            Assert.Equal("Downtown\\dscreen00.bmp", frames.First());
        }

        [Fact]
        public void DowntownFrames_StopAtFour_SoTheBlankSixthFileIsNotAFrame()
        {
            // UIGraphics.far ships Downtown\dscreen05.bmp, but it is 400x250 against the other
            // five's 800x600 and every one of its 100,000 pixels is the colour key: a blank
            // padding plate. Declaring it would put a full-size blank flash in the loop, and on
            // the un-keyed draw path a solid rectangle over the map.
            var frames = NeighbourhoodAnimations.DowntownFrames();

            Assert.DoesNotContain("Downtown\\dscreen05.bmp", frames);
            Assert.Equal("Downtown\\dscreen04.bmp", frames.Last());
        }

        [Fact]
        public void DowntownFrames_ReturnAFreshArray_EachCall()
        {
            // The config table calls these during its own static initialiser, so a shared array
            // could be sorted or cleared in place by one caller and corrupt the rest.
            var first = NeighbourhoodAnimations.DowntownFrames();
            var second = NeighbourhoodAnimations.DowntownFrames();

            Assert.NotSame(first, second);
            Assert.Equal(first, second);
        }

        [Fact]
        public void TheDowntownView_DeclaresExactlyOneLayer_AndItIsTheWater()
        {
            var config = Downtown();

            Assert.Equal("Downtown\\DScreen.bmp", config.Graphic);
            var layers = NeighbourhoodAnimations.OverlaysForMode(
                UINeighborhoodSelectionPanel.Neighborhoods, 2);

            Assert.Single(layers);
            Assert.Equal(NeighbourhoodAnimations.DowntownFrames(), NeighbourhoodAnimations.UsableFrames(layers[0]));
        }

        [Fact]
        public void TheDowntownWaterLayer_IsDrawnAtTheOrigin_BecauseItIsFullMapSized()
        {
            // dscreen00-04 are 800x600, the size of the whole map, so there is no offset to
            // choose. A non-zero position here would push the water off the sea it is meant to
            // be animating.
            var layer = NeighbourhoodAnimations.OverlaysForMode(
                UINeighborhoodSelectionPanel.Neighborhoods, 2).Single();

            Assert.Equal(Vector2.Zero, layer.Position);
            Assert.Equal(NeighbourhoodAnimations.DowntownWavePosition, layer.Position);
        }

        [Fact]
        public void TheDowntownWaterLayer_Animates_SinceItHasFiveUsableFrames()
        {
            var layer = NeighbourhoodAnimations.OverlaysForMode(
                UINeighborhoodSelectionPanel.Neighborhoods, 2).Single();

            Assert.Equal(5, NeighbourhoodAnimations.UsableFrameCount(layer));
            Assert.True(NeighbourhoodAnimations.IsAnimated(layer));
        }

        // ------------------------------------------- the finding: there is no Downtown car layer

        [Fact]
        public void TheDowntownView_DeclaresNoCarFrames_BecauseTheArtDoesNotExist()
        {
            // The reported bug was that Downtown's cars are missing or mispositioned. They are
            // neither: there is no Downtown car sprite in any shipped install to position.
            // Downtown\ in UIGraphics.far holds twenty files and the only vehicle-shaped ones
            // anywhere in the install's image art are Studiotown\Nhood_superstar_car*.bmp.
            var declared = NeighbourhoodAnimations.DeclaredFrames(Downtown());

            Assert.All(declared, name =>
                Assert.DoesNotContain("car", name, StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void TheDowntownView_DeclaresNoFrameFromTheStudioTownFolder()
        {
            // The way a "Downtown car overlay" would most plausibly be built - by reusing
            // StudioCarFrames - cannot work at all: those frames live in Studiotown\ and would
            // resolve to null on the Downtown view, whatever position they were given. So the
            // filtering below is not academic.
            var declared = NeighbourhoodAnimations.DeclaredFrames(Downtown());

            Assert.All(declared, name => Assert.NotEqual("Studiotown", NeighbourhoodAnimations.FolderOf(name)));
        }

        [Fact]
        public void NoShippedView_BorrowsFramesFromAnotherViewsFolder()
        {
            // A frame in a folder the install does not have resolves to null and
            // UINeighborhoodAnimationLayer drops it without a word, so a series pasted onto the
            // wrong map fails exactly like a typo and just as quietly. Every view's frames come
            // from the same FAR folder as its own background.
            for (var mode = 1; mode <= UINeighborhoodSelectionPanel.Neighborhoods.Length; mode++)
            {
                var config = NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, mode);
                var foreign = NeighbourhoodAnimations.ForeignFrames(config);

                Assert.True(foreign.Length == 0,
                    "mode " + mode + " (" + (config?.Graphic ?? "<no background>") + ") declares frames from another folder: "
                    + string.Join(", ", foreign));
            }
        }

        [Fact]
        public void TheStudioTownCarOverlay_IsTheOnlyVehicleSpriteInTheTable()
        {
            // Stated as the positive form of the finding above: exactly one view draws a
            // free-standing vehicle, and it is the one StudioTown layer that already existed.
            var vehicleFrames = new[]
            {
                NeighbourhoodAnimations.StudioCarFrames(),
                NeighbourhoodAnimations.DowntownFrames(),
                NeighbourhoodAnimations.NessieFrames(),
                NeighbourhoodAnimations.MagicBlimpFrames(),
            }.SelectMany(x => x).Where(x => x.Contains("car", StringComparison.OrdinalIgnoreCase)).ToArray();

            Assert.Equal(NeighbourhoodAnimations.StudioCarFrames(), vehicleFrames);
            Assert.All(vehicleFrames, name => Assert.StartsWith("Studiotown\\", name));
        }

        [Fact]
        public void TheStudioTownCarOverlay_IsUndisturbed_ByTheDowntownWork()
        {
            // The Downtown change must not have moved the layer it was asked not to disturb.
            var layers = NeighbourhoodAnimations.OverlaysForMode(
                UINeighborhoodSelectionPanel.Neighborhoods, 5);

            Assert.Equal(2, layers.Length);
            Assert.Equal(new[] { "Studiotown\\DScreen_top_layer.bmp" }, NeighbourhoodAnimations.UsableFrames(layers[0]));
            Assert.Equal(NeighbourhoodAnimations.StudioCarFrames(), NeighbourhoodAnimations.UsableFrames(layers[1]));
            Assert.Equal(NeighbourhoodAnimations.StudioCarPosition, layers[1].Position);
        }

        [Fact]
        public void TheCommunityAndMagicTownOverlays_AreUndisturbedByTheDowntownWork()
        {
            var unleashed = NeighbourhoodAnimations.OverlaysForMode(UINeighborhoodSelectionPanel.Neighborhoods, 4);
            var magic = NeighbourhoodAnimations.OverlaysForMode(UINeighborhoodSelectionPanel.Neighborhoods, 7);

            Assert.Equal(2, unleashed.Length);
            Assert.Equal(NeighbourhoodAnimations.NessiePosition, unleashed[1].Position);
            Assert.Equal(NeighbourhoodAnimations.NessieFrames(), NeighbourhoodAnimations.UsableFrames(unleashed[1]));

            Assert.Equal(2, magic.Length);
            // the pre-existing river layer keeps its (0, 62) offset, untouched
            Assert.Equal(new Vector2(0, 62), magic[0].Position);
            Assert.Equal(NeighbourhoodAnimations.MagicBlimpPosition, magic[1].Position);
            Assert.Equal(NeighbourhoodAnimations.MagicBlimpFrames(), NeighbourhoodAnimations.UsableFrames(magic[1]));
        }

        [Fact]
        public void TheVacationIslandView_IsUndisturbedByTheDowntownWork()
        {
            // Vacation Island is the view the original code's "//TODO: nessie" sat on, so it is
            // the one most at risk of being "tidied up" by a pass that is really about Downtown.
            var layers = NeighbourhoodAnimations.OverlaysForMode(UINeighborhoodSelectionPanel.Neighborhoods, 3);

            Assert.Equal(3, layers.Length);
            Assert.All(layers, layer => Assert.Equal(Vector2.Zero, layer.Position));
            Assert.All(NeighbourhoodAnimations.DeclaredFrames(
                    NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 3)),
                name => Assert.StartsWith("VIsland\\", name));
        }

        // ------------------------------------------------------------------------ FolderOf

        [Theory]
        [InlineData("Downtown\\DScreen.bmp", "Downtown")]
        [InlineData("Studiotown\\Nhood_superstar_car0.bmp", "Studiotown")]
        [InlineData("Magicland\\DScreen_waves6.bmp", "Magicland")]
        public void FolderOf_TakesEverythingBeforeTheLastSeparator(string name, string expected)
        {
            Assert.Equal(expected, NeighbourhoodAnimations.FolderOf(name));
        }

        [Fact]
        public void FolderOf_KeepsANestedFolderIntact()
        {
            // Downloads\FenceParty\Bulldoze.bmp is a real entry name in the install, so a split
            // that only handled one level would mis-file it.
            Assert.Equal("Downloads\\FenceParty", NeighbourhoodAnimations.FolderOf("Downloads\\FenceParty\\Bulldoze.bmp"));
        }

        [Fact]
        public void FolderOf_ToleratesATrailingSeparatorInTheFolderHalf()
        {
            Assert.Equal("Downtown", NeighbourhoodAnimations.FolderOf("Downtown\\dscreen00.bmp"));
            // a doubled separator would otherwise produce "Downtown\" and never match a view
            Assert.Equal("Downtown", NeighbourhoodAnimations.FolderOf("Downtown\\\\dscreen00.bmp"));
        }

        [Fact]
        public void FolderOf_TrimsSurroundingWhitespace_SoAPaddedNameStillResolves()
        {
            Assert.Equal("Downtown", NeighbourhoodAnimations.FolderOf("  Downtown\\dscreen00.bmp  "));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("dscreen00.bmp")]      // no folder at all
        [InlineData("\\dscreen00.bmp")]    // a leading separator leaves no folder
        public void FolderOf_WithNothingBeforeASeparator_IsNull(string name)
        {
            // A null folder can never equal a view's folder, so a frame like this is always
            // reported as foreign rather than quietly accepted.
            Assert.Null(NeighbourhoodAnimations.FolderOf(name));
        }

        [Fact]
        public void ViewFolder_ComeFromTheBackgroundGraphic()
        {
            Assert.Equal("Downtown", NeighbourhoodAnimations.ViewFolder(Downtown()));
            Assert.Equal("Studiotown", NeighbourhoodAnimations.ViewFolder(StudioTown()));
            Assert.Equal("VIsland", NeighbourhoodAnimations.ViewFolder(
                NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 3)));
        }

        [Fact]
        public void ViewFolder_IsNullForTheEmptySlotAndForNoConfigAtAll()
        {
            // Slot 6 is the deliberately empty config; PopulateScreen is what falls back to the
            // base view, the lookup just reports that there is no folder here.
            Assert.Null(NeighbourhoodAnimations.ViewFolder(
                NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 6)));
            Assert.Null(NeighbourhoodAnimations.ViewFolder(null));
            Assert.Null(NeighbourhoodAnimations.ViewFolder(new NeighborhoodViewConfig()));
        }

        // -------------------------------------------------------------------- ForeignFrames

        [Fact]
        public void ForeignFrames_OnAWellFormedView_IsEmpty()
        {
            Assert.Empty(NeighbourhoodAnimations.ForeignFrames(Downtown()));
            Assert.True(NeighbourhoodAnimations.OverlaysMatchView(Downtown()));
        }

        [Fact]
        public void ForeignFrames_ReportsAFrameBorrowedFromAnotherView()
        {
            // The exact shape of a "just reuse the Studio car on Downtown" change. Without the
            // filter this is a layer that resolves nothing and draws nothing, with no error.
            var config = new NeighborhoodViewConfig
            {
                Graphic = "Downtown\\DScreen.bmp",
                FullImageAnimations = new[]
                {
                    new NeighborhoodImageAnim(NeighbourhoodAnimations.StudioCarFrames()),
                }
            };

            Assert.Equal(NeighbourhoodAnimations.StudioCarFrames(), NeighbourhoodAnimations.ForeignFrames(config));
            Assert.False(NeighbourhoodAnimations.OverlaysMatchView(config));
        }

        [Fact]
        public void ForeignFrames_ReportsAFrameWithNoFolderAtAll()
        {
            // "dscreen00.bmp" rather than "Downtown\dscreen00.bmp": a plausible typo, and the
            // provider will not find it under that name.
            var config = new NeighborhoodViewConfig
            {
                Graphic = "Downtown\\DScreen.bmp",
                FullImageAnimations = new[] { new NeighborhoodImageAnim("dscreen00.bmp") }
            };

            Assert.Equal(new[] { "dscreen00.bmp" }, NeighbourhoodAnimations.ForeignFrames(config));
        }

        [Fact]
        public void ForeignFrames_ReportsOnlyTheOffendingFrames_NotTheGoodOnes()
        {
            // One bad name in an otherwise correct layer must not condemn the whole layer, or
            // the diagnostic stops pointing at anything.
            var config = new NeighborhoodViewConfig
            {
                Graphic = "Downtown\\DScreen.bmp",
                FullImageAnimations = new[]
                {
                    new NeighborhoodImageAnim("Downtown\\dscreen00.bmp", "Downtown\\dscreen01.bmp"),
                    new NeighborhoodImageAnim("Magicland\\Nhood_balloon1.bmp"),
                }
            };

            Assert.Equal(new[] { "Magicland\\Nhood_balloon1.bmp" }, NeighbourhoodAnimations.ForeignFrames(config));
        }

        [Fact]
        public void ForeignFrames_MatchesTheFolderCaseInsensitively()
        {
            // FAR entry names are looked up case-sensitively, but a *folder* that differs only
            // in case is a casing slip in one hand-written name, not a frame from another
            // neighbourhood. Reporting it as foreign would train people to ignore the filter.
            var config = new NeighborhoodViewConfig
            {
                Graphic = "downtown\\DScreen.bmp",
                FullImageAnimations = new[] { new NeighborhoodImageAnim("Downtown\\dscreen00.bmp") }
            };

            Assert.Empty(NeighbourhoodAnimations.ForeignFrames(config));
        }

        [Fact]
        public void ForeignFrames_ForAViewWithNothingDeclared_IsEmpty()
        {
            // Vacuously true: the empty slot 6, a config with no layers, and a null config.
            Assert.Empty(NeighbourhoodAnimations.ForeignFrames(null));
            Assert.Empty(NeighbourhoodAnimations.ForeignFrames(new NeighborhoodViewConfig()));
            Assert.Empty(NeighbourhoodAnimations.ForeignFrames(
                NeighbourhoodAnimations.ConfigForMode(UINeighborhoodSelectionPanel.Neighborhoods, 6)));
            Assert.True(NeighbourhoodAnimations.OverlaysMatchView(null));
        }

        [Fact]
        public void ForeignFrames_IgnoresUnusableFrameNames()
        {
            // A null or blank name is dropped by the layer constructor before any lookup, so it
            // is not evidence of a wrong folder.
            var config = new NeighborhoodViewConfig
            {
                Graphic = "Downtown\\DScreen.bmp",
                FullImageAnimations = new[] { new NeighborhoodImageAnim("Downtown\\dscreen00.bmp", null, "  ") }
            };

            Assert.Empty(NeighbourhoodAnimations.ForeignFrames(config));
        }

        // ------------------------------------------------------------------ OverlaysForMode

        [Fact]
        public void OverlaysForMode_IsOneBased_LikePopulateScreen()
        {
            // Mode 1 is the base neighbourhood, mode 2 is Downtown, mode 7 is MagicTown. An
            // off-by-one here would draw the previous view's water on the current one.
            var configs = UINeighborhoodSelectionPanel.Neighborhoods;

            Assert.Equal("Nbhd", NeighbourhoodAnimations.ViewFolder(NeighbourhoodAnimations.ConfigForMode(configs, 1)));
            Assert.Equal("Downtown", NeighbourhoodAnimations.ViewFolder(NeighbourhoodAnimations.ConfigForMode(configs, 2)));
            Assert.Equal("Magicland", NeighbourhoodAnimations.ViewFolder(NeighbourhoodAnimations.ConfigForMode(configs, 7)));

            // mode 1 is the base neighbourhood's day/night layers, mode 2 is Downtown's five wave
            // frames, so the two must not come out as the same list
            Assert.NotEqual(
                NeighbourhoodAnimations.DeclaredFrames(NeighbourhoodAnimations.ConfigForMode(configs, 1)),
                NeighbourhoodAnimations.DeclaredFrames(NeighbourhoodAnimations.ConfigForMode(configs, 2)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(8)]      // one past the end of the seven-config table
        [InlineData(int.MaxValue)]
        public void OverlaysForMode_OutsideTheTable_IsEmptyRatherThanThrowing(int mode)
        {
            // PopulateScreen falls back to the base view before it gets here; a caller that asks
            // directly gets an empty list, which the panel's loop handles as "draw nothing".
            Assert.Empty(NeighbourhoodAnimations.OverlaysForMode(UINeighborhoodSelectionPanel.Neighborhoods, mode));
        }

        [Fact]
        public void OverlaysForMode_WithNoTableOrNoLayers_IsEmpty()
        {
            Assert.Empty(NeighbourhoodAnimations.OverlaysForMode(null, 1));
            Assert.Empty(NeighbourhoodAnimations.OverlaysForMode(new NeighborhoodViewConfig[0], 1));
            Assert.Empty(NeighbourhoodAnimations.OverlaysForMode(
                new[] { new NeighborhoodViewConfig { FullImageAnimations = null } }, 1));
        }

        [Fact]
        public void OverlaysForMode_SkipsANullLayer_ThatWouldOtherwiseThrowWhileBuildingTheScreen()
        {
            // The panel's own loop used to walk the array directly, so a null entry reached the
            // layer constructor and threw there, mid-construction of the neighbourhood screen.
            var configs = new[]
            {
                new NeighborhoodViewConfig
                {
                    Graphic = "Downtown\\DScreen.bmp",
                    FullImageAnimations = new[] { null, new NeighborhoodImageAnim(NeighbourhoodAnimations.DowntownFrames()) },
                }
            };

            var layers = NeighbourhoodAnimations.OverlaysForMode(configs, 1);

            Assert.Single(layers);
            Assert.Equal(NeighbourhoodAnimations.DowntownFrames(), NeighbourhoodAnimations.UsableFrames(layers[0]));
        }

        [Fact]
        public void OverlaysForMode_ReturnsTheSameLayerObjectsTheConfigHolds()
        {
            // Not copies: PopulateScreen reads layer.Position off what it gets, so a rebuilt
            // object would silently lose the configured offset.
            var layers = NeighbourhoodAnimations.OverlaysForMode(UINeighborhoodSelectionPanel.Neighborhoods, 7);

            Assert.Same(
                UINeighborhoodSelectionPanel.Neighborhoods[6].FullImageAnimations[0], layers[0]);
            Assert.Same(
                UINeighborhoodSelectionPanel.Neighborhoods[6].FullImageAnimations[1], layers[1]);
        }
    }
}
