/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The vertex a lot facade is drawn as, and why it is not the one
    /// <c>LotFacadeGenerator</c> produces.
    ///
    /// <para><b>The bug this type exists for.</b> The facade draw threw every frame with the same
    /// error the terrain draw threw, from the same cause and with one extra step: the device
    /// builds its input layout by validating the vertex declaration against the vertex shader that
    /// is <em>bound</em>, and a shader is only bound by <c>EffectPass.Apply</c>. The terrain was
    /// fixed by applying its pass. The facades were already applying theirs, and kept throwing -
    /// because their declaration was wrong in a second, independent way, and applying the pass is
    /// what exposed it rather than what caused it.</para>
    ///
    /// <para><b>What is wrong with it.</b> A facade is drawn on
    /// <see cref="NeighbourhoodShaderState.CityObjectTechnique"/> (<c>RenderCityObj</c>), whose
    /// input struct is <c>ObjVertexIn</c>:
    /// <c>float4 position : SV_Position0; float2 texCoord : TEXCOORD0; float3 normal :
    /// TEXCOORD1;</c> - three elements. The facade surfaces were uploaded as
    /// <see cref="VertexPositionTexture"/>, which is <c>float3</c> plus <c>float2</c>: two of
    /// them, and no <c>TEXCOORD1</c> at all. D3D11 rejects an input layout that does not carry every
    /// element the shader reads, so the first <c>DrawIndexedPrimitives</c> of the first surface
    /// raised "the current vertex declaration does not include all the elements required by the
    /// current vertex shader" with a perfectly correct pass bound. Which is why the log named the
    /// facades and not the terrain: the facades draw first, and the throw is per surface, so the
    /// facades' was the one that surfaced.</para>
    ///
    /// <para><b>Why the mesh was not changed instead.</b> <c>LotFacadeGenerator</c> is shared with
    /// <c>SimitoneNeighOBJExporter</c>, which wants <see cref="VertexPositionTexture"/> and reads
    /// it out of <see cref="NeighbourhoodFacadeSurface.Vertices"/> - that stays exactly as it is.
    /// Only the copy that goes to the GPU is widened, here, so the one place that has to know what
    /// the object vertex is knows it in one place.</para>
    ///
    /// <para><b>What goes in the normal slot.</b> The real face normal, computed from the
    /// triangles. The shipped pixel shader's object passes hardcode <c>diffuse = 1</c> and never
    /// read it (<c>PixShader.fx</c> has the <c>dot(normalize(Input.normal.xyz), LightVec.xyz)</c>
    /// commented out), so nothing on screen depends on it today; it is computed anyway because the
    /// slot has to be there, and a slot filled with a placeholder is a lie the next person reading
    /// the vertex is entitled to believe.</para>
    /// </summary>
    public static class NeighbourhoodFacadeVertex
    {
        /// <summary>
        /// The input struct of the technique the facades and the trees are drawn with, as it is
        /// spelled in <c>VerShader.fx</c>. Named so a diagnostic can name the thing the declaration
        /// is being validated against.
        /// </summary>
        public const string ObjectInputStruct = "ObjVertexIn";

        /// <summary>How many elements <see cref="ObjectInputStruct"/> reads.</summary>
        public const int ObjectInputElementCount = 3;

        /// <summary>
        /// The declaration a facade's vertex buffer has to be drawn with: a three-component
        /// position, a two-component texture coordinate, and the three-component <c>TEXCOORD1</c>
        /// the object vertex reads. <see cref="DGRP3DVert"/>'s own, unchanged - it is the type
        /// <c>CityFoliage</c> draws the trees with and the one the effect was written for.
        /// </summary>
        public static VertexDeclaration ObjectDeclaration
        {
            get { return DGRP3DVert.VertexDeclaration; }
        }

        /// <summary>Bytes per facade vertex, for a log that has to name the layout.</summary>
        public static int SizeInBytes
        {
            get { return ObjectDeclaration.VertexStride; }
        }

        /// <summary>
        /// <see cref="ObjectDeclaration"/> as one line of text, for the same reason
        /// <c>NeighbourhoodTerrainVertex.DescribeDeclaration</c> exists: MonoGame's error names the
        /// failure without naming what it was compared against.
        /// </summary>
        public static string DescribeDeclaration()
        {
            return Describe(ObjectDeclaration);
        }

        /// <summary>Any declaration, as one line of text.</summary>
        /// <param name="declaration">The declaration to describe, or <c>null</c>.</param>
        public static string Describe(VertexDeclaration declaration)
        {
            if (declaration == null) return "no vertex declaration";
            var elements = declaration.GetVertexElements();
            var text = new System.Text.StringBuilder();
            for (int i = 0; i < elements.Length; i++)
            {
                if (i > 0) text.Append(", ");
                text.Append(elements[i].VertexElementUsage).Append(elements[i].UsageIndex)
                    .Append(' ').Append(elements[i].VertexElementFormat)
                    .Append(" at byte ").Append(elements[i].Offset);
            }
            return text.ToString();
        }

        /// <summary>
        /// Whether a declaration carries every element <see cref="ObjectInputStruct"/> reads, which
        /// is the whole input-layout rule: a position, a texture coordinate and a second texture
        /// coordinate.
        /// </summary>
        /// <remarks>
        /// A rule rather than a fact, and split out so it can be asserted against both the
        /// declaration that is used and the shipped <c>VerShader.fx</c> without a graphics device.
        /// Formats are not checked - D3D reads fewer components out of a wider slot, so a
        /// <c>Vector4</c> where the shader wants a <c>Vector2</c> is fine and a <c>Vector2</c> where
        /// it wants a <c>Vector3</c> is not, which is exactly the case this is about.
        /// </remarks>
        /// <param name="declaration">The declaration the draw will bind.</param>
        public static bool SatisfiesObjectInput(VertexDeclaration declaration)
        {
            if (declaration == null) return false;
            var elements = declaration.GetVertexElements();
            if (elements.Length < ObjectInputElementCount) return false;
            bool position = false, first = false, second = false;
            for (int i = 0; i < elements.Length; i++)
            {
                if (elements[i].VertexElementUsage == VertexElementUsage.Position
                    && elements[i].UsageIndex == 0) position = true;
                else if (elements[i].VertexElementUsage == VertexElementUsage.TextureCoordinate)
                {
                    if (elements[i].UsageIndex == 0) first = true;
                    else if (elements[i].UsageIndex == 1) second = true;
                }
            }
            return position && first && second;
        }

        /// <summary>
        /// The placed facade's vertices in the layout the object vertex reads, or <c>null</c>.
        /// </summary>
        /// <remarks>
        /// Positions and texture coordinates are copied across unchanged - they are what the lot
        /// facade actually is - and the normal slot is filled in from the triangles. Pure, so the
        /// conversion can be asserted without a device, which matters because the failure it fixes
        /// is a <c>SetData</c> of the wrong vertex type and there is no way to see that from a
        /// headless build except by checking the type and its declaration.
        /// </remarks>
        /// <param name="placed">The vertices, already placed in the world.</param>
        /// <param name="indices">The surface's triangle list, for the normals.</param>
        public static DGRP3DVert[] ToObjectVertices(VertexPositionTexture[] placed, int[] indices)
        {
            if (placed == null || placed.Length == 0) return null;
            var vertices = new DGRP3DVert[placed.Length];
            for (int i = 0; i < placed.Length; i++)
            {
                //Up until the face normals are worked out, so a vertex in no triangle is still a
                //finite normal rather than a zero the shader would normalize into a NaN
                vertices[i] = new DGRP3DVert(placed[i].Position, Vector3.UnitY, placed[i].TextureCoordinate);
            }

            if (indices != null && indices.Length > 2)
            {
                try
                {
                    //GenerateNormals works in place on a list, and it is a list because it accumulates
                    //into the vertices it is handed, so the array is copied out again afterwards.
                    var smoothed = new List<DGRP3DVert>(vertices);
                    DGRP3DVert.GenerateNormals(false, smoothed, indices);
                    smoothed.CopyTo(vertices);
                }
                catch (Exception)
                {
                    //Normals are decoration here - the object pixel pass hardcodes diffuse - so a
                    //mesh whose indices do not line up keeps its straight-up normals and still
                    //draws. Anything better than that would trade houses for shading.
                }
            }
            return vertices;
        }
    }

    /// <summary>One lot's facade mesh, once it has been built and placed in the world.</summary>
    public sealed class NeighbourhoodFacade
    {
        /// <summary>The lot this facade is.</summary>
        public NeighbourhoodLot Lot { get; internal set; }

        /// <summary>
        /// The hash the mesh was built from, so a lot whose <c>HouseNN.iff</c> has changed is
        /// rebuilt and one whose has not is not.
        /// </summary>
        public uint HouseHash { get; internal set; }

        /// <summary>The ground height the mesh was placed at, in world units.</summary>
        public float GroundHeight { get; internal set; }

        /// <summary>What the mesh occupies, for the frustum test.</summary>
        public BoundingBox Bounds { get; internal set; }

        /// <summary>Walls, roof and floor, each a vertex list, its indices and its texture.</summary>
        public NeighbourhoodFacadeSurface[] Surfaces { get; internal set; }

        /// <summary>Whether there is anything to draw.</summary>
        public bool HasGeometry
        {
            get { return Surfaces != null && Surfaces.Length > 0; }
        }

        /// <summary>Every triangle across every surface.</summary>
        public int PrimitiveCount
        {
            get
            {
                int total = 0;
                if (Surfaces == null) return 0;
                //a lot with no roof, or no floor, has a null slot: Assemble leaves it null
                for (int i = 0; i < Surfaces.Length; i++) total += Surfaces[i]?.PrimitiveCount ?? 0;
                return total;
            }
        }

        public void Dispose()
        {
            if (Surfaces == null) return;
            for (int i = 0; i < Surfaces.Length; i++) Surfaces[i]?.Dispose();
            Surfaces = null;
        }
    }

    /// <summary>
    /// What one frame's facade work did, so a caller can log it without asking again.
    /// </summary>
    /// <remarks>
    /// The three numbers and the flag are the whole of what a view needs to decide whether to say
    /// anything, and every one of them is something the view would otherwise have to recompute by
    /// walking the layout a second time - which is both a duplicate allocation every frame and a
    /// chance to log a number that was already stale.
    /// </remarks>
    public readonly struct NeighbourhoodFacadeStepResult
    {
        /// <summary>A frame that did no work at all: no device, or no layout.</summary>
        public static readonly NeighbourhoodFacadeStepResult Nothing =
            new NeighbourhoodFacadeStepResult(0, 0, 0, false);

        /// <summary>How many meshes this frame built.</summary>
        public int Built { get; }

        /// <summary>How many lots this frame tried and gave up on.</summary>
        public int Failed { get; }

        /// <summary>How many lots are still owed a mesh after this frame.</summary>
        public int Outstanding { get; }

        /// <summary>Whether the queue is empty: every drawable lot has been tried.</summary>
        public bool Drained { get; }

        public NeighbourhoodFacadeStepResult(int built, int failed, int outstanding, bool drained)
        {
            Built = built;
            Failed = failed;
            Outstanding = outstanding;
            Drained = drained;
        }
    }

    /// <summary>
    /// One drawable surface of a facade: its mesh, the buffers it lives in, and the texture it is
    /// drawn with.
    /// </summary>
    /// <remarks>
    /// The buffers are uploaded when the mesh is built rather than at draw time.
    /// <c>DrawUserPrimitives</c>'s indexed overload wants a <see cref="VertexDeclaration"/>
    /// argument, and a facade surface has three different ones under one draw call, so keeping
    /// the mesh on the GPU is both the tidier and the faster answer - and it is what lets a
    /// frustum-culled facade be three draw calls instead of three per-frame array uploads.
    /// </remarks>
    public sealed class NeighbourhoodFacadeSurface : IDisposable
    {
        /// <summary>The uploaded vertex buffer, or <c>null</c> if the upload failed.</summary>
        public VertexBuffer VertexBuffer { get; internal set; }

        /// <summary>The uploaded index buffer, or <c>null</c> if the upload failed.</summary>
        public IndexBuffer IndexBuffer { get; internal set; }

        /// <summary>
        /// Positions and texture coordinates, in world tiles, as
        /// <c>LotFacadeGenerator</c> produced them.
        /// </summary>
        /// <remarks>
        /// Kept as <see cref="VertexPositionTexture"/> and not replaced, because this is the array
        /// <c>SimitoneNeighOBJExporter</c> reads to write a lot's walls out. The vertex buffer below
        /// holds the widened copy the draw actually uses - see
        /// <see cref="NeighbourhoodFacadeVertex"/> - so the two are not the same layout and this
        /// property is deliberately not the buffer's source.
        /// </remarks>
        public VertexPositionTexture[] Vertices { get; internal set; }

        /// <summary>Triangle list indices into <see cref="Vertices"/>.</summary>
        public int[] Indices { get; internal set; }

        /// <summary>The rendered wall, roof or floor texture.</summary>
        public Texture2D Texture { get; internal set; }

        /// <summary>
        /// Whether disposing this surface may free <see cref="Texture"/>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The wall and floor targets are rendered for this lot alone and belong to it. The roof
        /// texture does not: <c>RoofComponent</c> hands out the content system's shared roof
        /// texture, one instance for every lot with that roof, and disposing it here takes the
        /// roofs out of the lot view as well as out of the neighbourhood.
        /// </para>
        /// <para>
        /// A surface that disposed a texture it did not own, and a facade that handed the same
        /// texture to two surfaces, are the two ways a cached facade stops drawing while the log
        /// still says it was built. Both are ownership questions, and this flag is the answer to
        /// one of them.
        /// </para>
        /// </remarks>
        public bool OwnsTexture { get; internal set; }

        /// <summary>How many triangles.</summary>
        public int PrimitiveCount
        {
            get { return Indices == null ? 0 : Indices.Length / 3; }
        }

        /// <summary>Whether there is anything to draw.</summary>
        public bool IsEmpty
        {
            get
            {
                return PrimitiveCount == 0 || Vertices == null || Vertices.Length == 0
                    || VertexBuffer == null || IndexBuffer == null;
            }
        }

        /// <summary>
        /// Whether this surface can actually be drawn: it has geometry, its buffers are uploaded,
        /// and it has a texture.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Separate from <see cref="IsEmpty"/> because a surface with geometry and no texture is
        /// not empty - it is worse than empty, because it issues a draw call against an unbound
        /// sampler and samples whatever the last bind left there. The draw loop asks this one and
        /// never the other.
        /// </para>
        /// <para>
        /// The rule is the static <see cref="Drawable"/> so it can be asserted without a graphics
        /// device: the buffer half of it can only ever be true on a machine with a GPU, and a rule
        /// that can only be tested on one is a rule that does not get tested.
        /// </para>
        /// </remarks>
        public bool IsDrawable
        {
            get
            {
                return Drawable(PrimitiveCount, Vertices == null ? 0 : Vertices.Length,
                    VertexBuffer != null && IndexBuffer != null, Texture != null);
            }
        }

        /// <summary>
        /// The surface-drawable rule, with no graphics device in it.
        /// </summary>
        /// <param name="primitives">How many triangles the surface has.</param>
        /// <param name="vertexCount">How many vertices it has.</param>
        /// <param name="buffersUploaded">Whether both GPU buffers exist.</param>
        /// <param name="hasTexture">Whether a texture is bound to it.</param>
        public static bool Drawable(int primitives, int vertexCount, bool buffersUploaded, bool hasTexture)
        {
            return primitives > 0 && vertexCount > 0 && buffersUploaded && hasTexture;
        }

        public void Dispose()
        {
            VertexBuffer?.Dispose();
            VertexBuffer = null;
            IndexBuffer?.Dispose();
            IndexBuffer = null;
            if (OwnsTexture)
            {
                Texture?.Dispose();
            }
            OwnsTexture = false;
            Texture = null;
            Vertices = null;
            Indices = null;
        }
    }

    /// <summary>
    /// The per-lot facade cache: builds each lot's <c>LotFacadeGenerator</c> mesh once, keyed by
    /// <see cref="NeighbourhoodLot.HouseHash"/>, frustum-culls it, and skips the lot the player is
    /// standing in. §7.2.
    ///
    /// <para><b>Why building one is expensive.</b> A facade is not geometry that can be
    /// synthesised: it is a render of the lot's own walls, roof and floor, drawn by standing a
    /// <c>FSO.LotView.World</c> and a <c>FSO.SimAntics.VM</c> up for that lot, loading its
    /// <c>HouseNN.iff</c> through <c>VMTS1Activator</c>, and letting
    /// <c>LotFacadeGenerator</c> rasterise it - which is exactly what
    /// <c>SimitoneNeighOBJExporter</c> already does per lot, and the reason it is a debug-flagged
    /// authoring tool rather than something the game runs behind a player's back. So it is
    /// budgeted: at most <see cref="DefaultBuildsPerFrame"/> meshes a frame, nearest the camera
    /// first (<see cref="NeighbourhoodFacadePlan.ToBuild"/>), at most
    /// <see cref="NeighbourhoodFacadePlan.DefaultMaxCooldownFrames"/> frames apart
    /// (<see cref="NeighbourhoodFacadePlan.DecideAfterBuild"/>), and the rest arrive over the next
    /// few seconds. A neighbourhood that opens in 3D fills in rather than stalling.
    /// </para>
    ///
    /// <para><b>And it always finishes.</b> The one thing this pass must not be able to do is
    /// stop, because a stop is indistinguishable from a hang: it used to stop on the first build
    /// that cost more than <see cref="MaxBuildMs"/>, and a machine where every build costs seconds
    /// came up with one house in a forty-one lot neighbourhood and a log line saying why. A build
    /// over that ceiling is now a <em>report</em> - marked on its own lot's line, with a warning
    /// the first time - and never an instruction. The stall itself cannot be avoided from here,
    /// since a build cannot be abandoned half way; what can be avoided is losing the other forty
    /// houses over it.
    /// </para>
    ///
    /// <para><b>What a broken lot costs.</b> One entry, and nothing else. A lot whose
    /// <c>HouseNN.iff</c> will not parse throws inside the generator; that is caught, recorded
    /// against that lot, and the pass carries on with the rest of the neighbourhood. That is the
    /// plan's guard case - "a neighbourhood with a broken <c>HouseNN.iff</c> still opens, with the
    /// bad lot's facade skipped and the rest generated" - and it is only true because the throw
    /// is caught per lot rather than per pass.
    /// </para>
    ///
    /// <para><b>Failure is not retried.</b> A lot that failed is recorded in the cache with its
    /// hash, so <see cref="NeighbourhoodFacadePlan.NeedsBuild"/> is false for it and it is not
    /// retried every frame. Re-attempting a lot that cannot be built fifty times a second would
    /// make one bad file the most expensive thing in the neighbourhood.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodFacades : IDisposable
    {
        /// <summary>
        /// How many lot meshes may be built in one frame. One.
        /// </summary>
        /// <remarks>
        /// <para>This was two, on the reasoning that a fifty-lot neighbourhood would otherwise
        /// take four seconds to fill in. That reasoning was made before anything had measured a
        /// build, and it was wrong in both directions: a build does not take two hundred
        /// milliseconds, it takes seconds, so "two per frame" was never a fill-in rate
        /// but a freeze with a log line at either end of it.</para>
        /// <para>One is the count that can be reasoned about at all, and it is now the only
        /// thing standing between a build and the rest of the frame: a facade build cannot be
        /// abandoned half way, so the per-frame cap is what bounds how much of a frame is
        /// building. The <em>rate</em> at which builds are started is set separately, by
        /// <see cref="NeighbourhoodFacadePlan.DefaultMaxCooldownFrames"/>, and that is the rule
        /// that guarantees the pass reaches the end of the queue.</para>
        /// </remarks>
        public const int DefaultBuildsPerFrame = 1;

        /// <summary>
        /// How long a frame may spend building facades by default, in milliseconds.
        /// </summary>
        /// <remarks>
        /// A build stands a whole <c>World</c> and <c>VM</c> up and rasterises the lot's walls at
        /// 1024 pixels wide, so its cost is the lot's and not ours - a plain house is quick and a
        /// fully furnished one is not. This is a target the pass averages towards, not a limit
        /// it enforces: it is consulted before a build starts, and a build that overruns it by a
        /// factor of five hundred is what the ceiling in
        /// <see cref="NeighbourhoodFacadePlan.DefaultMaxBuildMs"/> is for.
        /// </remarks>
        public const double DefaultBuildTimeBudgetMs = 10d;

        private readonly Dictionary<short, NeighbourhoodFacade> _cache =
            new Dictionary<short, NeighbourhoodFacade>();

        private readonly Dictionary<short, uint> _hashes = new Dictionary<short, uint>();
        //B2 task 5: the lots this cache must NOT build a facade for, because the bridge is
        //standing them up as real SubWorldComponents. Empty until a caller says otherwise,
        //which is the capability-off state and the map view's state.
        private readonly HashSet<short> _subWorldLots = new HashSet<short>();
        private readonly Dictionary<short, string> _failures = new Dictionary<short, string>();
        private readonly List<string> _log = new List<string>();

        private long _frameStart = Stopwatch.GetTimestamp();
        private int _frameBuilt;
        private int _cooldownFrames;
        private double _lastBuildMs;
        private bool _overCeiling;
        private bool _ceilingLogged;
        //the lot the player is standing in, as of the last Step. Read by the "gave up" message so
        //that its progress line counts the same lots the view's own summary would
        private int? _activeLot;

        private readonly GraphicsDevice _device;
        private readonly NeighbourhoodLayout _layout;
        private readonly NeighbourhoodMap _map;

        /// <summary>How many meshes a frame may build.</summary>
        public int BuildsPerFrame { get; set; }

        /// <summary>
        /// How far from the camera a lot may be and still be built this visit, in tiles.
        /// </summary>
        /// <remarks>
        /// A lot outside it is deferred rather than failed: it keeps its place in the queue and
        /// is built the moment the camera comes near enough for it to be visible. Zero or less
        /// builds every lot regardless of distance, which is the old behaviour and is only right
        /// on a machine where a build is cheap.
        /// </remarks>
        public float BuildRadiusTiles { get; set; }

        /// <summary>
        /// One build may take this long, in milliseconds, before the pass says out loud that a
        /// frame was held up for longer than that. Zero or less removes the reporting.
        /// </summary>
        /// <remarks>
        /// <para><b>A report, not a stop.</b> It used to be the other: a build over the ceiling
        /// ended building for the rest of the visit, and on a machine where a build costs seconds
        /// that produced one house in a forty-one lot neighbourhood - which is the bug this
        /// class is in the log about. Nothing about the cost can be reduced from here once the
        /// build has started, but the queue can always be resumed: the pass keeps to
        /// <see cref="BuildsPerFrame"/> builds a frame, waits at most
        /// <see cref="MaxCooldownFrames"/> frames between them, and marks every build's timing
        /// line that broke the ceiling. So a player on a slow machine gets a neighbourhood that
        /// fills in, one line per house saying what the house cost, and a warning the first time
        /// explaining the policy - rather than a frozen screen for ninety seconds, and rather than
        /// one house and an apology.</para>
        /// </remarks>
        public double MaxBuildMs { get; set; }

        /// <summary>
        /// The most frames a build of any cost may push the next one back by. Zero or less
        /// disables the delay between builds entirely.
        /// </summary>
        /// <remarks>
        /// The other half of the rate, and the part that guarantees the pass reaches the end of
        /// the queue. See <see cref="NeighbourhoodFacadePlan.DefaultMaxCooldownFrames"/> for why
        /// the delay is capped rather than left proportional to the cost.
        /// </remarks>
        public int MaxCooldownFrames { get; set; }

        /// <summary>How long the last build took, in milliseconds, or zero if none has run.</summary>
        public double LastBuildMs
        {
            get { return _lastBuildMs; }
        }

        /// <summary>
        /// Whether the last build cost more than <see cref="MaxBuildMs"/>, which is the fact the
        /// pass reports rather than acts on.
        /// </summary>
        /// <remarks>
        /// <b>Not a stop any more, and deliberately.</b> This used to mean "the pass has given
        /// up for this visit", it was the flag that made one build of 2.18 seconds into a
        /// neighbourhood of forty-one, and it is now exactly the question the warning line and the
        /// per-lot marks answer. A pass that can stop can stop at the first bad lot; a pass that
        /// can only report cannot.
        /// </remarks>
        public bool OverCeiling
        {
            get { return _overCeiling; }
        }

        /// <summary>How many frames the pass is waiting out before it will build again.</summary>
        public int CooldownFrames
        {
            get { return _cooldownFrames; }
        }

        /// <summary>
        /// The lot numbers this cache will never build a facade for, because they are real
        /// sub-worlds. Empty unless a caller has handed over a plan - see
        /// <see cref="UseSubWorlds"/>, and the decision itself in
        /// <see cref="NeighbourhoodRenderDecision"/>.
        /// </summary>
        public IReadOnlyCollection<short> SubWorldLots
        {
            get { return _subWorldLots; }
        }

        /// <summary>
        /// The lots this pass is queueing over: the lots the caller handed in, minus those the mesh
        /// emits no ground under, minus the lot the player is standing in.
        /// <c>SimitoneSurroundings.NearLotsLayout</c>'s and
        /// <see cref="WithoutGroundlessLots"/>'s own construction.
        /// </summary>
        /// <remarks>
        /// Exposed so a caller can ask what the queue actually holds - the surround does, to report
        /// <see cref="SimitoneSurroundings.FacadeFallbackCount"/> - without keeping a second copy of
        /// the layout and without reaching into the pass's private state. It is the same object the
        /// builds are drawn from, so a count taken through it cannot disagree with the queue.
        /// </remarks>
        public NeighbourhoodLayout Layout
        {
            get { return _layout; }
        }

        /// <summary>How many lots are withheld from this pass because they are sub-worlds.</summary>
        public int WithheldCount
        {
            get { return _subWorldLots.Count; }
        }

        /// <summary>
        /// Tells the cache which lots the bridge is standing up as real sub-worlds, so that
        /// it stops building facades for them. B2 task 5's one decision per lot.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Every question this class asks a lot goes through
        /// <see cref="NeighbourhoodFacadePlan.ShouldDraw"/>, which is handed this set: the
        /// build queue, the eligibility count, the progress line and the cross-visit adoption
        /// all consult it. The draw consults it separately, because a mesh built before the
        /// plan arrived is already in the cache and has to stop being submitted even though it
        /// was a correct mesh when it was built.
        /// </para>
        /// <para>
        /// <b>A withheld mesh is kept, not freed.</b> It is already paid for - seconds of
        /// <c>World</c> and <c>VM</c> - and it is in the cross-visit store anyway, so
        /// freeing it here would throw away a mesh the next visit would otherwise adopt in
        /// order to withhold again. It is not drawn while it is withheld and it comes back if
        /// the capability goes off.
        /// </para>
        /// <para>
        /// Safe to call at any time, including before the first <see cref="Step"/> and more
        /// than once: it reads the plan and replaces the set wholesale, so the order the
        /// screen builds the plan and stands the neighbours up in does not matter.
        /// </para>
        /// </remarks>
        /// <param name="plan">The neighbour plan, or <c>null</c> for "no sub-worlds".</param>
        /// <returns>How many lots are now withheld.</returns>
        public int UseSubWorlds(SimitoneSurroundingsPlan plan)
        {
            _subWorldLots.Clear();
            if (plan != null && plan.IsValid)
            {
                for (int i = 0; i < plan.Neighbours.Count; i++)
                {
                    var lot = plan.Neighbours[i].Lot;
                    if (lot != null) _subWorldLots.Add(lot.Number);
                }
            }
            if (_subWorldLots.Count > 0)
            {
                _log.Add(_subWorldLots.Count + " neighbouring lot(s) are real sub-worlds, so no facade is built or drawn for them");
            }
            return _subWorldLots.Count;
        }

        /// <summary>Whether a lot is withheld from this pass because it is a real sub-world.</summary>
        /// <param name="lotNumber">The lot.</param>
        public bool IsWithheld(short lotNumber)
        {
            return _subWorldLots.Contains(lotNumber);
        }

        /// <summary>How many lots have a facade in the cache.</summary>
        public int CachedCount
        {
            get { return _cache.Count; }
        }

        /// <summary>How many lots the pass has tried and given up on.</summary>
        public int FailedCount
        {
            get { return _failures.Count; }
        }

        /// <summary>Everything the pass has said about itself, for the log.</summary>
        public IReadOnlyList<string> Log
        {
            get { return _log; }
        }

        /// <summary>How many lots the pass has tried, built or not.</summary>
        public int AttemptedCount
        {
            get { return _hashes.Count; }
        }

        /// <summary>How many meshes this pass has built.</summary>
        public int BuiltCount
        {
            get { return _cache.Count; }
        }

        /// <summary>Why a particular lot has no facade, or <c>null</c>.</summary>
        /// <param name="lotNumber">The lot.</param>
        public string FailureFor(short lotNumber)
        {
            string reason;
            return _failures.TryGetValue(lotNumber, out reason) ? reason : null;
        }

        /// <summary>
        /// Every failure, one line each, in lot-number order. Read when the queue drains so the
        /// summary can say which lots are missing and why rather than only how many.
        /// </summary>
        public IReadOnlyList<string> Failures
        {
            get
            {
                var numbers = new List<short>(_failures.Keys);
                numbers.Sort();
                var lines = new List<string>(numbers.Count);
                for (int i = 0; i < numbers.Count; i++)
                {
                    lines.Add("lot " + numbers[i] + ": " + _failures[numbers[i]]);
                }
                return lines;
            }
        }

        /// <summary>
        /// A one-line account of the pass, for the log: how many of how many lots have a mesh and
        /// how many are still owed one.
        /// </summary>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="focus">The world tile the camera is looking at.</param>
        public string Describe(int? activeLot, Vector2 focus)
        {
            int outstanding, eligible;
            NeighbourhoodFacadePlan.Progress(_layout, _hashes, activeLot, focus, out outstanding,
                out eligible, _subWorldLots);
            return FacadeSummary(_cache.Count, _failures.Count, outstanding, eligible);
        }

        /// <summary>
        /// The wording of a facade progress line, with no graphics device, layout or cache in it.
        /// </summary>
        /// <remarks>
        /// Split out because "0 lot facades" on its own is the line that made this bug
        /// undiagnosable: a player reading it cannot tell a neighbourhood that has not started
        /// building from one that never will. With the denominator and the outstanding count on the
        /// same line, "0 of 38, 38 still to build" and "0 of 38, 38 failed" are different worlds.
        /// </remarks>
        /// <param name="built">How many lots have a mesh.</param>
        /// <param name="failed">How many lots the pass has given up on.</param>
        /// <param name="outstanding">How many lots are still owed a mesh.</param>
        /// <param name="eligible">How many lots a complete pass will ever attempt.</param>
        public static string FacadeSummary(int built, int failed, int outstanding, int eligible)
        {
            var summary = "lot facades: " + built + " of " + eligible + " built, " + failed + " failed";
            if (outstanding > 0)
            {
                summary += ", " + outstanding + " still to build";
            }
            return summary;
        }

        /// <summary>
        /// Creates a cache over one generated neighbourhood.
        /// </summary>
        /// <param name="device">The graphics device facade meshes are rendered on.</param>
        /// <param name="layout">The packed layout, or <c>null</c> to make an empty cache.</param>
        /// <param name="map">The generated map, for the ground heights.</param>
        public NeighbourhoodFacades(GraphicsDevice device, NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            _device = device;
            _map = map;
            //Before anything reads it: a lot with no drawn ground under it is not built for, not
            //built over, and not counted as eligible. See WithoutGroundlessLots.
            _layout = WithoutGroundlessLots(layout, map);
            layout = _layout;
            BuildsPerFrame = DefaultBuildsPerFrame;
            BuildTimeBudgetMs = DefaultBuildTimeBudgetMs;
            BuildRadiusTiles = NeighbourhoodFacadePlan.DefaultBuildRadiusTiles;
            MaxBuildMs = NeighbourhoodFacadePlan.DefaultMaxBuildMs;
            MaxCooldownFrames = NeighbourhoodFacadePlan.DefaultMaxCooldownFrames;
            _frameStart = Stopwatch.GetTimestamp();
            //The store holds this device's buffers and textures, so it has to hear about a reset
            //from the device itself rather than from whoever is drawing.
            NeighbourhoodFacadeMeshCache.Watch(device);
            //Adopt whatever the last visit of this neighbourhood already built. A facade is a
            //render of a lot's own HouseNN.iff and the file has not changed, so the mesh on the
            //GPU is still the right one - and re-rendering it costs seconds, which is the entire
            //cost being argued about. Keyed by the house file's own hash, so a lot that has been
            //edited since is not adopted, it is rebuilt.
            AdoptShared(layout, map);
        }

        /// <summary>
        /// The layout this pass will build for, with the lots the mesh emits no ground under taken
        /// out - and the caller's layout unchanged when there is nothing to take out.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Part 9 §9.1: a lot in one of the isometric diamond's cut corners stands where the mesh
        /// emits neither ground quads nor road quads, so a facade built for it is a house floating
        /// over a void. The diamond-aligned <c>NeighbourhoodPixelTransform</c> keeps the shipped
        /// layouts out of those corners, but the guard belongs to the pass that places houses rather
        /// than to the transform that aims to keep it unnecessary - a hand-edited sidecar, a modded
        /// lot or a changed map size can name a plot the clip does not cover, and then a house is
        /// drawn over nothing.
        /// </para>
        /// <para>
        /// <b>Here rather than in the plan, and here is the only place that can.</b> The in-lot
        /// surround already filters through <c>SimitoneSurroundingsCull.CoversLot</c>, which is the
        /// sub-world side's question; the <em>neighbourhood view</em> passes a null cut and no plan
        /// at all, so it had no place to ask. Filtering the layout the pass is constructed over is
        /// the one change that closes both views, and it costs one predicate per lot once.
        /// </para>
        /// <para>
        /// A layout with no map cannot be asked, and
        /// <see cref="SimitoneSurroundingsPlacement.GroundIsDrawnUnder"/> refuses a null map, so a
        /// map-less caller keeps every lot rather than losing its whole neighbourhood to a guard
        /// that had nothing to check against.
        /// </para>
        /// </remarks>
        /// <param name="layout">The layout the caller handed over.</param>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <summary>
        /// How many facades this pass will ever attempt: <see cref="NeighbourhoodFacadePlan.Eligible"/>
        /// over the layout it builds for, which has the groundless lots taken out. The neighbourhood
        /// view's build fraction has to use this denominator, not the whole layout's, or a lot the
        /// pass will never build counts as missing (audit O1: "16 of 41" was 16 of the 17 it builds).
        /// </summary>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        public int EligibleCount(int? activeLot)
        {
            return NeighbourhoodFacadePlan.Eligible(_layout, activeLot);
        }

        private NeighbourhoodLayout WithoutGroundlessLots(
            NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            if (layout == null || map == null) return layout;
            var kept = new List<NeighbourhoodLot>(layout.Lots.Count);
            var dropped = new List<short>();
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var lot = layout.Lots[i];
                if (lot != null && !SimitoneSurroundingsPlacement.GroundIsDrawnUnder(map, lot))
                {
                    dropped.Add(lot.Number);
                    continue;
                }
                kept.Add(lot);
            }
            if (dropped.Count == 0) return layout;
            dropped.Sort();
            _log.Add(dropped.Count + " lot(s) stand on ground the map never draws and are shown by"
                + " neither a house nor a lot: " + string.Join(", ", dropped)
                + ". Their base tiles are outside the ground the mesh draws.");
            return new NeighbourhoodLayout(layout.Slot, layout.Seed, layout.GeneratorVersion,
                layout.SchemaVersion, layout.MapSize, kept);
        }

        /// <summary>
        /// Takes over the meshes the last visit of this neighbourhood built, for every lot whose
        /// house file, side and facing have not changed since.
        /// </summary>
        /// <remarks>
        /// This is what makes a second visit free rather than a second eleven-second freeze, and it
        /// is the same rule as the per-visit cache: the key is
        /// <see cref="NeighbourhoodLot.HouseHash"/>, so a bulldozed-and-rebuilt lot is adopted on
        /// its old hash only if that hash still matches, and a lot whose file has moved is left
        /// for the build queue.
        ///
        /// <para><b>A lot that has moved is adopted and moved, not rebuilt.</b> The placement is
        /// not part of the render, so <see cref="NeighbourhoodFacadeMeshCache.Adoptable"/> does not
        /// compare positions and this re-places the adopted mesh instead - see
        /// <see cref="NeighbourhoodFacadeMeshCache.Reposition"/>. Comparing them here is what made
        /// Part 8's one-off change of the placement rule cost twenty seconds of re-rendering on
        /// every visit afterwards, with every lot rebuilt one per frame because a facade build costs
        /// seconds. A mesh that cannot be re-placed is dropped and the lot goes back to the build
        /// queue, which is the answer the cache gave before.</para>
        /// </remarks>
        /// <param name="layout">The packed layout, or <c>null</c>.</param>
        /// <param name="map">The generated map, for the lot's ground height.</param>
        private void AdoptShared(NeighbourhoodLayout layout, NeighbourhoodMap map)
        {
            if (layout == null) return;
            for (int i = 0; i < layout.Lots.Count; i++)
            {
                var lot = layout.Lots[i];
                if (!NeighbourhoodFacadePlan.ShouldDraw(lot, null, _subWorldLots)) continue;
                var facade = NeighbourhoodFacadeMeshCache.Take(lot);
                if (facade == null) continue;
                if (!NeighbourhoodFacadeMeshCache.Reposition(facade, lot, map, _device))
                {
                    //Take already moved it out of the store, so this cache is its only owner
                    facade.Dispose();
                    continue;
                }
                //this cache owns it now; Dispose hands it back to the store on the lot's new plot
                _cache[lot.Number] = facade;
                _hashes[lot.Number] = lot.HouseHash;
            }
        }

        /// <summary>The hash each cached (or failed) facade was built from.</summary>
        public IReadOnlyDictionary<short, uint> CachedHashes
        {
            get { return _hashes; }
        }

        /// <summary>
        /// Drops every cached mesh whose lot file has changed, or that the layout no longer
        /// mentions. Called when the layout is rebuilt, which is when a bulldoze or a rezone shows
        /// up.
        /// </summary>
        public int DropStale()
        {
            var stale = NeighbourhoodFacadePlan.Stale(_layout, _hashes);
            for (int i = 0; i < stale.Count; i++)
            {
                Drop(stale[i]);
            }
            if (stale.Count > 0)
            {
                _log.Add("dropped " + stale.Count + " stale facade(s): " + string.Join(", ", stale));
            }
            return stale.Count;
        }

        private void Drop(short lotNumber)
        {
            NeighbourhoodFacade facade;
            if (_cache.TryGetValue(lotNumber, out facade))
            {
                facade.Dispose();
                _cache.Remove(lotNumber);
            }
            //and out of the cross-visit store: a mesh the layout no longer wants must not be
            //handed to the next visit of this neighbourhood as though it were still current
            NeighbourhoodFacadeMeshCache.Forget(lotNumber);
            _hashes.Remove(lotNumber);
            _failures.Remove(lotNumber);
        }

        /// <summary>
        /// Builds up to <see cref="BuildsPerFrame"/> facades. Safe to call every frame; it does
        /// nothing once every drawable lot has been attempted.
        /// </summary>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="focus">The world tile the camera is looking at.</param>
        /// <returns>How many meshes this call built.</returns>
        public int BuildStep(int? activeLot, Vector2 focus)
        {
            //This is the once-a-frame entry point, so the frame's count and clock restart here.
            //Without it the per-frame count (_frameBuilt) was never reset for a caller that does
            //not own the frame loop (SimitoneSurroundings), and the cache built exactly one lot
            //for the life of the instance.
            BeginFrame();
            return Step(activeLot, focus).Built;
        }

        /// <summary>
        /// How long a frame may spend building facades, in milliseconds. Zero or less removes the
        /// wall-clock limit and leaves only <see cref="BuildsPerFrame"/>.
        /// </summary>
        public double BuildTimeBudgetMs { get; set; }

        /// <summary>
        /// How much of this frame's building time has already been spent, in milliseconds. The
        /// owner sets it at the start of its frame; it exists so a caller that never calls
        /// <see cref="Step"/> still resets the clock.
        /// </summary>
        public void BeginFrame()
        {
            _frameStart = Stopwatch.GetTimestamp();
            _frameBuilt = 0;
        }

        private double ElapsedMs()
        {
            return (Stopwatch.GetTimestamp() - _frameStart) * 1000d / Stopwatch.Frequency;
        }

        /// <summary>
        /// Builds up to <see cref="BuildsPerFrame"/> facades, and stops early if this frame has
        /// already spent <see cref="BuildTimeBudgetMs"/> doing it. Returns a report rather than a
        /// count, so a caller can log a failed pass without re-walking the layout to find out
        /// whether anything happened.
        /// </summary>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="focus">The world tile the camera is looking at.</param>
        public NeighbourhoodFacadeStepResult Step(int? activeLot, Vector2 focus)
        {
            if (_device == null || _layout == null)
            {
                return NeighbourhoodFacadeStepResult.Nothing;
            }
            _activeLot = activeLot;

            //Two gates, in the order that can refuse the frame cheapest. Both of them are the rules
            //the plan holds, called with the state only this class has. There used to be a third -
            //"has the pass given up" - and there deliberately is not any more: a build that cost
            //more than MaxBuildMs sets a flag that the pass reports and never obeys, so the only
            //things that can hold a frame back are the per-frame count and the delay between
            //builds, both of which are bounded. See RecordCost.
            if (_cooldownFrames > 0)
            {
                //the previous build cost more than a whole frame's worth; sit this one out
                _cooldownFrames--;
                return new NeighbourhoodFacadeStepResult(0, 0, Outstanding(activeLot, focus), false);
            }
            var allowance = NeighbourhoodFacadePlan.FrameAllowance(
                BuildsPerFrame, BuildTimeBudgetMs, _frameBuilt, ElapsedMs());
            if (allowance <= 0)
            {
                return new NeighbourhoodFacadeStepResult(0, 0, Outstanding(activeLot, focus), false);
            }

            var wanted = NeighbourhoodFacadePlan.ToBuild(
                _layout, _hashes, activeLot, focus, allowance, BuildRadiusTiles, _subWorldLots);
            var built = 0;
            var failed = 0;
            for (int i = 0; i < wanted.Count; i++)
            {
                var lot = wanted[i];
                string reason;
                NeighbourhoodFacadeTimings timings;
                NeighbourhoodFacade facade;
                try
                {
                    facade = NeighbourhoodFacadeBuilder.TryBuild(_device, lot, _map, out reason, out timings);
                }
                catch (Exception ex)
                {
                    //TryBuild already says it never throws. This is the last line between one
                    //lot's bad day and a frame that never finishes, and it costs nothing.
                    facade = null;
                    timings = NeighbourhoodFacadeTimings.None;
                    reason = ex.GetType().Name + ": " + ex.Message;
                }
                //what the build cost, whatever it produced. This is the line that names the
                //stage: the eleven seconds of silence this replaces had no attribution at all.
                RecordCost(lot, timings);
                if (facade == null)
                {
                    //once per lot, to the game's log, and not just to our own list. The list is
                    //read by nobody, which is how a neighbourhood of houses that all failed to
                    //build managed to look in the log like a neighbourhood that had not started.
                    if (!_failures.ContainsKey(lot.Number))
                    {
                        var line = timings.Describe(lot.Number) + " - " + reason;
                        _log.Add("lot " + lot.Number + " has no facade: " + reason);
                        _log.Add(line);
                        SimitoneLog.Warn("Neighbourhood " + lot.Number + " has no facade: " + reason);
                        if (timings.Measured) SimitoneLog.Info("  " + line);
                    }
                    _failures[lot.Number] = reason;
                    failed++;
                }
                else
                {
                    //a rebuild replaces the old mesh rather than leaking it
                    NeighbourhoodFacade previous;
                    if (_cache.TryGetValue(lot.Number, out previous)) previous.Dispose();
                    _cache[lot.Number] = facade;
                    //not stored across visits yet: this cache is the single owner until Dispose
                    //hands the mesh to NeighbourhoodFacadeMeshCache
                    built++;
                }
                //either way the hash is recorded, so a lot that cannot be built is not retried
                _hashes[lot.Number] = lot.HouseHash;
                _frameBuilt++;
            }

            var remaining = Outstanding(activeLot, focus);
            return new NeighbourhoodFacadeStepResult(built, failed, remaining, remaining == 0);
        }

        /// <summary>
        /// Notes what one build cost, spaces the next one out by it, and says out loud when the
        /// cost was over the per-frame stall ceiling.
        /// </summary>
        /// <remarks>
        /// <para>Every build, successful or not, goes through here - a build that threw is exactly
        /// the one whose cost needs recording, and it is the one whose reason the player needs
        /// most.</para>
        ///
        /// <para><b>Two lines for a build that broke the ceiling, and no stopping.</b> The
        /// per-lot timing line is emitted whatever the cost and is <em>marked</em> when the cost
        /// was over <see cref="MaxBuildMs"/>, so that "which frames were held up" is answerable
        /// from the log rather than inferred from a stutter. The first breach additionally gets a
        /// warning that names the policy - one build a frame, at most
        /// <see cref="MaxCooldownFrames"/> frames apart, no stop - because a player who has
        /// watched one house appear every couple of seconds is owed to be told that this is
        /// deliberate and that the cost is being reported per house. The pass used to stop
        /// instead, and that is the bug this method was rewritten for: a stop is indistinguishable
        /// from a hang, and it turns "this machine is slow" into "this view has one house in
        /// it".</para>
        /// </remarks>
        private void RecordCost(NeighbourhoodLot lot, NeighbourhoodFacadeTimings timings)
        {
            if (!timings.Measured) return;
            _lastBuildMs = timings.TotalMs;

            //The one decision: how long to wait, and whether to tell the player this one was over
            //the line. Pure, so the policy is asserted in the test suite rather than trusted.
            var pace = NeighbourhoodFacadePlan.DecideAfterBuild(
                timings.TotalMs, BuildTimeBudgetMs, MaxBuildMs, MaxCooldownFrames);
            _cooldownFrames = pace.Frames;
            _overCeiling = pace.OverCeiling;

            //logged once per lot, at the level the player's own log uses. A fifty-lot
            //neighbourhood is fifty lines, which is the point: one line per house is how the next
            //run can say "the walls are four seconds" rather than "the neighbourhood was slow".
            //and the over-ceiling builds are marked on the same line, which is what makes "every
            //frame that was held too long is in the log" a fact about the log rather than a hope
            var line = timings.Describe(lot.Number)
                + (pace.OverCeiling ? OverCeilingMark(MaxBuildMs) : string.Empty);
            _log.Add(line);
            SimitoneLog.Info(line);

            if (!pace.OverCeiling) return;
            if (_ceilingLogged) return;
            _ceilingLogged = true;
            var eligible = NeighbourhoodFacadePlan.Eligible(_layout, _activeLot, _subWorldLots);
            //clamped, because the active lot is excluded from "eligible" and can be in the cache
            //from before the player walked into it - and a progress line that says "-1 still to
            //build" is worse than one that says nothing
            var owed = Math.Max(0, eligible - _cache.Count);
            var why = "each lot's facade costs " + NeighbourhoodFacadeTimings.Milliseconds(timings.TotalMs)
                + " (most of it in " + NeighbourhoodFacadeTimings.DescribeStage(timings.Dominant)
                + "), which is over the " + NeighbourhoodFacadeTimings.Milliseconds(MaxBuildMs)
                + " a frame may be stalled for. That cost cannot be shortened from here - a build"
                + " cannot be abandoned half way - so the pass is not stopping: it builds at most "
                + BuildsPerFrame + " lot(s) a frame, waits at most " + Math.Max(1, pace.Frames + 1)
                + " frame(s) between them, and marks every lot's line with \""
                + OverCeilingMark(MaxBuildMs).Trim() + "\" so the stalls are accounted for. The"
                + " neighbourhood will fill in; the cost is a stall per house until it does."
                + " Reached: " + FacadeSummary(_cache.Count, _failures.Count, owed, eligible) + ".";
            //the progress is on the same line as the policy, because a player who has been told
            //the pass is going to keep stalling deserves to know how much of the street is done
            //at the moment they were told
            _log.Add(why);
            SimitoneLog.Warn("Neighbourhood facades: " + why);
        }

        /// <summary>
        /// The marker a timing line carries when its build was over the per-frame stall ceiling.
        /// </summary>
        /// <remarks>
        /// Its own method so that the line and the warning that explains it cannot spell it
        /// differently: a log that marks the stalls one way and describes them another is a log
        /// whose stalls have to be found by reading. Pure, and asserted as such.
        /// </remarks>
        /// <param name="maxBuildMs">The ceiling, in milliseconds.</param>
        public static string OverCeilingMark(double maxBuildMs)
        {
            return " [over the " + NeighbourhoodFacadeTimings.Milliseconds(maxBuildMs)
                + " a frame may be stalled for]";
        }

        /// <summary>Whether a lot has a mesh to draw.</summary>
        /// <param name="lotNumber">The lot.</param>
        public bool Has(short lotNumber)
        {
            return _cache.ContainsKey(lotNumber);
        }

        /// <summary>The lot's mesh, or <c>null</c>.</summary>
        /// <param name="lotNumber">The lot.</param>
        public NeighbourhoodFacade Get(short lotNumber)
        {
            NeighbourhoodFacade facade;
            return _cache.TryGetValue(lotNumber, out facade) ? facade : null;
        }

        /// <summary>How many lots have been attempted and are still waiting for a mesh.</summary>
        /// <param name="activeLot">The lot the player is standing in, or <c>null</c>.</param>
        /// <param name="focus">The world tile the camera is looking at.</param>
        public int Outstanding(int? activeLot, Vector2 focus)
        {
            return NeighbourhoodFacadePlan.CountToBuild(_layout, _hashes, activeLot, _subWorldLots);
        }

        /// <summary>
        /// Triangles the last <c>Draw</c> submitted, across every surface of every visible facade.
        /// </summary>
        /// <remarks>
        /// Reset first on every call, so a frame that returns early reports zero rather than the
        /// previous frame's number. It is on the frame report because the facades are half the
        /// answer to "is the world carrying the picture", and a bool that says "there were facades
        /// cached" is not the same sentence as "the facades submitted 4,128 triangles".
        /// </remarks>
        public int DrawnPrimitives
        {
            get { return _drawnPrimitives; }
        }

        /// <summary>Draw calls the last <c>Draw</c> issued, one per surface.</summary>
        public int DrawnSurfaces
        {
            get { return _drawnSurfaces; }
        }

        /// <summary>Whether the last <c>Draw</c> got as far as submitting anything at all.</summary>
        public bool DrawRan
        {
            get { return _drawRan; }
        }

        /// <summary>
        /// Draws every cached facade inside <paramref name="frustum"/>.
        /// </summary>
        /// <param name="vertexShader">The city's <c>VerShader</c>.</param>
        /// <param name="pixelShader">The city's <c>PixShader</c>.</param>
        /// <param name="viewProjection">The combined camera matrix.</param>
        /// <param name="lightColor">The light's colour and strength.</param>
        /// <param name="fogColor">The colour the far edge of the map fades into.</param>
        /// <param name="fogDistance">How far away the fade completes, in tiles.</param>
        /// <param name="frustum">What is on screen.</param>
        /// <returns>How many facades were drawn.</returns>
        /// <remarks>
        /// Hands <c>MV</c> the view-projection, which makes the fog measure a length in clip space
        /// and therefore come out near zero. Kept so an existing caller still compiles; the
        /// overload below is the one to call.
        /// </remarks>
        public int Draw(
            Effect vertexShader, Effect pixelShader, Matrix viewProjection,
            Vector4 lightColor, Vector4 fogColor, float fogDistance, BoundingFrustum frustum)
        {
            return Draw(vertexShader, pixelShader, viewProjection, viewProjection,
                lightColor, fogColor, fogDistance, frustum);
        }

        /// <summary>
        /// Draws every cached facade inside <paramref name="frustum"/>, with the view given
        /// separately from the view-projection so the fog measures a real distance.
        /// </summary>
        /// <param name="vertexShader">The city's <c>VerShader</c>.</param>
        /// <param name="pixelShader">The city's <c>PixShader</c>.</param>
        /// <param name="viewProjection">World to clip space, for <c>BaseMatrix</c>.</param>
        /// <param name="view">
        /// World to view space, for <c>MV</c> and the fog distance. This is the same distinction
        /// the terrain and the trees get, and the same reason: <c>CityObjFogVS</c> fills
        /// <c>vPos</c> with <c>mul(position, MV)</c> and <c>PixShader</c>'s
        /// <c>CityObjPSFog</c> fades by <c>min(1, length(vPos) / FogMaxDist)</c>, which is a
        /// distance from the camera only when <c>MV</c> is the view. Handed the view-projection
        /// instead, a house on the far side of the map draws as a flat sheet of
        /// <c>FogColor</c> - visible, and wrong, which is the worst of the two.
        /// </param>
        /// <param name="lightColor">The light's colour and strength.</param>
        /// <param name="fogColor">The colour the far edge of the map fades into.</param>
        /// <param name="fogDistance">How far away the fade completes, in tiles.</param>
        /// <param name="frustum">What is on screen, or <c>null</c> for no culling.</param>
        /// <returns>How many facades were drawn.</returns>
        public int Draw(
            Effect vertexShader, Effect pixelShader, Matrix viewProjection, Matrix view,
            Vector4 lightColor, Vector4 fogColor, float fogDistance, BoundingFrustum frustum)
        {
            //Reset first, so a frame that returns early reports zero rather than the previous
            //frame's numbers. A stale count is worse than none: it is a frame that submitted
            //nothing being described as one that submitted a street of houses.
            _drawnPrimitives = 0;
            _drawnSurfaces = 0;
            _drawRan = false;

            if (_cache.Count == 0) return 0;
            if (vertexShader == null || pixelShader == null) return 0;

            var vs = vertexShader.Techniques[NeighbourhoodShaderState.CityObjectTechnique];
            var ps = pixelShader.Techniques[NeighbourhoodShaderState.CityObjectTechnique];
            if (vs == null || ps == null)
            {
                if (!_shaderMissingLogged)
                {
                    _shaderMissingLogged = true;
                    SimitoneLog.Warn("The lot facades cannot draw: the city effects do not carry"
                        + " the '" + NeighbourhoodShaderState.CityObjectTechnique
                        + "' technique this cache needs, so no lot is drawn and the neighbourhood"
                        + " shows terrain without houses. The trees are on the same technique and are"
                        + " missing for the same reason.");
                }
                return 0;
            }

            //Both passes are FinalFog, and both are applied before every surface rather than once
            //for the frame. Three reasons, in the order they matter:
            //  - the pixel shader reads ObjTex, which is a different texture per surface, and a
            //    parameter only reaches the pipeline through a pass apply;
            //  - the vertex shader has to be re-bound too, because nothing in this loop leaves it
            //    bound on its own and the next draw's input layout is built against whatever is
            //    bound at the moment. MonoGame does not validate this, so a version of this loop
            //    that relied on the previous draw to have left the right shader up is a draw that
            //    fails on some other machine and not on this one;
            //  - it costs a few hundred Apply calls a frame for a few dozen visible lots, against
            //    a draw that throws every time and takes the whole neighbourhood's terrain down with
            //    it, since the facades are submitted first.
            const int objectVertexPass = NeighbourhoodShaderState.FogPass;
            const int objectPixelPass = NeighbourhoodShaderState.FogPass;
            if (objectVertexPass >= vs.Passes.Count || objectPixelPass >= ps.Passes.Count) return 0;

            vertexShader.CurrentTechnique = vs;
            pixelShader.CurrentTechnique = ps;
            vertexShader.Parameters["BaseMatrix"].SetValue(viewProjection);
            //View space, not clip space - see the remarks above.
            vertexShader.Parameters["MV"].SetValue(view);
            vertexShader.Parameters["ObjModel"].SetValue(Matrix.Identity);
            vertexShader.Parameters["DepthBias"].SetValue(0f);
            pixelShader.Parameters["LightCol"].SetValue(lightColor);
            pixelShader.Parameters["ShadowMult"].SetValue(1f);
            pixelShader.Parameters["FogColor"].SetValue(fogColor);
            pixelShader.Parameters["FogMaxDist"].SetValue(fogDistance > 1f ? fogDistance : 1f);

            var device = _device;
            if (device == null) return 0;
            var previousBlend = device.BlendState;
            var previousRast = device.RasterizerState;
            var previousDepth = device.DepthStencilState;
            //the sampler too, as the tree pass does: LinearClamp is set below and was left behind
            var previousSampler = device.SamplerStates[0];
            var drawn = 0;
            try
            {
                //walls carry transparency (a facade is a render of a real lot, so the parts
                //behind it show through), and the city graphics are non-premultiplied
                device.BlendState = BlendState.NonPremultiplied;
                device.RasterizerState = RasterizerState.CullNone;
                device.DepthStencilState = DepthStencilState.Default;
                device.SamplerStates[0] = SamplerState.LinearClamp;

                _drawRan = true;
                foreach (var pair in _cache)
                {
                    var facade = pair.Value;
                    //B2: a lot the bridge stood up as a real sub-world is not drawn as a facade
                    //as well. The mesh may well be in the cache - it can have been built
                    //before the plan arrived - and submitting it would draw the neighbour twice.
                    if (IsWithheld(pair.Key)) continue;
                    if (facade == null || !facade.HasGeometry) continue;
                    if (frustum != null && !facade.Bounds.Intersects(frustum)) continue;
                    drawn++;
                    for (int s = 0; s < facade.Surfaces.Length; s++)
                    {
                        //IsDrawable, not IsEmpty: a surface with a mesh and no texture is not
                        //degraded, it is an unbound sampler, and that draws whatever the last bind
                        //left behind over whatever was already on screen
                        var surface = facade.Surfaces[s];
                        if (surface == null || !surface.IsDrawable) continue;
                        pixelShader.Parameters["ObjTex"].SetValue(surface.Texture);
                        //Apply, every surface, and this is the whole fix: assigning
                        //CurrentTechnique and setting the parameters reaches nothing at all, so
                        //without this the surface is submitted against whatever vertex shader the
                        //previous draw left bound. See the remarks above.
                        vs.Passes[objectVertexPass].Apply();
                        ps.Passes[objectPixelPass].Apply();
                        device.SetVertexBuffer(surface.VertexBuffer);
                        device.Indices = surface.IndexBuffer;
                        try
                        {
                            device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, surface.PrimitiveCount);
                        }
                        catch (Exception ex)
                        {
                            //Named once, then rethrown: the caller's guard decides whether the
                            //painted map goes back, and it has to keep doing that. What the guard
                            //cannot do is say why, and this error names a mismatch without saying
                            // what it was compared against - which is the part that matters, since
                            // the two ways this draw can be wrong look identical from the outside.
                            LogDrawFailureOnce(surface, ex);
                            throw;
                        }
                        _drawnPrimitives += surface.PrimitiveCount;
                        _drawnSurfaces++;
                    }
                }
            }
            finally
            {
                device.BlendState = previousBlend;
                device.RasterizerState = previousRast;
                device.DepthStencilState = previousDepth;
                device.SamplerStates[0] = previousSampler;
            }
            return drawn;
        }

        private int _drawnPrimitives;
        private int _drawnSurfaces;
        private bool _drawRan;
        private bool _shaderMissingLogged;
        private bool _drawFailureLogged;

        /// <summary>
        /// Explains a failed facade draw once, naming the shader it is on and the declaration it is
        /// being validated against.
        /// </summary>
        private void LogDrawFailureOnce(NeighbourhoodFacadeSurface surface, Exception ex)
        {
            if (_drawFailureLogged) return;
            _drawFailureLogged = true;

            SimitoneLog.Error("A lot facade's draw threw while submitting "
                + surface.PrimitiveCount + " triangle(s): " + ex.Message
                + " The device builds its input layout from the surface's vertex declaration ("
                + NeighbourhoodFacadeVertex.DescribeDeclaration()
                + ", " + NeighbourhoodFacadeVertex.SizeInBytes
                + " bytes) against whichever vertex shader is bound, and only EffectPass.Apply binds"
                + " one. This draw is on Effects/VerShader's '"
                + NeighbourhoodShaderState.CityObjectTechnique + "' pass "
                + objectPassDescription
                + " - CityObjFogVS, whose input is " + NeighbourhoodFacadeVertex.ObjectInputStruct
                + ": a float4 position, a float2 texture coordinate and a float3 in TEXCOORD1, which"
                + " is what the declaration above provides. There are exactly two ways this draw"
                + " fails and they produce this same message: a pass that was not applied before"
                + " the submission, leaving the previous draw's shader bound, and a surface whose"
                + " vertex buffer was uploaded in a layout that does not carry all three of those"
                + " elements - a VertexPositionTexture has a position and a texture coordinate and"
                + " no TEXCOORD1, and is the layout this draw used to fail with every frame. The"
                + " facades are submitted before the terrain, so this exception takes the terrain"
                + " down with it and the whole world stays blank.");
        }

        /// <summary>
        /// The <c>RenderCityObj</c> pass index the facades are on, named for the log.
        /// </summary>
        private const string objectPassDescription = "3 (FinalFog)";

        public void Dispose()
        {
            //Handed to the cross-visit store rather than freed, so the next visit of this
            //neighbourhood adopts them instead of paying seconds per lot to render them all
            //again. Store is a no-op for a mesh it already holds, so a lot built and rebuilt
            //within the visit is stored once, under the hash it was finally built from.
            foreach (var pair in _cache)
            {
                //under the hash this cache built or adopted it for, which is what a later Take compares
                NeighbourhoodFacadeMeshCache.Store(pair.Value,
                    _hashes.TryGetValue(pair.Key, out var hash) ? hash : pair.Value.HouseHash);
            }
            _cache.Clear();
            _hashes.Clear();
            _failures.Clear();
            _log.Clear();
        }
    }

    /// <summary>
    /// The facade meshes built so far this session, kept across visits to a neighbourhood.
    ///
    /// <para><b>Why it exists.</b> A facade is a render of one lot's <c>HouseNN.iff</c>, and
    /// rendering one costs seconds on the game thread. The per-visit cache throws all of that
    /// away when the player closes the map, so the second visit to a neighbourhood paid the
    /// eleven-second freeze all over again for exactly the same houses. This is the store the
    /// closed view hands its meshes to, and the opened view takes them from.</para>
    ///
    /// <para><b>Keyed by the house file's own fingerprint, plus what can invalidate the render
    /// itself</b> - the lot's side and its facing - so a lot whose file has been edited is not
    /// adopted: it fails the hash check, stays in the build queue, and is re-rendered. Two lots
    /// that happen to share a house file are still two entries, keyed by lot number. <b>The plot a
    /// lot stands on is not in the key</b>, because it is not part of the render: see
    /// <see cref="Adoptable"/> and <see cref="Reposition"/>.</para>
    ///
    /// <para><b>It holds GPU resources, so it is bounded and it survives nothing but the
    /// device.</b> A wall texture is a render target's pixels and a lot's mesh is a pair of
    /// buffers; a few dozen lots is a few dozen megabytes, which is affordable and a few hundred
    /// is not, so the store evicts least-recently-used down to
    /// <see cref="DefaultCapacity"/> entries. A device reset frees every one of them underneath
    /// us, so the store subscribes to <c>GraphicsDevice.DeviceReset</c> and empties itself -
    /// which is why it does not need anything from the view to be told, and why a minimise
    /// followed by a return to the map rebuilds the facades rather than drawing dead buffers.</para>
    /// </summary>
    public static class NeighbourhoodFacadeMeshCache
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<short, NeighbourhoodFacade> Meshes =
            new Dictionary<short, NeighbourhoodFacade>();
        private static readonly Dictionary<short, uint> Hashes = new Dictionary<short, uint>();
        private static readonly Dictionary<short, NeighbourhoodLot> Plots =
            new Dictionary<short, NeighbourhoodLot>();
        private static readonly LinkedList<short> Recency = new LinkedList<short>();
        private static GraphicsDevice _subscribed;
        private static EventHandler<EventArgs> _reset;

        /// <summary>
        /// How many meshes the store keeps. A neighbourhood is a few dozen lots; this is a few
        /// neighbourhoods' worth, and the point is to survive closing and reopening the map, not
        /// to hold every lot the player has ever looked at.
        /// </summary>
        public const int DefaultCapacity = 128;

        /// <summary>How many meshes the store is currently holding.</summary>
        public static int Count
        {
            get { lock (Gate) { return Meshes.Count; } }
        }

        /// <summary>
        /// Whether a stored mesh may be re-used for a lot as the layout describes it now: the house
        /// file's fingerprint has not moved, and neither has anything about the house itself - its
        /// size or its facing.
        /// </summary>
        /// <remarks>
        /// <para><b>The plot is deliberately not part of this key.</b> A facade mesh is a render of
        /// one lot's walls, roof and floor, and that render happens in the lot's own local space;
        /// where the lot stands in the generated world is a separate, cheap step applied on top of
        /// it (<see cref="Reposition"/>). Keying the cache on the position therefore threw away a
        /// mesh that was still perfectly good and bought nothing: every lot that moved was
        /// re-rendered on the game thread, at seconds each, one per frame, which is the twenty-second
        /// freeze a change to the placement rule caused. Part 8 moved every lot at once, so the whole
        /// neighbourhood rebuilt on every visit.
        ///
        /// <para>What is left is what can actually invalidate a <em>render</em>: the house file
        /// behind the house hash, the lot's own side (the activator builds a
        /// <c>Size x Size</c> blueprint, so a different size is a different house) and its facing.
        /// A lot re-zoned onto another plot is still adoptable, and
        /// <see cref="Reposition"/> is what puts it on the plot it now owns.
        ///
        /// <para>Pure, and the one part of the cross-visit cache that can be asserted without a
        /// graphics device: the rule is three comparisons over three integers.</para>
        /// </remarks>
        /// <param name="stored">The lot the mesh was built for, as it was then.</param>
        /// <param name="current">The same lot as the layout describes it now.</param>
        public static bool Adoptable(NeighbourhoodLot stored, NeighbourhoodLot current)
        {
            if (stored == null || current == null) return false;
            if (stored.HouseHash != current.HouseHash) return false;
            if (stored.Size != current.Size) return false;
            if (stored.Rotation != current.Rotation) return false;
            return true;
        }

        /// <summary>
        /// Moves a facade that was built for one plot onto the plot its lot now owns, in place, and
        /// whether it worked.
        ///
        /// <para><b>This is the step that lets <see cref="Adoptable"/> ignore the position.</b> A
        /// facade's vertices are already in world tiles - they were placed by
        /// <see cref="NeighbourhoodFacadeTransform.PlacedVertices"/> when it was built - so a mesh
        /// adopted for a lot that has moved would otherwise be drawn at its old address, which is
        /// the "floating houses" defect in a new place. Moving one is a translation of every vertex
        /// by the difference between the two placements plus a rewrite of the vertex buffer: a few
        /// thousand vertex writes and one upload, against the seconds a re-render costs, and the
        /// only expensive part of a facade - standing a <c>World</c> and a <c>VM</c> up and
        /// rasterising the lot - is avoided entirely.</para>
        ///
        /// <para><b>Nothing is half-moved.</b> The new vertex lists are built and checked before any
        /// buffer is touched, and a refusal leaves the mesh exactly as it was; the caller is expected
        /// to dispose it and let the lot rebuild, which is the old behaviour and is always correct.
        /// </para>
        ///
        /// <para><b>The ground height is part of the placement, not part of the house.</b> It is
        /// read from the generated map at the lot's new corner by the same
        /// <see cref="NeighbourhoodFacadeTransform.GroundHeightFor"/> the builder used, so a facade
        /// that moves uphill is lifted with the hill instead of being left standing in the air.
        /// </para>
        /// </summary>
        /// <param name="facade">The adopted mesh, or <c>null</c>.</param>
        /// <param name="lot">The lot as the layout describes it now.</param>
        /// <param name="map">The generated map, for the ground height under the new plot.</param>
        /// <param name="device">The device the mesh's buffers live on, or <c>null</c>.</param>
        /// <returns>Whether the mesh now stands where <paramref name="lot"/> does.</returns>
        public static bool Reposition(
            NeighbourhoodFacade facade, NeighbourhoodLot lot, NeighbourhoodMap map, GraphicsDevice device)
        {
            if (facade == null || lot == null || facade.Lot == null) return false;

            var ground = NeighbourhoodFacadeTransform.GroundHeightFor(map, lot);
            var dx = lot.WorldX - facade.Lot.WorldX;
            var dy = lot.WorldY - facade.Lot.WorldY;
            var dh = ground - facade.GroundHeight;

            //the common case, and worth not touching a buffer for: same plot, same ground
            if (dx == 0 && dy == 0 && Math.Abs(dh) < 1e-6f)
            {
                facade.Lot = lot;
                facade.GroundHeight = ground;
                facade.Bounds = NeighbourhoodFacadeTransform.BoundsFor(lot, ground);
                return true;
            }

            var surfaces = facade.Surfaces;
            if (device == null || surfaces == null || surfaces.Length == 0) return false;

            //everything built and checked before anything is committed, so a refusal cannot leave
            //half the lot on the new plot
            var moved = new VertexPositionTexture[surfaces.Length][];
            var uploaded = new DGRP3DVert[surfaces.Length][];
            for (int i = 0; i < surfaces.Length; i++)
            {
                var surface = surfaces[i];
                if (surface == null || surface.Vertices == null) continue;
                if (surface.VertexBuffer == null || surface.IndexBuffer == null) return false;
                var placed = Shift(surface.Vertices, dx, dh, dy);
                if (placed == null) return false;
                var objectVertices = NeighbourhoodFacadeVertex.ToObjectVertices(placed, surface.Indices);
                if (objectVertices == null) return false;
                moved[i] = placed;
                uploaded[i] = objectVertices;
            }

            try
            {
                for (int i = 0; i < surfaces.Length; i++)
                {
                    if (uploaded[i] == null) continue;
                    surfaces[i].VertexBuffer.SetData(uploaded[i]);
                    surfaces[i].Vertices = moved[i];
                }
            }
            catch (Exception)
            {
                //a device that has just been reset refuses the upload. The mesh is not usable and
                //the caller drops it, which puts the lot back in the build queue - the answer the
                //cache gave before any of this.
                return false;
            }

            facade.Lot = lot;
            facade.GroundHeight = ground;
            facade.Bounds = NeighbourhoodFacadeTransform.BoundsFor(lot, ground);
            return true;
        }

        private static VertexPositionTexture[] Shift(
            VertexPositionTexture[] source, float dx, float dh, float dz)
        {
            if (source == null) return null;
            var moved = new VertexPositionTexture[source.Length];
            for (int i = 0; i < source.Length; i++)
            {
                var vertex = source[i];
                moved[i] = new VertexPositionTexture(
                    new Vector3(
                        vertex.Position.X + dx,
                        vertex.Position.Y + dh,
                        vertex.Position.Z + dz),
                    vertex.TextureCoordinate);
            }
            return moved;
        }

        /// <summary>
        /// Puts a mesh in the store, keyed by its lot and the hash it was built from. A null
        /// mesh, or one with no lot, is ignored rather than stored as an empty entry that a
        /// later visit would adopt and draw nothing for.
        /// </summary>
        /// <param name="facade">The mesh, or <c>null</c>.</param>
        public static void Store(NeighbourhoodFacade facade)
        {
            if (facade?.Lot == null) return;
            Store(facade, facade.HouseHash);
        }

        /// <summary>
        /// Puts a mesh in the store under an explicit hash, which is what a mesh whose house file
        /// has since changed must be stored as - so a later visit compares against the file it
        /// would be adopted by, not against one it no longer matches.
        /// </summary>
        /// <param name="facade">The mesh, or <c>null</c>.</param>
        /// <param name="houseHash">The hash the mesh was built from.</param>
        public static void Store(NeighbourhoodFacade facade, uint houseHash)
        {
            if (facade?.Lot == null) return;
            var number = facade.Lot.Number;
            lock (Gate)
            {
                if (Meshes.TryGetValue(number, out var previous) && !ReferenceEquals(previous, facade))
                {
                    //a rebuild of a lot already in the store frees the mesh it replaces, or the
                    //store grows one dead mesh per edit
                    previous.Dispose();
                }
                Meshes[number] = facade;
                Hashes[number] = houseHash;
                Plots[number] = facade.Lot;
                Touch(number);
                Evict();
            }
        }

        /// <summary>
        /// Takes the mesh for a lot, if the store holds one that
        /// <see cref="Adoptable"/> says is still current. Ownership passes to the caller, which is
        /// now the only thing that can free it.
        /// </summary>
        /// <param name="lot">The lot as the layout describes it now.</param>
        public static NeighbourhoodFacade Take(NeighbourhoodLot lot)
        {
            return Take(lot, AnyDrawable);
        }

        /// <summary>
        /// <see cref="Take(NeighbourhoodLot)"/> with the "is the mesh still usable" rule supplied,
        /// so the ownership hand-off can be asserted without a graphics device.
        /// </summary>
        /// <remarks>
        /// The mesh leaves the store when it is handed over: a single owner at a time, so two
        /// live <c>NeighbourhoodFacades</c> can never both hold (and one dispose under the other)
        /// the same buffers. The owner hands it back with <see cref="Store(NeighbourhoodFacade, uint)"/>
        /// when it is done. A mesh that fails the checks stays in the store.
        /// </remarks>
        internal static NeighbourhoodFacade Take(NeighbourhoodLot lot, Func<NeighbourhoodFacade, bool> usable)
        {
            if (lot == null) return null;
            lock (Gate)
            {
                if (!Meshes.TryGetValue(lot.Number, out var facade)) return null;
                if (!Hashes.TryGetValue(lot.Number, out var built) || built != lot.HouseHash) return null;
                if (!Plots.TryGetValue(lot.Number, out var plot) || !Adoptable(plot, lot)) return null;
                //a mesh whose buffers did not survive is not a mesh. IsDrawable is the same rule
                //the draw loop uses, so a store that adopted one would not draw it either - the
                //check here is what turns that into "rebuild it" rather than "an invisible house".
                if (facade.Surfaces == null || !usable(facade)) return null;
                Meshes.Remove(lot.Number);
                Hashes.Remove(lot.Number);
                Plots.Remove(lot.Number);
                Recency.Remove(lot.Number);
                return facade;
            }
        }

        /// <summary>Whether the store holds a mesh for a lot, whatever it was built from.</summary>
        /// <param name="lotNumber">The lot.</param>
        public static bool Contains(short lotNumber)
        {
            lock (Gate) { return Meshes.ContainsKey(lotNumber); }
        }

        /// <summary>Drops one lot's mesh and frees it.</summary>
        /// <param name="lotNumber">The lot.</param>
        public static void Forget(short lotNumber)
        {
            NeighbourhoodFacade dropped;
            lock (Gate)
            {
                if (!Meshes.TryGetValue(lotNumber, out dropped)) return;
                Meshes.Remove(lotNumber);
                Hashes.Remove(lotNumber);
                Plots.Remove(lotNumber);
                Recency.Remove(lotNumber);
            }
            dropped.Dispose();
        }

        /// <summary>Empties the store and frees every mesh in it.</summary>
        public static void Clear()
        {
            List<NeighbourhoodFacade> dropped;
            lock (Gate)
            {
                dropped = new List<NeighbourhoodFacade>(Meshes.Values);
                Meshes.Clear();
                Hashes.Clear();
                Plots.Clear();
                Recency.Clear();
            }
            for (int i = 0; i < dropped.Count; i++) dropped[i]?.Dispose();
        }

        /// <summary>
        /// Whether a mesh has anything to draw on it. The store's version of the draw loop's
        /// question, and deliberately the same one, so a mesh the draw loop would skip is a mesh
        /// the store refuses to hand over.
        /// </summary>
        private static bool AnyDrawable(NeighbourhoodFacade facade)
        {
            if (facade.Surfaces == null) return false;
            for (int i = 0; i < facade.Surfaces.Length; i++)
            {
                if (facade.Surfaces[i] != null && facade.Surfaces[i].IsDrawable) return true;
            }
            return false;
        }

        private static void Touch(short lotNumber)
        {
            Recency.Remove(lotNumber);
            Recency.AddLast(lotNumber);
        }

        private static void Evict()
        {
            while (Recency.Count > DefaultCapacity)
            {
                var oldest = Recency.First;
                if (oldest == null) return;
                var number = oldest.Value;
                Recency.RemoveFirst();
                if (!Meshes.TryGetValue(number, out var evicted)) continue;
                Meshes.Remove(number);
                Hashes.Remove(number);
                Plots.Remove(number);
                evicted.Dispose();
            }
        }

        /// <summary>
        /// Makes the store empty itself when the device is reset.
        /// </summary>
        /// <remarks>
        /// A reset frees every buffer and texture the old device had, and the store holds both.
        /// Subscribing here rather than being told by the view is the point: the store's contents
        /// are invalidated by an event the device raises, so the thing that has to react to it is
        /// the thing that owns the resources. A caller that never subscribes - a test, or a head
        /// - simply has an empty store.
        /// </remarks>
        /// <param name="device">The device, or <c>null</c> to forget the subscription.</param>
        public static void Watch(GraphicsDevice device)
        {
            lock (Gate)
            {
                if (_subscribed == device) return;
                if (_subscribed != null && _reset != null) _subscribed.DeviceReset -= _reset;
                _subscribed = device;
                _reset = null;
                if (device == null) return;
                _reset = (sender, args) => Clear();
                device.DeviceReset += _reset;
            }
        }
    }
}
