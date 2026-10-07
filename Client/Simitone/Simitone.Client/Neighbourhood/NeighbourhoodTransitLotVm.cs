/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using FSO.Content;
using FSO.Content.Model;
using FSO.LotView;
using FSO.LotView.Components;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Engine.TSOTransaction;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Marshals;
using FSO.SimAntics.Model;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.NetPlay.Drivers;
using FSO.SimAntics.Utils;
using Microsoft.Xna.Framework;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    // ================================================================================================
    // Part B, B4, task 1: "introduce a transit lot - a World + VM whose blueprint is the generated
    // street region around the hop, holding the travelling avatar."
    //
    // NeighbourhoodTransitLot.cs is the pure half of that: the route, the progress along it, and the
    // exactly-one-active-lot predicate. This file is the half that needs a graphics device, a
    // content manager and a VM - and it is split the same way one step further, because the half
    // that can be *decided* without any of those is most of the interesting arithmetic:
    //
    //   TransitStreetRegion   which generated map tiles the transit blueprint covers
    //   TransitStreetTerrain  the 4x4 height grid and the TSO road byte for that region
    //   TransitBlueprintPlan  the immutable blueprint decision (size, base altitude, placement)
    //   TransitHandover       the decision about what happens when a walk ends
    //   ITransitAvatarBinder  the seam that moves the travelling avatar between VMs
    //   NeighbourhoodTransitLotVm  the game-thread half that builds and walks the lot
    //
    // The four decision types have no engine dependency at all and are what the tests in
    // Simitone.Tests assert. The last two are the parts that cannot be asserted without a device
    // and a TS1 install, and the type remarks on each say exactly which.
    // ================================================================================================

    /// <summary>
    /// The square of generated map a transit lot's blueprint is built over: the tiles a walk
    /// touches, a margin past them, clipped to the map.
    ///
    /// <para>
    /// <b>Why a region and not the whole map.</b> A <c>Blueprint</c> is an architecture with a
    /// tile count in it, a room map, a terrain height field and a lighting batch, all of them
    /// sized by that count. A 512x512 generated map is a quarter of a million tiles; building a
    /// transit lot over all of it would allocate a lot's worth of architecture to hold a pavement.
    /// So the region is the walk's own bounding box plus a margin, which for the commonest walk -
    /// two doors either side of a house - is a lot-sized square rather than a map-sized one.
    /// </para>
    ///
    /// <para>
    /// <b>Square, not the box.</b> A <c>Blueprint</c> is <c>Blueprint(w, h)</c> but the TSO terrain
    /// helpers, the architecture and the landmark row all assume one side
    /// (<c>RestoreRoad</c> lays a strip at <c>x = 1</c>, <c>PositionLandmarkObjects</c> reads
    /// <c>arch.Width - 9</c> and <c>arch.Height - 7</c>), and <see cref="Side"/> is the larger of
    /// the two dimensions so the box always fits. The region is not centred inside that square -
    /// it sits in its top-left corner, so <see cref="LocalX"/>/<see cref="LocalZ"/> are plain
    /// subtractions and a waypoint's local tile never moves because the region was made square.
    /// </para>
    ///
    /// <para>
    /// <b>Pure, and the point of it.</b> No map is held and no map is read: the region is a decision
    /// about coordinates, so it can be written down on paper and asserted in a test with no
    /// generated neighbourhood and no device.
    /// </para>
    /// </summary>
    public readonly struct TransitStreetRegion
    {
        /// <summary>
        /// How many tiles of generated map are kept past the walk on every side, which is what
        /// stops the avatar reaching the edge of the blueprint on a turn.
        /// </summary>
        /// <remarks>
        /// Eight rather than <see cref="SimitoneSurroundingsPlacement.CullRadiusTiles"/>'s forty on
        /// purpose: that radius is a *rendering* horizon ("how far can a player see down the
        /// street"), and a blueprint is a simulation footprint, not a view. It also has to be at
        /// least <see cref="SimitoneSurroundingsPlan.MinSubWorldSize"/> wide, which
        /// <see cref="Side"/> enforces by rounding the square up rather than by widening the
        /// margin.
        /// </remarks>
        public const int DefaultMarginTiles = 8;

        private static readonly byte[] NoHeights = new byte[0];

        private TransitStreetRegion(int minX, int minZ, int maxX, int maxZ)
        {
            MinX = minX;
            MinZ = minZ;
            MaxX = maxX;
            MaxZ = maxZ;
        }

        /// <summary>West edge of the region, in map tiles. Inclusive.</summary>
        public int MinX { get; }

        /// <summary>North edge of the region, in map tiles. Inclusive.</summary>
        public int MinZ { get; }

        /// <summary>East edge of the region, in map tiles. Exclusive.</summary>
        public int MaxX { get; }

        /// <summary>South edge of the region, in map tiles. Exclusive.</summary>
        public int MaxZ { get; }

        /// <summary>How wide the region is, in tiles.</summary>
        public int Width
        {
            get { return MaxX - MinX; }
        }

        /// <summary>How deep the region is, in tiles.</summary>
        public int Depth
        {
            get { return MaxZ - MinZ; }
        }

        /// <summary>
        /// The side of the square the blueprint is built at: the larger of the two dimensions.
        /// Never less than <see cref="MinSideTiles"/>, which is what keeps the region inside its
        /// own blueprint on the short axis.
        /// </summary>
        public int Side
        {
            get
            {
                var side = Math.Max(Width, Depth);
                return side < MinSideTiles ? MinSideTiles : side;
            }
        }

        /// <summary>Whether this region covers any tiles at all. The default value does not.</summary>
        public bool IsValid
        {
            get { return Width > 0 && Depth > 0; }
        }

        /// <summary>
        /// The smallest blueprint side a transit lot is built at, in tiles. The TSO terrain helpers
        /// are why: see <see cref="SimitoneSurroundingsPlan.MinSubWorldSize"/>, which is the same
        /// floor stated for the same reason.
        /// </summary>
        public const int MinSideTiles = SimitoneSurroundingsPlan.MinSubWorldSize;

        /// <summary>
        /// The largest blueprint side a transit lot is built at, in tiles - a hard cap rather than a
        /// hope, because a 256x256 lot is a quarter of a million tiles of architecture, lighting and
        /// room map to stand a sim on for a few seconds.
        /// </summary>
        /// <remarks>
        /// It is also the backstop for the caller's own walk cap: a walk whose street region does
        /// not fit in this square is refused by name, and a caller that has capped the walk at
        /// <see cref="TS1GameScreen"/>'s 240 tiles never sees that refusal. Which of the two caps
        /// fires first is a presentation decision, not a correctness one.
        /// </remarks>
        public const int MaxSideTiles = 256;

        /// <summary>Whether a map tile is inside this region.</summary>
        /// <param name="mapX">Tile x in generated-map space.</param>
        /// <param name="mapZ">Tile z in generated-map space.</param>
        public bool Contains(int mapX, int mapZ)
        {
            return mapX >= MinX && mapX < MaxX && mapZ >= MinZ && mapZ < MaxZ;
        }

        /// <summary>
        /// A map tile as a tile of the blueprint, in tiles. Negative or too large for a
        /// <see cref="LotTilePos"/> when the tile is outside the region, which is why
        /// <see cref="Contains"/> is the gate a caller applies first.
        /// </summary>
        /// <param name="mapX">Tile x in generated-map space.</param>
        public int LocalX(int mapX)
        {
            return mapX - MinX;
        }

        /// <summary>A map tile as a tile of the blueprint, in tiles.</summary>
        /// <param name="mapZ">Tile z in generated-map space.</param>
        public int LocalZ(int mapZ)
        {
            return mapZ - MinZ;
        }

        /// <summary>How many tiles the region covers before it is squared up.</summary>
        public long TileCount
        {
            get { return (long)Width * Depth; }
        }

        /// <summary>
        /// The region a walk needs: its tiles' bounding box, grown by
        /// <paramref name="marginTiles"/> on every side and clipped to the map.
        /// </summary>
        /// <param name="path">The walk. Must be a valid path.</param>
        /// <param name="mapWidth">The generated map's width, in tiles.</param>
        /// <param name="mapHeight">The generated map's height, in tiles.</param>
        /// <param name="marginTiles">
        /// Tiles of margin past the walk on every side; negative is treated as none.
        /// </param>
        /// <param name="region">The region, or the default value when there is none.</param>
        /// <param name="reason">Why there is no region, or <c>null</c> when there is one.</param>
        /// <returns>Whether a region was produced.</returns>
        public static bool TryFor(TransitPath path, int mapWidth, int mapHeight, int marginTiles,
            out TransitStreetRegion region, out string reason)
        {
            region = default(TransitStreetRegion);
            if (path == null || !path.IsValid)
            {
                reason = "there is no planned walk, so there is no street region to build";
                return false;
            }
            if (mapWidth <= 0 || mapHeight <= 0)
            {
                reason = "the generated map is " + mapWidth + "x" + mapHeight
                    + ", so it has no tiles to take a street region from";
                return false;
            }

            var margin = Math.Max(0, marginTiles);
            var minX = int.MaxValue;
            var minZ = int.MaxValue;
            var maxX = int.MinValue;
            var maxZ = int.MinValue;
            var waypoints = path.Waypoints;
            for (int i = 0; i < waypoints.Count; i++)
            {
                var waypoint = waypoints[i];
                if (waypoint.X < 0 || waypoint.Y < 0 || waypoint.X >= mapWidth
                    || waypoint.Y >= mapHeight)
                {
                    //The planner only produces in-bounds waypoints, so this is the corrupt-sidecar
                    //case rather than a routine one - and it is a refusal rather than a clamp,
                    //because a clamped walk is a walk whose blueprint does not contain the walk.
                    reason = "the walk passes through map tile (" + waypoint.X + "," + waypoint.Y
                        + "), which is outside the " + mapWidth + "x" + mapHeight + " generated map";
                    return false;
                }
                if (waypoint.X < minX) minX = waypoint.X;
                if (waypoint.Y < minZ) minZ = waypoint.Y;
                if (waypoint.X > maxX) maxX = waypoint.X;
                if (waypoint.Y > maxZ) maxZ = waypoint.Y;
            }

            //Grown outwards and then clipped, in that order. Clipping first would cut the margin
            //in half on a lot at the edge of the map and quietly hand the avatar a smaller world
            //than the one the walk is in; growing first and clipping the result means a lot on the
            //border gets a smaller region - which is the truth about that map - rather than a
            //narrower margin.
            var clippedMinX = Math.Max(0, minX - margin);
            var clippedMinZ = Math.Max(0, minZ - margin);
            var clippedMaxX = Math.Min(mapWidth, maxX + margin + 1);
            var clippedMaxZ = Math.Min(mapHeight, maxZ + margin + 1);
            if (clippedMaxX <= clippedMinX || clippedMaxZ <= clippedMinZ)
            {
                reason = "the walk's street region falls entirely outside the generated map";
                return false;
            }

            region = new TransitStreetRegion(clippedMinX, clippedMinZ, clippedMaxX, clippedMaxZ);
            reason = null;
            return true;
        }

        /// <summary>One line for a log, or for a failing assertion's message.</summary>
        public string Describe()
        {
            if (!IsValid) return "no street region";
            return "map tiles x[" + MinX + "," + MaxX + ") z[" + MinZ + "," + MaxZ + ") ("
                + Width + "x" + Depth + ", " + TileCount + " tile(s)), built at " + Side
                + "x" + Side + " tiles";
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// The terrain a transit lot is built out of: a four-by-four height grid over its
    /// <see cref="TransitStreetRegion"/>, the TSO road byte for the region's edges, and the ground
    /// type the region's tiles are mostly made of.
    ///
    /// <para>
    /// <b>It is the lot's counterpart, not a reuse.</b> <see cref="SimitoneNeighbourTerrain"/> reads
    /// the same two things out of the same map for a <em>lot</em>: a square region of fixed size
    /// whose edges are a <em>property line</em>, with the road probed just <em>outside</em> it. A
    /// transit lot's region is a square of <em>street</em>, so its edges are not a property line
    /// and the road is on the inside. Reusing the lot's probe here would give the transit lot a
    /// road template laid along whichever edge of it happens to have more street one tile further
    /// out, which is a coin toss. So the probe direction is the other way, and that difference is
    /// the whole reason this type exists rather than a call to
    /// <see cref="SimitoneNeighbourTerrain.TryBuild"/>.
    /// </para>
    ///
    /// <para>
    /// <b>The height grid is sampled the way the lot's is</b> - integer division, so the two outer
    /// samples land on the region's first and last tile rather than one past it - and the base
    /// level is <see cref="SimitoneNeighbourTerrain.BaseLevel"/>'s arithmetic over it, mean of the
    /// centre four times a hundred. A difference of a truncated hundredth is a hundredth of a tile
    /// of visible step, so the two halves are the same expression rather than two similar ones.
    /// </para>
    ///
    /// <para>
    /// <b>Pure.</b> Reads a <see cref="NeighbourhoodMap"/> and returns a value; no device, no VM, no
    /// content, no file.
    /// </para>
    /// </summary>
    public readonly struct TransitStreetTerrain
    {
        /// <summary>Grid samples per axis. Four, as for a lot, because the helpers read a 4x4.</summary>
        public const int GridSize = SimitoneNeighbourTerrain.GridSize;

        /// <summary>Grid samples in total.</summary>
        public const int GridSamples = SimitoneNeighbourTerrain.GridSamples;

        private static readonly byte[] NoHeights = new byte[0];

        private readonly byte[] _heights;

        private TransitStreetTerrain(TerrainBlend blend, byte roads, byte[] heights)
        {
            Blend = blend;
            Roads = roads;
            _heights = heights;
        }

        /// <summary>
        /// The blend the TSO terrain helpers are handed: the region's most common ground type, and
        /// the adjacency flag set whenever any edge of the region has a street on it.
        /// </summary>
        public TerrainBlend Blend { get; }

        /// <summary>
        /// The TSO road byte for the region's own edges, in
        /// <see cref="SimitoneNeighbourTerrain"/>'s four-edge-four-corner layout: the low nibble
        /// is a street along that edge, the high nibble a corner where two of them meet.
        /// </summary>
        public byte Roads { get; }

        /// <summary>Whether this holds a grid at all. The default value does not.</summary>
        public bool IsValid
        {
            get { return _heights != null && _heights.Length == GridSamples; }
        }

        /// <summary>The sixteen height samples, or an empty list when there is no grid.</summary>
        public IReadOnlyList<byte> Heights
        {
            get { return IsValid ? new ReadOnlyCollection<byte>(_heights)
                : new ReadOnlyCollection<byte>(NoHeights); }
        }

        /// <summary>The height sample at a grid position, or 0 off the grid.</summary>
        /// <param name="x">Grid x, 0..3.</param>
        /// <param name="y">Grid y, 0..3.</param>
        public byte HeightAt(int x, int y)
        {
            if (!IsValid) return 0;
            if (x < 0 || x >= GridSize || y < 0 || y >= GridSize) return 0;
            return _heights[y * GridSize + x];
        }

        /// <summary>
        /// The region's ground height in the FreeSO helpers' own units - the mean of the centre four
        /// samples times a hundred, which is <see cref="SimitoneNeighbourTerrain.BaseLevel"/>'s
        /// arithmetic including its truncation. This is the number a transit lot's
        /// <c>Blueprint.BaseAlt</c> is measured from.
        /// </summary>
        public int BaseLevel
        {
            get
            {
                if (!IsValid) return 0;
                var centre = HeightAt(1, 1) + HeightAt(2, 1) + HeightAt(1, 2) + HeightAt(2, 2);
                return (int)((centre / 4f) * 100f);
            }
        }

        /// <summary>
        /// The <see cref="VMTSOSurroundingTerrain"/> the height helper is handed: the street's blend
        /// and road byte at the centre of a 3x3, and all sixteen height samples.
        /// </summary>
        /// <remarks>
        /// The same one-off container <c>SimitoneSurroundingsRestore.TerrainContainerFor</c> builds
        /// for a neighbour lot, and it exists for the same reason: §11's second landmine. A TS1 lot
        /// state has no <c>TSOState.Terrain</c>, and <c>RestoreHeight</c> and <c>GetBaseLevel</c>
        /// take a <c>VMTSOSurroundingTerrain</c> rather than a height array, so one has to be
        /// built and filled in explicitly. Only <c>[1, 1]</c> of the blend half is ever read,
        /// because the helper addresses the lot it is building as the centre of a 3x3.
        /// </remarks>
        public VMTSOSurroundingTerrain ToSurroundingTerrain()
        {
            var container = new VMTSOSurroundingTerrain();
            container.BlendN[1, 1] = Blend;
            container.Roads[1, 1] = Roads;
            for (int y = 0; y < GridSize; y++)
            {
                for (int x = 0; x < GridSize; x++)
                {
                    container.Height[x, y] = HeightAt(x, y);
                }
            }
            return container;
        }

        /// <summary>
        /// One generated-map elevation byte as a height-grid byte. Delegates rather than
        /// restating, because the rounding and the clamp are what keep a flat neighbourhood flat.
        /// </summary>
        /// <param name="elevation">A <see cref="NeighbourhoodMap.ElevationData"/> byte.</param>
        public static byte HeightByteFor(byte elevation)
        {
            return SimitoneNeighbourTerrain.HeightByteFor(elevation);
        }

        /// <summary>
        /// The TSO road byte for a street region: the four edge bits and the four corner bits, from
        /// the generated map's own road bytes <em>inside</em> the region.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The probe is inward and shallow - <see cref="SimitoneNeighbourTerrain.RoadProbeDepth"/>
        /// tiles from each edge, along that whole edge - because a transit lot's region is mostly
        /// street by construction. A region that is one tile wide has its west and east edges on the
        /// same column, and both get their bit, which is the honest answer for a pavement rather
        /// than a bug: the blueprint is one tile of road and there is road along both of its sides.
        /// </para>
        /// <para>
        /// Corners are set by the same rule the lot's are: a corner where two edges both have a
        /// street is a corner the road turns at, and one edge alone is a straight. Depth zero is
        /// part of the probe, so an edge that <em>is</em> road counts - a street running exactly
        /// along a region's border is the commonest case there is.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="region">The region to read the edges of.</param>
        public static byte RoadByteFor(NeighbourhoodMap map, TransitStreetRegion region)
        {
            if (map == null || !region.IsValid) return 0;
            var roads = (byte)0;
            if (EdgeHasRoad(map, region, true, 0, -1)) roads |= SimitoneNeighbourTerrain.RoadNorth;
            if (EdgeHasRoad(map, region, true, 0, 1)) roads |= SimitoneNeighbourTerrain.RoadSouth;
            if (EdgeHasRoad(map, region, false, -1, 0)) roads |= SimitoneNeighbourTerrain.RoadWest;
            if (EdgeHasRoad(map, region, false, 1, 0)) roads |= SimitoneNeighbourTerrain.RoadEast;

            if ((roads & SimitoneNeighbourTerrain.RoadNorth) != 0
                && (roads & SimitoneNeighbourTerrain.RoadWest) != 0)
            {
                roads |= SimitoneNeighbourTerrain.RoadCornerTopLeft;
            }
            if ((roads & SimitoneNeighbourTerrain.RoadNorth) != 0
                && (roads & SimitoneNeighbourTerrain.RoadEast) != 0)
            {
                roads |= SimitoneNeighbourTerrain.RoadCornerTopRight;
            }
            if ((roads & SimitoneNeighbourTerrain.RoadSouth) != 0
                && (roads & SimitoneNeighbourTerrain.RoadEast) != 0)
            {
                roads |= SimitoneNeighbourTerrain.RoadCornerBottomRight;
            }
            if ((roads & SimitoneNeighbourTerrain.RoadSouth) != 0
                && (roads & SimitoneNeighbourTerrain.RoadWest) != 0)
            {
                roads |= SimitoneNeighbourTerrain.RoadCornerBottomLeft;
            }
            return roads;
        }

        /// <summary>
        /// The terrain for a street region, read out of the generated map, or <c>false</c> with the
        /// reason. A refusal is never a wrong answer and never a fallback grid.
        /// </summary>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="region">The region to sample, or the default value.</param>
        /// <param name="terrain">The terrain, or the default value when there is none.</param>
        /// <param name="reason">Why there is no terrain, or <c>null</c> when there is.</param>
        /// <returns>Whether a terrain was produced.</returns>
        public static bool TryBuild(NeighbourhoodMap map, TransitStreetRegion region,
            out TransitStreetTerrain terrain, out string reason)
        {
            terrain = default(TransitStreetTerrain);
            if (map == null)
            {
                reason = "there is no generated map to build the transit lot's street out of";
                return false;
            }
            if (!region.IsValid)
            {
                reason = "the walk's street region covers no tiles, so there is no street to build";
                return false;
            }
            if (map.Width <= 0 || map.Height <= 0)
            {
                reason = "the generated map has no tiles";
                return false;
            }

            //Integer division, as the lot sampler does, so the outer samples land on the region's
            //first and last tile rather than one past them.
            var heights = new byte[GridSamples];
            var spanX = Math.Max(0, region.Width - 1);
            var spanZ = Math.Max(0, region.Depth - 1);
            for (int y = 0; y < GridSize; y++)
            {
                var sampleZ = region.MinZ + (y * spanZ) / (GridSize - 1);
                var clampedZ = Clamp(sampleZ, 0, map.Height - 1);
                for (int x = 0; x < GridSize; x++)
                {
                    var sampleX = region.MinX + (x * spanX) / (GridSize - 1);
                    var clampedX = Clamp(sampleX, 0, map.Width - 1);
                    heights[y * GridSize + x] =
                        HeightByteFor(map.ElevationData[map.Index(clampedX, clampedZ)]);
                }
            }

            var roads = RoadByteFor(map, region);
            var type = (TerrainType)MostCommonTerrain(map, region);
            var blend = new TerrainBlend
            {
                Base = type,
                Blend = type,
                AdjFlags = roads != 0 ? SimitoneNeighbourTerrain.AdjFlagMyEdge : (byte)0,
                WaterFlags = 0
            };

            terrain = new TransitStreetTerrain(blend, roads, heights);
            reason = null;
            return true;
        }

        /// <summary>One line for a log, or for a failing assertion's message.</summary>
        public string Describe()
        {
            if (!IsValid) return "no street terrain";
            var sb = new StringBuilder();
            sb.Append("street terrain ").Append(Blend.Base)
              .Append(", roads 0x").Append(Roads.ToString("X2", CultureInfo.InvariantCulture))
              .Append(" (").Append(SimitoneNeighbourTerrain.RoadsInWords(Roads)).Append(')')
              .Append(", base level ").Append(BaseLevel)
              .Append(", heights [");
            for (int i = 0; i < GridSamples; i++)
            {
                if (i > 0) sb.Append(i % GridSize == 0 ? "; " : ", ");
                sb.Append(_heights[i].ToString(CultureInfo.InvariantCulture));
            }
            return sb.Append(']').ToString();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }

        private static bool EdgeHasRoad(NeighbourhoodMap map, TransitStreetRegion region,
            bool horizontal, int stepX, int stepY)
        {
            //The edge itself is depth 0 and the strip one tile in is depth 1, up to the probe depth
            //- so a street that is the region's own border counts, which for a street region is the
            //ordinary case rather than an edge case.
            for (int depth = 0; depth < SimitoneNeighbourTerrain.RoadProbeDepth; depth++)
            {
                for (int t = 0; t < (horizontal ? region.Width : region.Depth); t++)
                {
                    int x, y;
                    if (horizontal)
                    {
                        x = region.MinX + t;
                        y = stepY < 0 ? region.MinZ + depth : region.MaxZ - 1 - depth;
                    }
                    else
                    {
                        x = stepX < 0 ? region.MinX + depth : region.MaxX - 1 - depth;
                        y = region.MinZ + t;
                    }
                    if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) continue;
                    if (map.RoadMaskAt(map.Index(x, y)) != RoadMask.None) return true;
                }
            }
            return false;
        }

        private static byte MostCommonTerrain(NeighbourhoodMap map, TransitStreetRegion region)
        {
            //The modal ground type over the region, ties to the lowest type value so the answer is
            //a function of the map rather than of iteration order. Water is not special-cased: the
            //generated map does not put water in a street, and a region that somehow has some is a
            //region the caller should not have walked into.
            var counts = new int[NeighbourhoodTerrain.MaxDrawable + 1];
            var counted = 0;
            for (int z = region.MinZ; z < Math.Min(region.MaxZ, map.Height); z++)
            {
                for (int x = region.MinX; x < Math.Min(region.MaxX, map.Width); x++)
                {
                    var terrain = map.TerrainType[map.Index(x, z)];
                    if (terrain <= NeighbourhoodTerrain.MaxDrawable) counts[terrain]++;
                    counted++;
                }
            }
            if (counted == 0) return (byte)NeighbourhoodTerrain.Grass;

            var best = 0;
            for (int i = 1; i < counts.Length; i++)
            {
                if (counts[i] > counts[best]) best = i;
            }
            return (byte)best;
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }

    /// <summary>
    /// Everything a transit lot's blueprint is decided from, decided once and immutably: the region
    /// of generated map it covers, the terrain that region is built out of, the side it is built
    /// at, the altitude it hangs at, which way it faces and where it stands inside the world that
    /// will draw it.
    ///
    /// <para>
    /// <b>Split out for the same reason <see cref="SimitoneSurroundingsPlan"/> is.</b> "How big is
    /// the transit lot and where does it stand" is arithmetic over a map and a layout; building it
    /// needs a device, a content manager and a VM. Keeping the decision in a value means the half
    /// that can be wrong in a way a player would see is assertable in a test, and the bridge is
    /// left with nothing to decide.
    /// </para>
    ///
    /// <para>
    /// <b>Placement, and the sign.</b> The transit lot is a sub-world of the lot the player left,
    /// because that is where a <c>SubWorldComponent</c> has to be created - a world belongs to a
    /// graphics device, and the one the player is looking at is the only one that has one. Its
    /// <c>GlobalPosition</c> is therefore the tile delta from the <em>origin lot's</em> minimum
    /// corner to the <em>region's</em>, unsigned, which is exactly
    /// <see cref="SimitoneSurroundingsPlan.SubWorldPositionFor"/>'s rule with the region standing
    /// in for the neighbour lot. See <see cref="SimitoneSurroundingsNeighbour.SubWorldPosition"/>
    /// for the three engine reads that establish the sign.
    /// </para>
    ///
    /// <para>
    /// <b>Altitude.</b> A sub-world's ground is drawn at
    /// <c>(altitude - BaseAlt) * TerrainFactor * 3</c>, so
    /// <c>BaseAlt = activeBase - ownBase</c> in hundredths of a helper height byte, and the units
    /// cancel because <c>BaseAlt</c> is in blueprint altitude units. That is
    /// <see cref="SimitoneSurroundingsRestore.BaseAltitudeFor"/>'s arithmetic and this file does not
    /// restate it: both sides are read out of the same map by the same expression, so they are in
    /// the same units by construction.
    /// </para>
    /// </summary>
    public sealed class TransitBlueprintPlan
    {
        private TransitBlueprintPlan(TransitStreetRegion region, TransitStreetTerrain terrain,
            NeighbourhoodLot originLot, Vector2 subWorldOrigin, int baseAltitude, ushort lotDirection,
            int marginTiles)
        {
            Region = region;
            Terrain = terrain;
            OriginLot = originLot;
            SubWorldOrigin = subWorldOrigin;
            BaseAltitude = baseAltitude;
            LotDirection = lotDirection;
            MarginTiles = marginTiles;
        }

        /// <summary>The generated map the transit lot is built over.</summary>
        public TransitStreetRegion Region { get; }

        /// <summary>The street that region is built out of.</summary>
        public TransitStreetTerrain Terrain { get; }

        /// <summary>
        /// The lot the player was standing in when the walk began: the sub-world's parent, and the
        /// lot whose base level and minimum corner this plan is measured from. Never null.
        /// </summary>
        public NeighbourhoodLot OriginLot { get; }

        /// <summary>
        /// Where the transit lot stands inside the origin lot's tile space, as a
        /// <c>SubWorldComponent.GlobalPosition</c>: the region's minimum corner minus the origin
        /// lot's, unsigned. See the type remarks.
        /// </summary>
        public Vector2 SubWorldOrigin { get; }

        /// <summary>
        /// The <c>Blueprint.BaseAlt</c> the transit lot hangs at: the origin lot's base level less
        /// the street's, both in the FreeSO helpers' hundredths of a height byte.
        /// </summary>
        public int BaseAltitude { get; }

        /// <summary>
        /// Which way the transit lot faces, 0..3, as the high half of <c>vm.TSOState.Size</c>.
        /// Taken from the origin lot's <see cref="NeighbourhoodLot.DoorSide"/> so the street lot
        /// faces the way the lot the player left does, and the landmark row
        /// <c>PositionLandmarkObjects</c> lays lands on the pavement the walk is on.
        /// </summary>
        public ushort LotDirection { get; }

        /// <summary>Tiles of margin past the walk on every side of <see cref="Region"/>.</summary>
        public int MarginTiles { get; }

        /// <summary>
        /// The side of the blueprint, in tiles. This is <see cref="TransitStreetRegion.Side"/>,
        /// which is already floored at <see cref="TransitStreetRegion.MinSideTiles"/> and
        /// <see cref="TransitBlueprintPlan.TryCreate"/> has already checked against
        /// <see cref="TransitStreetRegion.MaxSideTiles"/>.
        /// </summary>
        public int Side
        {
            get { return Region.Side; }
        }

        /// <summary>
        /// The lot the walk ends at, or <see cref="TransitPath.NoLot"/>. Recorded on the plan so a
        /// handover can be answered without reading the walk back, and so the plan and the walk
        /// cannot disagree about which door this was.
        /// </summary>
        public int DestinationLot { get; internal set; }

        /// <summary>
        /// The lot the walk starts at, or <see cref="TransitPath.NoLot"/>, for the same reason.
        /// </summary>
        public int OriginLotNumber { get; internal set; }

        /// <summary>Whether this plan can be built at all.</summary>
        public bool IsValid
        {
            get
            {
                return Region.IsValid && Terrain.IsValid && OriginLot != null
                    && Side >= TransitStreetRegion.MinSideTiles
                    && Side <= TransitStreetRegion.MaxSideTiles;
            }
        }

        /// <summary>
        /// Whether every tile of <paramref name="path"/> falls inside the blueprint this plan builds,
        /// which is the claim the margin exists to make and the thing that is easy to get wrong by
        /// one tile. Pure, and the reason the plan can be checked without building it.
        /// </summary>
        /// <param name="path">The walk, or <c>null</c>.</param>
        public bool ContainsWholeWalk(TransitPath path)
        {
            if (path == null || !path.IsValid || !Region.IsValid) return false;
            var waypoints = path.Waypoints;
            for (int i = 0; i < waypoints.Count; i++)
            {
                if (!Region.Contains(waypoints[i].X, waypoints[i].Y)) return false;
                if (LocalX(waypoints[i].X) < 0 || LocalX(waypoints[i].X) >= Side) return false;
                if (LocalZ(waypoints[i].Y) < 0 || LocalZ(waypoints[i].Y) >= Side) return false;
            }
            return true;
        }

        /// <summary>A map tile as a tile of this blueprint, in tiles.</summary>
        /// <param name="mapX">Tile x in generated-map space.</param>
        public int LocalX(int mapX)
        {
            return Region.LocalX(mapX);
        }

        /// <summary>A map tile as a tile of this blueprint, in tiles.</summary>
        /// <param name="mapZ">Tile z in generated-map space.</param>
        public int LocalZ(int mapZ)
        {
            return Region.LocalZ(mapZ);
        }

        /// <summary>
        /// Where a map tile is inside the transit lot, as the <see cref="LotTilePos"/> an entity's
        /// <c>Position</c> takes. The level is
        /// <see cref="SimitoneSurroundingsRestore.SubWorldLevel"/> - the same level a surround
        /// neighbour's own state sits at, and the lot view's silent level, so a walk crosses into
        /// and out of this lot without a change of storey.
        /// </summary>
        /// <param name="mapX">Tile x in generated-map space.</param>
        /// <param name="mapZ">Tile z in generated-map space.</param>
        public LotTilePos LocalTileAt(int mapX, int mapZ)
        {
            return new LotTilePos(
                (short)ClampTile(LocalX(mapX), Side),
                (short)ClampTile(LocalZ(mapZ), Side),
                SimitoneSurroundingsRestore.SubWorldLevel);
        }

        /// <summary>Where a waypoint is inside the transit lot. See <see cref="LocalTileAt"/>.</summary>
        /// <param name="waypoint">The waypoint, in generated-map tile space.</param>
        public LotTilePos LocalTileFor(TransitWaypoint waypoint)
        {
            return LocalTileAt(waypoint.X, waypoint.Y);
        }

        /// <summary>
        /// The plan for a walk, or <c>false</c> with the reason.
        /// </summary>
        /// <remarks>
        /// Every refusal is an answer rather than a throw, because "can the player walk that" is
        /// the question: no walk, no origin lot, no generated map, a region that will not fit in a
        /// blueprint, a street the map cannot answer for. A caller that cannot build one falls back
        /// to a lot switch, which is B3's behaviour and the one the capability's own description
        /// promises.
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="originLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="path">The planned walk, or <c>null</c>.</param>
        /// <param name="marginTiles">Tiles of margin past the walk, or a negative number for none.</param>
        /// <param name="plan">The plan, or <c>null</c> when there is none.</param>
        /// <param name="reason">Why no plan, or <c>null</c> when there was one.</param>
        /// <returns>Whether a plan was produced.</returns>
        public static bool TryCreate(NeighbourhoodMap map, NeighbourhoodLot originLot,
            TransitPath path, int marginTiles, out TransitBlueprintPlan plan, out string reason)
        {
            plan = null;
            if (path == null || !path.IsValid)
            {
                reason = "there is no planned walk, so there is no transit lot to build";
                return false;
            }
            if (originLot == null)
            {
                reason = "the lot the player is standing in is not in this neighbourhood's layout,"
                    + " so there is no parent lot to hang a transit lot in";
                return false;
            }
            if (map == null)
            {
                reason = "there is no generated map to build a transit lot out of";
                return false;
            }

            var margin = marginTiles < 0 ? TransitStreetRegion.DefaultMarginTiles : marginTiles;
            TransitStreetRegion region;
            if (!TransitStreetRegion.TryFor(path, map.Width, map.Height, margin,
                out region, out reason))
            {
                return false;
            }
            if (region.Side > TransitStreetRegion.MaxSideTiles)
            {
                reason = "the walk's street region is " + region.Side + " tiles square, which is"
                    + " more than the " + TransitStreetRegion.MaxSideTiles
                    + " a transit lot may be built at";
                return false;
            }

            TransitStreetTerrain terrain;
            if (!TransitStreetTerrain.TryBuild(map, region, out terrain, out reason))
            {
                return false;
            }

            //The origin lot's own base level, read out of the same map by the same sampler. A lot
            //the map cannot answer for is level zero, which is the flat-neighbourhood case and is
            //the only one the packer's own layouts produce for a lot on the edge of the map.
            SimitoneNeighbourTerrain parentTerrain;
            SimitoneNeighbourTerrain.TryBuild(map, originLot, out parentTerrain, out reason);
            var baseAltitude = SimitoneSurroundingsRestore.BaseAltitudeFor(
                parentTerrain.BaseLevel, terrain.BaseLevel);

            plan = new TransitBlueprintPlan(region, terrain, originLot,
                new Vector2(region.MinX - originLot.WorldX, region.MinZ - originLot.WorldY),
                baseAltitude,
                SimitoneSurroundingsPlan.LotDirectionFor(originLot.DoorSide),
                margin)
            {
                OriginLotNumber = path.OriginLot,
                DestinationLot = path.DestinationLot,
            };

            if (!plan.ContainsWholeWalk(path))
            {
                //Structurally unreachable - the region is grown by the margin and only clipped at
                //the map's own border, where the waypoints are already in bounds - but the claim is
                //worth making once, in the place that would otherwise be trusted.
                plan = null;
                reason = "the transit lot's blueprint does not contain the whole walk";
                return false;
            }

            reason = null;
            return true;
        }

        /// <summary>One line for a log, or for a failing assertion's message.</summary>
        public string Describe()
        {
            if (!IsValid) return "no transit blueprint plan";
            return "transit lot " + Side + "x" + Side + " at tile ("
                + SubWorldOrigin.X.ToString(CultureInfo.InvariantCulture) + ","
                + SubWorldOrigin.Y.ToString(CultureInfo.InvariantCulture) + ") of lot "
                + OriginLot.Number + ", walking lot " + OriginLotNumber + " -> lot "
                + DestinationLot + ", base alt " + BaseAltitude + ", facing " + LotDirection
                + ". " + Region.Describe() + ". " + Terrain.Describe();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }

        private static int ClampTile(int value, int side)
        {
            if (value < 0) return 0;
            if (value >= side) return Math.Max(0, side - 1);
            return value;
        }
    }

    /// <summary>
    /// What happens to a walk when it ends: whether it may end, what the world names afterwards,
    /// and whether the transit lot's own world goes away as part of it.
    ///
    /// <para>
    /// <b>A decision, so it is a value.</b> A walk ending is the one moment the exactly-one-active-lot
    /// invariant changes hands, and it is the moment at which a mistake leaves the player standing
    /// on a pavement that belongs to no lot at all. Stating the outcome as an immutable value - and
    /// making the refusal and the permission the same type, so a caller cannot have one without
    /// looking at the other - means the rule is a test rather than an ordering inside a method.
    /// </para>
    ///
    /// <para>
    /// <b>Three refusals, all of them conservative.</b> There is no walk, so there is nothing to
    /// hand over. The avatar is not here, so handing the world over would leave a destination lot
    /// with no sim in it. The avatar has not arrived, so completing would teleport it past the walk
    /// it was asked to perform - <see cref="CancelTransit"/> is the caller's route out of that, and
    /// B3's discrete switch is the one after it.
    /// </para>
    ///
    /// <para>
    /// <b>The permitted handover also re-asserts the invariant.</b> A permitted handover names the
    /// destination as the world's active lot; if that pair did not satisfy
    /// <see cref="NeighbourhoodTransitLot.IsExactlyOneActiveLot"/> the answer would be a refusal
    /// even though the walk finished, because "arrived" and "the world is in a state that can be
    /// drawn" are different claims.
    /// </para>
    /// </summary>
    public readonly struct TransitHandover
    {
        private TransitHandover(bool allowed, string reason, int destination,
            ActiveLotKind resultingKind, int resultingLot, bool disposesWorld)
        {
            Allowed = allowed;
            Reason = reason;
            Destination = destination;
            ResultingKind = resultingKind;
            ResultingLot = resultingLot;
            DisposesWorld = disposesWorld;
        }

        /// <summary>Whether the walk may be completed now.</summary>
        public bool Allowed { get; }

        /// <summary>Why not, when it may not. <c>null</c> when it may.</summary>
        public string Reason { get; }

        /// <summary>The lot the player arrives in, or <see cref="TransitPath.NoLot"/>.</summary>
        public int Destination { get; }

        /// <summary>Which kind of lot owns the avatar after the handover.</summary>
        public ActiveLotKind ResultingKind { get; }

        /// <summary>The lot that owns the avatar after the handover, or
        /// <see cref="TransitPath.NoLot"/>.</summary>
        public int ResultingLot { get; }

        /// <summary>
        /// Whether the transit lot's own <c>World</c> is taken down as part of the handover. Always
        /// true for a permitted one: a sub-world left on the parent's blueprint would be drawn, and
        /// would hold a light map and an architecture, for the rest of the session.
        /// </summary>
        public bool DisposesWorld { get; }

        /// <summary>Whether this handover is one that may be carried out.</summary>
        public bool IsValid
        {
            get { return Allowed; }
        }

        /// <summary>Whether the avatar ends up in a lot, as opposed to back on the pavement.</summary>
        public bool ArrivesInLot
        {
            get { return Allowed && ResultingKind != ActiveLotKind.Transit; }
        }

        /// <summary>
        /// Whether this object's own outcome satisfies the exactly-one-active-lot invariant. True
        /// for a refusal as well, because a refusal changes nothing - which is the point of it.
        /// </summary>
        public bool PreservesOwner
        {
            get
            {
                return !Allowed
                    || NeighbourhoodTransitLot.IsExactlyOneActiveLot(ResultingKind, ResultingLot);
            }
        }

        /// <summary>The decision for a walk at <paramref name="state"/>.</summary>
        /// <param name="state">This frame's walk state, from the transit lot.</param>
        /// <param name="avatarCarried">Whether the transit lot holds the travelling avatar.</param>
        public static TransitHandover For(TransitState state, bool avatarCarried)
        {
            if (!state.IsValid)
            {
                return Refuse("there is no walk in progress, so there is nothing to hand over");
            }
            if (!avatarCarried)
            {
                return Refuse("the transit lot does not hold the travelling avatar, so there is"
                    + " no sim to give the destination lot");
            }
            if (!state.IsArrived)
            {
                return Refuse("the avatar is still "
                    + state.Remaining.ToString("0.###", CultureInfo.InvariantCulture)
                    + " tile(s) short of lot " + state.Destination + "'s door");
            }
            if (state.Destination == TransitPath.NoLot)
            {
                return Refuse("the walk finished but names no destination lot, so the world would"
                    + " be left in no lot at all");
            }

            var handover = new TransitHandover(true, null, state.Destination,
                ActiveLotKind.Active, state.Destination, true);
            if (!handover.PreservesOwner)
            {
                return Refuse("completing this walk would leave the world in a state where more"
                    + " than one lot could claim the avatar");
            }
            return handover;
        }

        /// <summary>A handover that is not allowed, for <paramref name="why"/>.</summary>
        /// <param name="why">Why the walk may not be completed now.</param>
        public static TransitHandover Refuse(string why)
        {
            return new TransitHandover(false, why ?? "the walk cannot be completed",
                TransitPath.NoLot, ActiveLotKind.Transit, TransitPath.NoLot, false);
        }

        /// <summary>One line for a log, or for a failing assertion's message.</summary>
        public string Describe()
        {
            if (!Allowed) return "no handover: " + Reason;
            return "hand the avatar to lot " + Destination + "; the world names lot "
                + ResultingLot + " as a " + ResultingKind
                + (DisposesWorld ? " and the transit lot's world is taken down" : string.Empty);
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// The seam that moves the travelling avatar between VMs, and nothing else.
    ///
    /// <para>
    /// <b>Why it is an interface.</b> Moving a sim from one <c>VM</c> to another is the one part of
    /// B4 that is not arithmetic and not a lot-view convention: it needs the entity's own marshal,
    /// a world object definition for the avatar template, an empty thread, and registration with
    /// the receiving VM's object queries. All of that is public FreeSO API and it is written out in
    /// <see cref="TransitAvatarBinder"/>, but it is also the part that has to be right about
    /// <em>ownership</em> - a binder that quietly left the avatar in the origin VM would be the
    /// exact failure B4 exists to avoid, and it would be invisible. Putting it behind two methods
    /// means the invariant can be stated at the boundary: the traveller goes in, exactly one
    /// <c>VMAvatar</c> comes out, and the way back is a marshal the destination lot can adopt.
    /// </para>
    ///
    /// <para>
    /// <b>What the return value is and why.</b> <see cref="TryUnbind"/> hands back a
    /// <see cref="VMAvatarMarshal"/> rather than the entity, because the entity is bound to the
    /// transit VM's context and will be destroyed with it. A marshal is the only form of a sim that
    /// outlives its lot, and it is the form <c>VM.Load</c> and the destination's own
    /// <c>PerformLotSwitch</c> already speak.
    /// </para>
    ///
    /// <para>
    /// <b>A binder never throws.</b> Every refusal is a sentence, because a binder that throws from
    /// inside a lot switch takes the origin lot down with it.
    /// </para>
    /// </summary>
    public interface ITransitAvatarBinder
    {
        /// <summary>
        /// Takes the travelling avatar out of whatever VM holds it and puts a copy of it into
        /// <paramref name="transitVm"/> at <paramref name="start"/>.
        /// </summary>
        /// <param name="transitVm">The transit lot's own VM.</param>
        /// <param name="sourceVm">
        /// The VM the traveller is in now, so a binder can take it out of there. An entity does not
        /// reach back to its own VM - <c>VMEntity</c> has no <c>VM</c> of its own, every method
        /// that needs one takes it as an argument - so this is the only way a binder can do the
        /// half of its job that matters most.
        /// </param>
        /// <param name="traveller">The sim that is walking, in its current VM.</param>
        /// <param name="start">Where in the transit lot it is standing.</param>
        /// <param name="bound">The entity that now lives in the transit lot, or <c>null</c>.</param>
        /// <param name="reason">Why it could not be moved, or <c>null</c> when it was.</param>
        /// <returns>Whether the avatar is now in the transit lot.</returns>
        bool TryBind(VM transitVm, VM sourceVm, VMEntity traveller, LotTilePos start,
            out VMEntity bound, out string reason);

        /// <summary>
        /// Takes the avatar out of the transit lot and hands back the marshal the caller's
        /// destination VM will adopt.
        /// </summary>
        /// <param name="transitVm">The transit lot's own VM.</param>
        /// <param name="carried">The entity <see cref="TryBind"/> produced.</param>
        /// <param name="carriedMarshal">The avatar's own state, or <c>null</c>.</param>
        /// <param name="reason">Why it could not be taken back, or <c>null</c> when it was.</param>
        /// <returns>Whether the avatar is back in the caller's hands.</returns>
        bool TryUnbind(VM transitVm, VMEntity carried, out VMAvatarMarshal carriedMarshal,
            out string reason);

        /// <summary>
        /// The way back: rebuilds the travelling avatar in a lot from the marshal
        /// <see cref="TryUnbind"/> handed over, and makes that lot's controlled sim.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why this is a member and not "the caller loads the marshal".</b> Because loading an
        /// avatar marshal into a live <c>VM</c> is this binder's own sequence - load, add to the
        /// blueprint, fetch the tree, add to the VM, give it a thread, schedule it, resolve the
        /// cross references - and it is the part that is easy to get wrong in the two directions
        /// that matter. A caller that reimplemented it would get the ordering subtly wrong and the
        /// fault would show up as a sim whose inventory reads null, several frames after a walk
        /// was cancelled.
        /// </para>
        /// <para>
        /// <b>The two directions it serves.</b> A cancelled walk hands the sim back to the lot it
        /// came from - <see cref="NeighbourhoodTransitLotVm.TryCancel(VM, out VMAvatarMarshal,
        /// out VMEntity, out string)"/> - which is the origin lot losing its controlled sim
        /// because <see cref="TryBind"/> deleted it out of there. And a completed walk hands it to
        /// the lot the player is entering, so that lot adopts the sim that walked rather than
        /// spawning its own from the net driver's join command.
        /// </para>
        /// <para>
        /// <b>Ownership, stated once for both.</b> On success the sim is in
        /// <paramref name="destinationVm"/> and nowhere else: the marshal has been consumed into
        /// a live entity and the caller must not load it anywhere as well. On failure nothing has
        /// changed, so a caller that cannot afford to lose the sim keeps it - which is why the
        /// binder never throws and every refusal is a sentence.
        /// </para>
        /// </remarks>
        /// <param name="destinationVm">
        /// The lot the sim is going back into. Its <c>MyUID</c> becomes the marshal's persist id
        /// on success, so it is the lot's controlled sim afterwards.
        /// </param>
        /// <param name="carriedMarshal">The sim's own state, from <see cref="TryUnbind"/>.</param>
        /// <param name="at">Where in the destination lot the sim is standing.</param>
        /// <param name="bound">The entity that now lives in the destination lot, or <c>null</c>.</param>
        /// <param name="reason">Why it could not be put back, or <c>null</c> when it was.</param>
        /// <returns>Whether the sim is now the destination lot's controlled avatar.</returns>
        bool TryRebind(VM destinationVm, VMAvatarMarshal carriedMarshal, LotTilePos at,
            out VMEntity bound, out string reason);
    }

    /// <summary>
    /// The real binder: the default <see cref="ITransitAvatarBinder"/>, built out of public FreeSO
    /// API and following the same sequence <c>VM.LoadAsync</c> follows for an avatar arriving from
    /// a marshal.
    ///
    /// <para>
    /// <b>The order is not cosmetic.</b> A <c>VMAvatar</c> constructed and loaded but not registered
    /// is an object nothing can find; one registered without a thread is an object
    /// <c>VM.Tick</c> will fault on; one with a thread but no cross-reference pass has a
    /// <c>Contained</c> array of nulls and will fault on the first inventory read. So: load, add to
    /// the blueprint, fetch the tree, add to the VM, give it a thread, schedule it, resolve the
    /// cross references - in that order, which is <c>VM.LoadAsync</c>'s.
    /// </para>
    ///
    /// <para>
    /// <b>Why the traveller is deleted, not left.</b> <see cref="TryBind"/> removes the avatar from
    /// the VM it came from. That is the whole of B4's task 1: two lot VMs holding one sim is the
    /// failure mode, so "copy it across and leave the original" is not a polite option, it is the
    /// bug. The entity is deleted with <c>cleanupAll: true</c> so the lot the player left does not
    /// keep a null reference to it either.
    /// </para>
    ///
    /// <para>
    /// <b>What it cannot prove.</b> The engine's own avatar-in and avatar-out paths go through the
    /// net driver's join command, which this file does not synthesise: a transit lot is a local lot
    /// with one sim in it and no net driver worth the name, and the marshal round trip is the
    /// supported way to move a sim between two local VMs. What that leaves unverified is the
    /// interactive half - the travelling sim's own interactions, its queued actions and its
    /// cross-object references are rehydrated from the marshal rather than carried across, so a sim
    /// interrupted mid-interaction resumes at a stack boundary rather than inside the interaction.
    /// </para>
    /// </summary>
    public sealed class TransitAvatarBinder : ITransitAvatarBinder
    {
        /// <summary>
        /// The stack a freshly bound avatar's empty thread is given. The stack is grown on demand
        /// (<c>VMThread.Push</c>), so this only has to be non-zero; it is named so that it is not
        /// read as a tuning decision.
        /// </summary>
        public const int ThreadStackSize = 32;

        private static readonly TransitAvatarBinder Shared = new TransitAvatarBinder();

        /// <summary>The binder a caller gets when it does not supply one. Stateless.</summary>
        public static TransitAvatarBinder Default
        {
            get { return Shared; }
        }

        /// <inheritdoc/>
        public bool TryBind(VM transitVm, VM sourceVm, VMEntity traveller, LotTilePos start,
            out VMEntity bound, out string reason)
        {
            bound = null;
            try
            {
                if (transitVm == null)
                {
                    reason = "there is no transit lot to move the travelling sim into";
                    return false;
                }
                if (traveller == null)
                {
                    reason = "there is no travelling sim to move into the transit lot";
                    return false;
                }
                var source = traveller as VMAvatar;
                if (source == null)
                {
                    reason = "the travelling entity is a " + traveller.GetType().Name
                        + " rather than an avatar, so it cannot walk between lots";
                    return false;
                }
                var marshal = source.Save();
                if (!LoadInto(transitVm, marshal, start, out var avatar, out reason))
                {
                    return false;
                }

                //Last, and only once the transit lot certainly has its own copy: a bind that failed
                //half way has to leave the traveller where it was, and deleting it first would take
                //the player's sim away on the strength of a failure.
                DeleteFromSource(source, sourceVm);

                bound = avatar;
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: the travelling sim could not be moved into the transit lot, so"
                    + " this lot walk is a lot switch instead. The lot itself is unaffected.", ex);
                bound = null;
                reason = "moving the travelling sim into the transit lot failed: " + ex.Message;
                return false;
            }
        }

        /// <inheritdoc/>
        public bool TryRebind(VM destinationVm, VMAvatarMarshal carriedMarshal, LotTilePos at,
            out VMEntity bound, out string reason)
        {
            bound = null;
            try
            {
                if (destinationVm == null)
                {
                    reason = "there is no lot to put the travelling sim back into";
                    return false;
                }
                if (carriedMarshal == null)
                {
                    reason = "there is no travelling sim state to put back into that lot";
                    return false;
                }

                if (!LoadInto(destinationVm, carriedMarshal, at, out var avatar, out reason))
                {
                    return false;
                }

                //Now, with the new sim safely in the lot and nothing else moved.
                //
                //The origin lot on a cancelled walk: its avatar was deleted out of it by TryBind,
                //so there is nothing at this persist id here and this is a no-op. The destination
                //lot on an arrival: the load may have spawned a controlled sim of its own under
                //the same id, and two avatars registered at one persist id is the ambiguity B4
                //exists to prevent - so the older registration is dropped.
                //
                //Only the registration, never the entity. The entity stays because it is a person
                //in the house rather than a placeholder: on the template path it is a member of
                //the family the lot activates, and the net driver is watching it. Deleting it
                //would either take a family member away or - if the driver's own client avatar is
                //the one that goes - look to that driver like the local player disconnected.
                //Unregistering is the part that is ours to do, and it is the part that matters.
                RetireStaleControl(destinationVm, carriedMarshal.PersistID, avatar);

                //And then the registration itself, which is what makes the sim findable:
                //GetAvatarByPersist, the lot control's SelectedSimID and the interaction queue all
                //read ObjectQueries' own map rather than walking the entity list, so an avatar
                //missing from it is an avatar nothing can find - which is the first thing a
                //re-bound sim would have run into, and exactly the reason the origin lot loses its
                //controlled sim when a walk is cancelled and gets it back here.
                destinationVm.Context.ObjectQueries.RegisterAvatarPersist(
                    avatar, carriedMarshal.PersistID);

                bound = avatar;
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: the travelling sim could not be put back into the lot it is"
                    + " returning to. The lot itself is unaffected.", ex);
                bound = null;
                reason = "putting the travelling sim back into its lot failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// The half both directions share: an avatar entity built from a marshal, living in
        /// <paramref name="vm"/> at a tile, and made that lot's <c>MyUID</c>. Nothing outside this
        /// method knows the order the steps happen in, and nothing outside <see cref="TryBind"/>
        /// and <see cref="TryRebind"/> knows that registration with the destination's object
        /// queries is deliberately not among them - the transit lot has no net driver to keep a
        /// registration in step with, so only the direction that gives a sim back to a real lot
        /// pays for one.
        /// </summary>
        private static bool LoadInto(VM vm, VMAvatarMarshal marshal, LotTilePos at,
            out VMAvatar avatar, out string reason)
        {
            avatar = null;
            if (vm == null)
            {
                reason = "there is no lot to put the travelling sim into";
                return false;
            }
            if (marshal == null)
            {
                reason = "there is no travelling sim state to put into that lot";
                return false;
            }
            var context = vm.Context;
            if (context == null || context.Blueprint == null)
            {
                reason = "that lot has no context or blueprint to hold a sim in";
                return false;
            }

            var content = FSO.Content.Content.Get();
            if (content == null)
            {
                reason = "there is no content manager, so the avatar template cannot be resolved";
                return false;
            }

            var definition = content.WorldObjects.Get(VMAvatar.TEMPLATE_PERSON)
                ?? content.WorldObjects.Get(marshal.GUID);
            if (definition == null)
            {
                reason = "this install has no avatar template (0x"
                    + VMAvatar.TEMPLATE_PERSON.ToString("X8") + "), so no sim can be carried"
                    + " between lots";
                return false;
            }

            avatar = new VMAvatar(definition);
            avatar.Load(marshal);
            context.Blueprint.AddAvatar((AvatarComponent)avatar.WorldUI);
            avatar.FetchTreeByName(context);
            vm.AddEntity(avatar);
            avatar.Thread = new VMThread(context, avatar, ThreadStackSize);
            vm.Scheduler.ScheduleTickIn(avatar, 1);
            avatar.LoadCrossRef(marshal, context);
            vm.MyUID = marshal.PersistID;
            avatar.Position = at;
            reason = null;
            return true;
        }

        /// <summary>
        /// Unregisters whichever avatar was standing at <paramref name="persistId"/> before
        /// <paramref name="adopted"/> took that id, so exactly one avatar answers to it. A no-op
        /// when there was none, which is the origin-lot case every cancelled walk takes.
        /// </summary>
        private static void RetireStaleControl(VM vm, uint persistId, VMAvatar adopted)
        {
            try
            {
                var context = vm?.Context;
                if (context == null || persistId == 0) return;
                var registered = context.ObjectQueries.AvatarsByPersist[persistId];
                if (registered == null || ReferenceEquals(registered, adopted)) return;
                context.ObjectQueries.RemoveAvatarPersist(persistId);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: a stale controlled sim could not be unregistered before the"
                    + " walked sim took over, so that lot may keep reporting the older one until it"
                    + " reloads. The lot itself is unaffected.", ex);
            }
        }

        /// <inheritdoc/>
        public bool TryUnbind(VM transitVm, VMEntity carried, out VMAvatarMarshal carriedMarshal,
            out string reason)
        {
            carriedMarshal = null;
            try
            {
                if (carried == null)
                {
                    reason = "the transit lot holds no sim to take back";
                    return false;
                }
                var avatar = carried as VMAvatar;
                if (avatar == null)
                {
                    reason = "the transit lot's travelling entity is a " + carried.GetType().Name
                        + " rather than an avatar, so it cannot be carried back to a lot";
                    return false;
                }
                if (transitVm != null && avatar.Thread != null)
                {
                    //A half-stepped sim is worse than an unsimulated one: its stack would resume
                    //inside a BHAV branch that referred to objects this lot is about to lose.
                    avatar.Thread.BlockingState = null;
                    avatar.Thread.EODConnection = null;
                }
                carriedMarshal = avatar.Save();
                if (carriedMarshal == null)
                {
                    reason = "the travelling sim's own state could not be captured, so it cannot be"
                        + " handed to the destination lot";
                    return false;
                }
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: the travelling sim could not be taken back out of the transit"
                    + " lot. The lot itself is unaffected.", ex);
                carriedMarshal = null;
                reason = "taking the travelling sim out of the transit lot failed: " + ex.Message;
                return false;
            }
        }

        private static void DeleteFromSource(VMAvatar source, VM sourceVm)
        {
            if (sourceVm == null) return;
            try
            {
                source.Delete(true, sourceVm.Context);
            }
            catch (Exception ex)
            {
                //The transit lot already holds the sim, so the walk can proceed; what this costs is
                //a stale avatar left in the origin lot, which is why it is a warning rather than a
                //silent one.
                SimitoneLog.Warn("B4: the travelling sim could not be removed from the lot it left,"
                    + " so that lot may still hold a reference to it. The lot itself is unaffected.", ex);
            }
        }
    }

    /// <summary>
    /// B4's transit lot with a <c>VM</c> behind it: a <c>World</c> whose blueprint is the generated
    /// street region around a hop, a <c>VM</c> on it, and the travelling avatar moved into that VM
    /// for the duration of the walk.
    ///
    /// <para>
    /// <b>What this is for.</b> §6 B4 task 1: "exactly one VM owns the avatar at any moment; two
    /// lot VMs sharing one avatar is the failure mode to avoid". The pure half -
    /// <see cref="NeighbourhoodTransitLot"/> - plans the route and holds the progress, but the sim
    /// crossing it is a position rather than a sim, and the lot the player left keeps simulating
    /// underneath the walk. This class is what closes that: the walk moves a real
    /// <c>VMAvatar</c> inside a real VM, and the caller's <c>TransitionTo</c> receives the avatar's
    /// own marshal at the other end rather than a teleport.
    /// </para>
    ///
    /// <para>
    /// <b>Why the transit lot is a sub-world of the lot the player left.</b> A <c>World</c> is a
    /// graphics-device resource and the device the player is looking at belongs to the lot they
    /// are in, so the sub-world is created through <see cref="World.MakeSubWorld"/> on that lot's
    /// world and added to that lot's blueprint - the same two lines
    /// <see cref="SimitoneSurroundingsRestore"/> uses, and for the same reason. It is the only
    /// answer available without standing up a second lot view, and it has the useful side effect
    /// that the origin lot's own terrain, surround and facades are still there to draw the street
    /// the walk is on.
    /// </para>
    ///
    /// <para>
    /// <b>TS1 = false, and why that is right here.</b> The transit lot is built through
    /// <c>VMLotTerrainRestoreTools</c>, every helper of which reads <c>vm.TSOState</c> -
    /// <c>ObjectLimit</c> and <c>Size</c> - and <c>VM.Init</c> chooses the lot state with
    /// <c>PlatformState = TS1 ? VMTS1LotState : VMTSOLotState</c>. A TS1 lot state has no
    /// <c>TSOState</c>, so a TS1-flagged blank street lot throws on the first helper call. So the
    /// *lot* is TSO-shaped, exactly as a surround neighbour's is, and the *sim* is still TS1 -
    /// entity states are chosen by the static <c>VM.GlobTS1</c>, which the parent VM's constructor
    /// has already set from the content manager and which nothing here changes. The avatar arrives
    /// as a <c>VMAvatarMarshal</c> written by a TS1 VM and is rebuilt as a TS1 avatar.
    /// </para>
    ///
    /// <para>
    /// <b>Game thread only, and it says so.</b> §7: a <c>VM</c> is not thread-safe, this builds
    /// one and mutates another, and the only thing entitled to do that is the thread already
    /// bringing a lot up. There is no lock here and no assertion, because this is called from
    /// exactly one place - the lot switch block - and either would be a claim nobody maintains.
    /// </para>
    ///
    /// <para>
    /// <b>It never throws.</b> Every entry point wraps its body, and every refusal is a sentence in
    /// <c>reason</c>. A failure to build the transit lot is a lot switch instead of a walk, which is
    /// B3's behaviour and the one the capability's own description promises; a failure part way
    /// through takes the half-built sub-world down with it, because a <c>SubWorldComponent</c> owns
    /// a light map, an ambient light texture and a world architecture and nothing else would ever
    /// reach them.
    /// </para>
    ///
    /// <para>
    /// <b>Exactly one lot owns the avatar.</b> The invariant is asserted twice on this type, and
    /// both assertions are about the transition rather than about the steady state:
    /// <see cref="CanTakeOwnership"/> refuses to open while the origin lot still owns the avatar, and
    /// <see cref="TransitHandover"/> refuses to complete unless the world names the destination
    /// afterwards. Between the two, the avatar belongs to the transit lot and to nothing else, and
    /// <see cref="HasOwner"/> answers it for a caller that wants to assert rather than trust.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodTransitLotVm : IDisposable
    {
        /// <summary>
        /// Tiles of generated map kept past the walk on every side of the transit lot's blueprint.
        /// </summary>
        public const int DefaultMarginTiles = TransitStreetRegion.DefaultMarginTiles;

        /// <summary>
        /// The <c>vm.TSOState.LotID</c> a transit lot is built with. The helpers use it only as a
        /// seed, and it is <see cref="TransitPath.NoLot"/> because a transit lot is not a numbered
        /// lot - giving it a number would put it in the lot-number space the neighbourhood already
        /// has a meaning for.
        /// </summary>
        public const uint TransitLotId = (uint)TransitPath.NoLot;

        private readonly NeighbourhoodTransitLot _walk;
        private readonly TransitBlueprintPlan _plan;
        private readonly ITransitAvatarBinder _binder;
        private readonly Blueprint _parentBlueprint;

        private SubWorldComponent _subWorld;
        private VM _vm;
        private VMEntity _carried;
        private bool _isOpen;

        private NeighbourhoodTransitLotVm(
            NeighbourhoodTransitLot walk, TransitBlueprintPlan plan, ITransitAvatarBinder binder,
            Blueprint parentBlueprint, SubWorldComponent subWorld, VM vm, VMEntity carried)
        {
            _walk = walk;
            _plan = plan;
            _binder = binder;
            _parentBlueprint = parentBlueprint;
            _subWorld = subWorld;
            _vm = vm;
            _carried = carried;
            _isOpen = true;
        }

        /// <summary>
        /// The walk this lot is walking. Held, not copied: it holds the walk in progress, and a
        /// second copy would be a second answer to "how far has the avatar got".
        /// </summary>
        public NeighbourhoodTransitLot Walk
        {
            get { return _walk; }
        }

        /// <summary>The blueprint decision this lot was built from. Never null on an open lot.</summary>
        public TransitBlueprintPlan Plan
        {
            get { return _plan; }
        }

        /// <summary>The transit lot's own sub-world, or <c>null</c> once it has been taken down.</summary>
        public SubWorldComponent World
        {
            get { return _subWorld; }
        }

        /// <summary>The VM on the transit lot, or <c>null</c> once it has been taken down.</summary>
        public VM Vm
        {
            get { return _vm; }
        }

        /// <summary>
        /// The travelling avatar as it exists inside the transit lot, or <c>null</c>. Null after a
        /// handover or a cancel, because at that point the sim is in the caller's hands as a
        /// marshal and the entity is dead with its lot.
        /// </summary>
        public VMEntity Avatar
        {
            get { return _carried; }
        }

        /// <summary>The walk's state, from the pure half. A snapshot, rebuilt on every read.</summary>
        public TransitState State
        {
            get { return _walk.State; }
        }

        /// <summary>Which kind of lot owns the avatar. <see cref="ActiveLotKind.Transit"/> while
        /// this lot is open.</summary>
        public ActiveLotKind Kind
        {
            get { return _walk.Kind; }
        }

        /// <summary>
        /// The lot that owns the avatar, or <see cref="TransitPath.NoLot"/>. Zero for the whole of
        /// the walk, because the avatar is on the street in no numbered lot at all - which is the
        /// state <see cref="NeighbourhoodTransitLot.IsExactlyOneActiveLot"/> requires of
        /// <see cref="ActiveLotKind.Transit"/> and the reason the transit lot exists.
        /// </summary>
        public int ActiveLotNumber
        {
            get { return _walk.ActiveLotNumber; }
        }

        /// <summary>Whether the wrapped walk's own bookkeeping satisfies the one-owner invariant.</summary>
        public bool HasOwner
        {
            get { return _walk.HasOwner; }
        }

        /// <summary>Whether a walk is in progress in this lot.</summary>
        public bool IsWalking
        {
            get { return _isOpen && _walk.IsWalking; }
        }

        /// <summary>Whether the avatar is on the destination's door and has not been handed over.</summary>
        public bool IsArrived
        {
            get { return _isOpen && _walk.State.IsArrived; }
        }

        /// <summary>
        /// The walk-side half of <see cref="TryOpen"/>: may this transit lot take the avatar at all?
        /// Everything here is a question about the walk and needs no VM, a device or a content
        /// manager, which is what makes B4 task 5's guard a test rather than a belief.
        /// </summary>
        /// <param name="walk">The pure transit lot the caller has already begun a walk on.</param>
        /// <param name="reason">Why the transit lot may not take the avatar, or <c>null</c>.</param>
        /// <returns>Whether the walk is one a transit lot may own.</returns>
        public static bool CanTakeOwnership(NeighbourhoodTransitLot walk, out string reason)
        {
            if (walk == null)
            {
                reason = "there is no transit lot planning a walk";
                return false;
            }
            if (walk.Kind != ActiveLotKind.Transit || walk.ActiveLotNumber != TransitPath.NoLot)
            {
                //The one refusal that is the invariant itself. A walk that has not begun still has
                //the origin lot owning the avatar, and building a second VM now would give the
                //sim two homes.
                reason = "the walk has not been begun, so lot " + walk.ActiveLotNumber
                    + " still owns the avatar and a transit lot may not also hold it";
                return false;
            }
            if (!NeighbourhoodTransitLot.IsExactlyOneActiveLot(walk.Kind, walk.ActiveLotNumber,
                out reason))
            {
                return false;
            }
            if (!walk.IsWalking)
            {
                reason = "there is no walk in progress for a transit lot to own";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>
        /// Builds the transit lot, moves the travelling avatar into it, and takes ownership of the
        /// walk - or <c>false</c> with the reason, having taken back anything it had built.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>All the refusals happen before anything is allocated</b>, and they are in the order
        /// that decides the least: whether a transit lot may own the walk at all, then the parent
        /// lot, then the lot view and its device, then the plan. A call that is refused has left
        /// the session exactly as it found it.
        /// </para>
        /// <para>
        /// <b>Failure after the sub-world exists disposes it here.</b> This method is the only
        /// holder of the reference between <see cref="World.MakeSubWorld"/> and the return, so
        /// nothing outside it would ever reach a half-built transit lot's light map.
        /// </para>
        /// <para>
        /// <b>What remains undone, and it is not small.</b> The avatar moves and the street is
        /// built, but the walk does not yet <em>drive</em> the sim: <see cref="Advance"/> sets the
        /// entity's position rather than issuing a route, so the sim is carried along the pavement
        /// instead of walking it, its route stack is empty, and it will not stop at a kerb or turn
        /// a corner on its own. Issuing real <c>VMRouting</c> waypoints from the transit blueprint
        /// needs the routing grid that lot's own <c>VMArchitecture</c> builds, which needs the
        /// street lot to be a real routable lot rather than a TSO-shaped blank one - see
        /// <see cref="ITransitAvatarBinder"/>'s remarks for the rest.
        /// </para>
        /// </remarks>
        /// <param name="parentVm">The lot the player is standing in. Required.</param>
        /// <param name="map">The generated map. Required.</param>
        /// <param name="walk">A transit lot on which the caller has already begun the walk.</param>
        /// <param name="binder">
        /// The avatar mover, or <c>null</c> for <see cref="TransitAvatarBinder.Default"/>.
        /// </param>
        /// <param name="transit">The transit lot, or <c>null</c> when there is none.</param>
        /// <param name="reason">Why there is no transit lot, or <c>null</c> when there is one.</param>
        /// <returns>Whether a transit lot is now open and owns the avatar.</returns>
        public static bool TryOpen(
            VM parentVm, NeighbourhoodMap map, NeighbourhoodTransitLot walk,
            ITransitAvatarBinder binder, out NeighbourhoodTransitLotVm transit, out string reason)
        {
            transit = null;
            if (!CanTakeOwnership(walk, out reason)) return false;

            try
            {
                if (parentVm == null)
                {
                    reason = "there is no lot to walk out of";
                    return false;
                }
                var context = parentVm.Context;
                if (context == null || context.Blueprint == null)
                {
                    reason = "the lot the player is standing in has no context or blueprint";
                    return false;
                }
                if (!VM.UseWorld)
                {
                    reason = "this host has no lot view, so a transit lot has nothing to be drawn in";
                    return false;
                }
                var parentWorld = context.World;
                if (parentWorld == null || parentWorld.State == null || parentWorld.State.Device == null)
                {
                    reason = "the lot view has no graphics device, so a transit lot cannot be"
                        + " created";
                    return false;
                }

                var avatarBinder = binder ?? TransitAvatarBinder.Default;
                var traveller = parentVm.GetAvatarByPersist(parentVm.MyUID);
                if (traveller == null)
                {
                    reason = "the controlled sim has not been spawned in the lot the player is"
                        + " standing in, so there is nobody to walk";
                    return false;
                }

                TransitBlueprintPlan plan;
                var originLot = walk.Query.Layout.FindLot(OriginLotOf(walk));
                if (!TransitBlueprintPlan.TryCreate(map, originLot, walk.State.Path,
                    DefaultMarginTiles, out plan, out reason))
                {
                    return false;
                }

                return TryBuild(parentVm, parentWorld, context.Blueprint, walk, plan, avatarBinder,
                    traveller, out transit, out reason);
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: a transit lot could not be built, so this lot walk is a lot"
                    + " switch instead. The lot itself is unaffected.", ex);
                transit = null;
                reason = "the transit lot could not be built: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Walks the avatar <paramref name="distanceTiles"/> further along the route, and reports
        /// whether that step put it on the destination's door.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The avatar is moved, not routed.</b> The entity's <c>Position</c> is written from
        /// <see cref="TransitBlueprintPlan.LocalTileFor"/> every step, so the sim is on the
        /// pavement the camera is following, in the lot that owns it, with its motives and its
        /// appearance intact. What it is not doing is <em>walking</em>: see
        /// <see cref="TryOpen"/>'s remarks for what driving it properly would need.
        /// </para>
        /// <para>
        /// <b>Sticky arrival is the transit lot's, not this class's.</b> Once the walk has arrived
        /// every further step reports arrival again and moves nothing, so a caller whose fixed-step
        /// loop catches up twice cannot walk the sim past the door.
        /// </para>
        /// <para>
        /// A step on a lot that is not open, or with a step that is not a positive number, is a
        /// no-op that reports no arrival. A failure to move the entity is logged and the walk still
        /// advances, because a walk with a sim whose position could not be written is a worse answer
        /// than a walk the player is watching from the wrong tile.
        /// </para>
        /// </remarks>
        /// <param name="distanceTiles">How far to walk, in tiles.</param>
        /// <returns>Whether the avatar is on the destination's door after the step.</returns>
        public bool Advance(double distanceTiles)
        {
            if (!_isOpen) return false;
            try
            {
                var arrived = _walk.Advance(distanceTiles);
                var avatar = _carried;
                if (avatar != null)
                {
                    //The position is written from the state the advance produced, so the sim and
                    //the camera can never be looking at two different points of the walk.
                    avatar.Position = _plan.LocalTileFor(_walk.State.Current);
                }
                return arrived;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: a step of the lot walk could not be applied to the transit"
                    + " lot's sim. The walk itself is unaffected.", ex);
                return false;
            }
        }

        /// <summary>
        /// Ends the walk: hands the travelling sim back to the caller, names the destination as the
        /// world's active lot and takes the transit lot's own world down.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The caller's job, stated here.</b> What comes back is an avatar marshal, not a
        /// switched lot. The caller runs its own <c>TransitionTo(destination)</c> - B3's one switch
        /// path, the same call a discrete arrival makes - hands it this marshal so the arriving sim
        /// is the one that walked rather than a fresh spawn, and stashes or unloads the origin lot.
        /// This class does none of that, because the one-switch-path rule (§3) means it must not be
        /// duplicated.
        /// </para>
        /// <para>
        /// <b>Refused means nothing has changed.</b> The sim is still in the transit lot, the world
        /// is still open, and the caller may retry, cancel or fall back to a switch. In particular a
        /// refused handover never disposes the world, because a disposed world with the sim still
        /// in it is the one outcome this type exists to prevent.
        /// </para>
        /// </remarks>
        /// <param name="destinationLot">The lot the player arrives in, or
        /// <see cref="TransitPath.NoLot"/> on a refusal.</param>
        /// <param name="avatar">The sim's own state, for the caller's transition, or
        /// <c>null</c> on a refusal.</param>
        /// <param name="reason">Why the walk was not completed, or <c>null</c> when it was.</param>
        /// <returns>Whether the walk was completed and the transit lot taken down.</returns>
        public bool TryHandOver(out int destinationLot, out VMAvatarMarshal avatar, out string reason)
        {
            destinationLot = TransitPath.NoLot;
            avatar = null;
            if (!_isOpen)
            {
                reason = "there is no transit lot to hand the sim over from";
                return false;
            }
            try
            {
                var decision = TransitHandover.For(_walk.State, _carried != null);
                if (!decision.Allowed)
                {
                    reason = decision.Reason;
                    return false;
                }

                VMAvatarMarshal carried;
                if (!_binder.TryUnbind(_vm, _carried, out carried, out reason))
                {
                    return false;
                }

                if (!_walk.CompleteTransit(out reason))
                {
                    //Arrival is sticky, so this is structurally unreachable. It is still checked,
                    //because a walk that has arrived and then failed to complete would be advanced
                    //again by the next frame forever, and the refusal at least stops that.
                    SimitoneLog.Warn("B4: a lot walk reached the end of its route but did not"
                        + " complete, so no lot switch was made: " + reason);
                    return false;
                }

                //Only now, with the sim in the caller's hands and the world naming the destination.
                Shutdown();
                destinationLot = decision.Destination;
                avatar = carried;
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: the travelling sim could not be handed over, so no lot switch"
                    + " was made and the transit lot is still open. The lot itself is unaffected.", ex);
                destinationLot = TransitPath.NoLot;
                avatar = null;
                reason = "the handover failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Abandons the walk: hands the travelling sim back to the caller and puts the world's
        /// bookkeeping back where it was before the walk started.
        /// </summary>
        /// <remarks>
        /// This is the path a failed transition takes and the path a player who changes their mind
        /// takes. The sim comes back as a marshal exactly as in <see cref="TryHandOver"/>, so the
        /// caller's fallback is the same either way - and because the origin lot's own walk state is
        /// what gets restored, <see cref="Kind"/> is a
        /// <see cref="ActiveLotKind.Hollow"/> again for a walk that started from one.
        /// </remarks>
        /// <param name="avatar">The sim's own state, or <c>null</c> when the transit lot holds
        /// none.</param>
        /// <param name="reason">Why the walk was not cancelled, or <c>null</c> when it was.</param>
        /// <returns>Whether the walk was cancelled and the transit lot taken down.</returns>
        public bool TryCancel(out VMAvatarMarshal avatar, out string reason)
        {
            VMEntity ignored;
            return TryCancel(null, out avatar, out ignored, out reason);
        }

        /// <summary>
        /// Abandons the walk <em>and</em> puts the travelling sim back into the lot it was taken
        /// out of, which is what a cancelled walk owes that lot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why a caller needs this rather than the two-argument form.</b> This type's
        /// <see cref="ITransitAvatarBinder.TryBind"/> <em>moved</em> the sim out of the origin lot
        /// when the walk began - two lot VMs holding one sim is the failure mode B4 exists to
        /// prevent - and the two-argument <see cref="TryCancel(out VMAvatarMarshal, out string)"/>
        /// only gives it back as a marshal. Handing a caller a marshal it cannot load anywhere is
        /// how the origin lot ends up with no controlled sim at all, which is exactly what a
        /// cancelled walk must not do. This overload closes that: it unbinds, then hands the
        /// marshal straight back through
        /// <see cref="ITransitAvatarBinder.TryRebind"/>, and the origin lot has its sim again.
        /// </para>
        /// <para>
        /// <b>A null <paramref name="destinationVm"/> is the old behaviour exactly</b>, which is
        /// what the two-argument overload delegates to, so a caller with nowhere to put the sim
        /// back is no worse off than before.
        /// </para>
        /// <para>
        /// <b>The walk still comes down even when the sim cannot go back.</b> A transit lot left
        /// open holds a live <c>SubWorldComponent</c>, a light map and an architecture for the rest
        /// of the session, and the sim is already in the caller's hands as a marshal, so there is
        /// nothing a re-bind failure leaves behind that a refusal would have saved. The failure is
        /// therefore reported through <paramref name="restored"/> being null and through
        /// <paramref name="reason"/>, not by refusing to cancel.
        /// </para>
        /// </summary>
        /// <param name="destinationVm">
        /// The lot the sim came out of, which gets it back; or <c>null</c> to hand the marshal to
        /// the caller alone.
        /// </param>
        /// <param name="avatar">The sim's own state, or <c>null</c> when the transit lot holds
        /// none.</param>
        /// <param name="restored">
        /// The sim as an entity in <paramref name="destinationVm"/>, or <c>null</c> when there was
        /// nowhere to put it or it could not be put there.
        /// </param>
        /// <param name="reason">Why the walk was not cancelled, or <c>null</c> when it was.</param>
        /// <returns>Whether the walk was cancelled and the transit lot taken down.</returns>
        public bool TryCancel(VM destinationVm, out VMAvatarMarshal avatar, out VMEntity restored,
            out string reason)
        {
            avatar = null;
            restored = null;
            if (!_isOpen)
            {
                reason = "there is no transit lot to cancel";
                return false;
            }
            try
            {
                var state = _walk.State;
                var restoredTo = (_plan == null || state.Path == null || !state.IsValid)
                    ? LotTilePos.OUT_OF_WORLD
                    : _plan.LocalTileFor(state.Path.Start);
                if (_carried != null)
                {
                    VMAvatarMarshal carried;
                    if (!_binder.TryUnbind(_vm, _carried, out carried, out reason))
                    {
                        return false;
                    }
                    avatar = carried;

                    if (destinationVm != null)
                    {
                        //Back where it started, on the pavement outside the origin lot's door -
                        //the walk's own start waypoint, which is a tile of the transit lot's
                        //blueprint and therefore the origin lot's door as well. Anything else would
                        //be a tile that only means something to a lot nobody is in any more.
                        VMEntity back;
                        string rebindReason;
                        if (!_binder.TryRebind(destinationVm, carried, restoredTo, out back,
                            out rebindReason))
                        {
                            SimitoneLog.Warn("B4: the travelling sim could not be put back into the"
                                + " lot it was walking out of, so that lot is left without its"
                                + " controlled sim (" + rebindReason + "). The lot itself is"
                                + " otherwise unaffected.");
                            reason = rebindReason;
                        }
                        else
                        {
                            restored = back;
                        }
                    }
                }
                if (!_walk.CancelTransit(out var cancelReason))
                {
                    reason = cancelReason;
                    return false;
                }
                Shutdown();
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: the lot walk could not be cancelled. The lot itself is"
                    + " unaffected.", ex);
                avatar = null;
                restored = null;
                reason = "the cancellation failed: " + ex.Message;
                return false;
            }
        }

        /// <summary>
        /// Takes the transit lot down, returning the travelling sim to the caller if one is here.
        /// Idempotent, and never throws.
        /// </summary>
        /// <remarks>
        /// What a lot switch and <c>CleanupLastWorld</c>/<c>DisposeOnLot</c> call. A teardown that
        /// found no VM has still got a walk in flight if anything went wrong on the way up, and a
        /// walk left in flight would arrive into a lot that no longer exists. The sim is taken out
        /// before the world goes, so a caller that reads the log after a failed teardown can at
        /// least know the player was on the pavement rather than inside two lots.
        /// </remarks>
        public void Dispose()
        {
            if (!_isOpen) return;
            try
            {
                VMAvatarMarshal carried;
                string reason;
                if (_carried != null && !_binder.TryUnbind(_vm, _carried, out carried, out reason))
                {
                    SimitoneLog.Warn("B4: the travelling sim could not be taken back out of the"
                        + " transit lot before it was taken down: " + reason);
                }
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: taking the travelling sim back out of the transit lot threw,"
                    + " so the transit lot is being taken down without it. The lot itself is"
                    + " unaffected.", ex);
            }
            Shutdown();
        }

        /// <summary>One line for a log, or for a failing assertion's message.</summary>
        public string Describe()
        {
            if (!_isOpen) return "no transit lot (taken down)";
            return "transit lot owning the avatar, " + _plan.Describe() + ". " + _walk.Describe();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }

        // ---------------------------------------------------------------- building

        private static bool TryBuild(
            VM parentVm, World parentWorld, Blueprint parentBlueprint,
            NeighbourhoodTransitLot walk, TransitBlueprintPlan plan, ITransitAvatarBinder binder,
            VMEntity traveller, out NeighbourhoodTransitLotVm transit, out string reason)
        {
            transit = null;
            var side = plan.Side;
            var device = parentWorld.State.Device;

            SubWorldComponent subWorld = null;
            VM transitVm = null;
            try
            {
                subWorld = parentWorld.MakeSubWorld(device);
                subWorld.Initialize(device);
                subWorld.State.Level = SimitoneSurroundingsRestore.SubWorldLevel;
                //The tile delta from the origin lot's own origin, unsigned - the same rule and the
                //same sign as every surround neighbour, with the street region in place of the lot.
                subWorld.GlobalPosition = plan.SubWorldOrigin;

                transitVm = new VM(
                    new VMContext(subWorld),
                    new VMServerDriver(new VMTSOGlobalLinkStub()),
                    new VMNullHeadlineProvider());
                //Before Init, which is where the lot state is chosen. See the type remarks: the
                //street is built through TSO-shaped helpers, and the travelling sim is TS1 anyway
                //because entity states follow the static VM.GlobTS1.
                transitVm.TS1 = false;
                transitVm.Init();
                if (transitVm.TSOState == null)
                {
                    reason = "the transit lot did not get a TSO lot state, so its street cannot be"
                        + " built out of the generated map";
                    return false;
                }
                transitVm.TSOState.LotID = TransitLotId;
                transitVm.TSOState.Size =
                    SimitoneSurroundingsRestore.BuildableSizeIndexFor(side) | (int)plan.LotDirection << 16;

                var blueprint = new Blueprint(side, side);
                transitVm.Context.Blueprint = blueprint;
                subWorld.InitBlueprint(blueprint);

                transitVm.Context.Architecture =
                    new VMArchitecture(side, side, blueprint, transitVm.Context);
                transitVm.Context.Architecture.EmptyRoomMap();
                transitVm.Context.RegeneratePortalInfo();

                //A sub-world never makes its own terrain component, and a blueprint without one
                //draws no ground at all.
                var terrainComponent = new TerrainComponent(
                    new Rectangle(0, 0, side, side), blueprint);
                terrainComponent.Initialize(subWorld.State.Device, subWorld.State);
                blueprint.Terrain = terrainComponent;

                //Non-zero, or RestoreTerrain overwrites the direction set two lines above with
                //PickRoadDir and the landmark row lands on the wrong pavement.
                transitVm.TSOState.ObjectLimit = SimitoneSurroundingsRestore.BlankObjectLimit;

                VMLotTerrainRestoreTools.RestoreTerrain(transitVm, plan.Terrain.Blend,
                    plan.Terrain.Roads, RestoreLotType.Blank);
                //RestoreHeight writes the lot's own footprint at h * 100 - baseLevel, so the lot's
                //ground lands at about zero and the only thing that decides where the transit lot
                //hangs relative to the origin lot is BaseAlt - which is the plan's arithmetic over
                //two samples of the same map, not this call's return value. Both are written so the
                //reader can see which number is load bearing.
                VMLotTerrainRestoreTools.RestoreHeight(
                    transitVm, plan.Terrain.ToSurroundingTerrain(), 1, 1);
                blueprint.BaseAlt = plan.BaseAltitude;

                //The blank branch's landmark pass, and the rest of a blank lot's first tick, in the
                //order SimitoneSurroundingsRestore.BuildBlank uses: the room map and the portal
                //list were built against an architecture that terrain has since rewritten, and
                //lighting was never built at all.
                VMLotTerrainRestoreTools.EnsureCoreObjects(transitVm, RestoreLotType.Blank);
                transitVm.Context.Architecture.ClearDirty();
                transitVm.Context.Architecture.RegenRoomMap();
                transitVm.Context.RegeneratePortalInfo();
                transitVm.Context.RefreshAllLighting();
                transitVm.Tick();
                subWorld.CalculateFloorsUsed();
                blueprint.BaseAlt = plan.BaseAltitude;

                VMEntity carried;
                if (!binder.TryBind(transitVm, parentVm, traveller,
                    plan.LocalTileFor(walk.State.Path.Start), out carried, out reason))
                {
                    return false;
                }

                //Added to the parent's list, then the sky bounds measured over the whole set. In
                //that order: the bounds walk every sub-world's altitude, so they are stale the
                //moment one is added, and the parent's own box is the only one in the set that is
                //not ours.
                parentBlueprint.SubWorlds.Add(subWorld);
                parentWorld.InitSubWorlds();

                transit = new NeighbourhoodTransitLotVm(walk, plan, binder,
                    parentBlueprint, subWorld, transitVm, carried);
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                //The only holder of the reference to a transit lot that failed before it came back.
                SimitoneLog.Warn("B4: a transit lot failed while it was being built and has been"
                    + " taken down. The lot itself is unaffected.", ex);
                TryDispose(subWorld);
                transit = null;
                reason = "the transit lot could not be built: " + ex.Message;
                return false;
            }
        }

        private void Shutdown()
        {
            _isOpen = false;
            _carried = null;
            _vm = null;
            TryDispose(_subWorld);
            try
            {
                if (_parentBlueprint != null && _parentBlueprint.SubWorlds != null && _subWorld != null)
                {
                    //The list is shared across lot switches - VMContext hands the old blueprint's
                    //list to the new one - so an entry left behind would be drawn for the rest of
                    //the session and nothing else would ever reach it.
                    _parentBlueprint.SubWorlds.Remove(_subWorld);
                }
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("B4: the transit lot's sub-world could not be taken off the parent"
                    + " lot's blueprint: " + ex.Message, ex);
            }
            _subWorld = null;
        }

        private static void TryDispose(SubWorldComponent subWorld)
        {
            if (subWorld == null) return;
            try
            {
                subWorld.Dispose();
            }
            catch (Exception ex)
            {
                SimitoneLog.Warn("A transit lot's sub-world could not be disposed: " + ex.Message, ex);
            }
        }

        private static int OriginLotOf(NeighbourhoodTransitLot walk)
        {
            return walk == null ? TransitPath.NoLot : walk.State.Origin;
        }
    }
}
