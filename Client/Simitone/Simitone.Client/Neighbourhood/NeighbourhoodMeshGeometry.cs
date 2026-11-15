/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Threading.Tasks;
using FSO.Common.Utils;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The neighbourhood renderer's geometry backend: the bespoke
    /// <see cref="NeighbourhoodCityMeshBuilder"/> mesh, built and uploaded, in the shape
    /// <see cref="NeighbourhoodTerrainRenderer"/> draws.
    ///
    /// <para>
    /// <b>Why this exists.</b> The connected-mechanics rework replaced the neighbourhood's own mesh
    /// with the lifted TSO <c>CityGeometry</c> (docs/CONNECTED_MECHANICS_REWORK_PLAN.md §2.1/§2.2).
    /// The revert restores the bespoke path: this class builds a <see cref="NeighbourhoodCityMesh"/>
    /// - whole map at a level of detail, or one subdivided slice - and uploads it to the buffers the
    /// renderer draws from. It exposes the same per-chunk index bookkeeping the renderer's cull and
    /// range coalescer read, so the draw code is unchanged in shape and the mesh is entirely
    /// Simitone's.
    /// </para>
    ///
    /// <para>
    /// <b>Threading.</b> The mesh build is CPU work and runs on a worker when asked
    /// (<see cref="Build(GraphicsDevice, bool)"/>); the upload touches the graphics device and lands
    /// on the game thread through <see cref="GameThread.NextUpdate"/>, which is where
    /// <see cref="Ready"/> is set. A synchronous build (<c>async: false</c>) does both inline, for a
    /// caller that wants the mesh now.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodMeshGeometry : IDisposable
    {
        /// <summary>Per-layer index buffers, indexed by <see cref="NeighbourhoodTerrain"/> value.</summary>
        public IndexBuffer[] LayerIndices = new IndexBuffer[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>Per-layer vertex buffers, indexed by <see cref="NeighbourhoodTerrain"/> value.</summary>
        public VertexBuffer[] LayerVertices = new VertexBuffer[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>
        /// Per-layer per-chunk index <em>end</em> counts (the shape <c>CityGeometry.LayerSubPrims</c>
        /// has), so <see cref="NeighbourhoodTerrainRenderer"/>'s range coalescer reads them
        /// unchanged.
        /// </summary>
        public int[][] LayerSubPrims = new int[NeighbourhoodCityMeshBuilder.LayerCount][];

        /// <summary>Per-layer triangle counts.</summary>
        public int[] LayerPrims = new int[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>The road index buffer, or <c>null</c>.</summary>
        public IndexBuffer RoadIndices;

        /// <summary>The road vertex buffer, or <c>null</c>.</summary>
        public VertexBuffer RoadVertices;

        /// <summary>Road triangle count.</summary>
        public int RoadPrims;

        /// <summary>The road per-chunk index end counts.</summary>
        public int[] RoadSubPrims;

        /// <summary>
        /// The slice index the buffers hold, or <c>-1</c>. The renderer reads this to know whether a
        /// requested slice has landed.
        /// </summary>
        public int Ready = -1;

        /// <summary>The slice index last requested, or <c>-1</c>.</summary>
        public int CurrentSlice = -1;

        /// <summary>Tiles per ground quad edge this geometry was built at.</summary>
        public int LodFactor = NeighbourhoodMeshLod.Full;

        private NeighbourhoodMap _map;
        private bool _regenInProgress;
        private bool _dirty;
        private bool _disposed;
        private int _sliceSerial;

        /// <summary>Why the last whole-map build failed, or <c>null</c>.</summary>
        public string Failure { get; private set; }

        /// <summary>The slice whose last build failed, or <c>-1</c>; lets the renderer stop waiting for it.</summary>
        public int FailedSlice { get; private set; } = -1;

        /// <summary>The map this geometry meshes.</summary>
        public NeighbourhoodMap Map
        {
            get { return _map; }
            set { _map = value; }
        }

        /// <summary>Whether any buffer has been uploaded.</summary>
        public bool HasBuffers
        {
            get
            {
                for (int i = 0; i < LayerVertices.Length; i++)
                {
                    if (LayerVertices[i] != null) return true;
                }
                return RoadVertices != null;
            }
        }

        /// <summary>
        /// Builds the whole map at <see cref="LodFactor"/> and uploads it. On a worker when
        /// <paramref name="async"/> is true, with the upload on the game thread; inline otherwise.
        /// </summary>
        /// <param name="gd">The graphics device.</param>
        /// <param name="async">Whether to build off the game thread.</param>
        public void Build(GraphicsDevice gd, bool async)
        {
            if (_disposed || _map == null) return;
            var map = _map;
            var factor = LodFactor;
            NeighbourhoodCityMesh built = null;
            string failure = null;

            Action generate = () =>
            {
                try
                {
                    NeighbourhoodCityMeshReport report;
                    built = NeighbourhoodCityMeshBuilder.Build(map, factor, out report);
                }
                catch (Exception ex) { failure = ex.Message; }
            };

            Action upload = () =>
            {
                if (_disposed) return;
                if (built != null) UploadWholeMap(built, gd);
                else if (failure != null)
                {
                    Failure = failure;
                    SimitoneLog.Error("The generated neighbourhood's terrain mesh could not be built: " + failure);
                }
            };

            if (async)
            {
                if (_regenInProgress)
                {
                    _dirty = true;
                    return;
                }
                _dirty = false;
                _regenInProgress = true;
                Task.Run(generate).ContinueWith(x => GameThread.NextUpdate(state =>
                {
                    //a throwing upload (a lost device) must not leave the in-flight flag set, or no
                    //later build would ever start
                    try { upload(); }
                    finally { _regenInProgress = false; }
                    if (_dirty && !_disposed)
                    {
                        _dirty = false;
                        Build(gd, true);
                    }
                }));
            }
            else
            {
                generate();
                upload();
            }
        }

        /// <summary>
        /// Builds one subdivided slice and uploads it, always off the game thread with the upload on
        /// it. Sets <see cref="Ready"/> to the slice once the buffers land.
        /// </summary>
        /// <param name="gd">The graphics device.</param>
        /// <param name="range">The slice's tile rectangle, or <c>null</c> for the whole map.</param>
        /// <param name="subdiv">Ground quads per map tile edge.</param>
        /// <param name="cpos">The slice index, stamped onto <see cref="Ready"/> when it lands.</param>
        public void BuildSlice(GraphicsDevice gd, Rectangle? range, int subdiv, int cpos)
        {
            if (_disposed || _map == null) return;
            var map = _map;
            CurrentSlice = cpos;
            var serial = ++_sliceSerial;
            NeighbourhoodCityMesh built = null;
            string failure = null;

            Task.Run(() =>
            {
                try
                {
                    NeighbourhoodCityMeshReport report;
                    built = NeighbourhoodCityMeshBuilder.BuildSubdivSlice(map, cpos, subdiv, out report);
                }
                catch (Exception ex)
                {
                    built = null;
                    failure = ex.Message;
                }
            }).ContinueWith(x => GameThread.NextUpdate(state =>
            {
                //a newer request supersedes this one: landing it would overwrite the newer slice
                //with an older one when the worker finishes out of order
                if (_disposed || serial != _sliceSerial) return;
                if (built == null)
                {
                    FailedSlice = cpos;
                    SimitoneLog.Error("The generated neighbourhood's refined slice " + cpos
                        + " could not be built: " + failure);
                    return;
                }
                UploadSlice(built, gd);
                Ready = cpos;
            }));
        }

        private void UploadWholeMap(NeighbourhoodCityMesh mesh, GraphicsDevice gd)
        {
            for (int i = 0; i < LayerVertices.Length; i++)
            {
                LayerIndices[i]?.Dispose();
                LayerVertices[i]?.Dispose();
                LayerIndices[i] = null;
                LayerVertices[i] = null;
                LayerPrims[i] = 0;

                var vertices = mesh.Layers[i].Vertices;
                var indices = mesh.Layers[i].Indices;
                if (vertices != null && indices != null && indices.Length > 0)
                {
                    LayerVertices[i] = new VertexBuffer(gd, typeof(NeighbourhoodTerrainVertex),
                        vertices.Length, BufferUsage.None);
                    LayerVertices[i].SetData(vertices);
                    LayerIndices[i] = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits,
                        indices.Length, BufferUsage.None);
                    LayerIndices[i].SetData(indices);
                    LayerPrims[i] = indices.Length / 3;
                }
                LayerSubPrims[i] = EndCounts(mesh.LayerChunkIndexStarts[i]);
            }

            RoadIndices?.Dispose();
            RoadVertices?.Dispose();
            RoadIndices = null;
            RoadVertices = null;
            RoadPrims = 0;
            var roadVertices = mesh.Roads.Vertices;
            var roadIndices = mesh.Roads.Indices;
            if (roadVertices != null && roadIndices != null && roadIndices.Length > 0)
            {
                RoadVertices = new VertexBuffer(gd, typeof(NeighbourhoodTerrainVertex),
                    roadVertices.Length, BufferUsage.None);
                RoadVertices.SetData(roadVertices);
                RoadIndices = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits,
                    roadIndices.Length, BufferUsage.None);
                RoadIndices.SetData(roadIndices);
                RoadPrims = roadIndices.Length / 3;
            }
            RoadSubPrims = EndCounts(mesh.RoadChunkIndexStarts);
            Ready = 0;
        }

        private void UploadSlice(NeighbourhoodCityMesh mesh, GraphicsDevice gd)
        {
            for (int i = 0; i < LayerVertices.Length; i++)
            {
                LayerIndices[i]?.Dispose();
                LayerVertices[i]?.Dispose();
                LayerIndices[i] = null;
                LayerVertices[i] = null;
                LayerPrims[i] = 0;

                var vertices = mesh.Layers[i].Vertices;
                var indices = mesh.Layers[i].Indices;
                if (vertices != null && indices != null && indices.Length > 0)
                {
                    LayerVertices[i] = new VertexBuffer(gd, typeof(NeighbourhoodTerrainVertex),
                        vertices.Length, BufferUsage.None);
                    LayerVertices[i].SetData(vertices);
                    LayerIndices[i] = new IndexBuffer(gd, IndexElementSize.ThirtyTwoBits,
                        indices.Length, BufferUsage.None);
                    LayerIndices[i].SetData(indices);
                    LayerPrims[i] = indices.Length / 3;
                }
                LayerSubPrims[i] = EndCounts(mesh.LayerChunkIndexStarts[i]);
            }
            RoadIndices?.Dispose();
            RoadVertices?.Dispose();
            RoadIndices = null;
            RoadVertices = null;
            RoadPrims = 0;
            RoadSubPrims = EndCounts(mesh.RoadChunkIndexStarts);
        }

        /// <summary>
        /// Turns the builder's <c>starts[c]</c> sentinel array into the <c>ends[c]</c> array the
        /// renderer's <see cref="NeighbourhoodTerrainRenderer"/> range coalescer reads, by dropping
        /// the leading zero. The two describe the same runs.
        /// </summary>
        private static int[] EndCounts(int[] starts)
        {
            var ends = new int[NeighbourhoodCityMeshBuilder.ChunkCount];
            if (starts == null) return ends;
            for (int c = 0; c < ends.Length && c + 1 < starts.Length; c++) ends[c] = starts[c + 1];
            return ends;
        }

        public void Dispose()
        {
            _disposed = true;
            for (int i = 0; i < LayerVertices.Length; i++)
            {
                LayerIndices[i]?.Dispose();
                LayerVertices[i]?.Dispose();
                LayerIndices[i] = null;
                LayerVertices[i] = null;
                LayerPrims[i] = 0;
                LayerSubPrims[i] = null;
            }
            RoadIndices?.Dispose();
            RoadVertices?.Dispose();
            RoadIndices = null;
            RoadVertices = null;
            RoadPrims = 0;
            RoadSubPrims = null;
            Ready = -1;
            CurrentSlice = -1;
        }
    }
}
