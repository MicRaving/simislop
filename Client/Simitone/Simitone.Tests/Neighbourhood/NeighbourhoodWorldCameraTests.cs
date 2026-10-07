/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using Microsoft.Xna.Framework;
using Simitone.Client.Neighbourhood;
using Simitone.Client.UI.Panels;
using Xunit;

namespace Simitone.Tests.Neighbourhood
{
    /// <summary>
    /// B4's world camera: the state machine that follows a walking avatar, and the four
    /// promises that make it usable without a graphics device.
    ///
    /// <para>
    /// <b>It converges.</b> A follow camera that does not reach its target is a camera stuck on
    /// the last thing it was looking at, and the first version of a smoothing filter that never
    /// arrives looks exactly like a deliberate slow follow. So the arrival is pinned as an
    /// arrival, and the <em>approach</em> is pinned too - a filter that reached the target
    /// instantly would pass that test and be worse than useless.
    /// </para>
    ///
    /// <para>
    /// <b>It is frame-rate independent.</b> This is the promise the class exists to make, so it
    /// is pinned as an equality rather than as a tolerance on a feeling: the same elapsed time
    /// in 1, 7, 10 and 60 steps has to leave the camera in the same place. A per-frame spring -
    /// the obvious implementation - passes a convergence test and fails this one, which is
    /// exactly why it needed writing down.
    /// </para>
    ///
    /// <para>
    /// <b>Its modes are states, not flags.</b> A mode change is a change of goal rather than a
    /// cut, and a mode whose subject does not exist is refused outright, because
    /// <c>NeighCameraMachine</c>'s own remarks are about camera states nothing can get out of.
    /// </para>
    ///
    /// <para>
    /// <b>It cannot look off the map.</b> A walker that overshoots a path node, a lot centre
    /// left over from a narrower map, a free camera dragged past the edge: all of them aim at
    /// nothing, and an aim point outside the generated world is a screen full of backdrop.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldCameraTests
    {
        private const int MapSize = 512;
        private const float Ground = 7f;

        private static NeighbourhoodWorldCamera Walking()
        {
            return new NeighbourhoodWorldCamera(MapSize, Ground);
        }

        private static NeighbourhoodWorldCamera At(int mapSize)
        {
            return new NeighbourhoodWorldCamera(mapSize, Ground);
        }

        private static void Run(NeighbourhoodWorldCamera camera, double seconds, double step)
        {
            var steps = (int)Math.Round(seconds / step);
            for (var i = 0; i < steps; i++) camera.Update(step);
        }

        /// <summary>
        /// Advance by exactly <paramref name="seconds"/>, in steps of at most
        /// <paramref name="step"/>. Unlike <see cref="Run"/> this does not round the step count,
        /// because a rounding error here would be a *different elapsed time* and the frame-rate
        /// tests are about the state after an identical number of seconds.
        /// </summary>
        private static void RunExact(NeighbourhoodWorldCamera camera, double seconds, double step)
        {
            var remaining = seconds;
            while (remaining > 1e-12)
            {
                var dt = Math.Min(step, remaining);
                camera.Update(dt);
                remaining -= dt;
            }
        }

        private static void AssertClose(float expected, float actual, float tolerance, string what)
        {
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                what + ": expected " + expected.ToString("0.######") + ", was " + actual.ToString("0.######"));
        }

        private static void AssertClose(double expected, double actual, double tolerance, string what)
        {
            Assert.True(Math.Abs(expected - actual) <= tolerance,
                what + ": expected " + expected.ToString("0.#########") + ", was " + actual.ToString("0.#########"));
        }

        private static void AssertClose(Vector3 expected, Vector3 actual, float tolerance, string what)
        {
            AssertClose(expected.X, actual.X, tolerance, what + " x");
            AssertClose(expected.Y, actual.Y, tolerance, what + " y");
            AssertClose(expected.Z, actual.Z, tolerance, what + " z");
        }

        // ---- following ------------------------------------------------------------------------

