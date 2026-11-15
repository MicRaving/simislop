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
    /// The backdrop contract: the painted map is the guaranteed backdrop, the generated world has
    /// to earn the right to hide it, and any failure puts it straight back.
    ///
    /// <para>
    /// <b>Why these are all pure.</b> The thing that went wrong in the field was a world that
    /// <em>built</em> - <c>TryCreate</c> returned a scene, so the panel swapped the painted map for
    /// a transparent quad - and then rendered nothing, so the player was left with lot buttons over
    /// a blank field. Nothing about that is observable except by looking at a screen, which is why
    /// the decision had to become a value: every failure mode below is a plain input to
    /// <see cref="NeighbourhoodBackdropRule.Decide"/>, and every one of them now has to say "2D" or
    /// "both", never "neither".
    /// </para>
    ///
    /// <para>
    /// One test at the bottom is the guard against the rule being <em>too</em> eager: it runs the
    /// real layout packer, the real 512x512 generator and the real mesh probe, and asserts the
    /// health they produce promotes to 3D. A rule that never fell back would pass every test above
    /// it; a rule that always fell back would pass none, and that one is much easier to write by
    /// accident.
    /// </para>
    ///
    /// <para>
    /// <b>Two field reports, two halves of the same promise.</b> The first was a world that held
    /// 315424 triangles and submitted none of them, which is why the gate counts submissions. The
    /// second was a world that submitted 60660 terrain triangles on three consecutive frames and
    /// was promoted anyway, because "demonstrably produced something" had been read as "demonstrably
    /// produced some ground" - and the player was looking at a bare plain with the houses still
    /// streaming in. That is why the gate also counts houses, and why the tests in the last
    /// section are about facades: ground is not a neighbourhood.
    /// </para>
    /// </summary>
    public class NeighbourhoodBackdropTests
    {
        /// <summary>A world that has produced geometry: what the rule needs to say 3D.</summary>
        private static NeighbourhoodWorldHealth Healthy() => new NeighbourhoodWorldHealth(
            attached: true, meshUploaded: true, terrainTriangles: 24576, roadTriangles: 4096,
            groundQuads: 65536, roadQuads: 4096, lotCount: 12, facadeCount: 3, secondsSinceAttach: 2d);

        /// <summary>
        /// A world that attached and has not uploaded anything yet: the state every 3D
        /// neighbourhood is in for the first few frames.
        /// </summary>
        private static NeighbourhoodWorldHealth WarmingUp(double seconds = 0d) => new NeighbourhoodWorldHealth(
            attached: true, meshUploaded: false, terrainTriangles: 0, roadTriangles: 0,
            groundQuads: 65536, roadQuads: 4096, lotCount: 12, facadeCount: 0, secondsSinceAttach: seconds);

        // ---------------------------------------------------------------- the guarantee itself

        [Fact]
        public void ThePaintedMapIsNeverTakenAwayUntilTheWorldHasDrawnSomething()
        {
            //The whole bug, in one assertion. A world that attached and has not uploaded its mesh
            //gets "both": the map is still what the player sees, so a lot button over a blank field
            //is not reachable.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, WarmingUp());

            Assert.True(decision.ShowBoth);
            Assert.True(decision.UseBitmap);
            Assert.True(decision.UseWorld);
            Assert.False(decision.Show3D);
            Assert.False(decision.Proven);
            Assert.False(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.None, decision.Fault);
            Assert.Equal(NeighbourhoodBackdrop.PaintedMapAndWorld, decision.Backdrop);
        }

        [Fact]
        public void NoCombinationOfCircumstancesLeavesThePlayerWithNeither()
        {
            //The invariant, over every combination rather than the ones a reader thought of. Each
            //axis is a thing that can be true of a world at the same time as the others, and the
            //answer must never be "nothing is drawn" - which for this panel means a dark field
            //with lot buttons on it.
            var decisions = 0;
            foreach (var attached in new[] { false, true })
            foreach (var uploaded in new[] { false, true })
            foreach (var triangles in new[] { 0, 512 })
            foreach (var ground in new[] { NeighbourhoodWorldHealth.Unknown, 0, 4096 })
            foreach (var roads in new[] { NeighbourhoodWorldHealth.Unknown, 0, 512 })
            foreach (var lots in new[] { 0, 12 })
            foreach (var failures in new[] { 0, 1, 2, 5 })
            foreach (var lost in new[] { false, true })
            foreach (var seconds in new[] { 0d, 1d, 60d })
            {
                var health = new NeighbourhoodWorldHealth(attached, uploaded, triangles,
                    triangles > 0 ? 128 : 0, ground, roads, lots, 0, failures, lost, seconds,
                    failures > 0 ? "the device went away" : null);
                var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, health);

                Assert.True(decision.UseBitmap || decision.UseWorld,
                    "neither backdrop for " + health);
                // And the specific half of it: the map goes away only for the one state.
                Assert.Equal(decision.Show3D, decision.Proven);
                if (decision.Show3D)
                {
                    Assert.False(decision.UseBitmap);
                }
                else
                {
                    Assert.True(decision.UseBitmap, "the painted map vanished for " + health);
                }
                Assert.False(string.IsNullOrWhiteSpace(decision.Reason), health.ToString());
                decisions++;
            }

            Assert.Equal(2 * 2 * 2 * 3 * 3 * 2 * 4 * 2 * 3, decisions);
        }

        // ------------------------------------------------------------------------ generation

        [Fact]
        public void GenerationFailureIsThePaintedMapWithTheReason()
        {
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth(failure: "the road graph could not be made connected"));

            Assert.True(decision.Show2D);
            Assert.False(decision.UseWorld);
            Assert.True(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.SceneCreateFailed, decision.Fault);
            Assert.Contains("road graph", decision.Reason);
        }

        [Fact]
        public void GenerationFailureWithNoReasonStillSaysWhyItGaveUp()
        {
            // A fallback with no stated reason is the case worth chasing, so there is no path
            // through the rule that produces one - not even the path where nothing threw and
            // nothing was produced.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth());

            Assert.True(decision.Show2D);
            Assert.True(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.GenerationFailed, decision.Fault);
            Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
        }

        [Fact]
        public void NoWastedWarmUpOnAWorldThatWasNeverBuilt()
        {
            // A world that does not exist is not "still uploading". Waiting for it would leave the
            // player looking at the painted map for eight seconds for no reason, and - worse - it
            // would be indistinguishable in the log from a world that is genuinely building.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth(attached: false, secondsSinceAttach: 99d,
                    failure: "the layout could not be packed"));

            Assert.True(decision.Show2D);
            Assert.True(decision.FellBack);
        }

        // ------------------------------------------------------------------------- mesh upload

        [Fact]
        public void AMeshThatNeverUploadsEventuallyGivesThePaintedMapBack()
        {
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                WarmingUp(NeighbourhoodBackdropRule.MeshWarmUpSeconds + 0.1d));

            Assert.True(decision.Show2D);
            Assert.True(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.MeshNeverUploaded, decision.Fault);
            Assert.Contains("had not uploaded", decision.Reason);
        }

        [Fact]
        public void TheWarmUpWindowIsInclusiveAtItsEdgeAndExclusiveBeyondIt()
        {
            // Off-by-one here is a real difference in behaviour: exclusive at the edge throws the
            // world away on the exact frame it finishes uploading.
            var edge = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                WarmingUp(NeighbourhoodBackdropRule.MeshWarmUpSeconds));
            var past = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                WarmingUp(NeighbourhoodBackdropRule.MeshWarmUpSeconds + 0.001d));

            Assert.True(edge.ShowBoth);
            Assert.True(past.Show2D);
        }

        [Fact]
        public void TheWarmUpWindowIsGenerousEnoughToCoverASlowFirstUpload()
        {
            // The mesh is built on a worker and uploaded on the game thread, so a cold start pays a
            // whole frame's latency plus the device's own upload. A window too tight to cover a
            // normal cold start would tear down every world exactly once.
            Assert.True(NeighbourhoodBackdropRule.MeshWarmUpSeconds >= 4d,
                "a normal cold start can take longer than this");
        }

        [Fact]
        public void AnUploadThatProducedNothingIsNotAWorldThatIsMerelyEarly()
        {
            // Belt and braces on the derived signal. The panel reads "uploaded" as "the device
            // holds a triangle", so it cannot hand the rule this state - but a scene that publishes
            // its own INeighbourhoodRenderSignal can, and a mesh that uploaded with nothing in it
            // is a failure rather than a wait.
            var emptyUpload = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 0, groundQuads: 65536,
                roadQuads: 4096, lotCount: 12, secondsSinceAttach: NeighbourhoodBackdropRule.MeshWarmUpSeconds + 1d);

            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, emptyUpload).Show2D);

            // and, inside the window, it is given the same grace as a mesh that has not landed,
            // because the difference between the two is not something a player can see
            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth(attached: true, meshUploaded: true, terrainTriangles: 0,
                    groundQuads: 65536, roadQuads: 4096, lotCount: 12)).ShowBoth);
        }

        // ---------------------------------------------------------------------------- geometry

        [Fact]
        public void AMapWithNoGroundIsThePaintedMapFromTheFirstFrame()
        {
            // No grace period: this is knowable before the GPU is touched, so there is nothing to
            // wait for. A zero-quad mesh can never draw a neighbourhood.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth(attached: true, groundQuads: 0, roadQuads: 0,
                    lotCount: 12, secondsSinceAttach: 0d));

            Assert.True(decision.Show2D);
            Assert.True(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.NoGround, decision.Fault);
            Assert.Contains("no ground quads", decision.Reason);
        }

        [Fact]
        public void AMapWithNoRoadsIsThePaintedMapToo()
        {
            // This is the report from the field: houses with nothing between them. The painted map's
            // ground between the properties is the thing a lot button over an empty 3D field cannot
            // replace, so it is treated as the same failure as no ground at all.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth(attached: true, groundQuads: 65536, roadQuads: 0,
                    lotCount: 12, secondsSinceAttach: 0d));

            Assert.True(decision.Show2D);
            Assert.True(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.NoRoads, decision.Fault);
            Assert.Contains("no road quads", decision.Reason);
        }

        [Fact]
        public void ARoadlessWorldWithNoLotsInItIsNotCalledBroken()
        {
            // A neighbourhood whose location table named nothing has no lots, and "no roads" is the
            // right answer for it rather than a fault - there is nobody to drive to.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth(attached: true, meshUploaded: true, terrainTriangles: 32,
                    groundQuads: 4096, roadQuads: 0, lotCount: 0, secondsSinceAttach: 1d));

            Assert.True(decision.Show3D);
            Assert.False(decision.FellBack);
        }

        [Fact]
        public void AnUncountedMapIsNotTreatedAsAnEmptyOne()
        {
            // "Unknown" is not "zero". The probe can fail to run, and the safe reading of a count
            // nobody produced is that the map has not been condemned - the live rules still apply,
            // so a world that demonstrably drew is still promoted.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth(attached: true, meshUploaded: true, terrainTriangles: 900,
                    groundQuads: NeighbourhoodWorldHealth.Unknown, roadQuads: NeighbourhoodWorldHealth.Unknown,
                    lotCount: 12, secondsSinceAttach: 1d));

            Assert.True(decision.Show3D);
        }

        [Fact]
        public void ARealGeneratedMapIsNotEmptyAndNotRoadless()
        {
            // The guard against the rule being too eager, on real data: pack, generate, probe, and
            // the health the panel would build from all of that has to promote to 3D. A rule that
            // never fell back passes every test above; this is the one that catches it.
            var layout = NeighbourhoodLayoutBuilder.PackOnGrid(new[]
            {
                // CHANGED by the Part 9 TSO-alignment rework. Forty bitmap pixels are twenty-six tiles apart
                // under the axis-aligned square mapping and twenty-two under the diamond-aligned
                // projection, so four 16-tile lots on a forty-pixel grid leave no corridor between
                // them and the generator rightly refuses. Eighty pixels is thirty and twenty-six.
                Read(11, 20, 20, 16), Read(12, 20, 100, 16), Read(13, 100, 20, 16), Read(14, 100, 100, 16)
            }, 4, 12345);
            var map = NeighbourhoodMapGenerator.Generate(NeighbourhoodMapAdapter.InputFor(layout));
            var probe = NeighbourhoodCityMeshProbe.Probe(map);

            var health = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: probe.TotalQuads,
                groundQuads: probe.TotalLayerQuads, roadQuads: probe.RoadQuads + probe.CornerQuads,
                lotCount: layout.LotCount, facadeCount: 0, secondsSinceAttach: 0.5d);
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, health);

            Assert.True(probe.TotalLayerQuads > 0, "a generated neighbourhood should have ground");
            Assert.True(probe.RoadQuads + probe.CornerQuads > 0, "a generated neighbourhood should have roads");
            Assert.True(decision.Show3D, decision.Reason);
            Assert.False(decision.FellBack);
        }

        // ------------------------------------------------------------------------ live failures

        [Fact]
        public void ADrawThatThrowsRepeatedlyPutsThePaintedMapBackImmediately()
        {
            // "Immediately" is the point: the world had already been promoted, so this is the
            // recovery half of the contract and it cannot afford a warm-up window.
            var broken = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 24576, groundQuads: 65536,
                roadQuads: 4096, lotCount: 12, drawFailures: NeighbourhoodBackdropRule.DrawFailureLimit,
                secondsSinceAttach: 30d, failure: "the device was removed");

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, broken);

            Assert.True(decision.Show2D);
            Assert.True(decision.UseBitmap);
            Assert.True(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.DrawFailed, decision.Fault);
            Assert.Contains("device was removed", decision.Reason);
        }

        [Fact]
        public void OneBadFrameIsNotAVerdictOnAWorld()
        {
            // A single failed draw is a hiccup the next frame recovers from. Falling back on it
            // would flicker the backdrop on any machine that hiccups once.
            var hiccup = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 24576, groundQuads: 65536,
                roadQuads: 4096, lotCount: 12, drawFailures: 1, secondsSinceAttach: 30d,
                failure: "transient");

            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, hiccup).Show3D);
        }

        [Fact]
        public void ADeviceThatWentAwayPutsThePaintedMapBack()
        {
            var lost = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 24576, groundQuads: 65536,
                roadQuads: 4096, lotCount: 12, deviceLost: true, secondsSinceAttach: 30d);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, lost);

            Assert.True(decision.Show2D);
            Assert.True(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.DeviceLost, decision.Fault);
        }

        [Fact]
        public void ThePaintedMapComesBackAndTheWorldCanWinItAgain()
        {
            // The full recovery arc, in order: 3D, then a device reset puts the map back, then the
            // world draws once and takes the screen again. The last step is the one that would be
            // lost if the rule latched.
            var promoted = Healthy();
            var reset = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: false, terrainTriangles: 0, groundQuads: 65536,
                roadQuads: 4096, lotCount: 12, deviceLost: true, secondsSinceAttach: 0d);
            var recovered = Healthy();

            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, promoted).Show3D);
            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, reset).Show2D);
            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, recovered).Show3D);
        }

        // ---------------------------------------------------------------------- the 2D contract

        [Fact]
        public void OffMeansTheBitmapAndNothingAWorldCanSayChangesThat()
        {
            // The default-identical-when-off promise. A 2D neighbourhood reporting a perfectly
            // healthy world still gets the painted map, for the reason the player chose, with no
            // fault recorded - because nothing failed.
            foreach (var view in new[] { NeighbourhoodView.Off, NeighbourhoodView.Bitmap2D })
            {
                var decision = NeighbourhoodBackdropRule.Decide(view, Healthy());

                Assert.True(decision.Show2D);
                Assert.False(decision.UseWorld);
                Assert.False(decision.FellBack);
                Assert.Equal(NeighbourhoodWorldFault.None, decision.Fault);
                Assert.Equal(NeighbourhoodViewSelection.ForSetting(view).Reason, decision.Reason);
            }
        }

        [Fact]
        public void OffIgnoresEvenAWorldThatIsAlreadyBroken()
        {
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.Off,
                new NeighbourhoodWorldHealth(attached: false, failure: "something went wrong"));

            Assert.True(decision.Show2D);
            Assert.False(decision.FellBack);
            Assert.DoesNotContain("something went wrong", decision.Reason);
        }

        [Fact]
        public void ASettingChangedToGarbageStillReachesARealAnswer()
        {
            var decision = NeighbourhoodBackdropRule.Decide((NeighbourhoodView)77, Healthy());

            Assert.Equal(NeighbourhoodViewSetting.Default, decision.Requested);
            // The default is the painted map since the revert, so garbage lands on 2D - a real
            // answer rather than a view nothing handles.
            Assert.True(decision.Show2D);
        }

        [Theory]
        [InlineData(NeighbourhoodView.Off, false, NeighbourhoodBackdropAction.Keep)]
        [InlineData(NeighbourhoodView.Off, true, NeighbourhoodBackdropAction.Detach)]
        [InlineData(NeighbourhoodView.Bitmap2D, false, NeighbourhoodBackdropAction.Keep)]
        [InlineData(NeighbourhoodView.Bitmap2D, true, NeighbourhoodBackdropAction.Detach)]
        [InlineData(NeighbourhoodView.World3D, false, NeighbourhoodBackdropAction.Attach)]
        [InlineData(NeighbourhoodView.World3D, true, NeighbourhoodBackdropAction.Keep)]
        public void ChangingTheSettingWhileTheMapIsUpIsReconciled(
            NeighbourhoodView view, bool attached, NeighbourhoodBackdropAction expected)
        {
            // Every cell, because this is the live-switch case and the two interesting ones are the
            // off-diagonal: 3D on with nothing attached (build one) and 3D off with a world already
            // up (drop it, or the setting would be quietly not the setting).
            Assert.Equal(expected, NeighbourhoodBackdropRule.Reconcile(view, attached));
        }

        [Fact]
        public void TurningThreeDOnLiveNeverFlashesAnEmptyField()
        {
            // The world that Attach builds has not uploaded its mesh yet, so the very next decision
            // is "both" - which is the whole reason the middle value exists.
            var justBuilt = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: false, groundQuads: 65536, roadQuads: 4096,
                lotCount: 12, secondsSinceAttach: 0d);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, justBuilt);

            Assert.True(decision.ShowBoth);
            Assert.True(decision.UseBitmap);
        }

        [Fact]
        public void ChangingTheSettingIsReReportedAsAChoiceNotAFailure()
        {
            // A world that had fallen back, with the player then switching 3D off: the new answer is
            // the one they chose. A log that kept saying "the generated world gave up" after the
            // player turned it off would be describing a world they had explicitly dismissed.
            var failed = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                new NeighbourhoodWorldHealth(failure: "the road graph would not connect"));

            var after = NeighbourhoodBackdropRule.AfterSettingChange(failed, NeighbourhoodView.Off);

            Assert.True(after.Show2D);
            Assert.False(after.FellBack);
            Assert.DoesNotContain("road graph", after.Reason);
        }

        // ------------------------------------------------------- what the backdrop element does

        [Fact]
        public void TheBackdropElementIsThePaintedMapUntilTheWorldCarriesThePicture()
        {
            var warming = NeighbourhoodBackdropFrame.For(
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, WarmingUp()), true);
            var drawn = NeighbourhoodBackdropFrame.For(
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, Healthy()), true);

            Assert.True(warming.IsPaintedMap);
            Assert.True(warming.OverlaysVisible);
            Assert.False(warming.IsTransparentQuad);

            Assert.False(drawn.IsPaintedMap);
            Assert.False(drawn.OverlaysVisible);
            Assert.True(drawn.IsTransparentQuad);
        }

        [Fact]
        public void AnInstallWithNoBitmapKeepsItsBackdropElementRatherThanShrinkingIt()
        {
            // Nothing to blank, so nothing is blanked. The alternative - shrinking an element that
            // is already showing nothing - would buy no pixels and would cost the backdrop click its
            // hit area, so a player could no longer click away from a zoomed-in lot.
            var overWorld = NeighbourhoodBackdropFrame.For(
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, Healthy()), false);

            Assert.True(overWorld.IsPaintedMap);
            Assert.True(overWorld.OverlaysVisible);
        }

        [Fact]
        public void TheBackdropElementOnlyHasTwoStatesAndTheyAreDistinct()
        {
            Assert.False(NeighbourhoodBackdropFrame.PaintedMap.DiffersFrom(NeighbourhoodBackdropFrame.PaintedMap));
            Assert.False(NeighbourhoodBackdropFrame.OverWorld.DiffersFrom(NeighbourhoodBackdropFrame.OverWorld));
            Assert.True(NeighbourhoodBackdropFrame.PaintedMap.DiffersFrom(NeighbourhoodBackdropFrame.OverWorld));
            Assert.True(NeighbourhoodBackdropFrame.OverWorld.DiffersFrom(NeighbourhoodBackdropFrame.PaintedMap));

            //overlays always travel with the map, so "the map is back" and "the waves are back" can
            //never be two separate things that can disagree
            Assert.True(NeighbourhoodBackdropFrame.PaintedMap.OverlaysVisible);
            Assert.False(NeighbourhoodBackdropFrame.OverWorld.OverlaysVisible);
        }

        // ---------------------------------------------------------------- the log, and the map

        [Fact]
        public void EveryBackdropDecisionNamesItselfForTheLog()
        {
            var decisions = new[]
            {
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.Off, Healthy()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.Bitmap2D, Healthy()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, WarmingUp()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, Healthy()),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, new NeighbourhoodWorldHealth(failure: "nope")),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    WarmingUp(NeighbourhoodBackdropRule.MeshWarmUpSeconds + 1d)),
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    new NeighbourhoodWorldHealth(attached: true, groundQuads: 0, lotCount: 5)),
            };

            foreach (var decision in decisions)
            {
                Assert.False(string.IsNullOrWhiteSpace(decision.Reason));
                Assert.False(string.IsNullOrWhiteSpace(decision.ToString()));
            }

            //and a decision that fell back says so in its own ToString, because that is what the
            //log line a player reads is built from
            Assert.Contains("fell back",
                NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    new NeighbourhoodWorldHealth(failure: "nope")).ToString());
        }

        // ------------------------------------------------- what was actually submitted
        //
        // The field report: "the generated world has drawn 315424 terrain triangle(s) and 38 lot
        // facade(s), so it is carrying the picture" - and a blank screen. 315424 was what the
        // *mesh held*. Nothing counted what a frame submitted, so a world whose every chunk was
        // culled, or whose render state discarded everything, or whose camera was looking away from
        // the map, all read as healthy. These are the tests for the numbers that close that gap.

        /// <summary>A world whose frames submit geometry, as the scene now reports it.</summary>
        private static NeighbourhoodWorldHealth Submitting(int triangles = 24576, int frames = 5) =>
            new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 315424, roadTriangles: 4096,
                groundQuads: 65536, roadQuads: 4096, lotCount: 12, facadeCount: 38,
                secondsSinceAttach: 2d, submittedTriangles: triangles, submittedFrames: frames);

        [Fact]
        public void AWorldThatSubmitsNothingNeverTakesTheScreenAwayFromThePaintedMap()
        {
            // The whole bug, as a rule. A world holding 315424 triangles and submitting none of them
            // is exactly the field report, and it must read as "both" while it is inside the
            // warm-up window - the painted map stays up, and the world keeps its place behind it
            // rather than being torn down.
            var blank = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 315424, roadTriangles: 4096,
                groundQuads: 65536, roadQuads: 4096, lotCount: 12, facadeCount: 38,
                secondsSinceAttach: 2d, submittedTriangles: 0, submittedFrames: 0);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, blank);

            Assert.True(decision.ShowBoth);
            Assert.True(decision.UseBitmap);
            Assert.True(decision.UseWorld);
            Assert.False(decision.Show3D);
            Assert.False(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.None, decision.Fault);
        }

        [Fact]
        public void AWorldThatKeepsSubmittingNothingEventuallyGivesThePaintedMapBack()
        {
            // Past the warm-up window, "still coming up" stops being true. The fault is named
            // separately from MeshNeverUploaded because the mesh *is* there, and the log saying
            // "315424 triangles" while the screen is blank is the confusing part of this report.
            var blank = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 315424, roadTriangles: 4096,
                groundQuads: 65536, roadQuads: 4096, lotCount: 12, facadeCount: 38,
                secondsSinceAttach: NeighbourhoodBackdropRule.MeshWarmUpSeconds + 1d,
                submittedTriangles: 0, submittedFrames: 0);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, blank);

            Assert.True(decision.Show2D);
            Assert.True(decision.FellBack);
            Assert.True(decision.UseBitmap);
            Assert.Equal(NeighbourhoodWorldFault.NothingSubmitted, decision.Fault);
            Assert.Contains("submitted none of them", decision.Reason);
        }

        [Fact]
        public void AWorldThatDrawsOneFrameOnItsWayToFailingDoesNotTakeTheScreen()
        {
            // Why the gate is a run of frames rather than a single one. One good frame out of a
            // hundred failures is not a world that has demonstrated anything, and the run counter
            // is what makes that true: ProvenFrames is 1 here, not 3.
            var blink = new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: 315424,
                groundQuads: 65536, roadQuads: 4096, lotCount: 12,
                secondsSinceAttach: 30d, submittedTriangles: 24576, submittedFrames: 1);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, blink);

            Assert.True(decision.ShowBoth);
            Assert.True(decision.UseBitmap);
            Assert.False(decision.Show3D);
        }

        [Fact]
        public void PromotionNeedsSeveralConsecutiveDrawingFramesNotOne()
        {
            // And the boundary, because off-by-one here is a blank screen. Below the threshold is
            // "both"; at it, the world has earned the screen.
            for (var frames = 0; frames < NeighbourhoodBackdropRule.PromotionFrames; frames++)
            {
                var partial = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    Submitting(24576, frames));
                Assert.True(partial.ShowBoth,
                    "the world was promoted after only " + frames + " drawing frame(s)");
                Assert.True(partial.UseBitmap);
            }

            var enough = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                Submitting(24576, NeighbourhoodBackdropRule.PromotionFrames));

            Assert.True(enough.Show3D, enough.Reason);
            Assert.False(enough.UseBitmap);
            Assert.True(enough.Proven);
        }

        [Fact]
        public void ThePromotionReasonCountsWhatWasSubmittedNotWhatTheMeshHolds()
        {
            // The log line is the thing a player sends in, so it has to say which of the two numbers
            // it is. "submitted 24576 on 5 consecutive frames" is actionable; "315424 triangles" is
            // the sentence that sent us looking in the wrong place.
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, Submitting());

            Assert.Contains("submitted 24576", decision.Reason);
            Assert.Contains("5 consecutive frame", decision.Reason);
            Assert.Contains("38 lot facade", decision.Reason);
        }

        [Fact]
        public void AWorldNobodyIsCountingFallsBackToTheOlderPromise()
        {
            // A signal that does not report submissions - an older scene, or a fake in a test -
            // must not be treated as a world that submitted nothing. That would put every such world
            // on the painted map forever, which is the same failure the rule exists to prevent, in
            // the other direction. Unknown is not zero.
            var uncounted = Healthy();

            Assert.False(uncounted.CountsSubmission);
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, uncounted);

            Assert.True(decision.Show3D, decision.Reason);
            Assert.Contains("24576 terrain triangle", decision.Reason);
        }

        [Fact]
        public void NoCombinationOfCircumstancesPromotesAWorldThatSubmitsNothing()
        {
            // The invariant this change is for, over the same axes as the guarantee test above plus
            // the two new ones. Whatever else is true of a world, a frame that submitted nothing
            // and a run of fewer than the required frames must leave the painted map up.
            var checked_ = 0;
            foreach (var attached in new[] { false, true })
            foreach (var uploaded in new[] { false, true })
            foreach (var triangles in new[] { 0, 315424 })
            foreach (var submitted in new[] { 0, 1, 24576 })
            foreach (var frames in new[] { 0, 1, 2, 3, 50 })
            foreach (var ground in new[] { NeighbourhoodWorldHealth.Unknown, 0, 65536 })
            foreach (var roads in new[] { NeighbourhoodWorldHealth.Unknown, 0, 4096 })
            foreach (var lots in new[] { 0, 12 })
            foreach (var failures in new[] { 0, 1, 2 })
            foreach (var seconds in new[] { 0d, 1d, 60d })
            {
                var health = new NeighbourhoodWorldHealth(attached, uploaded, triangles, 4096,
                    ground, roads, lots, 38, failures, false, seconds, null, submitted, frames);
                var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, health);

                Assert.True(decision.UseBitmap || decision.UseWorld, "neither for " + health);
                if (submitted <= 0)
                {
                    Assert.True(decision.UseBitmap, "the painted map vanished for " + health);
                }
                if (submitted > 0 && frames < NeighbourhoodBackdropRule.PromotionFrames)
                {
                    Assert.True(decision.UseBitmap,
                        "the world was promoted on " + frames + " drawing frame(s): " + health);
                }
                checked_++;
            }

            Assert.Equal(2 * 2 * 2 * 3 * 5 * 3 * 3 * 2 * 3 * 3, checked_);
        }

        // ---------------------------------------------------------------------- the houses
        //
        // The second field report, and the one this gate exists for:
        //
        //   frame 11 submitted 60660 triangle(s) ... terrain 60660 (DREW), facades 10424
        //   triangle(s) ... over 38 visible lot(s)
        //   the generated world has submitted 60660 triangle(s) on 3 consecutive frame(s), so it
        //   is carrying the picture
        //
        // Everything above the gate was satisfied - the mesh had uploaded, the raster had ground
        // and roads, and three consecutive frames had submitted sixty thousand triangles - and the
        // player was looking at a bare plain with the houses still streaming in. "Demonstrably
        // produced something" turned out to mean "demonstrably produced some ground". These are
        // the tests for the second half of that promise: the world has to produce a *neighbourhood*.

        /// <summary>
        /// A world that is drawing, and knows how much of its housing is built - which is what the
        /// scene reports now, and what the gate needs.
        /// </summary>
        private static NeighbourhoodWorldHealth WithHouses(
            int facadeTriangles = 4128, int facadesVisible = 12, int built = 12, int eligible = 12,
            int lots = 12, double seconds = 2d, int submittedTriangles = 24576, int frames = 5,
            int terrainTriangles = 24576)
        {
            return new NeighbourhoodWorldHealth(
                attached: true, meshUploaded: true, terrainTriangles: terrainTriangles,
                roadTriangles: 4096, groundQuads: 65536, roadQuads: 4096, lotCount: lots,
                facadeCount: built, secondsSinceAttach: seconds,
                submittedTriangles: submittedTriangles, submittedFrames: frames,
                facadeTriangles: facadeTriangles, facadesVisible: facadesVisible,
                facadeEligible: eligible);
        }

        [Fact]
        public void AWorldWithNoHousesOnScreenNeverTakesTheScreenAwayFromThePaintedMap()
        {
            // The rule, in one assertion. Ground on the device, sixty thousand triangles submitted
            // for three consecutive frames, and not one facade among them: the player would be
            // looking at a plain with a lot button floating over it, so the map stays.
            var bare = WithHouses(facadeTriangles: 0, facadesVisible: 0, built: 12, seconds: 2d,
                submittedTriangles: 60660, frames: 3, terrainTriangles: 60660);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, bare);

            Assert.True(decision.ShowBoth, decision.Reason);
            Assert.True(decision.UseBitmap);
            Assert.True(decision.UseWorld);
            Assert.False(decision.Show3D);
            Assert.False(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.None, decision.Fault);
        }

        [Fact]
        public void TheReportedWorldIsNotPromotedAndTheSameWorldWithItsHousesIs()
        {
            // The log line from the report, as a test. Every condition the old rule had is satisfied:
            // the mesh is up, the raster had ground and roads, and three consecutive frames
            // submitted 60660 terrain triangles. The old rule called that "carrying the picture" and
            // took the painted map away over a plain with a street of cutouts on it, because 38 of
            // the map's 100 lots had a house built at the time.
            var reported = WithHouses(facadeTriangles: 10424, facadesVisible: 38, built: 38,
                eligible: 100, seconds: 2d, submittedTriangles: 60660, frames: 3,
                terrainTriangles: 60660);

            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, reported).ShowBoth,
                "the world from the report was promoted over the painted map again");

            //and the same world, with the rest of its houses built, is what the map was waiting for
            var finished = WithHouses(facadeTriangles: 10424, facadesVisible: 38, built: 100,
                eligible: 100, seconds: 2d, submittedTriangles: 60660, frames: 3,
                terrainTriangles: 60660);

            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, finished).Show3D);
        }

        [Fact]
        public void AWorldWithNoHousesOnScreenEventuallyGivesThePaintedMapBack()
        {
            // Past the warm-up window, "still coming up" stops being true - and the two house faults
            // are separate, because one is about the frame and one is about the build.
            var never = WithHouses(facadeTriangles: 0, facadesVisible: 0, built: 12,
                seconds: NeighbourhoodBackdropRule.MeshWarmUpSeconds + 1d);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, never);

            Assert.True(decision.Show2D);
            Assert.True(decision.FellBack);
            Assert.True(decision.UseBitmap);
            Assert.Equal(NeighbourhoodWorldFault.NoFacadesOnScreen, decision.Fault);
            Assert.Contains("no lot facade at all", decision.Reason);
        }

        [Fact]
        public void AWorldWhoseHousesAreStillBeingBuiltKeepsThePaintedMapOverIt()
        {
            // The build side of the same promise. Twelve facades of a hundred is a street of
            // cutouts on a plain, and the plain is what the player complains about, so the map
            // stays - over the world, which keeps drawing and is warm when the cut comes.
            var streaming = WithHouses(facadeTriangles: 4128, facadesVisible: 12, built: 12,
                eligible: 100, seconds: 2d);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, streaming);

            Assert.True(decision.ShowBoth, decision.Reason);
            Assert.True(decision.UseBitmap);
            Assert.True(decision.UseWorld);
            Assert.False(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.None, decision.Fault);
            Assert.Contains("12 of 100 built", decision.Reason);
        }

        [Fact]
        public void HousesThatNeverFinishBuildingEventuallyGiveThePaintedMapBack()
        {
            var streaming = WithHouses(facadeTriangles: 4128, facadesVisible: 12, built: 12,
                eligible: 100, seconds: NeighbourhoodBackdropRule.MeshWarmUpSeconds + 1d);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, streaming);

            Assert.True(decision.Show2D);
            Assert.True(decision.FellBack);
            Assert.Equal(NeighbourhoodWorldFault.FacadesIncomplete, decision.Fault);
            Assert.Contains("12 of 100", decision.Reason);
        }

        [Fact]
        public void AWorldWithEnoughHousesOnScreenIsPromoted()
        {
            // The other side of both gates: this is what the map is waiting for.
            var presentable = WithHouses(facadeTriangles: 4128, facadesVisible: 12, built: 100,
                eligible: 100);

            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, presentable);

            Assert.True(decision.Show3D, decision.Reason);
            Assert.False(decision.UseBitmap);
            Assert.False(decision.FellBack);
            Assert.True(decision.Proven);
            Assert.Contains("4128 facade triangle(s) over 12 visible lot(s)", decision.Reason);
        }

        [Fact]
        public void TheHouseGateWantsMostOfTheHousingAndNotAllOfIt()
        {
            // The fraction boundary, because off-by-one here is either a bare plain or a painted
            // map that never comes off. Below the bar is not promoted; at it, and above, is.
            for (var built = 0; built < 75; built++)
            {
                Assert.False(NeighbourhoodBackdropRule.EnoughFacades(built, 100),
                    "only " + built + " of 100 facades was called enough");
            }
            Assert.True(NeighbourhoodBackdropRule.EnoughFacades(75, 100));
            Assert.True(NeighbourhoodBackdropRule.EnoughFacades(100, 100));
            Assert.Equal(0.75d, NeighbourhoodBackdropRule.MinimumFacadeFraction);
        }

        [Fact]
        public void AMapWithNoHousesInItIsNotJudgedOnHavingNone()
        {
            // A neighbourhood of empty plots, or one whose lot table names nothing, has no house it
            // can be failing to draw. The terrain pass is the whole of the picture, so zero facade
            // triangles is the correct answer rather than a fault - and the same goes for a map the
            // probe found no lots on at all.
            foreach (var health in new[]
            {
                WithHouses(facadeTriangles: 0, facadesVisible: 0, built: 0, eligible: 0, lots: 9),
                WithoutLots(),
            })
            {
                var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, health);

                Assert.True(decision.Show3D, decision.Reason);
                Assert.False(decision.FellBack);
            }

            //and the fraction itself agrees: a map with nothing to build has built all of it
            Assert.True(NeighbourhoodBackdropRule.EnoughFacades(0, 0));
        }

        /// <summary>A world with no lot locations in it at all, so the gate does not apply.</summary>
        private static NeighbourhoodWorldHealth WithoutLots() => new NeighbourhoodWorldHealth(
            attached: true, meshUploaded: true, terrainTriangles: 24576, groundQuads: 65536,
            roadQuads: 0, lotCount: 0, facadeCount: 0, secondsSinceAttach: 1d,
            submittedTriangles: 24576, submittedFrames: 5, facadeTriangles: 0, facadesVisible: 0,
            facadeEligible: 0);

        [Fact]
        public void ASceneThatCannotSeeTheHousesIsNotHeldOnThePaintedMapForEver()
        {
            // The same rule as an un-counted submission, and for the same reason: a signal that
            // cannot see a thing is not evidence that the thing is missing, and treating it as one
            // would pin the map up on every world whose scene predates the counts. Unknown is not
            // zero - and note this is Healthy(), whose FacadeCount is a real number but whose
            // frame counts are not.
            var uncounted = Healthy();

            Assert.False(uncounted.CountsFacades);
            var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, uncounted);

            Assert.True(decision.Show3D, decision.Reason);
            Assert.Contains("facades not counted", uncounted.ToString());
        }

        [Fact]
        public void ThePaintedMapComesBackWhenTheHousesAreCulledAndTheWorldCanWinItAgain()
        {
            // The reversion half, and the reason the gate is symmetric by construction: the same
            // predicate that refuses to promote a world un-promotes it. Promoted, then the facade
            // pass submits nothing - every surface culled, or every draw failing - then the houses
            // come back and it takes the screen again. Nothing latches, so the last step works.
            var promoted = WithHouses();
            var culled = WithHouses(facadeTriangles: 0, facadesVisible: 12, built: 12, eligible: 12,
                seconds: 30d);
            var recovered = WithHouses();

            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, promoted).Show3D);
            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, culled).Show2D);
            Assert.True(NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, recovered).Show3D);
        }

        [Fact]
        public void TheWarmUpWindowCoversTheHousesAsWellAsTheMesh()
        {
            // The window is one window: the boundary is inclusive at the edge for the houses exactly
            // as it is for the mesh, and exclusive a hair beyond it. Two different faults on the two
            // sides of it.
            var edge = NeighbourhoodBackdropRule.MeshWarmUpSeconds;
            var past = edge + 0.001d;

            foreach (var health in new[]
            {
                //no house has reached the device at all
                WithHouses(facadeTriangles: 0, facadesVisible: 0),
                //houses are on screen, and a quarter of the map is still queued
                WithHouses(facadeTriangles: 4128, facadesVisible: 12, built: 12, eligible: 100),
            })
            {
                var inside = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    WithSeconds(health, edge));
                var beyond = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                    WithSeconds(health, past));

                Assert.True(inside.ShowBoth, "at the edge of the window: " + inside.Reason);
                Assert.False(inside.FellBack);
                Assert.True(beyond.Show2D, "just past the window: " + beyond.Reason);
                Assert.True(beyond.FellBack);
            }
        }

        /// <summary>The same health, re-reported at a different age.</summary>
        private static NeighbourhoodWorldHealth WithSeconds(NeighbourhoodWorldHealth health, double seconds)
        {
            return new NeighbourhoodWorldHealth(
                health.Attached, health.MeshUploaded, health.TerrainTriangles, health.RoadTriangles,
                health.GroundQuads, health.RoadQuads, health.LotCount, health.FacadeCount,
                health.DrawFailures, health.DeviceLost, seconds, health.Failure,
                health.SubmittedTriangles, health.SubmittedFrames, health.FacadeTriangles,
                health.FacadesVisible, health.FacadeEligible);
        }

        [Fact]
        public void TheHouseFaultSaysWhichOfTheTwoThingsWentWrong()
        {
            // Two different failures with one message between them is a bug that costs a day, so the
            // reason distinguishes "the facades were in view and every draw failed" from "there was
            // nothing in view to draw" - a broken facade pass versus a camera pointed at the water.
            var failed = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                WithHouses(facadeTriangles: 0, facadesVisible: 12, seconds: 30d));
            var nothingInView = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D,
                WithHouses(facadeTriangles: 0, facadesVisible: 0, seconds: 30d));

            Assert.Contains("12 facade(s) were in view and every one of their draws failed",
                failed.Reason);
            Assert.Contains("none of the 12 built facade(s) were in view", nothingInView.Reason);
        }

        [Fact]
        public void NoCombinationOfCircumstancesPromotesAWorldWithNoHousesOnIt()
        {
            // The invariant, over the same axes plus the three house ones. Whatever else is true of
            // a world, a map with lots in it that has no house geometry on the screen, or that has
            // less than the bar of its housing built, must not take the painted map away.
            var checked_ = 0;
            foreach (var lotCount in new[] { 0, 12 })
            foreach (var facadeTriangles in new[] { 0, 4128 })
            foreach (var facadesVisible in new[] { 0, 12 })
            foreach (var built in new[] { 0, 12, 100 })
            foreach (var eligible in new[] { 0, 100 })
            foreach (var submitted in new[] { 0, 60660 })
            foreach (var frames in new[] { 0, 3, 50 })
            foreach (var seconds in new[] { 0d, 2d, 60d })
            {
                var health = new NeighbourhoodWorldHealth(
                    attached: true, meshUploaded: true, terrainTriangles: 60660, roadTriangles: 4096,
                    groundQuads: 65536, roadQuads: 4096, lotCount: lotCount, facadeCount: built,
                    secondsSinceAttach: seconds, submittedTriangles: submitted, submittedFrames: frames,
                    facadeTriangles: facadeTriangles, facadesVisible: facadesVisible,
                    facadeEligible: eligible);
                var decision = NeighbourhoodBackdropRule.Decide(NeighbourhoodView.World3D, health);

                Assert.True(decision.UseBitmap || decision.UseWorld, "neither for " + health);
                Assert.False(string.IsNullOrWhiteSpace(decision.Reason), health.ToString());

                //The gate only applies to a map that has lots and houses to put on them; a
                //neighbourhood of empty plots has nothing it can be failing to draw.
                var housesExpected = lotCount > 0 && eligible > 0;
                var enoughHouses = !housesExpected
                    || (facadeTriangles > 0
                        && NeighbourhoodBackdropRule.EnoughFacades(built, eligible));
                var enoughFrames = submitted > 0
                    && frames >= NeighbourhoodBackdropRule.PromotionFrames;

                if (decision.Show3D)
                {
                    Assert.True(enoughHouses,
                        "a world with " + built + " of " + eligible + " facades and "
                        + facadeTriangles + " facade triangle(s) was promoted: " + health);
                    Assert.True(enoughFrames, "a world that had not drawn for long enough was"
                        + " promoted: " + health);
                }
                else if (housesExpected && enoughFrames && !enoughHouses
                    && seconds > NeighbourhoodBackdropRule.MeshWarmUpSeconds)
                {
                    //And the other direction: a world that has earned its frames, still has no house
                    //to show, and is past the warm-up window gets its painted map back rather than a
                    //bare plain, and says so. A house still being built is never a fault, so a fault
                    //here means it stopped being built - and inside the window the answer stays
                    //"both", which is the whole of what a slow facade build is supposed to look like.
                    Assert.True(decision.Show2D, "the painted map was not put back: " + health);
                    Assert.True(decision.FellBack, "a bare plain was not called a fault: " + health);
                }
                else if (housesExpected && enoughFrames && !enoughHouses)
                {
                    Assert.True(decision.ShowBoth, "the painted map was dropped while the houses were"
                        + " still being built: " + health);
                    Assert.False(decision.FellBack, "a house being built was called a fault: " + health);
                }
                checked_++;
            }

            Assert.Equal(2 * 2 * 2 * 3 * 2 * 2 * 3 * 3, checked_);
        }

        [Fact]
        public void TheFrameReportIsWhereTheHouseNumbersComeFrom()
        {
            // The panel reads the house gate off the scene's own report, so the report has to carry
            // both numbers and say which is which - "facades drawn" for a frame that put no
            // triangles on the screen is the same mistake the submission count was, one layer over.
            var report = new NeighbourhoodFrameReport();
            var placement = NeighbourhoodCameraPlacement.Framing(
                new Vector3(256f, 4f, 256f), 512f, NeighbourhoodCameraPlacement.DefaultAspect);

            report.Reset(1, placement);
            report.Submit(60660, 181980, 48, 1024);
            report.SubmitProps(10424, 96, 38, 4096);

            Assert.Equal(10424, report.FacadeTriangles);
            Assert.Equal(38, report.FacadesVisible);
            Assert.True(report.FacadeDrawn);
            Assert.Contains("facades 10424 triangle(s)", report.ToString());
            Assert.Contains("38 visible lot(s)", report.ToString());

            //and a frame whose facades submitted nothing says so, rather than reporting the previous
            //frame's street of houses
            report.Reset(2, placement);
            report.Submit(60660, 181980, 48, 1024);
            report.SubmitProps(0, 0, 38, 4096);

            Assert.Equal(0, report.FacadeTriangles);
            Assert.Equal(38, report.FacadesVisible);
            Assert.False(report.FacadeDrawn);
        }

        [Fact]
        public void TheFacadeProgressRatioIsAHonestFractionAndAnHonestUnknown()
        {
            Assert.Equal(0.5d, WithHouses(built: 50, eligible: 100).FacadeProgress);
            Assert.Equal(1d, WithHouses(built: 12, eligible: 0).FacadeProgress);
            Assert.Equal(NeighbourhoodWorldHealth.Unknown, Healthy().FacadeProgress);
            Assert.Equal(NeighbourhoodWorldHealth.Unknown,
                WithHouses(built: 12, eligible: NeighbourhoodWorldHealth.Unknown).FacadeProgress);
        }

        [Fact]
        public void TheHealthSnapshotSaysWhatWasSubmittedAndNotOnlyWhatTheMeshHolds()
        {
            // The two counts have to be separately visible in the log, because "315424 triangles" is
            // what made this bug hard to read.
            var submitting = Submitting(24576, 5);
            Assert.True(submitting.CountsSubmission);
            Assert.Contains("315424 triangle(s) in the mesh", submitting.ToString());
            Assert.Contains("24576 submitted last frame", submitting.ToString());
            Assert.Contains("5 consecutive frame", submitting.ToString());

            var uncounted = Healthy();
            Assert.Contains("submission not counted", uncounted.ToString());
        }

        [Fact]
        public void TheFrameReportDistinguishesAMeshThatHoldsTrianglesFromOneThatDrewThem()
        {
            // The report itself. A mesh of 315424 triangles with a frame that submitted none of them
            // has to *say* so, in one line, without a screenshot - that is the sentence the field
            // report did not have.
            var report = new NeighbourhoodFrameReport();
            var placement = NeighbourhoodCameraPlacement.Framing(
                new Vector3(256f, 4f, 256f), 512f, NeighbourhoodCameraPlacement.DefaultAspect);

            report.Reset(1, placement);
            report.Skip("the city shaders did not load");
            report.MeshTriangles = 315424;

            Assert.False(report.DrawRan);
            Assert.False(report.SubmittedGeometry);
            Assert.Equal(0, report.ProvenFrames);
            var line = report.ToString();
            Assert.Contains("drew nothing", line);
            Assert.Contains("315424", line);
            Assert.Contains("not the same thing as submitting any", line);
        }

        [Fact]
        public void AFrameThatSubmitsGeometryIsProvenOnlyForARunOfFrames()
        {
            // The counter the promotion gate reads, driven directly so the boundary is arithmetic.
            var report = new NeighbourhoodFrameReport();
            var placement = NeighbourhoodCameraPlacement.Framing(
                new Vector3(256f, 4f, 256f), 512f, NeighbourhoodCameraPlacement.DefaultAspect);

            report.Reset(1, placement);
            report.Skip("still uploading");
            Assert.Equal(0, report.ProvenFrames);

            //The run counts *finished* frames, so the frame currently being built is not in it: a
            //frame that has not finished cannot have proven anything, and counting it would let the
            //gate open one frame early.
            for (var frame = 2; frame <= 6; frame++)
            {
                report.Reset(frame, placement);
                //a frame that reached the device and submitted geometry
                report.Submit(24576, 73728, 6, 1024);
                Assert.Equal(frame - 2, report.ProvenFrames);
            }

            //and it reaches the gate's threshold
            Assert.True(report.ProvenFrames >= NeighbourhoodBackdropRule.PromotionFrames);

            //one bad frame resets the run to nothing, which is the whole point of a run
            report.Reset(7, placement);
            report.Skip("every chunk culled");
            Assert.Equal(0, report.ProvenFrames);
        }

        [Fact]
        public void TheFrameReportIsThrottledButNotSilent()
        {
            // A log line every frame is a log nobody reads; a world that has quietly stopped
            // drawing saying nothing is the bug. So: the opening frames, a heartbeat while nothing
            // is submitted, and a change - and nothing else.
            var report = new NeighbourhoodFrameReport();
            var placement = NeighbourhoodCameraPlacement.Framing(
                new Vector3(256f, 4f, 256f), 512f, NeighbourhoodCameraPlacement.DefaultAspect);

            //a healthy world: the opening frames are logged, then it goes quiet
            var opening = new NeighbourhoodFrameReport();
            for (var frame = 1; frame <= 40; frame++)
            {
                opening.Reset(frame, placement);
                opening.Submit(24576, 73728, 6, 1024);
                if (frame <= NeighbourhoodFrameReport.OpeningFrames)
                {
                    Assert.True(opening.ShouldLog, "the opening frame " + frame + " was not logged");
                }
            }
            Assert.False(opening.ShouldLog);

            //a change is logged: the cull starts doing something
            opening.Reset(41, placement);
            opening.Submit(3000, 9000, 3, 40);
            Assert.True(opening.ShouldLog, "a change in what is submitted was not logged");
            Assert.False(opening.ShouldLog);

            //and a world that submits nothing gets a heartbeat rather than a flood
            var quiet = new NeighbourhoodFrameReport();
            for (var frame = 1; frame <= 3 * NeighbourhoodFrameReport.HeartbeatFrames; frame++)
            {
                quiet.Reset(frame, placement);
                quiet.Skip("every chunk culled");
                var expected = frame <= NeighbourhoodFrameReport.OpeningFrames
                    || frame % NeighbourhoodFrameReport.HeartbeatFrames == 0;
                Assert.True(expected == quiet.ShouldLog, "frame " + frame + ": expected "
                    + expected + ", got " + !expected);
            }
        }

        [Fact]
        public void TheHealthSnapshotDescribesItselfForTheLogToo()
        {
            Assert.Contains("no generated world", new NeighbourhoodWorldHealth().ToString());
            Assert.Contains("mesh still uploading", WarmingUp().ToString());
            Assert.Contains("mesh uploaded", Healthy().ToString());
            Assert.Contains("device lost",
                new NeighbourhoodWorldHealth(attached: true, deviceLost: true).ToString());
        }

        [Fact]
        public void TheBitmapFallbackIsAsksForAViewThatHasOne()
        {
            // A view whose background is in the install is used as-is, and the reason is null so
            // the caller stays quiet.
            var reason = "unset";
            Assert.Equal((ushort)4, UINeighborhoodSelectionPanel.ResolveBitmapMode(4, _ => true, out reason));
            Assert.Null(reason);

            //The residential default is Unleashed, so this is the case that matters most in
            //practice: the fallback has to find something when Community\ is not there.
            Assert.Equal("Community\\NScreen_unleashed.bmp", UINeighborhoodSelectionPanel.BitmapOf(4));
        }

        [Fact]
        public void AMissingBitmapFallsBackToAnotherViewAndNamesTheFile()
        {
            // Unleashed is not in this install; the base residential map is. The log line has to
            // name the file that was looked for, or a player reading it cannot tell a missing
            // bitmap from a missing neighbourhood.
            var missing = "Community\\NScreen_unleashed.bmp";
            var reason = "unset";

            var chosen = UINeighborhoodSelectionPanel.ResolveBitmapMode(4,
                view => UINeighborhoodSelectionPanel.BitmapOf(view) != missing, out reason);

            Assert.Equal((ushort)1, chosen);
            Assert.Equal("Nbhd\\NScreen.BMP", UINeighborhoodSelectionPanel.BitmapOf(chosen));
            Assert.Contains(missing, reason);
            Assert.Contains("Community", reason);
        }

        [Fact]
        public void TheBitmapFallbackPrefersTheBaseMapSoItIsPredictable()
        {
            // Order matters: a player with an incomplete install gets the same picture on every
            // neighbourhood rather than a different one each time they travel.
            foreach (var mode in new ushort[] { 1, 2, 3, 4, 5, 7 })
            {
                // only the base map is in this install
                var reason = "unset";
                var chosen = UINeighborhoodSelectionPanel.ResolveBitmapMode(mode,
                    view => view == 1, out reason);

                Assert.Equal((ushort)1, chosen);
                Assert.Equal("Nbhd\\NScreen.BMP", UINeighborhoodSelectionPanel.BitmapOf(chosen));
                if (mode == 1)
                {
                    Assert.Null(reason);
                }
                else
                {
                    Assert.Contains("view 1's 'Nbhd\\NScreen.BMP' is drawn instead", reason);
                }
            }
        }

        [Fact]
        public void AnInstallWithNoBitmapsAtAllSaysSoAndNamesEveryFileItLookedFor()
        {
            // There is nothing left to fall back to, so the only honest thing is to say which files
            // were wanted. A blank field with no explanation is the failure this whole rule exists
            // to prevent, and it has to be loud.
            var reason = "unset";

            Assert.Equal((ushort)0,
                UINeighborhoodSelectionPanel.ResolveBitmapMode(4, _ => false, out reason));
            Assert.Contains("Community\\NScreen_unleashed.bmp", reason);
            Assert.Contains("no background", reason);
        }

        [Fact]
        public void TheBitmapFallbackNeverLandsOnTheEmptySlot()
        {
            // Slot 6 declares no background at all. "No background" is not "a background that
            // happens to be missing", so it is never a candidate - not as the requested view and not
            // as the answer.
            Assert.False(UINeighborhoodSelectionPanel.HasBackground(6));

            //asked for the empty slot, so there is nothing to draw and the base map answers
            var reason = "unset";
            Assert.Equal((ushort)1, UINeighborhoodSelectionPanel.ResolveBitmapMode(6, _ => true, out reason));
            Assert.Contains("declares no background", reason);

            //asked for a view that is not in this install, where the only other thing "available"
            //is the empty slot - which must be skipped rather than chosen
            reason = "unset";
            Assert.Equal((ushort)1,
                UINeighborhoodSelectionPanel.ResolveBitmapMode(7, view => view == 1 || view == 6, out reason));
        }

        [Fact]
        public void TheBitmapFallbackNamesAnOutOfRangeViewRatherThanThrowing()
        {
            // The mode comes from the frontend and the location table, so it is not guaranteed to
            // index the table. Nothing here may throw.
            var reason = "unset";

            Assert.Equal((ushort)1, UINeighborhoodSelectionPanel.ResolveBitmapMode(99, _ => true, out reason));
            Assert.Null(UINeighborhoodSelectionPanel.BitmapOf(0));
            Assert.Null(UINeighborhoodSelectionPanel.BitmapOf(99));
            Assert.False(UINeighborhoodSelectionPanel.HasBackground(0));
            Assert.False(UINeighborhoodSelectionPanel.HasBackground(99));
        }

        [Fact]
        public void EveryViewsBackgroundIsTheOneItsOverlaysAndLotTableExpect()
        {
            // The 2D path itself, confirmed: the seven views the panel can draw, each with a
            // background, each declaring overlays in its own folder. A view whose artwork had been
            // rewritten to point at another folder would look fine here and be wrong on screen.
            var expected = new Dictionary<string, string>
            {
                { "1", "Nbhd\\NScreen.BMP" },
                { "2", "Downtown\\DScreen.bmp" },
                { "3", "VIsland\\visland.bmp" },
                { "4", "Community\\NScreen_unleashed.bmp" },
                { "5", "Studiotown\\DScreen.bmp" },
                { "7", "Magicland\\DScreen.bmp" },
            };

            foreach (var pair in expected)
            {
                var view = ushort.Parse(pair.Key);
                Assert.Equal(pair.Value, UINeighborhoodSelectionPanel.BitmapOf(view));
                Assert.True(UINeighborhoodSelectionPanel.HasBackground(view));
                Assert.Equal(pair.Value.Split('\\')[0],
                    NeighbourhoodAnimations.ViewFolder(
                        NeighbourhoodAnimations.ConfigForMode(
                            UINeighborhoodSelectionPanel.Neighborhoods, (int)view)));
            }
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
    }
}
