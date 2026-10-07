/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.IO;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// Whether the painted map's own layers - the animated river and waves, Nessie, the blimp, the
    /// car, and the lot sprites - are on screen, as a rule that can be asked without a screen.
    ///
    /// <para>
    /// <b>What was reported.</b> A generated 3D neighbourhood with a flat 2D river lying across
    /// it, displaced from where anything in the world was. The overlays are positioned in the
    /// painted bitmap's own 800x600 pixel space and panned and zoomed with the 2D camera, so a
    /// correct answer of "the world is carrying the picture, take the painted map's layers away"
    /// was the only one that would not put the water somewhere it does not belong - and the panel
    /// did compute that answer: the backdrop element went to its 1x1 transparent quad. The river
    /// stayed. <see cref="UINeighborhoodSelectionPanel.PaintedMapLayersVisible"/> is the rule, and
    /// the last test in this file is the reason it was not enough on its own.
    /// </para>
    ///
    /// <para>
    /// <b>Why every case is a value.</b> The decision is
    /// <see cref="NeighbourhoodViewDecision"/> and the inputs to it are
    /// <see cref="NeighbourhoodWorldHealth"/> values, so the whole question - "3D showing means
    /// the painted layers are hidden, 2D and the warm-up mean they are visible" - is asked here
    /// with no graphics device, no content provider and no screen. A panel whose constructor needs
    /// a live <c>GameFacade.GraphicsDevice</c> cannot be stood up in a test, which is exactly why
    /// this rule was made static and value-shaped.
    /// </para>
    /// </summary>
    public class NeighbourhoodPaintedLayerVisibilityTests
    {
        /// <summary>A world that has earned the screen: ground, roads, houses and submissions.</summary>
        private static NeighbourhoodWorldHealth CarryingThePicture() => new NeighbourhoodWorldHealth(
            attached: true, meshUploaded: true, terrainTriangles: 24576, roadTriangles: 4096,
            groundQuads: 65536, roadQuads: 4096, lotCount: 12, facadeCount: 12,
            secondsSinceAttach: 2d, submittedTriangles: 24576, submittedFrames: 5,
            facadeTriangles: 4128, facadesVisible: 12, facadeEligible: 12);

        /// <summary>A world that is attached and still streaming: the map is what is on screen.</summary>
        private static NeighbourhoodWorldHealth WarmingUp(double seconds = 0d) =>
            new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: false, terrainTriangles: 0, roadTriangles: 0,
                groundQuads: 65536, roadQuads: 4096, lotCount: 12, facadeCount: 0,
                secondsSinceAttach: seconds);

        // -------------------------------------------------- the rule, in the two halves it has

        [Fact]
        public void ThreeDShowingTakesThePaintedMapLayersAway()
        {
            // The report. A world that is carrying the picture has replaced the bitmap, so nothing
            // that was drawn on top of the bitmap belongs on screen - the water, the serpent, the
            // airship, the car, and the lot sprites, which are the map's own house cut-outs rather
            // than labels for houses in this one.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                CarryingThePicture());

            Assert.True(decision.Show3D, decision.Reason);
            Assert.False(UINeighborhoodSelectionPanel.PaintedMapLayersVisible(decision));
        }

        [Theory]
        [InlineData(NeighbourhoodView.Off, false)]
        [InlineData(NeighbourhoodView.Bitmap2D, false)]
        [InlineData(NeighbourhoodView.World3D, true)]
        public void TwoDAndTheWarmUpKeepThePaintedMapLayers(NeighbourhoodView view, bool warmingUp)
        {
            // The other half, and the reason the middle backdrop value exists. 2D is the painted map
            // the game has always shown, and "3D warming up" is the painted map still on top of a
            // world that is not ready yet - taking its own river off it would leave a picture of a
            // neighbourhood with the river missing, which is a different defect, not a smaller one.
            var decision = NeighbourhoodBackdropRule.Decide(view,
                warmingUp ? WarmingUp() : CarryingThePicture());

            Assert.Equal(warmingUp, decision.ShowBoth);
            Assert.False(decision.Show3D);
            Assert.True(UINeighborhoodSelectionPanel.PaintedMapLayersVisible(decision));
        }

        [Fact]
        public void ThePaintedMapLayersFollowTheBackdropElementAndNeverDivergeFromIt()
        {
            // One answer, two writers. The backdrop element's own frame is
            // NeighbourhoodBackdropFrame.For, and the layers are this rule - so where the frame is
            // the authority (an install that has a bitmap) they have to say the same thing, or the
            // map comes back without its water or the water stays with no map.
            foreach (var decision in EveryDecision())
            {
                var frame = NeighbourhoodBackdropFrame.For(decision, hasPaintedMap: true);
                var layers = UINeighborhoodSelectionPanel.PaintedMapLayersVisible(decision);
                Assert.True(frame.OverlaysVisible == layers,
                    "the backdrop element says " + frame + " but the painted map's layers say "
                    + layers + ", for " + decision);
            }
        }

        [Fact]
        public void AWorldCarryingThePictureHidesTheLayersEvenOnAnInstallWithNoBitmap()
        {
            // The case the frame cannot answer, and the one that let the river through on a machine
            // whose background would not load: there is nothing to shrink the backdrop element to,
            // so its frame is deliberately the painted map - and reading the layers off the frame
            // rather than off the decision drew the map's water over the generated world with the
            // map it belongs to missing. The element is kept for its click; the water still goes.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                CarryingThePicture());

            var elementFrame = NeighbourhoodBackdropFrame.For(decision, hasPaintedMap: false);

            Assert.True(elementFrame.IsPaintedMap, "this test needs the no-bitmap frame to exist");
            Assert.False(UINeighborhoodSelectionPanel.PaintedMapLayersVisible(decision));
        }

        [Fact]
        public void NoBackdropStateLeavesThePaintedMapLayersOverTheGeneratedWorld()
        {
            // The invariant, over the axes the rule actually reads rather than over the ones a
            // reader thought of: whatever is true of a world, the painted map's layers are on
            // screen exactly when the world is not carrying the picture. Both directions, because
            // a rule that hid everything would pass the direction that matters and break the 2D map.
            var checked_ = 0;
            foreach (var attached in new[] { false, true })
            foreach (var uploaded in new[] { false, true })
            foreach (var submitted in new[] { 0, 60660 })
            foreach (var frames in new[] { 0, 3, 50 })
            foreach (var facadeTriangles in new[] { 0, 4128 })
            foreach (var built in new[] { 0, 12, 100 })
            foreach (var eligible in new[] { 0, 100 })
            foreach (var ground in new[] { NeighbourhoodWorldHealth.Unknown, 0, 65536 })
            foreach (var roads in new[] { NeighbourhoodWorldHealth.Unknown, 0, 4096 })
            foreach (var lots in new[] { 0, 12 })
            foreach (var failures in new[] { 0, 2 })
            foreach (var lost in new[] { false, true })
            foreach (var seconds in new[] { 0d, 1d, 60d })
            {
                var health = new NeighbourhoodWorldHealth(
                    attached, uploaded, 60660, 4096, ground, roads, lots, built, failures, lost,
                    seconds, failures > 0 ? "the device went away" : null, submitted, frames,
                    facadeTriangles, 12, eligible);
                var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, health);

                var layers = UINeighborhoodSelectionPanel.PaintedMapLayersVisible(decision);
                Assert.True(layers == !decision.Show3D, "the painted map's layers are "
                    + (layers ? "on" : "off") + " screen while the decision says " + decision
                    + ", for " + health);
                checked_++;
            }

            //Every cell was actually walked. The count is spelled out rather than left implicit because a
            //loop added above without its assertion updated is a silently narrowed sweep, which is
            //the failure mode this whole file exists to catch.
            //attached, uploaded, submitted, frames, facadeTriangles, built, eligible, ground,
            //roads, lots, failures, lost, seconds
            var axes = new[] { 2, 2, 2, 3, 2, 3, 2, 3, 3, 2, 2, 2, 3 };
            var expected = 1;
            foreach (var size in axes) expected *= size;
            Assert.Equal(expected, checked_);
            Assert.True(checked_ > 50000, "the sweep covered only " + checked_ + " state(s)");
        }

        [Fact]
        public void TheLayersComeBackForTwoDWhateverTheWorldIsDoing()
        {
            // The recovery half, and it is per frame: a world that gave the screen back mid-session
            // - a device reset, a draw that threw, a facade pass that stopped - has to bring the
            // painted map's water and its lot sprites back with it, not leave a bare map behind.
            var promoted = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                CarryingThePicture());
            var reset = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth(attached: true, meshUploaded: false, terrainTriangles: 0,
                    groundQuads: 65536, roadQuads: 4096, lotCount: 12, deviceLost: true,
                    secondsSinceAttach: 0d));
            var recovered = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                CarryingThePicture());

            Assert.False(UINeighborhoodSelectionPanel.PaintedMapLayersVisible(promoted));
            Assert.True(UINeighborhoodSelectionPanel.PaintedMapLayersVisible(reset));
            Assert.False(UINeighborhoodSelectionPanel.PaintedMapLayersVisible(recovered));
        }

        [Fact]
        public void TheRuleIsNotGatedOnThePartBCapabilityFlags()
        {
            // The other half of the report. The view setting defaults to 3D, and every
            // NeighbourhoodWorldFlag defaults to off, so the shipped configuration is exactly
            // "3D asked for, capability off" - and the rule used to be gated on the flags, which
            // made the river unavoidable rather than occasional. The flag now governs the click
            // and nothing else; it is not a parameter of this rule, so there is nothing here that
            // could turn it back on.
            var parameters = typeof(UINeighborhoodSelectionPanel)
                .GetMethod(nameof(UINeighborhoodSelectionPanel.PaintedMapLayersVisible))
                .GetParameters();

            Assert.Single(parameters);
            Assert.Equal(typeof(NeighbourhoodViewDecision), parameters[0].ParameterType);
        }

        // ------------------------------------------------------- the flag that was set and unread

        [Fact]
        public void TheOverlayLayerDrawsNothingWhenItIsHidden()
        {
            // The rule is necessary but not sufficient, and this is the half that failed in the
            // field. UIElement documents Visible as something "the super class" must implement,
            // and UIContainer tests its own flag and then calls every child's Draw without asking -
            // so a layer that sets Visible = false and does not look at it draws exactly as before.
            // The water stayed on the 3D world not because the answer was wrong but because the
            // flag was never read. That cannot be asserted through the type system here (the layer
            // needs a live provider and a graphics device to construct), so it is asserted on the
            // source: the guard has to sit between the Draw override and its first texture draw.
            var source = PanelSource();
            var layer = source.IndexOf("class UINeighborhoodAnimationLayer", StringComparison.Ordinal);
            Assert.True(layer >= 0, "UINeighborhoodAnimationLayer was not found in the panel file");

            var nextClass = source.IndexOf("\n    public class ", layer, StringComparison.Ordinal);
            if (nextClass < 0) nextClass = source.Length;

            var draw = source.IndexOf("public override void Draw(UISpriteBatch batch)", layer,
                StringComparison.Ordinal);
            Assert.True(draw > 0 && draw < nextClass,
                "UINeighborhoodAnimationLayer.Draw was not found in the panel file");

            var firstDraw = source.IndexOf("DrawLocalTexture", draw, StringComparison.Ordinal);
            Assert.True(firstDraw > 0 && firstDraw < nextClass,
                "UINeighborhoodAnimationLayer.Draw no longer draws a texture");

            Assert.Contains("!Visible", source.Substring(draw, firstDraw - draw));
        }

        /// <summary>The panel's own source, read from the repository rather than from a resource.</summary>
        private static string PanelSource()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var path = Path.Combine(dir.FullName, "Client", "Simitone", "Simitone.Client",
                    "UI", "Panels", "UINeighbourhoodSelectionPanel.cs");
                if (File.Exists(path)) return File.ReadAllText(path);
                dir = dir.Parent;
            }
            throw new FileNotFoundException(
                "Could not find UINeighbourhoodSelectionPanel.cs above " + AppContext.BaseDirectory);
        }

        /// <summary>
        /// One decision of every kind this panel can reach, so the "these two agree" test is over
        /// all three backdrops rather than over the two a reader would pick.
        /// </summary>
        private static NeighbourhoodViewDecision[] EveryDecision()
        {
            return new[]
            {
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.Off, CarryingThePicture()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.Bitmap2D, CarryingThePicture()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, WarmingUp()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, CarryingThePicture()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    new NeighbourhoodWorldHealth(failure: "the road graph could not be made connected")),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    WarmingUp(NeighbourhoodBackdropRule.MeshWarmUpSeconds + 1d)),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    new NeighbourhoodWorldHealth(attached: true, groundQuads: 0, roadQuads: 0,
                        lotCount: 12)),
            };
        }
    }
}