        [Fact]
        public void AFreshCameraStartsInTheMiddleOfTheMap()
        {
            var camera = Walking();

            //The middle, not the origin: a camera that starts at (0,0,0) spends its first half
            //second of smoothing crossing 256 tiles of a map it had no reason to cross.
            AssertClose(new Vector3(256f, Ground, 256f), camera.Target, 0.001f, "start target");
            Assert.Equal(NeighbourhoodWorldCameraMode.Follow, camera.Mode);
            Assert.False(camera.HasAvatar);
        }

        [Fact]
        public void FollowingReachesTheAvatar()
        {
            var camera = Walking();
            var avatar = new Vector3(180f, Ground, 320f);
            camera.Follow(avatar);

            //Not there yet: the whole point of the filter.
            camera.Update(1.0 / 60.0);
            Assert.True(Vector3.Distance(camera.Target, avatar) > 1f,
                "one frame must not arrive; the camera has to travel");

            Run(camera, 4.0, 1.0 / 60.0);

            Assert.True(camera.IsSettled(), "a follow camera must settle, not creep");
            AssertClose(avatar, camera.Target, 0.05f, "settled target");
            AssertClose(NeighbourhoodWorldCamera.DefaultFollowDistance, camera.Distance, 0.05f, "settled distance");
        }

        [Fact]
        public void FollowingClosesTheDistanceMonotonically()
        {
            var camera = Walking();
            var avatar = new Vector3(400f, Ground, 100f);
            camera.Follow(avatar);

            var last = Vector3.Distance(camera.Target, avatar);
            for (var frame = 0; frame < 120; frame++)
            {
                camera.Update(1.0 / 60.0);
                var now = Vector3.Distance(camera.Target, avatar);
                Assert.True(now <= last + 1e-4f, "frame " + frame + " moved away: " + now + " > " + last);
                last = now;
            }
        }

        [Fact]
        public void FollowingHappensOnEveryAxisTheAvatarMoves()
        {
            //The height is a real case, not a formality: the generated terrain is not flat, and a
            //camera that smoothed x and z but snapped y would judder over every hill.
            var camera = Walking();
            var avatar = new Vector3(64f, 41.5f, 448f);
            camera.Follow(avatar);
            Run(camera, 4.0, 1.0 / 60.0);

            AssertClose(avatar, camera.Target, 0.05f, "target");
            AssertClose(41.5f, camera.Placement.Target.Y, 0.05f, "placement target height");
        }

        [Fact]
        public void ThePlacementIsTheNeighbourhoodPlacement()
        {
            var camera = Walking();
            camera.Follow(new Vector3(300f, Ground, 300f));
            camera.SnapToAvatar();
            camera.Orbit(0.3f, 0.1f);
            Run(camera, 2.0, 1.0 / 60.0);

            var placement = camera.Placement;

            //The same type the 3D view already consumes, so the view matrix, the cull frustum
            //and the fog distance all come from one stand-point.
            NeighbourhoodCameraPlacement expected = NeighbourhoodCameraPlacement.Placing(
                camera.Target, camera.Distance, camera.Pitch, camera.Yaw,
                NeighbourhoodWorldCamera.WidthForDistance(camera.Distance, camera.Aspect));

            Assert.Equal(expected.Position, placement.Position);
            Assert.Equal(expected.TilesAcross, placement.TilesAcross);
            Assert.Equal(camera.Target, placement.Target);

            //And the orbit really is the one the class documents: 45 degrees of yaw and 30 of
            //pitch to start from, so the isometric diamond reads square-on (see the placement's
            //remarks), moved by the orbit the test asked for.
            AssertClose(NeighbourhoodCameraPlacement.DefaultYawRadians + 0.3f, camera.Yaw, 0.001f, "yaw");
            AssertClose(NeighbourhoodCameraPlacement.DefaultPitchRadians + 0.1f, camera.Pitch, 0.001f, "pitch");
            Assert.True(placement.FogDistance > 0f, "the fog distance must be positive for the shader");
        }

