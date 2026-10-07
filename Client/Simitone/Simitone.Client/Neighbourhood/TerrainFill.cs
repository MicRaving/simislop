/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Simitone.Client.Utils;

// NeighbourhoodMap writes its colour arrays as XNA Colors (CityMapData's type), so the foliage
// tints are XNA Colors too. Pinned with an alias so importing another namespace that happens to
// export a Color cannot change the array's element type.
using Color = Microsoft.Xna.Framework.Color;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Tunables for <see cref="TerrainFill"/>. Every field has a default, and
    /// <see cref="Default"/> is what the generator uses, so an option is only ever passed
    /// deliberately - by a test that wants water off, or by a later milestone with a reason to move
    /// it.
    /// </summary>
    public readonly struct TerrainFillOptions
    {
        /// <summary>
        /// Whether to generate water at all. The plan calls the water mask optional and this is
        /// that switch.
        /// </summary>
        public bool WaterEnabled { get; }

        /// <summary>
        /// The threshold the combined noise has to clear to become water, in 0..1023. Higher means
        /// less water; the default leaves roughly a tenth of the eligible connective area flooded.
        /// </summary>
        public int WaterLevel { get; }

        /// <summary>
        /// How far from the nearest lot, in tiles, water may start - so a lake cannot appear
        /// through somebody's driveway.
        /// </summary>
        public int WaterMinLotDistance { get; }

        /// <summary>
        /// The minimum spacing between two trees, in tiles. This is the Poisson-disk radius, and
        /// so it is also the tightest gap a wood has.
        /// </summary>
        public int TreeSpacing { get; }

        /// <summary>An upper bound on trees, so a huge map cannot take unbounded time.</summary>
        public int MaxTrees { get; }

        /// <summary>How many candidate positions each active tree is allowed to try.</summary>
        public int DartsPerActiveTree { get; }

        /// <summary>
        /// The density written at a tree's centre, falling to zero at the rim of its disc.
        /// </summary>
        public byte MaxTreeDensity { get; }

        /// <summary>
        /// How many tiles around a <em>community</em> lot are kept clear of trees. A community lot
        /// is a park, a pool or a club - ground people are meant to be on - and this is the only
        /// thing a lot's <c>Zone</c> changes. A residential lot gets no such exclusion, so
        /// residential yards are wooded the way a TS1 neighbourhood's are.
        /// </summary>
        public int CommunityClearance { get; }

        /// <summary>
        /// The generator's settings. A 6-tile tree spacing over a 512x512 map gives woods of a
        /// plausible density, and the two noise octaves are at a quarter and a sixteenth of the
        /// map.
        /// </summary>
        public static TerrainFillOptions Default
        {
            get
            {
                return new TerrainFillOptions(true, 800, 6, 6, 4000, 16, 255, 3);
            }
        }

        /// <summary>Every option at its default except the water mask, which is off.</summary>
        public static TerrainFillOptions WithoutWater
        {
            get
            {
                var d = Default;
                return new TerrainFillOptions(
                    false, d.WaterLevel, d.WaterMinLotDistance, d.TreeSpacing,
                    d.MaxTrees, d.DartsPerActiveTree, d.MaxTreeDensity, d.CommunityClearance);
            }
        }

        /// <summary>Creates a set of options, clamping every field to something usable.</summary>
        /// <param name="waterEnabled">Whether to generate water.</param>
        /// <param name="waterLevel">Water noise threshold, 0-1023.</param>
        /// <param name="waterMinLotDistance">Minimum distance from a lot for water, in tiles.</param>
        /// <param name="treeSpacing">Minimum tree spacing, in tiles.</param>
        /// <param name="maxTrees">Upper bound on tree count.</param>
        /// <param name="dartsPerActiveTree">Candidate positions tried per active tree.</param>
        /// <param name="maxTreeDensity">Density written at a tree's centre.</param>
        /// <param name="communityClearance">Tiles kept clear around a community lot.</param>
        public TerrainFillOptions(
            bool waterEnabled,
            int waterLevel,
            int waterMinLotDistance,
            int treeSpacing,
            int maxTrees,
            int dartsPerActiveTree,
            byte maxTreeDensity,
            int communityClearance)
        {
            WaterEnabled = waterEnabled;
            WaterLevel = waterLevel < 0 ? 0 : (waterLevel > 1023 ? 1023 : waterLevel);
            WaterMinLotDistance = waterMinLotDistance < 0 ? 0 : waterMinLotDistance;
            TreeSpacing = treeSpacing < 1 ? 1 : treeSpacing;
            MaxTrees = maxTrees < 0 ? 0 : maxTrees;
            DartsPerActiveTree = dartsPerActiveTree < 1 ? 1 : dartsPerActiveTree;
            MaxTreeDensity = maxTreeDensity;
            CommunityClearance = communityClearance < 0 ? 0 : communityClearance;
        }
    }

    /// <summary>What <see cref="TerrainFill.Fill"/> produced, for tests and for a query layer.</summary>
    public readonly struct TerrainFillReport
    {
        /// <summary>How many trees the Poisson-disk pass accepted.</summary>
        public int Trees { get; }

        /// <summary>
        /// The tile index of each accepted tree, in acceptance order. Kept because the one
        /// property the scatter is defined by - no two closer than
        /// <see cref="TerrainFillOptions.TreeSpacing"/> - is not checkable from the painted
        /// density field, and because "where are the trees" is a question a query layer will ask.
        /// </summary>
        public int[] TreeTiles { get; }

        /// <summary>How many tiles became water.</summary>
        public int WaterTiles { get; }

        /// <summary>
        /// The water mask, row-major. Kept because "where is the water" is a question a query
        /// layer will ask, and re-deriving the noise to answer it would be silly.
        /// </summary>
        public bool[] Water { get; }

        /// <summary>
        /// The nearest-lot owner per tile - the Voronoi the connective area's ground colour comes
        /// from - or -1 where there is no lot. Row-major.
        /// </summary>
        public int[] NearestLotOwner { get; }

        internal TerrainFillReport(
            int trees, int[] treeTiles, int waterTiles, bool[] water, int[] owners)
        {
            Trees = trees;
            TreeTiles = treeTiles;
            WaterTiles = waterTiles;
            Water = water;
            NearestLotOwner = owners;
        }

        /// <summary>
        /// One line describing what the fill produced, for a diagnostic sink or a failing
        /// assertion's message.
        /// </summary>
        public string Describe()
        {
            return "fill: " + Trees + " tree(s), " + WaterTiles + " water tile(s)";
        }

        /// <inheritdoc/>
        public override string ToString()
        {
            return Describe();
        }
    }

    /// <summary>
    /// §6.2: the ground between the lots. Lot interiors get their own type, the connective area is
    /// filled by nearest-lot-type so ground colour changes are coherent rather than noisy, an
    /// optional water mask, and a seeded Poisson-disk scatter of foliage.
    ///
    /// <para><b>Nearest-lot-type is a chamfer Voronoi, not a per-tile loop over lots.</b> A
    /// per-tile loop is O(tiles x lots) - about 26 million distance evaluations for a full
    /// 512x512 map with a hundred lots - and the obvious alternative, "propagate each lot outward
    /// in turn", is order-dependent and so not deterministic in the way §6 requires. This does the
    /// standard two-pass chamfer distance transform instead, with orthogonal cost 3 and diagonal
    /// cost 4 (units of a third of a tile), carrying <c>(distance, owning lot index)</c> and
    /// breaking distance ties on the lower lot index. One forward sweep and one backward sweep,
    /// O(tiles), and a function of the lot list alone.</para>
    ///
    /// <para><b>Why a chamfer and not an exact Euclidean Voronoi.</b> The diagonal cost of 4
    /// against an orthogonal 3 approximates sqrt(2) = 1.414 with 1.34, so the boundary where the
    /// ground changes colour can sit about a tile off its exact position. That is far below
    /// anything visible in a terrain colour change, and an exact transform would be slower and no
    /// more useful. It is worth naming because that boundary is the only place this approximation
    /// shows.</para>
    ///
    /// <para><b>Foliage is a seeded Poisson-disk scatter, and only ever outside the lots, the
    /// roads, the sidewalks and the water.</b> Bridson's algorithm with <em>integer</em> candidate
    /// offsets rather than random angles: floating-point trigonometry is deterministic within a
    /// runtime but not guaranteed across runtimes or maths libraries, and "same seed, same bytes"
    /// has to survive a rebuild on a different machine. Every candidate is then explicitly rejected
    /// if an accepted tree is within <see cref="TerrainFillOptions.TreeSpacing"/> tiles, which is
    /// what makes this a true minimum-distance scatter rather than approximately one. Each
    /// accepted tree is then painted as a disc of density falling from
    /// <see cref="TerrainFillOptions.MaxTreeDensity"/> at its centre to 0 at its rim, in one
    /// seeded species colour, so a wood reads as a wood and not as confetti.</para>
    /// </summary>
    public static class TerrainFill
    {
        /// <summary>The chamfer cost of an orthogonal step, in units of a third of a tile.</summary>
        public const int ChamferOrthogonal = 3;

        /// <summary>The chamfer cost of a diagonal step, in units of a third of a tile.</summary>
        public const int ChamferDiagonal = 4;

        private const int Unreached = int.MaxValue / 4;

        /// <summary>
        /// The five foliage tints a generated wood is drawn from. One is chosen per tree from the
        /// scatter's own stream, so a wood reads as one species and two woods beside each other
        /// usually are not.
        /// </summary>
        private static readonly Color[] FoliageTints =
        {
            new Color(58, 110, 42),
            new Color(86, 132, 58),
            new Color(44, 92, 36),
            new Color(112, 124, 62),
            new Color(148, 138, 92),
        };

        /// <summary>
        /// Fills <paramref name="map"/>'s <see cref="NeighbourhoodMap.TerrainType"/>,
        /// <see cref="NeighbourhoodMap.TerrainTypeColorData"/>,
        /// <see cref="NeighbourhoodMap.ForestDensityData"/> and
        /// <see cref="NeighbourhoodMap.ForestTypeData"/>.
        /// </summary>
        /// <param name="input">The generation input.</param>
        /// <param name="graph">
        /// The rasterised road graph. Its road and sidewalk masks decide where foliage may not go,
        /// so this must run after <see cref="RoadGraph.Rasterise"/>.
        /// </param>
        /// <param name="map">The map to fill.</param>
        /// <param name="rng">
        /// The caller's stream. Water and foliage each take a labelled sub-stream, so retuning
        /// either cannot move the other.
        /// </param>
        /// <param name="options">Tunables; use <see cref="TerrainFillOptions.Default"/>.</param>
        /// <returns>What was scattered, and where the water is.</returns>
        public static TerrainFillReport Fill(
            NeighbourhoodMapInput input,
            RoadGraph graph,
            NeighbourhoodMap map,
            NeighbourhoodRng rng,
            TerrainFillOptions options)
        {
            if (input == null) throw new ArgumentNullException(nameof(input));
            if (graph == null) throw new ArgumentNullException(nameof(graph));
            if (map == null) throw new ArgumentNullException(nameof(map));

            var size = input.MapSize;
            var lots = input.UsableLots;
            var count = size * size;

            int[] chamfer;
            var owners = NearestLotOwners(lots, size, out chamfer);

            // 1. Ground type. A lot's own interior is its own type; everything else takes the type
            //    of the nearest lot, so the change happens once, along a coherent boundary.
            for (int i = 0; i < count; i++)
            {
                var type = owners[i] >= 0 ? lots[owners[i]].TerrainType : NeighbourhoodTerrain.Grass;
                if (!NeighbourhoodTerrain.IsDrawable(type)) type = NeighbourhoodTerrain.Grass;
                map.TerrainType[i] = type;
                map.TerrainTypeColorData[i] = NeighbourhoodTerrain.ColorFor(type);
            }

            // 2. Water: never on a lot, a road, a sidewalk, or close enough to a lot that a
            //    driveway would have to cross it.
            var water = new bool[count];
            int waterTiles = 0;
            if (options.WaterEnabled && lots.Count > 0)
            {
                var coarse = new ValueNoise(size, Math.Max(4, size / 4), rng.ForLabel("water.coarse"));
                var fine = new ValueNoise(size, Math.Max(4, size / 16), rng.ForLabel("water.fine"));
                var minDistance = options.WaterMinLotDistance * ChamferOrthogonal;
                for (int y = 0; y < size; y++)
                {
                    for (int x = 0; x < size; x++)
                    {
                        var i = y * size + x;
                        if (!MayPlant(graph, i)) continue;
                        if (chamfer[i] < minDistance) continue;
                        if ((coarse.At(x, y) + 2 * fine.At(x, y)) / 3 < options.WaterLevel) continue;
                        water[i] = true;
                        waterTiles++;
                        map.TerrainType[i] = NeighbourhoodTerrain.Water;
                        map.TerrainTypeColorData[i] =
                            NeighbourhoodTerrain.ColorFor(NeighbourhoodTerrain.Water);
                    }
                }
            }

            // 3. Where foliage may go at all. One mask, used both to reject candidate tree
            //    positions and to trim the painted discs, so a disc can never leave foliage on a
            //    tile the candidate test refused.
            var keep = new bool[count];
            var community = CommunityClearanceMask(lots, size, options.CommunityClearance);
            for (int i = 0; i < count; i++)
            {
                keep[i] = MayPlant(graph, i) && !water[i] && !community[i];
            }

            int[] treeTiles;
            var trees = ScatterFoliage(keep, map, size, options, rng.ForLabel("forest"), out treeTiles);

            return new TerrainFillReport(trees, treeTiles, waterTiles, water, owners);
        }

        /// <summary>
        /// The tiles a community lot claims: its whole rectangle plus
        /// <paramref name="clearance"/> tiles of margin, so a park has a treeless apron.
        /// </summary>
        private static bool[] CommunityClearanceMask(
            IReadOnlyList<NeighbourhoodLotInput> lots, int size, int clearance)
        {
            var mask = new bool[size * size];
            if (clearance <= 0) return mask;

            for (int i = 0; i < lots.Count; i++)
            {
                var lot = lots[i];
                if (lot.Zone != LotZoning.ZoneCommunity) continue;
                for (var y = Math.Max(0, lot.WorldY - clearance);
                     y < Math.Min(size, lot.Bottom + clearance); y++)
                {
                    for (var x = Math.Max(0, lot.WorldX - clearance);
                         x < Math.Min(size, lot.Right + clearance); x++)
                    {
                        mask[y * size + x] = true;
                    }
                }
            }
            return mask;
        }

        private static bool MayPlant(RoadGraph graph, int index)
        {
            return !graph.LotMask.Interior[index] && !graph.Road[index] && !graph.Sidewalk[index];
        }

        /// <summary>
        /// Bridson's Poisson-disk scatter over the tiles <paramref name="keep"/> allows, then a
        /// density disc per accepted tree.
        /// </summary>
        private static int ScatterFoliage(
            bool[] keep, NeighbourhoodMap map, int size, TerrainFillOptions options,
            NeighbourhoodRng rng, out int[] treeTiles)
        {
            treeTiles = new int[0];
            if (options.MaxTrees <= 0) return 0;

            var spacing = options.TreeSpacing;
            var across = (size + spacing - 1) / spacing;
            var cells = new List<int>[across * across];
            var points = new List<int>();
            var active = new List<int>();

            // Seed with the first eligible tile in raster order. A random seed tile would also be
            // deterministic, but a raster one means "no trees at all" is obviously "nowhere was
            // eligible" rather than "the seed was unlucky".
            var seed = -1;
            for (int i = 0; i < keep.Length; i++)
            {
                if (keep[i]) { seed = i; break; }
            }
            if (seed < 0) return 0;
            AddPoint(points, active, cells, across, spacing, size, seed);

            while (active.Count > 0 && points.Count < options.MaxTrees)
            {
                var k = rng.NextInt(0, active.Count);
                var origin = points[active[k]];
                var ox = origin % size;
                var oy = origin / size;

                var placed = false;
                for (int dart = 0; dart < options.DartsPerActiveTree && !placed; dart++)
                {
                    // Integer candidate offsets: up to two cells out, in one of nine directions.
                    // No trigonometry, hence no dependence on the maths library.
                    var stepX = rng.NextInt(-1, 2);
                    var stepY = rng.NextInt(-1, 2);
                    var reach = spacing + dart * spacing / options.DartsPerActiveTree;
                    var x = ox + stepX * reach;
                    var y = oy + stepY * reach;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    var index = y * size + x;
                    if (!keep[index]) continue;
                    if (TooClose(points, cells, across, spacing, size, x, y)) continue;

                    AddPoint(points, active, cells, across, spacing, size, index);
                    placed = true;
                }

                if (!placed) active.RemoveAt(k);
            }

            for (int i = 0; i < points.Count; i++)
            {
                PaintTree(map, keep, size, points[i], spacing, options.MaxTreeDensity,
                    FoliageTints[rng.NextInt(0, FoliageTints.Length)]);
            }

            treeTiles = points.ToArray();
            return points.Count;
        }

        private static void AddPoint(
            List<int> points, List<int> active, List<int>[] cells, int across,
            int spacing, int size, int index)
        {
            var point = points.Count;
            points.Add(index);
            active.Add(point);

            var gx = (index % size) / spacing;
            var gy = (index / size) / spacing;
            var cell = gy * across + gx;
            if (cells[cell] == null) cells[cell] = new List<int>();
            cells[cell].Add(point);
        }

        /// <summary>
        /// Whether an accepted tree is already within <paramref name="spacing"/> tiles, in
        /// Chebyshev distance. Only the eight surrounding cells have to be consulted: a point two
        /// cells away is at least <c>spacing + 1</c> tiles off in that axis.
        /// </summary>
        private static bool TooClose(
            List<int> points, List<int>[] cells, int across, int spacing, int size, int x, int y)
        {
            var cx = x / spacing;
            var cy = y / spacing;
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var gx = cx + dx;
                    var gy = cy + dy;
                    if (gx < 0 || gy < 0 || gx >= across || gy >= across) continue;
                    var bucket = cells[gy * across + gx];
                    if (bucket == null) continue;
                    for (int i = 0; i < bucket.Count; i++)
                    {
                        var other = points[bucket[i]];
                        if (Math.Max(Math.Abs(x - other % size), Math.Abs(y - other / size)) < spacing)
                        {
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        private static void PaintTree(
            NeighbourhoodMap map, bool[] keep, int size, int index,
            int spacing, byte maxDensity, Color tint)
        {
            var radius = spacing * 2;
            var radiusSquared = radius * radius;
            var cx = index % size;
            var cy = index / size;

            for (var dy = -radius; dy <= radius; dy++)
            {
                var y = cy + dy;
                if (y < 0 || y >= size) continue;
                for (var dx = -radius; dx <= radius; dx++)
                {
                    var x = cx + dx;
                    if (x < 0 || x >= size) continue;
                    var squared = dx * dx + dy * dy;
                    if (squared > radiusSquared) continue;

                    var i = y * size + x;
                    if (!keep[i]) continue;
                    var density = (byte)((radiusSquared - squared) * (int)maxDensity / radiusSquared);
                    if (density > map.ForestDensityData[i])
                    {
                        map.ForestDensityData[i] = density;
                        map.ForestTypeData[i] = tint;
                    }
                }
            }
        }

        /// <summary>
        /// For each tile, the index into <paramref name="lots"/> of the lot whose rectangle is
        /// nearest, or -1 when there are no lots.
        /// </summary>
        /// <param name="lots">The lots to measure from.</param>
        /// <param name="mapSize">The side length of the map.</param>
        public static int[] NearestLotOwners(IReadOnlyList<NeighbourhoodLotInput> lots, int mapSize)
        {
            int[] distance;
            return NearestLotOwners(lots, mapSize, out distance);
        }

        /// <summary>
        /// For each tile, the index into <paramref name="lots"/> of the lot whose rectangle is
        /// nearest, plus the chamfer distance that decided it.
        /// </summary>
        /// <param name="lots">The lots to measure from.</param>
        /// <param name="mapSize">The side length of the map.</param>
        /// <param name="chamferDistance">
        /// The distance per tile in chamfer units - three units per orthogonal step, four per
        /// diagonal, so one tile is about three units. The water mask's clearance check divides by
        /// <see cref="ChamferOrthogonal"/>.
        /// </param>
        public static int[] NearestLotOwners(
            IReadOnlyList<NeighbourhoodLotInput> lots, int mapSize, out int[] chamferDistance)
        {
            if (mapSize < 1) throw new ArgumentOutOfRangeException(nameof(mapSize));
            var count = mapSize * mapSize;
            var distance = new int[count];
            var owner = new int[count];
            for (int i = 0; i < count; i++)
            {
                distance[i] = Unreached;
                owner[i] = -1;
            }

            if (lots != null)
            {
                for (int l = 0; l < lots.Count; l++)
                {
                    var lot = lots[l];
                    for (var y = Math.Max(0, lot.WorldY); y < Math.Min(mapSize, lot.Bottom); y++)
                    {
                        for (var x = Math.Max(0, lot.WorldX); x < Math.Min(mapSize, lot.Right); x++)
                        {
                            var i = y * mapSize + x;
                            // A tile inside two lots belongs to the lower-indexed one, which is the
                            // same tie-break the propagation below uses.
                            if (distance[i] == 0 && owner[i] <= l) continue;
                            distance[i] = 0;
                            owner[i] = l;
                        }
                    }
                }
            }

            if (lots == null || lots.Count == 0)
            {
                chamferDistance = distance;
                return owner;
            }

            for (int y = 0; y < mapSize; y++)
            {
                for (int x = 0; x < mapSize; x++)
                {
                    var i = y * mapSize + x;
                    if (x > 0) Improve(i, i - 1, ChamferOrthogonal, distance, owner);
                    if (y > 0) Improve(i, i - mapSize, ChamferOrthogonal, distance, owner);
                    if (x > 0 && y > 0) Improve(i, i - mapSize - 1, ChamferDiagonal, distance, owner);
                    if (x + 1 < mapSize && y > 0) Improve(i, i - mapSize + 1, ChamferDiagonal, distance, owner);
                }
            }

            for (int y = mapSize - 1; y >= 0; y--)
            {
                for (int x = mapSize - 1; x >= 0; x--)
                {
                    var i = y * mapSize + x;
                    if (x + 1 < mapSize) Improve(i, i + 1, ChamferOrthogonal, distance, owner);
                    if (y + 1 < mapSize) Improve(i, i + mapSize, ChamferOrthogonal, distance, owner);
                    if (x + 1 < mapSize && y + 1 < mapSize) Improve(i, i + mapSize + 1, ChamferDiagonal, distance, owner);
                    if (x > 0 && y + 1 < mapSize) Improve(i, i + mapSize - 1, ChamferDiagonal, distance, owner);
                }
            }

            chamferDistance = distance;
            return owner;
        }

        private static void Improve(int target, int from, int cost, int[] distance, int[] owner)
        {
            if (distance[from] == Unreached) return;
            var candidate = distance[from] + cost;
            if (candidate > distance[target]) return;
            // A tie is broken on the lower lot index, so ownership never depends on the order the
            // two sweeps happened to reach a tile.
            if (candidate == distance[target] && owner[from] >= owner[target]) return;
            distance[target] = candidate;
            owner[target] = owner[from];
        }

        /// <summary>
        /// A seeded bilinear value-noise lattice, sampled entirely in integer arithmetic. Two of
        /// these at different cell sizes give a water mask with both a coastline-scale shape and a
        /// ragged edge, without one floating-point operation.
        /// </summary>
        private sealed class ValueNoise
        {
            private readonly int[] _lattice;
            private readonly int _across;
            private readonly int _cell;

            internal ValueNoise(int size, int cell, NeighbourhoodRng rng)
            {
                _cell = cell < 1 ? 1 : cell;
                _across = size / _cell + 2;
                _lattice = new int[_across * _across];
                for (int i = 0; i < _lattice.Length; i++)
                {
                    _lattice[i] = rng.NextInt(0, 1024);
                }
            }

            internal int At(int x, int y)
            {
                var gx = x / _cell;
                var gy = y / _cell;
                if (gx + 1 >= _across) gx = _across - 2;
                if (gy + 1 >= _across) gy = _across - 2;
                var fx = x - gx * _cell;
                var fy = y - gy * _cell;

                var a = Sample(gx, gy);
                var b = Sample(gx + 1, gy);
                var c = Sample(gx, gy + 1);
                var d = Sample(gx + 1, gy + 1);

                var top = a + (b - a) * fx / _cell;
                var bottom = c + (d - c) * fx / _cell;
                return top + (bottom - top) * fy / _cell;
            }

            private int Sample(int gx, int gy)
            {
                if (gx < 0) gx = 0;
                if (gy < 0) gy = 0;
                if (gx >= _across) gx = _across - 1;
                if (gy >= _across) gy = _across - 1;
                return _lattice[gy * _across + gx];
            }
        }
    }
}
