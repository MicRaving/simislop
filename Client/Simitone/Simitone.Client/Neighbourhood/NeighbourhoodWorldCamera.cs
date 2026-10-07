/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Which of the three things the world camera is doing, i.e. what its look-at point is
    /// allowed to be. This is B4's "camera state machine": the mode is the whole of the
    /// camera's <em>intent</em>, and everything else in
    /// <see cref="NeighbourhoodWorldCamera"/> is the smoothing that gets it there.
    ///
    /// <para>
    /// <b>Why a mode at all.</b> <c>NeighCameraMachine</c> exists because the 2D selection panel
    /// had camera states nothing could get out of - zoomed in with no lot panel, and so on. The
    /// same failure is available here in a worse form: a follow camera that a mode change
    /// silently stops following, a "free" camera with nothing to free it from, a lot focus
    /// pointing at a lot the player cannot see. So the mode is recorded, <see cref="SetMode"/>
    /// <em>refuses</em> a state whose subject does not exist, and <see cref="Mode"/> is a single
    /// read the scene can log when a camera looks wrong.
    /// </para>
    /// </summary>
    public enum NeighbourhoodWorldCameraMode
    {
        /// <summary>
        /// Chase the avatar: the look-at point is wherever <c>Follow</c> last reported, clamped
        /// to the map. The normal walking camera.
        /// </summary>
        Follow,

        /// <summary>
        /// The player drives. The camera stays exactly where <c>Pan</c> / <c>ZoomTo</c> put it
        /// and <c>Follow</c> is recorded but not obeyed, which is what "free" has to mean if
        /// dragging the mouse is going to feel like it is dragging the world.
        /// </summary>
        Free,

        /// <summary>
        /// Frame a lot: the look-at point is the lot centre given to <c>FocusLot</c>, and the
        /// orbit is the lot's configured distance rather than the walking one. Entering this
        /// mode with no lot focused is refused.
        /// </summary>
        LotFocus
    }

    /// <summary>
    /// The rectangle the world camera's look-at point is confined to, in generated-map tiles.
    ///
    /// <para>
    /// <b>The square, and here is what that costs.</b> <c>CityGeometry</c> clips the 512x512
    /// tile square to TSO's isometric diamond, so the four corners of this rectangle are not
    /// ground. Clamping to it therefore keeps the camera from being aimed into empty space
    /// beyond the map, and can still let the aim point sit over one of those corners. Tightening
    /// it to the diamond is <c>SetBounds</c>'s job, and a caller that knows the real extent
    /// (the packed layout's, or the generator's) should say so rather than have this guess.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodCameraBounds
    {
        /// <summary>Lowest aimable x, in tiles.</summary>
        public float MinX { get; }

        /// <summary>Lowest aimable z, in tiles.</summary>
        public float MinZ { get; }

        /// <summary>Highest aimable x, in tiles.</summary>
        public float MaxX { get; }

        /// <summary>Highest aimable z, in tiles.</summary>
        public float MaxZ { get; }

        /// <summary>Width of the aimable rectangle, in tiles.</summary>
        public float Width { get { return Math.Max(0f, MaxX - MinX); } }

        /// <summary>Depth of the aimable rectangle, in tiles.</summary>
        public float Depth { get { return Math.Max(0f, MaxZ - MinZ); } }

        /// <summary>Middle of the aimable rectangle, in tiles.</summary>
        public Vector2 Centre { get { return new Vector2((MinX + MaxX) / 2f, (MinZ + MaxZ) / 2f); } }

        private NeighbourhoodCameraBounds(float minX, float minZ, float maxX, float maxZ)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
        }

        /// <summary>
        /// The bounds of a generated map of <paramref name="mapSize"/> tiles: the whole square,
        /// which is what <c>NeighbourhoodLayout.MapSize</c> means and therefore what the camera
        /// can be told about without asking the generator anything.
        /// </summary>
        /// <param name="mapSize">Side of the generated map, in tiles.</param>
        public static NeighbourhoodCameraBounds ForMap(int mapSize)
        {
            if (mapSize <= 0) mapSize = NeighbourhoodLayout.DefaultMapSize;
            return new NeighbourhoodCameraBounds(0f, 0f, mapSize, mapSize);
        }

        /// <summary>
        /// Bounds built from four numbers, with the corners ordered so the rectangle is never
        /// inside out. A caller that passes its extents the wrong way round gets a working
        /// camera, not an aim point stuck at the low corner.
        /// </summary>
        public static NeighbourhoodCameraBounds From(float minX, float minZ, float maxX, float maxZ)
        {
            if (!Finite(minX)) minX = 0f;
            if (!Finite(minZ)) minZ = 0f;
            if (!Finite(maxX)) maxX = minX;
            if (!Finite(maxZ)) maxZ = minZ;
            if (maxX < minX) { var t = minX; minX = maxX; maxX = t; }
            if (maxZ < minZ) { var t = minZ; minZ = maxZ; maxZ = t; }
            return new NeighbourhoodCameraBounds(minX, minZ, maxX, maxZ);
        }

        /// <summary>Whether <paramref name="world"/> is inside the rectangle, x and z.</summary>
        public bool Contains(Vector3 world)
        {
            return Finite(world.X) && Finite(world.Z)
                && world.X >= MinX && world.X <= MaxX && world.Z >= MinZ && world.Z <= MaxZ;
        }

        /// <summary>
        /// A point pulled inside the rectangle in x and z, with y untouched: the camera looks at
        /// the height of the ground it is over, and clamping height would put the horizon in the
        /// wrong place on a hill. A non-finite component is replaced by the bound on that axis,
        /// because a NaN that reaches <c>Vector3.Lerp</c> never comes back.
        /// </summary>
        public Vector3 Clamp(Vector3 world)
        {
            //A NaN height becomes zero rather than a bound: there is no upper bound on a height
            //to fold it into, and the placement's own guard would substitute a unit distance for
            //a NaN target anyway, which is a worse answer than the ground plane.
            return new Vector3(
                ClampAxis(world.X, MinX, MaxX),
                Finite(world.Y) ? world.Y : 0f,
                ClampAxis(world.Z, MinZ, MaxZ));
        }

        /// <summary>
        /// Whether the rectangle has any area at all. A zero-width bound is honoured rather
        /// than rejected, because a one-tile map is a map.
        /// </summary>
        public bool IsUsable
        {
            get { return Finite(MinX) && Finite(MinZ) && Finite(MaxX) && Finite(MaxZ) && MaxX >= MinX && MaxZ >= MinZ; }
        }

        private static float ClampAxis(float value, float min, float max)
        {
            if (float.IsNaN(value)) return min;
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        public override string ToString()
        {
            return "[" + MinX.ToString("0.#") + ".." + MaxX.ToString("0.#") + ", "
                + MinZ.ToString("0.#") + ".." + MaxZ.ToString("0.#") + "]";
        }
    }

    /// <summary>
    /// The camera for B4's walkable streets: it follows an avatar across the generated terrain
    /// and says where it is standing, as pure arithmetic with no graphics device, no scene and
    /// no game state.
    ///
    /// <para>
    /// <b>What it is for.</b> <c>NeighCameraMachine</c> decides where the neighbourhood camera
    /// goes and <see cref="NeighbourhoodCameraPlacement"/> turns that into a stand-point. Both
    /// answer in one frame: a target, and the placement for it. Neither of them moves - there
    /// is nothing to move, because a bitmap panel has no avatar and a jump-cut zoom is the
    /// whole of the 2D camera's motion. B4 needs the third thing: a camera that <em>travels</em>,
    /// so that a sim walking from one lot to the next is watched doing it rather than cut to.
    /// This is that camera's state, and it is the only part of it worth testing.
    /// </para>
    ///
    /// <para>
    /// <b>Half-lives, not a spring, and that is a deliberate choice.</b> The obvious smoother
    /// is a critically damped spring, and integrated per frame it is not frame-rate independent:
    /// the same second of wall clock at 15fps and at 144fps lands the camera in two different
    /// places, so a follow camera's feel becomes a property of the machine it is running on, and
    /// a hitch visibly changes the shot. What this uses instead is the exact solution of
    /// <c>dx/dt = -(ln2/h)(x - goal)</c> for a goal held constant across the step,
    /// <c>x += (goal - x) * (1 - 2^(-dt/h))</c>, whose per-step factors
    /// <em>multiply</em>: after <c>n</c> steps totalling <c>T</c> seconds the error is
    /// <c>2^(-T/h)</c> of what it was however the <c>T</c> was cut up. <see cref="AlphaFor"/> is
    /// that factor and it is public, because the claim is worth being able to check.
    /// <c>NeighbourhoodWorldCameraTests</c> checks it at 1, 7, 10 and 60 steps a second.
    /// </para>
    ///
    /// <para>
    /// <b>What frame-rate independence does and does not buy.</b> It is exact while the goal
    /// is constant across a step, which is the case for a standing avatar, a settled camera and
    /// every mode change. It is approximate for an avatar that moves <em>between</em> two
    /// frames, because then the only thing that can be asked of any filter is where the goal was
    /// sampled: a 15fps camera and a 144fps camera chasing the same walker will differ by at
    /// most the walker's own movement in a frame. That is a sampling difference, not a different
    /// integrator, and it is not something a smoothing rule can remove. The integration contract
    /// is therefore: <b>call <see cref="Update"/> once per frame with that frame's elapsed
    /// seconds, exactly once, and do not call it more than once per draw.</b> The elapsed time
    /// is a <c>double</c> on purpose - a frame is a fraction of a second and a float of it
    /// arrives already rounded - and a zero, negative or non-finite elapsed time is ignored
    /// rather than treated as an instant jump, because the usual sources of one (a paused clock,
    /// a first frame before the clock has ticked, a device reset) are pauses and not teleports.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing is snapped except by <see cref="SnapTo"/>.</b> A mode change is a change of
    /// goal, not of state, so the camera travels to the lot it is focusing rather than cutting
    /// to it; the cut is <see cref="SnapTo"/>'s job and the scene decides when to ask for one
    /// (on load, and on a lot switch the player has already been teleported through). That
    /// split is the reason a mode change is a single line at the call site.
    /// </para>
    ///
    /// <para>
    /// <b>What is deliberately not here.</b> Input, collision of the camera against buildings,
    /// the avatar's own lead/look-ahead, and anything to do with a
    /// <c>GraphicsDevice</c>. A caller that wants the camera to lead a walking sim passes the
    /// lead position to <see cref="Follow"/> - the camera follows the point it is given, and
    /// deciding what that point is belongs to the thing that knows how the sim moves.
    /// <see cref="Placement"/> is a <see cref="NeighbourhoodCameraPlacement"/>, so the view
    /// matrix, the cull frustum and the fog distance all come from the same stand-point rather
    /// than from three places that can disagree.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodWorldCamera
    {
        // ---- tuning ---------------------------------------------------------------------------

        /// <summary>
        /// How far behind the avatar the walking camera sits, in tiles. Near enough that a sim
        /// is a person rather than a speck, far enough that the road it is on is legible.
        /// </summary>
        public const float DefaultFollowDistance = 60f;

        /// <summary>
        /// How far back the lot camera sits, in tiles. Far enough to frame the largest lot
        /// (<see cref="NeighbourhoodLayout.MaxLotSize"/>) plus its sidewalk margin with the
        /// pitch the placement defaults to.
        /// </summary>
        public const float DefaultLotFocusDistance = 96f;

        /// <summary>
        /// Half-life of the look-at point's smoothing, in seconds. A tenth of a second is about
        /// the lag of a TSO-style follow camera: long enough that the sim does not judder with
        /// each footfall, short enough that a direction change reads immediately.
        /// </summary>
        public const float DefaultTargetHalfLife = 0.12f;

        /// <summary>
        /// Half-life of the orbit's smoothing - distance, pitch and yaw - in seconds. Shorter
        /// than the look-at's, because the player's own input already carries the intent and
        /// the camera should feel attached to the mouse rather than to the sim.
        /// </summary>
        public const float DefaultOrbitHalfLife = 0.1f;

        /// <summary>Nearest the camera may orbit, in tiles; inside this the placement's own
        /// guard would substitute 1 and the fog distance would jump.</summary>
        public const float MinDistance = 4f;

        /// <summary>Farthest the camera may orbit, in tiles - the far plane of the world view,
        /// which is where a wider value stops being a zoom and starts being a float error.</summary>
        public const float MaxDistance = 4000f;

        /// <summary>Shallowest pitch, 5 degrees: at zero the up vector degenerates.</summary>
        public const float MinPitchRadians = 0.08726646f;

        /// <summary>Steepest pitch, 89 degrees: at a right angle the camera is inside the
        /// target and the placement's trigonometry loses its sign.</summary>
        public const float MaxPitchRadians = 1.5533431f;

        // ---- state ----------------------------------------------------------------------------

        private NeighbourhoodCameraBounds _bounds;
        private float _groundHeight;
        private float _aspect;
        private float _targetHalfLife;
        private float _orbitHalfLife;

        private NeighbourhoodWorldCameraMode _mode;

        //The smoothed values.
        private Vector3 _target;
        private float _distance;
        private float _pitch;
        private float _yaw;

        //The values the smoothed ones are heading for.
        private float _goalDistance;
        private float _goalPitch;
        private float _goalYaw;

        private Vector3 _avatar;
        private bool _hasAvatar;
        private Vector3 _lot;
        private bool _hasLot;

        private float _followDistance;
        private float _lotFocusDistance;
        private float _freeDistance;

        /// <summary>
        /// A world camera for a generated map.
        /// </summary>
        /// <param name="mapSize">Side of the generated map, in tiles; the initial bounds.</param>
        /// <param name="groundHeight">
        /// World height of the ground, so a fresh camera starts level with the neighbourhood
        /// rather than at the origin under it.
        /// </param>
        /// <param name="aspect">Viewport aspect ratio.</param>
        /// <param name="followDistance">Walking distance, in tiles.</param>
        /// <param name="lotFocusDistance">Lot framing distance, in tiles.</param>
        /// <param name="pitch">Starting pitch, in radians above the horizontal.</param>
        /// <param name="yaw">Starting yaw, in radians off an axis.</param>
        /// <param name="targetHalfLife">Look-at half-life in seconds; 0 tracks exactly.</param>
        /// <param name="orbitHalfLife">Orbit half-life in seconds; 0 is instant.</param>
        public NeighbourhoodWorldCamera(
            int mapSize,
            float groundHeight = 0f,
            float aspect = NeighbourhoodCameraPlacement.DefaultAspect,
            float followDistance = DefaultFollowDistance,
            float lotFocusDistance = DefaultLotFocusDistance,
            float pitch = NeighbourhoodCameraPlacement.DefaultPitchRadians,
            float yaw = NeighbourhoodCameraPlacement.DefaultYawRadians,
            float targetHalfLife = DefaultTargetHalfLife,
            float orbitHalfLife = DefaultOrbitHalfLife)
        {
            _bounds = NeighbourhoodCameraBounds.ForMap(mapSize);
            _groundHeight = Finite(groundHeight) ? groundHeight : 0f;
            _aspect = aspect > 0f && Finite(aspect) ? aspect : NeighbourhoodCameraPlacement.DefaultAspect;
            _targetHalfLife = HalfLife(targetHalfLife);
            _orbitHalfLife = HalfLife(orbitHalfLife);

            _followDistance = SanitiseDistance(followDistance, DefaultFollowDistance);
            _lotFocusDistance = SanitiseDistance(lotFocusDistance, DefaultLotFocusDistance);
            _freeDistance = _followDistance;

            _goalPitch = ClampPitch(pitch);
            _goalYaw = (float)Normalize(yaw);
            _pitch = _goalPitch;
            _yaw = _goalYaw;
            _distance = _followDistance;
            _goalDistance = _followDistance;

            //The middle of the map, not the origin: a camera that starts at (0,0,0) spends its
            //first second of smoothing crossing a 512-tile map for no reason.
            var centre = _bounds.Centre;
            _target = new Vector3(centre.X, _groundHeight, centre.Y);

            _mode = NeighbourhoodWorldCameraMode.Follow;
        }

        // ---- configuration --------------------------------------------------------------------

        /// <summary>The rectangle the look-at point is confined to. See
        /// <see cref="NeighbourhoodCameraBounds"/> for what its square corners do and do not
        /// cover.</summary>
        public NeighbourhoodCameraBounds Bounds { get { return _bounds; } }

        /// <summary>
        /// Replace the bounds. The current look-at point is re-clamped into them at once, so a
        /// narrower map cannot leave the camera aimed at ground that is no longer generated.
        /// </summary>
        public void SetBounds(NeighbourhoodCameraBounds bounds)
        {
            _bounds = bounds.IsUsable ? bounds : NeighbourhoodCameraBounds.ForMap(NeighbourhoodLayout.DefaultMapSize);
            _target = _bounds.Clamp(_target);
        }

        /// <summary>World height of the ground, kept for the scene to blend a lot's base
        /// height into; the camera itself follows whatever height it is given.</summary>
        public float GroundHeight { get { return _groundHeight; } }

        /// <summary>
        /// Viewport aspect ratio. An unusable value folds to the placement's own default rather
        /// than propagating, because a zero aspect is a device that has just been resized and
        /// the next <see cref="Placement"/> has to be a real matrix.
        /// </summary>
        public float Aspect
        {
            get { return _aspect; }
            set { _aspect = value > 0f && Finite(value) ? value : NeighbourhoodCameraPlacement.DefaultAspect; }
        }

        /// <summary>Walking distance, in tiles, used by <see cref="NeighbourhoodWorldCameraMode.Follow"/>.</summary>
        public float FollowDistance
        {
            get { return _followDistance; }
            set { _followDistance = SanitiseDistance(value, _followDistance); }
        }

        /// <summary>Framing distance, in tiles, used by <see cref="NeighbourhoodWorldCameraMode.LotFocus"/>.</summary>
        public float LotFocusDistance
        {
            get { return _lotFocusDistance; }
            set { _lotFocusDistance = SanitiseDistance(value, _lotFocusDistance); }
        }

        /// <summary>Distance, in tiles, used by <see cref="NeighbourhoodWorldCameraMode.Free"/>.</summary>
        public float FreeDistance
        {
            get { return _freeDistance; }
            set { _freeDistance = SanitiseDistance(value, _freeDistance); }
        }

        /// <summary>
        /// The distance this mode would use if the camera were settled. <see cref="Distance"/> is
        /// where it is now; this is where it is going, and the two differ for as long as a zoom
        /// is travelling.
        /// </summary>
        public float GoalDistance
        {
            get
            {
                Vector3 goal;
                float distance;
                ResolveGoal(out goal, out distance);
                return distance;
            }
        }

        /// <summary>
        /// The pitch the orbit is turning towards, in radians. <see cref="Pitch"/> is where it
        /// is; this is the angle the last <see cref="SetOrbit"/> or <see cref="Orbit"/> asked
        /// for, already clamped - which is what makes those two separable from the travel between
        /// them.
        /// </summary>
        public float GoalPitch { get { return _goalPitch; } }

        /// <summary>The yaw the orbit is turning towards, in radians, in <c>(-pi, pi]</c>.</summary>
        public float GoalYaw { get { return (float)_goalYaw; } }

        /// <summary>How far the camera is from its target right now, in tiles.</summary>
        public float Distance { get { return ClampDistance(_distance); } }

        /// <summary>How far below the horizontal the camera sits right now, in radians.</summary>
        public float Pitch { get { return ClampPitch(_pitch); } }

        /// <summary>How far off an axis the camera sits right now, in radians, in
        /// <c>(-pi, pi]</c>.</summary>
        public float Yaw { get { return (float)Normalize(_yaw); } }

        /// <summary>Where the camera is looking right now, in world units, inside the bounds.</summary>
        public Vector3 Target { get { return _bounds.Clamp(_target); } }

        /// <summary>What the camera is in the middle of doing.</summary>
        public NeighbourhoodWorldCameraMode Mode { get { return _mode; } }

        /// <summary>The last position <see cref="Follow"/> was given, whether or not the mode
        /// is obeying it.</summary>
        public Vector3 Avatar { get { return _avatar; } }

        /// <summary>Whether an avatar has been reported yet. A fresh camera in
        /// <see cref="NeighbourhoodWorldCameraMode.Follow"/> with no avatar holds its start
        /// position rather than snapping to the origin.</summary>
        public bool HasAvatar { get { return _hasAvatar; } }

        /// <summary>Whether a lot has been focused, which is what
        /// <see cref="NeighbourhoodWorldCameraMode.LotFocus"/> requires.</summary>
        public bool HasLot { get { return _hasLot; } }

        // ---- inputs ---------------------------------------------------------------------------

        /// <summary>
        /// Report where the avatar is. In <see cref="NeighbourhoodWorldCameraMode.Follow"/> the
        /// camera's look-at point chases it; in the other two modes the position is recorded and
        /// ignored, which is what lets a free camera be free and a lot camera stay on its lot
        /// while the sim walks past.
        /// </summary>
        /// <remarks>
        /// A non-finite position is dropped rather than clamped. A NaN reaching the look-at
        /// point would come out of <c>Vector3.Lerp</c> as a NaN and would stay one through every
        /// later frame, taking the view matrix with it - so the last good position is kept and
        /// the bad one is a no-op. The caller that produced the NaN has a bug, and a camera
        /// frozen on the last good frame is a much better symptom than a black screen.
        /// </remarks>
        /// <param name="worldPosition">The avatar's world position, in tiles.</param>
        public void Follow(Vector3 worldPosition)
        {
            if (!Finite(worldPosition.X) || !Finite(worldPosition.Y) || !Finite(worldPosition.Z)) return;
            _avatar = worldPosition;
            _hasAvatar = true;
        }

        /// <summary>
        /// Frame a lot, and say so. Clamping happens where the point is used rather than here,
        /// so a lot outside the current bounds is still focusable after the bounds widen.
        /// </summary>
        /// <param name="lotCentre">The lot's centre in world tiles.</param>
        public void FocusLot(Vector3 lotCentre)
        {
            if (!Finite(lotCentre.X) || !Finite(lotCentre.Y) || !Finite(lotCentre.Z)) return;
            _lot = lotCentre;
            _hasLot = true;
        }

        /// <summary>
        /// Forget the focused lot, which is what makes
        /// <see cref="SetMode"/> refuse <see cref="NeighbourhoodWorldCameraMode.LotFocus"/>
        /// afterwards - the state the <c>NeighCameraMachine</c> remarks warn about, where the
        /// camera is in a mode whose subject is gone and nothing can get it out.
        /// </summary>
        public void ClearLot()
        {
            _lot = Vector3.Zero;
            _hasLot = false;
        }

        /// <summary>
        /// Change what the camera is doing. The camera travels to the new subject rather than
        /// jumping, and the change is refused when the new mode has nothing to point at.
        /// </summary>
        /// <param name="mode">The mode to enter.</param>
        /// <returns>
        /// False - and no change at all - when <paramref name="mode"/> is
        /// <see cref="NeighbourhoodWorldCameraMode.LotFocus"/> and no lot has been focused.
        /// Everything else is always enterable, because <see cref="NeighbourhoodWorldCameraMode.Follow"/>
        /// holds its position until an avatar arrives and <see cref="NeighbourhoodWorldCameraMode.Free"/>
        /// is a statement about the current position.
        /// </returns>
        public bool SetMode(NeighbourhoodWorldCameraMode mode)
        {
            if (mode == NeighbourhoodWorldCameraMode.LotFocus && !_hasLot) return false;
            _mode = mode;
            return true;
        }

        /// <summary>
        /// Which world-camera mode a panel camera mode means. A zoomed-in selection is a lot the
        /// player chose, so it is a lot focus; the full-screen view is the walkable overview, so
        /// it is a follow camera. There is no <c>Free</c> equivalent because the bitmap panel has
        /// no way to ask for one - the free camera belongs to the world's own controls.
        /// </summary>
        /// <param name="panelMode">A <c>NeighCameraMachine</c> mode.</param>
        public static NeighbourhoodWorldCameraMode ModeFor(NeighCameraMode panelMode)
        {
            return panelMode == NeighCameraMode.HouseFocus
                ? NeighbourhoodWorldCameraMode.LotFocus
                : NeighbourhoodWorldCameraMode.Follow;
        }

        /// <summary>
        /// Put the look-at point where it is told, in one step, and settle the orbit onto the
        /// current mode's distance and the configured pitch and yaw.
        /// </summary>
        /// <remarks>
        /// The one place in this class that moves the camera without smoothing, and it exists
        /// for the two moments smoothing is the wrong answer: a neighbourhood loading, and a lot
        /// switch the player has already been teleported through. Everywhere else, let the
        /// camera travel - a camera that glides across 500 tiles of its own neighbourhood on
        /// every lot change shows the player the ground they are not on.
        /// </remarks>
        /// <param name="worldPosition">Where to look, in world tiles; clamped into the bounds.</param>
        public void SnapTo(Vector3 worldPosition)
        {
            if (!Finite(worldPosition.X) || !Finite(worldPosition.Y) || !Finite(worldPosition.Z)) return;
            _target = _bounds.Clamp(worldPosition);

            Vector3 goal;
            float distance;
            ResolveGoal(out goal, out distance);
            _distance = ClampDistance(distance);
            _goalDistance = _distance;
            _pitch = _goalPitch;
            _yaw = _goalYaw;
        }

        /// <summary>
        /// Snap to wherever the avatar is, if there is one. The load path's
        /// <c>SnapTo(camera.Avatar)</c> without the null check.
        /// </summary>
        /// <returns>False when no avatar has been reported.</returns>
        public bool SnapToAvatar()
        {
            if (!_hasAvatar) return false;
            SnapTo(_avatar);
            return true;
        }

        /// <summary>
        /// Drive the camera in <see cref="NeighbourhoodWorldCameraMode.Free"/> mode: move the
        /// look-at point by a world offset.
        /// </summary>
        /// <remarks>
        /// Applied immediately rather than smoothed, unlike everything else here, and that is the
        /// point: a dragged camera has to track the mouse. Smoothing a direct manipulation
        /// makes the view lag the hand that is moving it, which reads as input lag rather than
        /// as inertia. <see cref="NeighbourhoodWorldCameraMode.Free"/> is also the only mode
        /// where this is the look-at point's whole story, so nothing is left for the smoothing
        /// to do afterwards.
        /// </remarks>
        /// <param name="worldDelta">How far to move, in world tiles.</param>
        public void Pan(Vector3 worldDelta)
        {
            if (!Finite(worldDelta.X) || !Finite(worldDelta.Y) || !Finite(worldDelta.Z)) return;
            _target = _bounds.Clamp(new Vector3(_target.X + worldDelta.X, _target.Y + worldDelta.Y, _target.Z + worldDelta.Z));
        }

        /// <summary>
        /// Set the orbit's yaw and pitch outright, in radians. The camera turns towards them.
        /// </summary>
        /// <param name="yawRadians">Yaw off an axis; any number of turns is the same angle.</param>
        /// <param name="pitchRadians">Pitch above the horizontal, clamped into
        /// <see cref="MinPitchRadians"/>..<see cref="MaxPitchRadians"/>.</param>
        public void SetOrbit(float yawRadians, float pitchRadians)
        {
            _goalYaw = (float)Normalize(yawRadians);
            _goalPitch = ClampPitch(pitchRadians);
        }

        /// <summary>
        /// Nudge the orbit, which is what a mouse drag becomes. Works in every mode: turning the
        /// camera while walking is a thing players expect, and refusing it in
        /// <see cref="NeighbourhoodWorldCameraMode.Follow"/> would mean the controls had to know
        /// which mode they were in.
        /// </summary>
        /// <param name="yawDelta">Radians to turn, positive towards +x from +z.</param>
        /// <param name="pitchDelta">Radians to raise or lower.</param>
        public void Orbit(float yawDelta, float pitchDelta)
        {
            if (Finite(yawDelta)) _goalYaw = (float)Normalize(_goalYaw + yawDelta);
            if (Finite(pitchDelta)) _goalPitch = ClampPitch(_goalPitch + pitchDelta);
        }

        /// <summary>
        /// Ask for a distance, in tiles, and let the camera travel there. The value is stored
        /// against the <em>current</em> mode - <see cref="FollowDistance"/>,
        /// <see cref="LotFocusDistance"/> or <see cref="FreeDistance"/> - so each mode keeps its
        /// own zoom and returning to one finds it as it was left.
        /// </summary>
        /// <param name="distance">How far back to sit, in tiles.</param>
        public void ZoomTo(float distance)
        {
            var clamped = ClampDistance(distance);
            switch (_mode)
            {
                case NeighbourhoodWorldCameraMode.Follow:
                    _followDistance = clamped;
                    break;
                case NeighbourhoodWorldCameraMode.LotFocus:
                    _lotFocusDistance = clamped;
                    break;
                default:
                    _freeDistance = clamped;
                    break;
            }
        }

        /// <summary>
        /// Zoom by a relative amount; positive is further out. The sign convention is the wheel's,
        /// so a caller does not have to remember which way round its own input is.
        /// </summary>
        /// <param name="tiles">How much further away, in tiles.</param>
        public void Zoom(float tiles)
        {
            if (!Finite(tiles)) return;
            ZoomTo(GoalDistance + tiles);
        }

        // ---- the tick -------------------------------------------------------------------------

        /// <summary>
        /// Advance the camera by one frame's worth of elapsed time: move the look-at point
        /// towards the current mode's subject and the orbit towards its goals, both by
        /// <see cref="AlphaFor"/> of what is left.
        /// </summary>
        /// <remarks>
        /// The integration contract is in the class remarks: one call per frame, with that
        /// frame's elapsed seconds, and the camera ends up in the same place after one second
        /// whether it was told about it once, seven times, or sixty.
        /// </remarks>
        /// <param name="deltaSeconds">Elapsed seconds since the last call.</param>
        public void Update(double deltaSeconds)
        {
            //NaN fails this test as surely as a negative does, which is the point: a frame that
            //lost its clock is a paused frame, and a paused frame must not teleport the camera.
            if (!(deltaSeconds > 0) || double.IsInfinity(deltaSeconds)) return;

            Vector3 goal;
            float distance;
            ResolveGoal(out goal, out distance);
            _goalDistance = ClampDistance(distance);

            var targetAlpha = AlphaFor(_targetHalfLife, deltaSeconds);
            var orbitAlpha = AlphaFor(_orbitHalfLife, deltaSeconds);

            _target = Vector3.Lerp(_target, _bounds.Clamp(goal), (float)targetAlpha);
            _target = _bounds.Clamp(_target);

            _distance = (float)(_distance + (_goalDistance - _distance) * orbitAlpha);
            _pitch = (float)(_pitch + (_goalPitch - _pitch) * orbitAlpha);
            //The short way round: a yaw is an angle, so 179 to -179 degrees is two degrees of
            //turn and 358 of spin, and the spin is the bug this line prevents.
            _yaw = (float)Normalize(_yaw + ShortestAngle(_yaw, _goalYaw) * orbitAlpha);

            _distance = ClampDistance(_distance);
            _pitch = ClampPitch(_pitch);
        }

        /// <summary>
        /// Whether the camera has arrived: both the look-at point and the distance are within
        /// <paramref name="tolerance"/> tiles of their goals. A scene can use it to stop
        /// re-sending input, and a test can use it instead of counting frames.
        /// </summary>
        /// <param name="tolerance">How close counts as arrived, in tiles.</param>
        public bool IsSettled(float tolerance = 0.05f)
        {
            if (!(tolerance >= 0f) || !Finite(tolerance)) tolerance = 0.05f;

            Vector3 goal;
            float distance;
            ResolveGoal(out goal, out distance);

            return Vector3.Distance(_target, _bounds.Clamp(goal)) <= tolerance
                && Math.Abs(_distance - ClampDistance(distance)) <= tolerance
                && Math.Abs(_pitch - _goalPitch) <= 1e-4f;
        }

        /// <summary>
        /// The stand-point this camera currently implies, as the same pure
        /// <see cref="NeighbourhoodCameraPlacement"/> the neighbourhood view already consumes -
        /// so the view matrix, the cull frustum and the fog distance are all derived from one
        /// target and one distance rather than from three that can disagree.
        /// </summary>
        public NeighbourhoodCameraPlacement Placement
        {
            get
            {
                var distance = ClampDistance(_distance);
                return NeighbourhoodCameraPlacement.Placing(
                    _bounds.Clamp(_target), distance, ClampPitch(_pitch), _yaw,
                    WidthForDistance(distance, _aspect));
            }
        }

        /// <summary>
        /// How wide a view is, in tiles, at a given distance and aspect: the inverse of
        /// <see cref="NeighbourhoodCameraPlacement.DistanceFor"/>, and the reason
        /// <see cref="Placement"/>'s <c>TilesAcross</c> agrees with the view matrix built from
        /// it. A camera that reported a stale width would still draw correctly and would still
        /// frame wrongly for anything that reasoned about <c>TilesAcross</c>.
        /// </summary>
        /// <param name="distance">How far the camera is from its target, in tiles.</param>
        /// <param name="aspect">Viewport aspect ratio.</param>
        public static float WidthForDistance(float distance, float aspect)
        {
            if (!(distance > 0f) || !Finite(distance)) distance = 1f;
            if (!(aspect > 0f) || !Finite(aspect)) aspect = NeighbourhoodCameraPlacement.DefaultAspect;
            return 2f * distance * (float)Math.Tan(NeighbourhoodCameraPlacement.FieldOfViewRadians / 2f) * aspect;
        }

        /// <summary>
        /// The fraction of the remaining error one step of length <paramref name="deltaSeconds"/>
        /// removes: <c>1 - 2^(-dt/h)</c>, the exact solution of a constant-goal exponential
        /// decay. The per-step factors multiply, so the camera is in the same place after any
        /// number of steps totalling the same time.
        /// </summary>
        /// <param name="halfLifeSeconds">Time for half the error to go, in seconds; 0 or less is instant.</param>
        /// <param name="deltaSeconds">The step, in seconds.</param>
        public static double AlphaFor(double halfLifeSeconds, double deltaSeconds)
        {
            if (double.IsNaN(halfLifeSeconds) || !(halfLifeSeconds > 0) || double.IsInfinity(halfLifeSeconds)) return 1.0;
            if (double.IsNaN(deltaSeconds) || !(deltaSeconds > 0) || double.IsInfinity(deltaSeconds)) return 0.0;
            var alpha = 1.0 - Math.Pow(0.5, deltaSeconds / halfLifeSeconds);
            if (double.IsNaN(alpha)) return 1.0;
            if (alpha < 0) return 0.0;
            return alpha > 1.0 ? 1.0 : alpha;
        }

        // ---- internals ------------------------------------------------------------------------

        /// <summary>
        /// What the current mode is trying to look at and how far back it wants to be.
        /// <see cref="Update"/>, <see cref="IsSettled"/> and <see cref="SnapTo"/> all read it
        /// through here, which is the only way they can be made to agree.
        /// </summary>
        private void ResolveGoal(out Vector3 target, out float distance)
        {
            switch (_mode)
            {
                case NeighbourhoodWorldCameraMode.Follow:
                    //no avatar yet: hold where the camera is rather than cross the map for the origin.
                    target = _hasAvatar ? _bounds.Clamp(_avatar) : _target;
                    distance = _followDistance;
                    break;
                case NeighbourhoodWorldCameraMode.LotFocus:
                    target = _hasLot ? _bounds.Clamp(_lot) : _target;
                    distance = _lotFocusDistance;
                    break;
                default:
                    //Free: the look-at point is wherever the player put it, so the goal and the
                    //state are the same number and the smoothing has nothing left to do.
                    target = _target;
                    distance = _freeDistance;
                    break;
            }
        }

        private static bool Finite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        private static float HalfLife(float seconds)
        {
            return seconds > 0f && Finite(seconds) ? seconds : 0f;
        }

        /// <summary>A distance a caller asked for, folded into something the orbit can hold.</summary>
        private static float SanitiseDistance(float value, float fallback)
        {
            return Finite(value) ? ClampDistance(value) : (fallback > 0f ? ClampDistance(fallback) : MinDistance);
        }

        private static float ClampDistance(float value)
        {
            if (!Finite(value)) return MinDistance;
            if (value < MinDistance) return MinDistance;
            if (value > MaxDistance) return MaxDistance;
            return value;
        }

        private static float ClampPitch(float value)
        {
            if (!Finite(value)) return NeighbourhoodCameraPlacement.DefaultPitchRadians;
            if (value < MinPitchRadians) return MinPitchRadians;
            if (value > MaxPitchRadians) return MaxPitchRadians;
            return value;
        }

        private static double ShortestAngle(double from, double to)
        {
            var delta = (to - from) % (Math.PI * 2.0);
            if (delta > Math.PI) delta -= Math.PI * 2.0;
            else if (delta < -Math.PI) delta += Math.PI * 2.0;
            return delta;
        }

        private static double Normalize(double radians)
        {
            if (double.IsNaN(radians) || double.IsInfinity(radians)) return 0.0;
            var twoPi = Math.PI * 2.0;
            var angle = radians % twoPi;
            if (angle > Math.PI) angle -= twoPi;
            if (angle <= -Math.PI) angle += twoPi;
            return angle;
        }

        public override string ToString()
        {
            return _mode + " camera, target (" + _target.X.ToString("0.##") + ", "
                + _target.Y.ToString("0.##") + ", " + _target.Z.ToString("0.##") + ") distance "
                + _distance.ToString("0.##") + " pitch " + _pitch.ToString("0.##")
                + " yaw " + _yaw.ToString("0.##") + " bounds " + _bounds;
        }
    }
}