        [Fact]
        public void TheReportedWidthIsTheInverseOfTheFramingDistance()
        {
            //Placement.TilesAcross has to be the width the view matrix actually frames, or
            //anything reasoning about "is the lot in shot" reasons about a different view.
            foreach (var distance in new[] { 4f, 60f, 96f, 400f, 4000f })
            {
                foreach (var aspect in new[] { 1f, 4f / 3f, 16f / 9f })
                {
                    var width = NeighbourhoodWorldCamera.WidthForDistance(distance, aspect);
                    var back = NeighbourhoodCameraPlacement.DistanceFor(width, aspect,
                        NeighbourhoodCameraPlacement.FieldOfViewRadians);
                    AssertClose(distance, back, distance * 1e-4f, "width " + width + " at " + distance);
                }
            }
        }

        // ---- frame-rate independence ----------------------------------------------------------

        [Fact]
        public void TheSameElapsedTimeGivesTheSameCameraAtAnyFrameRate()
        {
            const double elapsed = 1.5;

            //One frame, and the frame rates a player might actually have. The target is held
            //constant across the second, which is the case the closed form is exact for.
            var reference = Walking();
            reference.Follow(new Vector3(210f, Ground, 405f));
            reference.Orbit(0.4f, -0.2f);
            reference.ZoomTo(140f);
            reference.Update(elapsed);

            foreach (var step in new[] { elapsed, 1.0, 0.5, 1.0 / 7.0, 1.0 / 10.0, 1.0 / 30.0, 1.0 / 60.0, 1.0 / 144.0, 0.003 })
            {
                var camera = Walking();
                camera.Follow(new Vector3(210f, Ground, 405f));
                camera.Orbit(0.4f, -0.2f);
                camera.ZoomTo(140f);

                var remaining = elapsed;
                while (remaining > 1e-12)
                {
                    var dt = Math.Min(step, remaining);
                    camera.Update(dt);
                    remaining -= dt;
                }

                AssertClose(reference.Target, camera.Target, 1e-3f, "target at step " + step);
                AssertClose(reference.Distance, camera.Distance, 1e-3f, "distance at step " + step);
                AssertClose(reference.Pitch, camera.Pitch, 1e-4f, "pitch at step " + step);
                AssertClose(reference.Yaw, camera.Yaw, 1e-4f, "yaw at step " + step);
                AssertClose(reference.Placement.Position.X, camera.Placement.Position.X, 1e-3f,
                    "stand point at step " + step);
            }
        }

