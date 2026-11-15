/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using FSO.Content.Model;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Which edge of a lot's packed rectangle its driveway is on.
    /// </summary>
    /// <remarks>
    /// A rectangle in the generated world has four edges; the road graph (<c>RoadGraph</c>, M2)
    /// needs to know which one faces the street so it can grow a spur to the right place, and the
    /// facade generator uses the same edge to know which way a lot faces. The values are fixed
    /// 0..3 so they are stable in the sidecar and in any later format that stores them as an
    /// integer, and so a lookup of "the edge on side <c>n</c>" is an array index.
    ///
    /// <para>
    /// The name is <c>NeighbourhoodLotSide</c> rather than <c>NeighbourhoodDoorSide</c> because
    /// the generator (M2) declares a class of the latter name holding the same four values as
    /// <c>byte</c> constants plus the direction each one points in, and is deliberately built not
    /// to depend on this type - M1's layout is projected onto M2's own input record, a dozen field
    /// copies, and nothing else crosses. Two same-valued types on either side of a seam is the
    /// point: a change to one cannot silently reinterpret the other, and the cast at the seam is
    /// a line somebody can read.
    /// </para>
    /// </remarks>
    public enum NeighbourhoodLotSide
    {
        /// <summary>Low <c>y</c> edge: the top of the rectangle on the map.</summary>
        North = 0,

        /// <summary>High <c>x</c> edge: the right-hand edge of the rectangle.</summary>
        East = 1,

        /// <summary>High <c>y</c> edge: the bottom of the rectangle on the map.</summary>
        South = 2,

        /// <summary>Low <c>x</c> edge: the left-hand edge of the rectangle.</summary>
        West = 3
    }

    /// <summary>
    /// How much of a lot exists in the world, for Scope B's benefit. Always
    /// <see cref="Unloaded"/> in Scope A, which has no cross-lot simulation and no hollow lots.
    /// </summary>
    /// <remarks>
    /// §15 of the plan asks for this as an <em>optional</em> per-lot field: a sidecar written
    /// without it must still load, defaulting to "unloaded". That is exactly how
    /// <see cref="NeighbourhoodLot"/> declares it - a defaulted value, not a nullable one - so
    /// removing the field from the sidecar entirely leaves Scope A byte-for-byte identical.
    /// </remarks>
    public enum NeighbourhoodLotState
    {
        /// <summary>No lot contents exist for this lot. The only state Scope A ever produces.</summary>
        Unloaded = 0,

        /// <summary>Contents exist but are not resident.</summary>
        Hollow = 1,

        /// <summary>Contents are resident and being simulated.</summary>
        Active = 2
    }

    /// <summary>
    /// Where a lot's front door is in the generated world. Absent in Scope A: the entrance needs
    /// the road graph, which is M2's <c>ILotEntrance</c>.
    /// </summary>
    /// <remarks>
    /// §15 forward field. <c>null</c> means "not computed yet", which is a different statement
    /// from "computed and is (0, 0)", so this is a nullable struct rather than a defaulted one.
    /// </remarks>
    public struct NeighbourhoodLotEntrance
    {
        /// <summary>World tile x of the door.</summary>
        public int WorldX { get; set; }

        /// <summary>World tile y of the door.</summary>
        public int WorldY { get; set; }

        /// <summary>
        /// Which way the door faces, in the same 0..3 sense as <see cref="NeighbourhoodLotSide"/>:
        /// the side of the lot rectangle the door is on.
        /// </summary>
        public int Facing { get; set; }

        public NeighbourhoodLotEntrance(int worldX, int worldY, int facing)
        {
            WorldX = worldX;
            WorldY = worldY;
            Facing = facing;
        }
    }

    /// <summary>
    /// A half-open axis-aligned box in generated-map tile space: <c>[MinX, MaxX) x [MinY, MaxY)</c>.
    /// </summary>
    /// <remarks>
    /// The layout's <c>Extent</c> is the bounding box of everything the packer placed, which is
    /// not the same thing as the map: the map is always <c>0..MapSize</c> and lots may not fill
    /// it. Half-open rather than inclusive because every use is "does this box contain that
    /// rectangle" or "how wide is it", and both are answerable without a <c>+1</c> that a
    /// future change could forget.
    /// </remarks>
    public readonly struct NeighbourhoodExtent
    {
        /// <summary>Lowest tile x covered, inclusive.</summary>
        public int MinX { get; }

        /// <summary>Lowest tile y covered, inclusive.</summary>
        public int MinY { get; }

        /// <summary>One past the highest tile x covered, exclusive.</summary>
        public int MaxX { get; }

        /// <summary>One past the highest tile y covered, exclusive.</summary>
        public int MaxY { get; }

        /// <summary>Width in tiles. Never negative; an empty extent is zero wide.</summary>
        public int Width
        {
            get { return MaxX > MinX ? MaxX - MinX : 0; }
        }

        /// <summary>Height in tiles. Never negative; an empty extent is zero tall.</summary>
        public int Height
        {
            get { return MaxY > MinY ? MaxY - MinY : 0; }
        }

        /// <summary>Centre tile x, rounded down. Meaningless on an empty extent.</summary>
        public int CenterX
        {
            get { return MinX + Width / 2; }
        }

        /// <summary>Centre tile y, rounded down. Meaningless on an empty extent.</summary>
        public int CenterY
        {
            get { return MinY + Height / 2; }
        }

        /// <summary>An extent containing nothing. Also what an empty layout reports.</summary>
        public static NeighbourhoodExtent Empty
        {
            get { return new NeighbourhoodExtent(0, 0, 0, 0); }
        }

        public NeighbourhoodExtent(int minX, int minY, int maxX, int maxY)
        {
            MinX = minX;
            MinY = minY;
            MaxX = maxX;
            MaxY = maxY;
        }

        /// <summary>
        /// Is <paramref name="x"/>,<paramref name="y"/> inside this box?
        /// </summary>
        /// <remarks>
        /// Half-open, so a box that ends at 512 does not claim tile 512 - which is what makes
        /// "<c>0 &lt;= x &amp;&amp; x &lt; MapSize</c>" the same test the in-bounds assertion in
        /// the packer makes.
        /// </remarks>
        public bool Contains(int x, int y)
        {
            return x >= MinX && x < MaxX && y >= MinY && y < MaxY;
        }

        /// <summary>
        /// The smallest box containing this one and <paramref name="other"/>. Either box may be
        /// empty, in which case the other wins.
        /// </summary>
        public NeighbourhoodExtent Union(NeighbourhoodExtent other)
        {
            if (Width <= 0 || Height <= 0) return other;
            if (other.Width <= 0 || other.Height <= 0) return this;
            return new NeighbourhoodExtent(
                Math.Min(MinX, other.MinX),
                Math.Min(MinY, other.MinY),
                Math.Max(MaxX, other.MaxX),
                Math.Max(MaxY, other.MaxY));
        }

        public override string ToString()
        {
            return "[" + MinX + "," + MinY + " -> " + MaxX + "," + MaxY + "]";
        }
    }

    /// <summary>
    /// One lot of a neighbourhood, as the generated world sees it: a square rectangle of
    /// <see cref="Size"/> tiles at (<see cref="WorldX"/>, <see cref="WorldY"/>), carrying the
    /// handful of facts the generator and the renderer need about it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the §4.1 per-lot model and nothing more. It is a plain data holder: no graphics,
    /// no file system, no VM. Everything in it is either copied straight out of the lot's own
    /// <c>HouseNN.iff</c> (<see cref="Size"/>, <see cref="Rotation"/>, <see cref="TerrainType"/>,
    /// <see cref="BaseHeight"/>) or derived from the neighbourhood's own files
    /// (<see cref="Zone"/>, <see cref="MapX"/>/<see cref="MapY"/>, <see cref="HouseHash"/>,
    /// <see cref="HasHouse"/>), or assigned by the packer (<see cref="WorldX"/>,
    /// <see cref="WorldY"/>, <see cref="DoorSide"/>).
    /// </para>
    ///
    /// <para>
    /// The last three - <see cref="LotState"/>, <see cref="LastSimulated"/> and
    /// <see cref="Entrance"/> - are §15's forward-compatibility fields. They exist so a later
    /// scope can add simulated state without another schema migration, they are always written
    /// at their defaults by Scope A, and deleting them (and the three members below) changes
    /// nothing about how a lot is placed, drawn or persisted.
    /// </para>
    /// </remarks>
    public sealed class NeighbourhoodLot
    {
        /// <summary>
        /// The lot/house number, as it appears in <c>LotLocations.iff</c> and as
        /// <c>HouseNN.iff</c>. Never 99: that is the sentinel the lot-location parser drops.
        /// </summary>
        public short Number { get; set; }

        /// <summary>
        /// The lot's zone, one of <see cref="LotZoning.ZoneResidential"/>,
        /// <see cref="LotZoning.ZoneCommunity"/> or <see cref="LotZoning.ZoneStudio"/>.
        /// </summary>
        public short Zone { get; set; }

        /// <summary>
        /// The lot's side length in tiles. TS1 lots are square, so one number is the whole
        /// footprint; the activator builds a <c>Size x Size</c> blueprint from
        /// <c>SIMI.GlobalData[23]</c>.
        /// </summary>
        /// <remarks>
        /// Clamped to <see cref="NeighbourhoodLayout.MinLotSize"/>..
        /// <see cref="NeighbourhoodLayout.MaxLotSize"/> by the builder, because a corrupt or
        /// absent <c>SIMI</c> would otherwise put a zero or negative rectangle into the map.
        /// </remarks>
        public int Size { get; set; }

        /// <summary>
        /// The lot's <em>centre</em> on the 2D neighbourhood bitmap, in pixels, from
        /// <c>LotLocations.iff</c>.
        /// </summary>
        /// <remarks>
        /// Recorded, not authoritative: the shipped shelf packer (<c>PackOnGrid</c>) only uses it
        /// to order the lots and places <see cref="WorldX"/>/<see cref="WorldY"/> itself. It is
        /// what the 2D fallback, the camera and the house buttons read, so they can be
        /// cross-checked against the file that says so.
        /// </remarks>
        public int MapX { get; set; }

        /// <summary>The lot's centre on the 2D neighbourhood bitmap. See <see cref="MapX"/>.</summary>
        public int MapY { get; set; }

        /// <summary>
        /// The lot rectangle's origin in generated-map tile space, inclusive: its
        /// <see cref="MapX"/>/<see cref="MapY"/> centre through
        /// <c>NeighbourhoodPixelTransform</c>, half a lot back, clamped so the whole rectangle stays
        /// inside <c>0..MapSize</c>.
        /// </summary>
        /// <remarks>
        /// <b>Two lots may share tiles, on purpose.</b> TSO's own <c>RestoreSurroundings</c> puts
        /// neighbours <c>Size - 2</c> tiles apart, so two real lots overlap by two tiles. Every
        /// consumer of a layout has to tolerate that: the lot mask unions them, the road graph
        /// refuses to draw inside either, and the terrain fill's nearest-lot ownership breaks the
        /// tie on lot order.
        /// </remarks>
        public int WorldX { get; set; }

        /// <summary>The packed rectangle's origin in generated-map tile space. See <see cref="WorldX"/>.</summary>
        public int WorldY { get; set; }

        /// <summary>
        /// The lot's facing, from <c>HOUS.CameraDir</c>, normalised to 0..3.
        /// </summary>
        /// <remarks>
        /// Recorded because the facade generator and any later arrival placement need it, and
        /// because re-deriving it would mean re-reading every <c>HouseNN.iff</c>. Scope A's
        /// packer and generator ignore it: rectangles are axis-aligned and the roads are too, so
        /// nothing in M1-M5 is rotated.
        /// </remarks>
        public int Rotation { get; set; }

        /// <summary>
        /// The lot's ground type, from the same table <c>VMTS1Activator.HouseNumToType</c> uses
        /// for the lot's own terrain, so the generated ground around a lot matches the ground the
        /// player sees inside it.
        /// </summary>
        public TerrainType TerrainType { get; set; }

        /// <summary>
        /// The height of the lot's ground, in the lot's own altitude units, taken as the median of
        /// the decoded <c>ARRY(0)</c> heights.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The median rather than the minimum: a single spike in a lot's altitude array would drag
        /// a minimum down and make the whole neighbourhood sit below one bad tile, whereas a
        /// median moves with the lot. §6.3 then takes the minimum of this value <em>across</em>
        /// lots for the base plane, which is where a global low outlier actually matters.
        /// </para>
        /// <para>
        /// <b>Units.</b> This is the raw <c>ARRY(0)</c> altitude, in the same units the lot's own
        /// terrain uses - not the generated map's. <c>CityMapData.ElevationData</c> is a
        /// <c>byte</c> that <c>CityGeometry</c> divides by 12, so whoever projects this layout
        /// onto the generator's input record owns that quantisation; it is deliberately not done
        /// here, where it would put a map-specific scale in a model that otherwise knows nothing
        /// about maps.
        /// </para>
        /// </remarks>
        public short BaseHeight { get; set; }

        /// <summary>
        /// Which edge of the rectangle the driveway is on.
        /// </summary>
        /// <remarks>
        /// The edge nearest a map border, from <c>NeighbourhoodLayoutBuilder.DoorSideFor</c>.
        /// </remarks>
        public NeighbourhoodLotSide DoorSide { get; set; }

        /// <summary>
        /// A fingerprint of the lot's <c>HouseNN.iff</c>: its file size mixed with its last write
        /// time. Zero means "no house file", which is also a bulldozed lot.
        /// </summary>
        /// <remarks>
        /// This is what makes a cached layout safe. A layout is only regenerated wholesale when
        /// the seed or the generator version changes; a lot whose hash has moved is the cheap
        /// case - only that lot's placement, and the roads and terrain that touch it, are stale.
        /// </remarks>
        public uint HouseHash { get; set; }

        /// <summary>
        /// Is there a house file for this lot? <c>false</c> for an empty plot and for a bulldozed
        /// lot, both of which draw nothing (or a bare plot) and contribute no facade.
        /// </summary>
        public bool HasHouse { get; set; }

        // ---- §15 forward-compatibility fields -------------------------------------------------
        // Inert for Scope A. See the remarks on the class.

        /// <summary>How much of this lot exists in the world. Always <see cref="NeighbourhoodLotState.Unloaded"/> here.</summary>
        public NeighbourhoodLotState LotState { get; set; }

        /// <summary>
        /// When this lot's contents were last simulated, or <c>0</c> if never. Scope A never
        /// simulates a lot, so this is always <c>0</c>.
        /// </summary>
        public long LastSimulated { get; set; }

        /// <summary>
        /// Where this lot's door is, or <c>null</c> when the entrance has not been computed.
        /// Always <c>null</c> in Scope A: the entrance is M2's <c>ILotEntrance</c>.
        /// </summary>
        public NeighbourhoodLotEntrance? Entrance { get; set; }

        /// <summary>Is this lot inside a footprint that is only a plot - nothing to draw?</summary>
        public bool IsEmptyPlot
        {
            get { return !HasHouse; }
        }

        public NeighbourhoodLot()
        {
        }

        /// <summary>
        /// The lot's footprint: <see cref="Size"/> plus <paramref name="margin"/>, which is what the
        /// road graph and the surround's own arithmetic measure against.
        /// </summary>
        /// <remarks>
        /// The shelf packer advances by exactly this, so two shelf lots are never closer than
        /// the street corridor; <see cref="Overlaps"/> with a margin asks how close two lots are.
        /// </remarks>
        /// <param name="margin">Tiles of margin to add on each dimension.</param>
        public int Footprint(int margin)
        {
            return Size + Math.Max(0, margin);
        }

        /// <summary>One past the last tile x this lot covers, including its margin.</summary>
        public int WorldRight(int margin)
        {
            return WorldX + Footprint(margin);
        }

        /// <summary>One past the last tile y this lot covers, including its margin.</summary>
        public int WorldBottom(int margin)
        {
            return WorldY + Footprint(margin);
        }

        /// <summary>
        /// Do two lots' footprints share a tile? Rectangles are half-open, so a lot that ends
        /// exactly where another begins does not overlap it.
        /// </summary>
        /// <remarks>
        /// <b>Overlap is a normal answer here, not a defect.</b> Two real lots two tiles apart on the
        /// bitmap are drawn two tiles apart on the map, which is <c>Size - 2</c> against a
        /// <c>Size</c>-wide rectangle, so they overlap - see
        /// <see cref="NeighbourhoodMapAdapter.LotViewLotPitchTiles"/>. This is the question a
        /// diagnostic asks, not an invariant the pipeline keeps.
        /// </remarks>
        /// <param name="a">One lot.</param>
        /// <param name="b">The other.</param>
        /// <param name="margin">Tiles to add to each rectangle before measuring.</param>
        public static bool Overlaps(NeighbourhoodLot a, NeighbourhoodLot b, int margin)
        {
            if (a == null || b == null) return false;
            return a.WorldX < b.WorldRight(margin)
                && b.WorldX < a.WorldRight(margin)
                && a.WorldY < b.WorldBottom(margin)
                && b.WorldY < a.WorldBottom(margin);
        }

        public override string ToString()
        {
            return "lot " + Number + " (" + WorldX + "," + WorldY + ") " + Size + "x" + Size
                + " " + TerrainType + " door=" + DoorSide + (HasHouse ? string.Empty : " [plot]");
        }
    }

    /// <summary>
    /// The complete, pure description of one neighbourhood's generated world: where every lot
    /// sits in the 512x512 generated map, and the handful of facts about the map itself that a
    /// generated map needs in order to be reproducible.
    ///
    /// <para>
    /// §4.1's model. It is a value: two layouts built from the same lot records with the same
    /// seed are equal field-for-field, which is the property the sidecar's round-trip and the
    /// generator's determinism both rest on. It contains no lot raster data - the 512x512 arrays
    /// are regenerated from this and the seed at load time - and it holds no reference to
    /// anything outside itself, so it can be built, compared, serialised and asserted on with no
    /// graphics device, no VM and no game content.
    /// </para>
    ///
    /// <para>
    /// <b>And, optionally, the session.</b> <see cref="WorldState"/> is the clock, the active lot
    /// and the transit in progress. It is <c>null</c> on every layout Scope A builds, and it is
    /// written to the sidecar only when it is there - so a Scope A layout still serialises to the
    /// bytes Scope A wrote. It is mutable in the way session state is; <see cref="Lots"/> is not.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodLayout
    {
        /// <summary>
        /// Schema version of the sidecar this build writes. Bumped when the <em>shape</em> of the
        /// file changes; see <see cref="NeighbourhoodLayoutStore"/>.
        /// </summary>
        /// <remarks>
        /// <b>2</b> added exactly one top-level member, <c>worldState</c> (B7), and it is
        /// written only when a layout actually has one. That is what makes schema 1 a strict
        /// <em>prefix</em> of schema 2: the document a layout with no world state produces is
        /// byte-for-byte the document schema 1 produced, and a schema-2 file is that same
        /// document with one more member at the end. A reader of schema 1 therefore sees a file
        /// it already knows, and a reader of schema 2 sees the same lots either way.
        ///
        /// <para>
        /// The generator version is a different stamp and did not move: nothing about how lots
        /// are placed changed, so every layout packed by the previous generator version is still
        /// valid and reusing it is still correct.
        /// </para>
        /// </remarks>
        public const int CurrentSchemaVersion = 2;

        /// <summary>
        /// The oldest schema this build still reads. A layout stamped below it cannot be
        /// interpreted at all; one stamped at or above it and not above
        /// <see cref="CurrentSchemaVersion"/> is read and, if it is older, migrated in memory.
        /// </summary>
        public const int MinSchemaVersion = 1;

        /// <summary>Side of the generated map, in tiles, unless the caller asks for another.</summary>
        public const int DefaultMapSize = 512;

        /// <summary>Smallest accepted lot size, in tiles.</summary>
        public const int MinLotSize = 1;

        /// <summary>
        /// Largest accepted lot size, in tiles. TS1 lots are 64x64 and the activator turns
        /// <c>SIMI.GlobalData[23]</c> straight into a <c>Size x Size</c> blueprint, so anything
        /// larger is a corrupt value rather than a very large lot.
        /// </summary>
        public const int MaxLotSize = 64;

        /// <summary>Neighbourhood slot id this layout belongs to, 0..8.</summary>
        public int Slot { get; }

        /// <summary>
        /// The seed the whole generated world is derived from. Change it and the terrain, roads
        /// and foliage all change; the lot placement is derived from it too, so a layout is only
        /// valid for the seed it was built with.
        /// </summary>
        public int Seed { get; }

        /// <summary>
        /// The layout algorithm's version, stamped on the layout so a layout packed by an older
        /// arrangement rule is regenerated rather than half-trusted.
        /// </summary>
        public int GeneratorVersion { get; }

        /// <summary>The sidecar schema this layout is shaped for. See <see cref="CurrentSchemaVersion"/>.</summary>
        public int SchemaVersion { get; }

        /// <summary>Side of the generated map in tiles. The arrays are <c>MapSize x MapSize</c>.</summary>
        public int MapSize { get; }

        /// <summary>
        /// The bounding box of every lot placement, in tile space. Not the same as the map: lots
        /// need not reach its edges.
        /// </summary>
        public NeighbourhoodExtent Extent { get; }

        /// <summary>Every lot, in ascending lot-number order. Immutable.</summary>
        public IReadOnlyList<NeighbourhoodLot> Lots { get; }

        /// <summary>
        /// The session half of the sidecar - clock, active lot, transit - or <c>null</c> when this
        /// layout carries none.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Optional, and nullable rather than defaulted, because "this layout has never been
        /// stamped with a world" and "this layout was stamped with a world that has no clock" are
        /// different statements: the first is a Scope A layout, and the second is a player who
        /// quit on the neighbourhood screen. Only the first serialises to a file with no
        /// <c>worldState</c> member at all, which is what keeps a Scope A sidecar byte-identical
        /// to what Scope A wrote.
        /// </para>
        ///
        /// <para>
        /// Lot-level session state is <em>not</em> here. Which lots are hollow, when each was
        /// last simulated and where its door is belong to the lots themselves
        /// (<see cref="NeighbourhoodLot.LotState"/> and its neighbours). This member is the part
        /// that belongs to the world as a whole.
        /// </para>
        ///
        /// <para>
        /// Session state, so mutable in the way session state is: whoever simulates the world
        /// fills it in and then saves the layout. Nothing in Scope A writes to it, and removing
        /// this member changes nothing about how a lot is placed, drawn or persisted.
        /// </para>
        /// </remarks>
        public NeighbourhoodWorldState WorldState { get; }

        private readonly Dictionary<short, NeighbourhoodLot> _byNumber;

        /// <summary>How many lots the layout places.</summary>
        public int LotCount
        {
            get { return Lots.Count; }
        }

        /// <summary>
        /// A layout with no lots at all. The state of a neighbourhood that could not be read;
        /// rendering a map of it is still well defined (it is just empty ground), so the caller
        /// decides whether that is a failure rather than this type refusing to exist.
        /// </summary>
        public static NeighbourhoodLayout Empty(int slot, int seed, int generatorVersion)
        {
            return new NeighbourhoodLayout(slot, seed, generatorVersion, CurrentSchemaVersion,
                DefaultMapSize, new NeighbourhoodLot[0]);
        }

        /// <summary>
        /// A layout with no world state. Every Scope A construction - the packer, the patcher, the
        /// map adapter, the in-lot surroundings - goes through here, so adding the world state
        /// did not change a single one of them.
        /// </summary>
        public NeighbourhoodLayout(int slot, int seed, int generatorVersion, int schemaVersion,
            int mapSize, IEnumerable<NeighbourhoodLot> lots)
            : this(slot, seed, generatorVersion, schemaVersion, mapSize, lots, null)
        {
        }

        /// <summary>
        /// A layout carrying a session world state. <paramref name="worldState"/> may be
        /// <c>null</c>, which means "not stamped" and is exactly what the overload above passes.
        /// </summary>
        public NeighbourhoodLayout(int slot, int seed, int generatorVersion, int schemaVersion,
            int mapSize, IEnumerable<NeighbourhoodLot> lots, NeighbourhoodWorldState worldState)
        {
            if (mapSize <= 0) throw new ArgumentOutOfRangeException(nameof(mapSize), mapSize, "A generated map has a positive side length.");
            if (lots == null) throw new ArgumentNullException(nameof(lots));

            Slot = slot;
            Seed = seed;
            GeneratorVersion = generatorVersion;
            SchemaVersion = schemaVersion;
            MapSize = mapSize;
            WorldState = worldState;

            //ascending lot number is the canonical order: it makes the sidecar byte-stable, and
            //it removes any dependence on the order the lot-location table happened to be in
            var ordered = new List<NeighbourhoodLot>();
            foreach (var lot in lots)
            {
                if (lot != null) ordered.Add(lot);
            }
            ordered.Sort(CompareByNumber);

            var unique = new List<NeighbourhoodLot>(ordered.Count);
            _byNumber = new Dictionary<short, NeighbourhoodLot>(ordered.Count);
            for (int i = 0; i < ordered.Count; i++)
            {
                //the lot-location table can name a lot twice; the first entry wins, and the
                //duplicate is dropped rather than packed twice into the same place
                if (_byNumber.ContainsKey(ordered[i].Number)) continue;
                _byNumber.Add(ordered[i].Number, ordered[i]);
                unique.Add(ordered[i]);
            }

            Lots = new ReadOnlyCollection<NeighbourhoodLot>(unique);
            Extent = ComputeExtent(unique);
        }

        private static int CompareByNumber(NeighbourhoodLot a, NeighbourhoodLot b)
        {
            return a.Number.CompareTo(b.Number);
        }

        private static NeighbourhoodExtent ComputeExtent(List<NeighbourhoodLot> lots)
        {
            var result = NeighbourhoodExtent.Empty;
            for (int i = 0; i < lots.Count; i++)
            {
                var lot = lots[i];
                result = result.Union(new NeighbourhoodExtent(
                    lot.WorldX, lot.WorldY,
                    lot.WorldX + Math.Max(0, lot.Size),
                    lot.WorldY + Math.Max(0, lot.Size)));
            }
            return result;
        }

        /// <summary>
        /// The lot with this number, or <c>null</c>. This is the query the generator, the facade
        /// cache and the in-lot surroundings all start from.
        /// </summary>
        public NeighbourhoodLot FindLot(int number)
        {
            NeighbourhoodLot lot;
            if (number >= short.MinValue && number <= short.MaxValue && _byNumber.TryGetValue((short)number, out lot))
            {
                return lot;
            }
            return null;
        }

        /// <summary>
        /// Which lot covers this world tile, or <c>null</c> for connective ground. The margin is
        /// excluded: a tile in the gap between two lots belongs to neither of them.
        /// </summary>
        /// <param name="worldX">Tile x.</param>
        /// <param name="worldY">Tile y.</param>
        public NeighbourhoodLot LotAt(int worldX, int worldY)
        {
            for (int i = 0; i < Lots.Count; i++)
            {
                var lot = Lots[i];
                if (worldX >= lot.WorldX && worldX < lot.WorldX + Math.Max(0, lot.Size)
                    && worldY >= lot.WorldY && worldY < lot.WorldY + Math.Max(0, lot.Size))
                {
                    return lot;
                }
            }
            return null;
        }

        /// <summary>
        /// Would this layout still be valid for <paramref name="seed"/> and
        /// <paramref name="generatorVersion"/>? A <c>false</c> here is the whole of the
        /// sidecar's invalidation rule: the cached file is dropped and the world is rebuilt.
        /// </summary>
        public bool IsCurrentFor(int seed, int generatorVersion)
        {
            return Seed == seed && GeneratorVersion == generatorVersion;
        }

        /// <summary>Does this layout stamp a schema this build reads, understands and writes?</summary>
        public bool HasCurrentSchema
        {
            get { return SchemaVersion >= MinSchemaVersion && SchemaVersion <= CurrentSchemaVersion; }
        }

        public override string ToString()
        {
            return "slot " + Slot + " seed " + Seed + " gen " + GeneratorVersion
                + " map " + MapSize + " lots " + Lots.Count + " extent " + Extent
                + (WorldState == null ? string.Empty : " +world");
        }
    }
}
