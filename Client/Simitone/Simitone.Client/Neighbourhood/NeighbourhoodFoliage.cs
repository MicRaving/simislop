/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.Common.Utils;
using FSO.Files.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The trees on a generated map's connective ground, drawn from the shipped tree models and
    /// the shipped tree atlas.
    ///
    /// <para><b>Why this is not <c>CityFoliage</c>, and what of it is.</b> This is the same pass,
    /// reproduced against a generated map rather than a <c>Cities/city_XXXX</c> folder, and it
    /// follows <c>tso.client/Rendering/City/CityFoliage.cs</c> for everything that does not depend
    /// on the map being the city's own:
    /// </para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <b>Models</b> - the eighteen <c>Content/3D</c> obj files, <c>pine1..4</c>,
    ///     <c>tree1..4</c>, <c>palm1..4</c>, <c>cactus1..3</c> and <c>snow1..3</c>
    ///     (<c>CityFoliage.cs:36-51</c>, the table in <see cref="NeighbourhoodFoliageTint"/>), each
    ///     reduced to one vertex list and one index list with its triangles ordered back to front
    ///     (<c>CityFoliage.LoadModel:82-87</c>, see <see cref="Flatten"/>).
    ///   </description></item>
    ///   <item><description>
    ///     <b>Density</b> - <c>TreeCounts = {1, 4, 7, 15}</c> indexed by
    ///     <c>(ForestDensityData * 4) / 255</c> (<c>CityFoliage.cs:101, 201-203</c>), as
    ///     <see cref="TreesPerDensity"/>.
    ///   </description></item>
    ///   <item><description>
    ///     <b>Placement</b> - deterministic per tile, a yaw out of the same stream, the ground
    ///     height from the same Catmull-Rom interpolation over the elevation bytes the ground mesh
    ///     itself uses (<c>CityFoliage.GenerateChunk:239-249</c>, see
    ///     <see cref="GroundHeightAt"/>), and the <c>1/75f</c> model scale
    ///     (<c>CityFoliage.cs:185</c>).
    ///   </description></item>
    ///   <item><description>
    ///     <b>One buffer per 16x16 chunk</b>, with the tree's world position carried in the
    ///     vertex's <c>Normal</c> slot so that <c>VerShader</c>'s <c>TreeVS</c> pass adds it to the
    ///     transformed position (<c>CityFoliage.cs:250-262</c>) - which is how one buffer holds a
    ///     whole wood.
    ///   </description></item>
    ///   <item><description>
    ///     <b>The draw</b> - <c>RenderCityObj</c>'s <c>TreeVS</c> vertex pass against the fog pixel
    ///     pass, the tree atlas on the object sampler, and the city's own depth bias
    ///     (<c>CityFoliage.cs:129-145</c>), see <see cref="TreeDepthBias"/>.
    ///   </description></item>
    /// </list>
    /// <para>
    /// What is <b>not</b> TSO's is the species table. TSO reads <c>ForestTypeData</c> as a key into
    /// a dictionary of four specific colours and throws on anything else
    /// (<c>CityFoliage.cs:92-99</c>); a generated map's foliage tints come from M2's own five-entry
    /// palette, and deriving a species from the tint (<see cref="NeighbourhoodFoliageTint"/>) rather
    /// than looking one up is the only way to draw them without changing the generator. Nor is the
    /// tree budget: TSO builds and drops chunks as the camera moves
    /// (<c>CityFoliage.cs:115-127</c>), and a 512x512 generated map is too much to rebuild per move.
    /// </para>
    ///
    /// <para><b>It is bounded, and the budget is spread, not raced.</b> A 512x512 map with dense
    /// forest is a quarter of a million tiles, and TSO's own density table puts up to fifteen
    /// trees on one of them. That is millions of vertices, so there is a hard budget. Spending it
    /// first-come-first-served in map order is the obvious arrangement and it is wrong here: the
    /// generated forest is dense almost everywhere, fifteen trees to the tile, so a first-come
    /// walk spends the whole budget inside the first few rows of the map - and the first rows are
    /// the narrow top of the isometric diamond, which is off screen and then some. The log said
    /// four thousand trees and the player saw an empty field. So the budget is divided evenly over
    /// the chunks that can actually hold one, by
    /// <see cref="NeighbourhoodFoliagePlan"/>, and the walk is confined to the drawn part of the
    /// map. <see cref="TreeBudget"/> is the number to raise once somebody has looked at the frame
    /// rate.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodFoliage : IDisposable
    {
        /// <summary>Tile edge of one chunk of trees; the same 16 the city uses.</summary>
        public const int ChunkSize = 16;

        /// <summary>Side of the generated map the chunk grid and the clip are defined for.</summary>
        public const int MapSize = NeighbourhoodCityMeshBuilder.MapSize;

        /// <summary>Chunks per axis on a 512 map.</summary>
        public const int ChunksPerAxis = MapSize / ChunkSize;

        /// <summary>
        /// How many trees the whole map may have. Four is enough to read as a wood at the zoom the
        /// neighbourhood view sits at, and small enough that the vertex buffers are a few megabytes
        /// rather than a few hundred.
        /// </summary>
        public const int TreeBudget = 4096;

        /// <summary>
        /// Trees per tile, by density bucket, verbatim from <c>CityFoliage.TreeCounts</c>
        /// (<c>CityFoliage.cs:101</c>). The bucket is <c>(ForestDensity * 4) / 255</c>, so 0 is "no
        /// trees" and 1..3 index this.
        /// </summary>
        public static readonly int[] TreesPerDensity = { 1, 4, 7, 15 };

        /// <summary>
        /// World scale the shipped tree models are authored at. The models are in centimetre-ish
        /// units - the same <c>1/75f</c> <c>CityFoliage.cs:185</c> uses to get a 75-unit tall pine
        /// onto a one-tile-high world.
        /// </summary>
        public const float ModelScale = 1f / 75f;

        /// <summary>
        /// The depth bias the trees are drawn with, from <c>CityFoliage.cs:133</c>:
        /// <c>-0.12f * terrain.Camera.DepthBiasScale</c>, where <c>DepthBiasScale</c> is 1 in the
        /// 3D view (<c>CityCamera3D.cs:83-89</c>) and 0.01 in the 2D one
        /// (<c>CityCamera2D.cs:44-50</c>).
        /// </summary>
        /// <remarks>
        /// The neighbourhood view is a 3D camera, so it takes the 3D figure. It is a larger bias
        /// than the -0.02 this pass used to hard-code, and that is the point: a tree's own trunk and
        /// canopy are alpha-blended geometry standing on a tile of ground, and a bias too small
        /// lets the ground z-fight its own trees at grazing angles.
        /// </remarks>
        public const float TreeDepthBias = -0.12f;

        /// <summary>
        /// What the loaded tree models occupy, in world tiles, or <c>null</c> when none of them
        /// loaded.
        /// </summary>
        /// <remarks>
        /// Read rather than assumed, because the chunk's bounding box has to contain a whole tree
        /// and not only the ground it stands on: a box that stops at the maximum ground height culls
        /// the top of every canopy on screen. TSO gets away with the same mistake by accident -
        /// <c>CityFoliage.cs:178</c> writes <c>(x+1)*32</c> against a 16-tile chunk, so its box is
        /// twice as wide as the chunk is on both axes and happens to be loose enough - and its top
        /// is <c>255/12f</c> either.
        /// </remarks>
        public BoundingBox? ModelBounds { get; private set; }

        private readonly GraphicsDevice _device;
        private readonly NeighbourhoodMap _map;
        private readonly Dictionary<int, NeighbourhoodFoliageChunk> _chunks =
            new Dictionary<int, NeighbourhoodFoliageChunk>();

        private DGRP3DVert[][] _modelVerts;
        private int[][] _modelIndices;
        private readonly List<string> _warnings = new List<string>();

        /// <summary>
        /// The union of every loaded model's bounding box, already in world tiles, or <c>null</c>.
        /// </summary>
        /// <param name="modelVerts">The per-model vertex lists, as loaded so far.</param>
        /// <returns>The world-space box, or <c>null</c> when nothing has geometry.</returns>
        /// <remarks>
        /// The union rather than each model's own box, because the box that has to contain a tree
        /// is the one that has to contain the tallest and widest of the eighteen - a pine and a
        /// cactus are an order of magnitude apart and there is one box per chunk.
        /// </remarks>
        public static BoundingBox? ModelBoundsOf(DGRP3DVert[][] modelVerts)
        {
            if (modelVerts == null) return null;
            BoundingBox? bounds = null;
            for (int i = 0; i < modelVerts.Length; i++)
            {
                var verts = modelVerts[i];
                if (verts == null || verts.Length == 0) continue;
                var min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
                var max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
                for (int v = 0; v < verts.Length; v++)
                {
                    var p = verts[v].Position * ModelScale;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                }
                var model = new BoundingBox(min, max);
                bounds = bounds.HasValue ? BoundingBox.CreateMerged(bounds.Value, model) : model;
            }
            return bounds;
        }

        /// <summary>
        /// Every warning line this process has already logged, so a file that is missing from an
        /// install is reported when it is first found and not on every rebuild of the pass.
        /// </summary>
        private static readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>What could not be loaded or built.</summary>
        public IReadOnlyList<string> Warnings
        {
            get { return _warnings; }
        }

        /// <summary>How many trees are on the map right now.</summary>
        public int TreeCount { get; private set; }

        /// <summary>How many chunks the plan found that could hold a tree.</summary>
        public int EligibleChunkCount { get; private set; }

        /// <summary>The even share of <see cref="TreeBudget"/> each eligible chunk is given.</summary>
        public int TreesPerChunkShare { get; private set; }

        /// <summary>
        /// Forested, road-free tiles that fall outside the isometric diamond and are therefore
        /// never walked. On a square map this is a large number, and it is the reason the budget
        /// used to be spent on trees nobody could see. The whole number, not a sample - see
        /// <see cref="NeighbourhoodFoliagePlan.TilesOutsideClip"/>.
        /// </summary>
        public int TilesOutsideClip { get; private set; }

        /// <summary>One line for the log, saying where the trees went.</summary>
        public string Describe()
        {
            return TreeCount + " trees over " + _chunks.Count + " chunks ("
                + EligibleChunkCount + " eligible, " + TreesPerChunkShare + " each of a "
                + TreeBudget + " budget, " + TilesOutsideClip + " forested tiles outside the clip)";
        }

        /// <summary>Whether there is anything to draw.</summary>
        public bool HasTrees
        {
            get { return TreeCount > 0 && _modelVerts != null; }
        }

        /// <summary>
        /// Loads the tree models and builds every chunk. Synchronous and on the game thread: it is
        /// eighteen small obj files and a bounded walk, and the alternative - a worker with a
        /// graphics-device upload - is the kind of thing that is only safe once it has been looked
        /// at on a real machine.
        /// </summary>
        /// <param name="device">The graphics device.</param>
        /// <param name="map">The generated map.</param>
        /// <param name="content">The tree atlas, or <c>null</c> to skip the whole pass.</param>
        public NeighbourhoodFoliage(GraphicsDevice device, NeighbourhoodMap map, NeighbourhoodContent content)
        {
            _device = device;
            _map = map;
            if (device == null || map == null || content == null || content.TreeAtlas == null) return;
            if (map.Width != MapSize || map.Height != MapSize)
            {
                //the chunk grid, and the isometric clip both the walk and the ground are bounded
                //by, are defined for a 512 map; anything else is a map the mesh builder refuses
                //too, so there is nothing coherent to place trees on
                _warnings.Add("the generated map is " + map.Width + "x" + map.Height
                    + " but the tree pass is defined for a " + MapSize + "x" + MapSize + " map");
                WarnOnce(_warnings[_warnings.Count - 1]);
                return;
            }

            var root = NeighbourhoodContent.ResolveRoot(NeighbourhoodContent.DefaultRootCandidates(), File.Exists);
            if (root == null)
            {
                _warnings.Add("no game folder with " + NeighbourhoodContent.ModelFolder
                    + " was found, so the generated neighbourhood has no trees on it");
                WarnOnce(_warnings[_warnings.Count - 1]);
                return;
            }

            if (!LoadModels(root))
            {
                _warnings.Add("the shipped tree models could not be read, so the generated"
                    + " neighbourhood has no trees on it");
                WarnOnce(_warnings[_warnings.Count - 1]);
                return;
            }

            Build();

            // A tree count on its own cannot distinguish "no forest" from "all the forest is off
            // screen", which is the failure this pass had, so the line says where the trees went
            // and not only how many there are.
            SimitoneLog.Info("The generated neighbourhood's trees: " + Describe());
            // once per process, not once per rebuild: every one of these lines is a fact about
            // the install or about this map, and a fact that is re-announced each time the
            // neighbourhood view is opened is noise in the log the player sends you.
            foreach (var warning in _warnings) WarnOnce(warning);
        }

        private bool LoadModels(string root)
        {
            var folder = Path.Combine(root, NeighbourhoodContent.ModelFolder);
            _modelVerts = new DGRP3DVert[NeighbourhoodFoliageTint.ModelCount][];
            _modelIndices = new int[NeighbourhoodFoliageTint.ModelCount][];

            //Every model the shipped folder does not have, or cannot parse, falls back to one that
            //does rather than leaving a hole. The set is normally empty - ModelFiles is the list
            //of the eighteen names that ship - but "the install a player has" is not something the
            //table can promise, and a missing model is a wood with a bald patch in it rather than
            //a neighbourhood with no trees on it.
            var absent = new List<int>();
            for (int i = 0; i < _modelVerts.Length; i++)
            {
                var path = Path.Combine(folder, NeighbourhoodFoliageTint.FileNameFor(i));
                if (!File.Exists(path))
                {
                    absent.Add(i);
                    continue;
                }
                if (!TryReadModel(path, out var verts, out var indices))
                {
                    absent.Add(i);
                    continue;
                }
                _modelVerts[i] = verts;
                _modelIndices[i] = indices;
            }

            var fallback = FallbackModelIndex(_modelVerts);
            if (fallback < 0)
            {
                _warnings.Add("none of the " + NeighbourhoodFoliageTint.ModelCount
                    + " shipped tree models in " + folder + " could be read, so the generated"
                    + " neighbourhood has no trees on it");
                return false;
            }

            //The box a chunk's canopy has to fit inside. Read here, once, from the models that
            //actually loaded - a tree drawn with the fallback model is the fallback model's size,
            //so the bounds have to come from the same array the draw indexes.
            ModelBounds = ModelBoundsOf(_modelVerts);

            for (int i = 0; i < absent.Count; i++)
            {
                var index = absent[i];
                _modelVerts[index] = _modelVerts[fallback];
                _modelIndices[index] = _modelIndices[fallback];
                _warnings.Add("the tree model " + Path.Combine(folder, NeighbourhoodFoliageTint.FileNameFor(index))
                    + " is not in this install, so model " + index + " is drawn with "
                    + NeighbourhoodFoliageTint.FileNameFor(fallback) + " instead");
            }
            return true;
        }

        /// <summary>
        /// The model a missing one falls back to: the lowest index that has geometry.
        /// </summary>
        /// <remarks>
        /// Lowest, not first-found, and not random: the fallback is part of what a generated
        /// neighbourhood looks like, so it has to be the same model on every machine and in every
        /// run for the same install. Pure, so the rule is asserted without a graphics device.
        /// -1 when nothing loaded, which the caller reports rather than falling back to.
        /// </remarks>
        /// <param name="modelVerts">The per-model vertex lists, as loaded so far.</param>
        public static int FallbackModelIndex(DGRP3DVert[][] modelVerts)
        {
            if (modelVerts == null) return -1;
            for (int i = 0; i < modelVerts.Length; i++)
            {
                if (modelVerts[i] != null && modelVerts[i].Length > 0) return i;
            }
            return -1;
        }

        /// <summary>
        /// One obj off disk, reduced to vertices and indices, or false with the reason said once.
        /// </summary>
        private static bool TryReadModel(string path, out DGRP3DVert[] verts, out int[] indices)
        {
            try
            {
                OBJ obj;
                using (var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    obj = new OBJ(stream);
                }
                var model = Flatten(obj);
                verts = model.Item1;
                indices = model.Item2;
                if (verts != null && verts.Length > 0 && indices != null && indices.Length > 0) return true;
                verts = null;
                indices = null;
                WarnOnce("the tree model " + path + " has no geometry in it, so it was not used");
                return false;
            }
            catch (Exception ex)
            {
                verts = null;
                indices = null;
                WarnOnce("could not read the tree model " + path + ": " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Logs a line the first time this process sees it, and never again.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A missing model file is a property of the install, not of the map, and the foliage pass
        /// is rebuilt every time the neighbourhood view is opened - so a warning that is only
        /// suppressed per instance repeats itself for the whole session, which is the same as
        /// saying it per frame as far as the log is concerned. The key is the whole line, so two
        /// different missing files still get two lines and the same one does not get a second.
        /// </para>
        /// <para>
        /// Static because that is the scope of "once": the check has to outlive the instance that
        /// made it. Returns whether it logged, so a caller - and a test - can tell the difference
        /// between "said nothing because it has already been said" and "said nothing at all".
        /// </para>
        /// </remarks>
        /// <param name="message">The line.</param>
        public static bool WarnOnce(string message)
        {
            if (string.IsNullOrEmpty(message)) return false;
            lock (_warned)
            {
                if (!_warned.Add(message)) return false;
            }
            SimitoneLog.Warn(message);
            return true;
        }

        /// <summary>
        /// The file in <see cref="NeighbourhoodContent.ModelFolder"/> that a tree model index
        /// names, or <c>null</c> when the index is out of range.
        /// </summary>
        /// <remarks>
        /// A straight delegation to <see cref="NeighbourhoodFoliageTint.FileNameFor"/>, kept
        /// because this is the name the load site asks for and the tests read.
        /// </remarks>
        /// <param name="modelIndex">A model index, 0 to <c>ModelCount - 1</c>.</param>
        public static string FileNameFor(int modelIndex)
        {
            return NeighbourhoodFoliageTint.FileNameFor(modelIndex);
        }

        /// <summary>
        /// An obj reduced to one vertex list and one index list, with triangles ordered back to
        /// front by height.
        /// </summary>
        /// <remarks>
        /// The height ordering is not cosmetic: the tree is drawn with alpha blending, so drawing
        /// its lower triangles first is what stops a semi-transparent canopy from showing the
        /// inside of the far side through the near side. <c>CityFoliage.LoadModel:82-87</c> sorts
        /// for exactly the same reason and by exactly this key - the sum of the three corners' y -
        /// and the mesh's own per-triangle sort is what that produces.
        /// <para>
        /// Stable, where <c>CityFoliage</c>'s <c>OrderBy</c> is and a plain <c>List.Sort</c> is not.
        /// A tree model has many triangles at the same height - the trunk's own quads, a canopy's
        /// flat top - and an unstable sort is free to permute them between runs and between
        /// runtimes, which would make two machines draw the same install with different triangles.
        /// The index tie-break is what pins the order to the order the obj listed them in.
        /// </para>
        /// </remarks>
        private static Tuple<DGRP3DVert[], int[]> Flatten(OBJ obj)
        {
            var vertices = new List<DGRP3DVert>();
            var indices = new List<int>();
            var seen = new Dictionary<Tuple<int, int>, int>();

            var faces = obj.FacesByObjgroup.FirstOrDefault(x => x.Value != null && x.Value.Count > 0).Value;
            if (faces == null) return Tuple.Create(new DGRP3DVert[0], new int[0]);

            foreach (var face in faces)
            {
                var key = new Tuple<int, int>(face[0], face[1]);
                int index;
                if (!seen.TryGetValue(key, out index))
                {
                    index = vertices.Count;
                    var vert = new DGRP3DVert(obj.Vertices[face[0] - 1], Vector3.Zero, obj.TextureCoords[face[1] - 1]);
                    //the atlas is authored the other way up from the models' uv convention
                    vert.TextureCoordinate.Y = 1 - vert.TextureCoordinate.Y;
                    vertices.Add(vert);
                    seen.Add(key, index);
                }
                indices.Add(index);
            }

            var order = new int[indices.Count / 3];
            for (int i = 0; i < order.Length; i++) order[i] = i;
            var heights = new float[order.Length];
            for (int i = 0; i < order.Length; i++) heights[i] = Height(vertices, indices, i * 3);
            Array.Sort(order, (a, b) =>
            {
                var byHeight = heights[a].CompareTo(heights[b]);
                return byHeight != 0 ? byHeight : a.CompareTo(b);
            });

            indices.Clear();
            foreach (var triangle in order)
            {
                indices.Add(triangle * 3);
                indices.Add(triangle * 3 + 1);
                indices.Add(triangle * 3 + 2);
            }
            return Tuple.Create(vertices.ToArray(), indices.ToArray());
        }

        /// <summary>
        /// The sum of a triangle's three corner heights, which is <c>CityFoliage.cs:85</c>'s sort
        /// key.
        /// </summary>
        private static float Height(List<DGRP3DVert> vertices, List<int> indices, int first)
        {
            return vertices[indices[first]].Position.Y
                + vertices[indices[first + 1]].Position.Y
                + vertices[indices[first + 2]].Position.Y;
        }

        /// <summary>
        /// Whether a tile can carry a tree at all: it is inside the drawn region, the ground mesh
        /// emits a quad for it, it is not road, and it has forest on it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>This is the one answer, and both callers ask it.</b> The eligibility walk
        /// (<see cref="NeighbourhoodFoliagePlan.For"/>) and the chunk build
        /// (<see cref="BuildChunk"/>) used to each spell out their own version, and the bug the
        /// live log kept printing is the divergence between them and the ground: a tree is drawn
        /// wherever this says yes, so a tile that gets no ground quad is a tree standing on nothing.
        /// </para>
        /// <para>
        /// <b>The drawn region is the road diamond, not the fade skirt.</b>
        /// <see cref="NeighbourhoodCityMeshBuilder.IsInsideDiamond"/> is the strict test the road
        /// pass uses and the one the mesh clamps corners into
        /// (<c>NeighbourhoodCityMeshBuilder.cs:2204-2208</c>); the ground reaches
        /// <see cref="NeighbourhoodCityMeshBuilder.FadeRange"/> tiles past it and fades out there.
        /// A tree needs opaque ground under it, so it takes the strict test - which is inside the
        /// skirt by ten tiles, and is the only thing here that is not negotiable.
        /// </para>
        /// <para>
        /// <b>And a tile with no ground quad is not treeable either.</b> The ground pass skips
        /// any tile whose terrain byte is at or above
        /// <see cref="NeighbourhoodCityMeshBuilder.LayerCount"/> - which is
        /// <see cref="NeighbourhoodTerrain.Nothing"/> and anything corrupt
        /// (<c>NeighbourhoodCityMeshBuilder.cs:2635, 3264</c>). A tree on one of those is a tree in
        /// the void, which is the same defect as a tree outside the diamond and for the same reason.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map.</param>
        /// <param name="ox">Tile x.</param>
        /// <param name="oy">Tile y, the map row.</param>
        public static bool IsTreeable(NeighbourhoodMap map, int ox, int oy)
        {
            if (map == null) return false;
            if (ox < 0 || oy < 0 || ox >= map.Width || oy >= map.Height) return false;
            if (!NeighbourhoodCityMeshBuilder.IsInsideDiamond(ox, oy)) return false;
            var index = oy * map.Width + ox;
            if (map.TerrainType[index] >= NeighbourhoodCityMeshBuilder.LayerCount) return false;
            if (map.ForestDensityData[index] == 0) return false;
            return map.RoadMaskAt(index) == RoadMask.None;
        }

        /// <summary>
        /// How many trees a tile asks for, from its forest density, or 0 when it asks for none.
        /// </summary>
        /// <param name="density">The tile's <c>ForestDensityData</c> byte.</param>
        public static int TreesForDensity(int density)
        {
            var bucket = (density * 4) / 255;
            if (bucket <= 0 || bucket > TreesPerDensity.Length) return 0;
            return TreesPerDensity[bucket - 1];
        }

        /// <summary>
        /// The ground height under a point inside a tile, in world tiles.
        /// </summary>
        /// <remarks>
        /// <c>CityFoliage.GenerateChunk:239-249</c>, reproduced: the same four Catmull-Rom
        /// evaluations across <c>x</c> and then the same one across <c>y</c>, over the same
        /// elevation bytes the ground mesh interpolates. Reading the tile's own byte instead - which
        /// is what this did - puts a tree on the height of the tile's <em>corner</em> rather than
        /// of the ground it is standing on, so on any slope the wood floats above the terrain or
        /// sinks into it by up to a tile's worth of fall.
        /// <para>
        /// The sample index is <c>CityFoliage.O</c> (<c>CityFoliage.cs:103-106</c>): a clamp to the
        /// map and nothing else. The ground mesh's own sampler
        /// (<c>NeighbourhoodCityMeshBuilder.SampledElevation</c>) additionally clamps <c>x</c> into
        /// the row's diamond, so a tree within a tile of the edge can disagree with the mesh by the
        /// height of the clipped sliver. That is TSO's own arithmetic and the difference is inside
        /// the fade skirt, so it is kept rather than "corrected".
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map.</param>
        /// <param name="ox">The tile x.</param>
        /// <param name="oy">The tile y.</param>
        /// <param name="sx">Where in the tile in x, 0 to 1.</param>
        /// <param name="sy">Where in the tile in y, 0 to 1.</param>
        public static float GroundHeightAt(NeighbourhoodMap map, int ox, int oy, float sx, float sy)
        {
            if (map == null) return 0f;
            var md = map.ElevationData;
            var y1 = NeighbourhoodCityMeshBuilder.Cubic(Sample(md, ox - 1, oy - 1), Sample(md, ox - 1, oy),
                Sample(md, ox - 1, oy + 1), Sample(md, ox - 1, oy + 2), sy);
            var y2 = NeighbourhoodCityMeshBuilder.Cubic(Sample(md, ox, oy - 1), Sample(md, ox, oy),
                Sample(md, ox, oy + 1), Sample(md, ox, oy + 2), sy);
            var y3 = NeighbourhoodCityMeshBuilder.Cubic(Sample(md, ox + 1, oy - 1), Sample(md, ox + 1, oy),
                Sample(md, ox + 1, oy + 1), Sample(md, ox + 1, oy + 2), sy);
            var y4 = NeighbourhoodCityMeshBuilder.Cubic(Sample(md, ox + 2, oy - 1), Sample(md, ox + 2, oy),
                Sample(md, ox + 2, oy + 1), Sample(md, ox + 2, oy + 2), sy);
            var h = NeighbourhoodCityMeshBuilder.Cubic(y1, y2, y3, y4, sx);
            return NeighbourhoodCityMeshBuilder.WorldHeightFor(h);
        }

        /// <summary>
        /// An elevation byte with the index clamped to the map: <c>CityFoliage.O</c>
        /// (<c>CityFoliage.cs:103-106</c>).
        /// </summary>
        private static byte Sample(byte[] elevation, int x, int y)
        {
            x = x < 0 ? 0 : (x > MapSize - 1 ? MapSize - 1 : x);
            y = y < 0 ? 0 : (y > MapSize - 1 ? MapSize - 1 : y);
            return elevation[y * MapSize + x];
        }

        private void Build()
        {
            var plan = NeighbourhoodFoliagePlan.For(_map, TreeBudget);
            EligibleChunkCount = plan.EligibleChunks;
            TreesPerChunkShare = plan.SharePerChunk;
            TilesOutsideClip = plan.TilesOutsideClip;
            if (plan.EligibleChunks == 0)
            {
                _warnings.Add("no chunk of the generated map has both forest and room inside the"
                    + " isometric clip, so the generated neighbourhood has no trees on it");
                return;
            }

            for (int i = 0; i < plan.EligibleChunks; i++)
            {
                var chunkIndex = plan.Eligible[i];
                var share = plan.TreesFor(chunkIndex);
                if (share <= 0) continue;
                var chunk = BuildChunk(chunkIndex % ChunksPerAxis, chunkIndex / ChunksPerAxis, share);
                if (chunk == null) continue;
                TreeCount += chunk.TreeCount;
                _chunks[chunkIndex] = chunk;
            }
        }

        private NeighbourhoodFoliageChunk BuildChunk(int cx, int cy, int budget)
        {
            var verts = new List<DGRP3DVert>();
            var indices = new List<int>();
            var trees = 0;

            for (int oy = cy * ChunkSize; oy < (cy + 1) * ChunkSize && trees < budget; oy++)
            {
                for (int ox = cx * ChunkSize; ox < (cx + 1) * ChunkSize && trees < budget; ox++)
                {
                    //The drawn region, and only it: no ground under a tree, no tree. One predicate
                    //for the plan and for the build, so the two cannot disagree - see IsTreeable.
                    if (!IsTreeable(_map, ox, oy)) continue;

                    var index = oy * _map.Width + ox;
                    var wanted = TreesForDensity(_map.ForestDensityData[index]);
                    if (wanted <= 0) continue;

                    //deterministic per tile, so the same map always has the same wood
                    var rand = new NeighbourhoodRng(unchecked((ulong)index * 0x9E3779B97F4A7C15UL + 0x1234567));

                    for (int i = 0; i < wanted && trees < budget; i++)
                    {
                        var tint = _map.ForestTypeData[index];
                        var model = NeighbourhoodFoliageTint.ModelFor(tint, _map.TerrainType[index], rand.NextInt(0, 4));
                        if (model < 0 || model >= _modelVerts.Length) continue;
                        var modelVerts = _modelVerts[model];
                        if (modelVerts == null || modelVerts.Length == 0) continue;

                        var sx = (float)rand.NextDouble();
                        var sy = (float)rand.NextDouble();
                        var yaw = (float)(rand.NextDouble() * Math.PI * 2);
                        var ground = GroundHeightAt(_map, ox, oy, sx, sy);
                        var centre = new Vector3(ox + sx, ground, oy + sy);

                        var rotation = Matrix.CreateRotationY(yaw);
                        var baseIndex = verts.Count;
                        foreach (var vert in modelVerts)
                        {
                            var copy = vert;
                            var local = Vector3.Transform(vert.Position, rotation) * ModelScale;
                            copy.Position = new Vector3(centre.X + local.X, centre.Y + local.Y, centre.Z + local.Z);
                            //Carried, not shaded: VerShader's TreeVS pass adds the "normal" slot to
                            //the transformed position, which is how one buffer holds many trees.
                            copy.Normal = centre;
                            verts.Add(copy);
                        }
                        foreach (var modelIndex in _modelIndices[model]) indices.Add(modelIndex + baseIndex);
                        trees++;
                    }
                }
            }

            if (verts.Count == 0 || indices.Count == 0) return null;

            var chunk = new NeighbourhoodFoliageChunk
            {
                Ind = cx + cy * ChunksPerAxis,
                TreeCount = trees,
                Bounds = ChunkBoundsFor(cx, cy)
            };

            try
            {
                chunk.Vertices = new VertexBuffer(_device, typeof(DGRP3DVert), verts.Count, BufferUsage.None);
                chunk.Vertices.SetData(verts.ToArray());
                chunk.Indices = new IndexBuffer(_device, IndexElementSize.ThirtyTwoBits, indices.Count, BufferUsage.None);
                chunk.Indices.SetData(indices.ToArray());
            }
            catch (Exception ex)
            {
                _warnings.Add("could not upload a chunk of trees: " + ex.Message);
                chunk.Dispose();
                return null;
            }
            return chunk;
        }

        /// <summary>
        /// The box a chunk's trees occupy, from the models that actually loaded.
        /// </summary>
        /// <remarks>
        /// The 16x16 tile square is the floor of it. On top of that: the tallest tree, so a canopy
        /// is inside the box and not culled while it is on screen; and the model's sideways reach
        /// on all four sides, because a tree's trunk is jittered anywhere in its tile
        /// (<c>CityFoliage.cs:249</c>, <c>new Vector3(ox + sx, h/12f, oy + sy)</c>) and its canopy
        /// hangs over the tile it stands on.
        /// <para>
        /// The bottom is the ground, not zero: a tree is placed on the interpolated height at its
        /// own jittered point (<see cref="GroundHeightAt"/>) and the canopy hangs below that too,
        /// so the floor is the model's own lowest vertex rather than the terrain.
        /// </para>
        /// </remarks>
        public BoundingBox ChunkBoundsFor(int cx, int cy)
        {
            return ChunkBoundsFor(cx, cy, ModelBounds);
        }

        /// <summary>
        /// The box a chunk's trees occupy, given the models' own box.
        /// </summary>
        /// <param name="cx">The chunk's column.</param>
        /// <param name="cy">The chunk's row.</param>
        /// <param name="modelBounds">
        /// The models' union box in world tiles, from <see cref="ModelBoundsOf"/>, or <c>null</c>
        /// when nothing loaded - which gives a box for the ground alone.
        /// </param>
        public static BoundingBox ChunkBoundsFor(int cx, int cy, BoundingBox? modelBounds)
        {
            var radius = modelBounds.HasValue
                ? Math.Max(modelBounds.Value.Max.X, Math.Max(modelBounds.Value.Max.Z,
                    Math.Max(-modelBounds.Value.Min.X, -modelBounds.Value.Min.Z)))
                : 0f;
            var ground = modelBounds.HasValue ? modelBounds.Value.Min.Y : 0f;
            //the top is the ground a tree stands on plus the tallest tree on it, not one or the
            //other: a box that stops at the ground culls the canopy off every tree on the map, which
            //is the same visual defect as culling the trees
            var top = Math.Max(ground, NeighbourhoodCityMeshBuilder.WorldHeightFor(255f)
                + (modelBounds.HasValue ? modelBounds.Value.Max.Y : 0f));
            return new BoundingBox(
                new Vector3(cx * ChunkSize - radius, ground, cy * ChunkSize - radius),
                new Vector3((cx + 1) * ChunkSize + radius, top, (cy + 1) * ChunkSize + radius));
        }

        /// <summary>
        /// Throws the uploaded buffers away and builds them again, for a graphics device that has
        /// been reset.
        /// </summary>
        /// <remarks>
        /// A reset destroys every <see cref="VertexBuffer"/> the previous device owned, and
        /// <see cref="Chunk"/>'s are no exception - so without this the pass holds disposed buffers
        /// and submits nothing. The models are plain arrays on the CPU and survive, so the rebuild
        /// is the same bounded walk <see cref="Build"/> is, and the count comes back identical.
        /// </remarks>
        /// <returns>How many trees the rebuild placed.</returns>
        public int Rebuild()
        {
            foreach (var chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
            TreeCount = 0;
            if (_modelVerts == null || _map == null) return 0;
            Build();
            return TreeCount;
        }

        /// <summary>
        /// Draws every chunk whose bounds are inside <paramref name="frustum"/>, without a view
        /// matrix of its own.
        /// </summary>
        /// <remarks>
        /// Kept so a caller that has not been rewired yet still compiles and still draws: it
        /// hands <c>MV</c> the view-projection, which is the fog bug the other overload exists to
        /// fix. New callers pass the view.
        /// </remarks>
        /// <param name="vertexShader">The city's <c>VerShader</c>, on <c>RenderCityObj</c>.</param>
        /// <param name="pixelShader">The city's <c>PixShader</c>, on <c>RenderCityObj</c>.</param>
        /// <param name="viewProjection">The combined camera matrix.</param>
        /// <param name="content">The tree atlas.</param>
        /// <param name="lightColor">The light's colour and strength.</param>
        /// <param name="fogColor">The colour the far edge fades into.</param>
        /// <param name="fogDistance">How far away the fade completes, in tiles.</param>
        /// <param name="frustum">What is on screen.</param>
        public void Draw(
            Effect vertexShader, Effect pixelShader, Matrix viewProjection, NeighbourhoodContent content,
            Vector4 lightColor, Vector4 fogColor, float fogDistance, BoundingFrustum frustum)
        {
            Draw(vertexShader, pixelShader, viewProjection, viewProjection, content,
                lightColor, fogColor, fogDistance, frustum);
        }

        /// <summary>
        /// Draws every chunk whose bounds are inside <paramref name="frustum"/>, given the view
        /// matrix on its own.
        /// </summary>
        /// <remarks>
        /// <b>Why <c>MV</c> is the view matrix and not the view-projection.</b>
        /// <c>TreeVS</c> fills <c>vPos</c> with <c>mul(worldPos, MV)</c>, and <c>PixShader</c>'s
        /// fog pass fades by <c>min(1, length(vPos) / FogMaxDist)</c>. That is a distance from
        /// the camera only when <c>MV</c> is the <em>view</em> matrix: handed the
        /// view-projection instead, <c>length(vPos)</c> is a length in clip space, which runs to
        /// the far plane for every vertex on the map, so <c>fogDistance</c> clamps to 1 for the
        /// whole neighbourhood and the trees draw as a flat sheet of <c>FogColor</c>.
        /// <c>NeighbourhoodTerrainRenderer.BeginCity</c> sets the same uniform to the view for the
        /// same reason, and the two passes have to agree or the ground fades and the trees do not.
        /// </remarks>
        /// <param name="vertexShader">The city's <c>VerShader</c>, on <c>RenderCityObj</c>.</param>
        /// <param name="pixelShader">The city's <c>PixShader</c>, on <c>RenderCityObj</c>.</param>
        /// <param name="viewProjection">The combined camera matrix, for <c>BaseMatrix</c>.</param>
        /// <param name="view">World to view space, for <c>MV</c> and the fog distance.</param>
        /// <param name="content">The tree atlas.</param>
        /// <param name="lightColor">The light's colour and strength.</param>
        /// <param name="fogColor">The colour the far edge fades into.</param>
        /// <param name="fogDistance">How far away the fade completes, in tiles.</param>
        /// <param name="frustum">What is on screen.</param>
        public void Draw(
            Effect vertexShader, Effect pixelShader, Matrix viewProjection, Matrix view,
            NeighbourhoodContent content, Vector4 lightColor, Vector4 fogColor, float fogDistance,
            BoundingFrustum frustum)
        {
            //Reset first, so a frame that returns early reports zero rather than the previous
            //frame's number.
            _drawnPrimitives = 0;
            _drawnChunks = 0;
            _drawRan = false;

            if (!HasTrees || content?.TreeAtlas == null) return;
            if (vertexShader == null || pixelShader == null) return;

            var vs = vertexShader.Techniques[NeighbourhoodShaderState.CityObjectTechnique];
            var ps = pixelShader.Techniques[NeighbourhoodShaderState.CityObjectTechnique];
            if (vs == null || ps == null) return;

            //RenderCityObj's TreeVS vertex pass treats the vertex's normal slot as a per-vertex
            //world offset, which is how one buffer holds a whole wood. The pixel pass is the fog
            //one, so distant trees fade into the same haze the ground does.
            const int pixelPass = NeighbourhoodShaderState.FogPass;
            var treeVertexPass = NeighbourhoodShaderState.CityObjectOffsetVertexPass;
            if (pixelPass >= ps.Passes.Count || treeVertexPass >= vs.Passes.Count) return;

            vertexShader.CurrentTechnique = vs;
            pixelShader.CurrentTechnique = ps;

            vertexShader.Parameters["BaseMatrix"].SetValue(viewProjection);
            //View space, not clip space - see the remarks above. This one uniform is the
            //difference between a wood that fades into the distance and a wood that is entirely
            //fog.
            vertexShader.Parameters["MV"].SetValue(view);
            vertexShader.Parameters["ObjModel"].SetValue(Matrix.Identity);
            vertexShader.Parameters["HeightVScale"].SetValue(1f);
            //The city's own bias, CityFoliage.cs:133: -0.12f * DepthBiasScale, and the 3D camera's
            //DepthBiasScale is 1. A tree's own trunk is alpha-blended geometry standing on a tile
            //of ground, and a bias too small lets that ground z-fight its own canopy.
            vertexShader.Parameters["DepthBias"].SetValue(TreeDepthBias);

            pixelShader.Parameters["ObjTex"].SetValue(content.TreeAtlas);
            pixelShader.Parameters["LightCol"].SetValue(lightColor);
            pixelShader.Parameters["ShadowMult"].SetValue(1f);
            pixelShader.Parameters["FogColor"].SetValue(fogColor);
            pixelShader.Parameters["FogMaxDist"].SetValue(fogDistance > 1f ? fogDistance : 1f);

            var previousBlend = _device.BlendState;
            var previousRast = _device.RasterizerState;
            var previousDepth = _device.DepthStencilState;
            //The sampler too, not just the blend/raster/depth: the tree atlas is left on sampler 0
            //with a clamp wrap, and the next draw that assumes the wrap it found there gets a
            //clamped one. See NeighbourhoodCityDrawTests for the same argument about the others.
            var previousSampler = _device.SamplerStates[0];
            try
            {
                _device.BlendState = BlendState.NonPremultiplied;
                _device.RasterizerState = RasterizerState.CullNone;
                _device.DepthStencilState = DepthStencilState.DepthRead;
                _device.SamplerStates[0] = SamplerState.LinearClamp;

                //Apply, before the loop, and once is enough here - which is worth saying because the
                //terrain and the facades both re-apply per surface. The difference is what changes:
                //every chunk is drawn with the same tree atlas on the same sampler, and no parameter
                //is written inside the loop at all, so there is nothing for a later submission to
                //miss. The apply is still not optional: Effect.CurrentTechnique and the parameters
                //reach nothing without it, so a draw that skipped it would be submitted against
                //whatever vertex shader the previous draw left bound - and this vertex is
                //DGRP3DVert, whose declaration wants a float3 in TEXCOORD1, which a sprite batch's
                //SpriteEffect or the terrain's RenderNCity vertex does not satisfy. The device
                //builds its input layout against the bound shader, so that is an E_INVALIDARG and
                //an exception, not a wrong colour.
                vs.Passes[treeVertexPass].Apply();
                ps.Passes[pixelPass].Apply();
                _drawRan = true;

                foreach (var pair in _chunks)
                {
                    var chunk = pair.Value;
                    if (chunk == null || chunk.Indices == null) continue;
                    //a null frustum is "no culling", the same reading the terrain's cull takes
                    if (frustum != null && !chunk.Bounds.Intersects(frustum)) continue;
                    if (chunk.Vertices == null) continue;
                    _device.SetVertexBuffer(chunk.Vertices);
                    _device.Indices = chunk.Indices;
                    int triangles = chunk.Indices.IndexCount / 3;
                    try
                    {
                        _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, triangles);
                    }
                    catch (Exception ex)
                    {
                        //Named once, then rethrown. See LogDrawFailureOnce below for why the
                        //message alone is not enough to act on.
                        LogDrawFailureOnce(ex);
                        throw;
                    }
                    _drawnPrimitives += triangles;
                    _drawnChunks++;
                }
            }
            finally
            {
                _device.BlendState = previousBlend;
                _device.RasterizerState = previousRast;
                _device.DepthStencilState = previousDepth;
                _device.SamplerStates[0] = previousSampler;
            }
        }

        private int _drawnPrimitives;
        private int _drawnChunks;
        private bool _drawRan;
        private bool _drawFailureLogged;

        /// <summary>
        /// Triangles the last <see cref="Draw(Effect, Effect, Matrix, Matrix, NeighbourhoodContent, Vector4, Vector4, float, BoundingFrustum)"/>
        /// submitted across every visible chunk.
        /// </summary>
        /// <remarks>
        /// Reset at the top of the draw so a frame that returns early reports zero rather than the
        /// previous frame's number, for the same reason the terrain's counters are.
        /// </remarks>
        public int DrawnPrimitives
        {
            get { return _drawnPrimitives; }
        }

        /// <summary>How many tree chunks the last draw submitted.</summary>
        public int DrawnChunks
        {
            get { return _drawnChunks; }
        }

        /// <summary>Whether the last draw got as far as submitting anything.</summary>
        public bool DrawRan
        {
            get { return _drawRan; }
        }

        /// <summary>
        /// Explains a failed tree draw once, naming the shader it is on and the declaration it is
        /// validated against.
        /// </summary>
        private void LogDrawFailureOnce(Exception ex)
        {
            if (_drawFailureLogged) return;
            _drawFailureLogged = true;

            SimitoneLog.Error("The generated neighbourhood's trees threw while submitting: "
                + ex.Message
                + " The device builds its input layout from DGRP3DVert's declaration ("
                + NeighbourhoodFacadeVertex.Describe(DGRP3DVert.VertexDeclaration)
                + ", " + NeighbourhoodFacadeVertex.SizeInBytes
                + " bytes) against whichever vertex shader is bound, and only EffectPass.Apply binds"
                + " one. This draw is on Effects/VerShader's '"
                + NeighbourhoodShaderState.CityObjectTechnique + "' pass "
                + NeighbourhoodShaderState.CityObjectOffsetVertexPass
                + " (TreeVS), which reads the normal slot as a per-vertex world offset - the same"
                + " three elements RenderCityObj's input struct declares. A pass that was not"
                + " applied, or applied under another technique, leaves a shader bound that this"
                + " declaration does not satisfy, and D3D11 answers that with the same"
                + " 'vertex declaration does not include all the elements' error the facades and the"
                + " terrain were throwing.");
        }

        public void Dispose()
        {
            foreach (var chunk in _chunks.Values) chunk.Dispose();
            _chunks.Clear();
            _modelVerts = null;
            _modelIndices = null;
        }
    }

    /// <summary>
    /// Where a generated map's trees go, and how many each place gets.
    ///
    /// <para><b>Why this is a separate, pure type.</b> It is the decision half of the foliage
    /// pass: which of the 1024 chunks can hold a tree at all, and how the budget is divided
    /// between them. Both answers are functions of the map and the budget alone - no device, no
    /// obj file, no upload - so both can be asserted in a test, which is the only way this kind of
    /// placement bug is ever going to be caught. The bug in question is not hypothetical: the
    /// budget used to be spent first-come in map order, and a dense generated forest burns four
    /// thousand trees inside the first two rows of the map, which are the narrow top of the
    /// isometric diamond and not on screen at any zoom.
    /// </para>
    ///
    /// <para><b>Even shares, and the remainder to the low chunk indices.</b> Dividing the budget
    /// by the number of eligible chunks puts the same number of trees in a chunk on the far side
    /// of the map as in one beside the camera, which is what makes the wood read as a wood rather
    /// than as a smear along one edge. The leftover goes to the first few chunks in row-major
    /// order, which is arbitrary but deterministic - and determinism is the property the whole
    /// generated world rests on, so "arbitrary but fixed" is the right answer and "let the OS
    /// decide" is not.
    /// </para>
    ///
    /// <para><b>Shares are caps, not quotas.</b> A chunk that cannot fill its share does not
    /// hand the remainder on; the budget is a budget. In practice every eligible chunk on a
    /// generated map can hold far more than its share, and a chunk that could not would be one
    /// with a handful of tiles of forest in it.
    /// </para>
    ///
    /// <para><b>Eligibility is the drawn region, exactly.</b> The list is built by asking
    /// <see cref="NeighbourhoodFoliage.IsTreeable"/> about every tile on the map, which is the same
    /// question the chunk build asks, so a chunk is eligible if and only if it can hold a tree that
    /// will actually be drawn - on ground the ground mesh emits a quad for, inside the isometric
    /// diamond, and off the roads. The count that comes out of the same walk,
    /// <see cref="TilesOutsideClip"/>, is the whole number rather than a lower bound, because the
    /// walk no longer stops at the first treeable tile in each chunk.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodFoliagePlan
    {
        /// <summary>The budget the plan divides up.</summary>
        public int Budget { get; }

        /// <summary>The chunk indices that can hold at least one tree, ascending.</summary>
        public int[] Eligible { get; }

        /// <summary>How many chunks are eligible.</summary>
        public int EligibleChunks
        {
            get { return Eligible == null ? 0 : Eligible.Length; }
        }

        /// <summary>The even share, i.e. <c>Budget / EligibleChunks</c>, or 0 when nothing is.</summary>
        public int SharePerChunk
        {
            get { return EligibleChunks == 0 ? 0 : Budget / EligibleChunks; }
        }

        /// <summary>
        /// How many chunks get one more tree than the even share, i.e. <c>Budget %
        /// EligibleChunks</c>.
        /// </summary>
        public int RemainderChunks
        {
            get { return EligibleChunks == 0 ? 0 : Budget % EligibleChunks; }
        }

        /// <summary>
        /// Every forested, road-free tile on the map that falls outside the isometric diamond - the
        /// whole number, over the whole map, not a sample of it.
        /// </summary>
        /// <remarks>
        /// Counted rather than estimated, because "the budget went somewhere invisible" is only
        /// actionable if the log says how much. On a 512x512 square map the diamond is about half
        /// the area, so on a dense generated forest this runs to six figures: which is the point of
        /// logging it, and the reason the budget has to be spread over the chunks that are inside
        /// rather than spent in map order.
        /// </remarks>
        public int TilesOutsideClip { get; private set; }

        private NeighbourhoodFoliagePlan(int budget, int[] eligible, int tilesOutsideClip)
        {
            Budget = budget;
            Eligible = eligible ?? new int[0];
            TilesOutsideClip = tilesOutsideClip;
        }

        /// <summary>
        /// How many trees a chunk is allowed. Zero for a chunk that is not eligible.
        /// </summary>
        /// <param name="chunk">The chunk index.</param>
        public int TreesFor(int chunk)
        {
            var index = IndexOf(chunk);
            if (index < 0) return 0;
            return SharePerChunk + (index < RemainderChunks ? 1 : 0);
        }

        /// <summary>Whether a chunk is on the list.</summary>
        /// <param name="chunk">The chunk index.</param>
        public bool IsEligible(int chunk)
        {
            return IndexOf(chunk) >= 0;
        }

        private int IndexOf(int chunk)
        {
            if (Eligible == null || Eligible.Length == 0) return -1;
            //binary search: the list is built ascending, which is also the order the walk visits
            var low = 0;
            var high = Eligible.Length - 1;
            while (low <= high)
            {
                var mid = low + ((high - low) / 2);
                if (Eligible[mid] == chunk) return mid;
                if (Eligible[mid] < chunk) low = mid + 1;
                else high = mid - 1;
            }
            return -1;
        }

        /// <summary>
        /// The plan for a map and a budget: which chunks can hold a tree, and how the budget
        /// divides between them.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>One pass over the tiles, and one predicate.</b> The walk asks
        /// <see cref="NeighbourhoodFoliage.IsTreeable"/> - the same question the chunk build asks -
        /// so the eligible list cannot disagree with the tiles that actually get trees. It used to
        /// walk chunk by chunk and stop at each chunk's first treeable tile, which was fast and
        /// meant two things went wrong: the count of tiles outside the clip only ever reached the
        /// tiles examined before a chunk's first treeable one, so it under-counted by an unknown
        /// amount (the live log's "100659 forested tiles outside the clip" was a lower bound on a
        /// number the log presented as exact), and nothing here knew about a tile the ground mesh
        /// emits no quad for.
        /// </para>
        /// <para>
        /// A quarter of a million iterations is a few milliseconds once, on a pass that already
        /// builds every tree's vertices, and it buys a count that is the whole number and an
        /// eligibility rule that is the drawn region rather than an approximation of it.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map, or <c>null</c> for an empty plan.</param>
        /// <param name="budget">How many trees the whole map may have.</param>
        public static NeighbourhoodFoliagePlan For(NeighbourhoodMap map, int budget)
        {
            if (budget < 0) budget = 0;
            if (map == null || map.Width != NeighbourhoodFoliage.MapSize || map.Height != NeighbourhoodFoliage.MapSize)
            {
                return new NeighbourhoodFoliagePlan(budget, null, 0);
            }

            var seen = new bool[NeighbourhoodFoliage.ChunksPerAxis * NeighbourhoodFoliage.ChunksPerAxis];
            var eligible = new List<int>();
            var outside = 0;
            for (int oy = 0; oy < NeighbourhoodFoliage.MapSize; oy++)
            {
                for (int ox = 0; ox < NeighbourhoodFoliage.MapSize; ox++)
                {
                    var index = oy * map.Width + ox;
                    //Counted before the eligibility question, not as a side effect of it: this is
                    //the whole number of forested, road-free tiles the clip discards, and it is
                    //what says whether the clip is worth anything at all. Only two of the four
                    //conditions are asked here - forest and no road - because a tile with neither is
                    //not one the clip could have kept.
                    if (map.ForestDensityData[index] != 0
                        && map.RoadMaskAt(index) == RoadMask.None
                        && !NeighbourhoodCityMeshBuilder.IsInsideDiamond(ox, oy))
                    {
                        outside++;
                    }

                    //and then the one predicate, which is the chunk build's
                    if (!NeighbourhoodFoliage.IsTreeable(map, ox, oy)) continue;

                    var chunk = (ox / NeighbourhoodFoliage.ChunkSize)
                        + (oy / NeighbourhoodFoliage.ChunkSize) * NeighbourhoodFoliage.ChunksPerAxis;
                    if (seen[chunk]) continue;
                    seen[chunk] = true;
                    eligible.Add(chunk);
                }
            }
            //Sorted, and the walk does not produce it. The walk is row-major over tiles, which is ascending in
            //chunk index until the diamond's left edge starts retreating: row 11 of a 512 map reaches
            //chunks 18 and 19 and row 20 reaches chunk 17, so 17 is added after 18. Every lookup on
            //this list is a binary search over it, so an unsorted list silently loses trees -
            //TreesFor answers 0 for a chunk that is on the list - and the count of eligible chunks
            //stays right the whole time, which is the worst way for it to fail.
            eligible.Sort();
            return new NeighbourhoodFoliagePlan(budget, eligible.ToArray(), outside);
        }

        public override string ToString()
        {
            return Budget + " trees over " + EligibleChunks + " eligible chunks, "
                + SharePerChunk + " each (+" + RemainderChunks + " chunks get one more), "
                + TilesOutsideClip + " forested tiles outside the clip";
        }
    }

    /// <summary>
    /// Which of the shipped tree models a tile's foliage tint should be drawn with, and which of
    /// the shipped files each model index is.
    ///
    /// <para>
    /// The generated map records a colour per foliage tile, chosen by M2's terrain fill from a
    /// five-entry seeded palette so a wood reads as one species. TSO's own city renderer reads the
    /// same field as a four-way key into its own fixed dictionary of specific colours - which our
    /// palette is deliberately not - so the species has to be derived rather than looked up. It is
    /// derived here, from the tint itself, in the obvious way: how green, how yellow and how pale
    /// the tint is picks the model. A pine is dark green, a broadleaf tree is a brighter green, a
    /// palm is yellow, a cactus is pale, and snow-covered ground draws the snow model whatever the
    /// tint is.
    /// </para>
    ///
    /// <para><b>The index layout is a table of the files, not arithmetic.</b> This is the whole
    /// point of the type and it is worth saying why. The shipped <c>Content/3D</c> folder - which
    /// is <c>tso.content</c>'s, and which the game copies into its own output - contains
    /// <b>eighteen</b> tree models and no more:
    /// <c>pine1..4</c>, <c>tree1..4</c>, <c>palm1..4</c>, <c>cactus1..3</c>, <c>snow1..3</c>.
    /// TSO's <c>CityFoliage</c> loads exactly those eighteen, and it says so in the same way: a
    /// loop over eighteen, with <c>snow</c> from index 15. The pass here used to assume four
    /// variants of each of four species plus three of snow - nineteen - and derive the file name
    /// as <c>FileNames[index / 4] + (index % 4) + 1</c>. The sixteenth species model is therefore
    /// <c>cactus4.obj</c>, which does not ship, and every load ended in
    /// <c>"could not read the tree model ...\cactus4.obj: Could not find file"</c> - one model of
    /// nineteen missing, the warning repeated every time the foliage was rebuilt, and a desert tint
    /// silently drawing nothing on the fourth tree in a row. <see cref="ModelFiles"/> is now the
    /// list of the eighteen real names, so an index cannot name a file that was never shipped, and
    /// <c>NeighbourhoodFoliageModelsAllExistOnDisk</c> checks the list against the folder.
    /// </para>
    ///
    /// <para>
    /// Pure and total: any colour maps to a species and any index maps to a file, so a palette
    /// change in the generator cannot turn into a <c>KeyNotFoundException</c> and a variant out
    /// of range cannot turn into a model index outside the table.
    /// </para>
    /// </summary>
    public static class NeighbourhoodFoliageTint
    {
        /// <summary>The four species a tint can call for.</summary>
        public enum Species
        {
            /// <summary><c>pine1..4</c> - dark, coniferous.</summary>
            Pine = 0,

            /// <summary><c>tree1..4</c> - broadleaf.</summary>
            Broadleaf = 1,

            /// <summary><c>palm1..4</c> - yellow-green.</summary>
            Palm = 2,

            /// <summary><c>cactus1..3</c> - pale, desert.</summary>
            Cactus = 3
        }

        /// <summary>How many species a tint can resolve to.</summary>
        public const int SpeciesCount = 4;

        /// <summary>File stem of each species, in <see cref="Species"/> order.</summary>
        public static readonly string[] FileNames = { "pine", "tree", "palm", "cactus" };

        /// <summary>
        /// The first model index of each species, in <see cref="Species"/> order. Pine, tree and
        /// palm have four variants each and cactus has three, so the last block is short and every
        /// base is still a multiple of four.
        /// </summary>
        public static readonly int[] ModelBases = { 0, 4, 8, 12 };

        /// <summary>
        /// How many variants of each species ship, in <see cref="Species"/> order. Cactus is the
        /// odd one out at three; this is the number the old uniform <c>VariantsPerSpecies</c> got
        /// wrong, and it is why <c>cactus4.obj</c> was being asked for.
        /// </summary>
        public static readonly int[] VariantCounts = { 4, 4, 4, 3 };

        /// <summary>
        /// The eighteen tree models the shipped <c>Content/3D</c> folder provides, by model index,
        /// in <c>CityFoliage</c>'s order. Index <c>i</c> is <c>FileNameFor(i)</c>.
        /// </summary>
        public static readonly string[] ModelFiles =
        {
            "pine1.obj", "pine2.obj", "pine3.obj", "pine4.obj",
            "tree1.obj", "tree2.obj", "tree3.obj", "tree4.obj",
            "palm1.obj", "palm2.obj", "palm3.obj", "palm4.obj",
            "cactus1.obj", "cactus2.obj", "cactus3.obj",
            "snow1.obj", "snow2.obj", "snow3.obj"
        };

        /// <summary>How many models exist in total: the eighteen files above.</summary>
        public const int ModelCount = 18;

        /// <summary>
        /// The first of the three snow model indices, i.e. the model after the last cactus. The
        /// snow files sit outside the species blocks because they are chosen by ground type rather
        /// than by tint.
        /// </summary>
        public const int SnowModelBase = 15;

        /// <summary>How many snow models ship.</summary>
        public const int SnowVariantCount = ModelCount - SnowModelBase;

        /// <summary>
        /// The species a tint calls for. Total, so any colour resolves.
        /// </summary>
        /// <param name="tint">The tile's foliage tint.</param>
        public static Species SpeciesFor(Color tint)
        {
            if (tint.R > tint.G) return Species.Palm;
            if (tint.G < tint.R + 8 || tint.B >= 100) return Species.Pine;
            if (tint.G > tint.B + 20) return Species.Broadleaf;
            return Species.Cactus;
        }

        /// <summary>
        /// The file in <c>Content/3D</c> that a model index names, or <c>null</c> when the index
        /// is outside the table.
        /// </summary>
        /// <remarks>
        /// A lookup, not arithmetic, and the difference is the bug this type was rewritten for:
        /// the arithmetic could produce a fourth cactus, and no arithmetic can produce a name that
        /// is not in <see cref="ModelFiles"/>.
        /// </remarks>
        /// <param name="modelIndex">A model index, 0 to <c>ModelCount - 1</c>.</param>
        public static string FileNameFor(int modelIndex)
        {
            if (modelIndex < 0 || modelIndex >= ModelCount) return null;
            return ModelFiles[modelIndex];
        }

        /// <summary>How many variants of a species ship.</summary>
        /// <param name="species">The species.</param>
        public static int VariantCount(Species species)
        {
            return VariantCounts[Wrap((int)species, SpeciesCount)];
        }

        /// <summary>
        /// Which of the eighteen shipped tree models a tile draws, and which variant within it.
        /// </summary>
        /// <remarks>
        /// Snow is <see cref="SnowModelBase"/> plus the variant, because the three snow files sit
        /// after the species blocks rather than inside one. Every other model is its species'
        /// <see cref="ModelBases"/> entry plus the variant, wrapped to that species'
        /// <see cref="VariantCounts"/> entry - so a cactus's fourth roll asks for
        /// <c>cactus3.obj</c>, which exists, instead of <c>cactus4.obj</c>, which does not.
        /// </remarks>
        /// <param name="tint">The tile's foliage tint.</param>
        /// <param name="terrainType">The tile's ground type.</param>
        /// <param name="variant">Which variant; wrapped into range, negatives included.</param>
        public static int ModelFor(Color tint, byte terrainType, int variant)
        {
            if (terrainType == NeighbourhoodTerrain.Snow)
            {
                return SnowModelBase + Wrap(variant, SnowVariantCount);
            }
            var species = (int)SpeciesFor(tint);
            return ModelBases[species] + Wrap(variant, VariantCounts[species]);
        }

        /// <summary>
        /// A number into <c>0..count - 1</c>, negatives included.
        /// </summary>
        /// <remarks>
        /// <c>%</c> on its own leaves a negative variant negative, and a negative model index is
        /// an index into nothing: the tree pass skips it, so the tile silently loses its tree
        /// rather than drawing the first variant.
        /// </remarks>
        private static int Wrap(int value, int count)
        {
            if (count <= 0) return 0;
            var wrapped = value % count;
            return wrapped < 0 ? wrapped + count : wrapped;
        }
    }

    /// <summary>One 16x16 tile chunk of trees.</summary>
    public sealed class NeighbourhoodFoliageChunk : IDisposable
    {
        /// <summary>Index of the chunk, row-major over the 32x32 chunk grid.</summary>
        public int Ind;

        /// <summary>How many trees are in it.</summary>
        public int TreeCount;

        /// <summary>The trees' vertex buffer.</summary>
        public VertexBuffer Vertices;

        /// <summary>The trees' index buffer.</summary>
        public IndexBuffer Indices;

        /// <summary>What the chunk occupies, for the frustum test.</summary>
        public BoundingBox Bounds;

        public void Dispose()
        {
            Vertices?.Dispose();
            Indices?.Dispose();
            Vertices = null;
            Indices = null;
        }
    }
}
