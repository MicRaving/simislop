/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The spatial substrate a live neighbourhood asks questions of: what is on this tile, where
    /// is this lot's door, and which lots can this lot's occupants reach on foot.
    ///
    /// <para>
    /// <b>One object, built once, read forever.</b> Construction rasterises the layout's lot
    /// rectangles, reads the map's road and water bytes, resolves the entrances and flood-fills
    /// the road network; after that every member is a lookup. It holds no reference it can write
    /// through, allocates nothing per call, and has no setter, so a renderer, a pathfinder and a
    /// test can hold the same instance and agree by construction. It is pure managed code: no
    /// graphics device, no VM, no file system, no clock.
    /// </para>
    ///
    /// <para>
    /// <b>Coordinates.</b> Everything here is map-tile space - the same integers
    /// <see cref="NeighbourhoodMap.Index"/> takes - not pixels and not lot-local tiles. A member
    /// that can fail on a coordinate outside the map (<see cref="IsInBounds"/>,
    /// <see cref="IsRoad"/>, <see cref="IsWater"/>, <see cref="IsLotInterior"/>,
    /// <see cref="LotAt"/>, <see cref="RoadComponentAt"/>) answers <c>false</c> or
    /// <c>null</c> rather than throwing, because "is this tile in the world" is the question
    /// being asked, not a programming error.
    /// </para>
    ///
    /// <para>
    /// <b>What counts as road is the map's own answer.</b> <see cref="IsRoad"/> reads
    /// <see cref="NeighbourhoodMap.RoadData"/>'s low nibble, which is the format's contract: a
    /// non-zero nibble is one of the fifteen shapes indexed by
    /// <see cref="RoadMask"/> and zero means "no road here". The consequence worth knowing is
    /// that a road tile with <em>no road neighbours at all</em> has mask 0 and is therefore
    /// indistinguishable from bare ground - a degenerate layout, not one the packer produces, and
    /// a query built without a <see cref="RoadGraph"/> cannot see it. When a graph <em>is</em>
    /// supplied it still contributes only entrances, for the same reason: a query whose road
    /// answer depended on which constructor was used would be two answers wearing one type.
    /// <see cref="RoadGraph.IsRoadAt"/> remains the authority on that one degenerate case.
    /// </para>
    ///
    /// <para>
    /// <b>Where entrances come from, in order.</b> The first source that answers wins for the
    /// whole query, and the rules are not merged:
    /// </para>
    /// <list type="number">
    ///   <item><description>
    ///     The <see cref="NeighbourhoodEntrance"/> list passed to the constructor. It is
    ///     authoritative and complete: a lot it does not name has no entrance, because a
    ///     partially-truthful source is how a "route to nowhere" gets shipped. This is the
    ///     overload a persisted sidecar's entrance table uses.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="RoadGraph.Entrances"/>, when a graph is passed. A rendered map was drawn
    ///     from that graph, so its doors are the ones the player can see. The graph is not used
    ///     for anything else here - not its occupancy, not its nodes, not its repair history.
    ///   </description></item>
    ///   <item><description>
    ///     Derived from the layout by running <see cref="RoadGraph.BuildNetwork"/> - the very
    ///     call <see cref="NeighbourhoodMapGenerator"/> makes - so a query and the map rendered
    ///     beside it cannot disagree about where a door is. A lot whose own
    ///     <see cref="NeighbourhoodLot.Entrance"/> is set overrides the derived one, because a
    ///     sidecar that carries an entrance is stating a fact rather than asking for one.
    ///   </description></item>
    /// </list>
    /// <para>
    /// Only lots with a house get an entrance, in every case. A bulldozed plot has no driveway -
    /// <see cref="RoadGraph"/> refuses to draw one, and a road that dead-ends at an empty plot is
    /// worse than none - so a query reports no door for it and
    /// <see cref="NeighboursOf"/> gives it none either. An explicit
    /// <see cref="ILotEntrance"/> list is the one source that overrides this, because a caller
    /// that supplies the list knows better than this file does.
    /// </para>
    ///
    /// <para>
    /// <b>What "neighbour" means here, precisely.</b> <see cref="NeighboursOf"/> answers
    /// <em>road reachability</em>: every other lot whose door can be walked to from this lot's
    /// door along connected road tiles, in ascending lot-number order, never including the lot
    /// itself. It is deliberately transitive - "reachable", not "adjacent" - because a
    /// pathfinder needs the former and a renderer needs the latter, and the latter is one distance
    /// test away while the former is not. In a neighbourhood whose network verifies
    /// (<see cref="RoadGraph.VerifyConnected"/>) that is most of the map, which is correct: the
    /// proximity half of "which neighbours do I show" is the caller's, because a radius is a
    /// policy and a policy does not belong in a query object. A lot with no entrance, or one
    /// whose door tile carries no road, has no neighbours and no route; that is the state
    /// <see cref="Describe"/> reports by name.
    /// </para>
    ///
    /// <para>
    /// <b>Determinism.</b> Two queries over the same layout and map answer identically, member
    /// for member, and every list is in an order that does not depend on a hash table. Road
    /// component ids are handed out in raster order of each component's first tile, exactly as
    /// <see cref="RoadGraph.Components"/> does, so a component id in a log means the same thing
    /// in both.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodQuery
    {
        /// <summary>
        /// The component id of a tile that is not on the road: out of bounds, bare ground, or a
        /// lot interior. Every member that reports a component reports this rather than -1
        /// chosen ad hoc, and a lot with no entrance has this as its own component.
        /// </summary>
        public const int NoRoadComponent = -1;

        private static readonly int[] NoInts = new int[0];
        private static readonly ReadOnlyCollection<int> NoLots = new ReadOnlyCollection<int>(NoInts);

        private readonly NeighbourhoodMap _map;
        private readonly NeighbourhoodLot[] _lots;
        private readonly bool[] _road;
        private readonly bool[] _water;
        private readonly int[] _lotOf;
        private readonly int _overlappingTiles;
        private readonly int[] _componentOf;
        private readonly int _componentCount;
        private readonly int _largestComponent;
        private readonly int _largestComponentTiles;
        private readonly int _roadTiles;
        private readonly int _waterTiles;
        private readonly int _lotTiles;
        private readonly ReadOnlyCollection<ILotEntrance> _entrances;
        private readonly Dictionary<int, int> _entranceIndexOf;
        private readonly Dictionary<int, int> _lotPositionOf;
        private readonly ReadOnlyCollection<int>[] _neighbours;

        /// <summary>
        /// A query over a layout and a map, with entrances derived from the layout by building
        /// the road network. This is the constructor a caller uses when all it has is the two
        /// things a generated world is made of.
        /// </summary>
        /// <param name="layout">The packed layout. Required - a query with no lots has no questions to ask.</param>
        /// <param name="map">The map the layout was packed against. Required, and required to be square.</param>
        /// <exception cref="ArgumentNullException">Either argument is null.</exception>
        /// <exception cref="ArgumentException">The map is not square.</exception>
        public NeighbourhoodQuery(NeighbourhoodLayout layout, NeighbourhoodMap map)
            : this(layout, map, null, null)
        {
        }

        /// <summary>
        /// A query that takes its entrances from a road graph, for a caller that has just built
        /// or loaded one and would rather not have them derived a second time.
        /// </summary>
        /// <param name="layout">The packed layout.</param>
        /// <param name="map">The map. Only its road, water and terrain bytes are read.</param>
        /// <param name="graph">The graph, or <c>null</c> to derive entrances from the layout.</param>
        public NeighbourhoodQuery(NeighbourhoodLayout layout, NeighbourhoodMap map, RoadGraph graph)
            : this(layout, map, null, graph)
        {
        }

        /// <summary>
        /// A query over a caller-supplied entrance table - a persisted sidecar's entrances, a
        /// hand-authored neighbourhood, or a fixed set from a test. The list is authoritative and
        /// complete: see the type remarks.
        /// </summary>
        /// <remarks>
        /// The three-argument overloads are distinguished by argument type, so
        /// <c>new NeighbourhoodQuery(layout, map, null)</c> is ambiguous and wants a cast.
        /// </remarks>
        /// <param name="layout">The packed layout.</param>
        /// <param name="map">The map.</param>
        /// <param name="entrances">The entrance table, or <c>null</c> to use the other sources.</param>
        public NeighbourhoodQuery(
            NeighbourhoodLayout layout, NeighbourhoodMap map, IReadOnlyList<NeighbourhoodEntrance> entrances)
            : this(layout, map, entrances, null)
        {
        }

        private NeighbourhoodQuery(NeighbourhoodLayout layout, NeighbourhoodMap map,
            IReadOnlyList<NeighbourhoodEntrance> entrances, RoadGraph graph)
        {
            if (layout == null) throw new ArgumentNullException(nameof(layout));
            if (map == null) throw new ArgumentNullException(nameof(map));
            if (map.Width != map.Height)
            {
                //Not a shape the generator can produce - NeighbourhoodMap.Create and
                //RoadGraph.Rasterise both insist on it - but the derived-entrance path needs a
                //square grid, and quietly degrading a map nobody can build is worse than saying
                //so once, here.
                throw new ArgumentException(
                    "a neighbourhood query needs a square map, and this one is "
                    + map.Width + "x" + map.Height, nameof(map));
            }

            _map = map;
            Layout = layout;
            Width = map.Width;
            Height = map.Height;

            var count = map.Width * map.Height;
            _road = new bool[count];
            _water = new bool[count];
            for (int i = 0; i < count; i++)
            {
                _road[i] = map.RoadMaskAt(i) != RoadMask.None;
                _water[i] = map.TerrainType[i] == NeighbourhoodTerrain.Water;
                if (_road[i]) _roadTiles++;
                if (_water[i]) _waterTiles++;
            }

            _lots = new NeighbourhoodLot[layout.Lots.Count];
            _lotPositionOf = new Dictionary<int, int>(layout.Lots.Count);
            for (int i = 0; i < _lots.Length; i++)
            {
                //NeighbourhoodLayout holds its lots ascending by number, so this order is
                //canonical and everything derived from it is too.
                _lots[i] = layout.Lots[i];
                _lotPositionOf[_lots[i].Number] = i;
            }

            _lotOf = RasteriseLots(out _overlappingTiles, out _lotTiles);
            _entrances = ResolveEntrances(
                entrances, graph, out _entranceIndexOf);
            _componentOf = FloodRoadComponents(
                out _componentCount, out _largestComponent, out _largestComponentTiles);
            _neighbours = BuildNeighbourLists();
        }

        /// <summary>Map width in tiles. Every coordinate in this class is in these units.</summary>
        public int Width { get; }

        /// <summary>Map height in tiles.</summary>
        public int Height { get; }

        /// <summary>How many tiles the map has, <c>Width * Height</c>.</summary>
        public int TileCount
        {
            get { return _map.TerrainType.Length; }
        }

        /// <summary>The layout this query was built from. Not copied - it is already immutable
        /// from a reader's point of view, and copying it would mean two lot lists to keep in
        /// step.</summary>
        public NeighbourhoodLayout Layout { get; }

        /// <summary>Every lot the layout places, ascending by lot number.</summary>
        public IReadOnlyList<NeighbourhoodLot> Lots
        {
            get { return Layout.Lots; }
        }

        /// <summary>
        /// One entrance per lot that has a house, ascending by lot number. Immutable, and the
        /// same boxed instances every time - <see cref="EntranceFor"/> hands out the element of
        /// this list rather than a fresh copy.
        /// </summary>
        /// <remarks>
        /// The order is by lot number rather than by the order the source listed them in. That
        /// makes two queries over the same facts compare equal element for element, and it is the
        /// order a debug dump wants anyway.
        /// </remarks>
        public IReadOnlyList<ILotEntrance> Entrances
        {
            get { return _entrances; }
        }

        /// <summary>
        /// How many connected pieces the road network is in, or 0 when there is no road at all.
        /// A generated neighbourhood that verifies is 1; see <see cref="RoadGraph.Stats"/>.
        /// </summary>
        public int RoadComponentCount
        {
            get { return _componentCount; }
        }

        /// <summary>
        /// Is this tile inside the map? The gate every other tile member applies, exposed so a
        /// caller iterating a camera's view does not have to reimplement it.
        /// </summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsInBounds(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Width && y < Height;
        }

        /// <summary>
        /// Does this tile carry road? Read from the map's low nibble; see the type remarks for
        /// what a zero nibble means.
        /// </summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsRoad(int x, int y)
        {
            return IsInBounds(x, y) && _road[y * Width + x];
        }

        /// <summary>
        /// Is this tile water? Straight from <see cref="NeighbourhoodMap.TerrainType"/>: a tile
        /// whose type is <see cref="NeighbourhoodTerrain.Water"/>. Not inferred from a lot's
        /// ground, which is a different thing - a lot's terrain is its own interior's floor.
        /// </summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsWater(int x, int y)
        {
            return IsInBounds(x, y) && _water[y * Width + x];
        }

        /// <summary>
        /// Is this tile inside a lot's rectangle? The interior only - the packer's sidewalk
        /// margin and the road graph's one-tile border ring are connective ground, and the lot
        /// that owns one is found by looking a tile out.
        /// </summary>
        /// <remarks>
        /// Two lots may overlap in a hand-written layout. The lowest lot number wins the tile,
        /// which keeps the answer a function of the layout rather than of iteration order, and
        /// <see cref="Describe"/> reports how many tiles were contested.
        /// </remarks>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsLotInterior(int x, int y)
        {
            return LotPositionAt(x, y) >= 0;
        }

        /// <summary>
        /// The lot covering this tile, or <c>null</c> for connective ground, bare ground, or a
        /// coordinate outside the map. O(1), unlike
        /// <see cref="NeighbourhoodLayout.LotAt"/>'s scan, because construction rasterised the
        /// rectangles.
        /// </summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public NeighbourhoodLot LotAt(int x, int y)
        {
            var position = LotPositionAt(x, y);
            return position < 0 ? null : _lots[position];
        }

        /// <summary>
        /// Can something walking on the neighbourhood's ground stand here? False for
        /// <em>outside the map</em>, for <em>water</em>, and for <em>a lot's interior</em> -
        /// which is what "a lot's wall" means from out here: the tiles a lot owns are not ground
        /// a walker can be on, and the road graph's hardest invariant is that road never crosses
        /// them, so the rectangle boundary is exactly the thing that stops a walker.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Inside a lot this is not the authority: a sim standing in a lot is inside a lot, and
        /// what it collides with there is the lot's own <c>BLNT</c>/wall data and the VM's
        /// routing, not this. This member answers for the neighbourhood's own ground only.
        /// </para>
        /// <para>
        /// A <see cref="NeighbourhoodTerrain.Nothing"/> tile is <em>not</em> blocked. That value
        /// means "emit no quad here" - a hole in a hand-authored outline - and the generated
        /// diamond's unused corners are outside the walkable world rather than blocked by
        /// something. A generated map never contains one, so the two readings cannot both arise
        /// in practice; the hole reading is the one that stays true if one ever does.
        /// </para>
        /// </remarks>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsBlocked(int x, int y)
        {
            if (!IsInBounds(x, y)) return true;
            var index = y * Width + x;
            return _water[index] || _lotOf[index] >= 0;
        }

        /// <summary>
        /// The connected piece of road this tile belongs to, or <see cref="NoRoadComponent"/> for
        /// bare ground, a lot interior, or a coordinate outside the map. Ids are handed out in
        /// raster order of each component's first tile, as <see cref="RoadGraph.Components"/>
        /// does.
        /// </summary>
        /// <remarks>
        /// This is the primitive a "can these two tiles reach each other" test should use rather
        /// than a flood fill per question: it is the result of the flood fill, already done.
        /// </remarks>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public int RoadComponentAt(int x, int y)
        {
            if (!IsInBounds(x, y)) return NoRoadComponent;
            var index = y * Width + x;
            return _road[index] ? _componentOf[index] : NoRoadComponent;
        }

        /// <summary>
        /// This lot's door, or <c>null</c> when it has none: a lot that is not in this layout, or
        /// a lot with no house (and therefore no driveway). See the type remarks for the order
        /// the sources are consulted in.
        /// </summary>
        /// <param name="lotNumber">The lot number.</param>
        public ILotEntrance EntranceFor(int lotNumber)
        {
            int index;
            return _entranceIndexOf.TryGetValue(lotNumber, out index) ? _entrances[index] : null;
        }

        /// <summary>
        /// The lot with this number, or <c>false</c>. Delegated to
        /// <see cref="NeighbourhoodLayout.FindLot"/>, so a query and the layout never disagree
        /// about whether a number names a lot.
        /// </summary>
        /// <param name="lotNumber">The lot number; out of range is answered, not thrown at.</param>
        /// <param name="lot">The lot, when there is one.</param>
        public bool TryGetLot(int lotNumber, out NeighbourhoodLot lot)
        {
            lot = Layout.FindLot(lotNumber);
            return lot != null;
        }

        /// <summary>
        /// The lots reachable on foot from this lot's door: every other lot whose door lies in
        /// the same connected piece of road. Ascending by lot number, never including
        /// <paramref name="lotNumber"/> itself, and empty for a lot that is not in the layout, a
        /// lot with no house, or a lot whose door tile carries no road.
        /// </summary>
        /// <remarks>
        /// Reachability, not adjacency - see the type remarks. The returned list is a read-only
        /// wrapper over an array built at construction, so a caller may hold on to it; the same
        /// instance comes back every time.
        /// </remarks>
        /// <param name="lotNumber">The lot number.</param>
        public IReadOnlyList<int> NeighboursOf(int lotNumber)
        {
            int position;
            if (!_lotPositionOf.TryGetValue(lotNumber, out position)) return NoLots;
            return _neighbours[position];
        }

        /// <summary>
        /// Everything worth putting in a log, or in a failing assertion's message, about this
        /// query: the grid, the tile census, the road network's shape, the entrance table and
        /// every lot's neighbour list - plus the lots that have no route to anywhere, which is the
        /// only line here that is usually about a bug.
        /// </summary>
        /// <remarks>
        /// Multi-line on purpose, and ordered so the interesting part is at the bottom: a reader
        /// scanning a log wants the counts on the first line and the odd lot out on the last.
        /// </remarks>
        public string Describe()
        {
            var sb = new StringBuilder();
            sb.Append("neighbourhood query ").Append(Width).Append('x').Append(Height)
              .Append(" (").Append(TileCount).Append(" tile(s))");
            if (Layout.MapSize != Width)
            {
                //Not thrown over: a query can be built over a map of a different side length than
                //the layout claims, and the census below is still true of the map that exists.
                sb.Append(" [layout was packed for ").Append(Layout.MapSize).Append(']');
            }
            sb.Append('\n');

            sb.Append("  tiles: ").Append(_roadTiles).Append(" road, ")
              .Append(_waterTiles).Append(" water, ")
              .Append(_lotTiles).Append(" lot interior, ")
              .Append(TileCount - _roadTiles - _waterTiles - _lotTiles).Append(" open ground")
              .Append('\n');
            sb.Append("  road: ").Append(_roadTiles).Append(" tile(s) in ")
              .Append(_componentCount).Append(" component(s), largest ")
              .Append(_largestComponentTiles)
              .Append('\n');

            var withHouse = 0;
            var plots = 0;
            for (int i = 0; i < _lots.Length; i++)
            {
                if (_lots[i].HasHouse) withHouse++; else plots++;
            }
            sb.Append("  lots: ").Append(_lots.Length).Append(" (").Append(withHouse)
              .Append(" with a house, ").Append(plots).Append(" bulldozed plot(s)");
            if (_overlappingTiles > 0)
            {
                sb.Append("; ").Append(_overlappingTiles)
                  .Append(" tile(s) claimed by more than one lot, lowest number winning");
            }
            sb.Append('\n');
            sb.Append("  entrances: ").Append(_entrances.Count).Append(" of ")
              .Append(withHouse).Append(" lot(s) with a house\n");

            var stranded = new List<int>();
            for (int i = 0; i < _lots.Length; i++)
            {
                var lot = _lots[i];
                var neighbours = _neighbours[i];
                sb.Append("  lot ").Append(lot.Number).Append(" -> [");
                for (int k = 0; k < neighbours.Count; k++)
                {
                    if (k > 0) sb.Append(", ");
                    sb.Append(neighbours[k]);
                }
                sb.Append(']');
                if (neighbours.Count == 0) stranded.Add(lot.Number);
                sb.Append('\n');
            }
            if (stranded.Count > 0)
            {
                sb.Append("  no route from lot(s): ").Append(DescribeNumbers(stranded)).Append('\n');
            }
            return sb.ToString();
        }

        public override string ToString()
        {
            return "query " + Width + "x" + Height + ", " + _lots.Length + " lot(s), "
                + _entrances.Count + " entrance(s), " + _componentCount + " road component(s)";
        }

        // ---------------------------------------------------------------- construction

        private int LotPositionAt(int x, int y)
        {
            if (!IsInBounds(x, y)) return -1;
            return _lotOf[y * Width + x];
        }

        /// <summary>
        /// One entry per tile: the index into <see cref="_lots"/> of the lot that owns it, or -1
        /// for connective ground. Built once, which is what makes <see cref="LotAt"/> a lookup
        /// rather than a scan of every lot - and <see cref="NeighbourhoodLayout.LotAt"/> is a
        /// scan, because it has no raster and no idea what the map looks like.
        /// </summary>
        private int[] RasteriseLots(out int overlappingTiles, out int lotTiles)
        {
            var lotOf = new int[TileCount];
            for (int i = 0; i < lotOf.Length; i++)
            {
                lotOf[i] = -1;
            }

            var overlaps = 0;
            var tiles = 0;
            for (int position = 0; position < _lots.Length; position++)
            {
                var lot = _lots[position];
                //Clamped to the map's own size, which is generous for every real lot and removes
                //the one way a corrupt Size could overflow the right-hand edge below.
                var size = lot.Size < 0 ? 0 : Math.Min(lot.Size, Math.Max(Width, Height));
                if (size == 0) continue;

                for (int y = Math.Max(0, lot.WorldY); y < Math.Min(Height, lot.WorldY + size); y++)
                {
                    for (int x = Math.Max(0, lot.WorldX); x < Math.Min(Width, lot.WorldX + size); x++)
                    {
                        var index = y * Width + x;
                        if (lotOf[index] >= 0)
                        {
                            overlaps++;
                            continue;
                        }
                        lotOf[index] = position;
                        tiles++;
                    }
                }
            }
            overlappingTiles = overlaps;
            lotTiles = tiles;
            return lotOf;
        }

        private ReadOnlyCollection<ILotEntrance> ResolveEntrances(
            IReadOnlyList<NeighbourhoodEntrance> supplied, RoadGraph graph,
            out Dictionary<int, int> indexOf)
        {
            var resolved = new Dictionary<int, NeighbourhoodEntrance>();

            if (supplied != null)
            {
                for (int i = 0; i < supplied.Count; i++)
                {
                    AddFirst(resolved, supplied[i]);
                }
            }
            else if (graph != null)
            {
                var fromGraph = graph.Entrances;
                for (int i = 0; i < fromGraph.Count; i++)
                {
                    AddFirst(resolved, fromGraph[i]);
                }
            }
            else if (Layout.MapSize >= MinGeneratedMapSize)
            {
                //The same call the generator makes, so the two cannot disagree. BuildNetwork
                //touches no map and no graphics; it is the layout's own lot list turned into
                //streets and door anchors.
                var network = RoadGraph.BuildNetwork(NeighbourhoodMapAdapter.InputFor(Layout));
                var derived = network.Entrances;
                for (int i = 0; i < derived.Count; i++)
                {
                    AddFirst(resolved, derived[i]);
                }

                //A lot that states its own entrance states it as fact, so it overrides the
                //derived anchor - including for a plot, which the network deliberately skipped.
                for (int i = 0; i < _lots.Length; i++)
                {
                    LotEntrance own;
                    if (!LotEntrance.TryFrom(_lots[i], out own)) continue;
                    //short, because that is what NeighbourhoodEntrance and NeighbourhoodLot both
                    //number their lots with; the interface widens it to int so a caller never has
                    //to care, and this is the one place the narrowing is visible.
                    resolved[own.LotNumber] = new NeighbourhoodEntrance(
                        (short)own.LotNumber, own.X, own.Y, own.FacingX, own.FacingY,
                        LotEntrance.ClampFacing(_lots[i].Entrance.Value.Facing));
                }
            }

            //Ascending lot number, so the list is canonical whatever order the sources used.
            var numbers = new List<int>(resolved.Keys);
            numbers.Sort();
            var entrances = new List<ILotEntrance>(numbers.Count);
            indexOf = new Dictionary<int, int>(numbers.Count);
            for (int i = 0; i < numbers.Count; i++)
            {
                indexOf.Add(numbers[i], i);
                entrances.Add(LotEntrance.From(resolved[numbers[i]]));
            }
            return new ReadOnlyCollection<ILotEntrance>(entrances);
        }

        /// <summary>
        /// The smallest map the generator will accept, and therefore the smallest this class can
        /// derive entrances for. <see cref="NeighbourhoodMapInput"/>'s own constructor is where
        /// the four comes from; it is restated here because the guard below has to decide
        /// whether to call that constructor at all, and a layout may claim a map of one, two or
        /// three tiles - sizes <see cref="NeighbourhoodLayout"/> accepts and the sidecar
        /// will happily load.
        /// </summary>
        private const int MinGeneratedMapSize = 4;

        private static void AddFirst(Dictionary<int, NeighbourhoodEntrance> into,
            NeighbourhoodEntrance entrance)
        {
            //First wins for a duplicate, so a list that names a lot twice resolves to the first
            //mention rather than to whichever iteration a hash table happened to end on.
            if (!into.ContainsKey(entrance.LotNumber)) into.Add(entrance.LotNumber, entrance);
        }

        private int[] FloodRoadComponents(
            out int componentCount, out int largest, out int largestTiles)
        {
            var componentOf = new int[TileCount];
            for (int i = 0; i < componentOf.Length; i++)
            {
                componentOf[i] = NoRoadComponent;
            }
            largest = NoRoadComponent;
            largestTiles = 0;
            componentCount = 0;
            if (_roadTiles == 0)
            {
                return componentOf;
            }

            //A ring buffer over the tile indices: a queue that cannot reallocate, so a
            //262,144-tile map does not allocate a second one halfway through.
            var queue = new int[TileCount];
            var components = 0;
            var biggest = NoRoadComponent;
            var biggestSize = 0;

            for (int start = 0; start < componentOf.Length; start++)
            {
                if (!_road[start] || componentOf[start] != NoRoadComponent) continue;

                var id = components++;
                var head = 0;
                var tail = 0;
                var size = 0;
                componentOf[start] = id;
                queue[tail++] = start;

                while (head < tail)
                {
                    var index = queue[head++];
                    size++;
                    var x = index % Width;
                    var y = index / Width;
                    if (x + 1 < Width && _road[index + 1] && componentOf[index + 1] == NoRoadComponent)
                    {
                        componentOf[index + 1] = id; queue[tail++] = index + 1;
                    }
                    if (y + 1 < Height && _road[index + Width] && componentOf[index + Width] == NoRoadComponent)
                    {
                        componentOf[index + Width] = id; queue[tail++] = index + Width;
                    }
                    if (x > 0 && _road[index - 1] && componentOf[index - 1] == NoRoadComponent)
                    {
                        componentOf[index - 1] = id; queue[tail++] = index - 1;
                    }
                    if (y > 0 && _road[index - Width] && componentOf[index - Width] == NoRoadComponent)
                    {
                        componentOf[index - Width] = id; queue[tail++] = index - Width;
                    }
                }

                if (size > biggestSize)
                {
                    biggestSize = size;
                    biggest = id;
                }
            }

            componentCount = components;
            largest = biggest;
            largestTiles = biggestSize;
            return componentOf;
        }

        private ReadOnlyCollection<int>[] BuildNeighbourLists()
        {
            var lists = new ReadOnlyCollection<int>[_lots.Length];
            var byComponent = new Dictionary<int, List<int>>(_componentCount);

            for (int position = 0; position < _lots.Length; position++)
            {
                var component = ComponentOfLot(position);
                if (component == NoRoadComponent)
                {
                    lists[position] = NoLots;
                    continue;
                }
                List<int> members;
                if (!byComponent.TryGetValue(component, out members))
                {
                    members = new List<int>();
                    byComponent.Add(component, members);
                }
                //Positions ascend with lot numbers, so each of these lists is already in the
                //order the answer is documented to be in.
                members.Add(position);
            }

            for (int position = 0; position < _lots.Length; position++)
            {
                List<int> members;
                var component = ComponentOfLot(position);
                if (component == NoRoadComponent
                    || !byComponent.TryGetValue(component, out members)
                    || members.Count < 2)
                {
                    lists[position] = NoLots;
                    continue;
                }

                var numbers = new List<int>(members.Count - 1);
                for (int i = 0; i < members.Count; i++)
                {
                    if (members[i] == position) continue;
                    numbers.Add(_lots[members[i]].Number);
                }
                lists[position] = new ReadOnlyCollection<int>(numbers);
            }
            return lists;
        }

        private int ComponentOfLot(int position)
        {
            //No entrance, a door outside the map, or bare ground under the door: all three mean
            //the lot is on no road network at all, which is one answer and one constant.
            int index;
            if (!_entranceIndexOf.TryGetValue(_lots[position].Number, out index))
            {
                return NoRoadComponent;
            }
            var entrance = _entrances[index];
            return RoadComponentAt(entrance.X, entrance.Y);
        }

        private static string DescribeNumbers(List<int> numbers)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < numbers.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(numbers[i]);
            }
            return sb.ToString();
        }
    }
}
