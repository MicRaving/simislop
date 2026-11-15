/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Which edge of a lot's rectangle the driveway is on, and therefore which way the road graph
    /// runs a spur away from it.
    ///
    /// <para>
    /// Compass-ordered, and deliberately <b>not</b> the same numbering as <see cref="RoadMask"/>'s
    /// nibble bits. M1 (<c>NeighbourhoodLayoutBuilder</c>, a different milestone) decides this
    /// value from the lot's <c>DoorSide</c>; it is stored in the sidecar as a small integer, and
    /// there is nothing to gain from making that stored integer mean "bit 0 of a road mask".
    /// Keeping the two apart means a change to one cannot silently reinterpret the other, and
    /// <see cref="DeltaX"/> / <see cref="DeltaY"/> are the only place that translates between the
    /// stored value and a direction.
    /// </para>
    /// </summary>
    public static class NeighbourhoodDoorSide
    {
        /// <summary>The edge at the low-y side of the rectangle; the driveway runs to -y.</summary>
        public const byte North = 0;

        /// <summary>The edge at the high-x side; the driveway runs to +x.</summary>
        public const byte East = 1;

        /// <summary>The edge at the high-y side; the driveway runs to +y.</summary>
        public const byte South = 2;

        /// <summary>The edge at the low-x side; the driveway runs to -x.</summary>
        public const byte West = 3;

        /// <summary>
        /// The x component of a door side's outward normal: +1, -1, or 0 for a horizontal edge.
        /// </summary>
        /// <param name="side">A door side.</param>
        public static int DeltaX(byte side)
        {
            switch (Clamp(side))
            {
                case East: return 1;
                case West: return -1;
                default: return 0;
            }
        }

        /// <summary>The y component of a door side's outward normal: +1, -1, or 0.</summary>
        /// <param name="side">A door side.</param>
        public static int DeltaY(byte side)
        {
            switch (Clamp(side))
            {
                case South: return 1;
                case North: return -1;
                default: return 0;
            }
        }

        /// <summary>
        /// Folds an out-of-range value into the four real sides. A sidecar written by a build that
        /// knew a fifth edge should produce a working neighbourhood, not an index crash.
        /// </summary>
        /// <param name="side">A door side, possibly out of range.</param>
        public static byte Clamp(byte side)
        {
            return side <= West ? side : (byte)North;
        }

        /// <summary>A readable name, for logs and failure messages.</summary>
        /// <param name="side">A door side.</param>
        public static string Name(byte side)
        {
            switch (Clamp(side))
            {
                case North: return "North";
                case East: return "East";
                case South: return "South";
                default: return "West";
            }
        }
    }

    /// <summary>
    /// One lot, as far as the generator is concerned: where its square sits in the generated
    /// world, what its ground is, how high it sits, and which way its driveway leaves.
    ///
    /// <para>
    /// <b>This is the seam to M1.</b> It is deliberately <em>not</em>
    /// <c>NeighbourhoodLayout</c>: M1's type is owned by a parallel milestone that also owns the
    /// sidecar, the store and <c>INeighbourhoodWorld</c>, and M2's tests must be able to run with
    /// a canned list of lots and no I/O at all. So M1's layout is projected onto a list of these
    /// (a dozen field copies) and nothing else crosses.
    /// </para>
    ///
    /// <para>
    /// The field set is what §6.1-§6.3 of the plan actually consume. <c>MapX</c>/<c>MapY</c>
    /// arrived in Part 8 and are the 2D bitmap position the lot's rectangle was derived from -
    /// carried so the camera, the 2D buttons and the hit test can name the same place the
    /// generator put the rectangle, and so a diagnostic can quote the number
    /// <c>LotLocations.iff</c> actually holds. <c>Rotation</c> (0 for Scope A - the rectangle is
    /// axis aligned either way) and <c>HouseHash</c> (the layout's own cache key; the generator has
    /// no cache) are still absent.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodLotInput
    {
        /// <summary>The TS1 lot number. The stable identity of the lot in every diagnostic.</summary>
        public short Number { get; }

        /// <summary>
        /// The lot's x on the 2D neighbourhood bitmap, from <c>LotLocations.iff</c> - a
        /// <em>centre</em>, not an origin, and not in tile space. See
        /// <see cref="NeighbourhoodPixelTransform"/>.
        /// </summary>
        /// <remarks>
        /// Zero when the caller had no bitmap position to give, which is the same "no position" the
        /// layout builder reads as a lot it must clamp. Nothing derives a rectangle from it here.
        /// </remarks>
        public int MapX { get; }

        /// <summary>The lot's y on the 2D neighbourhood bitmap. See <see cref="MapX"/>.</summary>
        public int MapY { get; }

        /// <summary>Left edge of the lot's square, in generated-map tiles.</summary>
        public int WorldX { get; }

        /// <summary>Top edge of the lot's square, in generated-map tiles.</summary>
        public int WorldY { get; }

        /// <summary>
        /// Side of the lot's square, in tiles. A TS1 lot is square (<c>SIMI.GlobalData[23]</c>), so
        /// one value is the whole footprint. See <see cref="EffectiveSize"/> for the clamped form
        /// every consumer is expected to use.
        /// </summary>
        public int Size { get; }

        /// <summary>
        /// The lot's zoning: <see cref="LotZoning.ZoneResidential"/>,
        /// <see cref="LotZoning.ZoneCommunity"/> or <see cref="LotZoning.ZoneStudio"/>. Used by
        /// the terrain fill, not by the road graph.
        /// </summary>
        public short Zone { get; }

        /// <summary>
        /// The lot's own ground, as a <see cref="NeighbourhoodTerrain"/> index - normally the
        /// same mapping <c>VMTS1Activator.HouseNumToType</c> applies to the lot's own terrain, so
        /// the generated surroundings agree with what you see when you walk in.
        /// </summary>
        public byte TerrainType { get; }

        /// <summary>
        /// The lot's ground height, already quantised onto the <c>CityMapData.ElevationData</c>
        /// scale (1/12 of a tile per unit). The lot's whole interior and its one-tile border ring
        /// take this value, and it is the Dirichlet boundary the seam solver interpolates between.
        /// </summary>
        public byte BaseHeight { get; }

        /// <summary>Which edge the driveway leaves from; see <see cref="NeighbourhoodDoorSide"/>.</summary>
        public byte DoorSide { get; }

        /// <summary>
        /// The lot's own decoded <c>ARRY(0)</c> height plane, or
        /// <see cref="SimitoneArryAltitude.None"/> when there is none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is what makes the generated ground the lot's real shape rather than a plane.</b>
        /// <see cref="BaseHeight"/> is a single median, so a map solved only from it pins every lot
        /// interior flat; the lot's real per-tile grading lives in its own <c>HouseNN.iff</c> as its
        /// <c>ARRY(0)</c> plane and nowhere else. The seam solver reads this field and stamps the
        /// interior with it, which is what gives the terrain the relief the player walks on. A lot
        /// with no plane falls back to <see cref="BaseHeight"/> exactly as before, so a canned lot
        /// list and a lot with no house file are unchanged.
        /// </para>
        /// <para>
        /// It is a value and not a reference to a file: the plane is read once, by the generator,
        /// through a reader that is a parameter rather than ambient state, so a test can hand in a
        /// known plane with no install present.
        /// </para>
        /// </remarks>
        public SimitoneArryAltitude Arry { get; }

        /// <summary>
        /// Whether the lot still has a house file. False means bulldozed: the plot is still
        /// generated (its ground and its border ring exist, because the ground is still there)
        /// but it gets no driveway, so no road is drawn to an empty plot.
        /// </summary>
        public bool HasHouse { get; }

        /// <summary>
        /// Creates a lot record with no 2D bitmap position behind it - the shape every canned lot
        /// list in the tests and every caller that only knows tile space uses.
        /// </summary>
        /// <param name="number">The TS1 lot number.</param>
        /// <param name="worldX">Left edge, in tiles.</param>
        /// <param name="worldY">Top edge, in tiles.</param>
        /// <param name="size">Side of the square, in tiles.</param>
        /// <param name="zone">A <c>LotZoning</c> zone value.</param>
        /// <param name="terrainType">A <see cref="NeighbourhoodTerrain"/> index.</param>
        /// <param name="baseHeight">Height on the <c>ElevationData</c> scale.</param>
        /// <param name="doorSide">A <see cref="NeighbourhoodDoorSide"/> value.</param>
        /// <param name="hasHouse">Whether the lot still has a house.</param>
        public NeighbourhoodLotInput(
            short number,
            int worldX,
            int worldY,
            int size,
            short zone,
            byte terrainType,
            byte baseHeight,
            byte doorSide,
            bool hasHouse)
            : this(number, 0, 0, worldX, worldY, size, zone, terrainType, baseHeight, doorSide, hasHouse)
        {
        }

        /// <summary>
        /// Creates a lot record that knows both where the lot is on the 2D bitmap and where its
        /// rectangle ended up. The overload every real lot takes, because
        /// <c>LotLocations.iff</c> is where the position comes from and dropping it at this seam is
        /// exactly the bug Part 8 §8.2 names.
        /// </summary>
        /// <param name="number">The TS1 lot number.</param>
        /// <param name="mapX">Bitmap x, the lot's centre.</param>
        /// <param name="mapY">Bitmap y, the lot's centre.</param>
        /// <param name="worldX">Left edge, in tiles.</param>
        /// <param name="worldY">Top edge, in tiles.</param>
        /// <param name="size">Side of the square, in tiles.</param>
        /// <param name="zone">A <c>LotZoning</c> zone value.</param>
        /// <param name="terrainType">A <see cref="NeighbourhoodTerrain"/> index.</param>
        /// <param name="baseHeight">Height on the <c>ElevationData</c> scale.</param>
        /// <param name="doorSide">A <see cref="NeighbourhoodDoorSide"/> value.</param>
        /// <param name="hasHouse">Whether the lot still has a house.</param>
        public NeighbourhoodLotInput(
            short number,
            int mapX,
            int mapY,
            int worldX,
            int worldY,
            int size,
            short zone,
            byte terrainType,
            byte baseHeight,
            byte doorSide,
            bool hasHouse)
            : this(number, mapX, mapY, worldX, worldY, size, zone, terrainType, baseHeight,
                doorSide, hasHouse, SimitoneArryAltitude.None)
        {
        }

        /// <summary>
        /// <see cref="NeighbourhoodLotInput(short,int,int,int,int,int,short,byte,byte,byte,bool)"/>
        /// with the lot's own decoded <c>ARRY(0)</c> plane, so the seam solver can grade the
        /// interior with the lot's real heights rather than pinning it to the median.
        /// </summary>
        /// <param name="number">The TS1 lot number.</param>
        /// <param name="mapX">Bitmap x, the lot's centre.</param>
        /// <param name="mapY">Bitmap y, the lot's centre.</param>
        /// <param name="worldX">Left edge, in tiles.</param>
        /// <param name="worldY">Top edge, in tiles.</param>
        /// <param name="size">Side of the square, in tiles.</param>
        /// <param name="zone">A <c>LotZoning</c> zone value.</param>
        /// <param name="terrainType">A <see cref="NeighbourhoodTerrain"/> index.</param>
        /// <param name="baseHeight">Height on the <c>ElevationData</c> scale.</param>
        /// <param name="doorSide">A <see cref="NeighbourhoodDoorSide"/> value.</param>
        /// <param name="hasHouse">Whether the lot still has a house.</param>
        /// <param name="arry">The lot's own <c>ARRY(0)</c> plane, or <c>None</c>.</param>
        public NeighbourhoodLotInput(
            short number,
            int mapX,
            int mapY,
            int worldX,
            int worldY,
            int size,
            short zone,
            byte terrainType,
            byte baseHeight,
            byte doorSide,
            bool hasHouse,
            SimitoneArryAltitude arry)
        {
            Number = number;
            MapX = mapX;
            MapY = mapY;
            WorldX = worldX;
            WorldY = worldY;
            Size = size;
            Zone = zone;
            TerrainType = terrainType;
            BaseHeight = baseHeight;
            DoorSide = NeighbourhoodDoorSide.Clamp(doorSide);
            HasHouse = hasHouse;
            Arry = arry;
        }

        /// <summary>This lot with a different <c>ARRY(0)</c> plane and everything else unchanged.</summary>
        /// <param name="arry">The plane to carry, or <c>None</c>.</param>
        public NeighbourhoodLotInput WithArry(SimitoneArryAltitude arry)
        {
            return new NeighbourhoodLotInput(Number, MapX, MapY, WorldX, WorldY, Size, Zone,
                TerrainType, BaseHeight, DoorSide, HasHouse, arry);
        }

        /// <summary>
        /// <see cref="Size"/> clamped to at least one tile. A zero or negative size is not a real
        /// lot, but a corrupt sidecar can produce one, and a rectangle of negative width would
        /// make every downstream loop misbehave quietly. Every consumer uses this rather than
        /// <see cref="Size"/> so the road graph, the lot mask and the seam solver all agree on
        /// the rectangle they are talking about.
        /// </summary>
        public int EffectiveSize
        {
            get { return Size < 1 ? 1 : Size; }
        }

        /// <summary>First x tile *past* the rectangle, i.e. <c>WorldX + EffectiveSize</c>.</summary>
        public int Right
        {
            get { return WorldX + EffectiveSize; }
        }

        /// <summary>First y tile *past* the rectangle.</summary>
        public int Bottom
        {
            get { return WorldY + EffectiveSize; }
        }

        /// <summary>Whether a tile is strictly inside the rectangle.</summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool Contains(int x, int y)
        {
            return x >= WorldX && y >= WorldY && x < Right && y < Bottom;
        }
    }

    /// <summary>
    /// Everything <see cref="NeighbourhoodMapGenerator"/> consumes: a seed, a map size, and the
    /// lots.
    ///
    /// <para>
    /// Like <see cref="NeighbourhoodLotInput"/> this is a standalone projection rather than a
    /// reference to M1's <c>NeighbourhoodLayout</c>, for the same reason - so M2 is testable
    /// without the store, the filesystem, or the parallel milestone. The projection is one-way
    /// and lossless for the fields M2 uses; M1 keeps the rest.
    /// </para>
    ///
    /// <para>
    /// A malformed lot - entirely off the map, or a size that clamps to nothing - is <b>not</b>
    /// rejected here. Bad input has to reach the generator to be reported, and dropping it in the
    /// constructor would make "the generator produced a neighbourhood with no roads" and "the
    /// generator never saw the lot" indistinguishable. <see cref="UsableLots"/> is the filtered
    /// view every stage actually iterates.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodMapInput
    {
        private readonly NeighbourhoodLotInput[] _lots;
        private readonly NeighbourhoodLotInput[] _usable;

        /// <summary>
        /// The world seed. Also the layout's seed - M1 stores one <c>seed</c> per neighbourhood and
        /// both milestones read it, so a regenerated map always matches the layout that produced
        /// it. The generator derives its random streams from it; see
        /// <c>NeighbourhoodStreams</c>.
        /// </summary>
        public long Seed { get; }

        /// <summary>
        /// The side length of the map to generate. <see cref="NeighbourhoodMap.DefaultSize"/> (512)
        /// for the game; tests use a smaller one because the generator is O(size^2) and a test
        /// that builds a dozen 512x512 maps is a test nobody waits for.
        /// </summary>
        public int MapSize { get; }

        /// <summary>
        /// Every lot handed to the generator, in the order it was handed over, bad ones included.
        /// </summary>
        public IReadOnlyList<NeighbourhoodLotInput> Lots
        {
            get { return _lots; }
        }

        /// <summary>
        /// The lots that overlap the map at all, in input order. A rectangle entirely outside
        /// 0..MapSize can contribute neither ground, nor a border ring, nor a road, so every
        /// stage iterates this instead of <see cref="Lots"/>.
        /// </summary>
        public IReadOnlyList<NeighbourhoodLotInput> UsableLots
        {
            get { return _usable; }
        }

        /// <summary>
        /// Creates a generation input.
        /// </summary>
        /// <param name="seed">The world seed.</param>
        /// <param name="mapSize">The side length of the map; 512 for a real one.</param>
        /// <param name="lots">The lots, in any order. <c>null</c> means an empty neighbourhood.</param>
        public NeighbourhoodMapInput(long seed, int mapSize, IEnumerable<NeighbourhoodLotInput> lots)
        {
            if (mapSize < 4) throw new ArgumentOutOfRangeException(
                nameof(mapSize), mapSize, "a map must be at least 4 tiles across.");

            Seed = seed;
            MapSize = mapSize;

            _lots = lots == null ? new NeighbourhoodLotInput[0] : new List<NeighbourhoodLotInput>(lots).ToArray();

            var usable = new List<NeighbourhoodLotInput>();
            for (int i = 0; i < _lots.Length; i++)
            {
                if (OverlapsMap(_lots[i], mapSize)) usable.Add(_lots[i]);
            }
            _usable = usable.ToArray();
        }
        private static bool OverlapsMap(NeighbourhoodLotInput lot, int mapSize)
        {
            return lot.WorldX < mapSize && lot.WorldY < mapSize
                && lot.Right > 0 && lot.Bottom > 0;
        }

        /// <summary>
        /// Convenience for the very common "a few lots in a row, all the same everything" case.
        /// </summary>
        /// <param name="seed">The world seed.</param>
        /// <param name="mapSize">The side length of the map.</param>
        /// <param name="lots">The lots to place.</param>
        public static NeighbourhoodMapInput Of(long seed, int mapSize, params NeighbourhoodLotInput[] lots)
        {
            return new NeighbourhoodMapInput(seed, mapSize, lots);
        }
    }

    /// <summary>
    /// The two raster masks every later stage needs, projected from a lot list: which tiles are
    /// inside a lot, and which are on the one-tile border ring around one.
    ///
    /// <para>
    /// The ring is a real geometric object in the plan (§6.1's sidewalk, §6.3's Dirichlet
    /// boundary), not a per-stage convenience: three separate stages want it - the road graph
    /// refuses to draw over it only for <em>interiors</em> but a lot's own ring is where its
    /// driveway starts, the terrain fill keeps foliage off it, and the seam solver pins it. It
    /// lives in this file because it is nothing but an interpretation of the input, which is what
    /// this file is for.
    /// </para>
    ///
    /// <para>
    /// Where two lots' rings overlap, both masks are set and
    /// <see cref="BorderRing"/> does not say which lot claimed the tile. Nothing needs it to: the
    /// only consumer that cared, the seam solver's "which lot owns this height", asks
    /// <see cref="NeighbourhoodTerrain"/>-independent nearest-lot ownership via
    /// <c>TerrainFill.NearestLotOwners</c> and breaks ties by lot order.
    /// </para>
    ///
    /// <para>
    /// <b>Overlapping lots are legal, and that is a Part 8 change.</b> Lots are now placed at their
    /// real <c>LotLocations</c> positions through
    /// <see cref="NeighbourhoodPixelTransform"/>, and TSO's own <c>RestoreSurroundings</c> puts two
    /// neighbours <c>Size - 2</c> tiles apart - so two real lots' rectangles overlap by two tiles
    /// on purpose. Everything here is written to tolerate that: a tile in two interiors is simply
    /// interior, a ring is never laid inside any interior (interiors are filled first), and both
    /// loops are clipped to the map rather than assuming a rectangle is inside it. The
    /// no-overlap invariant the packer used to promise is gone with the packer; what replaced it is
    /// that a road is never drawn inside one - <see cref="RoadGraph"/>'s <c>Mark</c> refuses, which
    /// is the invariant that actually shows on screen.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodLotMask
    {
        private readonly int _size;

        /// <summary>True for every tile strictly inside some lot's rectangle. Row-major.</summary>
        public bool[] Interior { get; }

        /// <summary>
        /// True for every tile on the one-tile ring just outside some lot's rectangle, clipped to
        /// the map. Row-major. Never includes interior tiles.
        /// </summary>
        public bool[] BorderRing { get; }

        private NeighbourhoodLotMask(int size, bool[] interior, bool[] ring)
        {
            _size = size;
            Interior = interior;
            BorderRing = ring;
        }

        /// <summary>Builds the masks for a lot list. A tile may be in both.</summary>
        /// <param name="lots">The lots to rasterise, in any order.</param>
        /// <param name="mapSize">The side length of the map.</param>
        public static NeighbourhoodLotMask Build(IReadOnlyList<NeighbourhoodLotInput> lots, int mapSize)
        {
            if (mapSize < 4) throw new ArgumentOutOfRangeException(nameof(mapSize));
            var interior = new bool[mapSize * mapSize];
            var ring = new bool[mapSize * mapSize];
            var mask = new NeighbourhoodLotMask(mapSize, interior, ring);

            if (lots == null) return mask;

            for (int i = 0; i < lots.Count; i++)
            {
                var lot = lots[i];
                for (int y = Math.Max(0, lot.WorldY); y < Math.Min(mapSize, lot.Bottom); y++)
                {
                    for (int x = Math.Max(0, lot.WorldX); x < Math.Min(mapSize, lot.Right); x++)
                    {
                        interior[y * mapSize + x] = true;
                    }
                }
            }

            // The ring is filled last and only outside interiors, so a lot packed hard against
            // another lot's ring cannot claim the neighbour's ground.
            for (int i = 0; i < lots.Count; i++)
            {
                var lot = lots[i];
                for (int y = Math.Max(0, lot.WorldY - 1); y < Math.Min(mapSize, lot.Bottom + 1); y++)
                {
                    for (int x = Math.Max(0, lot.WorldX - 1); x < Math.Min(mapSize, lot.Right + 1); x++)
                    {
                        var index = y * mapSize + x;
                        if (interior[index]) continue;
                        if (lot.Contains(x, y)) continue;
                        ring[index] = true;
                    }
                }
            }

            return mask;
        }

        /// <summary>Whether a tile is inside a lot.</summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsInterior(int x, int y)
        {
            return InMap(x, y) && Interior[y * _size + x];
        }

        /// <summary>Whether a tile is on a lot's border ring.</summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsBorderRing(int x, int y)
        {
            return InMap(x, y) && BorderRing[y * _size + x];
        }

        private bool InMap(int x, int y)
        {
            return x >= 0 && y >= 0 && x < _size && y < _size;
        }
    }
}
