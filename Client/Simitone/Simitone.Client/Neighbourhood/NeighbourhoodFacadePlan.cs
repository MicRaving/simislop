/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Where one lot's facade mesh goes in the generated world: the lot's local tile coordinates
    /// and heights, turned into world tiles.
    ///
    /// <para><b>The units.</b> <c>LotFacadeGenerator</c> emits its vertices in <em>lot tiles</em>,
    /// not world units: a wall corner is <c>wall.Points / 16</c>, so a 64-tile lot spans
    /// <c>0..64</c> on x and z, and the y of a vertex is the floor number times 2.95 plus the
    /// lot's own interpolated altitude - i.e. height above the lot's ground, in tiles. (The
    /// generator divides the lot view's own world units by three on the way out, which is why the
    /// storey height arrives as 2.95 and not 8.85: a tile is one unit in this space.) The generated
    /// world, on the other hand, is <c>CityGeometry</c>'s world: x and z are absolute map tiles and
    /// y is <see cref="NeighbourhoodCityMeshBuilder.WorldHeightFor"/> of the elevation byte. Both
    /// spaces are tile-based and the origin is simply the lot's rectangle, so this type is the seam
    /// and it is three lines of arithmetic.
    /// </para>
    ///
    /// <para><b>The vertical scale is the mesh builder's, not this file's.</b> Everything below that
    /// converts a height calls
    /// <see cref="NeighbourhoodCityMeshBuilder.WorldHeightFor"/> or
    /// <see cref="NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit"/> rather than dividing by a
    /// number written here, because a facade that stands on a ground height measured on a different
    /// scale from the mesh it stands on is a house floating on a plinth of its own making - and
    /// because the number is then written down in exactly one file. The chain, for the reader who
    /// has to check it: <c>1 tile = 12 elevation bytes</c> (the city mesh's own divisor, which the
    /// terrain, the camera and the in-lot surround all use too), and <c>1 ARRY(0) unit = 3/16 of a
    /// tile</c> (FSO's <c>Blueprint.TerrainFactor</c> of 3/160 tiles per blueprint altitude unit,
    /// times the ten altitude units <c>VMTS1Activator</c> puts in one <c>ARRY(0)</c> unit). Both live
    /// on <see cref="NeighbourhoodCityMeshBuilder"/> as
    /// <see cref="NeighbourhoodCityMeshBuilder.ElevationScale"/> and
    /// <see cref="NeighbourhoodCityMeshBuilder.TilesPerArryUnit"/>.
    /// </para>
    ///
    /// <para><b>The map-to-world conversion on the way in.</b> The other end of the seam is
    /// <c>NeighbourhoodMapAdapter.QuantiseAltitude</c>, which turns a lot's <c>ARRY(0)</c> altitude
    /// into the elevation byte the map is stamped with. That one has to use
    /// <see cref="NeighbourhoodCityMeshBuilder.ElevationUnitsPerArryUnit"/> - nine quarters of a
    /// byte per <c>ARRY(0)</c> unit - and <see cref="NeighbourhoodCityMeshBuilder.ElevationByteForArryUnit"/>
    /// does it; multiplying by <see cref="NeighbourhoodCityMeshBuilder.ElevationScale"/> instead,
    /// as it currently does, says an <c>ARRY(0)</c> unit is a whole tile and inflates every lot's
    /// ground by <c>16/3</c>. Nothing downstream of it can undo that: the mesh, this ground height
    /// and the in-lot surround would all agree with each other and all be wrong together, which is
    /// why the number is named once, here, rather than left to each of them.
    /// </para>
    ///
    /// <para><b>The ground height is read out of the generated map, not out of the layout.</b> The
    /// layout's <see cref="NeighbourhoodLot.BaseHeight"/> is the lot's own ARRY(0) median in the
    /// lot's units; the map's elevation byte is that same value after the quantisation and after
    /// the seam solver has had its way with the border rings. The facade has to stand on what the
    /// ground actually is, so the map is the only honest source, and reading it here means the
    /// quantisation is not duplicated in a second place where it could drift.
    /// </para>
    ///
    /// <para><b>Which tile, and why it is the lot's minimum corner.</b> A plot's ground is not one
    /// height, and a facade can only be given one, so the choice has to match whatever the rest of
    /// the pipeline pairs it with. This one is the corner because that is the tile
    /// <c>SimitoneSurroundingsPlacement.FacadeGroundInLotView</c> measures from when it drops a
    /// neighbouring house's whole mesh onto the surround - it forms the pair
    /// <c>(lot origin, ground height)</c> and asks for the translation that puts it at zero - and
    /// because <see cref="GroundOffsetFor"/>'s correction is anchored at the lot's own altitude
    /// plane rather than at a tile. On a flat plot, which is the common case and true of every stock
    /// TS1 house, the two are the same number and the question does not arise.
    /// </para>
    ///
    /// <para><b>Rotation.</b> <see cref="NeighbourhoodLot.Rotation"/> is <c>HOUS.CameraDir</c>, and
    /// the packer leaves it at 0 for Scope A. It is honoured here as a quarter turn about the lot's
    /// centre - a square is invariant under a quarter turn, so a rotated house stays inside its own
    /// plot and the roads and sidewalks around it do not have to move. It is a real thing about a
    /// TS1 lot (the house can face a different way from the plot) and implementing it now means
    /// the layout field is not decorative.
    /// </para>
    /// </summary>
    public static class NeighbourhoodFacadeTransform
    {
        /// <summary>
        /// How tall a generated lot's walls can be, in tiles, for the frustum bounds.
        /// </summary>
        /// <remarks>
        /// The lot view's own floor height is 2.95 tiles, and a TS1 house is at most a handful of
        /// floors, so this is generous rather than exact. Bounds that are a little too generous
        /// cost nothing; bounds that are too tight make a house disappear at the edge of the view.
        /// </remarks>
        public const float MaxWallHeight = 20f;

        /// <summary>
        /// How far a facade may be lifted or dropped to sit on the generated ground, in tiles.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <c>LotFacadeGenerator</c> measures height from the lot's <em>own</em> altitude plane
        /// (<c>Blueprint.InterpAltitude</c>), while the generated ground comes from
        /// <see cref="GroundHeightFor"/>. For a lot whose <c>ARRY(0)</c> is flat - the common case,
        /// and true of every stock TS1 house - the two agree and the offset is zero. For a lot with
        /// a slope, the lot's own plane at its centre can be several tiles above or below the
        /// generated ground, and without a correction the house is either buried to its eaves or
        /// floating on a plinth of its own making.
        /// </para>
        /// <para>
        /// The correction is deliberately capped. A cap that did not exist would let one corrupt
        /// altitude array fling a house off the map, which is strictly worse than a house that sits
        /// a tile off; four tiles is well past any real lot's relief and well short of anywhere else
        /// on the map.
        /// </para>
        /// <para>
        /// <b>In tiles, like everything else here.</b> A lot's own altitude arrives as either
        /// <c>InterpAltitude</c>'s answer or a raw <c>ARRY(0)</c> median; the second is three
        /// sixteenths of a tile per unit and must go through
        /// <see cref="GroundHeightForArryUnit"/>, or the cap below is being applied to the wrong
        /// quantity and does not bound anything.
        /// </para>
        /// </remarks>
        public const float MaxGroundOffset = 4f;

        /// <summary>
        /// The ground height of a lot in the generated world, in world tiles, or 0 when the map
        /// cannot be asked.
        /// </summary>
        /// <remarks>
        /// Read at the lot's minimum corner, and through
        /// <see cref="NeighbourhoodCityMeshBuilder.WorldHeightFor"/> so that this answer is the mesh
        /// vertex's answer: the facade and the ground it stands on are converted by one function
        /// from one byte, which is the only reason a house cannot end up a quantisation step above
        /// or below the tile it is on. See the type remarks for why the corner and not the centre.
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        public static float GroundHeightFor(NeighbourhoodMap map, NeighbourhoodLot lot)
        {
            if (map == null || lot == null) return 0f;

            //The packer clamps lots into the map, so the origin is normally inside it. A lot that
            //is not - a hand-edited sidecar, a map size that changed under a cached layout - is
            //asked about at its nearest in-map tile rather than being refused, because one bad
            //rectangle must not be able to take a whole neighbourhood down. The height it gets is
            //the map's, not the lot's own, which is the whole point: the seam solver has already
            //had its way with this lot's border rings.
            var x = Math.Max(0, Math.Min(map.Width - 1, lot.WorldX));
            var y = Math.Max(0, Math.Min(map.Height - 1, lot.WorldY));
            return NeighbourhoodCityMeshBuilder.WorldHeightFor(map.ElevationData[map.Index(x, y)]);
        }

        /// <summary>
        /// A lot's own <c>ARRY(0)</c> altitude - its <see cref="NeighbourhoodLot.BaseHeight"/>, read
        /// straight out of the decoded <c>ARRY(0)</c> - as world height in tiles.
        /// </summary>
        /// <remarks>
        /// The other half of the seam, and the one with the factor of ten in it. A TS1 lot's altitudes
        /// are in <c>ARRY(0)</c> units, which are three sixteenths of a tile of height and not a tile
        /// and not a twelfth of one; see
        /// <see cref="NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit"/>. Anything that has to
        /// compare a lot's own ground against the generated ground goes through here, so the
        /// comparison cannot be made in the wrong units by accident.
        ///
        /// <para>
        /// Only for a raw <c>ARRY(0)</c> altitude. A live blueprint's
        /// <c>Blueprint.InterpAltitude</c> has already been through
        /// <c>Blueprint.TerrainFactor</c> and is in tiles, which is why it is what
        /// <see cref="GroundOffsetFor"/> takes directly; running it through this one as well would
        /// squash the plot's relief by a factor of thirty-two.
        /// </para>
        /// </remarks>
        /// <param name="arryUnits">A raw <c>ARRY(0)</c> altitude.</param>
        public static float GroundHeightForArryUnit(float arryUnits)
        {
            return NeighbourhoodCityMeshBuilder.WorldHeightForArryUnit(arryUnits);
        }

        /// <summary>
        /// A point in a lot's own tile space, placed into the generated world.
        /// </summary>
        /// <param name="lot">The lot.</param>
        /// <param name="local">
        /// Lot-local coordinates: x and z in tiles from the lot's own origin, y in tiles above the
        /// lot's ground.
        /// </param>
        /// <param name="groundHeight">The lot's ground height, from <see cref="GroundHeightFor"/>.</param>
        public static Vector3 ToWorld(NeighbourhoodLot lot, Vector3 local, float groundHeight)
        {
            if (lot == null) return local;
            var size = Math.Max(0, lot.Size);
            var centre = size / 2f;
            Rotate(local.X, local.Z, centre, lot.Rotation, out var rx, out var rz);
            return new Vector3(lot.WorldX + rx, groundHeight + local.Y, lot.WorldY + rz);
        }

        /// <summary>
        /// A lot's whole footprint as a world-space box, for the frustum test.
        /// </summary>
        /// <param name="lot">The lot.</param>
        /// <param name="groundHeight">The lot's ground height.</param>
        public static BoundingBox BoundsFor(NeighbourhoodLot lot, float groundHeight)
        {
            if (lot == null) return new BoundingBox(Vector3.Zero, Vector3.Zero);
            var size = Math.Max(1, lot.Size);
            var min = new Vector3(lot.WorldX, groundHeight, lot.WorldY);
            var max = new Vector3(lot.WorldX + size, groundHeight + MaxWallHeight, lot.WorldY + size);
            return new BoundingBox(min, max);
        }

        /// <summary>
        /// The height, in tiles, that a facade's lot-local zero plane has to be moved by to stand on
        /// the generated ground instead of on the lot's own altitude plane.
        /// </summary>
        /// <remarks>
        /// Negative <paramref name="ownGround"/> means the lot's own terrain sits below its own zero
        /// plane, so the facade is lifted; positive means it is dropped. The result is a pure
        /// function of one number and a constant, which is what makes it testable without a
        /// blueprint - the blueprint's altitude is read once by the builder and everything after that
        /// is arithmetic.
        ///
        /// <para>
        /// <b>The units are world tiles</b>, the same as <see cref="GroundHeightFor"/>'s answer and
        /// the mesh's vertices, because that is what it is subtracted from. The builder reads it as
        /// <c>Blueprint.InterpAltitude</c>, which FSO returns in tiles already (altitude units times
        /// <see cref="NeighbourhoodCityMeshBuilder.LotTerrainFactor"/>); a caller holding a raw
        /// <c>ARRY(0)</c> altitude instead must convert it first, with
        /// <see cref="GroundHeightForArryUnit"/>. The cap is four tiles, which is
        /// <c>4 / (3/16) = 64/3</c> <c>ARRY(0)</c> units - far past any real lot's relief.
        /// </para>
        /// </remarks>
        /// <param name="ownGround">
        /// The lot's own altitude at the centre of its plot, in tiles, from
        /// <c>Blueprint.InterpAltitude</c>. Any value outside +/-<see cref="MaxGroundOffset"/> is
        /// clamped to it.
        /// </param>
        public static float GroundOffsetFor(float ownGround)
        {
            //NaN and the infinities are a missing altitude rather than an extreme one, and get the
            //answer for a missing one: no correction, i.e. a flat lot. A NaN that reached the
            //comparisons above would fall through to -NaN, and a house placed at a NaN height is a
            //house the frustum test cannot sort out, so it is caught here rather than downstream.
            if (float.IsNaN(ownGround) || float.IsInfinity(ownGround)) return 0f;
            if (ownGround > MaxGroundOffset) return -MaxGroundOffset;
            if (ownGround < -MaxGroundOffset) return MaxGroundOffset;
            return -ownGround;
        }

        /// <summary>
        /// A facade's vertex list, placed into the generated world.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Pure on purpose. The builder's job after the generator has run is exactly this
        /// arithmetic followed by an upload, and keeping the arithmetic in a method with no
        /// graphics device in it is what lets a test pin the placement of a wall, a roof and a
        /// floor vertex - the three things that can each be wrong in a different way - without a
        /// GPU. A facade that is built correctly and placed wrongly is still invisible, and that is
        /// exactly the failure a pure test catches and a screenshot does not explain.
        /// </para>
        /// <para>
        /// The result is a new array: the generator's own vertex list is left alone, so a caller
        /// that still holds it (the failure path, which reports a reason from it) is unaffected.
        /// </para>
        /// </remarks>
        /// <param name="lot">The lot the mesh was rendered from.</param>
        /// <param name="local">
        /// The generator's output: x and z in tiles from the lot's own origin, y in tiles above the
        /// lot's own altitude plane.
        /// </param>
        /// <param name="groundHeight">The generated world's ground height under the lot.</param>
        /// <param name="groundOffset">
        /// The correction from <see cref="GroundOffsetFor"/>, or 0 to skip it.
        /// </param>
        public static VertexPositionTexture[] PlacedVertices(
            NeighbourhoodLot lot, VertexPositionTexture[] local, float groundHeight, float groundOffset)
        {
            if (local == null) return null;
            var placed = new VertexPositionTexture[local.Length];
            for (int i = 0; i < local.Length; i++)
            {
                var source = local[i];
                placed[i] = new VertexPositionTexture(
                    ToWorld(lot, new Vector3(source.Position.X, source.Position.Y + groundOffset, source.Position.Z),
                        groundHeight),
                    source.TextureCoordinate);
            }
            return placed;
        }

        /// <summary>
        /// A quarter-turn rotation about a centre, in the plane. Rotation 0 is the identity and
        /// rotations are taken modulo four, so a corrupt <c>CameraDir</c> cannot produce a
        /// mirrored or out-of-range house.
        /// </summary>
        /// <param name="x">Local x.</param>
        /// <param name="z">Local z.</param>
        /// <param name="centre">The centre of rotation.</param>
        /// <param name="rotation">Quarter turns, clockwise seen from above.</param>
        /// <param name="resultX">The rotated x.</param>
        /// <param name="resultZ">The rotated z.</param>
        public static void Rotate(float x, float z, float centre, int rotation, out float resultX, out float resultZ)
        {
            var quarter = ((rotation % 4) + 4) % 4;
            var dx = x - centre;
            var dz = z - centre;
            switch (quarter)
            {
                case 1:
                    resultX = centre + dz;
                    resultZ = centre - dx;
                    break;
                case 2:
                    resultX = centre - dx;
                    resultZ = centre - dz;
                    break;
                case 3:
                    resultX = centre - dz;
                    resultZ = centre + dx;
                    break;
                default:
                    resultX = x;
                    resultZ = z;
                    break;
            }
        }
    }

    /// <summary>
    /// Which lots a facade pass should build this frame, and which it should draw. The decisions,
    /// with no graphics device in them.
    ///
    /// <para>
    /// §7.2's three rules, in one place: build each lot's mesh once, cache it by
    /// <see cref="NeighbourhoodLot.HouseHash"/>, and skip the lot the player is standing in
    /// because the lot view is already drawing that one in full. Plus the one the plan does not
    /// mention and a fifty-lot neighbourhood makes obvious: a lot's mesh costs a
    /// <c>FSO.LotView.World</c> and a <c>FSO.SimAntics.VM</c> stood up and torn down, and a whole
    /// neighbourhood's worth of that in one frame is a visible freeze. So the build list is
    /// budgeted, nearest the camera's focus first, and the budget is a parameter rather than a
    /// constant so a test can drive it.
    /// </para>
    /// </summary>
    public static class NeighbourhoodFacadePlan
    {
        /// <summary>
        /// How far from the camera's focus a lot may be and still be worth a mesh, in tiles.
        ///
        /// <para><b>Why a radius at all.</b> The build list used to be "every drawable lot, nearest
        /// first", which means a lot on the far side of a 512x512 map is eventually built whether
        /// or not the player will ever see it - and each of those builds is a whole
        /// <c>FSO.LotView.World</c> and <c>FSO.SimAntics.VM</c> stood up on the game thread. With
        /// 41 lots that is 41 stalls for a neighbourhood of which the camera sees perhaps six at a
        /// time. Outside the radius a lot is <em>deferred</em>, not failed: it keeps its place in
        /// the queue, still has no cache entry, and is built the moment the camera comes near
        /// enough - which is the only moment it could be seen.</para>
        ///
        /// <para>Deliberately generous: the neighbourhood view's fog runs out at the far
        /// corner of the island - about 1,140 world units, and 380 tiles of map from its middle -
        /// so a smaller radius would blank houses that are visibly there and merely small. It also
        /// means the lot view queues every lot of the neighbourhood, which is the same queue the map
        /// view drains and the same cross-visit cache serves it, so walking into a lot from the map
        /// view adopts its houses rather than re-rendering them.</para>
        /// </summary>
        public const float DefaultBuildRadiusTiles = 420f;

        /// <summary>
        /// One facade build, in milliseconds, is a hitch. One facade build, ten times over, is a
        /// freeze, and that was the bug: the log proved two lots' worth of meshes arriving across
        /// eleven seconds of silence with no frame in between.
        /// </summary>
        /// <remarks>
        /// <para>A line the pass reports, not a line it enforces. A build cannot be abandoned half
        /// way - a half-built facade is exactly the partial mesh the view must not show - and
        /// <see cref="FrameAllowance"/>'s millisecond budget is checked <em>before</em> each build,
        /// so it bounds the work a frame starts and never the cost of a build it has already begun.
        /// What is left to bound a build that is too expensive is when the <em>next</em> one is
        /// allowed to start, and that is <see cref="PaceAfterBuild"/>.</para>
        ///
        /// <para><b>It no longer stops the pass, and that is the whole of the change.</b> It used
        /// to: a build over the ceiling set a flag that ended building for the rest of the visit,
        /// which on a machine where every build costs two seconds produced exactly one house in a
        /// forty-one lot neighbourhood and a log line explaining why. "The player has been told
        /// and the screen is frozen for ninety seconds" and "one house" are not a defensible pair
        /// of outcomes for a view whose whole job is to show a neighbourhood; the honest trade is
        /// to say on every line which build broke the ceiling, keep the pass to at most
        /// <see cref="DefaultBuildsPerFrame"/> builds a frame, and let it run to the end of the
        /// queue. See <see cref="PaceAfterBuild"/> and
        /// <see cref="NeighbourhoodFacadePace"/>.</para>
        /// </remarks>
        public const double DefaultMaxBuildMs = 250d;

        /// <summary>
        /// The most frames a build of any cost is allowed to push the next one back by.
        ///
        /// <para>
        /// <b>Why the cooldown needs a ceiling as well as a floor.</b> The cooldown exists to
        /// average a frame's building cost down towards
        /// <see cref="NeighbourhoodFacades.BuildTimeBudgetMs"/>, and for a build that costs less
        /// than that it does not engage at all. For a build that costs seconds it engages hard: a
        /// two second build at a ten millisecond budget is two hundred quiet frames, and forty-one
        /// of those is a hundred and twenty seconds of a neighbourhood that looks empty. But the
        /// stall has already happened by the time the cost is known - nothing the pass does after
        /// the fact makes that frame shorter - so the only thing the long cooldown buys is a
        /// longer wait before the next equally long stall, and what it costs is the rest of the
        /// houses. Two frames is the answer that keeps the two apart: at most two skipped frames
        /// between builds, so the queue advances at whatever rate the builds are, and the player
        /// sees a house appear rather than a progress bar they have to wait out.
        /// </para>
        /// </summary>
        public const int DefaultMaxCooldownFrames = 2;

        /// <summary>
        /// What one build's cost means for the rest of the visit: how long the pass waits before
        /// starting the next one, and whether that build broke the per-frame stall ceiling.
        /// </summary>
        /// <remarks>
        /// A struct rather than two return values because the two are one decision, and the caller
        /// must act on both of them together: the frames are set whatever the answer, and the flag
        /// is what decides whether the log line has to say the cost broke the ceiling. Returning
        /// them separately invites a caller that reads the flag and forgets the delay, which is a
        /// forty-one build freeze.
        /// </remarks>
        public readonly struct NeighbourhoodFacadePace
        {
            /// <summary>How many whole frames to leave alone before the next build.</summary>
            public int Frames { get; }

            /// <summary>Whether the build cost more than the per-frame stall ceiling.</summary>
            public bool OverCeiling { get; }

            /// <summary>No measurement, no delay and nothing over the line.</summary>
            public static readonly NeighbourhoodFacadePace Immediate =
                new NeighbourhoodFacadePace(0, false);

            public NeighbourhoodFacadePace(int frames, bool overCeiling)
            {
                Frames = frames < 0 ? 0 : frames;
                OverCeiling = overCeiling;
            }
        }

        /// <summary>
        /// How long to wait before the next build, given what the last one cost, and whether that
        /// cost broke the per-frame stall ceiling.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Three rules, in the order they matter.</b> The wait is
        /// <see cref="FramesAfterBuild"/>, clamped to <paramref name="maxCooldownFrames"/> so a
        /// slow machine cannot throttle the queue to a crawl - see
        /// <see cref="DefaultMaxCooldownFrames"/>. The flag is
        /// <see cref="WithinBuildCeiling"/>, which the pass does not act on by stopping but by
        /// saying: every build's line carries the cost, a build that broke the ceiling is marked
        /// on it, and the first one that breaks it gets a warning saying what the pass is going to
        /// do instead. The count of builds per frame is <em>not</em> here - it is
        /// <see cref="FrameAllowance"/>, and it is unaffected by any of this.
        /// </para>
        /// <para>
        /// <b>What this guarantees.</b> Steady progress. There is no input to
        /// <c>DecideAfterBuild</c> - not any build cost, however large, and not any ceiling,
        /// however small - for which it can return a wait the pass will not come back from, and
        /// the wait it does return is bounded by a constant. That is what makes "1 of 41 houses
        /// appears" impossible rather than merely unlikely.
        /// </para>
        /// </remarks>
        /// <param name="buildMs">How long the build that just finished took, in milliseconds.</param>
        /// <param name="frameBudgetMs">
        /// The per-frame cost the pass averages towards, in milliseconds. Zero or less disables
        /// the spacing entirely, which is also the answer for a cost that could not be measured.
        /// </param>
        /// <param name="maxBuildMs">
        /// The per-frame stall ceiling, in milliseconds. Zero or less means no ceiling, and the
        /// flag is then never raised.
        /// </param>
        /// <param name="maxCooldownFrames">
        /// The most frames a build may push the next one back by. Zero or less means the cooldown
        /// is not used at all, which is the same thing as a build every frame.
        /// </param>
        public static NeighbourhoodFacadePace DecideAfterBuild(
            double buildMs, double frameBudgetMs, double maxBuildMs, int maxCooldownFrames)
        {
            var cap = maxCooldownFrames < 0 ? 0 : maxCooldownFrames;
            var frames = FramesAfterBuild(buildMs, frameBudgetMs);
            if (frames > cap) frames = cap;
            return new NeighbourhoodFacadePace(frames, !WithinBuildCeiling(buildMs, maxBuildMs));
        }

        /// <summary>
        /// The lots whose facades should be built this frame, nearest the camera's focus first,
        /// within <paramref name="radius"/> of it.
        /// </summary>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="cachedHashes">
        /// The hash each already-cached facade was built from, keyed by lot number. A lot absent
        /// from it needs building; a lot whose entry differs from the layout's needs rebuilding; a
        /// lot whose entry matches needs nothing.
        /// </param>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="focus">The world tile the camera is looking at.</param>
        /// <param name="budget">How many meshes may be built this frame.</param>
        /// <param name="radius">
        /// How far from <paramref name="focus"/> a lot may be and still be built. Zero or less
        /// removes the limit.
        /// </param>
        /// <param name="subWorldLots">
        /// The lot numbers a <see cref="SimitoneSurroundingsPlan"/> will stand up as real
        /// sub-worlds, or <c>null</c> for none. One of these is never built here - see
        /// <see cref="NeighbourhoodRenderDecision"/> - and the map view, which has no sub-worlds,
        /// passes nothing and gets exactly the list it got before the decision existed.
        /// </param>
        public static IReadOnlyList<NeighbourhoodLot> ToBuild(
            NeighbourhoodLayout layout, IReadOnlyDictionary<short, uint> cachedHashes,
            int? activeLot, Vector2 focus, int budget, float radius,
            IReadOnlyCollection<short> subWorldLots = null)
        {
            var wanted = new List<NeighbourhoodLot>();
            if (layout == null || budget <= 0) return wanted;

            var distance = new Dictionary<short, float>();
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var lot = layout.Lots[i];
                if (!ShouldDraw(lot, activeLot, subWorldLots)) continue;
                if (!NeedsBuild(lot, cachedHashes)) continue;

                var centre = NeighbourhoodWorldMapping.PackedCentre(lot);
                var offset = new Vector2(centre.X - focus.X, centre.Y - focus.Y);
                if (!WithinRadius(offset, radius)) continue;
                distance[lot.Number] = offset.LengthSquared();
                wanted.Add(lot);
            }

            //nearest first, with the lot number as the tie-break, so the order is deterministic and
            //two lots at the same distance cannot swap places between frames
            wanted.Sort((a, b) =>
            {
                var da = distance[a.Number];
                var db = distance[b.Number];
                if (da != db) return da.CompareTo(db);
                return a.Number.CompareTo(b.Number);
            });

            if (wanted.Count > budget) wanted.RemoveRange(budget, wanted.Count - budget);
            return wanted;
        }

        /// <summary>
        /// The lots whose facades should be built this frame, nearest the camera's focus first,
        /// with no distance limit.
        /// </summary>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="cachedHashes">The hash each already-cached facade was built from.</param>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="focus">The world tile the camera is looking at.</param>
        /// <param name="budget">How many meshes may be built this frame.</param>
        public static IReadOnlyList<NeighbourhoodLot> ToBuild(
            NeighbourhoodLayout layout, IReadOnlyDictionary<short, uint> cachedHashes,
            int? activeLot, Vector2 focus, int budget)
        {
            return ToBuild(layout, cachedHashes, activeLot, focus, budget, 0f);
        }

        /// <summary>
        /// Whether an offset from the camera's focus is inside a build radius.
        /// </summary>
        /// <remarks>
        /// A radius of zero or less is "no limit" rather than "nothing is in range", because the
        /// unbounded overload has to be the same rule with the radius turned off, not a different
        /// rule. A NaN offset is outside every radius: a camera that has not been placed yet
        /// should build nothing, and the alternative is building everything.
        /// </remarks>
        /// <param name="offset">The lot's offset from the camera's focus, in tiles.</param>
        /// <param name="radius">The radius, in tiles.</param>
        public static bool WithinRadius(Vector2 offset, float radius)
        {
            if (radius <= 0f) return true;
            if (float.IsNaN(offset.X) || float.IsNaN(offset.Y)) return false;
            return offset.LengthSquared() <= radius * radius;
        }

        /// <summary>
        /// How many whole frames to leave alone after a build that cost
        /// <paramref name="buildMs"/>, so the pass's average cost per frame stays near
        /// <paramref name="frameBudgetMs"/>.
        /// </summary>
        /// <remarks>
        /// <para>This is the rule that turns a stall into a background trickle, and it is the one
        /// that matters once a build is known to be expensive. A build cannot be abandoned half
        /// way - a half-built facade is exactly the partial mesh the view must not show - so the
        /// only lever is how often the next one is started. Spacing them by their own measured
        /// cost is self-calibrating: a cheap lot lets the next one go next frame, and a four
        /// second lot is followed by four hundred quiet ones.</para>
        ///
        /// <para>One frame is subtracted because the build already happened in one: the frame that
        /// just ran <em>was</em> the cost, and waiting a full multiple on top of it doubles the
        /// first interval for no benefit.</para>
        /// </remarks>
        /// <param name="buildMs">How long the build that just finished took, in milliseconds.</param>
        /// <param name="frameBudgetMs">
        /// The per-frame cost the pass is trying to average down to, in milliseconds. Zero or less
        /// disables the spacing.
        /// </param>
        public static int FramesAfterBuild(double buildMs, double frameBudgetMs)
        {
            if (buildMs <= 0d || frameBudgetMs <= 0d) return 0;
            if (double.IsNaN(buildMs) || double.IsInfinity(buildMs)) return 0;
            var frames = (int)Math.Ceiling(buildMs / frameBudgetMs) - 1;
            return frames < 0 ? 0 : frames;
        }

        /// <summary>
        /// Whether the last build stayed inside the per-frame stall ceiling, and so whether the
        /// pass has to tell the player that a frame was held up for longer than it said it would.
        /// </summary>
        /// <remarks>
        /// <para>The rule is deliberately one-sided, and the <em>consequence</em> of it is
        /// deliberately small. A build that came in under the ceiling says nothing about whether
        /// the next one will - the cost is the lot's, not ours, and the next lot may be an
        /// apartment rather than a detached house - so the pass is allowed to keep trying. A build
        /// that came in over it says the ceiling is wrong for this machine and this neighbourhood,
        /// and the only thing that follows from that is that somebody has to be told: the per-lot
        /// timing line is marked, and the first breach gets a warning. It does not follow that the
        /// pass should stop, which is what used to happen and what left a neighbourhood with one
        /// house in it and a log line saying why. See <see cref="DecideAfterBuild"/>.</para>
        /// </remarks>
        /// <param name="lastBuildMs">How long the last build took, in milliseconds, or zero.</param>
        /// <param name="maxBuildMs">
        /// The ceiling, in milliseconds. Zero or less means no ceiling.
        /// </param>
        public static bool WithinBuildCeiling(double lastBuildMs, double maxBuildMs)
        {
            if (maxBuildMs <= 0d) return true;
            if (lastBuildMs <= 0d) return true;
            if (double.IsNaN(lastBuildMs)) return false;
            return lastBuildMs <= maxBuildMs;
        }

        /// <summary>
        /// Whether a lot contributes a facade at all. An empty plot draws nothing - the plan's
        /// "lots with no house draw nothing, or a bare plot" - the lot the player is standing in is
        /// drawn by the lot view instead, and a lot with no drawn ground under it draws nothing
        /// either.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The fourth exclusion is B2's: a lot a <see cref="SimitoneSurroundingsPlan"/> will stand
        /// up as a real <c>SubWorldComponent</c> is not also a facade. That is the whole of "one
        /// rendered decision per lot" on the facade side of it - the sub-world side is
        /// <see cref="NeighbourhoodRenderDecision"/>, which says the same thing about the same lot
        /// from the other direction. Two answers to one question is how a neighbour ends up drawn
        /// twice: once as a wall mesh, and once as a real lot standing in the middle of it.
        /// </para>
        /// <para>
        /// <b>Where the ground rule lives, and why it is not here.</b>
        /// <see cref="SimitoneSurroundingsPlacement.GroundIsDrawnUnder"/> is part of
        /// <see cref="SimitoneSurroundingsCull.CoversLot"/>, and the cache's layout is built from
        /// <see cref="SimitoneSurroundingsCull.LotsInRange"/> - so a lot with no ground under it is
        /// not in the layout this method is asked about and never reaches the build queue. Adding
        /// the same test here would be a second spelling of a rule one region already owns, and
        /// this method has no map to ask: the ground is a property of the raster, and a facade pass
        /// that took a lot by number would have nothing to check it against.
        /// </para>
        /// <para>
        /// A null or empty <paramref name="subWorldLots"/> changes nothing here, which is what keeps
        /// the capability-off behaviour identical: the map view passes nothing and gets the list it
        /// always got.
        /// </para>
        /// </remarks>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="subWorldLots">
        /// The lot numbers built as real sub-worlds, or <c>null</c> for none.
        /// </param>
        public static bool ShouldDraw(
            NeighbourhoodLot lot, int? activeLot, IReadOnlyCollection<short> subWorldLots = null)
        {
            if (lot == null) return false;
            if (!lot.HasHouse) return false;
            if (activeLot.HasValue && lot.Number == activeLot.Value) return false;
            //B2's fourth exclusion: a lot the plan will stand up as a real SubWorldComponent
            //is not also a facade. A null or empty set changes nothing here, which is what
            //keeps the capability-off behaviour identical: the map view has no sub-worlds and
            //passes none.
            if (NeighbourhoodRenderDecision.IsSubWorld(subWorldLots, lot.Number)) return false;
            return true;
        }

        /// <summary>
        /// Whether a lot's mesh has to be built, given what is already cached.
        /// </summary>
        /// <remarks>
        /// The key is the whole of the cache policy in one comparison, and it is deliberately the
        /// <em>house file's</em> fingerprint and not the lot number or its size: a lot's furniture
        /// changing must not cost a re-mesh of its walls, and a lot's walls changing must not be
        /// drawn with the mesh it had before. A hash of zero means "no cache", which is also what a
        /// bulldozed lot reports - and a bulldozed lot is already excluded by
        /// <see cref="ShouldDraw"/>, so the two rules cannot disagree.
        /// </remarks>
        /// <param name="lot">The lot.</param>
        /// <param name="cachedHashes">The hash each cached facade was built from.</param>
        public static bool NeedsBuild(NeighbourhoodLot lot, IReadOnlyDictionary<short, uint> cachedHashes)
        {
            if (lot == null) return false;
            if (cachedHashes == null) return true;
            uint built;
            if (!cachedHashes.TryGetValue(lot.Number, out built)) return true;
            return built != lot.HouseHash;
        }

        /// <summary>
        /// How many facades are worth building in a whole neighbourhood, ignoring the budget and
        /// what is already cached: the denominator for a progress line, and the number that turns
        /// "0 lot facades" from a mystery into a ratio.
        /// </summary>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="subWorldLots">
        /// The lot numbers built as real sub-worlds, or <c>null</c> for none, so the
        /// denominator is the number of facades this pass will ever attempt and not the
        /// number of lots in the neighbourhood.
        /// </param>
        public static int Eligible(NeighbourhoodLayout layout, int? activeLot,
                IReadOnlyCollection<short> subWorldLots = null)
        {
            if (layout == null) return 0;
            var total = 0;
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                if (ShouldDraw(layout.Lots[i], activeLot, subWorldLots)) total++;
            }
            return total;
        }

        /// <summary>
        /// How many lots are still owed a mesh, and how many the pass will ever attempt. Both
        /// numbers in one call, because a caller that has to ask twice walks the layout twice and
        /// the two answers can straddle a build that happened in between.
        /// </summary>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="cachedHashes">The hash each already-cached facade was built from.</param>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="focus">The world tile the camera is looking at.</param>
        /// <param name="outstanding">How many lots still need a mesh.</param>
        /// <param name="eligible">How many lots a complete pass will ever attempt.</param>
        /// <param name="subWorldLots">
        /// The lot numbers built as real sub-worlds, or <c>null</c> for none. Both numbers
        /// exclude them, so "still to build" never counts a lot that is deliberately not being
        /// built as a facade.
        /// </param>
        public static void Progress(
            NeighbourhoodLayout layout, IReadOnlyDictionary<short, uint> cachedHashes,
            int? activeLot, Vector2 focus, out int outstanding, out int eligible,
                IReadOnlyCollection<short> subWorldLots = null)
        {
            outstanding = ToBuild(layout, cachedHashes, activeLot, focus, int.MaxValue, 0f,
                subWorldLots).Count;
            eligible = Eligible(layout, activeLot, subWorldLots);
        }

        /// <summary>
        /// How many further meshes this frame may start, given the count budget, a wall-clock
        /// budget, and how much has already been built this frame.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The count alone is not enough. A facade build's cost is the lot's, not ours: a plain
        /// house is quick and a house full of furniture is not, and on a slow machine two of them
        /// in a frame is a visible hitch. So the caller passes both a count and a millisecond
        /// budget and takes whichever runs out first.
        /// </para>
        /// <para>
        /// The millisecond budget is checked <em>before</em> each build rather than after, so a
        /// single build is never abandoned half-way: a half-built facade is exactly the "partial
        /// mesh drawn" the view is not allowed to show. The first build of a frame is always
        /// allowed even if the previous frame overran, because a queue that can never advance is
        /// indistinguishable from a queue that is broken - which is the bug this replaced.
        /// </para>
        /// </remarks>
        /// <param name="countBudget">How many meshes a frame may build at most.</param>
        /// <param name="msBudget">
        /// How long a frame may spend building, in milliseconds; zero or less means no wall-clock
        /// limit.
        /// </param>
        /// <param name="builtSoFar">How many meshes this frame has already started.</param>
        /// <param name="elapsedMs">How long this frame has already spent building.</param>
        public static int FrameAllowance(int countBudget, double msBudget, int builtSoFar, double elapsedMs)
        {
            var left = countBudget - Math.Max(0, builtSoFar);
            if (left <= 0) return 0;
            if (msBudget <= 0d) return left;
            //the first build of a frame is unconditional, so one slow lot cannot wedge the queue
            if (builtSoFar > 0 && elapsedMs >= msBudget) return 0;
            return left;
        }

        /// <summary>
        /// The lots whose cached mesh is out of date: the ones whose hash has moved, and the ones
        /// the layout no longer mentions at all - a neighbourhood edited under the cache, where a
        /// lot that has been bulldozed away is as stale as one whose file changed.
        /// </summary>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="cachedHashes">The hash each cached facade was built from.</param>
        public static IReadOnlyList<short> Stale(NeighbourhoodLayout layout, IReadOnlyDictionary<short, uint> cachedHashes)
        {
            var stale = new List<short>();
            if (layout == null || cachedHashes == null) return stale;
            foreach (var pair in cachedHashes)
            {
                var lot = layout.FindLot(pair.Key);
                if (lot == null || lot.HouseHash != pair.Value) stale.Add(pair.Key);
            }
            stale.Sort();
            return stale;
        }
    }

    /// <summary>
    /// What one neighbouring lot is drawn as. B2 task 5's "one rendered decision per lot": a
    /// neighbouring lot is either a real <c>SubWorldComponent</c> or a flat facade mesh, never
    /// both, and this is the answer that says which.
    /// </summary>
    /// <remarks>
    /// Three values rather than a bool because the third one is a real outcome and not a failure:
    /// the lot the player is standing in is drawn by the lot view, and a lot outside the drawn
    /// region is drawn by neither. A bool pair would have to invent a name for "not mine to draw".
    /// </remarks>
    public enum NeighbourhoodLotRender
    {
        /// <summary>Neither: the player's own lot, an empty plot, or one outside the region.</summary>
        None = 0,

        /// <summary>
        /// A flat <c>LotFacadeGenerator</c> mesh - walls, roof and floor, no contents, no objects.
        /// </summary>
        Facade = 1,

        /// <summary>A real neighbouring lot, stood up as a sub-world of the one being played.</summary>
        SubWorld = 2,
    }

    /// <summary>
    /// The one per-lot answer to "what is this neighbour drawn as", as a pure function of four
    /// facts. B2 task 5.
    ///
    /// <para><b>The rule, in one sentence.</b> The lot the player is standing in is drawn by neither;
    /// a lot the <see cref="SimitoneSurroundingsPlan"/> will stand up as a sub-world is drawn as a
    /// sub-world and <em>not</em> as a facade; anything else inside the region that has a
    /// house on it is a facade; anything else is neither.</para>
    ///
    /// <para><b>Why it is one function and not two.</b> Part A answers "facade or nothing" in
    /// <see cref="NeighbourhoodFacadePlan.ShouldDraw"/>; B2 answers "sub-world or nothing" in
    /// <see cref="SimitoneSurroundingsPlan"/>. Left as they were, a lot both answers yes to is
    /// drawn twice - once as a wall mesh standing at the right height, and once as a real lot with
    /// its own ground, its own objects and its own wall, occupying the same plot. From inside the
    /// active lot that reads as a neighbour drawn over itself, with the two copies' walls fighting
    /// for the depth buffer. So the two halves now ask one question, and the facade cache is told
    /// the plan's lot numbers rather than working them out for itself.</para>
    ///
    /// <para><b>Why it is pure.</b> The decision is the thing that cannot be eyeballed: it needs no
    /// graphics device, no <c>VM</c>, no lot store and no GPU mesh, and every failure it prevents
    /// is a picture rather than a number. It is four comparisons over a lot, so it is asserted in
    /// <c>NeighbourhoodLotRenderDecisionTests</c> rather than reviewed.</para>
    ///
    /// <para><b>The order of the tests is the rule.</b> The player's own lot is refused first,
    /// before the sub-world set is consulted, so the answer does not depend on the plan happening
    /// to have excluded it (<see cref="SimitoneSurroundingsPlan"/> does, and it should - but the
    /// lot view is drawing that lot in full either way, and a second copy of it standing in the
    /// middle of the property is the exact failure this file exists to prevent). The sub-world set
    /// is consulted next, before the cut, so a real lot is never downgraded to a facade just
    /// because its plot also falls inside the region - which it always does.</para>
    /// </summary>
    public static class NeighbourhoodRenderDecision
    {
        /// <summary>
        /// The whole rule, with nothing in it but the four facts it is made of.
        /// </summary>
        /// <param name="hasHouse">Whether the lot has a house file on it.</param>
        /// <param name="isActiveLot">Whether this is the lot the player is standing in.</param>
        /// <param name="isPlannedSubWorld">
        /// Whether a <see cref="SimitoneSurroundingsPlan"/> will stand this lot up as a sub-world.
        /// </param>
        /// <param name="insideCut">
        /// Whether the lot falls inside the region the surround draws - the rectangle
        /// <see cref="SimitoneSurroundingsCull.LotsInRange"/> produces the facade queue from.
        /// </param>
        public static NeighbourhoodLotRender Decide(
            bool hasHouse, bool isActiveLot, bool isPlannedSubWorld, bool insideCut)
        {
            //The lot the player is standing in, first and unconditionally: the lot view is drawing
            //it in full, from its own house file, right now.
            if (isActiveLot) return NeighbourhoodLotRender.None;
            //A real lot next. Before the cut and before HasHouse, because a sub-world is a whole
            //lot - its own ground, roads and contents - rather than a wall mesh, so a bulldozed
            //plot can be one and an empty plot that is inside the region cannot.
            if (isPlannedSubWorld) return NeighbourhoodLotRender.SubWorld;
            //The far neighbours. Inside the region and with something on them, a facade is the
            //whole of what the player sees of them, and it is the only thing that is affordable at
            //that distance.
            if (!hasHouse) return NeighbourhoodLotRender.None;
            return insideCut
                ? NeighbourhoodLotRender.Facade
                : NeighbourhoodLotRender.None;
        }

        /// <summary>
        /// The rule for a lot the layout knows, against a set of lot numbers built as sub-worlds.
        /// </summary>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="subWorldLots">
        /// The lot numbers a plan will build as sub-worlds, or <c>null</c> for none.
        /// </param>
        /// <param name="insideCut">Whether the lot falls inside the region, on drawn ground.</param>
        public static NeighbourhoodLotRender Decide(
            NeighbourhoodLot lot, int? activeLot,
            IReadOnlyCollection<short> subWorldLots, bool insideCut)
        {
            if (lot == null) return NeighbourhoodLotRender.None;
            var isActive = activeLot.HasValue && lot.Number == activeLot.Value;
            return Decide(lot.HasHouse, isActive, IsSubWorld(subWorldLots, lot.Number), insideCut);
        }

        /// <summary>
        /// The rule for a lot of a layout, against a plan and the region - the one call a
        /// caller makes when it has both halves rather than one of them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The plan and the cut are taken together on purpose. They are the two numbers that used
        /// to be read independently - a radius in <c>TS1GameScreen</c> and a rectangle in
        /// <see cref="SimitoneSurroundingsCull"/> - and a lot is a sub-world exactly when the first
        /// selects it and a facade exactly when the second does. Passing them in one call makes it
        /// impossible to ask the question with one and draw against the other.
        /// </para>
        /// <para>
        /// A null <paramref name="cull"/> means no cut at all, which is the map view's situation:
        /// it draws the whole neighbourhood and has no sub-worlds, and it passes no plan either.
        /// A null or invalid <paramref name="plan"/> means no sub-worlds, which is the same thing
        /// the capability-off surround gets - every lot in the cut is a facade, byte for byte what
        /// it was before this type existed.
        /// </para>
        /// </remarks>
        /// </summary>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="plan">The neighbour plan, or <c>null</c> for none.</param>
        /// <param name="cull">The region, or <c>null</c> for "no cut".</param>
        public static NeighbourhoodLotRender Decide(
            NeighbourhoodLot lot, int? activeLot,
            SimitoneSurroundingsPlan plan, SimitoneSurroundingsCull? cull)
        {
            if (lot == null) return NeighbourhoodLotRender.None;
            var isActive = activeLot.HasValue && lot.Number == activeLot.Value;
            return Decide(lot.HasHouse, isActive,
                IsSubWorld(plan, lot.Number), InsideCut(lot, cull));
        }

        /// <summary>Whether a lot number is one the plan will build as a sub-world.</summary>
        /// <param name="plan">The plan, or <c>null</c>.</param>
        /// <param name="lotNumber">The lot number.</param>
        public static bool IsSubWorld(SimitoneSurroundingsPlan plan, short lotNumber)
        {
            //Only a plan that is actually usable counts. An invalid plan has no active lot and its
            //neighbour list is empty, and reading that as "no sub-worlds" is what keeps a failed
            //plan from silently blanking the neighbourhood.
            if (plan == null || !plan.IsValid) return false;
            return plan.Contains(lotNumber);
        }

        /// <summary>Whether a lot number is in a set of lot numbers built as sub-worlds.</summary>
        /// <param name="subWorldLots">The set, or <c>null</c> for none.</param>
        /// <param name="lotNumber">The lot number.</param>
        public static bool IsSubWorld(IReadOnlyCollection<short> subWorldLots, short lotNumber)
        {
            if (subWorldLots == null || subWorldLots.Count == 0) return false;
            foreach (var number in subWorldLots)
            {
                if (number == lotNumber) return true;
            }
            return false;
        }

        /// <summary>
        /// Whether a lot falls inside the region the surround draws.
        ///
        /// <remarks>
        /// The one call both halves make for this, and it is
        /// <see cref="SimitoneSurroundingsCull.CoversLot"/> - the same test
        /// <see cref="SimitoneSurroundingsCull.LotsInRange"/> makes when it builds the layout the
        /// facade cache is handed. Spelling it twice is how the queue and the decision end up
        /// disagreeing about which lots exist; asking the region once is the fix.
        ///
        /// <para>
        /// <b>CHANGED: "in the cut" now also means "on drawn ground".</b>
        /// <see cref="SimitoneSurroundingsCull.CoversLot"/> carries
        /// <see cref="SimitoneSurroundingsPlacement.GroundIsDrawnUnder"/>, so a lot in the island's
        /// cut corner is not inside the cut - it is drawn by neither a facade nor a sub-world,
        /// because the mesh emits no ground under it and §9.1's floating house is worse than a
        /// missing one that the log names. The test file for this decision still stands a plan and a
        /// region up against each other, so the two sides cannot drift apart on it.
        /// </para>
        ///
        /// <para>
        /// A null <paramref name="cull"/> is "no cut", not "nothing is in it": the map view has no
        /// drawn region because it draws the whole neighbourhood, and a lot there is in every
        /// sense that matters - and for it the ground rule is enforced a layer down, by
        /// <see cref="NeighbourhoodFacades"/> taking those lots out of the layout it builds for
        /// rather than by having nowhere to ask here.
        /// </para>
        /// </remarks>
        /// </summary>
        /// <param name="lot">The lot, or <c>null</c>.</param>
        /// <param name="cull">The region, or <c>null</c> for "no cut".</param>
        public static bool InsideCut(NeighbourhoodLot lot, SimitoneSurroundingsCull? cull)
        {
            if (lot == null) return false;
            if (!cull.HasValue) return true;
            if (!cull.Value.IsValid) return false;
            return cull.Value.CoversLot(lot);
        }

        /// <summary>Whether the decision means "build and draw a facade mesh for this lot".</summary>
        /// <param name="render">The decision.</param>
        public static bool IsFacade(NeighbourhoodLotRender render)
        {
            return render == NeighbourhoodLotRender.Facade;
        }

        /// <summary>Whether the decision means "this lot is a real sub-world".</summary>
        /// <param name="render">The decision.</param>
        public static bool IsSubWorld(NeighbourhoodLotRender render)
        {
            return render == NeighbourhoodLotRender.SubWorld;
        }

        /// <summary>
        /// Whether every lot the plan will build as a sub-world is inside the facade cut, so
        /// withholding a facade from it cannot open a hole in the street.
        ///
        /// <remarks>
        /// The property that lets the two radii be different numbers and still not disagree. The
        /// plan's radius (<see cref="SimitoneSurroundingsPlan.DefaultSubWorldRadiusTiles"/>) is a
        /// budget of whole lots stood up, and the cull's
        /// (<see cref="SimitoneSurroundingsPlacement.CullRadiusTiles"/>) is how much ground is
        /// drawn; the sub-world set has to be the smaller of the two, or a neighbour would be
        /// built as a real lot somewhere the surround is not drawing. This is the assertion, and
        /// it is pure, so it is asked of a plan and a region rather than looked for on screen.
        /// </remarks>
        /// </summary>
        /// <param name="plan">The plan, or <c>null</c>.</param>
        /// <param name="cull">The region, or <c>null</c> for "no cut".</param>
        public static bool SubWorldsAreInsideCut(SimitoneSurroundingsPlan plan, SimitoneSurroundingsCull? cull)
        {
            if (plan == null || !plan.IsValid || !cull.HasValue) return true;
            for (int i = 0; i < plan.Neighbours.Count; i++)
            {
                if (!InsideCut(plan.Neighbours[i].Lot, cull)) return false;
            }
            return true;
        }

        /// <summary>
        /// Whether the plan's radius and the region's radius are the same number, which is
        /// what makes the whole region eligible to be real rather than a real centre with a
        /// ring of facades round it.
        /// </summary>
        /// <remarks>
        /// The stronger half of <see cref="SimitoneSurroundingsPlan.RadiusFitsCut"/>, asked of a plan
        /// and a region together for the same reason that one is: the two radii are the two numbers
        /// the surround and the lot screen each had a copy of, and the copy in the screen was the
        /// narrower one. A test that only asserted <c>RadiusFitsCut</c> would still pass on a
        /// screen asking for eight tiles against a forty-tile region, which is the arrangement this
        /// exists to end.
        /// </remarks>
        /// <param name="plan">The plan, or <c>null</c>.</param>
        /// <param name="cull">The region, or <c>null</c> for "no cut".</param>
        public static bool RadiiAreShared(SimitoneSurroundingsPlan plan, SimitoneSurroundingsCull? cull)
        {
            if (plan == null || !plan.IsValid || !cull.HasValue) return false;
            return plan.RadiusSharedWith(cull);
        }

        /// <summary>
        /// How many lots inside the region are facades rather than real sub-worlds, over the
        /// whole of a layout. Zero means the region is fully real.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the count a player means when they say "some of these houses aren't real".</b>
        /// It is not the budget's deferred count, because a flat lot in view has four possible
        /// causes and each needs a different answer: the plan's radius was narrower than the region
        /// region (a radius to fix), the sub-world budget was exhausted (a count to fix), the restore
        /// refused and nothing was stood up (a lot-load problem), or the capability is off (nothing
        /// to fix). Asking this over the layout names the first two and lets the caller infer the
        /// others from what it already knows.
        /// </para>
        /// <para>
        /// Every lot goes through <see cref="Decide(NeighbourhoodLot, int?, SimitoneSurroundingsPlan, SimitoneSurroundingsCull?)"/>
        /// rather than through the plan's own lists, so the number is the render decision's and not
        /// a second opinion about it - and it is asked before the facade queue has drained, so a
        /// surround can report "the region is real, and three of those facades are still building"
        /// rather than conflating the two.
        /// </para>
        /// <para>
        /// A null <paramref name="cull"/> is "no cut", which is the map view: there is no drawn
        /// region, so the count is over the whole layout - and it is asked of a null plan there too,
        /// which is the honest answer for a view that has no sub-worlds.
        /// </para>
        /// </remarks>
        /// <param name="layout">The packed layout, or <c>null</c> for none.</param>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="plan">The neighbour plan, or <c>null</c> for none.</param>
        /// <param name="cull">The region, or <c>null</c> for "no cut".</param>
        public static int FacadeFallbackCount(
            NeighbourhoodLayout layout, int? activeLot,
            SimitoneSurroundingsPlan plan, SimitoneSurroundingsCull? cull)
        {
            if (layout == null) return 0;
            var count = 0;
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                if (IsFacade(Decide(layout.Lots[i], activeLot, plan, cull))) count++;
            }
            return count;
        }

        /// <summary>
        /// Whether every lot inside the region is a real sub-world, asked over a whole layout:
        /// no lot in view is a facade for any reason.
        /// </summary>
        /// <remarks>
        /// The same question <see cref="FacadeFallbackCount"/> answers, as a flag, because the two
        /// are wanted in different places: the surround logs the count and a caller deciding whether
        /// the street is fully real wants the boolean. It is deliberately wider than
        /// <see cref="SimitoneSurroundingsPlan.IsFullyCovered"/>, which is about the budget alone -
        /// this is about the whole decision, so a lot the radius never reached counts against it here
        /// and not there.
        /// </remarks>
        /// <param name="layout">The packed layout, or <c>null</c> for none.</param>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="plan">The neighbour plan, or <c>null</c> for none.</param>
        /// <param name="cull">The region, or <c>null</c> for "no cut".</param>
        public static bool RegionIsFullyReal(
            NeighbourhoodLayout layout, int? activeLot,
            SimitoneSurroundingsPlan plan, SimitoneSurroundingsCull? cull)
        {
            return FacadeFallbackCount(layout, activeLot, plan, cull) == 0;
        }

        /// <summary>
        /// One line naming a decision, for a log: what a lot is drawn as and, where it is not the
        /// obvious answer, why.
        /// </summary>
        /// <param name="render">The decision.</param>
        public static string Describe(NeighbourhoodLotRender render)
        {
            switch (render)
            {
                case NeighbourhoodLotRender.Facade: return "facade";
                case NeighbourhoodLotRender.SubWorld: return "sub-world";
                default: return "not drawn";
            }
        }
    }

    /// <summary>
    /// One of the stages a facade build is made of, named the way the log names it.
    /// </summary>
    /// <remarks>
    /// An enum rather than a free string because the stages are what the timing line is grouped
    /// by, and a typo in a stage name is a stage that never appears in the total - a line that
    /// silently adds up to less than the build actually cost.
    /// </remarks>
    public enum NeighbourhoodFacadeStage
    {
        /// <summary>Nothing was timed.</summary>
        None = 0,

        /// <summary>Reading and parsing the lot's <c>HouseNN.iff</c>.</summary>
        HouseFile,

        /// <summary>Standing the throwaway <c>World</c> and <c>VM</c> up.</summary>
        World,

        /// <summary>
        /// <c>VMTS1Activator.LoadFromIff</c>: the whole house file onto a live VM. This is the
        /// stage that loads every object in the lot, runs its entry point, and rebuilds the lot's
        /// room map.
        /// </summary>
        Load,

        /// <summary>Forcing the time of day and preparing the throwaway lot's lighting.</summary>
        Lighting,

        /// <summary><c>LotFacadeGenerator.GenerateWalls</c>.</summary>
        Walls,

        /// <summary><c>LotFacadeGenerator.GenerateFloor</c>.</summary>
        Floor,

        /// <summary><c>LotFacadeGenerator.GenerateRoof</c>.</summary>
        Roof,

        /// <summary>Placing the three surfaces into the generated world and uploading them.</summary>
        Assemble
    }

    /// <summary>
    /// How long each stage of one lot's facade build took, in milliseconds.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this exists.</b> The pass was budgeted per frame and still froze the game for
    /// eleven seconds, and the log could not say why: it recorded <em>that</em> a lot had been
    /// built and nothing about what building it cost. The budget was a number nobody had
    /// measured, checked before a build it could not bound, and applied to a cost that turned out
    /// to be two orders of magnitude above it. This is the measurement, taken per stage, so that
    /// the next run says which of <see cref="NeighbourhoodFacadeStage.Load"/>,
    /// <see cref="NeighbourhoodFacadeStage.Walls"/>, <see cref="NeighbourhoodFacadeStage.Floor"/>
    /// and <see cref="NeighbourhoodFacadeStage.Roof"/> is the five seconds.</para>
    ///
    /// <para><b>Pure, and the formatting is the point.</b> The numbers come from a stopwatch on
    /// the game thread, but turning them into the line a person reads is arithmetic over ten
    /// doubles, and that is testable without a graphics device. A stage nobody can see in the log
    /// is a stage nobody will go and look at.</para>
    /// </remarks>
    public readonly struct NeighbourhoodFacadeTimings
    {
        private readonly double[] _stages;
        private readonly double _totalMs;

        /// <summary>How many stages there are to time.</summary>
        public const int StageCount = 8;

        /// <summary>
        /// The slot in a stage array that a stage's duration belongs in, or -1 for a stage that is
        /// not timed.
        /// </summary>
        /// <remarks>
        /// <para><b>This is the whole of the mapping, and it exists because getting it wrong by one
        /// is invisible until it is fatal.</b> The array has <see cref="StageCount"/> slots for
        /// eight stages whose enum values are 1..8, so a stage's value is one past its slot. An
        /// earlier version indexed the array with the stage's own value: seven stages fitted, the
        /// eighth - <see cref="NeighbourhoodFacadeStage.Assemble"/>, charged the moment the facade
        /// had been built and placed - wrote to slot 8 of an eight-slot array, and threw an
        /// <c>IndexOutOfRangeException</c> that the pipeline reported as "while placing the lot's
        /// facade into the generated world". The mesh was already on the GPU by then. Every lot in
        /// the neighbourhood was built, paid for, and thrown away by its own bookkeeping.</para>
        /// <para>The same off-by-one was in the reader, so the line in the log attributed every
        /// stage's cost to the stage after it: a 1.58 second wall pass was printed as
        /// "floor 1.58s", and the stage that actually cost it was never printed at all.</para>
        /// <para>Pure, and therefore assertable without a graphics device: it is arithmetic on an
        /// enum.</para>
        /// </remarks>
        /// <param name="stage">The stage.</param>
        public static int SlotFor(NeighbourhoodFacadeStage stage)
        {
            var slot = (int)stage - 1;
            if (slot < 0 || slot >= StageCount) return -1;
            return slot;
        }

        /// <summary>
        /// A fresh stage array: one zeroed slot per real stage, in pipeline order.
        /// </summary>
        public static double[] NewStages()
        {
            return new double[StageCount];
        }

        /// <summary>
        /// Charges a stage its milliseconds, into the slot <see cref="SlotFor"/> names for it.
        /// </summary>
        /// <remarks>
        /// Returns <c>false</c> - and charges nothing - for a stage with no slot, rather than
        /// indexing past the end. A stopwatch that cannot record one stage is a line that is one
        /// stage short; a stopwatch that throws takes the whole facade with it, which is the bug
        /// this method exists to make impossible to write again.
        /// </remarks>
        /// <param name="stages">The array from <see cref="NewStages"/>, or any stage array.</param>
        /// <param name="stage">The stage to charge.</param>
        /// <param name="milliseconds">How long it took.</param>
        /// <returns>Whether the charge was recorded.</returns>
        public static bool TryCharge(double[] stages, NeighbourhoodFacadeStage stage, double milliseconds)
        {
            if (stages == null) return false;
            var slot = SlotFor(stage);
            if (slot < 0 || slot >= stages.Length) return false;
            stages[slot] += milliseconds;
            return true;
        }

        /// <summary>Timings for a build that never happened.</summary>
        public static readonly NeighbourhoodFacadeTimings None =
            new NeighbourhoodFacadeTimings(new double[StageCount]);

        public NeighbourhoodFacadeTimings(double[] stages)
            : this(stages, 0d)
        {
        }

        public NeighbourhoodFacadeTimings(double[] stages, double totalMs)
        {
            if (stages == null || stages.Length != StageCount)
            {
                _stages = new double[StageCount];
                _totalMs = totalMs;
                return;
            }
            _stages = stages;
            _totalMs = totalMs;
        }

        /// <summary>How long one stage took, in milliseconds.</summary>
        /// <param name="stage">The stage.</param>
        public double this[NeighbourhoodFacadeStage stage]
        {
            get
            {
                var slot = SlotFor(stage);
                if (_stages == null || slot < 0 || slot >= _stages.Length) return 0d;
                return _stages[slot];
            }
        }

        /// <summary>
        /// One slot's value, or zero for a timings whose array is absent or the wrong length.
        /// </summary>
        /// <remarks>
        /// The log line is written after a build has already failed, from a struct that may have
        /// been default-constructed or deserialised with no stages in it, and a line that throws
        /// is a line that never reaches the person who needed it.
        /// </remarks>
        private double Slot(int slot)
        {
            if (_stages == null || slot < 0 || slot >= _stages.Length) return 0d;
            return _stages[slot];
        }

        /// <summary>Every stage, in pipeline order, for a caller that wants to walk them.</summary>
        public IReadOnlyList<NeighbourhoodFacadeStage> Stages
        {
            get
            {
                var stages = new NeighbourhoodFacadeStage[StageCount];
                for (int i = 0; i < StageCount; i++) stages[i] = (NeighbourhoodFacadeStage)(i + 1);
                return stages;
            }
        }

        /// <summary>Everything the build cost, which is more than the sum of the stages.</summary>
        /// <remarks>
        /// Strictly more: the world is stood up and torn down outside every stage, and a stage
        /// that threw is only charged for the part that ran. The difference is small when the
        /// build is fast and is the whole story when it is not, so it is measured rather than
        /// assumed.
        /// </remarks>
        public double TotalMs
        {
            get { return _totalMs; }
        }

        /// <summary>Whether any stage was timed at all.</summary>
        public bool Measured
        {
            get { return TotalMs > 0d; }
        }

        /// <summary>
        /// The one line, per lot, that says where the time went - and, when one stage dominates,
        /// says so in as many words.
        /// </summary>
        /// <remarks>
        /// Every stage is listed even at 0ms. A line reading "load 4120ms, walls 0ms" would send
        /// the next reader looking for why the walls cost nothing, and the answer - the pass threw
        /// before the walls - is on the failure line already and does not need to be guessed at
        /// here.
        /// </remarks>
        /// <param name="lotNumber">The lot the build was for.</param>
        public string Describe(int lotNumber)
        {
            var parts = new string[StageCount];
            for (int i = 0; i < StageCount; i++)
            {
                //slot i holds the stage whose value is i + 1. One number, and the two halves of
                //this line cannot disagree: a stage is printed with its own cost or not at all.
                var stage = (NeighbourhoodFacadeStage)(i + 1);
                parts[i] = DescribeStage(stage) + " " + Milliseconds(Slot(SlotFor(stage)));
            }
            return "lot " + lotNumber + " facade: " + Milliseconds(TotalMs) + " total ("
                + string.Join(", ", parts) + ")";
        }

        /// <summary>The stage that cost the most, or <see cref="NeighbourhoodFacadeStage.None"/>
        /// when nothing was measured or everything tied at zero.</summary>
        public NeighbourhoodFacadeStage Dominant
        {
            get
            {
                if (_stages == null) return NeighbourhoodFacadeStage.None;
                var best = NeighbourhoodFacadeStage.None;
                var worst = 0d;
                for (int i = 0; i < StageCount; i++)
                {
                    var cost = Slot(i);
                    if (cost > worst)
                    {
                        worst = cost;
                        best = (NeighbourhoodFacadeStage)(i + 1);
                    }
                }
                return best;
            }
        }

        /// <summary>
        /// One stage's name as the log says it: the phrase a person would use, not the enum name.
        /// </summary>
        /// <param name="stage">The stage.</param>
        public static string DescribeStage(NeighbourhoodFacadeStage stage)
        {
            switch (stage)
            {
                case NeighbourhoodFacadeStage.HouseFile: return "read house";
                case NeighbourhoodFacadeStage.World: return "stand up world";
                case NeighbourhoodFacadeStage.Load: return "load house into VM";
                case NeighbourhoodFacadeStage.Lighting: return "lighting";
                case NeighbourhoodFacadeStage.Walls: return "walls";
                case NeighbourhoodFacadeStage.Floor: return "floor";
                case NeighbourhoodFacadeStage.Roof: return "roof";
                case NeighbourhoodFacadeStage.Assemble: return "assemble";
                default: return "none";
            }
        }

        /// <summary>
        /// A duration in the units a log line should use: whole milliseconds below a second, and
        /// seconds above it, because 4900ms is read as a number and skipped and 4.9s is read as a
        /// freeze.
        /// </summary>
        /// <remarks>
        /// <para><b>Invariant culture, deliberately.</b> This string goes into a log file that a
        /// player attaches to a bug report, and the machine that wrote it is not the machine that
        /// reads it. A formatted 0.25 that comes out as "0,25s" on a machine whose locale uses a
        /// comma is not a log line anybody can act on - and it is not a hypothetical, because the
        /// first run of this wrote exactly that.</para>
        /// </remarks>
        /// <param name="ms">The duration, in milliseconds.</param>
        public static string Milliseconds(double ms)
        {
            if (ms <= 0d) return "0ms";
            if (double.IsNaN(ms) || double.IsInfinity(ms)) return "?ms";
            if (ms < 1000d) return ((long)Math.Round(ms)).ToString(CultureInfo.InvariantCulture) + "ms";
            if (ms < 10000d)
            {
                return (ms / 1000d).ToString("0.00", CultureInfo.InvariantCulture) + "s";
            }
            return (ms / 1000d).ToString("0.0", CultureInfo.InvariantCulture) + "s";
        }
    }
}
