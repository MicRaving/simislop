/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// Owns a generated neighbourhood's terrain mesh and draws it.
    ///
    /// <para>
    /// <b>The mesh and the draw are Simitone's own.</b> The renderer builds a
    /// <see cref="NeighbourhoodCityMesh"/> from the generated map through
    /// <see cref="NeighbourhoodCityMeshBuilder"/> (via <see cref="NeighbourhoodMeshGeometry"/>) and
    /// draws it with <see cref="NeighbourhoodContent"/>. The connected-mechanics rework had briefly
    /// replaced this with the lifted <c>FSO.LotView.City.CityGeometry</c>; the revert restores the
    /// bespoke path, so the two modes never share a mesh and the neighbourhood owns everything it
    /// draws.
    /// </para>
    ///
    /// <para>
    /// <b>The cull is the point.</b> A 512x512 map is a few hundred thousand triangles and a
    /// camera is usually looking at a fraction of it. The geometry keeps a running index count per
    /// 16x16 chunk in <c>LayerSubPrims</c>/<c>RoadSubPrims</c>; the draw coalesces the chunks the
    /// frustum touches into a handful of <c>DrawIndexedPrimitives</c> calls. At full screen every
    /// chunk is visible and this submits exactly as much as the whole-map draw did, so culling
    /// costs nothing when there is nothing to cull; focused on a lot it is the difference between
    /// the whole map a frame and a few thousand triangles.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodTerrainRenderer : IDisposable
    {
        private readonly GraphicsDevice _device;
        private readonly NeighbourhoodMap _map;
        private NeighbourhoodContent _content;

        /// <summary>
        /// The whole-map base geometry: the five ground layers and the roads at one quad per map
        /// tile, built by <see cref="NeighbourhoodCityMeshBuilder"/>.
        /// </summary>
        private NeighbourhoodMeshGeometry _geometry = new NeighbourhoodMeshGeometry();

        /// <summary>
        /// The close level's refined ground, built one 32x32-tile slice at a time by
        /// <see cref="NeighbourhoodCityMeshBuilder.BuildSubdivSlice"/> and drawn whole - the same
        /// two-instance arrangement TSO's own <c>Terrain</c> uses, but entirely on Simitone's mesh.
        /// </summary>
        private NeighbourhoodMeshGeometry _subdiv = new NeighbourhoodMeshGeometry();

        /// <summary>
        /// The base geometry's per-chunk index starts, converted from the lifted
        /// <c>LayerSubPrims</c> into the sentinel-included shape
        /// <see cref="NeighbourhoodCityMeshBuilder.VisibleDrawRanges"/> reads. Built once per mesh.
        /// </summary>
        private int[][] _layerStarts;
        private int[] _roadStarts;

        private volatile bool _buildRequested;
        private volatile bool _meshReady;
        private string _buildFailure;
        private NeighbourhoodMeshLod _lod = new NeighbourhoodMeshLod(NeighbourhoodMeshLod.Full, 1);
        private bool _subdividing;

        private readonly List<int> _visibleChunks = new List<int>(NeighbourhoodCityMeshBuilder.ChunkCount);
        private readonly List<int> _drawChunks = new List<int>(NeighbourhoodCityMeshBuilder.ChunkCount);
        private readonly List<NeighbourhoodMeshDrawRange> _drawRanges = new List<NeighbourhoodMeshDrawRange>(64);

        // ---- the close level's single refined slice ---------------------------------------------
        //
        // TSO keeps one SubdivGeometry and rebuilds it for the slice the camera is in, rather than
        // caching many. That is the whole of the close level: one 32x32-tile slice, built on a
        // worker, uploaded on the game thread, and mixed with the base for everything else.
        private int _subdivRequested = -1;
        private int _subdivSubdivisions = 1;
        private bool _subdivBusy;
        private int[] _subdivChunks;

        // ---- per-frame diagnostics -----------------------------------------------------------------
        private int _drawnPrimitives;
        private int _drawnIndices;
        private int _drawnRoadPrimitives;
        private int _drawnLayers;
        private int _drawnChunks;
        private int _drawnDrawCalls;
        private bool _drawRan;
        private Rectangle _visibleRegion;
        private bool _hasVisibleRegion;

        private bool _summaryLogged;
        private bool _nothingSubmittedLogged;
        private bool _roadAtlasWarned;
        private bool _shaderFailureLogged;
        private bool _drawFailureLogged;

        /// <summary>Whether the draw is refining its ground with the subdivided close level.</summary>
        public bool IsSubdividing
        {
            get { return _subdividing; }
        }

        /// <summary>The generated map this renderer draws.</summary>
        public NeighbourhoodMap Map
        {
            get { return _map; }
        }

        /// <summary>The textures being drawn with. Never <c>null</c>; may be placeholders.</summary>
        public NeighbourhoodContent Content
        {
            get { return _content; }
        }

        /// <summary>Whether a mesh has finished uploading at least once.</summary>
        public bool MeshReady
        {
            get { return _meshReady || AnyBuffers(); }
        }

        /// <summary>Whether a mesh build is in flight.</summary>
        public bool IsBuilding
        {
            get { return _buildRequested && !MeshReady; }
        }

        /// <summary>Why the mesh could not be built, or <c>null</c>.</summary>
        public string BuildFailure
        {
            get { return _buildFailure; }
        }

        /// <summary>Every triangle across the five layers and the roads.</summary>
        public int TotalPrimitives
        {
            get
            {
                int total = _geometry.RoadPrims;
                for (int i = 0; i < NeighbourhoodCityMeshBuilder.LayerCount; i++)
                {
                    total += _geometry.LayerPrims[i];
                }
                return total;
            }
        }

        /// <summary>How many triangles a terrain layer holds, or 0.</summary>
        /// <param name="terrainType">A <see cref="NeighbourhoodTerrain"/> value.</param>
        public int LayerPrimitives(int terrainType)
        {
            if (terrainType < 0 || terrainType >= NeighbourhoodCityMeshBuilder.LayerCount) return 0;
            return _geometry.LayerPrims[terrainType];
        }

        /// <summary>How many road triangles, including corner quads.</summary>
        public int RoadPrimitives
        {
            get { return _geometry.RoadPrims; }
        }

        /// <summary>Triangles the last <see cref="Draw(Effect, Effect, Matrix, Matrix, BoundingFrustum, Vector3, Vector4, Vector4, float)"/> submitted.</summary>
        public int DrawnPrimitives
        {
            get { return _drawnPrimitives; }
        }

        /// <summary>How many chunks the last draw touched.</summary>
        public int DrawnChunks
        {
            get { return _drawnChunks; }
        }

        /// <summary>How many draw calls the last draw issued.</summary>
        public int DrawnDrawCalls
        {
            get { return _drawnDrawCalls; }
        }

        /// <summary>Indices the last draw submitted.</summary>
        public int DrawnIndices
        {
            get { return _drawnIndices; }
        }

        /// <summary>Road triangles the last draw submitted.</summary>
        public int DrawnRoadPrimitives
        {
            get { return _drawnRoadPrimitives; }
        }

        /// <summary>How many of the five ground layers the last draw submitted anything for.</summary>
        public int DrawnLayers
        {
            get { return _drawnLayers; }
        }

        /// <summary>Whether the last draw got as far as the chunk cull at all.</summary>
        public bool DrawRan
        {
            get { return _drawRan; }
        }

        /// <summary>The map tiles the last draw found inside the frustum.</summary>
        public Rectangle VisibleRegion
        {
            get { return _visibleRegion; }
        }

        /// <summary>Whether <see cref="VisibleRegion"/> means anything yet.</summary>
        public bool HasVisibleRegion
        {
            get { return _hasVisibleRegion; }
        }

        /// <summary>The average ground height of the map, in world units.</summary>
        public float MeanGroundHeight
        {
            get { return NeighbourhoodCityMeshBuilder.MeanGroundHeight(_map); }
        }

        /// <summary>The level of detail the last draw reported.</summary>
        public NeighbourhoodMeshLod Lod
        {
            get { return _lod; }
        }

        /// <summary>
        /// Builds a renderer over a generated map, or returns <c>null</c> with the reason.
        /// </summary>
        /// <param name="device">The graphics device.</param>
        /// <param name="map">The generated map.</param>
        /// <param name="reason">Why the map could not be meshed, when it could not be.</param>
        public static NeighbourhoodTerrainRenderer TryCreate(
            GraphicsDevice device, NeighbourhoodMap map, out string reason)
        {
            if (device == null)
            {
                reason = "there is no graphics device";
                return null;
            }
            string why;
            if (!NeighbourhoodCityMeshBuilder.CanMesh(map, out why))
            {
                reason = why;
                return null;
            }
            try
            {
                var renderer = new NeighbourhoodTerrainRenderer(device, map);
                reason = null;
                return renderer;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return null;
            }
        }

        private NeighbourhoodTerrainRenderer(GraphicsDevice device, NeighbourhoodMap map)
        {
            _device = device;
            _map = map;
            _content = new NeighbourhoodContent(device);
        }

        /// <summary>
        /// Kicks the whole-map mesh build off on a worker thread. The upload lands on the game
        /// thread, inside <see cref="NeighbourhoodMeshGeometry.Build"/>'s own
        /// <c>GameThread.NextUpdate</c> callback.
        /// </summary>
        public void BeginMeshBuild()
        {
            if (_buildRequested || _meshReady) return;
            _buildRequested = true;
            _buildFailure = null;
            ResetGeometry();
            _geometry.Build(_device, true);
        }

        /// <summary>Builds and uploads the mesh on this thread, for a caller that wants it now.</summary>
        public void BuildMeshNow()
        {
            ResetGeometry();
            _geometry.Build(_device, false);
            PromoteReady();
        }

        /// <summary>
        /// Throws away the current geometry and makes a fresh pair over the same map. A fresh
        /// <see cref="NeighbourhoodMeshGeometry"/> has no buffers, which is what lets readiness be
        /// read off the buffers themselves: a rebuild must not be mistaken for the previous build's
        /// result.
        /// </summary>
        private void ResetGeometry()
        {
            _geometry.Dispose();
            _subdiv.Dispose();
            _geometry = new NeighbourhoodMeshGeometry { Map = _map, LodFactor = _lod.Factor };
            _subdiv = new NeighbourhoodMeshGeometry { Map = _map };
            _layerStarts = null;
            _roadStarts = null;
            _meshReady = false;
            _subdivRequested = -1;
            _subdivBusy = false;
            _subdivChunks = null;
        }

        /// <summary>Whether the geometry holds any uploaded buffer at all.</summary>
        private bool AnyBuffers()
        {
            for (int i = 0; i < NeighbourhoodCityMeshBuilder.LayerCount; i++)
            {
                if (_geometry.LayerVertices[i] != null) return true;
            }
            return _geometry.RoadVertices != null;
        }

        /// <summary>
        /// Marks the mesh ready once the worker's upload has landed and converts the lifted
        /// per-chunk counts into the sentinel arrays the range coalescer reads. Idempotent and
        /// cheap, so it can be called at the top of every draw.
        /// </summary>
        private void PromoteReady()
        {
            if (_meshReady) return;
            if (!AnyBuffers()) return;
            _meshReady = true;
            _buildRequested = false;
            EnsureStarts();
        }

        private void EnsureStarts()
        {
            if (_layerStarts != null) return;
            var chunkCount = NeighbourhoodCityMeshBuilder.ChunkCount;
            _layerStarts = new int[NeighbourhoodCityMeshBuilder.LayerCount][];
            for (int i = 0; i < _layerStarts.Length; i++)
            {
                var ends = _geometry.LayerSubPrims[i];
                var starts = new int[chunkCount + 1];
                if (ends != null)
                {
                    for (int c = 0; c < chunkCount; c++) starts[c + 1] = ends[c];
                }
                _layerStarts[i] = starts;
            }
            _roadStarts = new int[chunkCount + 1];
            var roadEnds = _geometry.RoadSubPrims;
            if (roadEnds != null)
            {
                for (int c = 0; c < chunkCount; c++) _roadStarts[c + 1] = roadEnds[c];
            }
        }

        /// <summary>
        /// Points the draw at <paramref name="lod"/>.
        /// </summary>
        /// <remarks>
        /// The bespoke mesh bakes the whole-map factor into its buffers, so a factor change is a
        /// rebuild rather than only a recorded number: a coarse level is a different mesh, not the
        /// same mesh drawn differently. A subdivision change asks for a refined slice instead, which
        /// is the close level's separate axis.
        /// </remarks>
        /// <param name="lod">The level the camera wants.</param>
        /// <returns>Whether the level changed.</returns>
        public bool SetLod(NeighbourhoodMeshLod lod)
        {
            var changed = !NeighbourhoodMeshLod.SameLevel(_lod, lod);
            _lod = lod;
            _subdividing = lod.IsSubdivided;
            if (changed && _geometry != null && _geometry.LodFactor != lod.Factor)
            {
                //The factor is a different mesh, so rebuild it. The old buffers are dropped in
                //ResetGeometry; the draw falls back to whatever has landed, which is nothing for a
                //frame and the new level after that.
                _meshReady = false;
                _buildRequested = false;
                _summaryLogged = false;
                _nothingSubmittedLogged = false;
                BeginMeshBuild();
            }
            return changed;
        }

        /// <summary>
        /// Tells the renderer which slice the camera wants refined, and starts that slice's build.
        /// Cheap enough to call every frame. One slice is in flight at a time, matching TSO's one
        /// <c>SubdivGeometry</c>.
        /// </summary>
        /// <param name="focusTiles">Where the camera is looking, in map tiles.</param>
        /// <param name="tileRegion">The tiles the camera can see.</param>
        public void UpdateSubdivSlices(Vector2 focusTiles, Rectangle tileRegion)
        {
            PromoteReady();
            if (!_subdividing || !_meshReady) return;

            var slice = NeighbourhoodCityMeshBuilder.SliceAt(
                (int)Math.Round(focusTiles.X), (int)Math.Round(focusTiles.Y));
            if (slice < 0) return;

            if (_subdivBusy)
            {
                if (_subdiv.Ready == _subdivRequested) _subdivBusy = false;
                else return;
            }

            if (slice == _subdivRequested && _subdivSubdivisions == _lod.Subdivisions) return;

            _subdivRequested = slice;
            _subdivSubdivisions = _lod.Subdivisions;
            _subdivChunks = NeighbourhoodCityMeshBuilder.ChunksInSlice(slice);
            _subdivBusy = true;
            _subdiv.BuildSlice(_device, NeighbourhoodCityMeshBuilder.SliceBounds(slice),
                _lod.Subdivisions, slice);
        }

        /// <summary>Replaces the textures, e.g. after a device reset, and rebuilds the geometry.</summary>
        /// <remarks>
        /// A device reset invalidates the geometry's vertex and index buffers as well as the
        /// textures, so this rebuilds the mesh rather than only swapping content. The caller
        /// (<c>Neighbourhood3DView.DeviceReset</c>) passes a fresh content for the new device; the
        /// <c>GraphicsDevice</c> object itself is reused across a MonoGame reset.
        /// </remarks>
        /// <param name="content">The new content; the old one is disposed.</param>
        public void SetContent(NeighbourhoodContent content)
        {
            if (ReferenceEquals(content, _content)) return;
            _content?.Dispose();
            _content = content ?? new NeighbourhoodContent(null);

            _buildRequested = false;
            _meshReady = false;
            _summaryLogged = false;
            _nothingSubmittedLogged = false;
            BeginMeshBuild();
        }

        /// <summary>The road sampler: clamped, point-magnified, mip-filtered when minified.</summary>
        public static SamplerState RoadSampler
        {
            get { return _roadSampler ?? (_roadSampler = CreateRoadSampler()); }
        }

        private static SamplerState _roadSampler;

        private static SamplerState CreateRoadSampler()
        {
            return new SamplerState
            {
                AddressU = TextureAddressMode.Clamp,
                AddressV = TextureAddressMode.Clamp,
                Filter = TextureFilter.MinLinearMagPointMipLinear
            };
        }

        /// <summary>Frees the cached <see cref="RoadSampler"/>, for a device reset.</summary>
        public static void DisposeSamplerStates()
        {
            _roadSampler?.Dispose();
            _roadSampler = null;
        }

        /// <summary>
        /// Draws the terrain and the roads, submitting only the chunks the camera can see.
        /// </summary>
        public void Draw(
            Effect vertexShader, Effect pixelShader, Matrix viewProjection,
            Vector3 lightDirection, Vector4 lightColor, Vector4 fogColor, float fogDistance)
        {
            Draw(vertexShader, pixelShader, viewProjection,
                new BoundingFrustum(viewProjection),
                lightDirection, lightColor, fogColor, fogDistance);
        }

        /// <summary>Draws against a frustum the caller already has.</summary>
        public void Draw(
            Effect vertexShader, Effect pixelShader, Matrix viewProjection, BoundingFrustum frustum,
            Vector3 lightDirection, Vector4 lightColor, Vector4 fogColor, float fogDistance)
        {
            Draw(vertexShader, pixelShader, viewProjection, viewProjection, frustum,
                lightDirection, lightColor, fogColor, fogDistance);
        }

        /// <summary>
        /// Draws the terrain and the roads, with the view given separately from the
        /// view-projection so the fog measures a real distance from the camera.
        /// </summary>
        /// <param name="vertexShader">The city's <c>Effects/VerShader</c>.</param>
        /// <param name="pixelShader">The city's <c>Effects/PixShader</c>.</param>
        /// <param name="viewProjection">World to clip space, for the vertices and the cull.</param>
        /// <param name="view">World to view space, for the fog distance.</param>
        /// <param name="frustum">What is on screen, or <c>null</c> for no culling.</param>
        /// <param name="lightDirection">A unit vector pointing towards the light.</param>
        /// <param name="lightColor">The light's colour and strength.</param>
        /// <param name="fogColor">The colour the far edge of the map fades into.</param>
        /// <param name="fogDistance">How far away the fade completes, in tiles.</param>
        /// <param name="wholeMapWhenEmpty">
        /// Whether a frustum that selects no chunk at all should submit the whole map instead of
        /// nothing. False for the map view, whose camera can legitimately point away from the map
        /// (that is the cull doing its job). True for the in-lot surround, where "the frustum
        /// selected nothing" is never a reason to leave the lot in a void: the surround exists to
        /// draw the connected ground, and its camera is the lot's, which during a lot load can be
        /// off the map or between camera states.
        /// </param>
        public void Draw(
            Effect vertexShader, Effect pixelShader, Matrix viewProjection, Matrix view,
            BoundingFrustum frustum,
            Vector3 lightDirection, Vector4 lightColor, Vector4 fogColor, float fogDistance,
            bool wholeMapWhenEmpty = false)
        {
            //Reset first, so a frame that returns early reports zero rather than the previous
            //frame's numbers.
            _drawnPrimitives = 0;
            _drawnDrawCalls = 0;
            _drawnIndices = 0;
            _drawnLayers = 0;
            _drawnRoadPrimitives = 0;
            _drawRan = false;

            PromoteReady();
            if (!_meshReady || _content == null) return;
            var content = _content;
            if (_layerStarts == null) EnsureStarts();

            var vsPass = NeighbourhoodShaderState.BeginCity(
                vertexShader, pixelShader, viewProjection, view,
                lightDirection, lightColor, fogColor, fogDistance);
            if (vsPass == null)
            {
                if (!_shaderFailureLogged)
                {
                    _shaderFailureLogged = true;
                    SimitoneLog.Warn("The generated neighbourhood cannot draw at all: the city"
                        + " effects do not carry the '" + NeighbourhoodShaderState.CityTechnique
                        + "' technique this renderer needs, so every frame is skipped and the"
                        + " screen stays whatever the clear colour is. The painted map is the"
                        + " backdrop until that is fixed.");
                }
                return;
            }

            var previousBlend = _device.BlendState;
            var previousRaster = _device.RasterizerState;
            var previousDepth = _device.DepthStencilState;
            try
            {
                _device.BlendState = BlendState.AlphaBlend;
                _device.RasterizerState = RasterizerState.CullNone;
                _device.DepthStencilState = DepthStencilState.Default;
                _device.SamplerStates[0] = SamplerState.LinearWrap;
                _device.SamplerStates[1] = SamplerState.LinearClamp;

                CollectVisibleChunks(frustum, wholeMapWhenEmpty);
                var subdivActive = _subdividing && _subdiv.Ready >= 0
                    && _subdiv.Ready == _subdivRequested;
                BuildDrawChunks(subdivActive);
                if (wholeMapWhenEmpty && _drawChunks.Count == 0)
                {
                    //The refined slice took every chunk the frustum selected (or the frustum
                    //selected nothing), so the base geometry would submit no ground at all. The
                    //surround must never be a void: submit the whole base map instead.
                    SelectEverything();
                    BuildDrawChunks(false);
                }
                _drawRan = true;

                //The per-tile tint is on for the terrain and off for the roads, which is the uniform
                //the city's own draw sets. A generated map has no tint artwork, so the bound
                //VertexColor is the white stand-in and this is the identity tint.
                pixelShader.Parameters["VertexColorTex"].SetValue(content.VertexColor);
                pixelShader.Parameters["UseVertexColor"].SetValue(true);

                for (int i = 0; i < NeighbourhoodCityMeshBuilder.LayerCount; i++)
                {
                    if (_geometry.LayerPrims[i] <= 0 || _geometry.LayerVertices[i] == null) continue;
                    pixelShader.Parameters["TextureAtlasTex"].SetValue(content.TerrainTextures[i]);
                    SetBlendAtlas(pixelShader, content, i);
                    //Applying the pass is the only thing that binds the shader; a parameter write
                    //does not reach the pipeline on its own. Every texture change re-applies.
                    NeighbourhoodShaderState.ApplyCity(vsPass, vertexShader, pixelShader);
                    var before = _drawnPrimitives;
                    DrawRanges(_layerStarts[i], _geometry.LayerVertices[i], _geometry.LayerIndices[i],
                        _drawChunks);
                    if (_drawnPrimitives > before) _drawnLayers++;
                }

                if (content.RoadAtlas != null && _geometry.RoadPrims > 0 && _geometry.RoadVertices != null)
                {
                    pixelShader.Parameters["TextureAtlasTex"].SetValue(content.RoadAtlas);
                    pixelShader.Parameters["UseVertexColor"].SetValue(false);
                    _device.SamplerStates[0] = RoadSampler;
                    NeighbourhoodShaderState.ApplyCity(vsPass, vertexShader, pixelShader);
                    var before = _drawnPrimitives;
                    DrawRanges(_roadStarts, _geometry.RoadVertices, _geometry.RoadIndices, _drawChunks);
                    _drawnRoadPrimitives = _drawnPrimitives - before;
                }
                else if (content.RoadAtlas == null && _geometry.RoadPrims > 0)
                {
                    LogMissingRoadAtlasOnce(content);
                }

                //The close level: one refined 32x32-tile slice, drawn whole over the base's chunks
                //of the same slice (which BuildDrawChunks removed). The slice is a separate mesh
                //over the same map, so it is drawn per layer with the same texture and pass as the
                //base - which is what the city's own SubdivGeometry draw does.
                if (subdivActive && _subdiv.HasBuffers)
                {
                    for (int i = 0; i < NeighbourhoodCityMeshBuilder.LayerCount; i++)
                    {
                        if (_subdiv.LayerPrims[i] <= 0 || _subdiv.LayerVertices[i] == null) continue;
                        pixelShader.Parameters["TextureAtlasTex"].SetValue(content.TerrainTextures[i]);
                        SetBlendAtlas(pixelShader, content, i);
                        NeighbourhoodShaderState.ApplyCity(vsPass, vertexShader, pixelShader);
                        DrawRanges(null, _subdiv.LayerVertices[i], _subdiv.LayerIndices[i], _visibleChunks);
                    }
                }

                if (!_summaryLogged) LogSummaryOnce(content);
                LogNothingSubmittedOnce();
            }
            finally
            {
                _device.BlendState = previousBlend;
                _device.RasterizerState = previousRaster;
                _device.DepthStencilState = previousDepth;
            }
        }

        /// <summary>
        /// Binds the blend mask atlas a ground layer is drawn with, or leaves it unbound for layer
        /// zero. This is the city draw's <c>(i == 0) ? null : content.TransAtlas[trans]</c> line,
        /// with the same per-layer index.
        /// </summary>
        private static void SetBlendAtlas(Effect pixelShader, NeighbourhoodContent content, int layer)
        {
            if (pixelShader == null || content?.TransAtlas == null) return;
            if (layer <= 0) return;
            var atlas = NeighbourhoodBlendFlags.AtlasIndexForLayer(layer);
            if (atlas < 0 || atlas >= content.TransAtlas.Length) return;
            pixelShader.Parameters["TransAtlasTex"].SetValue(content.TransAtlas[atlas]);
        }

        /// <summary>
        /// The chunks the base geometry should submit: every visible chunk, minus the four chunks
        /// of the refined slice when one is resident (they are drawn by <see cref="_subdiv"/>).
        /// </summary>
        private void BuildDrawChunks(bool subdivActive)
        {
            _drawChunks.Clear();
            if (!subdivActive || _subdivChunks == null)
            {
                _drawChunks.AddRange(_visibleChunks);
                return;
            }
            foreach (var chunk in _visibleChunks)
            {
                if (!Contains(_subdivChunks, chunk)) _drawChunks.Add(chunk);
            }
        }

        private static bool Contains(int[] values, int value)
        {
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] == value) return true;
            }
            return false;
        }

        /// <summary>
        /// Works out which chunks the camera can see, with a whole-map shortcut in front of the
        /// per-chunk box test.
        /// </summary>
        private void CollectVisibleChunks(BoundingFrustum frustum, bool wholeMapWhenEmpty)
        {
            _visibleChunks.Clear();
            _hasVisibleRegion = false;
            if (frustum == null)
            {
                SelectEverything();
                return;
            }

            //1. The camera can see the whole map. `Contains`, not `!= Disjoint`: an intersecting
            //frustum is true of almost every camera aimed at the map, which is what kept every
            //chunk and made the per-chunk cull below dead in the common case.
            if (frustum.Contains(WholeMapBounds()) == ContainmentType.Contains)
            {
                SelectEverything();
                return;
            }

            //2. A projection that has gone to NaN or infinity is not a reason to draw nothing.
            if (!IsUsable(frustum))
            {
                SelectEverything();
                return;
            }

            //3. The real cull: a box test per chunk.
            NeighbourhoodCityMeshBuilder.ChunksInFrustum(frustum, _visibleChunks);
            _drawnChunks = _visibleChunks.Count;

            if (_visibleChunks.Count == 0)
            {
                //4. A caller that must never be a void (the in-lot surround) gets the whole map
                //rather than nothing. The lot camera is the lot's, not the map view's, and during a
                //lot load it can be off the map or mid-transition, which selects no chunk at all;
                //submitting the whole map then is the difference between the connected ground and
                //the lot floating in the clear colour.
                if (wholeMapWhenEmpty)
                {
                    SelectEverything();
                    return;
                }
                _visibleRegion = new Rectangle(0, 0, 0, 0);
                _hasVisibleRegion = true;
                return;
            }

            var region = NeighbourhoodCityMeshBuilder.ChunkBounds(_visibleChunks[0]);
            for (int i = 1; i < _visibleChunks.Count; i++)
            {
                region = Rectangle.Union(region, NeighbourhoodCityMeshBuilder.ChunkBounds(_visibleChunks[i]));
            }
            _visibleRegion = region;
            _hasVisibleRegion = true;
        }

        private static bool IsUsable(BoundingFrustum frustum)
        {
            var corners = frustum.GetCorners();
            for (int i = 0; i < corners.Length; i++)
            {
                if (!IsFinite(corners[i].X) || !IsFinite(corners[i].Y) || !IsFinite(corners[i].Z))
                {
                    return false;
                }
            }
            return true;
        }

        private void SelectEverything()
        {
            for (int i = 0; i < NeighbourhoodCityMeshBuilder.ChunkCount; i++) _visibleChunks.Add(i);
            _visibleRegion = new Rectangle(
                0, 0, NeighbourhoodCityMeshBuilder.MapSize, NeighbourhoodCityMeshBuilder.MapSize);
            _hasVisibleRegion = true;
            _drawnChunks = _visibleChunks.Count;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }

        /// <summary>
        /// The whole map as a box: the cull's "can this camera see all of it" test, and the same
        /// question the frame report asks.
        /// </summary>
        public static BoundingBox WholeMapBounds()
        {
            return new BoundingBox(
                new Vector3(0f, -MaxGroundHeightDelta, 0f),
                new Vector3(NeighbourhoodCityMeshBuilder.MapSize, MaxGroundHeightDelta,
                    NeighbourhoodCityMeshBuilder.MapSize));
        }

        /// <summary>
        /// Half the full height range of the terrain, in world units, so the whole-map box is
        /// symmetric about the map's own plane.
        /// </summary>
        public const float MaxGroundHeightDelta = 255f / NeighbourhoodCityMeshBuilder.ElevationScale / 2f;

        private void DrawRanges(int[] chunkStarts, VertexBuffer vertices, IndexBuffer indices)
        {
            DrawRanges(chunkStarts, vertices, indices, _drawChunks);
        }

        /// <summary>
        /// Submits the ranges of one buffer that cover exactly <paramref name="chunks"/>.
        /// </summary>
        private void DrawRanges(int[] chunkStarts, VertexBuffer vertices, IndexBuffer indices,
            ICollection<int> chunks)
        {
            if (vertices == null || indices == null) return;
            if (chunks == null || chunks.Count == 0) return;
            _device.SetVertexBuffer(vertices);
            _device.Indices = indices;
            if (chunkStarts == null)
            {
                Submit(NeighbourhoodDrawCall.WholeBuffer(indices.IndexCount));
                return;
            }

            NeighbourhoodCityMeshBuilder.VisibleDrawRanges(chunkStarts, chunks, _drawRanges);
            for (int i = 0; i < _drawRanges.Count; i++)
            {
                Submit(NeighbourhoodDrawCall.ForRange(_drawRanges[i]));
            }
        }

        /// <summary>Submits one range and counts it against this frame.</summary>
        private void Submit(NeighbourhoodDrawCall call)
        {
            if (call.IsEmpty) return;
            try
            {
                _device.DrawIndexedPrimitives(PrimitiveType.TriangleList,
                    call.BaseVertex, call.StartIndex, call.PrimitiveCount);
            }
            catch (Exception ex)
            {
                LogDrawFailureOnce(call, ex);
                throw;
            }
            _drawnPrimitives += call.PrimitiveCount;
            _drawnIndices += call.PrimitiveCount * 3;
            _drawnDrawCalls++;
        }

        private void LogDrawFailureOnce(NeighbourhoodDrawCall call, Exception ex)
        {
            if (_drawFailureLogged) return;
            _drawFailureLogged = true;
            SimitoneLog.Error("The generated neighbourhood's terrain draw threw while submitting "
                + call + ": " + ex.Message
                + " The draw is on Effects/VerShader's '"
                + NeighbourhoodShaderState.CityTechnique + "' pass "
                + NeighbourhoodShaderState.FogPass
                + " and the buffers are the lifted CityGeometry's TLayerVertex layout. A pass that"
                + " was not applied, or one applied under another technique, leaves a different"
                + " shader bound and gives exactly this error. The painted map stays the backdrop"
                + " until it is fixed.");
        }

        private void LogNothingSubmittedOnce()
        {
            if (_nothingSubmittedLogged || _drawnPrimitives > 0) return;
            _nothingSubmittedLogged = true;

            var sb = new System.Text.StringBuilder();
            sb.Append("The generated neighbourhood's terrain frame submitted nothing. ");
            if (_visibleChunks.Count == 0)
            {
                sb.Append("The frustum touches none of the ")
                  .Append(NeighbourhoodCityMeshBuilder.ChunkCount)
                  .Append(" chunks, so this is the cull doing its job rather than a failure: the"
                      + " camera is pointing away from the map. Visible tiles ")
                  .Append(_visibleRegion.X).Append(',').Append(_visibleRegion.Y).Append(' ')
                  .Append(_visibleRegion.Width).Append('x').Append(_visibleRegion.Height).Append('.');
            }
            else
            {
                sb.Append("The frustum touches ").Append(_visibleChunks.Count)
                  .Append(" chunks and the mesh holds ").Append(TotalPrimitives)
                  .Append(" triangles, so every non-empty layer and the roads were submitted"
                      + " anyway. The road pass is skipped outright when the content has no road"
                      + " atlas.");
            }
            SimitoneLog.Warn(sb.ToString());
        }

        private void LogMissingRoadAtlasOnce(NeighbourhoodContent content)
        {
            if (_roadAtlasWarned) return;
            _roadAtlasWarned = true;
            SimitoneLog.Warn("The generated neighbourhood built " + RoadPrimitives
                + " road triangles but has no road atlas, so the streets are in the mesh and not"
                + " on screen. The content loader said: "
                + string.Join("; ", content.Warnings));
        }

        private void LogSummaryOnce(NeighbourhoodContent content)
        {
            _summaryLogged = true;
            var sb = new System.Text.StringBuilder();
            sb.Append("The generated neighbourhood's terrain mesh (bespoke Simitone builder): ");
            sb.Append(TotalPrimitives).Append(" triangles in ").Append(RoadPrimitives)
              .Append(" road triangles at ").Append(_lod);
            if (content.RoadAtlas == null) sb.Append(", NO ROAD ATLAS");
            sb.Append("; this frame submitted ").Append(_drawnPrimitives).Append(" triangles (")
              .Append(_drawnIndices).Append(" indices) in ")
              .Append(_drawnDrawCalls).Append(" draw calls over ").Append(_drawnChunks).Append(" of ")
              .Append(NeighbourhoodCityMeshBuilder.ChunkCount).Append(" chunks");
            if (_subdividing)
            {
                sb.Append("; subdivided: ").Append(_subdivChunks == null ? 0 : _subdivChunks.Length)
                  .Append(" of the visible chunks from the refined ")
                  .Append(NeighbourhoodCityMeshBuilder.SliceSize).Append("-tile slice (")
                  .Append(_subdivRequested >= 0 ? _subdivRequested.ToString() : "none")
                  .Append("), and ").Append(_drawChunks.Count).Append(" from the whole-map level");
            }
            sb.Append("; the camera can see tiles ").Append(_visibleRegion.X).Append(',')
              .Append(_visibleRegion.Y).Append(' ').Append(_visibleRegion.Width).Append('x')
              .Append(_visibleRegion.Height);
            SimitoneLog.Info(sb.ToString());
        }

        public void Dispose()
        {
            _geometry?.Dispose();
            _subdiv?.Dispose();
            _meshReady = false;
            _buildRequested = false;
            _layerStarts = null;
            _roadStarts = null;
            _subdivRequested = -1;
            _subdivBusy = false;
            _subdivChunks = null;
            _content?.Dispose();
            _content = null;
        }
    }

    /// <summary>
    /// The handful of <c>VerShader</c>/<c>PixShader</c> parameters the generated neighbourhood's
    /// passes share. The technique and pass are the ones the city mesh is drawn with, so this is
    /// also what <c>NeighbourhoodFacades</c> and <c>NeighbourhoodFoliage</c> reference for their
    /// object technique.
    /// </summary>
    public static class NeighbourhoodShaderState
    {
        /// <summary>
        /// The technique that reads the generated vertex layout: position, two texture coordinate
        /// pairs, a normal and a transparency.
        /// </summary>
        public const string CityTechnique = "RenderNCity";

        /// <summary>The technique for the city's object buffers - the tree models and the lot facades.</summary>
        public const string CityObjectTechnique = "RenderCityObj";

        /// <summary>
        /// The <see cref="CityObjectTechnique"/> vertex pass that reads the normal slot as an
        /// offset. The one that shades instead is pass 0.
        /// </summary>
        public const int CityObjectOffsetVertexPass = 5;

        /// <summary>
        /// The pass both shaders are put on: <c>FinalFog</c>, which is the only one that applies
        /// the per-vertex transparency.
        /// </summary>
        public const int FogPass = 3;

        /// <summary>Set both shaders up for one city draw and return the vertex pass to re-apply per buffer.</summary>
        public static EffectPass BeginCity(
            Effect vertexShader, Effect pixelShader, Matrix viewProjection,
            Vector3 lightDirection, Vector4 lightColor, Vector4 fogColor, float fogDistance)
        {
            return BeginCity(vertexShader, pixelShader, viewProjection, viewProjection,
                lightDirection, lightColor, fogColor, fogDistance);
        }

        /// <summary>
        /// Set both shaders up for one city draw, with the view and the view-projection given
        /// separately, and return the vertex pass to re-apply per buffer.
        /// </summary>
        /// <remarks>
        /// <c>BaseMatrix</c> is <c>view * projection</c>; <c>MV</c> is the view, and it exists so
        /// <c>PixShader</c>'s fog can take a real distance from the camera. Handing the
        /// view-projection as <c>MV</c> measures a length in clip space and renders the whole map
        /// as one flat sheet of fog colour.
        /// </remarks>
        public static EffectPass BeginCity(
            Effect vertexShader, Effect pixelShader, Matrix viewProjection, Matrix view,
            Vector3 lightDirection, Vector4 lightColor, Vector4 fogColor, float fogDistance)
        {
            if (vertexShader == null || pixelShader == null) return null;

            var vs = vertexShader.Techniques[CityTechnique];
            var ps = pixelShader.Techniques[CityTechnique];
            if (vs == null || ps == null) return null;
            if (FogPass >= vs.Passes.Count || FogPass >= ps.Passes.Count) return null;
            vertexShader.CurrentTechnique = vs;
            pixelShader.CurrentTechnique = ps;

            vertexShader.Parameters["BaseMatrix"].SetValue(viewProjection);
            vertexShader.Parameters["DepthBias"].SetValue(0f);
            vertexShader.Parameters["MV"].SetValue(view);

            pixelShader.Parameters["LightCol"].SetValue(lightColor);
            pixelShader.Parameters["LightVec"].SetValue(lightDirection);
            pixelShader.Parameters["ShadowMult"].SetValue(1f);
            pixelShader.Parameters["FogColor"].SetValue(fogColor);
            pixelShader.Parameters["FogMaxDist"].SetValue(fogDistance > 1f ? fogDistance : 1f);
            pixelShader.Parameters["UseVertexColor"].SetValue(false);

            return vs.Passes[FogPass];
        }

        /// <summary>Apply both techniques' passes. Separate because the pass is re-applied after every texture change.</summary>
        public static void ApplyCity(EffectPass vertexPass, Effect vertexShader, Effect pixelShader)
        {
            if (vertexPass == null || vertexShader == null || pixelShader == null) return;
            vertexPass.Apply();
            if (FogPass < pixelShader.CurrentTechnique.Passes.Count)
            {
                pixelShader.CurrentTechnique.Passes[FogPass].Apply();
            }
        }
    }
}