        [Fact]
        public void AModeChangeMidSecondLandsInTheSamePlaceAtAnyFrameRate()
        {
            //The case that matters in practice: half a second following, then a mode change, then
            //another half second. The state machine is the same code either way, so the seam
            //between the two phases must not become a frame-rate artefact.
            NeighbourhoodWorldCamera Scripted(double step)
            {
                var camera = Walking();
                camera.Follow(new Vector3(120f, Ground, 380f));
                camera.Update(0.5);

                var lot = new Vector3(400f, Ground, 400f);
                camera.FocusLot(lot);
                Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.LotFocus));
                RunExact(camera, 0.5, step);
                return camera;
            }

            var reference = Scripted(0.5);
            foreach (var step in new[] { 1.0 / 7.0, 1.0 / 60.0, 0.003 })
            {
                var camera = Scripted(step);
                AssertClose(reference.Target, camera.Target, 1e-3f, "target at step " + step);
                AssertClose(reference.Distance, camera.Distance, 1e-3f, "distance at step " + step);
            }
        }

        [Fact]
        public void AlphaIsTheExactDecayAndSaysSo()
        {
            const double halfLife = 0.12;

            //Half the error gone in one half-life, and the factor that does it.
            AssertClose(0.5, NeighbourhoodWorldCamera.AlphaFor(halfLife, halfLife), 1e-9, "one half-life");
            //Then the property the whole class rests on: the error left after a second is the
            //same whether the second was one step or sixty, because the factors multiply.
            var perStep = NeighbourhoodWorldCamera.AlphaFor(halfLife, 1.0 / 60.0);
            var compounded = 1.0;
            for (var i = 0; i < 60; i++) compounded *= (1.0 - perStep);
            AssertClose(1.0 - NeighbourhoodWorldCamera.AlphaFor(halfLife, 1.0), compounded, 1e-12,
                "sixty steps of a sixtieth, against one step of a second");

            //And the degenerate inputs are answers rather than exceptions: no elapsed time is no
            //movement, no half-life is no smoothing, and both stay in [0, 1].
            Assert.Equal(0.0, NeighbourhoodWorldCamera.AlphaFor(halfLife, 0));
            Assert.Equal(0.0, NeighbourhoodWorldCamera.AlphaFor(halfLife, -1));
            Assert.Equal(0.0, NeighbourhoodWorldCamera.AlphaFor(halfLife, double.NaN));
            Assert.Equal(1.0, NeighbourhoodWorldCamera.AlphaFor(0, 0.016));
            Assert.Equal(1.0, NeighbourhoodWorldCamera.AlphaFor(-1, 0.016));
            Assert.Equal(1.0, NeighbourhoodWorldCamera.AlphaFor(halfLife, 3600));

            //A zero half-life is a caller asking for an exact camera, and gets one.
            var instant = new NeighbourhoodWorldCamera(MapSize, Ground, targetHalfLife: 0f);
            instant.Follow(new Vector3(11f, Ground, 22f));
            instant.Update(1.0 / 60.0);
            AssertClose(new Vector3(11f, Ground, 22f), instant.Target, 0.0001f, "instant follow");
        }

        [Fact]
        public void TwoCamerasFedTheSameScriptEndUpIdentical()
        {
            //Determinism, in the only form that means anything: the same inputs twice, bit for
            //bit. There is no RNG and no clock in here, so anything else is a bug.
            NeighbourhoodWorldCamera Script()
            {
                var camera = new NeighbourhoodWorldCamera(MapSize, Ground, 16f / 9f);
                camera.Follow(new Vector3(3f, 0f, 5f));
                for (var i = 0; i < 90; i++)
                {
                    camera.Follow(new Vector3(3f + i * 0.5f, i * 0.02f, 5f + i * 0.25f));
                    if (i == 30) camera.ZoomTo(30f);
                    if (i == 45) camera.FocusLot(new Vector3(400f, Ground, 400f));
                    if (i == 50) camera.SetMode(NeighbourhoodWorldCameraMode.LotFocus);
                    if (i == 60) camera.Orbit(0.11f, 0.03f);
                    if (i == 70) camera.SetMode(NeighbourhoodWorldCameraMode.Follow);
                    camera.Update(1.0 / 59.0);
                }
                return camera;
            }

            var a = Script();
            var b = Script();
            Assert.Equal(a.Target, b.Target);
            Assert.Equal(a.Placement.Position, b.Placement.Position);
            Assert.Equal(a.Distance, b.Distance);
            Assert.Equal(a.Pitch, b.Pitch);
            Assert.Equal(a.Yaw, b.Yaw);
        }

        // ---- modes ----------------------------------------------------------------------------

        [Fact]
        public void SwitchingToLotFocusTravelsToTheLotRatherThanCutting()
        {
            var camera = Walking();
            camera.Follow(new Vector3(100f, Ground, 100f));
            camera.SnapToAvatar();

            var lot = new Vector3(400f, Ground, 380f);
            camera.FocusLot(lot);
            Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.LotFocus));
            Assert.Equal(NeighbourhoodWorldCameraMode.LotFocus, camera.Mode);

            //One frame in, the camera is on its way - neither where it was nor where it is going.
            camera.Update(1.0 / 60.0);
            Assert.True(Vector3.Distance(camera.Target, lot) > 1f, "one frame must not arrive");

            Run(camera, 6.0, 1.0 / 60.0);
            AssertClose(lot, camera.Target, 0.05f, "lot target");
            AssertClose(NeighbourhoodWorldCamera.DefaultLotFocusDistance, camera.Distance, 0.05f, "lot distance");
            Assert.True(camera.IsSettled(), "a lot camera must settle on its lot");
        }

        [Fact]
        public void LotFocusIsRefusedWithoutALot()
        {
            //The state NeighCameraMachine's remarks are about: zoomed in on a lot that is not
            //there, with nothing to get back out. Refused rather than entered.
            var camera = Walking();
            camera.Follow(new Vector3(100f, Ground, 100f));
            camera.SnapToAvatar();
            var before = camera.Target;

            Assert.False(camera.SetMode(NeighbourhoodWorldCameraMode.LotFocus));
            Assert.Equal(NeighbourhoodWorldCameraMode.Follow, camera.Mode);
            AssertClose(before, camera.Target, 0.0001f, "target after a refused mode change");

            //Ask again once there is a lot, and it is allowed - and it is allowed twice, because
            //SetMode is idempotent rather than a toggle.
            camera.FocusLot(new Vector3(200f, Ground, 200f));
            Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.LotFocus));
            Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.LotFocus));
            Assert.Equal(NeighbourhoodWorldCameraMode.LotFocus, camera.Mode);

            //Losing the lot again puts the camera back where it was left rather than stranded.
            camera.ClearLot();
            Assert.False(camera.HasLot);
            Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.Follow));
            Run(camera, 4.0, 1.0 / 60.0);
            AssertClose(new Vector3(100f, Ground, 100f), camera.Target, 0.05f, "back on the avatar");
        }

        [Fact]
        public void FreeModeIgnoresTheAvatarAndGoesWhereItIsTold()
        {
            var camera = Walking();
            camera.Follow(new Vector3(100f, Ground, 100f));
            camera.SnapToAvatar();
            Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.Free));

            //The avatar keeps walking; the camera does not follow, because "free" has to mean
            //that or dragging the mouse is dragging the sim.
            camera.Follow(new Vector3(300f, Ground, 300f));
            Run(camera, 2.0, 1.0 / 60.0);
            AssertClose(new Vector3(100f, Ground, 100f), camera.Target, 0.0001f, "free target");
            Assert.Equal(new Vector3(300f, Ground, 300f), camera.Avatar);

            //A drag is applied outright: a camera that lagged the hand moving it would read as
            //input lag rather than as inertia.
            var before = camera.Target;
            camera.Pan(new Vector3(0f, 0f, 25f));
            AssertClose(before + new Vector3(0f, 0f, 25f), camera.Target, 0.0001f, "panned target");

            camera.ZoomTo(30f);
            Run(camera, 2.0, 1.0 / 60.0);
            AssertClose(30f, camera.Distance, 0.05f, "free distance");

            //And back to following when the player lets go.
            Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.Follow));
            Run(camera, 6.0, 1.0 / 60.0);
            AssertClose(new Vector3(300f, Ground, 300f), camera.Target, 0.05f, "back on the avatar");
            AssertClose(NeighbourhoodWorldCamera.DefaultFollowDistance, camera.Distance, 0.05f, "follow distance");
        }

        [Fact]
        public void FollowHoldsItsPositionUntilAnAvatarArrives()
        {
            var camera = Walking();
            Assert.True(camera.IsSettled(), "a camera with nothing to follow is not moving");

            camera.Follow(new Vector3(48f, Ground, 452f));
            Assert.False(camera.IsSettled(), "one report is a target, not an arrival");
            Run(camera, 4.0, 1.0 / 60.0);
            AssertClose(new Vector3(48f, Ground, 452f), camera.Target, 0.05f, "target");
        }

        [Fact]
        public void ThePanelCameraModesMapOntoTheWorldCameraModes()
        {
            //B4 task 4's "with the UI mode switch": a zoomed-in selection is a lot focus and the
            //full-screen view is the walkable overview.
            Assert.Equal(NeighbourhoodWorldCameraMode.LotFocus,
                NeighbourhoodWorldCamera.ModeFor(NeighCameraMode.HouseFocus));
            Assert.Equal(NeighbourhoodWorldCameraMode.Follow,
                NeighbourhoodWorldCamera.ModeFor(NeighCameraMode.FullScreen));
        }

        [Fact]
        public void OrbitingTurnsTheCameraTheShortWayRound()
        {
            var camera = Walking();
            camera.SetOrbit(3.1f, 0.5f);
            Run(camera, 2.0, 1.0 / 60.0);
            AssertClose(3.1f, camera.Yaw, 0.001f, "yaw");

            //From just under +pi round to just under -pi: two hundredths of a radian of turn,
            // not six. A filter that takes the long way spins the world past the player.
            camera.SetOrbit(-3.1f, 0.5f);
            camera.Update(1.0 / 60.0);
            Assert.True(Math.Abs(camera.Yaw) > 3.0f, "the turn must go through pi, not the long way round");

            //Pitch is clamped rather than allowed to put the camera under the ground or inside
            //its own target.
            camera.SetOrbit(0f, -10f);
            camera.Update(1.0 / 60.0);
            AssertClose(NeighbourhoodWorldCamera.MinPitchRadians, camera.GoalPitch, 0.0001f, "min pitch");
            camera.SetOrbit(0f, 10f);
            AssertClose(NeighbourhoodWorldCamera.MaxPitchRadians, camera.GoalPitch, 0.0001f, "max pitch");
        }

        // ---- bounds ---------------------------------------------------------------------------

        [Fact]
        public void TheLookAtPointCannotLeaveTheMap()
        {
            var camera = At(64);

            //An avatar off the edge - a path follower that overshot, a lot centre from a wider
            //map, a drag past the scenery. The aim point is pinned, so the camera is left looking
            //at generated ground rather than at the backdrop beyond it.
            camera.Follow(new Vector3(-500f, Ground, 5000f));
            Run(camera, 4.0, 1.0 / 60.0);

            AssertClose(new Vector3(0f, Ground, 64f), camera.Target, 0.001f, "clamped target");
            Assert.Equal(new Vector3(-500f, Ground, 5000f), camera.Avatar);
            Assert.True(camera.Bounds.Contains(camera.Target), "the target must be inside the bounds");
            Assert.True(camera.Placement.Target.X >= 0f && camera.Placement.Target.Z <= 64f, "and so must the placement");
        }

        [Fact]
        public void BoundsApplyToEveryModeAndToAPan()
        {
            var camera = At(64);
            camera.FocusLot(new Vector3(900f, Ground, -900f));
            Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.LotFocus));
            Run(camera, 4.0, 1.0 / 60.0);
            AssertClose(new Vector3(64f, Ground, 0f), camera.Target, 0.001f, "clamped lot");

            camera.Pan(new Vector3(0f, 0f, 1000f));
            AssertClose(64f, camera.Target.Z, 0.001f, "panned past the far edge");

            camera.Pan(new Vector3(-1000f, 0f, 0f));
            AssertClose(0f, camera.Target.X, 0.001f, "panned past the near edge");
        }

        [Fact]
        public void NarrowingTheBoundsPullsTheCameraBackIn()
        {
            var camera = At(512);
            camera.Follow(new Vector3(500f, Ground, 500f));
            Run(camera, 4.0, 1.0 / 60.0);
            AssertClose(500f, camera.Target.X, 0.05f, "before");

            //A different neighbourhood, half the size: the aim point cannot stay over ground
            //that is no longer generated.
            camera.SetBounds(NeighbourhoodCameraBounds.From(0f, 0f, 256f, 256f));
            AssertClose(256f, camera.Target.X, 0.0001f, "after");
            AssertClose(256f, camera.Placement.Target.X, 0.0001f, "and the placement, with no frame in between");
        }

        [Fact]
        public void SnapToJumpsAndSettlesTheOrbit()
        {
            var camera = Walking();
            camera.Follow(new Vector3(100f, Ground, 100f));
            camera.SnapToAvatar();
            AssertClose(new Vector3(100f, Ground, 100f), camera.Target, 0.0001f, "snapped target");

            //The one unsmoothed move in the class, and it moves the orbit too: a lot switch the
            //player has already been teleported through should not be watched gliding across the
            //neighbourhood.
            camera.Orbit(0.8f, 0.2f);
            Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.Free));
            camera.ZoomTo(500f);
            camera.SnapTo(new Vector3(400f, Ground, 400f));

            AssertClose(new Vector3(400f, Ground, 400f), camera.Target, 0.0001f, "snapped target");
            AssertClose(500f, camera.Distance, 0.0001f, "snapped distance");
            AssertClose(camera.GoalPitch, camera.Pitch, 0.0001f, "snapped pitch");
            AssertClose(camera.GoalYaw, camera.Yaw, 0.0001f, "snapped yaw");
            AssertClose(NeighbourhoodCameraPlacement.DefaultPitchRadians + 0.2f, camera.Pitch, 0.0001f,
                "onto the orbit the player left it at, not the constructor's");
            Assert.True(camera.IsSettled(), "a snap settles the camera");
        }

        [Fact]
        public void SnapToAvatarIsARefusalWhenThereIsNoAvatar()
        {
            var camera = Walking();
            Assert.False(camera.SnapToAvatar());
            camera.Follow(new Vector3(90f, Ground, 90f));
            Assert.True(camera.SnapToAvatar());
            AssertClose(new Vector3(90f, Ground, 90f), camera.Target, 0.0001f, "snapped target");
        }

        [Fact]
        public void TheBoundsForAMapAreTheMapItself()
        {
            var bounds = NeighbourhoodCameraBounds.ForMap(MapSize);
            Assert.Equal(0f, bounds.MinX);
            Assert.Equal((float)MapSize, bounds.MaxZ);
            Assert.Equal(256f, bounds.Centre.X);
            Assert.True(bounds.Contains(new Vector3(10f, 999f, 10f)), "height is not part of the rectangle");
            Assert.False(bounds.Contains(new Vector3(-1f, 0f, 10f)), "just outside is outside");

            //Corners the wrong way round are ordered rather than producing a stuck camera, and
            //a default-sized map is the fallback for a nonsensical one.
            var swapped = NeighbourhoodCameraBounds.From(64f, 64f, 0f, 0f);
            AssertClose(0f, swapped.MinX, 0.0001f, "ordered min x");
            AssertClose(64f, swapped.MaxX, 0.0001f, "ordered max x");
            Assert.Equal(NeighbourhoodLayout.DefaultMapSize, (int)NeighbourhoodCameraBounds.ForMap(0).MaxX);
            Assert.Equal(NeighbourhoodLayout.DefaultMapSize, (int)NeighbourhoodCameraBounds.ForMap(-4).MaxZ);
        }

        // ---- rubbish in -----------------------------------------------------------------------

        [Fact]
        public void AFrameWithNoTimeIsAPauseAndNotATeleport()
        {
            var camera = Walking();
            camera.Follow(new Vector3(100f, Ground, 100f));
            Run(camera, 0.5, 1.0 / 60.0);
            var before = camera.Target;
            var distance = camera.Distance;

            camera.Update(0);
            camera.Update(-1.0);
            camera.Update(double.NaN);
            camera.Update(double.PositiveInfinity);
            camera.Update(double.NegativeInfinity);

            AssertClose(before, camera.Target, 0.0001f, "target after a paused frame");
            AssertClose(distance, camera.Distance, 0.0001f, "distance after a paused frame");
        }

        [Fact]
        public void RubbishPositionsAreDroppedRatherThanClamped()
        {
            //A NaN in the look-at point never comes back out of a lerp - it is still a NaN a
            //thousand frames later, and it takes the view matrix with it. So the last good
            //position is kept and the bad one is a no-op, which is a symptom the caller can use.
            var camera = Walking();
            camera.Follow(new Vector3(100f, Ground, 100f));
            Run(camera, 4.0, 1.0 / 60.0);
            var good = camera.Avatar;

            camera.Follow(new Vector3(float.NaN, 0f, 0f));
            camera.Follow(new Vector3(0f, float.PositiveInfinity, 0f));
            camera.Follow(new Vector3(float.NaN, float.NaN, float.NaN));
            camera.FocusLot(new Vector3(0f, float.NaN, 0f));
            camera.Pan(new Vector3(float.NaN, 0f, 0f));
            camera.Orbit(float.NaN, float.NaN);
            camera.Zoom(float.NaN);
            camera.SnapTo(new Vector3(float.NaN, 0f, 0f));

            Assert.Equal(good, camera.Avatar);
            Assert.False(camera.HasLot);
            Run(camera, 1.0, 1.0 / 60.0);
            AssertClose(good, camera.Target, 0.05f, "target after rubbish input");
            Assert.True(!float.IsNaN(camera.Placement.Position.X), "the stand point must stay a number");        }

        [Fact]
        public void DistancesAndAspectAreClampedToSomethingUsable()
        {
            var camera = Walking();

            camera.ZoomTo(0f);
            AssertClose(NeighbourhoodWorldCamera.MinDistance, camera.GoalDistance, 0.0001f, "nearest");
            camera.ZoomTo(float.MaxValue);
            AssertClose(NeighbourhoodWorldCamera.MaxDistance, camera.GoalDistance, 0.0001f, "farthest");
            camera.ZoomTo(-1000f);
            AssertClose(NeighbourhoodWorldCamera.MinDistance, camera.GoalDistance, 0.0001f, "negative");

            //A device that has just been resized reports a zero aspect; the next placement still
            //has to be a real matrix.
            camera.Aspect = 0f;
            AssertClose(NeighbourhoodCameraPlacement.DefaultAspect, camera.Aspect, 0.0001f, "default aspect");
            camera.Aspect = 16f / 9f;
            AssertClose(16f / 9f, camera.Aspect, 0.0001f, "wide aspect");
            Assert.True(camera.Placement.TilesAcross > 0f, "the framing width must survive a resize");

            camera.SetBounds(NeighbourhoodCameraBounds.From(10f, 10f, 5f, 5f));
            Assert.True(camera.Bounds.IsUsable, "a reversed rectangle is ordered, not rejected");
            AssertClose(5f, camera.Bounds.MinX, 0.0001f, "ordered bounds");
        }

        [Fact]
        public void EachModeKeepsItsOwnZoom()
        {
            //A zoom the player chose is a property of the mode they chose it in, the same way
            //the pitch and yaw are the player's and not the mode's: focusing a lot should not
            //silently reset the walking distance, and coming back to walking should not need
            //the wheel again.
            var camera = Walking();
            camera.Follow(new Vector3(100f, Ground, 100f));
            camera.SnapToAvatar();
            camera.ZoomTo(37f);
            Run(camera, 1.0, 1.0 / 60.0);
            AssertClose(37f, camera.Distance, 0.05f, "walked in at 37");

            camera.FocusLot(new Vector3(400f, Ground, 400f));
            Assert.True(camera.SetMode(NeighbourhoodWorldCameraMode.LotFocus));
            AssertClose(NeighbourhoodWorldCamera.DefaultLotFocusDistance, camera.GoalDistance, 0.0001f,
                "a lot frames itself, and the walking zoom is not it");

            camera.Zoom(10f);
            AssertClose(NeighbourhoodWorldCamera.DefaultLotFocusDistance + 10f, camera.LotFocusDistance, 0.0001f,
                "the wheel zooms the mode the player is in");
            AssertClose(37f, camera.FollowDistance, 0.0001f, "and leaves the other one alone");

            camera.SetMode(NeighbourhoodWorldCameraMode.Follow);
            Run(camera, 2.0, 1.0 / 60.0);
            AssertClose(37f, camera.Distance, 0.05f, "the walking zoom is still where it was left");
        }
    }
}
