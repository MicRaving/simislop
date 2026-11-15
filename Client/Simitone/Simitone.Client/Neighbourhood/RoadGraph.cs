/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Text;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The 4-bit road adjacency mask that both nibbles of a <c>CityMapData.RoadData</c> byte
    /// carry, and the bit order it uses.
    ///
    /// <para>
    /// <b>The encoding, verified against the shipped atlas rather than assumed.</b>
    /// <c>CityGeometry.RegenMeshVerts</c> does exactly this:
    /// </para>
    /// <code>
    /// var normalRoad = roadByte &amp; 15;          // indexes CityContent.RoadLayout
    /// var cornerRoad = roadByte &gt;&gt; 4;          // indexes CityContent.RoadCLayout
    /// if (normalRoad &gt; 0) { ... RoadLayout[normalRoad] ... }
    /// if (cornerRoad &gt; 0) { ... RoadCLayout[cornerRoad] ... }
    /// </code>
    /// <para>
    /// So the nibble is an <em>index</em> into a 16-entry table, and 0 means "no road here" /
    /// "no corner here". It is not a bitfield the renderer decodes - the renderer only
    /// indexes a table. Which means the nibble value has to be the *adjacency mask itself* if
    /// the fifteen road textures are to be the fifteen shapes, and the file sizes of
    /// <c>tso.content/Content/Textures/terrain/road00..15.png</c> confirm exactly that grouping:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     {1, 2, 4, 8} are the four smallest files (966-995 bytes for the corner set, 2044-2730
    ///     for the road set) - four shapes with one connection each, i.e. the four dead ends.
    ///   </description></item>
    ///   <item><description>
    ///     {3, 5, 6, 9, 10, 12} are one size band (3721-4473) - the two straights and the four
    ///     corners.
    ///   </description></item>
    ///   <item><description>
    ///     {7, 11, 13, 14} are the next band up (5733-6022) - the four T-junctions.
    ///   </description></item>
    ///   <item><description>
    ///     {15} is the largest file of all (7225) - the four-way cross.
    ///   </description></item>
    /// </list>
    /// <para>
    /// The bit order was then read off the tiles themselves: sampling the alpha channel along
    /// each edge of <c>road01..15.png</c> shows which edges the road actually reaches, and
    /// <c>road05</c> reaches east and west (a horizontal straight, mask 5 = 1|4) while
    /// <c>road10</c> reaches north and south (mask 10 = 2|8). That fixes the remaining freedom -
    /// the bit rotation - and gives:
    /// </para>
    /// <code>
    /// bit 0 (1) = East  (+x)      mask 5  = E|W   horizontal straight
    /// bit 1 (2) = South (+y)      mask 10 = S|N   vertical straight
    /// bit 2 (4) = West  (-x)      mask 3  = E|S   corner
    /// bit 3 (8) = North (-y)      mask 6  = S|W   corner
    /// </code>
    /// <para>
    /// The same edge sampling on <c>roadcorner01..15.png</c> gives the identical pattern, so the
    /// high nibble carries the same mask. <b>Both nibbles are the mask.</b> There is no
    /// permutation table to get wrong, and no reason for a renderer to interpret anything: it
    /// reads the nibble and indexes the static table, exactly as TSO's own city maps do.
    /// </para>
    ///
    /// <para>
    /// <b>Where the high nibble is set.</b> A turn (two adjacent connections) or a T-junction
    /// (three) gets the corner piece; a straight, a dead end and a four-way cross get 0. The
    /// cross case is deliberate: a street two tiles wide or more is drawn as parallel rows of road,
    /// and every interior tile of it is a full four-way cross by construction (see
    /// <see cref="RoadGraph.MainStreetWidth"/>), so putting a corner piece there would draw a
    /// crossroads on every tile of a long straight street. A 1-tile-wide four-way crossing reads
    /// as two crossing straights, which is also what the mask says it is.
    /// </para>
    /// </summary>
    public static class RoadMask
    {
        /// <summary>Mask of a tile with no road (the low nibble) and no corner (the high nibble).</summary>
        public const byte None = 0;

        /// <summary>Bit 0: this tile connects to its eastern neighbour.</summary>
        public const byte East = 1;

        /// <summary>Bit 1: this tile connects to its southern neighbour.</summary>
        public const byte South = 2;

        /// <summary>Bit 2: this tile connects to its western neighbour.</summary>
        public const byte West = 4;

        /// <summary>Bit 3: this tile connects to its northern neighbour.</summary>
        public const byte North = 8;

        /// <summary>All four bits, i.e. a four-way crossing.</summary>
        public const byte All = East | South | West | North;

        /// <summary>The mask's own bit mask, i.e. how much of a <c>RoadData</c> byte it occupies.</summary>
        public const byte MaskBits = 0x0F;

        private static readonly int[] StepX = { 1, 0, -1, 0 };
        private static readonly int[] StepY = { 0, 1, 0, -1 };

        /// <summary>The x step of a single connection bit, or 0 for a compound mask.</summary>
        /// <param name="mask">A single bit, or any mask.</param>
        public static int DeltaX(byte mask)
        {
            return mask == East ? 1 : (mask == West ? -1 : 0);
        }

        /// <summary>The y step of a single connection bit, or 0 for a compound mask.</summary>
        /// <param name="mask">A single bit, or any mask.</param>
        public static int DeltaY(byte mask)
        {
            return mask == South ? 1 : (mask == North ? -1 : 0);
        }

        /// <summary>The connection bit for a step direction, or <see cref="None"/> for (0,0).</summary>
        /// <param name="dx">Step in x; -1, 0 or 1.</param>
        /// <param name="dy">Step in y; -1, 0 or 1.</param>
        public static byte BitForStep(int dx, int dy)
        {
            if (dx == 1) return East;
            if (dx == -1) return West;
            if (dy == 1) return South;
            if (dy == -1) return North;
            return None;
        }

        /// <summary>
        /// Whether a mask claims a connection in a direction. The two halves of the nibble
        /// contract meet here: a tile's mask has to agree with its neighbours' occupancy, and this
        /// is the question a renderer asks when it picks
        /// <c>CityContent.RoadLayout[mask]</c>.
        /// </summary>
        /// <param name="mask">A 4-bit adjacency mask.</param>
        /// <param name="bit">One of the four connection bits.</param>
        public static bool IsSet(byte mask, byte bit)
        {
            return (mask & bit) != 0;
        }

        /// <summary>The four neighbour offsets, in <c>East, South, West, North</c> bit order.</summary>
        public static void GetStep(int bit, out int dx, out int dy)
        {
            var i = bit >= East && bit <= North ? bit - 1 : 0;
            dx = StepX[i];
            dy = StepY[i];
        }

        /// <summary>Two opposite connections: 5 (E|W) and 10 (N|S).</summary>
        /// <param name="mask">A 4-bit adjacency mask.</param>
        public static bool IsStraight(byte mask)
        {
            return mask == (East | West) || mask == (South | North);
        }

        /// <summary>Two adjacent connections: 3, 6, 9, 12.</summary>
        /// <param name="mask">A 4-bit adjacency mask.</param>
        public static bool IsCorner(byte mask)
        {
            return mask == (East | South) || mask == (South | West)
                || mask == (West | North) || mask == (North | East);
        }

        /// <summary>Three connections: 7, 11, 13, 14.</summary>
        /// <param name="mask">A 4-bit adjacency mask.</param>
        public static bool IsTJunction(byte mask)
        {
            return mask == (All & ~West) || mask == (All & ~North)
                || mask == (All & ~East) || mask == (All & ~South);
        }

        /// <summary>Four connections: 15.</summary>
        /// <param name="mask">A 4-bit adjacency mask.</param>
        public static bool IsCross(byte mask)
        {
            return mask == All;
        }

        /// <summary>
        /// One connection: exactly the four single-bit masks 1, 2, 4, 8.
        /// </summary>
        /// <remarks>
        /// The obvious one-liner here is a range test, <c>mask &gt;= East &amp;&amp; mask &lt;= North</c>,
        /// and it is wrong: every mask from 1 to 8 has a single bit, so 3, 5, 6 and 7 - a corner, two
        /// straights and a T-junction - would all be reported as dead ends. A dead end is "exactly
        /// one connection", which is the popcount, not the magnitude.
        /// </remarks>
        /// <param name="mask">A 4-bit adjacency mask.</param>
        public static bool IsDeadEnd(byte mask)
        {
            return mask != None && (mask & (mask - 1)) == 0;
        }

        /// <summary>
        /// Whether a mask gets a corner piece in the high nibble: a turn or a T-junction, but not
        /// a straight, a dead end or a four-way cross. See the type remarks for why a cross is
        /// excluded.
        /// </summary>
        /// <param name="mask">A 4-bit adjacency mask.</param>
        public static bool IsTurnOrJunction(byte mask)
        {
            return IsCorner(mask) || IsTJunction(mask);
        }
    }

    /// <summary>What a <see cref="RoadEdge"/> is for. Only affects reporting and width defaults.</summary>
    public enum RoadEdgeKind
    {
        /// <summary>
        /// A main street: a maximal run of free ground between two rows or columns of lots, as
        /// wide as that ground allows up to <see cref="RoadGraph.MainStreetWidth"/>, and carrying
        /// its own <see cref="RoadEdge.Width"/>.
        /// </summary>
        Street,

        /// <summary>The short run from a lot's door out to the street.</summary>
        Driveway,

        /// <summary>
        /// A run added by the connectivity repair. Always one tile wide, and always
        /// <em>added</em> to the network rather than a consequence of the layout, so it is worth
        /// being able to tell apart in a dump.
        /// </summary>
        Repair,
    }

    /// <summary>
    /// A vertex of the road graph: a junction where two streets cross, or the middle of a street
    /// that meets no other. Door positions are <em>not</em> vertices - a driveway is an edge that
    /// starts at a lot's border and ends on a street, and its far end is a street tile rather
    /// than a junction.
    ///
    /// <para>
    /// The graph is kept in this form as well as rasterised because §15 of the plan wants the
    /// *graph* persisted, not just the bytes - the graph is what a renderer draws from and what a
    /// future pathfinder would route on.
    /// </para>
    /// </summary>
    public readonly struct RoadNode
    {
        /// <summary>Node x, in tiles.</summary>
        public int X { get; }

        /// <summary>Node y, in tiles.</summary>
        public int Y { get; }

        /// <summary>
        /// Whether this node is a street vertex. Every node the generator produces is one; the
        /// flag exists because a persisted graph loaded from an older sidecar could carry door
        /// vertices, and because "is this on the network or is it a lot's doorstep" is a question
        /// worth being able to ask without inferring it from coordinates.
        /// </summary>
        public bool OnStreetGrid { get; }

        /// <summary>
        /// The lot this node serves, or -1 for a pure street vertex. Street junctions report -1.
        /// </summary>
        public short LotNumber { get; }

        /// <summary>Creates a node.</summary>
        /// <param name="x">Node x.</param>
        /// <param name="y">Node y.</param>
        /// <param name="onStreetGrid">Whether it is a street-grid vertex.</param>
        /// <param name="lotNumber">The lot it serves, or -1.</param>
        public RoadNode(int x, int y, bool onStreetGrid, short lotNumber)
        {
            X = x;
            Y = y;
            OnStreetGrid = onStreetGrid;
            LotNumber = lotNumber;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return "(" + X + "," + Y + ")" + (OnStreetGrid ? " street" : " door")
                + (LotNumber < 0 ? string.Empty : " lot " + LotNumber);
        }
    }

    /// <summary>
    /// One straight, axis-aligned run of road. Every edge in the graph is a single run - an
    /// L-shaped path is two or three edges, never one bent edge - so an edge can always be
    /// rasterised without a direction test, and a persisted graph reads as a list of runs.
    /// </summary>
    public readonly struct RoadEdge
    {
        /// <summary>Start x (the end is normalised so this is the smaller x on a horizontal edge).</summary>
        public int AX { get; }

        /// <summary>Start y (the smaller y on a vertical edge).</summary>
        public int AY { get; }

        /// <summary>End x, inclusive.</summary>
        public int BX { get; }

        /// <summary>End y, inclusive.</summary>
        public int BY { get; }

        /// <summary>Thickness in tiles, measured downwards (rows) or rightwards (columns).</summary>
        public int Width { get; }

        /// <summary>What the run is for.</summary>
        public RoadEdgeKind Kind { get; }

        /// <summary>Creates an edge, normalising the endpoints so the run reads low-to-high.</summary>
        /// <param name="ax">Start x.</param>
        /// <param name="ay">Start y.</param>
        /// <param name="bx">End x.</param>
        /// <param name="by">End y.</param>
        /// <param name="width">Thickness in tiles, at least 1.</param>
        /// <param name="kind">What the run is for.</param>
        public RoadEdge(int ax, int ay, int bx, int by, int width, RoadEdgeKind kind)
        {
            AX = Math.Min(ax, bx);
            BX = Math.Max(ax, bx);
            AY = Math.Min(ay, by);
            BY = Math.Max(ay, by);
            Width = width < 1 ? 1 : width;
            Kind = kind;
        }

        /// <summary>How many tiles long this run is along its own axis.</summary>
        public int Length
        {
            get
            {
                return AX == BX ? BY - AY + 1 : BX - AX + 1;
            }
        }

        /// <summary>Whether the run is vertical.</summary>
        public bool IsVertical
        {
            get { return AX == BX; }
        }

        /// <summary>How many tiles the run covers before lot interiors clip it.</summary>
        public int Tiles
        {
            get { return Length * Width; }
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Kind + " " + (AX == BX
                ? "x=" + AX + " y=" + AY + ".." + BY
                : "y=" + AY + " x=" + AX + ".." + BX) + " w" + Width;
        }
    }

    /// <summary>
    /// Where a lot's door meets the generated world: the tile the driveway starts from, and the
    /// way it faces.
    ///
    /// <para>
    /// This is the data §15's <c>ILotEntrance</c> prep item is about - "world tile + facing for
    /// a lot's door" - expressed as a plain struct so M2 does not own an interface name another
    /// milestone may want. M1/M3/M4 can wrap it without the generator changing.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodEntrance
    {
        /// <summary>The lot this entrance belongs to.</summary>
        public short LotNumber { get; }

        /// <summary>The tile the driveway starts on. Always outside the lot's own interior.</summary>
        public int X { get; }

        /// <summary>The tile's y.</summary>
        public int Y { get; }

        /// <summary>The outward normal's x component: +1, -1, or 0.</summary>
        public int FacingX { get; }

        /// <summary>The outward normal's y component: +1, -1, or 0.</summary>
        public int FacingY { get; }

        /// <summary>
        /// The side the driveway actually leaves from: normally the layout's own
        /// <see cref="NeighbourhoodLotInput.DoorSide"/>, but a lot flush against a map border has
        /// no ground on that side, and the generator picks the first other side that does rather
        /// than dropping the lot off the network - see <see cref="RoadGraph.TryResolveEntrance"/>.
        /// <see cref="UnknownSide"/> when a caller has not said.
        /// </summary>
        public byte Side { get; }

        /// <summary>The value <see cref="Side"/> holds when it was not given.</summary>
        public const byte UnknownSide = byte.MaxValue;

        /// <summary>Creates an entrance whose side is <see cref="UnknownSide"/>.</summary>
        /// <param name="lotNumber">The lot number.</param>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        /// <param name="facingX">Outward normal x.</param>
        /// <param name="facingY">Outward normal y.</param>
        public NeighbourhoodEntrance(short lotNumber, int x, int y, int facingX, int facingY)
            : this(lotNumber, x, y, facingX, facingY, UnknownSide)
        {
        }

        /// <summary>Creates an entrance, recording which side it leaves from.</summary>
        /// <param name="lotNumber">The lot number.</param>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        /// <param name="facingX">Outward normal x.</param>
        /// <param name="facingY">Outward normal y.</param>
        /// <param name="side">The side it leaves from.</param>
        public NeighbourhoodEntrance(short lotNumber, int x, int y, int facingX, int facingY, byte side)
        {
            LotNumber = lotNumber;
            X = x;
            Y = y;
            FacingX = facingX;
            FacingY = facingY;
            Side = side;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return "lot " + LotNumber + " door at (" + X + "," + Y + ") facing ("
                + FacingX + "," + FacingY + ")";
        }
    }

    /// <summary>One run the connectivity repair had to add, kept so a failure can be explained.</summary>
    public readonly struct RoadRepair
    {
        /// <summary>What prompted it: an isolated component, or a lot that could not be reached.</summary>
        public string Reason { get; }

        /// <summary>How many tiles the added path is long.</summary>
        public int Length { get; }

        /// <summary>Where the added path starts.</summary>
        public int FromX { get; }

        /// <summary>Where the added path starts, in y.</summary>
        public int FromY { get; }

        /// <summary>Where the added path ends.</summary>
        public int ToX { get; }

        /// <summary>Where the added path ends, in y.</summary>
        public int ToY { get; }

        /// <summary>Creates a repair record.</summary>
        /// <param name="reason">What prompted the repair.</param>
        /// <param name="length">Path length in tiles.</param>
        /// <param name="fromX">Start x.</param>
        /// <param name="fromY">Start y.</param>
        /// <param name="toX">End x.</param>
        /// <param name="toY">End y.</param>
        public RoadRepair(string reason, int length, int fromX, int fromY, int toX, int toY)
        {
            Reason = reason;
            Length = length;
            FromX = fromX;
            FromY = fromY;
            ToX = toX;
            ToY = toY;
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Reason + ": " + Length + " tiles from (" + FromX + "," + FromY + ") to (" + ToX + "," + ToY + ")";
        }
    }

    /// <summary>The result of flood-filling the road tiles: a label per tile, grouped by component.</summary>
    public sealed class RoadComponents
    {
        private readonly int[] _componentOf;
        private readonly List<int[]> _tiles;

        /// <summary>Component id per tile, or -1 where there is no road. Row-major.</summary>
        public int[] ComponentOf
        {
            get { return _componentOf; }
        }

        /// <summary>Each component's tile indices, ascending. Indexed by component id.</summary>
        public IReadOnlyList<int[]> Tiles
        {
            get { return _tiles; }
        }

        /// <summary>How many components there are.</summary>
        public int Count
        {
            get { return _tiles.Count; }
        }

        /// <summary>
        /// The id of the biggest component, or -1 when there is no road at all. Ties go to the
        /// lowest id, which is the component containing the lowest tile index - i.e. the north-west
        /// most - so "the main component" is a deterministic choice and not whichever one a hash
        /// table happened to yield first.
        /// </summary>
        public int LargestId { get; }

        internal RoadComponents(int size, int[] componentOf, List<int[]> tiles)
        {
            _componentOf = componentOf;
            _tiles = tiles;

            var best = -1;
            var bestSize = 0;
            for (int i = 0; i < tiles.Count; i++)
            {
                if (tiles[i].Length > bestSize)
                {
                    bestSize = tiles[i].Length;
                    best = i;
                }
            }
            LargestId = best;
        }

        /// <summary>Whether a tile belongs to a given component.</summary>
        /// <param name="index">A row-major tile index.</param>
        /// <param name="component">A component id.</param>
        public bool Contains(int index, int component)
        {
            return index >= 0 && index < _componentOf.Length && _componentOf[index] == component;
        }

        /// <summary>How many tiles a component holds.</summary>
        /// <param name="component">A component id.</param>
        public int Size(int component)
        {
            return component >= 0 && component < _tiles.Count ? _tiles[component].Length : 0;
        }
    }

    /// <summary>
    /// What the generated network actually looks like, as numbers.
    ///
    /// <para>
    /// This exists because "the neighbourhood has no connecting pieces" is not a question the
    /// graph answers on its own: the invariant only says the network is <em>one piece</em>, and a
    /// network of two hundred one-tile driveways satisfies it perfectly while looking like
    /// scattered stubs. The numbers below are the ones a bug report has to quote - how many road
    /// tiles there are at all, how many runs there are and how long they are, how the network
    /// breaks up, and how much of it is driveway rather than street.
    /// </para>
    /// </summary>
    public readonly struct RoadNetworkStats
    {
        /// <summary>Tiles carrying road, of any kind.</summary>
        public int RoadTiles { get; }

        /// <summary>Tiles on a sidewalk.</summary>
        public int SidewalkTiles { get; }

        /// <summary>Tiles inside a lot.</summary>
        public int LotTiles { get; }

        /// <summary>
        /// The width the streets were asked for - <see cref="RoadGraph.MainStreetWidth"/> - rather
        /// than the width of any one of them, which is <see cref="RoadEdge.Width"/> and can be narrower where the gap was.
        /// See <see cref="RoadGraph.StreetWidth"/>.
        /// </summary>
        public int StreetWidth { get; }

        /// <summary>How many main-street runs there are.</summary>
        public int StreetRuns { get; }

        /// <summary>How many tiles the main streets cover once rasterised.</summary>
        public int StreetTiles { get; }

        /// <summary>The longest single street run, in tiles along its axis.</summary>
        public int LongestStreetRun { get; }

        /// <summary>How many driveway runs there are, including one-tile doorsteps.</summary>
        public int DrivewayRuns { get; }

        /// <summary>
        /// How many driveway runs actually leave the door, i.e. how many lots needed a spur to
        /// reach the street rather than opening straight onto it.
        /// </summary>
        public int DrivewaySpurs { get; }

        /// <summary>How many tiles the driveways cover.</summary>
        public int DrivewayTiles { get; }

        /// <summary>How many runs the connectivity repair had to add.</summary>
        public int RepairRuns { get; }

        /// <summary>How many tiles the repair runs cover.</summary>
        public int RepairTiles { get; }

        /// <summary>How many connected pieces the rasterised network is in.</summary>
        public int Components { get; }

        /// <summary>How many tiles the biggest piece holds.</summary>
        public int LargestComponentTiles { get; }

        /// <summary>How many street junctions the graph has.</summary>
        public int Junctions { get; }

        /// <summary>How many lots have a driveway.</summary>
        public int Entrances { get; }

        /// <summary>How many lots could not be reached from the main piece.</summary>
        public int UnreachableLots { get; }

        internal RoadNetworkStats(
            int roadTiles, int sidewalkTiles, int lotTiles, int streetWidth,
            int streetRuns, int streetTiles, int longestStreetRun,
            int drivewayRuns, int drivewaySpurs, int drivewayTiles,
            int repairRuns, int repairTiles,
            int components, int largestComponentTiles, int junctions,
            int entrances, int unreachableLots)
        {
            RoadTiles = roadTiles;
            SidewalkTiles = sidewalkTiles;
            LotTiles = lotTiles;
            StreetWidth = streetWidth;
            StreetRuns = streetRuns;
            StreetTiles = streetTiles;
            LongestStreetRun = longestStreetRun;
            DrivewayRuns = drivewayRuns;
            DrivewaySpurs = drivewaySpurs;
            DrivewayTiles = drivewayTiles;
            RepairRuns = repairRuns;
            RepairTiles = repairTiles;
            Components = components;
            LargestComponentTiles = largestComponentTiles;
            Junctions = junctions;
            Entrances = entrances;
            UnreachableLots = unreachableLots;
        }

        /// <summary>
        /// The share of the network that is main street rather than driveway. A neighbourhood
        /// where this is tiny is a neighbourhood made of driveway stubs, which is exactly the
        /// failure this type was added to make visible.
        /// </summary>
        public double StreetShare
        {
            get { return RoadTiles == 0 ? 0.0 : (double)StreetTiles / RoadTiles; }
        }

        /// <summary>One line, for a log or a failing assertion's message.</summary>
        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append(StreetRuns).Append(" street run(s) at ").Append(StreetWidth)
              .Append(" wide, ").Append(StreetTiles).Append(" tile(s), longest ")
              .Append(LongestStreetRun);
            sb.Append("; ").Append(DrivewaySpurs).Append(" driveway spur(s) over ")
              .Append(DrivewayRuns).Append(" run(s), ").Append(DrivewayTiles).Append(" tile(s)");
            if (RepairRuns > 0)
            {
                sb.Append("; ").Append(RepairRuns).Append(" repair run(s), ").Append(RepairTiles).Append(" tile(s)");
            }
            sb.Append("; ").Append(RoadTiles).Append(" road tile(s) total, street share ")
              .Append((int)(StreetShare * 100)).Append('%');
            sb.Append("; ").Append(Components).Append(" component(s), largest ")
              .Append(LargestComponentTiles);
            sb.Append("; ").Append(SidewalkTiles).Append(" sidewalk tile(s)");
            sb.Append("; ").Append(Entrances).Append(" entrance(s), ").Append(UnreachableLots)
              .Append(" unreachable");
            return sb.ToString();
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Summary();
        }
    }

    /// <summary>
    /// A drawing decision about a road network: is there anything here to draw, and is it in one
    /// piece. Two flags rather than one because they answer different questions - see
    /// <see cref="RoadGraph.Check"/>.
    /// </summary>
    public readonly struct RoadNetworkCheck
    {
        /// <summary>
        /// Whether the neighbourhood can be rendered at all. False only when there are lots and no
        /// road anywhere between them; a split or partly-stranded network is still drawable.
        /// </summary>
        public bool Drawable { get; }

        /// <summary>
        /// Whether the network is one connected piece with every lot reachable - the §6.1
        /// invariant. False with <see cref="Drawable"/> true means "drawn, but the log should say
        /// what is wrong with it".
        /// </summary>
        public bool SingleNetwork { get; }

        /// <summary>
        /// One line explaining why <see cref="SingleNetwork"/> is false, or - when
        /// <see cref="Drawable"/> is false - what could not be drawn. Null when the network is
        /// clean.
        /// </summary>
        public string Message { get; }

        internal RoadNetworkCheck(bool drawable, bool singleNetwork, string message)
        {
            Drawable = drawable;
            SingleNetwork = singleNetwork;
            Message = message;
        }
    }

    /// <summary>
    /// §6.1: the street skeleton, the driveway spurs, the rasterisation of both into
    /// <c>RoadData</c> nibbles, the sidewalk ring, and the connectivity invariant.
    ///
    /// <para><b>How the streets are found, and why it is the gaps and not the doors.</b></para>
    ///
    /// <para>
    /// The first version of this built a Manhattan minimum spanning tree over the door positions
    /// and snapped its segments to a fixed eight-tile grid. On a real 512x512 neighbourhood -
    /// forty-one 64-tile lots, packed with the two-tile margin
    /// <c>NeighbourhoodLayoutBuilder</c> leaves between them - that produced almost no street. The
    /// lots are on a 66-tile pitch, so the free ground between two rows or columns of them is
    /// exactly two tiles wide and sits at <c>66n + 64</c>: <b>no multiple of eight is ever inside
    /// a gap</b>. Every street node therefore landed inside a lot, every street run was refused
    /// by the "roads never cross a lot interior" rule, and what survived was nine one-tile
    /// stair-stepped repair paths stitching together thirty-odd driveways. Connected, assertable,
    /// and completely invisible - which is the shape of the bug this section exists to prevent.
    /// </para>
    /// <para>
    /// So the skeleton is derived from the gaps between lots instead. For every lot, on every side,
    /// the free run immediately beyond that edge is measured across the lot's whole extent; where
    /// there are <see cref="MainStreetWidth"/> tiles of free ground there is a street, and the
    /// street is that run grown as far as it stays clear. A gap between two lots is therefore one
    /// continuous street running the full height of the block rather than a sequence of stubs, two
    /// lots that share a gap agree on exactly where it runs, and every street is - by construction -
    /// in ground no lot occupies. §6.1's "cluster lot centres into rows/columns by proximity" is
    /// what this does: the gaps <em>are</em> the rows and columns, and "snap segments to a coarse
    /// street grid" is satisfied by the layout's own lattice rather than by a grid unrelated to it.
    /// </para>
    ///
    /// <para><b>The order the stages run in.</b></para>
    /// <list type="number">
    ///   <item><description>
    ///     <b>Streets.</b> Every usable lot contributes, whether or not it still has a house: a
    ///     bulldozed plot's edges are as good a place for a street as a built one, and a
    ///     neighbourhood where one block's roads exist only because those houses do is a
    ///     neighbourhood with holes in it.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Junctions.</b> Two streets that cross are a node. A street that meets nothing is
    ///     given one node so the persisted graph has a vertex to hang its geometry on.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Driveways.</b> One per lot with a house: a doorstep tile on the lot's own border,
    ///     plus an L-shaped spur to the nearest point of the nearest street.
    ///   </description></item>
    /// </list>
    ///
    /// <para><b>Widths.</b> A measured street is <see cref="MainStreetWidth"/> tiles - six, which is
    /// the width of the street the lot view draws around the player - and a driveway
    /// <see cref="DrivewayWidth"/>. The nominal is a target rather than a promise about every
    /// street: one is laid in the ground between the lots, so a street is
    /// <c>min(<see cref="MainStreetWidth"/>, the free gap)</c> and
    /// <see cref="RoadEdge.Width"/> is the truth for any one of them. A layout with no free ground
    /// at all falls back to one tile rather than drawing no streets at all;
    /// <see cref="StreetWidth"/> is what was asked for. A street two tiles wide or more is two
    /// parallel rows of road, so every tile in the interior of a long straight street is a four-way
    /// cross by construction - mask 15, no corner nibble. That is the format working as intended,
    /// not a mistake, and it is why <see cref="RoadMask.IsTurnOrJunction"/> excludes the
    /// cross.</para>
    ///
    /// <para><b>Sidewalks.</b> A one-tile ring around every lot, minus road: §6.1 calls it "a
    /// separate cosmetic strip; does not need a road bit", so it is not in <c>RoadData</c> at all.
    /// Note the consequence of a packing margin narrower than <see cref="MainStreetCorridorWidth"/>,
    /// because it is a real limit rather than an oversight: a gap narrower than the street is street
    /// and nothing else, so the ring survives there as road and the pavement that remains is the
    /// ring on the sides that have no street. Where a gap is wider than the street the surplus is
    /// deliberately left as pavement, up to <see cref="SidewalkWidth"/> tiles against the lot's own
    /// side.</para>
    ///
    /// <para><b>The invariant.</b> A neighbourhood must be one connected road network with every
    /// lot that has a house reachable from it - §6.1's "this is an explicit, assertable
    /// invariant". The raster is not the graph: a street that runs into a lot's interior is
    /// refused, because "roads never cross lot interiors" is the harder invariant and the
    /// visually important one, and a refusal can sever the network. So after rasterising the graph
    /// is flood-filled and, if it came out in more than one piece, a breadth-first search over the
    /// tiles that are <em>not</em> inside a lot finds the cheapest axis-aligned path from an
    /// isolated piece back to the main one; the path is added as <see cref="RoadEdgeKind.Repair"/>
    /// edges and the whole thing is rasterised again. Lots that still cannot be reached are
    /// reported rather than papered over, and <see cref="Check"/> says so out loud - to the
    /// generator's diagnostic line for a surviving failure, and to
    /// <see cref="VerifyConnected"/> as an exception for a caller that wants the invariant as an
    /// assertion. Only one of those is a drawing decision, and only
    /// <see cref="RoadNetworkCheck.Drawable"/> is one: a cul-de-sac the repair could not bridge
    /// still draws as a cul-de-sac, so refusing the whole neighbourhood over one throws away a
    /// world that was right apart from a street end.</para>
    /// </summary>
    public sealed class RoadGraph
    {
        /// <summary>
        /// Main-street thickness in tiles - <b>the nominal width, and the single source of it</b>.
        ///
        /// <para>
        /// Six, because that is the width of the street the lot view draws around the player:
        /// <c>VMLotTerrainRestoreTools.RestoreRoad</c> stamps each lot's own half of it
        /// (<c>TopLeftRoadTile</c> and <c>BottomRightRoadTile</c> are three columns of pattern 9,
        /// black tarmac, with a pattern 12 pavement beyond), and <c>RestoreSurroundings</c> puts the
        /// next lot's origin <c>Size - 2</c> tiles away, so both halves are drawn: three tiles a
        /// side, six in all, in lot tiles - and a generated map tile <em>is</em> a lot tile
        /// (<see cref="NeighbourhoodMapAdapter.MapTilesPerLotTile"/>), so six map tiles. At
        /// <c>FSO.LotView.WorldState.WorldUnitsPerTile</c> = 3 that is eighteen world units, which
        /// is exactly <see cref="NeighbourhoodMapAdapter.LotViewStreetTarmacUnits"/>.
        /// </para>
        ///
        /// <para>
        /// <b>This was two, and that was the bug a player reported as "the roads are a twentieth
        /// of the size they should be".</b> Two tiles is six world units against the lot view's
        /// eighteen, so every generated street was a third of the width of the street it runs past,
        /// drawn inside a lot whose own view already had a full-width road around it. The correction
        /// is here and only here: the mesh draws exactly the tiles this file marks
        /// (<see cref="NeighbourhoodCityMeshBuilder.RoadQuadWidthMapTiles"/> is one tile and does
        /// not scale), the adapter's <see cref="NeighbourhoodMapAdapter.StreetTarmacMapTiles"/>
        /// reads this constant rather than restating it, and a sub-world is placed on a raw
        /// map-tile delta, which is this same tile.
        /// </para>
        ///
        /// <para>
        /// <b>The nominal width is a target, not a promise about every street.</b> A street is laid
        /// in ground the packer left, so a street is <c>min(this, the free gap)</c> - see
        /// <see cref="MeasureBands"/>. The packer's margin
        /// (<see cref="NeighbourhoodLayoutBuilder.SidewalkMargin"/>) is what decides which of the
        /// two a given street gets, the honest number for any one street is
        /// <see cref="RoadEdge.Width"/>, and <see cref="StreetWidth"/> is what this graph asked
        /// for.
        /// </para>
        /// </summary>
        public const int MainStreetWidth = 6;

        /// <summary>Driveway and repair-run thickness in tiles, per §6.1.</summary>
        public const int DrivewayWidth = 1;

        /// <summary>
        /// The sidewalk ring's thickness around a lot, and the most pavement the generator will
        /// leave between a street and a lot's border when a gap is wider than the street.
        /// </summary>
        public const int SidewalkWidth = 1;

        /// <summary>
        /// The ground one street occupies: its tarmac plus the pavement on each side. Eight tiles,
        /// twenty-four world units.
        ///
        /// <para>
        /// This is the number a gap between two lots has to be for a full-width street to appear:
        /// a gap narrower than the tarmac can only carry a narrower street, and a gap of exactly
        /// <see cref="MainStreetWidth"/> carries a street with no pavement at all
        /// (see <see cref="BandStart"/>). It is written out rather than left to a reader to add up
        /// because it is the one of the three that a change to the layout builder has to agree with
        /// - <see cref="NeighbourhoodLayoutBuilder.StreetCorridorTiles"/> is that side's copy of the
        /// same number - and <see cref="NeighbourhoodMapAdapter.StreetCorridorMapTiles"/> is the
        /// adapter's.
        /// </para>
        /// <para>
        /// <b>It no longer says anything about how lots are placed.</b> Since Part 8 the lots are at
        /// their real <c>LotLocations</c> positions, so the gaps between them are whatever the 2D map
        /// has and this constant is only the width a street is measured against when the skeleton is
        /// derived from them rather than read off the backdrop.
        /// </para>
        /// </summary>
        public const int MainStreetCorridorWidth = MainStreetWidth + 2 * SidewalkWidth;

        /// <summary>
        /// How far a gap is measured outward from a lot's edge. Past this the gap is open ground
        /// rather than a street between two things, and the only thing its length is still used
        /// for is how much pavement the street leaves behind - which <see cref="SidewalkWidth"/>
        /// caps anyway.
        /// </summary>
        public const int MaxCorridorScan = 24;

        /// <summary>
        /// How far the connectivity repair may go before giving up. Each pass adds at least one
        /// edge and removes at least one component, so a handful is generous; the bound exists so
        /// a pathological layout costs a fixed amount of time instead of looping.
        /// </summary>
        public const int MaxRepairPasses = 16;

        /// <summary>
        /// How far a door anchor may be pushed outward to escape another lot's interior.
        /// </summary>
        public const int MaxAnchorSearch = 64;

        /// <summary>
        /// How many alternative street points a driveway considers before settling for the
        /// nearest one. A driveway normally opens straight onto a street and this costs one
        /// comparison; the alternatives only matter when the nearest street is across a lot.
        /// </summary>
        public const int MaxDrivewayCandidates = 6;

        private readonly int _size;
        private readonly List<RoadNode> _nodes = new List<RoadNode>();
        private readonly List<RoadEdge> _edges = new List<RoadEdge>();
        private readonly List<RoadEdge> _streets = new List<RoadEdge>();
        private readonly List<NeighbourhoodEntrance> _entrances = new List<NeighbourhoodEntrance>();
        private readonly List<RoadRepair> _repairs = new List<RoadRepair>();
        private readonly List<short> _unreachable = new List<short>();
        private readonly bool[] _road;
        private readonly bool[] _sidewalk;
        private readonly bool[] _streetTiles;
        private readonly bool[] _drivewayTiles;

        private int _streetWidth = MainStreetWidth;

        /// <summary>The lot rectangles, rasterised. Never null after construction.</summary>
        public NeighbourhoodLotMask LotMask { get; }

        /// <summary>
        /// The road graph's vertices: one per street crossing, plus one for any street that meets
        /// no other. In creation order, which is ascending lot order then ascending street
        /// coordinate, so the list is stable. Persistable as-is.
        /// </summary>
        public IReadOnlyList<RoadNode> Nodes
        {
            get { return _nodes; }
        }

        /// <summary>
        /// The graph's edges: the streets, then the driveways, then any repair runs, in that
        /// order. Persistable as-is.
        /// </summary>
        public IReadOnlyList<RoadEdge> Edges
        {
            get { return _edges; }
        }

        /// <summary>
        /// The main-street runs on their own, in the order they were added. This is the list a
        /// renderer draws and a diagnostic counts: it is the difference between "there are
        /// streets" and "there are only doorsteps".
        /// </summary>
        public IReadOnlyList<RoadEdge> StreetRuns
        {
            get { return _streets; }
        }

        /// <summary>
        /// One entrance per lot that has a house, in lot-input order. This is the road graph's
        /// answer to §15's <c>ILotEntrance</c>.
        /// </summary>
        public IReadOnlyList<NeighbourhoodEntrance> Entrances
        {
            get { return _entrances; }
        }

        /// <summary>Every run the connectivity repair added, in the order it added them.</summary>
        public IReadOnlyList<RoadRepair> Repairs
        {
            get { return _repairs; }
        }

        /// <summary>
        /// Lots with a house whose driveway did not end up on the main road network, after
        /// <see cref="RefreshReachability"/>. Empty in any neighbourhood that verifies.
        /// </summary>
        public IReadOnlyList<short> UnreachableLots
        {
            get { return _unreachable; }
        }

        private readonly List<short> _offMap = new List<short>();

        /// <summary>
        /// Lots whose driveway could not be placed: no side of the rectangle had ground outside it
        /// that a road may occupy. Empty for any layout <c>VerifyConnected</c> accepts, because a
        /// lot with no entrance is a lot the reachability check cannot even ask about.
        /// </summary>
        public IReadOnlyList<short> OffMapLots
        {
            get { return _offMap; }
        }

        /// <summary>
        /// The width the streets were asked for: <see cref="MainStreetWidth"/>, one tile when the
        /// packing left no free ground at all.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the nominal, and the per-street answer is <see cref="RoadEdge.Width"/>.</b> A
        /// measured street is laid in the ground the packer left, so a street is
        /// <c>min(this, the free gap)</c> - a street in a gap of <see cref="MainStreetWidth"/> or
        /// more is this wide, and a street in a narrower gap is as wide as the gap. Both are correct
        /// and they are the reason this is not a promise about every edge:
        /// <see cref="StreetRuns"/>' widths are the ground truth, and
        /// <see cref="RoadNetworkStats"/>'s <see cref="RoadNetworkStats.StreetWidth"/> is this same
        /// nominal.
        /// </para>
        /// </remarks>
        public int StreetWidth
        {
            get { return _streetWidth; }
        }

        /// <summary>
        /// Road occupancy per tile, row-major. True exactly where
        /// <see cref="NeighbourhoodMap.RoadData"/>'s low nibble is non-zero.
        /// </summary>
        public bool[] Road
        {
            get { return _road; }
        }

        /// <summary>
        /// The sidewalk ring, row-major: a one-tile band just outside each lot, with road tiles
        /// removed (a sidewalk under a road is not a sidewalk) and lot interiors never included.
        /// §6.1 calls this "a separate cosmetic strip; does not need a road bit", so it is not in
        /// <c>RoadData</c> at all - a renderer draws it as extra flat geometry.
        /// </summary>
        public bool[] Sidewalk
        {
            get { return _sidewalk; }
        }

        private RoadGraph(int size, NeighbourhoodLotMask lotMask, IReadOnlyList<short> offMapLots)
        {
            _size = size;
            LotMask = lotMask;
            _offMap.AddRange(offMapLots);
            _road = new bool[size * size];
            _sidewalk = new bool[size * size];
            _streetTiles = new bool[size * size];
            _drivewayTiles = new bool[size * size];
        }

        /// <summary>The 4-bit adjacency of a road tile, by the nibble rule's own definition.</summary>
        private byte AdjacencyAt(int x, int y)
        {
            byte mask = RoadMask.None;
            if (IsRoad(x + 1, y)) mask |= RoadMask.East;
            if (IsRoad(x, y + 1)) mask |= RoadMask.South;
            if (IsRoad(x - 1, y)) mask |= RoadMask.West;
            if (IsRoad(x, y - 1)) mask |= RoadMask.North;
            return mask;
        }

        /// <summary>
        /// Builds the network and rasterises it, then runs the connectivity repair and the
        /// reachability check. This is what <c>NeighbourhoodMapGenerator</c> calls; the stages are
        /// also public separately so a test can look at the network <em>before</em> repair.
        /// </summary>
        /// <param name="input">The generation input.</param>
        /// <param name="map">The map to fill. Must be <see cref="NeighbourhoodMapInput.MapSize"/> square.</param>
        public static RoadGraph Build(NeighbourhoodMapInput input, NeighbourhoodMap map)
        {
            var graph = BuildNetwork(input);
            graph.Rasterise(map);
            graph.RepairConnectivity(map);
            graph.RefreshReachability(map);
            return graph;
        }

        /// <summary>
        /// Builds the network - streets measured from the gaps between lots, then driveways - but
        /// does not touch a map. Everything downstream of this is a function of the network plus
        /// the lot mask.
        /// </summary>
        /// <param name="input">The generation input.</param>
        public static RoadGraph BuildNetwork(NeighbourhoodMapInput input)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));

            var size = input.MapSize;
            var mask = NeighbourhoodLotMask.Build(input.UsableLots, size);
            var offMap = new List<short>();
            var graph = new RoadGraph(size, mask, offMap);

            // Streets first: a driveway has to know what it is aiming at, and every lot
            // contributes its gaps whether or not it still has a house.
            graph.BuildStreets(input.UsableLots);
            graph.BuildJunctions();
            graph.BuildDriveways(input.UsableLots);
            return graph;
        }

        // ---------------------------------------------------------------- streets

        /// <summary>
        /// A candidate street, before it has been grown: a band of free ground and the stretch
        /// that vouched for it.
        /// </summary>
        private readonly struct StreetBand
        {
            /// <summary>True for a band of columns (a street running down the map), false for rows.</summary>
            public bool Vertical { get; }

            /// <summary>The band's low x, if vertical, or low y if horizontal.</summary>
            public int Start { get; }

            /// <summary>The low coordinate on the other axis.</summary>
            public int Lo { get; }

            /// <summary>The high coordinate on the other axis, inclusive.</summary>
            public int Hi { get; }

            /// <summary>
            /// How thick this band is, in tiles: the free run it was measured from, capped at
            /// <see cref="MainStreetWidth"/>. Per band rather than global, because that cap is the
            /// whole of the degradation rule - a street in a wide gap is the full street and a
            /// street in a narrow one is as wide as the narrow one, and both are correct answers to
            /// "how wide is this street".
            /// </summary>
            public int Width { get; }

            public StreetBand(bool vertical, int start, int lo, int hi, int width)
            {
                Vertical = vertical;
                Start = start;
                Lo = lo;
                Hi = hi;
                Width = width < DrivewayWidth ? DrivewayWidth : width;
            }
        }

        private void BuildStreets(IReadOnlyList<NeighbourhoodLotInput> lots)
        {
            var bands = MeasureBands(lots);

            // A packing with no free run at all still gets streets, just narrow ones. The
            // alternative - "no streets at all" - is the bug this whole file is a fix for, so the
            // graceful degradation is one tile rather than nothing.
            if (bands.Count == 0 && _streetWidth > DrivewayWidth)
            {
                _streetWidth = DrivewayWidth;
                bands = MeasureBands(lots);
            }

            var vertical = new SortedDictionary<int, List<int[]>>();
            var horizontal = new SortedDictionary<int, List<int[]>>();
            var canonical = new Dictionary<int, int>[]
            {
                CanonicalStarts(bands, true),
                CanonicalStarts(bands, false),
            };
            for (int i = 0; i < bands.Count; i++)
            {
                var band = bands[i];
                var into = band.Vertical ? vertical : horizontal;
                var start = canonical[band.Vertical ? 0 : 1][band.Start];
                List<int[]> seeds;
                if (!into.TryGetValue(start, out seeds))
                {
                    seeds = new List<int[]>();
                    into.Add(start, seeds);
                }
                // lo, hi and this band's own thickness. Two lots facing each other across the same
                // gap agree on all three; a third that measured the same position at a narrower
                // point of its extent contributes a thinner band at the same start rather than a
                // second street on top of the first, which is what GroupByWidth is for.
                seeds.Add(new[] { band.Lo, band.Hi, band.Width });
            }

            foreach (var pair in vertical)
            {
                var column = pair.Key;
                foreach (var group in GroupByWidth(pair.Value))
                {
                    foreach (var range in GrowVertical(column, group.Width, MergeRanges(group.Ranges)))
                    {
                        AddStreet(new RoadEdge(column, range[0], column, range[1],
                            group.Width, RoadEdgeKind.Street));
                    }
                }
            }
            foreach (var pair in horizontal)
            {
                var row = pair.Key;
                foreach (var group in GroupByWidth(pair.Value))
                {
                    foreach (var range in GrowHorizontal(row, group.Width, MergeRanges(group.Ranges)))
                    {
                        AddStreet(new RoadEdge(range[0], row, range[1], row,
                            group.Width, RoadEdgeKind.Street));
                    }
                }
            }
        }

        /// <summary>One thickness's worth of seed ranges at a single street position.</summary>
        private readonly struct WidthGroup
        {
            /// <summary>The thickness every range in the group is drawn at.</summary>
            public int Width { get; }

            /// <summary>The seed ranges, as <c>{lo, hi}</c> pairs.</summary>
            public List<int[]> Ranges { get; }

            public WidthGroup(int width, List<int[]> ranges)
            {
                Width = width;
                Ranges = ranges;
            }
        }

        /// <summary>
        /// The seeds at one street position, split by thickness.
        /// </summary>
        /// <remarks>
        /// Two lots facing each other across the same gap produce the same band and so the same
        /// thickness, which makes the common case a single group and this the identity. It only
        /// does anything where a gap changes width along a street - a band measured at a narrow
        /// point of the gap and one measured at a wide point - because a street edge carries one
        /// <see cref="RoadEdge.Width"/> and the graph has to stay a list of rectangles. Splitting
        /// keeps both widths honest instead of stamping the wider number onto a run the ground
        /// cannot hold.
        /// </remarks>
        private static List<WidthGroup> GroupByWidth(List<int[]> seeds)
        {
            var widths = new List<int>();
            var ranges = new List<List<int[]>>();
            for (int i = 0; i < seeds.Count; i++)
            {
                var width = seeds[i][2];
                var found = widths.IndexOf(width);
                if (found < 0)
                {
                    widths.Add(width);
                    ranges.Add(new List<int[]> { new[] { seeds[i][0], seeds[i][1] } });
                }
                else
                {
                    ranges[found].Add(new[] { seeds[i][0], seeds[i][1] });
                }
            }

            var groups = new List<WidthGroup>();
            for (int g = 0; g < widths.Count; g++)
            {
                groups.Add(new WidthGroup(widths[g], ranges[g]));
            }
            return groups;
        }

        private void AddStreet(RoadEdge edge)
        {
            _streets.Add(edge);
            _edges.Add(edge);
        }

        /// <summary>
        /// Collapses the candidate band positions that are really the same street.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Two lots facing each other across a gap agree on where the street goes exactly, except
        /// when one of them saw the gap run on into open ground and stopped measuring while the
        /// other found a lot at the far end of it. They then disagree by at most
        /// <see cref="SidewalkWidth"/> tiles, and two streets one tile apart are not two streets -
        /// they are one street with a ragged kerb, which is what an unchecked pair of bands
        /// produced at the corner of the last block in a map.
        /// </para>
        /// <para>
        /// The grouping is over the whole set rather than over what has been seen so far, so it
        /// cannot depend on the order the lots were visited in. That matters more than it looks:
        /// the layout builder is deterministic about lot order today, but a generator whose streets
        /// move when the same lots are listed the other way round is one refactor away from a bug nobody
        /// can reproduce.
        /// </para>
        /// </remarks>
        private static Dictionary<int, int> CanonicalStarts(List<StreetBand> bands, bool vertical)
        {
            var starts = new SortedSet<int>();
            for (int i = 0; i < bands.Count; i++)
            {
                if (bands[i].Vertical == vertical) starts.Add(bands[i].Start);
            }

            var map = new Dictionary<int, int>();
            var canonical = int.MinValue;
            var first = true;
            foreach (var start in starts)
            {
                if (first || start - canonical > SidewalkWidth)
                {
                    canonical = start;
                    first = false;
                }
                map[start] = canonical;
            }
            return map;
        }

        /// <summary>
        /// Measures the free run beyond each of every lot's four edges and turns the ones wide
        /// enough for a street into a candidate band.
        /// </summary>
        /// <remarks>
        /// The run is measured across the lot's <em>whole</em> extent on the other axis, not just
        /// its middle tile. A gap that exists only at the lot's centre would make a band whose
        /// seed range is not actually free, and a street that has to be re-cut around every lot it
        /// passes is a street made of fragments - the exact failure the previous version had.
        /// Requiring the whole extent keeps every street straight from one end to the other.
        /// </remarks>
        private List<StreetBand> MeasureBands(IReadOnlyList<NeighbourhoodLotInput> lots)
        {
            var bands = new List<StreetBand>();
            if (lots == null || lots.Count == 0) return bands;

            for (int i = 0; i < lots.Count; i++)
            {
                var lot = lots[i];
                var top = Math.Max(0, lot.WorldY);
                var bottom = Math.Min(_size, lot.Bottom) - 1;
                var left = Math.Max(0, lot.WorldX);
                var right = Math.Min(_size, lot.Right) - 1;
                if (bottom < top || right < left) continue;

                int run;

                if (FreeRunVertical(lot.Right, top, bottom, 1, out run))
                {
                    bands.Add(Band(true, lot.Right, run, top, bottom));
                }
                if (lot.WorldX - 1 >= 0
                    && FreeRunVertical(lot.WorldX - 1, top, bottom, -1, out run))
                {
                    bands.Add(Band(true, lot.WorldX - run, run, top, bottom));
                }
                if (FreeRunHorizontal(lot.Bottom, left, right, 1, out run))
                {
                    bands.Add(Band(false, lot.Bottom, run, left, right));
                }
                if (lot.WorldY - 1 >= 0
                    && FreeRunHorizontal(lot.WorldY - 1, left, right, -1, out run))
                {
                    bands.Add(Band(false, lot.WorldY - run, run, left, right));
                }
            }

            return bands;
        }

        /// <summary>
        /// One candidate band from one measured free run: as wide as the run allows, up to the
        /// nominal <see cref="MainStreetWidth"/>, placed by <see cref="BandStart"/>.
        /// </summary>
        /// <remarks>
        /// <b>Any free run is a street, and the run decides how wide.</b> The previous version
        /// required <c>run &gt;= MainStreetWidth</c> and then drew the whole nominal width, which
        /// meant a packing with a two-tile margin found no bands at all and fell back to one-tile
        /// streets everywhere - the nominal width was unreachable precisely when the lots were
        /// packed, which is the case the generator is for. Taking the width from the run instead
        /// makes a wide gap a full-width street and the packer's narrow margin a narrower one, and
        /// makes the two agree with each other by construction rather than by a fallback.
        /// </remarks>
        private StreetBand Band(bool vertical, int lo, int run, int from, int to)
        {
            var width = run < _streetWidth ? run : _streetWidth;
            return new StreetBand(vertical, BandStart(lo, run, width), from, to, width);
        }

        /// <summary>
        /// Where inside a measured gap the street goes.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <paramref name="lo"/> is the low end of the free run and <paramref name="run"/> its
        /// length. Whatever the gap has beyond the street's own width is pavement, and as much of it
        /// as <see cref="SidewalkWidth"/> is kept against the lot - which is the only way §6.1's
        /// one-tile sidewalk ring survives at all. The pad rounds <em>up</em> so an odd surplus
        /// still leaves the pavement on the lot's side rather than the far side: a lot wants a
        /// footway in front of its own door, not one on the other side of the street.
        /// </para>
        /// <para>
        /// Two lots facing each other across the same gap get the same answer, because both derive
        /// <paramref name="lo"/> and <paramref name="run"/> from the same free run and both take
        /// the low end. That is what makes a street one street rather than two proposals for two
        /// streets.
        /// </para>
        /// <para>
        /// The limit is worth stating plainly, because it is a real one and not a shortfall in the
        /// search. <paramref name="width"/> is the width the ground allows
        /// (<c>min(<see cref="MainStreetWidth"/>, run)</c>), so the surplus here is never negative -
        /// but the surplus is capped at <see cref="SidewalkWidth"/> on the lot's side and nothing
        /// more, and a gap exactly <see cref="MainStreetWidth"/> wide therefore carries a street
        /// with no pavement at all. Since Part 8 the lots are at their real positions, so the gaps
        /// between them are whatever the 2D map has: a full
        /// <see cref="MainStreetWidth"/>-tile street with a footway on each side needs
        /// <see cref="MainStreetCorridorWidth"/> tiles between two lots, and whether there are that
        /// many is now a fact about the neighbourhood rather than a margin this pipeline chose.
        /// <see cref="RoadNetworkStats.SidewalkTiles"/> is the honest number either way.
        /// </para>
        /// </remarks>
        private static int BandStart(int lo, int run, int width)
        {
            var spare = run - width;
            if (spare < 0) spare = 0;
            var pad = (spare + 1) / 2;
            if (pad > SidewalkWidth) pad = SidewalkWidth;
            return lo + pad;
        }

        /// <summary>
        /// Counts how many consecutive tiles outward from <paramref name="column"/> are outside
        /// every lot, for every row in the cross-section at once. Bounded by
        /// <see cref="MaxCorridorScan"/>.
        /// </summary>
        private bool FreeRunVertical(int column, int y0, int y1, int dir, out int run)
        {
            run = 0;
            var x = column;
            while (run < MaxCorridorScan)
            {
                if (x < 0 || x >= _size) break;
                if (!ColumnFree(x, y0, y1)) break;
                run++;
                x += dir;
            }
            return run > 0;
        }

        /// <summary>
        /// The horizontal counterpart of <see cref="FreeRunVertical"/>.
        /// </summary>
        private bool FreeRunHorizontal(int row, int x0, int x1, int dir, out int run)
        {
            run = 0;
            var y = row;
            while (run < MaxCorridorScan)
            {
                if (y < 0 || y >= _size) break;
                if (!RowFree(y, x0, x1)) break;
                run++;
                y += dir;
            }
            return run > 0;
        }

        private bool ColumnFree(int x, int y0, int y1)
        {
            for (int y = y0; y <= y1; y++)
            {
                if (LotMask.Interior[y * _size + x]) return false;
            }
            return true;
        }

        private bool RowFree(int y, int x0, int x1)
        {
            for (int x = x0; x <= x1; x++)
            {
                if (LotMask.Interior[y * _size + x]) return false;
            }
            return true;
        }

        /// <summary>Sorts and merges touching or overlapping inclusive ranges.</summary>
        private static List<int[]> MergeRanges(List<int[]> ranges)
        {
            var sorted = new List<int[]>(ranges);
            sorted.Sort((a, b) => a[0] != b[0] ? a[0].CompareTo(b[0]) : a[1].CompareTo(b[1]));

            var merged = new List<int[]>();
            for (int i = 0; i < sorted.Count; i++)
            {
                if (merged.Count > 0 && sorted[i][0] <= merged[merged.Count - 1][1] + 1)
                {
                    if (sorted[i][1] > merged[merged.Count - 1][1]) merged[merged.Count - 1][1] = sorted[i][1];
                }
                else
                {
                    merged.Add(new[] { sorted[i][0], sorted[i][1] });
                }
            }
            return merged;
        }

        /// <summary>
        /// Grows one seeded range along its column, and returns the run it became.
        /// </summary>
        /// <remarks>
        /// Growth stops where the band is no longer <paramref name="width"/> wide of clear ground,
        /// which is the same rule as before with the width supplied per band rather than read off
        /// one global. A narrower band therefore grows at least as far as a wider one would, which
        /// is what lets a street widen along its length as the ground allows.
        /// </remarks>
        private List<int[]> GrowVertical(int column, int width, List<int[]> seeds)
        {
            var grown = new List<int[]>(seeds.Count);
            for (int i = 0; i < seeds.Count; i++)
            {
                var lo = seeds[i][0];
                var hi = seeds[i][1];
                while (lo > 0 && BandFreeVertical(column, width, lo - 1)) lo--;
                while (hi < _size - 1 && BandFreeVertical(column, width, hi + 1)) hi++;
                grown.Add(new[] { lo, hi });
            }
            return MergeRanges(grown);
        }

        /// <summary>Grows one seeded range along its row, and returns the run it became.</summary>
        private List<int[]> GrowHorizontal(int row, int width, List<int[]> seeds)
        {
            var grown = new List<int[]>(seeds.Count);
            for (int i = 0; i < seeds.Count; i++)
            {
                var lo = seeds[i][0];
                var hi = seeds[i][1];
                while (lo > 0 && BandFreeHorizontal(row, width, lo - 1)) lo--;
                while (hi < _size - 1 && BandFreeHorizontal(row, width, hi + 1)) hi++;
                grown.Add(new[] { lo, hi });
            }
            return MergeRanges(grown);
        }

        /// <summary>Whether the whole street width is clear of lots on one row of a column band.</summary>
        private bool BandFreeVertical(int column, int width, int y)
        {
            for (int x = column; x <= column + width - 1; x++)
            {
                if (x < 0 || x >= _size) return false;
                if (LotMask.Interior[y * _size + x]) return false;
            }
            return true;
        }

        /// <summary>Whether the whole street width is clear of lots on one column of a row band.</summary>
        private bool BandFreeHorizontal(int row, int width, int x)
        {
            for (int y = row; y <= row + width - 1; y++)
            {
                if (y < 0 || y >= _size) return false;
                if (LotMask.Interior[y * _size + x]) return false;
            }
            return true;
        }

        // ---------------------------------------------------------------- junctions

        /// <summary>
        /// One node per place two streets cross, and one for any street that crosses nothing -
        /// a persisted graph with an edge but no vertex on it cannot be drawn or routed on.
        /// </summary>
        private void BuildJunctions()
        {
            var seen = new Dictionary<long, int>();
            var crossings = new int[_streets.Count];

            for (int i = 0; i < _streets.Count; i++)
            {
                var a = _streets[i];
                for (int j = 0; j < _streets.Count; j++)
                {
                    if (i == j) continue;
                    var b = _streets[j];
                    if (a.IsVertical == b.IsVertical) continue;
                    if (!TryCrossing(a, b, out int x, out int y)) continue;
                    crossings[i]++;
                    crossings[j]++;
                    AddNode(seen, x, y);
                }
            }

            for (int i = 0; i < _streets.Count; i++)
            {
                if (crossings[i] > 0) continue;
                var e = _streets[i];
                AddNode(seen, (e.AX + e.BX) / 2, (e.AY + e.BY) / 2);
            }
        }

        /// <summary>The low corner of the rectangle two crossing streets share.</summary>
        private static bool TryCrossing(RoadEdge a, RoadEdge b, out int x, out int y)
        {
            var v = a.IsVertical ? a : b;
            var h = a.IsVertical ? b : a;
            var x0 = Math.Max(v.AX, h.AX);
            var x1 = Math.Min(v.AX + v.Width - 1, h.BX);
            var y0 = Math.Max(h.AY, v.AY);
            var y1 = Math.Min(h.AY + h.Width - 1, v.BY);
            x = x0;
            y = y0;
            return x0 <= x1 && y0 <= y1;
        }

        private void AddNode(Dictionary<long, int> seen, int x, int y)
        {
            if (x < 0 || y < 0 || x >= _size || y >= _size) return;
            var key = ((long)x << 32) | (uint)y;
            if (seen.ContainsKey(key)) return;
            seen.Add(key, _nodes.Count);
            _nodes.Add(new RoadNode(x, y, true, -1));
        }

        // ---------------------------------------------------------------- driveways

        private void BuildDriveways(IReadOnlyList<NeighbourhoodLotInput> lots)
        {
            for (int i = 0; i < lots.Count; i++)
            {
                var lot = lots[i];
                if (!lot.HasHouse) continue;

                byte side;
                int ax, ay;
                if (!TryResolveEntrance(lot, out side, out ax, out ay))
                {
                    // No side of this lot has ground outside it that a road may occupy - the lot
                    // is walled in on all four sides. Keep a mutable field for exactly this.
                    _offMap.Add(lot.Number);
                    continue;
                }

                _entrances.Add(new NeighbourhoodEntrance(
                    lot.Number, ax, ay,
                    NeighbourhoodDoorSide.DeltaX(side),
                    NeighbourhoodDoorSide.DeltaY(side),
                    side));

                // A one-tile doorstep at the door itself, before anything else. When the driveway
                // has length this changes nothing - the L-run already covers the anchor - but when
                // the door opens straight onto the street the run is empty, and without the
                // doorstep the door tile would not be road at all. That is not a cosmetic gap: a
                // lot whose door lands exactly on a street would then be reported as a lot with
                // no road access, which is the generator refusing to draw a neighbourhood it
                // drew perfectly well.
                _edges.Add(new RoadEdge(ax, ay, ax, ay, DrivewayWidth, RoadEdgeKind.Driveway));

                int tx, ty;
                if (TryRouteToStreet(ax, ay, out tx, out ty))
                {
                    AddLRoute(ax, ay, tx, ty, DrivewayWidth, RoadEdgeKind.Driveway);
                }
            }
        }

        /// <summary>
        /// The door anchor for a lot and the side it leaves from: its own
        /// <see cref="NeighbourhoodLotInput.DoorSide"/> if that side has ground outside the map
        /// edge, and otherwise the first side in the fixed order East, South, West, North that
        /// does.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The fallback is not cosmetic. <c>NeighbourhoodLayoutBuilder.DoorSideFor</c> picks the
        /// edge nearest a map border, which for the lots packed along the edge of the map means a
        /// door pointing off the edge of the world: the anchor would be at y = -1, there is no
        /// free tile to step to, and the lot drops out of the network entirely. On a forty-one lot
        /// map that is twelve lots with no driveway and no road of their own, silently, because a
        /// lot that never gets an entrance is not a failure the connectivity check can see.
        /// </para>
        /// <para>
        /// The order is fixed rather than "whichever side has the biggest gap" so the choice is a
        /// function of the lot alone. Corner lots consequently turn their driveways to face along
        /// the street, which is what a corner house's driveway does.
        /// </para>
        /// </remarks>
        public bool TryResolveEntrance(
            NeighbourhoodLotInput lot, out byte side, out int x, out int y)
        {
            for (int i = 0; i < 5; i++)
            {
                // The lot's own side first, then the other four in a fixed order. Index 0 is the
                // lot's own side whatever its value, so the loop runs five times for four sides
                // and the repeat is skipped by the same check the rest of this method uses.
                var candidate = i == 0
                    ? lot.DoorSide
                    : (byte)((NeighbourhoodDoorSide.East + i - 1) % 4);
                if (i > 0 && candidate == lot.DoorSide) continue;

                if (TryResolveAnchor(lot, candidate, LotMask, _size, out x, out y))
                {
                    side = candidate;
                    return true;
                }
            }

            side = lot.DoorSide;
            x = 0;
            y = 0;
            return false;
        }

        /// <summary>
        /// The door anchor for a lot on its own door side: the middle of that edge, one tile
        /// outside, moved outward until it is a tile a road may occupy.
        /// </summary>
        /// <param name="lot">The lot.</param>
        /// <param name="mask">The rasterised lot rectangles.</param>
        /// <param name="size">The map's side length.</param>
        /// <param name="x">The anchor's x.</param>
        /// <param name="y">The anchor's y.</param>
        /// <returns>False when no free tile could be found within <see cref="MaxAnchorSearch"/>.</returns>
        public static bool TryResolveAnchor(
            NeighbourhoodLotInput lot, NeighbourhoodLotMask mask, int size, out int x, out int y)
        {
            return TryResolveAnchor(lot, lot.DoorSide, mask, size, out x, out y);
        }

        /// <summary>
        /// <see cref="TryResolveAnchor(NeighbourhoodLotInput,NeighbourhoodLotMask,int,out int,out int)"/>
        /// for a named side, which is what the door-side fallback needs.
        /// </summary>
        /// <param name="lot">The lot.</param>
        /// <param name="side">The side to leave from.</param>
        /// <param name="mask">The rasterised lot rectangles.</param>
        /// <param name="size">The map's side length.</param>
        /// <param name="x">The anchor's x.</param>
        /// <param name="y">The anchor's y.</param>
        public static bool TryResolveAnchor(
            NeighbourhoodLotInput lot, byte side, NeighbourhoodLotMask mask, int size,
            out int x, out int y)
        {
            var dx = NeighbourhoodDoorSide.DeltaX(side);
            var dy = NeighbourhoodDoorSide.DeltaY(side);
            var size2 = lot.EffectiveSize;

            // The middle of the door edge, one tile outside it. A side with no normal component
            // (impossible for a real door side, but Clamp folds a corrupt one to North) falls
            // back to the middle of the lot's top edge.
            int startX = dx > 0 ? lot.WorldX + size2 : (dx < 0 ? lot.WorldX - 1 : lot.WorldX + size2 / 2);
            int startY = dy > 0 ? lot.WorldY + size2 : (dy < 0 ? lot.WorldY - 1 : lot.WorldY + size2 / 2);

            for (int step = 0; step <= MaxAnchorSearch; step++)
            {
                var tx = startX + dx * step;
                var ty = startY + dy * step;
                if (tx < 0 || ty < 0 || tx >= size || ty >= size) break;
                if (mask.IsInterior(tx, ty)) continue;
                x = tx;
                y = ty;
                return true;
            }

            x = 0;
            y = 0;
            return false;
        }

        /// <summary>
        /// Picks the street point a driveway runs to: the nearest point of the nearest street
        /// whose L-shaped route back to the door does not cross a lot.
        /// </summary>
        private bool TryRouteToStreet(int ax, int ay, out int tx, out int ty)
        {
            tx = ax;
            ty = ay;

            if (_streets.Count == 0) return false;

            var candidates = new List<int[]>();
            for (int i = 0; i < _streets.Count && candidates.Count < MaxDrivewayCandidates * 4; i++)
            {
                int cx, cy, distance;
                NearestPoint(_streets[i], ax, ay, out cx, out cy, out distance);
                candidates.Add(new[] { cx, cy, distance, i });
            }
            if (candidates.Count == 0) return false;

            // Deterministic order: nearest first, then the street that produced it, then the tile
            // itself. The last two matter because two points on the same street are equally near
            // more often than not, and a sort that is not total would make the map depend on it.
            candidates.Sort(CompareCandidates);

            var chosen = candidates[0];
            for (int i = 0; i < candidates.Count && i < MaxDrivewayCandidates; i++)
            {
                if (!RouteIsClear(ax, ay, candidates[i][0], candidates[i][1])) continue;
                chosen = candidates[i];
                break;
            }

            tx = chosen[0];
            ty = chosen[1];
            return true;
        }

        private static int CompareCandidates(int[] a, int[] b)
        {
            if (a[2] != b[2]) return a[2].CompareTo(b[2]);
            if (a[3] != b[3]) return a[3].CompareTo(b[3]);
            if (a[0] != b[0]) return a[0].CompareTo(b[0]);
            return a[1].CompareTo(b[1]);
        }

        /// <summary>The tile of a street that is nearest to a point, and how far away it is.</summary>
        private static void NearestPoint(RoadEdge edge, int x, int y, out int cx, out int cy, out int distance)
        {
            if (edge.IsVertical)
            {
                cx = Clamp(x, edge.AX, edge.AX + edge.Width - 1);
                cy = Clamp(y, edge.AY, edge.BY);
            }
            else
            {
                cx = Clamp(x, edge.AX, edge.BX);
                cy = Clamp(y, edge.AY, edge.AY + edge.Width - 1);
            }
            distance = Math.Abs(x - cx) + Math.Abs(y - cy);
        }

        private static int Clamp(int value, int lo, int hi)
        {
            if (value < lo) return lo;
            return value > hi ? hi : value;
        }

        /// <summary>Whether the L-shaped route between two points stays off every lot interior.</summary>
        private bool RouteIsClear(int ax, int ay, int bx, int by)
        {
            var horizontalFirst = Math.Abs(bx - ax) >= Math.Abs(by - ay);
            var ex = horizontalFirst ? bx : ax;
            var ey = horizontalFirst ? ay : by;
            return LineIsClear(ax, ay, ex, ey) && LineIsClear(ex, ey, bx, by);
        }

        private bool LineIsClear(int ax, int ay, int bx, int by)
        {
            var dx = bx == ax ? 0 : (bx > ax ? 1 : -1);
            var dy = by == ay ? 0 : (by > ay ? 1 : -1);
            var x = ax;
            var y = ay;
            while (true)
            {
                if (LotMask.IsInterior(x, y)) return false;
                if (x == bx && y == by) return true;
                x += dx;
                y += dy;
            }
        }

        /// <summary>
        /// Appends the one or two straight runs that make up an L from A to B. The longer leg is
        /// traversed first, so the elbow is <c>(B.x, A.y)</c> for a wider-than-tall move and
        /// <c>(A.x, B.y)</c> for a taller-than-wide one - a fixed rule, because which elbow a
        /// route picks is visible in the finished map and must not depend on a sort.
        /// </summary>
        private void AddLRoute(int ax, int ay, int bx, int by, int width, RoadEdgeKind kind)
        {
            var horizontalFirst = Math.Abs(bx - ax) >= Math.Abs(by - ay);
            var ex = horizontalFirst ? bx : ax;
            var ey = horizontalFirst ? ay : by;
            AddRun(ax, ay, ex, ey, width, kind);
            AddRun(ex, ey, bx, by, width, kind);
        }

        private void AddRun(int ax, int ay, int bx, int by, int width, RoadEdgeKind kind)
        {
            if (ax == bx && ay == by) return;
            if (ax != bx && ay != by)
            {
                throw new ArgumentException("road runs are axis aligned", nameof(bx));
            }
            _edges.Add(new RoadEdge(ax, ay, bx, by, width, kind));
        }

        // ---------------------------------------------------------------- raster

        /// <summary>
        /// Draws the graph into <paramref name="map"/>'s <see cref="NeighbourhoodMap.RoadData"/>,
        /// one nibble pair at a time, and rebuilds the sidewalk ring. Idempotent: the occupancy
        /// is cleared first, so calling it again after adding a repair edge produces exactly the
        /// network that is in <see cref="Edges"/> and nothing extra.
        /// </summary>
        /// <param name="map">The map to fill.</param>
        public void Rasterise(NeighbourhoodMap map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (map.Width != _size || map.Height != _size)
            {
                throw new ArgumentException(
                    "this graph was built for a " + _size + "x" + _size + " map", nameof(map));
            }

            Array.Clear(_road, 0, _road.Length);
            Array.Clear(_streetTiles, 0, _streetTiles.Length);
            Array.Clear(_drivewayTiles, 0, _drivewayTiles.Length);

            for (int i = 0; i < _edges.Count; i++)
            {
                var e = _edges[i];
                var owner = e.Kind == RoadEdgeKind.Street
                    ? _streetTiles
                    : (e.Kind == RoadEdgeKind.Driveway ? _drivewayTiles : null);

                if (e.IsVertical)
                {
                    var xEnd = Math.Min(_size, e.AX + e.Width);
                    for (int y = e.AY; y <= e.BY; y++)
                    {
                        for (int x = e.AX; x < xEnd; x++)
                        {
                            Mark(x, y, owner);
                        }
                    }
                }
                else
                {
                    var yEnd = Math.Min(_size, e.AY + e.Width);
                    for (int y = e.AY; y < yEnd; y++)
                    {
                        for (int x = e.AX; x <= e.BX; x++)
                        {
                            Mark(x, y, owner);
                        }
                    }
                }
            }

            WriteMasks(map);
            WriteSidewalk();
        }

        private void Mark(int x, int y, bool[] owner)
        {
            if (x < 0 || y < 0 || x >= _size || y >= _size) return;
            // The invariant that outranks connectivity: a road never crosses a lot's interior.
            var index = y * _size + x;
            if (LotMask.Interior[index]) return;
            _road[index] = true;
            if (owner != null) owner[index] = true;
        }

        /// <summary>
        /// The nibble contract, applied to whatever is road.
        /// </summary>
        /// <remarks>
        /// <b>This is the one place <see cref="NeighbourhoodMap.RoadData"/> is written, and the
        /// rule has not moved.</b> Low nibble: the 4-bit adjacency mask, which
        /// <c>NeighbourhoodCityMeshBuilder.AddRoadQuadsForTile</c> reads as
        /// <c>roadByte &amp; RoadMask.MaskBits</c> and indexes <c>RoadLayout</c> with. High nibble:
        /// the same mask again on a turn or a T-junction, which that builder reads as
        /// <c>roadByte &gt;&gt; 4</c> and indexes <c>RoadCLayout</c> with; a straight, a dead end
        /// and a four-way cross get 0 there. <c>SimitoneNeighbourTerrain.RoadByteFor</c> reads the
        /// same bytes from outside a lot rectangle.
        /// </remarks>
        private void WriteMasks(NeighbourhoodMap map)
        {
            for (int y = 0; y < _size; y++)
            {
                for (int x = 0; x < _size; x++)
                {
                    var index = y * _size + x;
                    if (!_road[index])
                    {
                        map.RoadData[index] = 0;
                        continue;
                    }

                    var mask = AdjacencyAt(x, y);

                    // The low nibble is the mask (CityContent.RoadLayout[mask]); the high nibble
                    // is the same mask on a turn or a T-junction (CityContent.RoadCLayout[mask]).
                    map.RoadData[index] = RoadMask.IsTurnOrJunction(mask)
                        ? (byte)(mask | (mask << 4))
                        : mask;
                }
            }
        }

        private void WriteSidewalk()
        {
            Array.Clear(_sidewalk, 0, _sidewalk.Length);
            var ring = LotMask.BorderRing;
            for (int i = 0; i < _sidewalk.Length; i++)
            {
                _sidewalk[i] = ring[i] && !_road[i];
            }
        }

        private bool IsRoad(int x, int y)
        {
            if (x < 0 || y < 0 || x >= _size || y >= _size) return false;
            return _road[y * _size + x];
        }

        /// <summary>Whether a tile carries road.</summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsRoadAt(int x, int y)
        {
            return IsRoad(x, y);
        }

        /// <summary>Whether a tile carries main-street road rather than a driveway.</summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsStreetAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= _size || y >= _size) return false;
            return _streetTiles[y * _size + x];
        }

        /// <summary>Whether a tile is on the sidewalk ring.</summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsSidewalkAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= _size || y >= _size) return false;
            return _sidewalk[y * _size + x];
        }

        /// <summary>Whether a tile is inside a lot.</summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsLotInteriorAt(int x, int y)
        {
            return LotMask.IsInterior(x, y);
        }

        // ---------------------------------------------------------------- connectivity

        /// <summary>
        /// Flood-fills the road tiles. Component ids are assigned in the order their first tile
        /// appears in raster order, so the labelling is a function of the raster alone.
        /// </summary>
        public RoadComponents Components()
        {
            var componentOf = new int[_road.Length];
            for (int i = 0; i < componentOf.Length; i++)
            {
                componentOf[i] = -1;
            }

            var tiles = new List<int[]>();
            var queue = new Queue<int>();

            for (int start = 0; start < _road.Length; start++)
            {
                if (!_road[start] || componentOf[start] >= 0) continue;

                var id = tiles.Count;
                var members = new List<int>();
                componentOf[start] = id;
                queue.Enqueue(start);
                while (queue.Count > 0)
                {
                    var index = queue.Dequeue();
                    members.Add(index);
                    var x = index % _size;
                    var y = index / _size;
                    if (x + 1 < _size && IsRoad(x + 1, y) && componentOf[index + 1] < 0)
                    {
                        componentOf[index + 1] = id; queue.Enqueue(index + 1);
                    }
                    if (y + 1 < _size && IsRoad(x, y + 1) && componentOf[index + _size] < 0)
                    {
                        componentOf[index + _size] = id; queue.Enqueue(index + _size);
                    }
                    if (x > 0 && IsRoad(x - 1, y) && componentOf[index - 1] < 0)
                    {
                        componentOf[index - 1] = id; queue.Enqueue(index - 1);
                    }
                    if (y > 0 && IsRoad(x, y - 1) && componentOf[index - _size] < 0)
                    {
                        componentOf[index - _size] = id; queue.Enqueue(index - _size);
                    }
                }
                tiles.Add(members.ToArray());
            }

            return new RoadComponents(_size, componentOf, tiles);
        }

        /// <summary>How many connected pieces the rasterised network is in.</summary>
        public int ComponentCount
        {
            get { return Components().Count; }
        }

        /// <summary>
        /// Whether the rasterised network is a single connected piece. A neighbourhood with no
        /// lots at all has no roads, and zero components counts as connected - the invariant is
        /// about not being <em>split</em>.
        /// </summary>
        public bool IsSingleNetwork
        {
            get { return ComponentCount <= 1; }
        }

        /// <summary>
        /// Repairs the network until it is connected, adding a breadth-first-shortest path over
        /// the tiles outside every lot from each isolated piece back to the main one, and linking
        /// any lot whose entrance did not land on the network. Re-rasterises as it goes.
        /// </summary>
        /// <param name="map">The map to keep in step with the graph.</param>
        /// <returns>How many repair paths were added.</returns>
        public int RepairConnectivity(NeighbourhoodMap map)
        {
            int added = 0;

            for (int pass = 0; pass < MaxRepairPasses; pass++)
            {
                Rasterise(map);
                var components = Components();
                var main = components.LargestId;
                if (components.Count <= 1) break;

                bool bridged = false;
                for (int c = 0; c < components.Count && !bridged; c++)
                {
                    if (c == main) continue;
                    List<int> path;
                    if (TryFindPath(components, components.Tiles[c], main, out path))
                    {
                        AddPath(path, "isolated component " + c);
                        bridged = true;
                    }
                }
                if (bridged)
                {
                    added++;
                    continue;
                }

                // Every piece is unreachable from every other (or there is only one piece and it
                // is not where a door is). Try to attach a door to the main piece instead.
                bool linked = false;
                for (int i = 0; i < _entrances.Count && !linked; i++)
                {
                    var e = _entrances[i];
                    if (main >= 0 && e.X >= 0 && e.Y >= 0 && e.X < _size && e.Y < _size
                        && components.ComponentOf[e.Y * _size + e.X] == main)
                    {
                        continue;
                    }
                    List<int> path;
                    if (main >= 0 && TryFindPath(components, new[] { e.Y * _size + e.X }, main, out path))
                    {
                        AddPath(path, "entrance for lot " + e.LotNumber);
                        linked = true;
                    }
                }
                if (!linked) break;

                added++;
            }

            Rasterise(map);
            return added;
        }

        /// <summary>
        /// Breadth-first search over the tiles that are <em>not</em> inside a lot, from
        /// <paramref name="sources"/> to the first tile of <paramref name="targetComponent"/>.
        /// The path it returns is the cheapest axis-aligned connection available - fewest tiles -
        /// which is the plan's "add the cheapest axis-aligned edge to the main component".
        /// </summary>
        /// <param name="components">
        /// The flood fill of the current raster. Passed in rather than recomputed: a 512x512 map is
        /// a quarter of a million tiles, and the repair loop already has the labelling in hand.
        /// </param>
        private bool TryFindPath(
            RoadComponents components, int[] sources, int targetComponent, out List<int> path)
        {
            path = null;
            if (targetComponent < 0) return false;

            var componentOf = components.ComponentOf;
            var parent = new int[_road.Length];
            for (int i = 0; i < parent.Length; i++)
            {
                parent[i] = -2;
            }

            var queue = new Queue<int>();
            for (int i = 0; i < sources.Length; i++)
            {
                var s = sources[i];
                if (s < 0 || s >= parent.Length || parent[s] != -2) continue;
                if (LotMask.Interior[s]) continue;
                parent[s] = -1;
                queue.Enqueue(s);
            }

            int hit = -1;
            while (queue.Count > 0 && hit < 0)
            {
                var index = queue.Dequeue();
                if (componentOf[index] == targetComponent)
                {
                    hit = index;
                    break;
                }

                var x = index % _size;
                var y = index / _size;
                for (int step = 0; step < 4; step++)
                {
                    int dx, dy;
                    RoadMask.GetStep((byte)(step + 1), out dx, out dy);
                    var nx = x + dx;
                    var ny = y + dy;
                    if (nx < 0 || ny < 0 || nx >= _size || ny >= _size) continue;
                    var ni = ny * _size + nx;
                    if (parent[ni] != -2) continue;
                    if (LotMask.Interior[ni]) continue;
                    parent[ni] = index;
                    queue.Enqueue(ni);
                }
            }

            if (hit < 0) return false;

            var reversed = new List<int>();
            for (var at = hit; at >= 0; at = parent[at])
            {
                reversed.Add(at);
                if (parent[at] < 0) break;
            }
            reversed.Reverse();
            path = reversed;
            return true;
        }

        /// <summary>
        /// Turns a tile path into straight <see cref="RoadEdgeKind.Repair"/> runs - one per maximal
        /// straight stretch - so the persisted graph stays a list of runs and re-rasterising it
        /// reproduces the path exactly.
        /// </summary>
        private void AddPath(List<int> path, string reason)
        {
            if (path == null || path.Count == 0) return;

            var runStart = 0;
            for (int i = 2; i < path.Count; i++)
            {
                // A run continues only while the direction of the step into i is the same as the
                // direction of the run's first step. Comparing rows and columns is not enough: a
                // path that goes east and then north shares a column between the two steps, and
                // calling that "still straight" emits an edge that is not axis aligned at all -
                // which the rasteriser then refuses, so the repair adds nothing and the next pass
                // finds the very same disconnection again.
                if (StepX(path, i) == StepX(path, runStart + 1) && StepY(path, i) == StepY(path, runStart + 1))
                {
                    continue;
                }
                EmitRun(path, runStart, i - 1);
                runStart = i - 1;
            }
            EmitRun(path, runStart, path.Count - 1);

            var a = path[0];
            var b = path[path.Count - 1];
            _repairs.Add(new RoadRepair(reason, path.Count, a % _size, a / _size, b % _size, b / _size));
        }

        private int StepX(List<int> path, int index)
        {
            return path[index] % _size - path[index - 1] % _size;
        }

        private int StepY(List<int> path, int index)
        {
            return path[index] / _size - path[index - 1] / _size;
        }

        private void EmitRun(List<int> path, int from, int to)
        {
            if (to < from) return;
            _edges.Add(new RoadEdge(
                path[from] % _size, path[from] / _size,
                path[to] % _size, path[to] / _size,
                DrivewayWidth, RoadEdgeKind.Repair));
        }

        /// <summary>
        /// Recomputes <see cref="UnreachableLots"/> from the current raster. Called after
        /// <see cref="RepairConnectivity"/>; calling it again is harmless, which is why it is not
        /// folded into the repair loop (a lot unreachable on pass 1 may be reachable on pass 3,
        /// and a list that accumulated would never forget).
        /// </summary>
        /// <param name="map">The map the graph was rasterised into.</param>
        public void RefreshReachability(NeighbourhoodMap map)
        {
            var components = Components();
            var main = components.LargestId;
            _unreachable.Clear();
            for (int i = 0; i < _entrances.Count; i++)
            {
                var e = _entrances[i];
                var ok = main >= 0 && e.X >= 0 && e.Y >= 0 && e.X < _size && e.Y < _size
                    && components.ComponentOf[e.Y * _size + e.X] == main;
                if (!ok) _unreachable.Add(e.LotNumber);
            }
        }

        /// <summary>
        /// Throws unless the neighbourhood is one connected road network with every lot that has a
        /// house reachable. This is the assertable invariant of §6.1, and the throw is the signal
        /// a test keys on: a layout the generator cannot honour is a claim about the network, and
        /// <see cref="VerifyDrawable"/> is what decides whether it is drawn.
        /// </summary>
        /// <remarks>
        /// Kept strict on purpose, and not used by <see cref="NeighbourhoodMapGenerator.Generate"/>
        /// any more. A split network is a cosmetic defect - a cul-de-sac the repair pass could not
        /// bridge still draws as a cul-de-sac - and refusing to draw the whole neighbourhood over
        /// one was throwing away a correct world in exchange for an aesthetic one. See
        /// <see cref="Check"/> for the question the generator actually asks.
        /// </remarks>
        public void VerifyConnected()
        {
            var stats = Stats;
            var count = stats.Components;
            if (count > 1)
            {
                throw new InvalidOperationException(
                    "generated neighbourhood road network is split into " + count
                    + " components after " + _repairs.Count + " repair(s); " + stats.Summary()
                    + "; " + DescribeRepairs());
            }
            if (_unreachable.Count > 0)
            {
                throw new InvalidOperationException(
                    "generated neighbourhood has " + _unreachable.Count
                    + " lot(s) with no road access: " + DescribeLots(_unreachable)
                    + "; " + stats.Summary()
                    + "; " + DescribeRepairs());
            }
        }

        /// <summary>
        /// Whether the network is in one piece with every lot reachable, and - separately - whether
        /// there is anything here worth drawing at all.
        /// </summary>
        /// <remarks>
        /// The two questions used to be one, and collapsing them is what sent whole neighbourhoods
        /// back to the 2D bitmap: a network split into fifteen pieces is cosmetically wrong, not
        /// unbuildable, while lots with no road anywhere under them is. A caller that wants the
        /// invariant as an assertion asks <see cref="VerifyConnected"/>; a caller that wants a
        /// drawing decision - <see cref="NeighbourhoodMapGenerator.Generate"/> does - asks for this
        /// and throws <see cref="RoadNetworkCheck.Message"/> itself, so there is exactly one place
        /// the "is this drawable" rule is written down.
        /// </remarks>
        public RoadNetworkCheck Check()
        {
            var stats = Stats;
            var split = stats.Components > 1;
            var stranded = _unreachable.Count > 0;

            // The single undrawable case: lots, and not one tile of road between them. Everything
            // else - one island, nineteen doorsteps off the island, a repair pass that gave up -
            // still renders as streets.
            if (stats.RoadTiles == 0 && stats.LotTiles > 0)
            {
                return new RoadNetworkCheck(
                    false, true,
                    "generated neighbourhood has " + stats.LotTiles + " lot tile(s) and no road at all; "
                    + stats.Summary() + "; " + DescribeRepairs());
            }

            if (!split && !stranded)
            {
                return new RoadNetworkCheck(true, true, null);
            }

            var why = split
                ? "the generated neighbourhood road network is split into " + stats.Components
                  + " components after " + _repairs.Count + " repair(s)"
                : "the generated neighbourhood has " + _unreachable.Count
                  + " lot(s) with no road access: " + DescribeLots(_unreachable);
            var also = split && stranded
                ? ", and " + _unreachable.Count + " lot(s) with no road access: "
                  + DescribeLots(_unreachable)
                : "";

            return new RoadNetworkCheck(
                true, false, why + also + "; " + stats.Summary() + "; " + DescribeRepairs());
        }

        // ---------------------------------------------------------------- diagnostics

        /// <summary>
        /// What the network looks like, as numbers. Every one of these has been needed to explain
        /// a bad map: "is there any road", "is any of it a street rather than a doorstep", "how
        /// long is the longest run", "is it in one piece", and "how much of it is the repair
        /// pass stitching together stubs".
        /// </summary>
        public RoadNetworkStats Stats
        {
            get
            {
                int streetRuns = 0, longest = 0, drivewayRuns = 0, drivewaySpurs = 0, repairRuns = 0;
                for (int i = 0; i < _edges.Count; i++)
                {
                    var e = _edges[i];
                    if (e.Kind == RoadEdgeKind.Street)
                    {
                        streetRuns++;
                        if (e.Length > longest) longest = e.Length;
                    }
                    else if (e.Kind == RoadEdgeKind.Driveway)
                    {
                        drivewayRuns++;
                        // A one-tile run is the doorstep a lot gets for opening onto the street;
                        // anything longer is a spur that actually had to cross open ground.
                        if (e.Length > 1) drivewaySpurs++;
                    }
                    else
                    {
                        repairRuns++;
                    }
                }

                int streetTiles = 0, drivewayTiles = 0, repairTiles = 0, roadTiles = 0, sidewalkTiles = 0;
                for (int i = 0; i < _road.Length; i++)
                {
                    if (_road[i]) roadTiles++;
                    if (_streetTiles[i]) streetTiles++;
                    if (_drivewayTiles[i]) drivewayTiles++;
                    else if (_road[i] && !_streetTiles[i]) repairTiles++;
                    if (_sidewalk[i]) sidewalkTiles++;
                }

                int lotTiles = 0;
                for (int i = 0; i < LotMask.Interior.Length; i++)
                {
                    if (LotMask.Interior[i]) lotTiles++;
                }

                var components = Components();
                var main = components.LargestId;

                return new RoadNetworkStats(
                    roadTiles, sidewalkTiles, lotTiles, _streetWidth,
                    streetRuns, streetTiles, longest,
                    drivewayRuns, drivewaySpurs, drivewayTiles,
                    repairRuns, repairTiles,
                    components.Count, main >= 0 ? components.Size(main) : 0,
                    _nodes.Count, _entrances.Count, _unreachable.Count);
            }
        }

        /// <summary>
        /// A one-line summary for a log or a failing assertion's message. See
        /// <see cref="RoadNetworkStats.Summary"/>.
        /// </summary>
        public string DescribeNetwork()
        {
            return Stats.Summary();
        }

        private string DescribeRepairs()
        {
            if (_repairs.Count == 0) return "no repair was possible";
            var sb = new StringBuilder();
            for (int i = 0; i < _repairs.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                sb.Append(_repairs[i].ToString());
            }
            return sb.ToString();
        }

        private static string DescribeLots(IReadOnlyList<short> lots)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < lots.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(lots[i]);
            }
            return sb.ToString();
        }

        /// <summary>
        /// A human-readable dump of the graph, for a log line or a failing assertion message.
        /// </summary>
        public string WriteDebugSummary()
        {
            var sb = new StringBuilder();
            sb.Append("road graph: ").Append(_nodes.Count).Append(" node(s), ")
              .Append(_edges.Count).Append(" edge(s), ")
              .Append(_streets.Count).Append(" street run(s), ")
              .Append(_entrances.Count).Append(" entrance(s), ")
              .Append(_repairs.Count).Append(" repair(s)\n");
            sb.Append(DescribeNetwork()).Append('\n');
            if (_unreachable.Count > 0) sb.Append("unreachable lots: ").Append(DescribeLots(_unreachable)).Append('\n');
            for (int i = 0; i < _nodes.Count; i++)
            {
                sb.Append("  node ").Append(_nodes[i]).Append('\n');
            }
            for (int i = 0; i < _streets.Count; i++)
            {
                sb.Append("  street ").Append(_streets[i]).Append('\n');
            }
            for (int i = 0; i < _edges.Count; i++)
            {
                if (_edges[i].Kind == RoadEdgeKind.Street) continue;
                sb.Append("  edge ").Append(_edges[i]).Append('\n');
            }
            for (int i = 0; i < _repairs.Count; i++)
            {
                sb.Append("  repair ").Append(_repairs[i]).Append('\n');
            }
            return sb.ToString();
        }
    }
}