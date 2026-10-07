/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Panels;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Where the 3D camera stands and what it looks at, as pure arithmetic.
    ///
    /// <para>
    /// <b>What it has to reproduce.</b> <c>NeighCameraMachine</c> answers in bitmap pixels: "the
    /// whole map", or "this lot, at this zoom, centred here". M3 needs that as a camera, and the
    /// only honest translation is the one that keeps <em>zoom meaning the same thing</em>, because
    /// zoom is the one parameter the player already understands and the one the panel's tween
    /// drives. So the zoom scalar is carried across untouched and spent on camera distance
    /// instead of on a 2D scale factor, and the only thing the mode changes is which point is
    /// looked at.
    /// </para>
    ///
    /// <para>
    /// <b>Yaw 45 degrees, because the mesh is a diamond.</b> <c>CityGeometry</c>'s tile loop clips
    /// the 512x512 square to TSO's isometric diamond, and §0 decision 2 accepts that rather than
    /// forking it. A square rotated 45 degrees in the XZ plane only looks square from a camera
    /// whose yaw is 45 degrees off an axis, so that is the yaw; and at that yaw the packer's own
    /// projection lines up with the screen axes - <c>mapX</c> runs along screen-x and <c>mapY</c>
    /// along screen-y, which is why the house buttons, which the panel places at their bitmap
    /// positions, land over roughly the right place without being moved.
    /// </para>
    ///
    /// <para>
    /// <b>Pitch is a starting point, not a claim.</b> A 2:1 isometric bitmap foreshortens the
    /// vertical by one half, which is <c>sin(pitch) = 0.5</c> - 30 degrees. Whether that is the
    /// angle a generated neighbourhood reads best at is a thing to look at, and
    /// <see cref="DefaultPitchRadians"/> is a single named constant so that tuning it is a
    /// one-line change rather than a hunt.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodCameraPlacement
    {
        /// <summary>
        /// The pitch: 30 degrees, the angle at which a world step in <c>mapY</c> is foreshortened
        /// to half a step in <c>mapX</c>, i.e. the 2:1 isometric the neighbourhood bitmaps use.
        /// </summary>
        public const float DefaultPitchRadians = (float)(Math.PI / 6.0);

        /// <summary>
        /// The yaw: 45 degrees off an axis, so the isometric diamond the mesh is clipped to is
        /// seen square-on rather than as a lozenge.
        /// </summary>
        public const float DefaultYawRadians = (float)(Math.PI / 4.0);

        /// <summary>
        /// Vertical field of view, matching <c>BasicCamera</c>'s own default so the framing maths
        /// and the projection agree about what "45 degrees" means.
        /// </summary>
        public const float FieldOfViewRadians = (float)(Math.PI / 4.0);

        /// <summary>
        /// Screen aspect assumed when no device has been measured. 4:3, because the neighbourhood
        /// bitmap is 800x600 and the panel's own zoom arithmetic is built around that.
        /// </summary>
        public const float DefaultAspect = 4f / 3f;

        /// <summary>
        /// How wide the visible region is, in tiles, when the whole map is framed - the width the
        /// camera has to fit, not the map's side.
        ///
        /// <para>
        /// <b>It is the diagonal, not the side, and getting that wrong crops the map.</b> The mesh
        /// is a 512x512 square clipped to TSO's isometric diamond, and the camera's yaw is 45
        /// degrees - which is what makes the diamond look square-on. The consequence is that the
        /// diamond's <em>diagonal</em> is what lies along the screen-x axis, so framing the map's
        /// side fills the screen with the middle of it and cuts the four points of the diamond off
        /// at the edges. Measured on the real 512 map with the real camera: a 512-tile framing puts
        /// 3648 of the map's own tile corners outside the frustum and culls 102 of the 1024
        /// chunks; 512 * sqrt(2) - about 724 - puts nothing outside and culls nothing.
        /// </para>
        /// <para>
        /// That cropped state is not a cosmetic problem either: two of the culled chunks carry real
        /// ground, and a full-screen view that drops a corner of the neighbourhood is a view of most
        /// of a neighbourhood.
        /// </para>
        /// </summary>
        /// <param name="mapSize">Side of the generated map, in tiles.</param>
        public static float VisibleWidthFor(int mapSize)
        {
            if (mapSize <= 0) mapSize = NeighbourhoodLayout.DefaultMapSize;
            return mapSize * DiagonalFactor;
        }

        /// <summary>
        /// The square root of two, the ratio of a square's diagonal to its side. Named because it
        /// is the whole of <see cref="VisibleWidthFor"/>'s answer and the number is otherwise a
        /// magic constant sitting in a framing calculation. Spelled out rather than
        /// <c>(float)Math.Sqrt(2)</c> because a <c>const</c> cannot call it.
        /// </summary>
        public const float DiagonalFactor = 1.41421356f;

        /// <summary>
        /// How far back a camera has to sit to fit <paramref name="tilesAcross"/> of world into a
        /// viewport of the given field of view and aspect.
        /// </summary>
        /// <remarks>
        /// The horizontal half-angle, not the vertical one: a 4:3 screen is wider than it is tall,
        /// so a square footprint is limited by the horizontal field of view. Nearer than this and
        /// the neighbourhood's corners fall off the screen; further and it is a diorama in the
        /// middle of a lot of sky.
        /// </remarks>
        /// <param name="tilesAcross">Width to fit, in tiles.</param>
        /// <param name="aspect">Viewport width divided by height.</param>
        /// <param name="fovRadians">Vertical field of view.</param>
        public static float DistanceFor(float tilesAcross, float aspect, float fovRadians)
        {
            if (!(tilesAcross > 0f)) tilesAcross = 1f;
            if (!(aspect > 0f)) aspect = DefaultAspect;
            if (!(fovRadians > 0f) || fovRadians >= Math.PI) fovRadians = FieldOfViewRadians;

            var horizontalHalf = Math.Atan(Math.Tan(fovRadians / 2f) * aspect);
            return (tilesAcross / 2f) / (float)Math.Tan(horizontalHalf);
        }

        /// <summary>The world position the camera looks at.</summary>
        public Vector3 Target { get; }

        /// <summary>The world position the camera stands at.</summary>
        public Vector3 Position { get; }

        /// <summary>How far the camera is from its target, in world units (tiles).</summary>
        public float Distance { get; }

        /// <summary>How far below the horizontal the camera sits, in radians.</summary>
        public float Pitch { get; }

        /// <summary>How far off an axis the camera sits, in radians.</summary>
        public float Yaw { get; }

        /// <summary>How wide the view was asked to be, in tiles, before distance turned it into
        /// an angle. Kept so a caller can reason about "is the lot in frame" without a camera.</summary>
        public float TilesAcross { get; }

        /// <summary>
        /// The view-projection this placement implies, as a pure function of the placement and a
        /// viewport. The point of it being here rather than in the scene is that the frustum the
        /// cull is tested against and the matrix the vertices are transformed by have to come from
        /// the same place, and there is no way for that to go wrong if there is only one of them.
        /// </summary>
        /// <param name="nearPlane">Near clip distance.</param>
        /// <param name="farPlane">Far clip distance.</param>
        /// <param name="aspect">Viewport width divided by height.</param>
        public Matrix ViewProjection(float nearPlane, float farPlane, float aspect)
        {
            if (!(nearPlane > 0f)) nearPlane = 1f;
            //A far plane nearer than the near plane is a projection matrix full of NaNs, and a
            //NaN matrix in a BoundingFrustum culls every chunk: a blank screen out of a typo.
            if (!(farPlane > nearPlane)) farPlane = nearPlane * 1000f;
            if (!(aspect > 0f)) aspect = DefaultAspect;
            var projection = Matrix.CreatePerspectiveFieldOfView(FieldOfViewRadians, aspect, nearPlane, farPlane);
            return View * projection;
        }

        /// <summary>
        /// The bounding frustum for this placement, in the same space as the mesh.
        ///
        /// <para>
        /// <b>Derived from <see cref="ViewProjection"/> and nothing else</b>, so the cull cannot
        /// be handed a matrix from somewhere else and quietly disagree with the draw. That was the
        /// bug this exists to make impossible: a caller that built a frustum from, say, the view
        /// matrix alone would cull every chunk, and the log - which counted the mesh's triangles -
        /// would still say the world was drawn.
        /// </para>
        /// </summary>
        /// <param name="nearPlane">Near clip distance.</param>
        /// <param name="farPlane">Far clip distance.</param>
        /// <param name="aspect">Viewport width divided by height.</param>
        public BoundingFrustum Frustum(float nearPlane, float farPlane, float aspect)
        {
            return new BoundingFrustum(ViewProjection(nearPlane, farPlane, aspect));
        }

        /// <summary>
        /// The view matrix this placement implies.
        ///
        /// <para>
        /// <b>Not the same matrix as the fog wants.</b> <c>PixShader</c>'s <c>Fog</c> computes
        /// <c>length(vPos) / FogMaxDist</c> where <c>vPos</c> is the vertex transformed by the
        /// <c>MV</c> uniform, and <c>Terrain.cs</c> sets that uniform to the <em>view</em> matrix -
        /// so it is a distance from the camera, and passing the view-projection instead measures a
        /// length in clip space, which is near zero for everything and fogs the whole map flat.
        /// </para>
        /// </summary>
        public Matrix View
        {
            get { return Matrix.CreateLookAt(Position, Target, Vector3.Up); }
        }

        /// <summary>
        /// How many camera-distances away the distance fade completes, as a multiple of
        /// <see cref="Distance"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The fog distance has to be a function of the camera's distance rather than a constant,
        /// because the distance is what the zoom spends. A constant tuned for the full-screen view
        /// is a wall of fog at half that distance and a constant tuned for a focused lot is no fog
        /// at all on the overview: the camera stands about 655 units from the middle of a 512-tile
        /// map at full screen and about 220 at Unleashed's lot zoom, and the map's own corners run
        /// from roughly 0.6x to 1.5x the target's distance at any of them.
        /// </para>
        /// <para>
        /// Three, so the near ground is barely hazed and the far corner of the map is about half
        /// faded. That is aerial perspective rather than a white-out, and it is what the city's own
        /// 2D view does at the same zoom.
        /// </para>
        /// </remarks>
        public const float FogDistanceFactor = 3f;

        /// <summary>
        /// The distance at which the fog completes for this placement, in world units. Always
        /// positive, so it can be handed to the pixel shader as <c>FogMaxDist</c> without the shader
        /// dividing by zero.
        /// </summary>
        public float FogDistance
        {
            get { return FogDistanceFor(Distance); }
        }

        /// <summary>
        /// The fog distance for a camera at <paramref name="distance"/>, and a usable positive
        /// number for any input at all - including none, because a placement built from a device
        /// that has just been reset can have a distance of zero.
        /// </summary>
        /// <param name="distance">How far the camera is from its target, in world units.</param>
        public static float FogDistanceFor(float distance)
        {
            if (float.IsNaN(distance) || float.IsInfinity(distance) || !(distance > 0f)) return 1f;
            return distance * FogDistanceFactor;
        }

        private NeighbourhoodCameraPlacement(Vector3 target, Vector3 position, float distance, float pitch, float yaw, float tilesAcross)
        {
            Target = target;
            Position = position;
            Distance = distance;
            Pitch = pitch;
            Yaw = yaw;
            TilesAcross = tilesAcross;
        }

        /// <summary>
        /// Build a placement from a look-at point and a width to frame.
        /// </summary>
        /// <param name="target">The world point to look at.</param>
        /// <param name="tilesAcross">Width to fit, in tiles.</param>
        /// <param name="aspect">Viewport aspect ratio.</param>
        public static NeighbourhoodCameraPlacement Framing(
            Vector3 target, float tilesAcross, float aspect)
        {
            var distance = DistanceFor(tilesAcross, aspect, FieldOfViewRadians);
            return Placing(target, distance, DefaultPitchRadians, DefaultYawRadians, tilesAcross);
        }

        /// <summary>
        /// Build a placement from a look-at point and a distance. The orbit is the standard one:
        /// the camera sits on a sphere of that radius around the target, at
        /// <paramref name="pitch"/> above the horizontal and <paramref name="yaw"/> off an axis.
        /// </summary>
        /// <param name="target">The world point to look at.</param>
        /// <param name="distance">Radius of the orbit, in world units.</param>
        /// <param name="pitch">Radians above the horizontal.</param>
        /// <param name="yaw">Radians off the +Z axis, towards +X.</param>
        /// <param name="tilesAcross">The width this placement was asked to frame.</param>
        public static NeighbourhoodCameraPlacement Placing(
            Vector3 target, float distance, float pitch, float yaw, float tilesAcross)
        {
            if (!(distance > 0f)) distance = 1f;
            var horizontal = (float)Math.Cos(pitch) * distance;
            var position = new Vector3(
                target.X + horizontal * (float)Math.Sin(yaw),
                target.Y + (float)Math.Sin(pitch) * distance,
                target.Z + horizontal * (float)Math.Cos(yaw));
            return new NeighbourhoodCameraPlacement(target, position, distance, pitch, yaw, tilesAcross);
        }

        /// <summary>
        /// The placement for a <c>NeighCameraMachine</c> target.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The width to frame is <c>the whole map / zoom</c> in <em>both</em> camera modes, so
        /// zooming in behaves the way the bitmap zooms - <c>HouseFocusZoom</c> of 1.5 is 1.5 times
        /// closer, and Unleashed's 3.0 is three times closer, without either number being
        /// reinterpreted on the way through.
        /// </para>
        /// <para>
        /// The <em>point</em> is the only thing the mode changes, and it is resolved in this order:
        /// the selected lot's packed rectangle if the layout has it (that is where the house
        /// actually is, wherever the packer moved it to), otherwise the projection of the lot's
        /// bitmap position through <see cref="NeighbourhoodWorldMapping"/>, otherwise the
        /// centre of the layout's extent, otherwise the centre of the map. The last two matter
        /// because the full-screen target's bitmap centre (400, 300) is not a lot, and on a map
        /// whose lots cluster in one corner the layout's own centre is the better answer.
        /// </para>
        /// </remarks>
        /// <param name="target">The camera target from <c>NeighCameraMachine</c>.</param>
        /// <param name="mapping">The bitmap/world correspondence.</param>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="mapSize">Side of the generated map, in tiles.</param>
        /// <param name="aspect">Viewport aspect ratio.</param>
        /// <param name="groundHeight">
        /// World height of the ground the camera should look at - the generated map's mean
        /// elevation, so a hilly neighbourhood is not viewed from below its own horizon.
        /// </param>
        /// <param name="houseBitmapPosition">
        /// Where the selected lot's button is on the current bitmap, from
        /// <c>UINeighborhoodSelectionPanel.HousePositions</c>, or <c>null</c> when this view has
        /// no button for it. Only consulted for a lot the layout does not contain.
        /// </param>
        public static NeighbourhoodCameraPlacement For(
            NeighCameraTarget target,
            NeighbourhoodWorldMapping mapping,
            NeighbourhoodLayout layout,
            int mapSize,
            float aspect,
            float groundHeight,
            Vector2? houseBitmapPosition = null)
        {
            var side = mapSize > 0 ? mapSize : NeighbourhoodLayout.DefaultMapSize;
            var zoom = target.Zoom > 0.0001f ? target.Zoom : NeighCameraMachine.FullScreenZoom;
            var tilesAcross = VisibleWidthFor(side) / zoom;

            var focus = target.Mode == NeighCameraMode.HouseFocus
                ? ResolveFocus(target.House, mapping, layout, side, houseBitmapPosition)
                : ResolveCentre(layout, side);

            return Framing(new Vector3(focus.X, groundHeight, focus.Y), tilesAcross, aspect);
        }

        /// <summary>
        /// Where to point the camera when the whole neighbourhood is in frame.
        /// </summary>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="mapSize">Side of the generated map, in tiles.</param>
        public static Vector2 ResolveCentre(NeighbourhoodLayout layout, int mapSize)
        {
            if (layout != null && layout.Extent.Width > 0 && layout.Extent.Height > 0)
            {
                return new Vector2(layout.Extent.CenterX, layout.Extent.CenterY);
            }
            return new Vector2(mapSize / 2f, mapSize / 2f);
        }

        /// <summary>
        /// Where to point the camera for a lot: its packed rectangle if the layout knows it, else
        /// the projection of its bitmap position, else the map's centre.
        /// </summary>
        /// <param name="house">The lot number, or <c>null</c> for no lot.</param>
        /// <param name="mapping">The bitmap/world correspondence.</param>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="mapSize">Side of the generated map, in tiles.</param>
        /// <param name="bitmapPosition">
        /// The lot's button position on the current bitmap, or <c>null</c> when the view has none.
        /// </param>
        public static Vector2 ResolveFocus(
            int? house, NeighbourhoodWorldMapping mapping, NeighbourhoodLayout layout, int mapSize,
            Vector2? bitmapPosition)
        {
            if (house.HasValue)
            {
                if (layout != null)
                {
                    var lot = layout.FindLot(house.Value);
                    if (lot != null) return NeighbourhoodWorldMapping.PackedCentre(lot);
                }
                //not in the layout - a lot the player can select but this map's location table did
                //not name. The projection is still a better guess than the map's centre, because it
                //at least lands in the neighbourhood rather than possibly outside it.
                if (bitmapPosition.HasValue)
                {
                    return mapping.BitmapToWorld((int)bitmapPosition.Value.X, (int)bitmapPosition.Value.Y);
                }
            }
            return ResolveCentre(layout, mapSize);
        }

        public override string ToString()
        {
            return "target (" + Target.X.ToString("0.##") + ", " + Target.Y.ToString("0.##")
                + ", " + Target.Z.ToString("0.##") + ") distance " + Distance.ToString("0.##")
                + " across " + TilesAcross.ToString("0.##");
        }
    }
}
