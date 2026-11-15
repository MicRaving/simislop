/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The road cell tables, copied from <c>FSO.Client.Rendering.City.CityContent</c>.
    ///
    /// <para><b>Why they are here.</b> They are the one thing the generated neighbourhood genuinely
    /// cannot do without: the road atlases in <c>tso.content</c> are shipped as sixteen separate
    /// 128x128 pngs and are only drawn as a coherent set because the <c>CityContent</c> statics say
    /// which cell of the assembled 8x4 atlas each road shape belongs in. Sixteen tables' worth of
    /// layout data is not a dependency on TSO's <em>content</em>, which is what Scope A is trying
    /// to avoid - it is eight numbers, and copying them keeps the whole neighbourhood renderer
    /// inside this project.
    /// </para>
    ///
    /// <para><b>Verified, not trusted.</b> <see cref="CellFor"/> folds both tables into one
    /// sixteen-entry answer and the mesh builder only ever asks it that, so there is exactly one
    /// place a table can be wrong. A test asserts every cell both tables name is a real cell of the
    /// 8x4 atlas, and that entry 0 - the "no road" mask - is the -1 that means "draw nothing".
    /// </para>
    /// </summary>
    public static class NeighbourhoodRoadLayout
    {
        /// <summary>Cells across in the assembled road atlas.</summary>
        public const int Width = 8;

        /// <summary>Cells down in the assembled road atlas.</summary>
        public const int Height = 4;

        /// <summary>Road shapes, indexed by a <see cref="RoadMask"/> nibble. Verbatim from
        /// <c>CityContent.RoadLayout</c>.</summary>
        public static readonly int[] Straights =
        {
            -1, 5, 12, 13, 7, 6, 15, 14, 28, 29, 20, 21, 31, 30, 23, 22
        };

        /// <summary>Turns and T-junctions, indexed by the same nibble. Verbatim from
        /// <c>CityContent.RoadCLayout</c>.</summary>
        public static readonly int[] Corners =
        {
            -1, 8, 2, 26, 3, 17, 16, 10, 25, 24, 9, 18, 1, 27, 11, 19
        };

        /// <summary>
        /// The atlas cell a road shape is drawn from, as a linear index, or <c>-1</c> for the
        /// "no road" mask.
        /// </summary>
        /// <param name="mask">A 4-bit <see cref="RoadMask"/> nibble.</param>
        /// <param name="corner">Whether to read the turn table rather than the straight one.</param>
        public static int CellFor(int mask, bool corner)
        {
            if (mask <= 0 || mask > 15) return -1;
            return (corner ? Corners : Straights)[mask];
        }

        /// <summary>
        /// The atlas cell's top-left corner, in UV space, for a cell index. Returns the origin for
        /// <c>-1</c> so a caller that ignores the "no road" answer still gets a usable coordinate.
        /// </summary>
        /// <param name="cell">A cell index, or <c>-1</c>.</param>
        public static Vector2 CellOrigin(int cell)
        {
            if (cell < 0) return Vector2.Zero;
            return new Vector2((cell % Width) / (float)Width, (cell / Width) / (float)Height);
        }

        /// <summary>How wide one cell is in UV space.</summary>
        public static Vector2 CellSize
        {
            get { return new Vector2(1f / Width, 1f / Height); }
        }

        /// <summary>
        /// Whether a cell index names a real cell of the 8x4 atlas. Everything the tables can
        /// produce should satisfy this, and a test says so.
        /// </summary>
        /// <param name="cell">A cell index.</param>
        public static bool IsRealCell(int cell)
        {
            return cell >= 0 && cell / Width < Height;
        }
    }

    /// <summary>
    /// The blend-mask layout: how a tile's four-bit adjacency mask becomes a cell of the
    /// transparency atlas.
    ///
    /// <para>
    /// <b>This is <c>CityGeometry.GetBlend</c>'s indexing, verbatim.</b> The 4-bit
    /// <see cref="Binary"/> mask, the sixteen-entry <see cref="FlagLayout"/> remap, and the 7x3
    /// atlas cell it names are all <c>FSO.Client.Rendering.City.CityGeometry.cs:41-80</c> and
    /// <c>CityContent.cs:49-52</c>. They are reproduced rather than replaced because the blend
    /// mask is not decoration: it is how one tile's higher-numbered neighbour is cross-faded over
    /// it, and a different mask or a different atlas shape is a different map edge.
    /// </para>
    ///
    /// <para>
    /// <b>The bit order is TSO's and is not intuitive.</b> A bit is <em>set</em> when that edge
    /// has <em>no</em> higher-priority neighbour, because <c>GetBlend</c> builds the mask as
    /// <c>((edges[0] &gt; -1) ? 0 : 2) | ((edges[1] &gt; -1) ? 0 : 1) | ((edges[2] &gt; -1) ? 0 : 8)
    /// | ((edges[3] &gt; -1) ? 0 : 4)</c> - so bit 0 is edge 1, bit 1 is edge 0, bit 2 is edge 3 and
    /// bit 3 is edge 2, and the set bits name the <em>absent</em> edges. It is written out here
    /// once so the mask arithmetic has one spelling.
    /// </para>
    /// </summary>
    public static class NeighbourhoodBlendFlags
    {
        /// <summary>Atlas cells across. <c>CityGeometry.RegenMeshVerts</c>'s <c>bDelta.X</c> is <c>1/7</c>.</summary>
        public const int AtlasWidth = 7;

        /// <summary>Atlas cells down. <c>bDelta.Y</c> is <c>1/3</c>.</summary>
        public const int AtlasHeight = 3;

        /// <summary>
        /// Each blend flag's index in the atlas, verbatim from <c>CityContent.FlagLayout</c>
        /// (<c>CityContent.cs:49-52</c>). The atlas is arranged to minimise the visual errors that
        /// mipmapping and linear filtering across cell boundaries cause, so the order is data, not
        /// an identity permutation.
        /// </summary>
        public static readonly int[] FlagLayout =
        {
            11, 7, 15, 2, 9, 6, 0, 4, 1, 16, 20, 12, 14, 18, 10, 8
        };

        /// <summary>How many distinct adjacency masks there are.</summary>
        public const int MaskCount = 16;

        /// <summary>
        /// The size of one atlas cell in UV space, from <c>CityGeometry.RegenMeshVerts</c>'s
        /// <c>var bDelta = new Vector2(1 / 7f, 1 / 3f)</c> (<c>CityGeometry.cs:164</c>).
        /// </summary>
        public static Vector2 CellSize
        {
            get { return new Vector2(1f / AtlasWidth, 1f / AtlasHeight); }
        }

        /// <summary>
        /// The mask for which no blend is drawn at all: every edge present, so there is no base
        /// terrain left to fade.
        /// </summary>
        public const int NoBlend = 15;

        /// <summary>
        /// Whether a mask names a blend that gets drawn. <c>CityGeometry</c>'s
        /// <c>if (blendData.Binary &lt; 15)</c>, at <c>CityGeometry.cs:303</c> and
        /// <c>CityGeometry.cs:721</c>.
        /// </summary>
        /// <param name="binary">A four-bit adjacency mask.</param>
        public static bool NeedsBlend(int binary)
        {
            return binary >= 0 && binary < NoBlend;
        }

        /// <summary>
        /// The top-left of the atlas cell a mask names, from <c>GetBlend</c>'s
        /// <c>var atlasPos = new Vector2((remap % 7) / 7f, (remap / 7) / 3f)</c>
        /// (<c>CityGeometry.cs:66-67</c>).
        /// </summary>
        /// <param name="binary">A four-bit adjacency mask.</param>
        public static Vector2 AtlasPositionFor(int binary)
        {
            var remap = FlagLayout[binary & 0x0F];
            return new Vector2((remap % AtlasWidth) / (float)AtlasWidth,
                (remap / AtlasWidth) / (float)AtlasHeight);
        }

        /// <summary>
        /// Which transparency atlas a ground layer is drawn with, from
        /// <c>CityGeometry.DrawAll</c>'s <c>var trans = (1 - (i - 1) / 2) * 2 + ((4 - i) % 2)</c>
        /// (<c>CityGeometry.cs:864</c>), integer division and all.
        /// </summary>
        /// <remarks>
        /// The expression is reproduced rather than simplified because it is the draw order's
        /// business and not the mesh's: it hands layer 1 atlas 3, layer 2 atlas 2, layer 3 atlas 1
        /// and layer 4 atlas 0, and a generated atlas set has to be built and indexed the same way
        /// or the four layers are not the four ramps the mask table was authored against. A test
        /// pins the four numbers.
        /// </remarks>
        /// <param name="layer">The ground layer index, 0 to 4.</param>
        public static int AtlasIndexForLayer(int layer)
        {
            return (1 - (layer - 1) / 2) * 2 + ((4 - layer) % 2);
        }

        /// <summary>
        /// How many transparency atlases the city has, and therefore how many a content set has to
        /// supply: <c>CityContent.TransAtlas</c> is a <c>Texture2D[4]</c>
        /// (<c>CityContent.cs:26</c>) and is indexed by <see cref="AtlasIndexForLayer"/>.
        /// </summary>
        public const int AtlasCount = 4;
    }

    /// <summary>
    /// What one tile blends into: which mask it has, where that mask lives in the atlas, and which
    /// ground layer the blend quad belongs to.
    /// </summary>
    /// <remarks>
    /// The three fields and their names are <c>FSO.Client.Rendering.City.Blend</c>
    /// (<c>Blend.cs:5-10</c>), and the field order matters for nothing here because this is a
    /// managed struct that never reaches a vertex buffer - only its three numbers do.
    /// </remarks>
    public readonly struct NeighbourhoodBlend
    {
        /// <summary>The four-bit adjacency mask; see <see cref="NeighbourhoodBlendFlags"/>.</summary>
        public readonly int Binary;

        /// <summary>Top-left of the mask's atlas cell, in UV space.</summary>
        public readonly Vector2 AtlasPosition;

        /// <summary>
        /// The ground layer the blend quad is drawn into, from <c>GetBlend</c>'s <c>maxEdge</c>
        /// loop (<c>CityGeometry.cs:69-72</c>): the lowest-numbered neighbouring terrain that is
        /// higher than this tile's own and lower than water, or water itself.
        /// </summary>
        public readonly int MaxEdge;

        public NeighbourhoodBlend(int binary, Vector2 atlasPosition, int maxEdge)
        {
            Binary = binary;
            AtlasPosition = atlasPosition;
            MaxEdge = maxEdge;
        }

        /// <summary>Whether this mask gets a blend quad.</summary>
        public bool NeedsBlend
        {
            get { return NeighbourhoodBlendFlags.NeedsBlend(Binary); }
        }

        public override string ToString()
        {
            return "mask " + Binary + " at " + AtlasPosition + " into layer " + MaxEdge;
        }
    }

    /// <summary>
    /// One vertex of a generated neighbourhood's terrain mesh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The field order and the declaration match <c>FSO.Client.Rendering.City.TLayerVertex</c>
    /// exactly, byte for byte, and that is the whole point: it is the vertex TSO's own
    /// <c>CityGeometry</c> fills and <c>FSO.Client.Rendering.City.Terrain</c> draws, so the same
    /// <c>Effects/VerShader</c> and <c>Effects/PixShader</c> technique - <c>RenderNCity</c> - draws
    /// this mesh with no change to either side. <c>RenderNCity</c>'s input struct
    /// (<c>VerShader.fx</c>'s <c>CityVertex</c>) is a position, one four-float texture coordinate
    /// holding the ground UV in <c>xy</c> and the blend mask in <c>zw</c>, and one four-float normal
    /// holding the normal in <c>xyz</c> and the transparency in <c>w</c> - which is
    /// <c>TextureCoord + MaskTextureCoord</c> as <c>TEXCOORD0</c> and <c>Normal + Transparency</c>
    /// as <c>TEXCOORD1</c>. All three of its elements are read; there is none to drop.
    /// </para>
    /// <para>
    /// <c>MaskTextureCoord</c> is always <c>(-1, -1)</c> here, which the shader reads as "no blend
    /// mask on this quad" - the reason §0 decision 3 (no cross-fades in the first version) costs
    /// nothing at the shader level.
    /// </para>
    /// <para>
    /// <b>Matching the declaration is necessary but not sufficient.</b> The device builds its input
    /// layout against <em>whatever vertex shader is bound</em>, and a shader is only bound by
    /// <c>EffectPass.Apply</c> - setting <c>CurrentTechnique</c> does nothing. So a draw that sets up
    /// the technique and never applies the pass is validated against the previous draw's shader
    /// (the facades' <c>RenderCityObj</c>, a sprite batch's <c>SpriteEffect</c>, anything) and is
    /// rejected as a declaration that "does not include all the elements required by the current
    /// vertex shader", which is MonoGame's <c>InputLayoutCache</c> wrapping D3D11's
    /// <c>E_INVALIDARG</c>. That is what used to happen here, and no amount of changing this struct
    /// would have fixed it. See <c>NeighbourhoodTerrainRenderer.Draw</c>.
    /// </para>
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    public struct NeighbourhoodTerrainVertex : IVertexType
    {
        /// <summary>
        /// World position: x and z are tiles, y is height in tiles -
        /// <see cref="NeighbourhoodCityMeshBuilder.WorldHeightFor"/> of the elevation byte at that
        /// corner.
        /// </summary>
        public Vector3 Position;

        /// <summary>UV into this layer's ground texture.</summary>
        public Vector2 TextureCoord;

        /// <summary>UV into the blend-mask atlas; always (-1, -1), i.e. "none".</summary>
        public Vector2 MaskTextureCoord;

        /// <summary>Surface normal, for the shader's diffuse term.</summary>
        public Vector3 Normal;

        /// <summary>
        /// Per-vertex <em>transparency</em>: <b>0 is opaque, 1 is fully transparent</b>.
        ///
        /// <para>
        /// <b>This is the city's convention, and getting it backwards draws nothing at all.</b>
        /// <c>PixShader.fx</c>'s <c>Fog</c> finishes with
        /// <c>BCol.w *= (1.0 - Input.NormalTrans.w); BCol.xyz = lerp(BCol.xyz, FogColor.xyz, fogDistance) * BCol.w;</c>
        /// so the vertex value is subtracted from one, not multiplied into it. A mesh that writes
        /// opacity here - 1 meaning "solid ground" - therefore asks for alpha zero and a black
        /// colour: the fog pass emits fragments that blend to exactly nothing, the terrain
        /// contributes not one pixel, and the player gets a blank screen with a log full of
        /// triangles. <c>FSO.Client.Rendering.City.TLayerVertex</c> says it outright
        /// ("0 is opaque") and <c>CityGeometry</c> writes
        /// <c>clamp(outside * fR, 0, 1)</c> - zero inside the diamond, rising to one in the fade
        /// skirt. See <see cref="NeighbourhoodCityMeshBuilder.FadeTransparency"/> and
        /// <c>NeighbourhoodTerrainVertexIsOpaqueInsideTheDiamond</c>.
        /// </para>
        /// </summary>
        public float Transparency;

        /// <summary>Bytes per vertex; eleven floats.</summary>
        public static readonly int SizeInBytes = sizeof(float) * 11;

        /// <summary>The declaration a vertex buffer of these must be drawn with.</summary>
        public static readonly VertexDeclaration VertexElements = new VertexDeclaration(
            new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
            new VertexElement(sizeof(float) * 3, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 0),
            new VertexElement(sizeof(float) * (3 + 4), VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 1));

        /// <summary>
        /// <see cref="VertexElements"/> as one line of text, for a log that has to name what the
        /// device is being asked to build an input layout out of.
        /// </summary>
        /// <remarks>
        /// The point of naming it is that the error this declaration used to produce -
        /// MonoGame's "the current vertex declaration does not include all the elements required by
        /// the current vertex shader" - does not say which shader it was compared against, and
        /// without the declaration on the same line as the technique there is nothing to compare.
        /// </remarks>
        public static string DescribeDeclaration()
        {
            var elements = VertexElements.GetVertexElements();
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

        VertexDeclaration IVertexType.VertexDeclaration
        {
            get { return VertexElements; }
        }
    }

    /// <summary>
    /// A contiguous run of chunks' worth of one layer's index buffer, ready for one
    /// <c>DrawIndexedPrimitives</c>.
    /// </summary>
    /// <remarks>
    /// A range is <em>half open</em>: chunks <see cref="FirstChunk"/> to (exclusive)
    /// <see cref="EndChunk"/>, and <see cref="StartIndex"/> to (exclusive)
    /// <see cref="StartIndex"/> + <see cref="IndexCount"/> in the layer's index array. Chunks are
    /// emitted in row-major order by the builder, so a run of consecutive chunks is a contiguous
    /// run of the index array and the two descriptions cannot disagree - which is what
    /// <see cref="NeighbourhoodCityMeshBuilder.VisibleDrawRanges"/> relies on to turn a set of
    /// visible chunks into a handful of draw calls instead of one per chunk.
    /// </remarks>
    public readonly struct NeighbourhoodMeshDrawRange
    {
        /// <summary>The first chunk in the run.</summary>
        public readonly int FirstChunk;

        /// <summary>One past the last chunk in the run.</summary>
        public readonly int EndChunk;

        /// <summary>The first index of the run inside the layer's index array.</summary>
        public readonly int StartIndex;

        /// <summary>How many indices the run covers - a multiple of three.</summary>
        public readonly int IndexCount;

        /// <summary>How many triangles the run covers, for a log line.</summary>
        public int PrimitiveCount
        {
            get { return IndexCount / 3; }
        }

        /// <summary>Whether the run covers anything.</summary>
        public bool IsEmpty
        {
            get { return IndexCount <= 0; }
        }

        /// <summary>
        /// The three numbers a <c>DrawIndexedPrimitives</c> call for this run needs, in the order
        /// that overload takes them.
        /// </summary>
        /// <remarks>
        /// The 4-argument overload is
        /// <c>(primitiveType, baseVertex, startIndex, primitiveCount)</c>, and two of those are
        /// adjacent integers that mean different things: <c>baseVertex</c> is an offset added to
        /// every index before it fetches a vertex, <c>startIndex</c> is where in the index buffer
        /// the run begins. Handing <see cref="StartIndex"/> to <c>baseVertex</c> compiles, does not
        /// throw, and submits the wrong span - so this is named once, in one place, rather than at
        /// each call site.
        /// </remarks>
        public NeighbourhoodDrawCall ToDrawCall()
        {
            //baseVertex 0: the index buffer already holds absolute vertex indices, because the
            //builder appends every quad's first vertex and refers to it by that absolute position
            return new NeighbourhoodDrawCall(0, StartIndex, PrimitiveCount);
        }

        /// <summary>Builds a range over a run of chunks.</summary>
        /// <param name="firstChunk">The first chunk in the run.</param>
        /// <param name="endChunk">One past the last chunk in the run.</param>
        /// <param name="startIndex">The first index of the run.</param>
        /// <param name="indexCount">How many indices the run covers.</param>
        public NeighbourhoodMeshDrawRange(int firstChunk, int endChunk, int startIndex, int indexCount)
        {
            FirstChunk = firstChunk;
            EndChunk = endChunk;
            StartIndex = startIndex;
            IndexCount = indexCount;
        }

        public override string ToString()
        {
            return "chunks " + FirstChunk + ".." + EndChunk + " indices " + StartIndex + "+" + IndexCount;
        }
    }

    /// <summary>
    /// What a mesh build found in a map, and what it emitted - the numbers a log line needs to
    /// answer "why does this neighbourhood not look like a neighbourhood".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Four of these numbers are the difference between the two failure modes that look
    /// identical on screen. If <see cref="MapRoadTiles"/> is zero, the generator drew no streets
    /// and that is the road graph's problem. If it is thousands and
    /// <see cref="ClippedRoadTiles"/> is most of them, the streets are there and the isometric
    /// clip is throwing them away - which is a different bug in a different file, and one a
    /// screenshot cannot distinguish. <see cref="Describe"/> puts both in one line for exactly
    /// that reason.
    /// </para>
    /// <para>
    /// <see cref="Probe"/> is the second, independent derivation of the same counts: it walks the
    /// map a tile at a time rather than chunk by chunk. <see cref="RecordEmitted"/> is what the
    /// builder's own walk produced, and a test asserts the two agree - so a change to the clip
    /// that the accounting does not share is a failing test, not a different-looking map.
    /// </para>
    /// </remarks>
    public sealed class NeighbourhoodCityMeshReport
    {
        /// <summary>Ground quads per layer, indexed by <see cref="NeighbourhoodTerrain"/> value.</summary>
        public readonly int[] LayerQuads = new int[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>Straight/T/cross road quads the mesh emitted, from the low nibble.</summary>
        public int RoadQuads;

        /// <summary>Turn/T-junction quads the mesh emitted, from the high nibble.</summary>
        public int CornerQuads;

        /// <summary>Tiles anywhere in the 512x512 square that carry either road nibble.</summary>
        public int MapRoadTiles;

        /// <summary>
        /// Map tiles per ground quad edge in the build this report came from: 1, 2 or 4. One for a
        /// report that only probed the map, since then nothing was built.
        /// </summary>
        public int LodFactor { get; internal set; } = 1;

        /// <summary>
        /// Ground quads per map tile edge in the build this report came from. One for every
        /// whole-map level; <see cref="NeighbourhoodMeshLod.MaxSubdivisions"/> for a subdivided
        /// slice. See <see cref="NeighbourhoodCityMesh.Subdivisions"/>.
        /// </summary>
        public int Subdivisions { get; internal set; } = 1;

        /// <summary>
        /// The slice this report came from, or <c>-1</c> for a whole-map build.
        ///
        /// <para>
        /// It is on the report rather than inferred from the quad counts because
        /// <see cref="EmissionMatchesProbe"/> is the test that holds the builder against the
        /// independent walk, and a slice is not a whole map: its emission is a sixteenth of the
        /// probe's and is <em>supposed</em> to be. A report that knows which it is can refuse the
        /// comparison instead of a test having to remember.
        /// </para>
        /// </summary>
        public int SliceIndex { get; internal set; } = -1;

        /// <summary>Whether this report describes one slice rather than the whole map.</summary>
        public bool IsSlice
        {
            get { return SliceIndex >= 0; }
        }

        /// <summary>Tiles strictly inside the isometric diamond that carry the low nibble.</summary>
        public int DiamondRoadTiles;

        /// <summary>Tiles strictly inside the isometric diamond that carry the high nibble.</summary>
        public int DiamondCornerTiles;

        /// <summary>
        /// Road-bearing tiles that survive the clip, counted once per tile rather than once per
        /// nibble. This is the numerator of <see cref="RoadCoverage"/>, and it is a separate
        /// field from the two above precisely because a tile with both nibbles would otherwise be
        /// counted twice and a junction-heavy map would report more than 100% coverage.
        /// </summary>
        public int DiamondRoadBearingTiles;

        /// <summary>
        /// Road-bearing tiles the clip threw away: in the square, outside the diamond. The map is
        /// a square and the drawn world is a kite, so this is normally not a small number.
        /// </summary>
        public int ClippedRoadTiles;

        /// <summary>Corner quads the clip threw away, on the same terms.</summary>
        public int ClippedCornerTiles;

        /// <summary>How many of the square's tiles the drawn diamond actually contains.</summary>
        public int DiamondTiles;

        /// <summary>How many tiles the square has.</summary>
        public int MapTiles;

        /// <summary>Every road atlas cell the map reaches, straight and corner alike.</summary>
        public readonly HashSet<int> RoadCells = new HashSet<int>();

        /// <summary>
        /// Blend faces the map should produce <em>per ground layer they land in</em>, so a layer
        /// buffer's triangle count can be checked against
        /// <c>LayerQuads + BlendQuadsPerLayer</c> rather than against the ground alone.
        /// </summary>
        /// <remarks>
        /// This is what the two counts need to be a test rather than a coincidence. A blend face is
        /// a quad in a <em>neighbour's</em> buffer, so a layer's <c>PrimitiveCount</c> is its own
        /// ground plus whatever blends other tiles sent into it - and which of those it is depends on
        /// the mask arithmetic, which is exactly the thing worth holding against something.
        /// </remarks>
        public readonly int[] BlendQuadsPerLayer = new int[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>
        /// Blend faces the map should produce: one per strictly-inside tile whose four-bit adjacency
        /// mask is below <see cref="NeighbourhoodBlendFlags.NoBlend"/>.
        /// </summary>
        /// <remarks>
        /// Counted on the builder's own chunked window, exactly as
        /// <see cref="ChunkedLayerQuads"/> does for the ground, so a change to the clip that the
        /// blend count does not share shows up as a disagreement with
        /// <see cref="EmittedBlendQuads"/> rather than as a cross-fade quietly missing from the
        /// map's border.
        /// </remarks>
        public int BlendQuads;

        /// <summary>Distinct adjacency masks the map reaches.</summary>
        public readonly HashSet<int> BlendMasks = new HashSet<int>();

        /// <summary>
        /// Distinct cells of the 7x3 mask atlas the map reaches, after
        /// <see cref="NeighbourhoodBlendFlags.FlagLayout"/>'s remap.
        /// </summary>
        public readonly HashSet<int> BlendAtlasCells = new HashSet<int>();

        /// <summary>
        /// Records one tile's blend, counting it and the mask and atlas cell it reaches.
        /// </summary>
        /// <remarks>
        /// The two exclusions are the same two the builder applies: a mask of 15 is TSO's "nothing
        /// to blend into" answer, and a <c>MaxEdge</c> outside the five layers has no buffer to go
        /// into. Counting either would make the report claim a face that was never emitted.
        /// </remarks>
        /// <param name="blend">A tile's resolved blend.</param>
        public void AddBlend(NeighbourhoodBlend blend)
        {
            if (!blend.NeedsBlend) return;
            if (blend.MaxEdge < 0 || blend.MaxEdge >= NeighbourhoodCityMeshBuilder.LayerCount) return;
            BlendQuads++;
            BlendQuadsPerLayer[blend.MaxEdge]++;
            BlendMasks.Add(blend.Binary);
            BlendAtlasCells.Add(NeighbourhoodBlendFlags.FlagLayout[blend.Binary & 0x0F]);
        }

        /// <summary>What the mesh actually emitted, as opposed to what the map holds.</summary>
        public readonly int[] EmittedLayerQuads = new int[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>
        /// Blend faces the mesh actually emitted, per layer they landed in.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="EmittedLayerQuads"/> because a blend face is not that layer's
        /// ground - it is another tile's neighbour drawn over it - and a per-layer count that
        /// conflated the two could not be checked against a layer buffer's triangle count.
        /// </remarks>
        public readonly int[] EmittedBlendQuadsPerLayer = new int[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>Road quads the builder emitted.</summary>
        public int EmittedRoadQuads;

        /// <summary>Corner quads the builder emitted.</summary>
        public int EmittedCornerQuads;

        /// <summary>
        /// Blend faces the builder emitted: one per tile that has a higher-numbered neighbour and
        /// a mask below <see cref="NeighbourhoodBlendFlags.NoBlend"/>.
        /// </summary>
        /// <remarks>
        /// Counted apart from <see cref="EmittedLayerQuads"/> because a blend face is a quad in
        /// <em>another</em> layer's buffer, so the layer counts are ground quads only and the
        /// triangle count of a layer buffer is its ground quads plus whatever blends other tiles
        /// sent into it. The probe counts the same two things the same way, so the two derivations
        /// still have to agree.
        /// </remarks>
        public int EmittedBlendQuads;

        /// <summary>Every ground quad the mesh should hold, all five layers.</summary>
        public int TotalLayerQuads
        {
            get
            {
                var total = 0;
                for (int i = 0; i < LayerQuads.Length; i++) total += LayerQuads[i];
                return total;
            }
        }

        /// <summary>Every road quad the mesh should hold.</summary>
        public int TotalRoadQuads
        {
            get { return RoadQuads + CornerQuads; }
        }

        /// <summary>
        /// The fraction of the square's tiles the drawn diamond covers, from 0 to 1. Below 1 the
        /// map's corners are simply not in the world, which matters for anything laid out over
        /// the square rather than over the diamond.
        /// </summary>
        public float DiamondCoverage
        {
            get { return MapTiles <= 0 ? 0f : (float)DiamondTiles / MapTiles; }
        }

        /// <summary>
        /// What fraction of the map's road tiles survive the clip, from 0 to 1.
        /// </summary>
        public float RoadCoverage
        {
            get { return MapRoadTiles <= 0 ? 0f : (float)DiamondRoadBearingTiles / MapRoadTiles; }
        }

        /// <summary>
        /// Walks a map a tile at a time and records what the mesh ought to hold.
        /// </summary>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        public static NeighbourhoodCityMeshReport Probe(NeighbourhoodMap map)
        {
            var report = new NeighbourhoodCityMeshReport();
            if (map == null) return report;
            report.MapTiles = map.Width * map.Height;
            if (map.Width != NeighbourhoodCityMeshBuilder.MapSize || map.Height != NeighbourhoodCityMeshBuilder.MapSize)
            {
                //a map of the wrong size cannot be meshed at all, so there is nothing to count;
                //the refusal is CanMesh's job and it is better at saying so
                return report;
            }

            var terrain = map.TerrainType;
            var road = map.RoadData;

            for (int row = 0; row < NeighbourhoodCityMeshBuilder.MapSize; row++)
            {
                var rowLeft = NeighbourhoodCityMeshBuilder.DiamondLeft(row);
                var rowRight = NeighbourhoodCityMeshBuilder.DiamondRight(row);
                if (rowRight > rowLeft) report.DiamondTiles += rowRight - rowLeft;

                for (int x = 0; x < NeighbourhoodCityMeshBuilder.MapSize; x++)
                {
                    var roadByte = road[row * NeighbourhoodCityMeshBuilder.MapSize + x];
                    var straight = roadByte & RoadMask.MaskBits;
                    var corner = roadByte >> 4;
                    if (straight == 0 && corner == 0) continue;
                    report.MapRoadTiles++;

                    if (!NeighbourhoodCityMeshBuilder.IsInsideDiamond(x, row))
                    {
                        report.ClippedRoadTiles++;
                        if (corner > 0) report.ClippedCornerTiles++;
                        continue;
                    }
                    report.DiamondRoadBearingTiles++;
                    if (straight > 0)
                    {
                        report.DiamondRoadTiles++;
                        report.Note(NeighbourhoodRoadLayout.CellFor(straight, false));
                    }
                    if (corner > 0)
                    {
                        report.DiamondCornerTiles++;
                        report.Note(NeighbourhoodRoadLayout.CellFor(corner, true));
                    }
                }
            }

            //The ground quads are counted the way the builder's own chunk loop walks rather than
            //row by row, because the two are not the same walk: the builder clamps each row's
            //ten-tile fade window against the 16-wide chunk band, which makes the skirt
            //asymmetric and drops tiles on rows whose skirt crosses a band. Reproducing the
            //chunked walk is the whole point of this being a second derivation rather than a
            //formula - if it disagreed with the builder that would be the finding.
            report.SetLayerQuads(ChunkedLayerQuads(terrain));
            report.RoadQuads = report.DiamondRoadTiles;
            report.CornerQuads = report.DiamondCornerTiles;

            //The blend faces, walked a tile at a time on the builder's own chunked window so the two
            //derivations cover the same tiles. They go through the same GetBlend the builder's walk
            //does, because the mask arithmetic is not what is under test here - the window is - and
            //re-deriving it would be a third spelling of the same sixteen lines.
            for (int cy = 0; cy < NeighbourhoodCityMeshBuilder.ChunksPerAxis; cy++)
            {
                for (int cx = 0; cx < NeighbourhoodCityMeshBuilder.ChunksPerAxis; cx++)
                {
                    var chunkLeft = cx * NeighbourhoodCityMeshBuilder.ChunkSize;
                    var chunkRight = (cx + 1) * NeighbourhoodCityMeshBuilder.ChunkSize;
                    for (int row = cy * NeighbourhoodCityMeshBuilder.ChunkSize;
                        row < (cy + 1) * NeighbourhoodCityMeshBuilder.ChunkSize; row++)
                    {
                        var rowLeft = NeighbourhoodCityMeshBuilder.DiamondLeft(row);
                        var rowRight = NeighbourhoodCityMeshBuilder.DiamondRight(row);
                        var start = Math.Max(rowLeft - NeighbourhoodCityMeshBuilder.FadeRange, chunkLeft);
                        var end = Math.Min(chunkRight, rowRight + NeighbourhoodCityMeshBuilder.FadeRange);
                        for (int x = start; x < end; x++)
                        {
                            if (!NeighbourhoodCityMeshBuilder.IsInsideDiamond(x, row)) continue;
                            var clamped = Math.Min(Math.Max(rowLeft, x), rowRight - 1);
                            if (terrain[row * NeighbourhoodCityMeshBuilder.MapSize + clamped]
                                >= NeighbourhoodCityMeshBuilder.LayerCount) continue;
                            report.AddBlend(NeighbourhoodCityMeshBuilder.GetBlend(terrain, row, clamped));
                        }
                    }
                }
            }
            return report;
        }

        /// <summary>
        /// Fills the per-layer ground quad counts. <c>internal</c> rather than private because a
        /// subdivided slice reports <em>its own</em> counts instead of running the whole-map probe,
        /// and it is the builder that has them.
        /// </summary>
        internal void SetLayerQuads(int[] counts)
        {
            if (counts == null) return;
            for (int i = 0; i < LayerQuads.Length && i < counts.Length; i++) LayerQuads[i] = counts[i];
        }

        private static int[] ChunkedLayerQuads(byte[] terrain)
        {
            var counts = new int[NeighbourhoodCityMeshBuilder.LayerCount];
            for (int cy = 0; cy < NeighbourhoodCityMeshBuilder.ChunksPerAxis; cy++)
            {
                for (int cx = 0; cx < NeighbourhoodCityMeshBuilder.ChunksPerAxis; cx++)
                {
                    var chunkLeft = cx * NeighbourhoodCityMeshBuilder.ChunkSize;
                    var chunkRight = (cx + 1) * NeighbourhoodCityMeshBuilder.ChunkSize;
                    for (int row = cy * NeighbourhoodCityMeshBuilder.ChunkSize;
                        row < (cy + 1) * NeighbourhoodCityMeshBuilder.ChunkSize; row++)
                    {
                        var rowLeft = NeighbourhoodCityMeshBuilder.DiamondLeft(row);
                        var rowRight = NeighbourhoodCityMeshBuilder.DiamondRight(row);
                        var start = Math.Max(rowLeft - NeighbourhoodCityMeshBuilder.FadeRange, chunkLeft);
                        var end = Math.Min(chunkRight, rowRight + NeighbourhoodCityMeshBuilder.FadeRange);
                        for (int x = start; x < end; x++)
                        {
                            var clamped = Math.Min(Math.Max(rowLeft, x), rowRight - 1);
                            var type = terrain[row * NeighbourhoodCityMeshBuilder.MapSize + clamped];
                            if (type >= NeighbourhoodCityMeshBuilder.LayerCount) continue;
                            counts[type]++;
                        }
                    }
                }
            }
            return counts;
        }

        private void Note(int cell)
        {
            if (NeighbourhoodRoadLayout.IsRealCell(cell)) RoadCells.Add(cell);
        }

        /// <summary>
        /// Records what the builder's own walk emitted, so a test can hold the two derivations
        /// against each other.
        /// </summary>
        /// <param name="layerQuads">Ground quads per layer.</param>
        /// <param name="roadQuads">Straight road quads.</param>
        /// <param name="cornerQuads">Corner road quads.</param>
        public void RecordEmitted(int[] layerQuads, int roadQuads, int cornerQuads)
        {
            RecordEmitted(layerQuads, roadQuads, cornerQuads, null);
        }

        /// <summary>
        /// Records what the builder's own walk emitted, blends included.
        /// </summary>
        /// <param name="layerQuads">Ground quads per layer.</param>
        /// <param name="roadQuads">Straight road quads.</param>
        /// <param name="cornerQuads">Corner road quads.</param>
        /// <param name="blendQuadsPerLayer">
        /// Blend faces per layer they landed in, or <c>null</c> for a build with none.
        /// </param>
        public void RecordEmitted(int[] layerQuads, int roadQuads, int cornerQuads,
            int[] blendQuadsPerLayer)
        {
            if (layerQuads != null)
            {
                for (int i = 0; i < EmittedLayerQuads.Length && i < layerQuads.Length; i++)
                {
                    EmittedLayerQuads[i] = layerQuads[i];
                }
            }
            if (blendQuadsPerLayer != null)
            {
                for (int i = 0; i < EmittedBlendQuadsPerLayer.Length && i < blendQuadsPerLayer.Length; i++)
                {
                    EmittedBlendQuadsPerLayer[i] = blendQuadsPerLayer[i];
                }
            }
            EmittedRoadQuads = roadQuads;
            EmittedCornerQuads = cornerQuads;
        }

        /// <summary>
        /// Whether the builder emitted exactly what the independent walk expects.
        /// </summary>
        /// <remarks>
        /// Always <c>false</c> for a slice, and by construction rather than by accident: the probe
        /// walks the whole map and a slice is a thirty-two by thirty-two piece of it. See
        /// <see cref="SliceIndex"/>.
        /// </remarks>
        public bool EmissionMatchesProbe
        {
            get
            {
                if (IsSlice) return false;
                if (Subdivisions > 1) return false;
                for (int i = 0; i < LayerQuads.Length; i++)
                {
                    if (LayerQuads[i] != EmittedLayerQuads[i]) return false;
                    //Per layer, not just in total: a blend face emitted into the wrong neighbour's
                    //buffer would leave the total right and the layer wrong, and the total is what
                    //the log line prints.
                    if (BlendQuadsPerLayer[i] != EmittedBlendQuadsPerLayer[i]) return false;
                }
                //The blend faces in total too, and for the same reason as the ground: they are
                //emitted by the same walk over the same window, and a blend count the walk does not
                //share is a silently missing cross-fade rather than a failing test.
                if (BlendQuads != EmittedBlendQuads) return false;
                return RoadQuads == EmittedRoadQuads && CornerQuads == EmittedCornerQuads;
            }
        }

        /// <summary>One line for the log, with the clip's cost stated rather than implied.</summary>
        public override string ToString()
        {
            var text = new System.Text.StringBuilder();
            if (IsSlice)
            {
                var slice = NeighbourhoodCityMeshBuilder.SliceBounds(SliceIndex);
                text.Append("slice ").Append(SliceIndex).Append(" (tiles ").Append(slice.X)
                    .Append(',').Append(slice.Y).Append(' ').Append(slice.Width).Append('x')
                    .Append(slice.Height).Append(") at ").Append(Subdivisions)
                    .Append(" quads per tile: ground quads");
                for (int i = 0; i < LayerQuads.Length; i++)
                {
                    if (i > 0) text.Append(' ');
                    text.Append('[').Append(i).Append("]=").Append(LayerQuads[i]);
                }
                //and nothing about roads, because there are none here: the road quads come from the
                //whole-map level at every level, so a line claiming this slice drew no streets
                //would be a report of a decision rather than of the mesh
                text.Append(", ").Append(TotalLayerQuads).Append(" ground quads, ")
                    .Append(TotalLayerQuads * 2).Append(" ground triangles");
                return text.ToString();
            }
            text.Append("ground quads");
            for (int i = 0; i < LayerQuads.Length; i++)
            {
                if (i > 0) text.Append(' ');
                text.Append('[').Append(i).Append("]=").Append(LayerQuads[i]);
            }
            text.Append(", road quads ").Append(RoadQuads).Append(" corner quads ").Append(CornerQuads);
            text.Append(" of ").Append(MapRoadTiles).Append(" road tiles (")
                .Append((RoadCoverage * 100f).ToString("0.0")).Append("% survives the clip)");
            text.Append(", the clip keeps ").Append((DiamondCoverage * 100f).ToString("0.0"))
                .Append("% of the map's tiles");
            if (ClippedRoadTiles > 0)
            {
                text.Append(", ").Append(ClippedRoadTiles).Append(" road tiles and ")
                    .Append(ClippedCornerTiles).Append(" corner quads fall outside it");
            }
            return text.ToString();
        }
    }

    /// <summary>
    /// One <c>DrawIndexedPrimitives</c> call, as the three numbers the 4-argument overload takes
    /// after the primitive type.
    /// </summary>
    /// <remarks>
    /// This exists so the two adjacent integers cannot be transposed at a call site.
    /// <c>DrawIndexedPrimitives(PrimitiveType.TriangleList, range.StartIndex, 0, count)</c> passes
    /// the index offset where the vertex offset belongs, so every chunk draws from the wrong place in
    /// the vertex buffer and reads past the end of it on the last chunk - and nothing throws, which
    /// is the only reason it survived. The correct call is
    /// <c>DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, range.StartIndex, count)</c>.
    /// </remarks>
    public readonly struct NeighbourhoodDrawCall
    {
        /// <summary>
        /// The offset added to every index before it fetches a vertex. Always 0 for a terrain mesh:
        /// the index buffer holds absolute vertex indices already.
        /// </summary>
        public readonly int BaseVertex;

        /// <summary>The index inside the index buffer where the run starts.</summary>
        public readonly int StartIndex;

        /// <summary>How many triangles the run covers.</summary>
        public readonly int PrimitiveCount;

        /// <summary>Builds a call.</summary>
        /// <param name="baseVertex">The per-index vertex offset.</param>
        /// <param name="startIndex">Where in the index buffer the run starts.</param>
        /// <param name="primitiveCount">How many triangles the run covers.</param>
        public NeighbourhoodDrawCall(int baseVertex, int startIndex, int primitiveCount)
        {
            BaseVertex = baseVertex;
            StartIndex = startIndex;
            PrimitiveCount = primitiveCount;
        }

        /// <summary>Whether this call would submit nothing.</summary>
        public bool IsEmpty
        {
            get { return PrimitiveCount <= 0; }
        }

        /// <summary>
        /// The call that draws an entire index buffer from its start - the whole-buffer path, which
        /// has no chunk bookkeeping and so has no range to convert.
        /// </summary>
        /// <param name="indexCount">The index buffer's length; three indices per triangle.</param>
        public static NeighbourhoodDrawCall WholeBuffer(int indexCount)
        {
            return new NeighbourhoodDrawCall(0, 0, indexCount / 3);
        }

        /// <summary>The call that draws one chunk range.</summary>
        /// <param name="range">The run of chunks to submit.</param>
        public static NeighbourhoodDrawCall ForRange(NeighbourhoodMeshDrawRange range)
        {
            return range.ToDrawCall();
        }

        public override string ToString()
        {
            return "baseVertex " + BaseVertex + ", startIndex " + StartIndex
                + ", primitiveCount " + PrimitiveCount;
        }
    }

    /// <summary>One drawable buffer of a generated neighbourhood's terrain mesh.</summary>
    public sealed class NeighbourhoodMeshLayer
    {
        /// <summary>The vertices, or empty when the layer has nothing in it.</summary>
        public NeighbourhoodTerrainVertex[] Vertices { get; internal set; }

        /// <summary>Triangle list indices into <see cref="Vertices"/>.</summary>
        public int[] Indices { get; internal set; }

        /// <summary>How many triangles this layer holds.</summary>
        public int PrimitiveCount
        {
            get { return Indices == null ? 0 : Indices.Length / 3; }
        }

        /// <summary>Whether there is anything to draw.</summary>
        public bool IsEmpty
        {
            get { return PrimitiveCount == 0; }
        }
    }

    /// <summary>
    /// A built terrain mesh: five ground layers plus the roads, in the shape
    /// <c>CityGeometry</c> would have produced for the same arrays.
    /// </summary>
    public sealed class NeighbourhoodCityMesh
    {
        /// <summary>
        /// The five ground layers, indexed by <see cref="NeighbourhoodTerrain"/> value - grass,
        /// sand, rock, snow, water, which is the order the draw order is documented in and the
        /// order a flat per-layer renderer needs.
        /// </summary>
        public readonly NeighbourhoodMeshLayer[] Layers = new NeighbourhoodMeshLayer[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>The roads, drawn over the ground with the assembled road atlas.</summary>
        public NeighbourhoodMeshLayer Roads { get; internal set; }

        /// <summary>
        /// Map tiles per quad edge: 1 for the full-detail level, 2 or 4 for the coarse ones.
        /// The roads are the same at every level, so this is about the ground alone.
        /// </summary>
        public int LodFactor { get; internal set; } = 1;

        /// <summary>
        /// Ground quads per map tile edge, inside one tile: 1 for the coarse whole-map levels and
        /// <see cref="NeighbourhoodMeshLod.MaxSubdivisions"/> for the close one.
        ///
        /// <para>
        /// <b>This is the second half of the level of detail, and it is the half that makes
        /// zooming in look better rather than merely cheaper.</b> <see cref="LodFactor"/> says how
        /// much map one quad covers, which is the question a wide camera asks. This says how finely
        /// one tile is cut up, which is the question a close camera asks: at the closest zoom a
        /// tile is about five pixels across, and one quad per tile with a single normal per
        /// corner is a faceted plane five pixels wide, so the ground reads as a grid of little
        /// flats however much of the map is on screen.
        /// </para>
        ///
        /// <para>
        /// The value is <c>CityGeometry.SubRegenMeshVerts</c>'s <c>subdiv</c> argument verbatim
        /// (four, from <c>Terrain.cs</c>'s two call sites), and the geometry it produces is that
        /// method's: a <c>(subdiv + 1) * (subdiv + 1)</c> vertex grid per tile whose heights are
        /// Catmull-Rom interpolated across the tile and whose normals and fade are interpolated
        /// bilinearly. See <see cref="NeighbourhoodCityMeshBuilder.BuildSubdivSlice"/>.
        /// </para>
        /// </summary>
        public int Subdivisions { get; internal set; } = 1;

        /// <summary>
        /// For each ground layer, the index at which every chunk's geometry starts, plus a final
        /// sentinel holding the layer's whole index count - so the array is
        /// <see cref="NeighbourhoodCityMeshBuilder.ChunkCount"/> + 1 long and
        /// <c>starts[c + 1] - starts[c]</c> is chunk <c>c</c>'s size in indices.
        /// </summary>
        /// <remarks>
        /// This is <c>CityGeometry</c>'s <c>LayerSubPrims</c>, which TSO's own
        /// <c>DrawChunk</c> uses to submit a few ranges of a layer rather than all of it. It
        /// exists because a 512x512 map is a quarter of a million triangles and a neighbourhood
        /// camera is usually looking at a fraction of the map: without it, every frame pays for
        /// the whole neighbourhood whatever the zoom.
        /// </remarks>
        public int[][] LayerChunkIndexStarts { get; internal set; }

        /// <summary>
        /// The road buffer's equivalent of <see cref="LayerChunkIndexStarts"/>: the index at
        /// which every chunk's road geometry starts, plus the same final sentinel.
        /// </summary>
        public int[] RoadChunkIndexStarts { get; internal set; }

        internal NeighbourhoodCityMesh()
        {
            for (int i = 0; i < Layers.Length; i++) Layers[i] = new NeighbourhoodMeshLayer();
            Roads = new NeighbourhoodMeshLayer();
            LayerChunkIndexStarts = new int[Layers.Length][];
            for (int i = 0; i < LayerChunkIndexStarts.Length; i++)
            {
                LayerChunkIndexStarts[i] = new int[NeighbourhoodCityMeshBuilder.ChunkCount + 1];
            }
            RoadChunkIndexStarts = new int[NeighbourhoodCityMeshBuilder.ChunkCount + 1];
        }

        /// <summary>Every triangle in the mesh.</summary>
        public int TotalPrimitives
        {
            get
            {
                int total = Roads.PrimitiveCount;
                for (int i = 0; i < Layers.Length; i++) total += Layers[i].PrimitiveCount;
                return total;
            }
        }

        /// <summary>
        /// This mesh's own level of detail, as the two numbers that describe it: how much map one
        /// ground quad covers, and how finely a map tile is cut up. Built from the fields rather
        /// than from a factor lookup, so it describes the geometry that is actually in the buffers.
        /// </summary>
        public NeighbourhoodMeshLod Lod
        {
            get { return new NeighbourhoodMeshLod(LodFactor, Subdivisions); }
        }

        /// <summary>
        /// Ground triangles, across the five layers. The number a level of detail is really about;
        /// <see cref="TotalPrimitives"/> also counts the roads, which never coarsen.
        /// </summary>
        public int GroundPrimitives
        {
            get
            {
                int total = 0;
                for (int i = 0; i < Layers.Length; i++) total += Layers[i].PrimitiveCount;
                return total;
            }
        }

        /// <summary>Every vertex in the mesh.</summary>
        public int TotalVertices
        {
            get
            {
                int total = Roads.Vertices?.Length ?? 0;
                for (int i = 0; i < Layers.Length; i++) total += Layers[i].Vertices?.Length ?? 0;
                return total;
            }
        }

        public override string ToString()
        {
            return TotalPrimitives + " triangles, " + TotalVertices + " vertices ("
                + Roads.PrimitiveCount + " road)";
        }
    }

    /// <summary>
    /// How much of the ground a mesh can skip: the three levels of detail the renderer chooses
    /// between, and the rule that picks one from a camera.
    ///
    /// <para>
    /// <b>Why a 512x512 map needs them.</b> The full-detail mesh is about three hundred thousand
    /// terrain triangles. A camera framing the whole neighbourhood has the whole map on screen, so
    /// the frustum cull - worth a great deal when a lot is focused - removes nothing at exactly the
    /// zoom where the frame is most expensive. The coarse levels are the answer: one quad per
    /// 2x2 or 4x4 tiles, a quarter and a sixteenth of the triangles, and at the zooms they are
    /// chosen at a tile is two or three pixels wide, so they are not distinguishable from the fine
    /// mesh on screen.
    /// </para>
    ///
    /// <para>
    /// <b>The rule is stated in tiles, not pixels, so it does not need a device.</b> A level is good
    /// enough as long as the view still spans <see cref="MinTilesAcross"/> tiles of it, which is
    /// the "one screen pixel is about this much of the level" statement written without a
    /// resolution. Zoomed out on the whole map that is the coarsest level; focused on a lot it is
    /// the finest, and in between it is the middle one - so the neighbourhood is correct at every
    /// zoom, and the level only ever changes where it cannot be seen changing.
    /// </para>
    ///
    /// <para>
    /// <b>The finest level subdivides, and that is a separate axis from the factor.</b> Before it
    /// was added the three levels differed only in how much map one quad covered, which means the
    /// closest of them was still one flat-shaded quad per tile: at the zoom where a tile is five
    /// pixels wide, that is a grid of five-pixel facets, and the answer to "zooming in shows
    /// detail" was "zooming in shows the same mesh, larger". <see cref="Subdivisions"/> is
    /// <c>CityGeometry.SubRegenMeshVerts</c>'s <c>subdiv</c> - four - so the closest level cuts
    /// every tile into four by four Catmull-Rom quads with interpolated normals, which is what the
    /// city itself draws when the camera is in among the houses. It is a separate axis because the
    /// two questions are separate: the factor is what a wide camera needs to be cheap, the
    /// subdivision is what a close camera needs to be smooth.
    /// </para>
    /// </summary>
    public readonly struct NeighbourhoodMeshLod
    {
        /// <summary>One quad per tile. The full-detail level.</summary>
        public const int Full = 1;

        /// <summary>One quad per 2x2 tiles.</summary>
        public const int Half = 2;

        /// <summary>One quad per 4x4 tiles.</summary>
        public const int Quarter = 4;

        /// <summary>
        /// Ground quads per map tile edge at the closest level: <c>CityGeometry.SubRegenMeshVerts</c>'s
        /// <c>subdiv</c> argument, which is four at both of <c>Terrain.cs</c>'s call sites.
        ///
        /// <para>
        /// Four and not two, because the cost is not linear in it: the subdivided level emits a
        /// <c>(subdiv + 1) * (subdiv + 1)</c> vertex grid per tile, so four is 25 vertices where
        /// one is 4 - 6.25x - and 25 of them is where the surface stops being a grid of facets at
        /// the closest zoom. It is not applied to the whole map: a 512x512 map at four is a
        /// quarter of a million tiles' worth of that, hundreds of megabytes of vertex buffer, so
        /// the close level is built per <see cref="NeighbourhoodCityMeshBuilder.SliceSize"/>-tile
        /// slice for the region the camera is in. See
        /// <see cref="NeighbourhoodCityMeshBuilder.BuildSubdivSlice"/>.
        /// </para>
        /// </summary>
        public const int MaxSubdivisions = 4;

        /// <summary>Every factor the builder will accept, coarsest last.</summary>
        public static readonly int[] Factors = { Full, Half, Quarter };

        /// <summary>
        /// How finely a level cuts up one map tile, from its factor. Only the finest level
        /// subdivides: the whole point of a coarse level is that a quad is cheap, and four by four
        /// quads per tile would make it dearer than the level it replaced.
        /// </summary>
        /// <param name="factor">Map tiles per quad edge.</param>
        public static int SubdivisionsFor(int factor)
        {
            return ClampFactor(factor) <= Full ? MaxSubdivisions : 1;
        }

        /// <summary>
        /// The fewest tiles of the level that may span the view. Below this the level's quads are
        /// coarse enough on screen to be worth avoiding.
        ///
        /// <para>
        /// 160, and the reasoning is a screen-pixel count in tiles: the neighbourhood bitmap is
        /// 800x600, so a 160-tile span is about five pixels per tile, and a level that still spans
        /// 160 of its own tiles across that has quads of a few pixels each - which is the
        /// resolution an overview of a map is read at. A whole 512-tile map framed at full screen
        /// spans 724 tiles (the diamond's diagonal; see
        /// <see cref="NeighbourhoodCameraPlacement.VisibleWidthFor"/>), so the quarter level gives
        /// 181 and is chosen; a lot focused at the ordinary 1.5x zoom spans 483, where the quarter
        /// level would give 121 and the half level 241, so the half level is chosen; and anything
        /// closer than about 160 tiles gets the full-detail one.
        /// </para>
        /// </summary>
        public const float MinTilesAcross = 160f;

        /// <summary>
        /// The view has to be closer than this, in tiles, before a level subdivides each tile.
        ///
        /// <para>
        /// <b>Why this is a second threshold and not the same one.</b>
        /// <see cref="MinTilesAcross"/> is the coarse level's question - "is a tile small enough
        /// on screen that a 2x2 or 4x4 block of them is indistinguishable from ground" - and the
        /// answer at 160 tiles is yes, which is why the full-detail factor is chosen there. But
        /// subdivision asks the opposite question: is a tile <em>large</em> enough on screen that
        /// one flat quad of it is visible as a flat quad. At 160 tiles across, on the documented
        /// 800x600 neighbourhood bitmap, a tile is five pixels wide, and cutting it into sixteen is
        /// sixteen quads where a pixel or two of them are - sixteen times the geometry for nothing,
        /// sixteen times the draw calls, and a bigger answer to "why is this frame expensive".
        /// </para>
        ///
        /// <para>
        /// Forty-eight, because that is about sixteen pixels per tile on an 800-wide view, which is
        /// the point at which a per-tile facet is a shape rather than a shading gradient. It is the
        /// same reasoning as <see cref="MinTilesAcross"/> and in the other direction: that one is
        /// the widest view at which a coarse level is good enough, this one is the closest view at
        /// which a subdivided one is worth having, and the band between them - 48 to 160 tiles
        /// across - is where a tile is drawn flat, which is exactly how a five-pixel tile should
        /// be drawn.
        /// </para>
        /// </summary>
        public const float SubdivTilesAcross = 48f;

        /// <summary>Map tiles per quad edge.</summary>
        public int Factor { get; }

        /// <summary>
        /// Ground quads per map tile edge at this level, from <see cref="SubdivisionsFor"/>.
        /// One at every coarse level, <see cref="MaxSubdivisions"/> at the closest.
        /// </summary>
        public int Subdivisions { get; }

        /// <summary>
        /// The width above which <paramref name="factor"/> stops being good enough: the point at
        /// which it becomes eligible itself. Every release threshold in the level rule is this
        /// number times <c>1 + Hysteresis</c>.
        /// </summary>
        /// <param name="factor">Map tiles per quad edge.</param>
        public static float BoundaryAboveFactor(int factor)
        {
            return MinTilesAcross * ClampFactor(factor);
        }

        /// <summary>The coarsest level that still spans <see cref="MinTilesAcross"/> tiles.</summary>
        public static NeighbourhoodMeshLod ForTilesAcross(float tilesAcross)
        {
            //Nonsense in, the finest level out: a camera that is looking at a fraction of a tile, at
            //nothing at all, or at a width no viewport could produce, must never select a coarse
            //mesh. The seams between coarse blocks are exactly what a close-up view is made of, and
            //an infinite width is a broken camera rather than a very wide one.
            if (!(tilesAcross > 0f) || float.IsInfinity(tilesAcross) || float.IsNaN(tilesAcross))
            {
                return new NeighbourhoodMeshLod(Full, MaxSubdivisions);
            }

            var chosen = Full;
            for (int i = Factors.Length - 1; i >= 0; i--)
            {
                var factor = Factors[i];
                if (factor <= 1) break;
                if (tilesAcross / factor >= MinTilesAcross)
                {
                    chosen = factor;
                    break;
                }
            }
            //The two axes are decided separately and that is the point: the factor answers whether
            //the view is too wide to afford one quad per tile, and the subdivision answers whether
            //it is close enough for one quad per tile to be visibly a quad. See
            //SubdivTilesAcross.
            return new NeighbourhoodMeshLod(chosen, SubdivisionsForTilesAcross(tilesAcross));
        }

        /// <summary>
        /// How finely a view of this width cuts each map tile up, from
        /// <see cref="SubdivTilesAcross"/>. The rule on its own, so the selector's hysteresis can
        /// apply its margin to it without re-deriving the boundary.
        /// </summary>
        /// <param name="tilesAcross">How many map tiles the view spans.</param>
        public static int SubdivisionsForTilesAcross(float tilesAcross)
        {
            if (!(tilesAcross > 0f) || float.IsInfinity(tilesAcross) || float.IsNaN(tilesAcross))
            {
                //The safe direction, as everywhere else: a camera the rule cannot read gets the
                //finest geometry, because that is the geometry with no visible seams in it.
                return MaxSubdivisions;
            }
            return tilesAcross < SubdivTilesAcross ? MaxSubdivisions : 1;
        }

        /// <summary>The level with a given factor, clamped to one that exists.</summary>
        public static NeighbourhoodMeshLod ForFactor(int factor)
        {
            var clamped = ClampFactor(factor);
            return new NeighbourhoodMeshLod(clamped, SubdivisionsFor(clamped));
        }

        /// <summary>
        /// A factor, clamped to one of <see cref="Factors"/>. Nearest, and a tie goes to the
        /// <em>coarser</em> level, so a factor between two real ones costs frames rather than
        /// quality - the safe direction for a rule whose only job is to spend less.
        /// </summary>
        public static int ClampFactor(int factor)
        {
            if (factor <= Full) return Full;
            var best = Factors[0];
            var bestDistance = int.MaxValue;
            for (int i = 0; i < Factors.Length; i++)
            {
                var distance = Math.Abs(Factors[i] - factor);
                // "<=" rather than "<" so a later, coarser level wins a tie
                if (distance > bestDistance) continue;
                bestDistance = distance;
                best = Factors[i];
            }
            return best;
        }

        /// <summary>
        /// A subdivision count, clamped to something the builder will actually emit: 1 or
        /// <see cref="MaxSubdivisions"/>. Same tie-breaking as <see cref="ClampFactor"/> - the
        /// coarser (one quad per tile) answer - because a caller with a number between the two is
        /// better served by cheap geometry than by a vertex grid it did not ask for.
        /// </summary>
        public static int ClampSubdivisions(int subdivisions)
        {
            if (subdivisions <= 1) return 1;
            return subdivisions >= MaxSubdivisions ? MaxSubdivisions : 1;
        }

        /// <summary>Builds a level directly. Use <see cref="ForFactor"/> to clamp the factor.</summary>
        /// <param name="factor">Map tiles per quad edge.</param>
        public NeighbourhoodMeshLod(int factor)
            : this(factor, SubdivisionsFor(factor))
        {
        }

        /// <summary>Builds a level with an explicit subdivision count.</summary>
        /// <param name="factor">Map tiles per quad edge.</param>
        /// <param name="subdivisions">Ground quads per map tile edge.</param>
        public NeighbourhoodMeshLod(int factor, int subdivisions)
        {
            Factor = factor;
            Subdivisions = ClampSubdivisions(subdivisions);
        }

        /// <summary>Whether this is the full-detail level.</summary>
        public bool IsFull
        {
            get { return Factor <= Full; }
        }

        /// <summary>
        /// Whether the level cuts each map tile up into more than one quad, and so has to be built
        /// for a region rather than for the whole map. See <see cref="MaxSubdivisions"/>.
        /// </summary>
        public bool IsSubdivided
        {
            get { return Subdivisions > 1; }
        }

        /// <summary>Whether two levels are the same level of detail, subdivision included.</summary>
        public static bool SameLevel(NeighbourhoodMeshLod a, NeighbourhoodMeshLod b)
        {
            return a.Factor == b.Factor && a.Subdivisions == b.Subdivisions;
        }

        public override string ToString()
        {
            switch (Factor)
            {
                case Half: return "half (one quad per 2x2 tiles)";
                case Quarter: return "quarter (one quad per 4x4 tiles)";
                default:
                    return IsSubdivided
                        ? "full (" + Subdivisions + " quads per tile, Catmull-Rom ground)"
                        : "full (one quad per tile)";
            }
        }
    }

    /// <summary>
    /// Picks the level of detail for a camera, with hysteresis, so that a zoom crossing a level
    /// boundary does not flicker.
    ///
    /// <para>
    /// <b>Why the stateless rule cannot be used directly.</b>
    /// <see cref="NeighbourhoodMeshLod.ForTilesAcross"/> is a pure function of the view's width,
    /// and that is what makes it testable. It is also why it flickers: a camera tweening through a
    /// boundary passes the exact width at which the rule says "quarter", and one frame later says
    /// "half", and the neighbourhood visibly pops twice on every zoom step. The pop is not a
    /// shading difference - it is a different set of buffers and a different mesh - so it is one of
    /// the few artefacts a player notices immediately.
    /// </para>
    ///
    /// <para>
    /// <b>What this adds is a deadband, not a delay.</b> A level is only left once the view has
    /// crossed the boundary by <see cref="Hysteresis"/> - fifteen per cent of it - and once it has
    /// crossed, the same margin applies in the other direction, so each boundary owns a band of
    /// widths in which the answer depends on where the camera came from. That is the standard
    /// hysteresis and it is what makes the level a function of the camera's <em>history</em>, which
    /// is the whole point: the same width gives the same answer on the way out as on the way back
    /// unless the player zoomed past the margin while inside the band.
    /// </para>
    ///
    /// <para>
    /// <b>The direction of the margin is the same in both directions, and that matters.</b> A level
    /// is <em>kept</em> while the view is inside the band and <em>left</em> when the view is
    /// outside it, whichever way it is going. So the fine level survives a small zoom-out past its
    /// own boundary, and the coarse level survives a small zoom-in - rather than a rule where one
    /// direction is sticky and the other is not, which is the version that still pops on the way
    /// back. A level is therefore held across its own boundary rather than dropped at it, and each
    /// level owns the gap between its neighbour's release point and its own - the half level is held
    /// from 368 tiles across to 736 - which is <see cref="IsInsideDeadband"/>'s answer and the
    /// reason a same-width-different-direction pair of answers is expected rather than a bug.
    /// <see cref="MinFramesBetweenChanges"/> is the belt to that pair of braces: even a camera that
    /// is genuinely moving through the band cannot change level faster than this, which also bounds
    /// how much of the screen can be in the middle of a swap.
    /// </para>
    ///
    /// <para>
    /// <b>Both axes get a deadband, because a level is two numbers.</b> The factor changes at
    /// <c>MinTilesAcross * factor</c> tiles across and the subdivision at
    /// <see cref="NeighbourhoodMeshLod.SubdivTilesAcross"/>, and a zoom can be sitting on one
    /// boundary while nowhere near the other - which is the normal case between 48 and 160 tiles
    /// across, where the factor is settled and the subdivision is what is still moving.
    /// <see cref="ClearedBoundary"/> therefore asks each question separately and both have to say
    /// yes. The first <see cref="Select"/> is exempt, and the reason is in
    /// <see cref="NeighbourhoodMeshLodSelector.Select(float, int)"/>.
    /// </para>
    ///
    /// <para>
    /// Pure and clock-free, so a test can sweep a zoom in both directions and count the changes.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodMeshLodSelector
    {
        /// <summary>
        /// How far past a level boundary the view has to go before the level changes, as a fraction
        /// of the boundary.
        ///
        /// <para>
        /// Fifteen per cent, and the number is a screen-space statement: the boundary between the
        /// half and the quarter level is at 320 tiles across, so the deadband is 48 tiles - about
        /// a fifth of the view at that zoom, which is more than a pop needs to be noticed and far
        /// less than a zoom step is wide, so it costs nothing in latency.
        /// </para>
        /// </summary>
        public const float Hysteresis = 0.15f;

        /// <summary>
        /// How many frames must pass between two level changes. Zero by default, so the width
        /// deadband alone is the rule and a test that sweeps a zoom sees exactly the deadband's
        /// shape; a camera that feeds it every frame can raise it to bound the swap rate.
        /// </summary>
        public const int DefaultMinFramesBetweenChanges = 0;

        private NeighbourhoodMeshLod _current;
        private int _framesHeld;
        private int _changes;
        private bool _selected;

        /// <summary>
        /// A selector sitting at <paramref name="initial"/>, or at the finest level when none is
        /// named.
        /// </summary>
        /// <param name="initial">The level the first <see cref="Select"/> confirms.</param>
        public NeighbourhoodMeshLodSelector(NeighbourhoodMeshLod? initial = null)
        {
            _current = initial ?? NeighbourhoodMeshLod.ForFactor(NeighbourhoodMeshLod.Full);
        }

        /// <summary>The level the draw is on now.</summary>
        public NeighbourhoodMeshLod Current
        {
            get { return _current; }
        }

        /// <summary>How many times the level has actually changed, for a log line and a test.</summary>
        public int Changes
        {
            get { return _changes; }
        }

        /// <summary>
        /// The level for a view of this width, which may or may not be a change.
        /// </summary>
        /// <param name="tilesAcross">How many map tiles the view spans.</param>
        public NeighbourhoodMeshLod Select(float tilesAcross)
        {
            return Select(tilesAcross, DefaultMinFramesBetweenChanges);
        }

        /// <summary>
        /// The level for a view of this width, with a floor on how often the level may change.
        /// </summary>
        /// <param name="tilesAcross">How many map tiles the view spans.</param>
        /// <param name="minFramesBetweenChanges">
        /// Frames to hold a level for before another change is allowed; zero or less means no
        /// floor.
        /// </param>
        public NeighbourhoodMeshLod Select(float tilesAcross, int minFramesBetweenChanges)
        {
            _framesHeld++;

            var wanted = NeighbourhoodMeshLod.ForTilesAcross(tilesAcross);
            if (NeighbourhoodMeshLod.SameLevel(_current, wanted))
            {
                _framesHeld = 0;
                _selected = true;
                return _current;
            }

            //The first call is not subject to the deadband. The selector starts at the finest
            //level, and a neighbourhood screen starts zoomed out on the whole map, so a deadband
            //here would refuse the very first change - a view of 724 tiles would sit on the
            //full-detail subdivided level for ever, because getting from it to the quarter level
            //needs 736 tiles of width and no view that starts a world at 724 will ever get there.
            //History needs something to be history about; there is none on the first frame.
            if (_selected && minFramesBetweenChanges > 0 && _framesHeld < minFramesBetweenChanges)
            {
                return _current;
            }

            //The deadband. A change is allowed only once the view is past the wanted level's own
            //boundary by the margin, in the direction it wants to move - and each of the two axes
            //has to agree, so crossing the factor boundary and the subdivision boundary are
            //separate decisions that can be in different deadbands at the same time.
            if (_selected && !ClearedBoundary(tilesAcross, wanted, _current)) return _current;

            _current = wanted;
            _changes++;
            _framesHeld = 0;
            _selected = true;
            return _current;
        }

        /// <summary>
        /// Whether the view has moved far enough past <paramref name="wanted"/>'s own reach for the
        /// level to change, in whichever direction it is being asked to go.
        ///
        /// <para>
        /// <b>One boundary, and the direction only says which side of it.</b> A level's own reach is
        /// <c>MinTilesAcross * factor</c> - the width at which that level becomes eligible at all -
        /// and it is kept until the view is <c>Hysteresis</c> past it: a coarser level is adopted
        /// only <em>over</em> that point and a finer one only <em>under</em> it. So a level is held
        /// across its own boundary rather than dropped at it, in both directions, which is the
        /// property that stops a zoom popping on the way back as well as on the way out.
        /// </para>
        ///
        /// <para>
        /// <b>Where the band comes from.</b> A level is not dropped at its own boundary but at the
        /// next one's, so each level owns the gap between its neighbour's release point and its own:
        /// the half level is held from 320 * 1.15 = 368 tiles (where the full level is released) up
        /// to 640 * 1.15 = 736 (where the quarter level is adopted), and inside that whole range the
        /// answer depends on which way the camera came. That is a wide band on purpose - it is the
        /// price of never popping - and it is bounded, not a latch: keep zooming and it gives way.
        /// <see cref="IsInsideDeadband"/> says where it is.
        /// </para>
        ///
        /// <para>
        /// The subdivision is the other shape and gets the other construction. It has one threshold
        /// rather than a ladder of levels, so its band is the margin either side of that threshold:
        /// subdividing switches on under <c>SubdivTilesAcross * (1 - Hysteresis)</c> and off over
        /// <c>SubdivTilesAcross * (1 + Hysteresis)</c>, which is a band around a number rather than a
        /// gap between two levels. A level of detail with a single boundary has to put its margin on
        /// both sides of it or it has no band at all.
        /// </para>
        /// </summary>
        /// <param name="tilesAcross">How many map tiles the view spans.</param>
        /// <param name="wanted">The level the stateless rule asked for.</param>
        /// <param name="from">The level in hand.</param>
        public static bool ClearedBoundary(float tilesAcross, NeighbourhoodMeshLod wanted,
            NeighbourhoodMeshLod from)
        {
            if (NeighbourhoodMeshLod.SameLevel(wanted, from)) return false;
            if (!(tilesAcross > 0f) || float.IsInfinity(tilesAcross) || float.IsNaN(tilesAcross))
            {
                //A width the rule cannot read is not a reason to swap buffers: keep what is in
                //hand, which is the level the last frame that could be read asked for.
                return false;
            }

            if (wanted.Factor != from.Factor)
            {
                var release = NeighbourhoodMeshLod.BoundaryAboveFactor(wanted.Factor)
                    * (1f + Hysteresis);
                if (wanted.Factor > from.Factor)
                {
                    //coarser: over the wanted level's own reach, by the margin
                    if (!(tilesAcross >= release)) return false;
                }
                //finer: under the same number, by the same margin
                else if (!(tilesAcross < release)) return false;
            }

            if (wanted.Subdivisions != from.Subdivisions)
            {
                var boundary = NeighbourhoodMeshLod.SubdivTilesAcross;
                if (wanted.Subdivisions > from.Subdivisions)
                {
                    //subdividing on: inside the threshold, by the margin
                    if (!(tilesAcross < boundary * (1f - Hysteresis))) return false;
                }
                //subdividing off: outside it, by the same margin
                else if (!(tilesAcross >= boundary * (1f + Hysteresis))) return false;
            }

            return true;
        }

        /// <summary>
        /// Whether a width is inside a deadband, which is to say whether the level there depends on
        /// which way the camera came.
        ///
        /// <para>
        /// Stated as a function so a test can say where the answer is supposed to be the same on the
        /// way out and on the way back, and where it is allowed not to be. Without it, "the same
        /// width gives the same level in both directions" is either false inside every deadband or
        /// only true because the bands are too narrow to matter.
        /// </para>
        /// </summary>
        /// <param name="tilesAcross">How many map tiles the view spans.</param>
        public static bool IsInsideDeadband(float tilesAcross)
        {
            if (!(tilesAcross > 0f) || float.IsInfinity(tilesAcross) || float.IsNaN(tilesAcross))
            {
                return false;
            }
            //The factor axis: between the finest level's release point and the coarsest level's,
            //because outside those two the answer is the same whichever way the camera came - below
            //the finest there is only one level left, and above the coarsest it is already in hand.
            var low = NeighbourhoodMeshLod.BoundaryAboveFactor(NeighbourhoodMeshLod.Factors[0])
                * (1f + Hysteresis);
            var high = NeighbourhoodMeshLod.BoundaryAboveFactor(
                NeighbourhoodMeshLod.Factors[NeighbourhoodMeshLod.Factors.Length - 1])
                * (1f + Hysteresis);
            if (tilesAcross >= low && tilesAcross < high) return true;
            //and the subdivision's own threshold, the other shape
            return IsInBand(tilesAcross, NeighbourhoodMeshLod.SubdivTilesAcross);
        }

        private static bool IsInBand(float tilesAcross, float boundary)
        {
            if (boundary <= 0f) return false;
            return tilesAcross >= boundary * (1f - Hysteresis)
                && tilesAcross < boundary * (1f + Hysteresis);
        }

        /// <summary>Forgets the history, so the next <see cref="Select"/> takes the stateless rule.</summary>
        public void Reset()
        {
            _current = NeighbourhoodMeshLod.ForFactor(NeighbourhoodMeshLod.Full);
            _framesHeld = 0;
            _changes = 0;
            _selected = false;
        }

        /// <summary>One line for a log, naming the level and how often it has moved.</summary>
        public string Describe()
        {
            return _current + " (changed " + _changes + " times, hysteresis "
                + (Hysteresis * 100f).ToString("0") + "%)";
        }
    }

    /// <summary>
    /// Turns a generated <see cref="NeighbourhoodMap"/> into a terrain mesh.
    ///
    /// <para><b>Why this exists instead of <c>CityGeometry.RegenMeshVerts</c>.</b> Simitone.Client
    /// cannot reference <c>tso.client</c> (its type names clash across the client), so the mesh
    /// algebra is reproduced here rather than reused.
    /// </para>
    ///
    /// <para><b>What is reproduced, and what is deliberately not.</b> Reproduced, because it is
    /// what makes a generated neighbourhood look like a city rather than a heightmap: the
    /// isometric diamond clip, the ten-tile fade skirt past it, the five-layer split by terrain
    /// type, the per-vertex surface normals from the elevation field, the elevation scale of
    /// twelve (<see cref="ElevationScale"/>, and the whole of it in
    /// <see cref="WorldHeightFor"/>), the four repeats per texture tile, the road and corner quads
    /// laid over the ground, and the fact that a road is only drawn strictly inside the diamond so a
    /// street never overhangs the edge. Not reproduced, on purpose and by decision: the blend-mask
    /// pass (§0 decision 3 - and the shader reads <c>(-1,-1)</c> as "no mask" anyway), the per-tile
    /// vertex colour tint, and the shadow map.
    /// </para>
    ///
    /// <para><b>The near-view subdivision is reproduced, per slice.</b> It used to be in the
    /// not-reproduced list, and that was the reason zooming in showed no more detail than zooming
    /// out: the finest level was one quad per tile. <see cref="BuildSubdivSlice"/> now builds what
    /// <c>CityGeometry.SubRegenMeshVerts</c> builds - a Catmull-Rom height and a bilinearly
    /// interpolated normal across each tile, cut into <see cref="NeighbourhoodMeshLod.MaxSubdivisions"/>
    /// by <see cref="NeighbourhoodMeshLod.MaxSubdivisions"/> quads - and it builds it for one
    /// <see cref="SliceSize"/>-tile slice at a time, which is the same rectangle and the same
    /// subdivision count <c>Terrain.cs</c> hands it. The slice is the reason it is affordable: the
    /// close level is only wanted when the camera is in among the houses, where a handful of
    /// slices is the whole of it, and at that zoom a tile is a handful of pixels, so a whole-map
    /// subdivided buffer would be hundreds of megabytes of geometry for what is on screen.
    /// </para>
    ///
    /// <para>
    /// <b>The two paths that draw this mesh are consistent by sharing the rule, not by sharing a
    /// buffer.</b> The neighbourhood screen picks a level from the camera's tiles across, and the
    /// in-lot surround is built from <see cref="TryBuild(NeighbourhoodMap, out NeighbourhoodCityMeshReport)"/>
    /// - the whole-map full-detail level, un-subdivided, cropped to the lot's surroundings. Those
    /// are not two answers to one question: the surround is drawn at a lot-focused zoom, which is
    /// well above <see cref="NeighbourhoodMeshLod.SubdivTilesAcross"/>, so the neighbourhood screen
    /// would not subdivide there either, and the only close ground in the lot view is the lot's own
    /// mesh, which <c>FSO.LotView</c> subdivides itself. The same rule, the same vertex layout and
    /// the same technique at both, and the subdivision switches on in both the moment a camera
    /// genuinely gets inside a few lots - which is what makes the two paths agreeing a property
    /// rather than a coincidence.
    /// </para>
    ///
    /// <para><b>The vertical scale, in one place.</b> <see cref="ElevationScale"/>,
    /// <see cref="LotTerrainFactor"/>, <see cref="LotAltitudeUnitsPerArryUnit"/>,
    /// <see cref="TilesPerArryUnit"/>, <see cref="ElevationUnitsPerArryUnit"/> and the two functions
    /// at the bottom of that block are the whole vertical contract, and the mesh, the facade
    /// placement, the camera and the layout-to-map seam all read them rather than each keeping its
    /// own divisor. The relationship, stated once so that nobody has to re-derive it:
    /// <c>1 tile = 12 elevation bytes = 16/3 ARRY(0) units</c>, so
    /// <c>1 ARRY(0) unit = 3/16 of a tile = 9/4 elevation bytes</c>. A lot's own altitude is in
    /// <c>ARRY(0)</c> units and the map's is in bytes, and the map-to-world conversion that mixes
    /// the two has to use 9/4 and not 12 - which is
    /// <see cref="ElevationByteForArryUnit"/>, and which is
    /// <c>NeighbourhoodMapAdapter.QuantiseAltitude</c>'s to use.
    /// </para>
    ///
    /// <para><b>Pure, and tested against a second derivation.</b> No graphics device, no content,
    /// no file, no clock. <see cref="NeighbourhoodCityMeshProbe"/> walks the same map
    /// independently - an analytic diamond area rather than the chunked loop, and a per-tile
    /// decision rather than a vertex count - and the two have to agree, so a change to either that
    /// the other does not share is a failing test rather than a different-looking neighbourhood.
    /// </para>
    ///
    /// <para>
    /// The neighbourhood draw meshes through <see cref="Build"/> and <see cref="BuildSubdivSlice"/>
    /// (via <c>NeighbourhoodMeshGeometry</c>), and the in-lot surround through <see cref="TryBuild"/>
    /// and <c>SimitoneSurroundingsCrop</c>; this is the one mesh path.
    /// </para>
    /// </summary>
    public static class NeighbourhoodCityMeshBuilder
    {
        /// <summary>How many ground layers the mesh has: grass, sand, rock, snow, water.</summary>
        public const int LayerCount = 5;

        /// <summary>Side of the map this builder meshes.</summary>
        public const int MapSize = 512;

        /// <summary>Tile edge of one of the 32x32 chunks the builder walks.</summary>
        public const int ChunkSize = 16;

        /// <summary>Chunks per axis.</summary>
        public const int ChunksPerAxis = MapSize / ChunkSize;

        /// <summary>
        /// How many chunks the map is cut into, and therefore how long every per-chunk index
        /// array is (plus the one sentinel entry).
        /// </summary>
        public const int ChunkCount = ChunksPerAxis * ChunksPerAxis;

        /// <summary>
        /// Tile edge of one slice of subdivided ground: thirty-two, which is
        /// <c>CityGeometry.SubRegenMeshVerts</c>'s <c>new Rectangle(slicex * 16, slicey * 16, 32, 32)</c>
        /// at both of <c>Terrain.cs</c>'s call sites.
        ///
        /// <para>
        /// Thirty-two and not sixteen because that is what the city uses, and because it is the
        /// right size twice over: a slice is two chunks by two, so the per-chunk index bookkeeping
        /// the cull and the draw ranges rely on is exact - a chunk is either wholly in a slice or
        /// wholly out of it, and a slice boundary can never fall inside a chunk and leave half a
        /// chunk drawn from one buffer and half from another. It is also about a megabyte of
        /// vertices, which is a unit of work small enough to build between two frames.
        /// </para>
        /// </summary>
        public const int SliceSize = 32;

        /// <summary>Slices per axis.</summary>
        public const int SlicesPerAxis = MapSize / SliceSize;

        /// <summary>How many slices the map is cut into, and how long the slice cache is.</summary>
        public const int SliceCount = SlicesPerAxis * SlicesPerAxis;

        /// <summary>Chunks per slice edge, which is what makes a slice and a chunk grid agree.</summary>
        public const int ChunksPerSliceEdge = SliceSize / ChunkSize;

        /// <summary>
        /// The tile square a slice covers, in map tiles.
        /// </summary>
        /// <param name="slice">The slice index, row-major over the slice grid.</param>
        public static Rectangle SliceBounds(int slice)
        {
            var sx = slice % SlicesPerAxis;
            var sy = slice / SlicesPerAxis;
            return new Rectangle(sx * SliceSize, sy * SliceSize, SliceSize, SliceSize);
        }

        /// <summary>The slice covering a tile, or <c>-1</c> if the tile is off the map.</summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public static int SliceAt(int x, int y)
        {
            if (x < 0 || y < 0 || x >= MapSize || y >= MapSize) return -1;
            return x / SliceSize + (y / SliceSize) * SlicesPerAxis;
        }

        /// <summary>The slice index for a slice column and row, or <c>-1</c> if out of range.</summary>
        /// <param name="sliceX">Slice column.</param>
        /// <param name="sliceY">Slice row.</param>
        public static int SliceIndex(int sliceX, int sliceY)
        {
            if (sliceX < 0 || sliceY < 0 || sliceX >= SlicesPerAxis || sliceY >= SlicesPerAxis) return -1;
            return sliceX + sliceY * SlicesPerAxis;
        }

        /// <summary>
        /// The slices a tile rectangle touches, ascending.
        ///
        /// <para>
        /// The camera's whole visible region has to be padded by a slice on every side, not just
        /// intersected: a slice is built on a worker and lands a frame or more later, and a
        /// rectangle intersected exactly would start a build for slices the camera is about to
        /// leave and would not start one for the slice the camera is about to enter. A pad of one
        /// is the smallest that makes the queue ahead of the camera rather than behind it.
        /// </para>
        /// </summary>
        /// <param name="region">A tile rectangle; its ends are order-insensitive and clipped.</param>
        public static int[] SlicesInRegion(Rectangle region)
        {
            var list = new List<int>();
            SlicesInRegion(region, list);
            return list.ToArray();
        }

        /// <summary>
        /// The slice a chunk belongs to, or <c>-1</c> if the chunk index is not one of ours.
        ///
        /// <para>
        /// A slice is a whole number of chunks - two by two - precisely so that this is a total
        /// function on the chunk grid: every chunk of the draw's cull is in exactly one slice, and
        /// the draw can ask "is this chunk's slice on the device" per chunk and get a definite
        /// answer. A slice that was not aligned to the chunk grid could not answer that, and the
        /// half-and-half chunk boundary it would create is the one thing that could make a refined
        /// region tear.
        /// </para>
        /// </summary>
        /// <param name="chunk">The chunk index, row-major over the chunk grid.</param>
        public static int SliceOfChunk(int chunk)
        {
            if (chunk < 0 || chunk >= ChunkCount) return -1;
            var cx = chunk % ChunksPerAxis;
            var cy = chunk / ChunksPerAxis;
            return (cx / ChunksPerSliceEdge) + (cy / ChunksPerSliceEdge) * SlicesPerAxis;
        }

        /// <summary>
        /// The chunk indices a slice covers, ascending. At most
        /// <c>ChunksPerSliceEdge * ChunksPerSliceEdge</c> of them, and always the same ones.
        /// </summary>
        /// <param name="slice">The slice index, row-major over the slice grid.</param>
        public static int[] ChunksInSlice(int slice)
        {
            if (slice < 0 || slice >= SliceCount) return new int[0];
            var firstCx = (slice % SlicesPerAxis) * ChunksPerSliceEdge;
            var firstCy = (slice / SlicesPerAxis) * ChunksPerSliceEdge;
            var chunks = new int[ChunksPerSliceEdge * ChunksPerSliceEdge];
            var at = 0;
            for (int cy = firstCy; cy < firstCy + ChunksPerSliceEdge; cy++)
            {
                for (int cx = firstCx; cx < firstCx + ChunksPerSliceEdge; cx++)
                {
                    chunks[at++] = cx + cy * ChunksPerAxis;
                }
            }
            return chunks;
        }

        /// <summary>The slices a tile rectangle touches, appended to <paramref name="into"/>.</summary>
        /// <param name="region">A tile rectangle; its ends are order-insensitive and clipped.</param>
        /// <param name="into">The list to append to, in ascending slice order.</param>
        public static void SlicesInRegion(Rectangle region, List<int> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            var x0 = region.X;
            var y0 = region.Y;
            var x1 = region.X + region.Width;
            var y1 = region.Y + region.Height;
            if (x1 < x0) { var swap = x0; x0 = x1; x1 = swap; }
            if (y1 < y0) { var swap = y0; y0 = y1; y1 = swap; }
            if (x1 <= 0 || y1 <= 0) return;
            if (x0 >= MapSize || y0 >= MapSize) return;

            var firstSx = Math.Max(0, Math.Min(SlicesPerAxis - 1, x0 / SliceSize));
            var firstSy = Math.Max(0, Math.Min(SlicesPerAxis - 1, y0 / SliceSize));
            var lastSx = Math.Max(0, Math.Min(SlicesPerAxis - 1, (x1 - 1) / SliceSize));
            var lastSy = Math.Max(0, Math.Min(SlicesPerAxis - 1, (y1 - 1) / SliceSize));

            for (int sy = firstSy; sy <= lastSy; sy++)
            {
                for (int sx = firstSx; sx <= lastSx; sx++) into.Add(sx + sy * SlicesPerAxis);
            }
        }

        /// <summary>
        /// TSO's isometric diamond: the row where its left edge stops counting down and starts
        /// counting up. The mesh no longer clips to this diamond (see <see cref="ClipInset"/>);
        /// <see cref="NeighbourhoodPixelTransform"/> still fits the backdrop into it.
        /// </summary>
        public const int DiamondCentre = 306;

        /// <summary>The row where the diamond's right edge stops counting up and starts counting down.</summary>
        public const int DiamondRightPivot = 205;

        /// <summary>
        /// How far past the diamond edge a chunk still emits ground, so the border fades out
        /// instead of ending in a cliff. Ten tiles, at one ninth of a tile per step, which is what
        /// makes the fade a gradient rather than a step.
        /// </summary>
        public const int FadeRange = 10;

        /// <summary>How fast the fade rises, per tile outside the diamond.</summary>
        public const float FadeRate = 1f / 9f;

        // ---- the elevation scale, and the whole of it ------------------------------------------------
        //
        // Four numbers and two functions, and they are the entire vertical contract of the generated
        // world. Everything that has to agree about height agrees *here*: the mesh's vertices, the
        // facade placement's ground height, the camera's mean ground, the surround's ground join and
        // the layout-to-map quantisation. A second spelling of "elevation byte to tiles" is a bug
        // waiting for the byte that only one of the two converts, which is why there is one.

        /// <summary>
        /// Elevation bytes in one tile of world height: the divisor between a stored
        /// <see cref="NeighbourhoodMap.ElevationData"/> byte and world height.
        ///
        /// <para>
        /// Twelve, because that is what the city does with the very same byte.
        /// <c>FSO.Client.Rendering.City.CityGeometry</c> and <c>Terrain</c> write
        /// <c>MapData.ElevationData[...] / 12.0f</c> into a vertex position, and the generated mesh
        /// is drawn by the city's own <c>RenderNCity</c> technique through the city's own camera -
        /// where x and z are absolute map tiles and one tile is one world unit. So the y of a vertex
        /// has to be in that unit as well: at any other divisor the ground is not a heightmap, it is
        /// a slope, and the shader's diffuse term lights it as one.
        /// </para>
        ///
        /// <para>
        /// <b>Everything vertical reads this one constant.</b> The vertices, the facade
        /// placement's <c>GroundHeightFor</c>, <see cref="MeanGroundHeight"/>, the surround's ground
        /// join, and <c>NeighbourhoodMapAdapter</c> - whose <c>ElevationUnitsPerTile</c> and
        /// <c>DequantiseAltitude</c> are derived from this rather than restating it. See
        /// <see cref="WorldHeightFor"/> for the function rather than the division.
        /// </para>
        /// </summary>
        public const float ElevationScale = 12f;

        /// <summary>
        /// The divisor between a stored elevation byte and a normal's rise, from
        /// <c>Terrain.GetNormalAt</c>'s <c>GetElevationPoint</c>.
        ///
        /// <para>
        /// Six and not <see cref="ElevationScale"/>, so a normal describes a slope twice as steep as
        /// the geometry under it is. That is FSO's own arithmetic - the same byte, divided by twelve
        /// for a position and by six for a normal - and it is reproduced rather than tidied, because
        /// the city's lighting is calibrated against it and a "corrected" normal set would darken
        /// every hillside in the game.
        /// </para>
        /// </summary>
        public const float NormalElevationScale = 6f;

        /// <summary>
        /// <c>FSO.LotView.Model.Blueprint.TerrainFactor</c>: world tiles per blueprint altitude
        /// unit. The lot view multiplies by three everywhere it converts an altitude into world
        /// units (<c>InterpAltitude</c> returns the value this multiplies, already in tiles), so the
        /// three is easy to lose; it is named separately so that nobody has to.
        /// </summary>
        public const float LotTerrainFactor = 3f / 160f;

        /// <summary>
        /// Blueprint altitude units in one <c>ARRY(0)</c> unit: ten, from <c>VMTS1Activator</c>'s
        /// <c>arch.Terrain.Heights = ARRY(0) * 10</c> - which is also the array it hands to
        /// <c>Blueprint.Altitude</c> with <c>BaseAlt</c> left at zero. Ten is the whole reason a
        /// unit-for-unit comparison with <see cref="LotTerrainFactor"/> is wrong by a factor of ten.
        /// </summary>
        public const float LotAltitudeUnitsPerArryUnit = 10f;

        /// <summary>
        /// World tiles one <c>ARRY(0)</c> unit is worth: three sixteenths of a tile, exactly.
        ///
        /// <para>
        /// <c>LotTerrainFactor * LotAltitudeUnitsPerArryUnit</c> = (3/160) * 10 = 3/16, written as
        /// the fraction it is because 3/16 is exactly representable and the composed float is not
        /// quite. A test asserts the two spellings agree.
        /// </para>
        ///
        /// <para>
        /// <b>This is the number the rest of the pipeline is easy to get wrong.</b> A TS1 lot's own
        /// ground altitude - <see cref="NeighbourhoodLot.BaseHeight"/>, the median of its
        /// <c>ARRY(0)</c> - is in <c>ARRY(0)</c> units, and inside the lot view it is three
        /// sixteenths of a tile of height, not a tile and not an eighth of one. Treating it as a tile
        /// is what <c>NeighbourhoodMapAdapter.QuantiseAltitude</c> does, and the error is
        /// <c>1 / (3/16) = 16/3 = 5.33</c>: every lot's stamped ground lands five and a third times
        /// too high, so a map built from real altitudes has hillsides steeper than anything the game
        /// ships, and a neighbour's own walls end up far below the surround that was drawn for them.
        /// See <see cref="ElevationByteForArryUnit"/>, which is the quantisation that does not.
        /// </para>
        /// </summary>
        public const float TilesPerArryUnit = 3f / 16f;

        /// <summary>
        /// <c>ARRY(0)</c> units of lot altitude in one tile of world height: sixteen thirds.
        /// </summary>
        public const float ArryUnitsPerTile = 16f / 3f;

        /// <summary>
        /// Elevation bytes in one <c>ARRY(0)</c> unit: nine quarters, exactly - an <c>ARRY(0)</c>
        /// unit is 3/16 of a tile and a tile is twelve of them.
        ///
        /// <para>
        /// This is the correct multiplier for <c>QuantiseAltitude</c>, against the twelve it uses.
        /// It is deliberately <em>not</em> twelve and not an integer: rounding one <c>ARRY(0)</c>
        /// unit onto whole elevation bytes costs an eighth of a tile, which is the smallest error
        /// the byte can express.
        /// </para>
        /// </summary>
        public const float ElevationUnitsPerArryUnit = 9f / 4f;

        /// <summary>
        /// A stored elevation byte as world height in tiles. <b>The conversion.</b>
        ///
        /// <para>
        /// Every height in the generated world - a vertex's y, a facade's ground, the camera's mean,
        /// the surround's ground join, the reverse of the layout's quantisation - is this function
        /// applied to a byte. It exists so that the divisor is written down once: the failure mode of
        /// a bare <c>/ 12f</c> repeated in six files is not a wrong number, it is a right number in
        /// five files and a different right number in the sixth.
        /// </para>
        /// </summary>
        /// <param name="elevationBytes">A <see cref="NeighbourhoodMap.ElevationData"/> byte.</param>
        public static float WorldHeightFor(float elevationBytes)
        {
            return elevationBytes / ElevationScale;
        }

        /// <summary>
        /// A TS1 lot's own <c>ARRY(0)</c> altitude as world height in tiles: the same space
        /// <see cref="WorldHeightFor"/> answers in, reached the other way round.
        ///
        /// <para>
        /// This is what a facade's <c>GroundOffsetFor</c> correction is measured against, and it is
        /// why the two must not be confused: a lot's own <c>ARRY(0)</c> altitude and the generated
        /// map's elevation byte are not the same quantity and are not the same size - see
        /// <see cref="TilesPerArryUnit"/>. Only for a raw <c>ARRY(0)</c> altitude; a live
        /// blueprint's <c>Blueprint.InterpAltitude</c> has already been multiplied by
        /// <see cref="LotTerrainFactor"/> and is in tiles.
        /// </para>
        /// </summary>
        /// <param name="arryUnits">A raw <c>ARRY(0)</c> altitude, e.g. a lot's base height.</param>
        public static float WorldHeightForArryUnit(float arryUnits)
        {
            return arryUnits * TilesPerArryUnit;
        }

        /// <summary>
        /// A lot's own <c>ARRY(0)</c> altitude as the map's elevation byte: the map-to-world
        /// conversion, and the inverse of <see cref="WorldHeightForArryUnit"/> through
        /// <see cref="WorldHeightFor"/>.
        ///
        /// <para>
        /// Rounded and clamped, because the byte cannot hold a cliff and a wrap would put a lot that
        /// is high on the map at the bottom of it. <b>This is the function
        /// <c>NeighbourhoodMapAdapter.QuantiseAltitude</c> has to use</b>; it currently multiplies by
        /// <see cref="ElevationScale"/> instead, which says one <c>ARRY(0)</c> unit is a whole tile
        /// and is <c>ElevationScale / <see cref="ElevationUnitsPerArryUnit"/> = 16/3</c> too large.
        /// </para>
        /// </summary>
        /// <param name="arryUnits">A raw <c>ARRY(0)</c> altitude, e.g. a lot's base height.</param>
        public static byte ElevationByteForArryUnit(float arryUnits)
        {
            var value = (int)Math.Round(arryUnits * ElevationUnitsPerArryUnit,
                MidpointRounding.AwayFromZero);
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }

        /// <summary>
        /// A stored elevation byte as a normal's rise, i.e. the one conversion that deliberately
        /// does <em>not</em> agree with <see cref="WorldHeightFor"/>. See
        /// <see cref="NormalElevationScale"/>.
        /// </summary>
        /// <param name="elevationBytes">A <see cref="NeighbourhoodMap.ElevationData"/> byte.</param>
        public static float NormalRiseFor(float elevationBytes)
        {
            return elevationBytes / NormalElevationScale;
        }

        /// <summary>How many map tiles one texture repeat covers.</summary>
        public const int TilesPerTextureRepeat = 4;

        /// <summary>
        /// How wide one road quad is: one map tile, because <c>AddRoadQuad</c> is handed the four
        /// corners of a single tile and spends the whole road atlas cell on them.
        /// </summary>
        /// <remarks>
        /// <b>This is the number that settles "how wide is a road, really".</b> The mesh does not
        /// choose a street width - the road graph does, by marking
        /// <see cref="RoadGraph.RoadEdge.Width"/> tiles per run, capped at
        /// <see cref="RoadGraph.MainStreetWidth"/> - and this builder draws one quad per marked
        /// tile, so the drawn width is exactly the marked width, with no scaling anywhere between.
        /// A street is therefore up to
        /// <see cref="NeighbourhoodMapAdapter.StreetTarmacMapTiles"/> map tiles of tarmac and
        /// <see cref="NeighbourhoodMapAdapter.StreetCorridorMapTiles"/> with its pavements, which
        /// in the lot view is
        /// <see cref="NeighbourhoodMapAdapter.StreetTarmacLotViewUnits"/> and
        /// <see cref="NeighbourhoodMapAdapter.StreetCorridorLotViewUnits"/> world units - which is
        /// what the lot view's own street is
        /// (<see cref="NeighbourhoodMapAdapter.LotViewStreetTarmacUnits"/>), so
        /// <see cref="NeighbourhoodMapAdapter.StreetTarmacDeficit"/> is one rather than the three
        /// it was. Written here because this is the file that turns the mask into geometry, and
        /// because a mesh that quietly halved or doubled a quad would make every one of those
        /// numbers a lie. <b>It is deliberately still one:</b> the correction belongs in the width
        /// the road graph marks, not in the quad that draws it.
        /// </remarks>
        public const int RoadQuadWidthMapTiles = 1;

        private static readonly Matrix RotationToNormalXY = Matrix.CreateRotationZ((float)(Math.PI / 2));
        private static readonly Matrix RotationToNormalZY = Matrix.CreateRotationX(-(float)(Math.PI / 2));

        /// <summary>
        /// How far the drawn region stands in from every edge of the map, in tiles: one
        /// <see cref="FadeRange"/> and the edge tile itself, so the whole fade skirt outside the
        /// drawn edge still lies on the map.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The drawn region is the map square inset by this, not TSO's isometric diamond.</b>
        /// The "Diamond" names (<see cref="DiamondLeft"/>, <see cref="DiamondRight"/>,
        /// <see cref="IsInsideDiamond"/>) are kept because two dozen call sites read them, but the
        /// clip they describe is now that square. The diamond (<see cref="DiamondCentre"/>,
        /// <see cref="DiamondRightPivot"/>, <see cref="DiamondRightBase"/>) covers about 48% of the
        /// map (125,460 tiles), and the shelf packer's 41-lot neighbourhood needs 122,989 tiles of
        /// footprint, so no packing puts every lot of a large neighbourhood inside it. The packer
        /// laid lots from tile (0,0), which is the diamond's cut corner, so a 10-lot neighbourhood
        /// had all 10 lots there and slot 4 had 24 of 41 (audit O1). At the camera's 45-degree yaw the
        /// whole square still reads as a diamond on screen.
        /// </para>
        /// <para>
        /// The diamond constants stay, because <see cref="NeighbourhoodPixelTransform"/> fits the
        /// 800x600 backdrop into that shape. The diamond lies inside this square, so its guarantee
        /// still holds.
        /// </para>
        /// </remarks>
        public const int ClipInset = FadeRange + 1;

        /// <summary>
        /// Where a map row's drawn region starts: the last tile <em>outside</em> it on the left, so
        /// a tile is inside when <c>x &gt; DiamondLeft(row)</c>. The same for every row, because
        /// the clip is the inset square (see <see cref="ClipInset"/>). Rows above and below the
        /// square keep the same span, and their ground is faded by <see cref="RowFadeTransparency"/>.
        /// </summary>
        /// <param name="row">The map row.</param>
        public static int DiamondLeft(int row)
        {
            return ClipInset - 1;
        }

        /// <summary>
        /// Where TSO's diamond's right edge starts, on row zero (no longer the mesh clip; see
        /// <see cref="ClipInset"/>). It counts up from here to the pivot
        /// row and then back down, so the widest row is <see cref="DiamondRightPivot"/>, not the
        /// centre row - the two edges of TSO's clip turn around at different rows.
        /// </summary>
        public const int DiamondRightBase = 307;

        /// <summary>
        /// One past a map row's last drawn tile; the same for every row (see <see cref="ClipInset"/>).
        /// One more than <c>MapSize - ClipInset</c> so the opaque ground runs from vertex
        /// <c>ClipInset - 1</c> to vertex <c>MapSize - ClipInset + 1</c>, symmetric about the map's
        /// centre, and the fade skirt reaches the last vertex on both sides.
        /// </summary>
        /// <param name="row">The map row.</param>
        public static int DiamondRight(int row)
        {
            return MapSize - ClipInset + 1;
        }

        /// <summary>
        /// Whether a tile is strictly inside the isometric diamond - the same rule the builder
        /// uses to decide whether a tile gets a road quad, and the rule anything that has to
        /// agree with the visible ground has to use.
        /// </summary>
        /// <remarks>
        /// <para>
        /// "Strictly inside" is the city's own wording: <c>if (j &gt; rXS &amp;&amp; j &lt; rXE)</c>
        /// gates the road pass, while the ground pass admits the ten-tile fade skirt on either
        /// side. So this is a test of the <em>road</em> region, and the name says so.
        /// </para>
        /// <para>
        /// It is a named predicate rather than an expression repeated at each call site because
        /// getting it wrong is silent in the worst way: a row's first tile is excluded and its
        /// last is not, which is one tile's difference at the border and not obviously a mistake
        /// anywhere else, so the error shows up as a street that stops one tile short of the
        /// edge and nobody can say why. The foliage pass uses it to keep the tree budget inside
        /// the part of the map that is actually drawn.
        /// </para>
        /// </remarks>
        /// <param name="x">Tile x.</param>
        /// <param name="row">Tile y (the map row).</param>
        public static bool IsInsideDiamond(int x, int row)
        {
            //rows use the same strict bounds as columns: [ClipInset, MapSize - ClipInset)
            if (row <= DiamondLeft(row) || row >= DiamondRight(row)) return false;
            return x > DiamondLeft(row) && x < DiamondRight(row);
        }

        /// <summary>
        /// The tile square a chunk's geometry can reach, in map tiles.
        /// </summary>
        /// <remarks>
        /// The builder walks a chunk's rows against both the chunk's own column band and the
        /// diamond's fade skirt, so a chunk's quads can sit up to <see cref="FadeRange"/> tiles
        /// outside its own 16x16. The rectangle this returns is the union of the two, and it is
        /// what a frustum test has to use - a test against the bare 16x16 drops the skirt quads
        /// that make the map edge fade rather than end in a cliff.
        /// </remarks>
        /// <param name="chunk">The chunk index, row-major over the chunk grid.</param>
        public static Rectangle ChunkBounds(int chunk)
        {
            var cx = chunk % ChunksPerAxis;
            var cy = chunk / ChunksPerAxis;
            return new Rectangle(
                cx * ChunkSize - FadeRange,
                cy * ChunkSize - FadeRange,
                ChunkSize + FadeRange * 2,
                ChunkSize + FadeRange * 2);
        }

        /// <summary>
        /// The chunk indices whose 16x16 core overlaps a tile rectangle, row-major and ascending.
        /// </summary>
        /// <remarks>
        /// This is the pure half of the camera cull: give it the tile rectangle the frustum
        /// covers and it hands back the chunks worth submitting. It is deliberately independent
        /// of <see cref="Microsoft.Xna.Framework.Graphics.BoundingFrustum"/> so a test can
        /// assert it without a camera, and so the frustum-to-tiles step is the only thing the GPU
        /// code has to get right on its own.
        /// </remarks>
        /// <param name="region">A tile rectangle; its ends are order-insensitive and clipped.</param>
        public static int[] ChunksInRegion(Rectangle region)
        {
            var list = new List<int>();
            ChunksInRegion(region, list);
            return list.ToArray();
        }

        /// <summary>
        /// The chunk indices whose 16x16 core overlaps a tile rectangle, appended to
        /// <paramref name="into"/>.
        /// </summary>
        /// <param name="region">A tile rectangle; its ends are order-insensitive and clipped.</param>
        /// <param name="into">The list to append to, in ascending chunk order.</param>
        public static void ChunksInRegion(Rectangle region, List<int> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            var x0 = region.X;
            var y0 = region.Y;
            var x1 = region.X + region.Width;
            var y1 = region.Y + region.Height;
            if (x1 < x0) { var swap = x0; x0 = x1; x1 = swap; }
            if (y1 < y0) { var swap = y0; y0 = y1; y1 = swap; }
            if (x1 <= 0 || y1 <= 0) return;
            if (x0 >= MapSize || y0 >= MapSize) return;

            //integer division truncates towards zero, so a negative origin would wrap; the clamp
            //first and the max(0, ..) second are both load-bearing
            var firstCx = Math.Max(0, Math.Min(ChunksPerAxis - 1, x0 / ChunkSize));
            var firstCy = Math.Max(0, Math.Min(ChunksPerAxis - 1, y0 / ChunkSize));
            var lastCx = Math.Max(0, Math.Min(ChunksPerAxis - 1, (x1 - 1) / ChunkSize));
            var lastCy = Math.Max(0, Math.Min(ChunksPerAxis - 1, (y1 - 1) / ChunkSize));

            for (int cy = firstCy; cy <= lastCy; cy++)
            {
                for (int cx = firstCx; cx <= lastCx; cx++)
                {
                    into.Add(cx + cy * ChunksPerAxis);
                }
            }
        }

        /// <summary>
        /// The box a chunk's geometry can reach, in world units.
        /// </summary>
        /// <remarks>
        /// <see cref="ChunkBounds"/> is the same rectangle in tiles; this is it in the space the
        /// mesh lives in, which is what a frustum test needs. The height range is the whole one the
        /// elevation bytes can express rather than a typical map's, because the test has to be
        /// conservative in the safe direction: a chunk whose box is a hair inside the frustum and
        /// whose geometry is a hair outside it is not a case worth the risk of dropping.
        /// </remarks>
        /// <param name="chunk">The chunk index, row-major over the chunk grid.</param>
        public static BoundingBox ChunkBounds3D(int chunk)
        {
            var bounds = ChunkBounds(chunk);
            return new BoundingBox(
                new Vector3(bounds.X, 0f, bounds.Y),
                new Vector3(bounds.X + bounds.Width, MaxChunkHeight, bounds.Y + bounds.Height));
        }

        /// <summary>
        /// The highest world height any tile can reach: the largest elevation byte on the city's
        /// own scale, i.e. <see cref="WorldHeightFor"/> of 255.
        /// </summary>
        public const float MaxChunkHeight = 255f / ElevationScale;

        /// <summary>
        /// The chunks whose geometry a frustum can touch, ascending.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>A per-chunk box test, not a rectangle of the frustum's corners.</b> The corner
        /// rectangle is what the renderer used to do, and it is very loose: a frustum is a wedge,
        /// so the axis-aligned box around its eight corners covers the map long before the view
        /// does. Measured on a lot-focused camera - 64 tiles across, the case the cull exists for -
        /// the corner rectangle kept 361 of 1024 chunks against the 25 a box test keeps. It is
        /// conservative in the safe direction, so it is not wrong, but it throws away most of what
        /// the cull was for, and the far plane is what makes it so bad: eight thousand units out,
        /// the wedge is enormous whichever way it points.
        /// </para>
        /// <para>
        /// The box test is also the one that is <em>checkable</em>, because "which chunks does this
        /// camera see" becomes a pure function of a matrix rather than a property of a frame. A
        /// camera pointing away from the map keeps nothing; a camera framing the whole map keeps
        /// all 1024; a camera on one lot keeps a handful. Those are three assertions rather than
        /// three screenshots, and the middle one is the guarantee that a full-screen view can never
        /// be a blank screen.
        /// </para>
        /// </remarks>
        /// <param name="frustum">What is on screen, in the same space as the mesh.</param>
        /// <param name="into">The list to append to, in ascending chunk order.</param>
        public static void ChunksInFrustum(BoundingFrustum frustum, List<int> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            if (frustum == null) return;
            for (int chunk = 0; chunk < ChunkCount; chunk++)
            {
                if (frustum.Intersects(ChunkBounds3D(chunk))) into.Add(chunk);
            }
        }

        /// <summary>
        /// Turns a set of visible chunks into the fewest draw ranges that cover exactly them.
        /// </summary>
        /// <remarks>
        /// Consecutive chunks are consecutive index ranges, so a run of them is one
        /// <c>DrawIndexedPrimitives</c>. A zoomed-in view of a 512x512 map touches a handful of
        /// chunks and submits two or three ranges; the full-screen view touches all of them and
        /// submits one, which is exactly the old behaviour - so culling costs nothing when there
        /// is nothing to cull.
        /// </remarks>
        /// <param name="chunkIndexStarts">
        /// One of the <c>ChunkIndexStarts</c> arrays, sentinel entry included.
        /// </param>
        /// <param name="chunks">The visible chunk indices; not modified (an unsorted collection is copied and sorted).</param>
        /// <param name="into">The ranges to fill, cleared first.</param>
        public static void VisibleDrawRanges(
            int[] chunkIndexStarts, ICollection<int> chunks, List<NeighbourhoodMeshDrawRange> into)
        {
            if (into == null) throw new ArgumentNullException(nameof(into));
            into.Clear();
            if (chunkIndexStarts == null || chunks == null || chunks.Count == 0) return;

            //The frustum walk hands over an ascending list already, so the sort is only paid for
            //by a caller who does not; a draw calls this once per layer and the check is six
            //integers' worth of comparison against a thousand-element sort.
            var sorted = AsSorted(chunks);

            var runStart = sorted[0];
            var runEnd = sorted[0] + 1;
            for (int i = 1; i < sorted.Count; i++)
            {
                var chunk = sorted[i];
                if (chunk == runEnd) { runEnd = chunk + 1; continue; }
                AddRange(chunkIndexStarts, runStart, runEnd, into);
                runStart = chunk;
                runEnd = chunk + 1;
            }
            AddRange(chunkIndexStarts, runStart, runEnd, into);
        }

        private static List<int> AsSorted(ICollection<int> chunks)
        {
            var list = chunks as List<int>;
            if (list != null && IsAscending(list)) return list;
            var copy = new List<int>(chunks);
            copy.Sort();
            return copy;
        }

        private static bool IsAscending(List<int> values)
        {
            for (int i = 1; i < values.Count; i++)
            {
                if (values[i] < values[i - 1]) return false;
            }
            return true;
        }

        private static void AddRange(
            int[] chunkIndexStarts, int firstChunk, int endChunk, List<NeighbourhoodMeshDrawRange> into)
        {
            if (chunkIndexStarts == null) return;
            if (firstChunk < 0 || endChunk > NeighbourhoodCityMeshBuilder.ChunkCount) return;
            var start = chunkIndexStarts[firstChunk];
            var end = chunkIndexStarts[endChunk];
            if (end <= start) return;
            into.Add(new NeighbourhoodMeshDrawRange(firstChunk, endChunk, start, end - start));
        }

        /// <summary>
        /// Whether a map of this size can be meshed, and if not, why in one sentence.
        /// </summary>
        /// <param name="map">The generated map, or <c>null</c>.</param>
        /// <param name="reason">The reason, when the answer is <c>false</c>; otherwise <c>null</c>.</param>
        public static bool CanMesh(NeighbourhoodMap map, out string reason)
        {
            if (map == null)
            {
                reason = "there is no generated map";
                return false;
            }
            if (map.Width != MapSize || map.Height != MapSize)
            {
                reason = "the generated map is " + map.Width + "x" + map.Height
                    + " but the mesh is built for a " + MapSize + "x" + MapSize + " map";
                return false;
            }
            var bad = NeighbourhoodCityMeshProbe.FirstUnmeshableTerrainType(map);
            if (bad >= 0)
            {
                reason = "the generated map holds terrain type " + bad
                    + ", which is neither one of the five drawable terrains nor 255 ('emit nothing')";
                return false;
            }
            reason = null;
            return true;
        }

        /// <summary>Builds the mesh, or returns <c>null</c> with the reason.</summary>
        /// <param name="map">The generated map.</param>
        /// <param name="reason">The reason, when the answer is <c>null</c>; otherwise <c>null</c>.</param>
        public static NeighbourhoodCityMesh TryBuild(NeighbourhoodMap map, out string reason)
        {
            NeighbourhoodCityMeshReport report;
            return TryBuild(map, out reason, out report);
        }

        /// <summary>
        /// Builds the mesh and reports what it emitted, or returns <c>null</c> with the reason.
        /// </summary>
        /// <param name="map">The generated map.</param>
        /// <param name="reason">The reason, when the answer is <c>null</c>; otherwise <c>null</c>.</param>
        /// <param name="report">
        /// What the map holds and what the mesh emitted, or an empty report when the map could not
        /// be meshed at all.
        /// </param>
        public static NeighbourhoodCityMesh TryBuild(
            NeighbourhoodMap map, out string reason, out NeighbourhoodCityMeshReport report)
        {
            return TryBuild(map, NeighbourhoodMeshLod.Full, out reason, out report);
        }

        /// <summary>
        /// Builds the mesh at a level of detail and reports what it emitted, or returns
        /// <c>null</c> with the reason.
        /// </summary>
        /// <param name="map">The generated map.</param>
        /// <param name="lodFactor">Tiles per ground quad edge: 1, 2 or 4.</param>
        /// <param name="reason">The reason, when the answer is <c>null</c>; otherwise <c>null</c>.</param>
        /// <param name="report">What the map holds and what the mesh emitted.</param>
        public static NeighbourhoodCityMesh TryBuild(
            NeighbourhoodMap map, int lodFactor, out string reason, out NeighbourhoodCityMeshReport report)
        {
            report = null;
            if (!CanMesh(map, out reason)) return null;
            return Build(map, lodFactor, out report);
        }

        /// <summary>
        /// Builds the mesh. Throws only on a map <see cref="CanMesh"/> would have refused, so
        /// callers that do not want the exception should use <see cref="TryBuild"/>.
        /// </summary>
        /// <param name="map">The generated map.</param>
        public static NeighbourhoodCityMesh Build(NeighbourhoodMap map)
        {
            return Build(map, out _);
        }

        /// <summary>
        /// Builds the mesh and reports what it emitted, for the log.
        /// </summary>
        /// <param name="map">The generated map.</param>
        /// <param name="report">
        /// What the walk produced: quads per ground layer, road and corner quads, the road tiles
        /// the isometric clip discarded, and the atlas cells the map reaches. The discarded count
        /// is the one worth having, because "the generator drew no roads" and "the clip threw the
        /// roads away" look exactly the same from a screenshot.
        /// </param>
        public static NeighbourhoodCityMesh Build(NeighbourhoodMap map, out NeighbourhoodCityMeshReport report)
        {
            return Build(map, 1, out report);
        }

        /// <summary>
        /// Builds the mesh at one level of detail: every ground quad covers
        /// <c>lodFactor * lodFactor</c> map tiles, and the roads are unaffected.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Why there is a level of detail at all.</b> A 512x512 map emits around three hundred
        /// thousand terrain triangles, and a full-screen framing of the whole neighbourhood has the
        /// whole map on screen at once, so the camera cull - which is worth a great deal zoomed in
        /// on a lot - removes nothing at all exactly when the frame is most expensive. One coarse
        /// quad per 2x2 or 4x4 tiles cuts that by four or sixteen, and at those zooms a tile is two
        /// or three pixels wide, so the coarser ground is indistinguishable from the fine one.
        /// The rule that picks the level is <see cref="NeighbourhoodMeshLod"/>'s, and it is pure, so
        /// "zoomed out draws a cheaper mesh" is an assertion rather than a screenshot.
        /// </para>
        /// <para>
        /// <b>The roads never coarsen.</b> A road quad is a mask-driven cell of the road atlas, and
        /// averaging four tiles' masks would produce a street that is not on the map. They are a
        /// small fraction of the triangle count anyway - a few per cent - so full detail everywhere
        /// is both correct and affordable.
        /// </para>
        /// <para>
        /// <b>The blend faces coarsen to nothing, and that is the reference's behaviour rather than
        /// an omission.</b> A blend face is a cross-fade between two terrains, and the city only ever
        /// builds one per tile at full detail (<c>CityGeometry.RegenMeshVerts</c>, and its subdiv
        /// equivalent at <c>CityGeometry.cs:721</c>). A coarse level covering a 2x2 or 4x4 block has
        /// no single neighbour to fade towards, so the ground lands on the block's near-corner
        /// terrain and the boundary is a step. That is a visible difference from the city at the two
        /// widest zooms, and it is the one place the coarse levels are not faithful; it is stated
        /// rather than papered over, because the alternative - inventing a blend per block - would
        /// be a fourth terrain-boundary rule that exists nowhere in the reference.
        /// </para>
        /// <para>
        /// <b>The chunk bookkeeping is identical at every level</b>, which is what lets one draw
        /// path serve all three: same 32x32 chunk grid, same
        /// <c>LayerChunkIndexStarts</c> shape, so the same cull and the same draw-range coalescing
        /// work whatever level is in hand. A level's quads stay inside their own chunk's 16x16
        /// core - no fade skirt is emitted - so a coarse level's map edge is a stair at the
        /// <c>lodFactor</c>-tile scale rather than a smooth ramp. At the only zooms a coarse level
        /// is used at, that stair is two to eleven pixels wide and invisible.
        /// </para>
        /// </remarks>
        /// <param name="map">The generated map.</param>
        /// <param name="lodFactor">
        /// Tiles per coarse quad edge: 1, 2 or 4. Anything else is clamped to the nearest of those,
        /// and 1 is the full-detail level.
        /// </param>
        /// <param name="report">What the walk produced, for the log.</param>
        public static NeighbourhoodCityMesh Build(
            NeighbourhoodMap map, int lodFactor, out NeighbourhoodCityMeshReport report)
        {
            string reason;
            if (!CanMesh(map, out reason)) throw new ArgumentException(reason, nameof(map));
            var factor = NeighbourhoodMeshLod.ClampFactor(lodFactor);

            var mesh = new NeighbourhoodCityMesh();
            mesh.LodFactor = factor;
            //A whole-map level is never subdivided, whichever factor it is: the subdivision is what
            //the close level is for, and it is built per slice instead. See BuildSubdivSlice.
            mesh.Subdivisions = 1;
            var layerVerts = new List<NeighbourhoodTerrainVertex>[LayerCount];
            var layerIndices = new List<int>[LayerCount];
            //Initial capacity is a full-detail guess; a coarse level emits 1/factor^2 of it, and an
            //oversized list is a transient per-layer allocation (tens of MB across the five layers)
            //on every level change, followed by a ToArray copy.
            var coarseDivisor = factor * factor;
            for (int i = 0; i < LayerCount; i++)
            {
                layerVerts[i] = new List<NeighbourhoodTerrainVertex>(300000 / coarseDivisor);
                layerIndices[i] = new List<int>(400000 / coarseDivisor);
            }
            var roadVerts = new List<NeighbourhoodTerrainVertex>(100000);
            var roadIndices = new List<int>(150000);

            var terrain = map.TerrainType;
            var elevation = map.ElevationData;

            var layerQuads = new int[LayerCount];
            //Per layer, because a blend face lands in its neighbour's buffer and a total would hide
            //which one - see NeighbourhoodCityMeshReport.BlendQuadsPerLayer.
            var blendQuadsPerLayer = new int[LayerCount];
            var blendQuads = 0;
            var roadQuads = 0;
            var cornerQuads = 0;
            var chunkIndex = 0;

            for (int cy = 0; cy < ChunksPerAxis; cy++)
            {
                for (int cx = 0; cx < ChunksPerAxis; cx++)
                {
                    var chunkLeft = cx * ChunkSize;
                    var chunkRight = (cx + 1) * ChunkSize;

                    //where this chunk's geometry starts in every buffer, so a draw can submit
                    //the chunks the camera can see rather than all of them
                    for (int i = 0; i < LayerCount; i++) mesh.LayerChunkIndexStarts[i][chunkIndex] = layerIndices[i].Count;
                    mesh.RoadChunkIndexStarts[chunkIndex] = roadIndices.Count;

                    if (factor > 1)
                    {
                        //A coarse level walks its own 2x2-or-4x4 blocks for the ground, and then
                        //the ordinary per-tile walk for the roads only: see
                        //Build(map, lodFactor, report) for why the chunk's own 16x16 core is the
                        //coarse level's extent, why the fade skirt is dropped, and why the roads
                        //stay at full detail.
                        //No blend faces on a coarse level, and the reason is in Build's remarks: a
                        //block of 2x2 or 4x4 tiles has no single higher-priority neighbour to fade
                        //towards, so the city has no such quad either (CityGeometry only ever emits
                        //one per tile, RegenMeshVerts :310-344 and SubRegenMeshVerts :721-742).
                        AddCoarseChunkGround(map, factor, chunkLeft, cy * ChunkSize,
                            layerVerts, layerIndices, layerQuads);
                        AddChunkRoads(map, chunkLeft, chunkRight, cy * ChunkSize,
                            roadVerts, roadIndices, ref roadQuads, ref cornerQuads);
                        chunkIndex++;
                        continue;
                    }

                    for (int i = cy * ChunkSize; i < (cy + 1) * ChunkSize; i++)
                    {
                        var rowLeft = DiamondLeft(i);
                        var rowRight = DiamondRight(i);
                        var nextLeft = DiamondLeft(i + 1);
                        var nextRight = DiamondRight(i + 1);

                        var start = Math.Max(rowLeft - FadeRange, chunkLeft);
                        var end = Math.Min(chunkRight, rowRight + FadeRange);
                        if (end <= start) continue;

                        for (int j = start; j < end; j++)
                        {
                            var x = Math.Min(Math.Max(rowLeft, j), rowRight - 1);
                            var type = terrain[i * MapSize + x];
                            if (type >= LayerCount) continue; // 255 or a corrupt byte: no quad

                            var x0 = Math.Min(Math.Max(rowLeft, j), rowRight - 1);
                            var x1 = Math.Min(Math.Max(rowLeft, j + 1), rowRight - 1);
                            var x2 = Math.Min(Math.Max(nextLeft, j + 1), nextRight - 1);
                            var x3 = Math.Min(Math.Max(nextLeft, j), nextRight - 1);
                            var y1 = Math.Min(i + 1, MapSize - 1);

                            var n0 = NormalAt(elevation, x0, i);
                            var n1 = NormalAt(elevation, x1, i);
                            var n2 = NormalAt(elevation, x2, y1);
                            var n3 = NormalAt(elevation, x3, y1);

                            var p0 = new Vector3(j, WorldHeightFor(elevation[i * MapSize + x0]), i);
                            var p1 = new Vector3(j + 1, WorldHeightFor(elevation[i * MapSize + x1]), i);
                            var p2 = new Vector3(j + 1, WorldHeightFor(elevation[y1 * MapSize + x2]), i + 1);
                            var p3 = new Vector3(j, WorldHeightFor(elevation[y1 * MapSize + x3]), i + 1);

                            var a0 = FadeTransparency(rowLeft, rowRight, j, i);
                            var a1 = FadeTransparency(rowLeft, rowRight, j + 1, i);
                            var a2 = FadeTransparency(nextLeft, nextRight, j + 1, i + 1);
                            var a3 = FadeTransparency(nextLeft, nextRight, j, i + 1);

                            AddQuad(layerVerts[type], layerIndices[type], p0, n0, a0, j, i, p1, n1, a1, j + 1, i,
                                p2, n2, a2, j + 1, i + 1, p3, n3, a3, j, i + 1);
                            layerQuads[type]++;

                            //The blend face on top of it, in the neighbour's layer. CityGeometry
                            //regen only does this strictly inside the diamond (:301), for the same
                            //reason roads do: the fade skirt is a cosmetic edge and a cross-fade
                            //hanging over it would be a visible seam hanging over nothing.
                            if (IsInsideDiamond(j, i))
                            {
                                var blend = GetBlend(terrain, i, x);
                                if (blend.NeedsBlend && blend.MaxEdge < LayerCount)
                                {
                                    AddBlendQuad(layerVerts[blend.MaxEdge], layerIndices[blend.MaxEdge],
                                        blend.AtlasPosition, p0, n0, a0, p1, n1, a1, p2, n2, a2,
                                        p3, n3, a3, j, i);
                                    blendQuads++;
                                    blendQuadsPerLayer[blend.MaxEdge]++;
                                }
                            }

                            //a road only exists strictly inside the diamond: the fade skirt is a
                            //cosmetic edge and a street hanging over it would look broken
                            AddRoadQuadsForTile(map, j, i,
                                p0, n0, p1, n1, p2, n2, p3, n3,
                                roadVerts, roadIndices, ref roadQuads, ref cornerQuads);
                        }
                    }

                    chunkIndex++;
                }
            }

            for (int i = 0; i < LayerCount; i++)
            {
                mesh.Layers[i].Vertices = layerVerts[i].ToArray();
                mesh.Layers[i].Indices = layerIndices[i].ToArray();
                //the sentinel, so a chunk's range is starts[c + 1] - starts[c] with no special
                //case for the last chunk
                mesh.LayerChunkIndexStarts[i][ChunkCount] = layerIndices[i].Count;
            }
            mesh.RoadChunkIndexStarts[ChunkCount] = roadIndices.Count;
            mesh.Roads.Vertices = roadVerts.ToArray();
            mesh.Roads.Indices = roadIndices.ToArray();

            report = NeighbourhoodCityMeshReport.Probe(map);
            report.RecordEmitted(layerQuads, roadQuads, cornerQuads, blendQuadsPerLayer);
            //A blend face is a second quad in somebody else's layer, so the per-layer ground quad
            //counts no longer add up to the triangles in the buffers. Both counts are reported - per
            //layer and in total - because the probe's LayerQuads is the <em>ground</em> count and a
            //layer buffer's PrimitiveCount is its ground plus the blends sent into it.
            report.EmittedBlendQuads = blendQuads;
            report.LodFactor = factor;

            return mesh;
        }

        /// <summary>
        /// Builds one slice of the close, subdivided level of detail, which is the geometry
        /// <c>CityGeometry.SubRegenMeshVerts</c> builds and <c>Terrain.cs</c> regenerates a slice
        /// of at a time.
        ///
        /// <para>
        /// <b>Why a slice and not the whole map.</b> The subdivided level is the finest one, and
        /// the finest one is only wanted when the camera is in among the houses - a view spanning
        /// under 320 tiles, out of 724 for the whole map. At that zoom a tile is a handful of
        /// pixels, so a whole-map subdivided buffer would be several hundred megabytes of vertices
        /// describing ground that is off screen, built on a worker and uploaded on a frame the
        /// player is watching. A slice is 32x32 tiles - about a megabyte of vertices, a whole
        /// 2x2 of chunks, and about a thousandth of the frame's own work - and a view that close
        /// touches nine or ten of them. This is the same shape as the city: a camera-driven range,
        /// a slice at a time, a level that is only ever built where it is looked at.
        /// </para>
        ///
        /// <para>
        /// <b>The tile set is the whole-map level's tile set, restricted to the slice.</b> The walk
        /// below is the fine walk's own arithmetic - the diamond plus its ten-tile fade skirt,
        /// clamped against each chunk's column band - with the same
        /// <see cref="AddCoarseChunkGround"/>-free per-tile emitter replaced by
        /// <see cref="AddSubdividedTile"/>. Re-deriving the bounds here instead of sharing them is
        /// the mistake <see cref="AddChunkRoads"/>'s documentation describes, so they are not
        /// re-derived: the slice is a union of whole chunks, and every chunk inside it runs the
        /// same expression the whole-map walk runs for that chunk.
        /// </para>
        ///
        /// <para>
        /// <b>Roads are not in here.</b> A road quad is a mask-driven cell of the road atlas and
        /// has no subdivided form; the roads keep coming from the whole-map level's road buffer,
        /// which is where they are at every level anyway. <see cref="NeighbourhoodCityMesh"/>'s
        /// road bookkeeping is therefore all zeros for a slice, and drawing a slice's roads submits
        /// nothing rather than something wrong.
        /// </para>
        ///
        /// <para>
        /// <b>Chunk bookkeeping is the same shape as every other level's</b>, which is what lets one
        /// draw path serve the slice and the whole-map level it refines: the same 32x32 chunk grid,
        /// the same <c>LayerChunkIndexStarts</c> length, and the chunks outside the slice left at
        /// zero so a range over them is empty and is skipped. It is also why a slice boundary can
        /// never fall inside a chunk, so no chunk is ever drawn half from one buffer and half from
        /// the other.
        /// </para>
        /// </summary>
        /// <param name="map">The generated map.</param>
        /// <param name="slice">The slice index, row-major over the slice grid.</param>
        /// <param name="subdivisions">
        /// Ground quads per map tile edge; clamped to 1 or <see cref="NeighbourhoodMeshLod.MaxSubdivisions"/>.
        /// </param>
        /// <param name="report">What the walk produced, for the log.</param>
        public static NeighbourhoodCityMesh BuildSubdivSlice(
            NeighbourhoodMap map, int slice, int subdivisions, out NeighbourhoodCityMeshReport report)
        {
            string reason;
            if (!CanMesh(map, out reason)) throw new ArgumentException(reason, nameof(map));
            if (slice < 0 || slice >= SliceCount)
            {
                throw new ArgumentOutOfRangeException(nameof(slice),
                    "a slice of a " + MapSize + "x" + MapSize + " map is 0.." + (SliceCount - 1)
                    + ", and this was " + slice);
            }
            var subdiv = NeighbourhoodMeshLod.ClampSubdivisions(subdivisions);

            var bounds = SliceBounds(slice);
            var mesh = new NeighbourhoodCityMesh();
            mesh.LodFactor = NeighbourhoodMeshLod.Full;
            mesh.Subdivisions = subdiv;

            var layerVerts = new List<NeighbourhoodTerrainVertex>[LayerCount];
            var layerIndices = new List<int>[LayerCount];
            for (int i = 0; i < LayerCount; i++)
            {
                layerVerts[i] = new List<NeighbourhoodTerrainVertex>(8192);
                layerIndices[i] = new List<int>(16384);
            }
            //A blend face is a quad per sub-vertex rather than per tile, so a slice's blend budget is
            //subdiv^2 the tile count - the buffers have to be sized for it or they reallocate on the
            //worker thread a slice at a time.
            var layerQuads = new int[LayerCount];
            var blendQuads = 0;
            var blendQuadsPerLayer = new int[LayerCount];

            var terrain = map.TerrainType;
            var elevation = map.ElevationData;

            //the slice's own 2x2 of chunks, and nothing else: the per-chunk loop below is the
            //whole-map loop's body with the tile emitter swapped, so the two agree tile for tile
            var firstCx = bounds.X / ChunkSize;
            var firstCy = bounds.Y / ChunkSize;
            var chunkIndex = 0;

            for (int cy = 0; cy < ChunksPerAxis; cy++)
            {
                for (int cx = 0; cx < ChunksPerAxis; cx++)
                {
                    //Every chunk records where the next one's geometry starts, including the ones
                    //this slice does not touch, and that is not bookkeeping for its own sake: the
                    //draw reads a chunk's size as starts[c + 1] - starts[c], so a chunk outside the
                    //slice has to carry the *current* count rather than a zero, or the slice's last
                    //chunk would be measured from a zero and come out negative. Filling every entry
                    //is what keeps the array monotone, and monotonicity is what the range coalescing
                    //relies on.
                    for (int i = 0; i < LayerCount; i++)
                    {
                        mesh.LayerChunkIndexStarts[i][chunkIndex] = layerIndices[i].Count;
                    }
                    if (cx >= firstCx && cx < firstCx + ChunksPerSliceEdge
                        && cy >= firstCy && cy < firstCy + ChunksPerSliceEdge)
                    {
                        var chunkLeft = cx * ChunkSize;
                        var chunkRight = (cx + 1) * ChunkSize;
                        AddSubdividedChunkGround(terrain, elevation, subdiv, chunkLeft, chunkRight,
                            cy * ChunkSize, layerVerts, layerIndices, layerQuads, ref blendQuads,
                            blendQuadsPerLayer);
                    }
                    chunkIndex++;
                }
            }

            for (int i = 0; i < LayerCount; i++)
            {
                mesh.Layers[i].Vertices = layerVerts[i].ToArray();
                mesh.Layers[i].Indices = layerIndices[i].ToArray();
                mesh.LayerChunkIndexStarts[i][ChunkCount] = layerIndices[i].Count;
            }
            mesh.RoadChunkIndexStarts[ChunkCount] = 0;

            //A slice report is not a whole-map report and does not pretend to be. The probe walks all
            //262144 tiles of the map to produce its numbers and a slice is 1024 of them, so running
            //it per slice would be a quarter of a million tile reads to describe a thousand - and
            //the answer would be about the whole map rather than about the slice. So the report
            //holds the slice's own counts, EmissionMatchesProbe refuses to answer for a slice, and
            //the log line is the slice's.
            report = new NeighbourhoodCityMeshReport();
            report.SliceIndex = slice;
            report.Subdivisions = subdiv;
            report.LodFactor = mesh.LodFactor;
            report.MapTiles = SliceSize * SliceSize;
            report.SetLayerQuads(layerQuads);
            report.RecordEmitted(layerQuads, 0, 0, blendQuadsPerLayer);
            report.EmittedBlendQuads = blendQuads;
            return mesh;
        }

        /// <summary>
        /// The subdivided ground for one chunk: the whole-map fine walk's tile window, with every
        /// tile cut into <paramref name="subdiv"/> by <paramref name="subdiv"/> quads.
        /// </summary>
        private static void AddSubdividedChunkGround(
            byte[] terrain, byte[] elevation, int subdiv, int chunkLeft, int chunkRight, int chunkTop,
            List<NeighbourhoodTerrainVertex>[] layerVerts, List<int>[] layerIndices, int[] layerQuads,
            ref int blendQuads, int[] blendQuadsPerLayer)
        {
            //one sample buffer for the whole chunk rather than one per tile: a slice is a thousand
            //tiles and this is a worker thread, so a thousand short-lived arrays is a thousand
            //collections for sixteen floats' worth of work
            var samples = new float[16];
            for (int i = chunkTop; i < chunkTop + ChunkSize; i++)
            {
                var rowLeft = DiamondLeft(i);
                var rowRight = DiamondRight(i);
                if (rowRight <= rowLeft) continue;

                //the fine walk's own window, verbatim: the diamond plus its fade skirt, clamped
                //against this chunk's column band
                var start = Math.Max(rowLeft - FadeRange, chunkLeft);
                var end = Math.Min(chunkRight, rowRight + FadeRange);
                if (end <= start) continue;

                for (int j = start; j < end; j++)
                {
                    AddSubdividedTile(terrain, elevation, subdiv, j, i, samples, layerVerts,
                        layerIndices, layerQuads, ref blendQuads, blendQuadsPerLayer);
                }
            }
        }

        /// <summary>
        /// One tile as a <c>(subdiv + 1) * (subdiv + 1)</c> grid of vertices, which is
        /// <c>CityGeometry.SubRegenMeshVerts</c>'s inner loop: the same Catmull-Rom heights, the
        /// same bilinearly interpolated normals, the same indices over the interior of the grid.
        ///
        /// <para>
        /// <b>The corners agree with the whole-map levels exactly, and that is the load-bearing
        /// property.</b> The interpolated height at a grid corner is the sampled elevation at that
        /// corner, because a cubic with zero tangents out of it reduces to its own control points
        /// at the ends - so a subdivided slice and the un-subdivided level either side of it meet
        /// on exactly the same points, and the boundary between "refined" and "not refined yet" is
        /// a change of tessellation rather than a crack in the ground. A test asserts it.
        /// </para>
        ///
        /// <para>
        /// The normals are interpolated rather than sampled per sub-vertex, which is the reference's
        /// own choice and the one that matters: the un-subdivided level gives all four corners of a
        /// tile the same normal, so its lighting is flat per tile, and lerping across the tile is
        /// what turns a grid of five-pixel facets into ground.
        /// </para>
        /// </summary>
        private static void AddSubdividedTile(
            byte[] terrain, byte[] elevation, int subdiv, int j, int i, float[] d,
            List<NeighbourhoodTerrainVertex>[] layerVerts, List<int>[] layerIndices, int[] layerQuads,
            ref int blendQuads, int[] blendQuadsPerLayer)
        {
            var rowLeft = DiamondLeft(i);
            var rowRight = DiamondRight(i);
            var x = Math.Min(Math.Max(rowLeft, j), rowRight - 1);
            var type = terrain[i * MapSize + x];
            if (type >= LayerCount) return; // 255 or a corrupt byte: no geometry at all

            var nextLeft = DiamondLeft(i + 1);
            var nextRight = DiamondRight(i + 1);
            var y1 = Math.Min(i + 1, MapSize - 1);

            //the four corners, exactly as the fine walk resolves them, so the two levels agree
            var x0 = Math.Min(Math.Max(rowLeft, j), rowRight - 1);
            var x1c = Math.Min(Math.Max(rowLeft, j + 1), rowRight - 1);
            var x2c = Math.Min(Math.Max(nextLeft, j + 1), nextRight - 1);
            var x3c = Math.Min(Math.Max(nextLeft, j), nextRight - 1);
            var n0 = NormalAt(elevation, x0, i);
            var n1 = NormalAt(elevation, x1c, i);
            var n2 = NormalAt(elevation, x2c, y1);
            var n3 = NormalAt(elevation, x3c, y1);
            var a0 = FadeTransparency(rowLeft, rowRight, j, i);
            var a1 = FadeTransparency(rowLeft, rowRight, j + 1, i);
            var a2 = FadeTransparency(nextLeft, nextRight, j + 1, i + 1);
            var a3 = FadeTransparency(nextLeft, nextRight, j, i + 1);

            //The blend, resolved once per tile rather than per sub-vertex.
            //CityGeometry.SubRegenMeshVerts resolves it at the top of its own loop
            //(CityGeometry.cs:642-646) and then interpolates the mask's atlas cell across the grid
            //(CityGeometry.cs:729, bOff + bDelta * new Vector2(xi, yi)), so the mask's UV moves with
            //xi/yi rather than being constant over the tile - and it is gated on the same
            //j > rXS && j < rXE test the coarse walk uses (CityGeometry.cs:666, :719).
            var blend = GetBlend(terrain, i, x);
            var blendT = blend.MaxEdge;
            var blendOffset = blend.AtlasPosition;
            var blendDelta = NeighbourhoodBlendFlags.CellSize;
            var useBlend = blend.NeedsBlend && blendT >= 0 && blendT < LayerCount;

            //the 4x4 elevation neighbourhood the cubic interpolates across, laid out as the
            //reference lays it out: four consecutive x samples per row, four rows
            d[0] = SampledElevation(elevation, j - 1, i - 1);
            d[1] = SampledElevation(elevation, j - 1, i);
            d[2] = SampledElevation(elevation, j - 1, i + 1);
            d[3] = SampledElevation(elevation, j - 1, i + 2);
            d[4] = SampledElevation(elevation, j, i - 1);
            d[5] = SampledElevation(elevation, j, i);
            d[6] = SampledElevation(elevation, j, i + 1);
            d[7] = SampledElevation(elevation, j, i + 2);
            d[8] = SampledElevation(elevation, j + 1, i - 1);
            d[9] = SampledElevation(elevation, j + 1, i);
            d[10] = SampledElevation(elevation, j + 1, i + 1);
            d[11] = SampledElevation(elevation, j + 1, i + 2);
            d[12] = SampledElevation(elevation, j + 2, i - 1);
            d[13] = SampledElevation(elevation, j + 2, i);
            d[14] = SampledElevation(elevation, j + 2, i + 1);
            d[15] = SampledElevation(elevation, j + 2, i + 2);

            //CityGeometry's normalTile (CityGeometry.cs:666). Inside the diamond the fade is zero at
            //every corner anyway, so this is belt and braces - except where the diamond narrows from
            //one row to the next, where the *next* row's corner is outside its own edge and the
            //interpolation would otherwise leak a sliver of transparency into ground the city draws
            //solid. CityGeometry gates it, so it is gated here.
            var normalTile = IsInsideDiamond(j, i);
            var blendVerts = useBlend ? layerVerts[blendT] : null;
            var blendIndices = useBlend ? layerIndices[blendT] : null;

            var verts = layerVerts[type];
            var indices = layerIndices[type];
            var side = subdiv + 1;

            for (int gy = 0; gy <= subdiv; gy++)
            {
                var yi = gy / (float)subdiv;
                var y1s = Cubic(d[0], d[1], d[2], d[3], yi);
                var y2s = Cubic(d[4], d[5], d[6], d[7], yi);
                var y3s = Cubic(d[8], d[9], d[10], d[11], yi);
                var y4s = Cubic(d[12], d[13], d[14], d[15], yi);

                for (int gx = 0; gx <= subdiv; gx++)
                {
                    var xi = gx / (float)subdiv;
                    var height = Cubic(y1s, y2s, y3s, y4s, xi);

                    //normals first along x, then along y: the reference's order, and the one that
                    //makes a tile's four corners agree with the un-subdivided level's
                    var lerpNx = Vector3.Lerp(n0, n1, xi);
                    var lerpNx2 = Vector3.Lerp(n3, n2, xi);
                    var normal = Vector3.Lerp(lerpNx, lerpNx2, yi);

                    //zero inside the diamond, interpolated in the fade skirt
                    //(CityGeometry.cs:687-694)
                    var transparency = 0f;
                    if (!normalTile)
                    {
                        var lerpTx = a0 * (1f - xi) + a1 * xi;
                        var lerpTx2 = a3 * (1f - xi) + a2 * xi;
                        transparency = lerpTx * (1f - yi) + lerpTx2 * yi;
                    }

                    var position = new Vector3(j + xi, WorldHeightFor(height), i + yi);
                    var groundUv = new Vector2(j + xi, i + yi) / TilesPerTextureRepeat;

                    var baseInd = verts.Count;
                    verts.Add(new NeighbourhoodTerrainVertex
                    {
                        Position = position,
                        Normal = normal,
                        Transparency = transparency,
                        TextureCoord = groundUv,
                        MaskTextureCoord = new Vector2(-1, -1)
                    });

                    // The interior of the grid only, which is what makes it subdiv * subdiv quads
                    // out of a (subdiv + 1)^2 vertex set. CityGeometry.cs:707.
                    if (gx < subdiv && gy < subdiv)
                    {
                        indices.Add(baseInd);
                        indices.Add(baseInd + 1);
                        indices.Add(baseInd + side);
                        indices.Add(baseInd + side);
                        indices.Add(baseInd + 1);
                        indices.Add(baseInd + side + 1);
                    }

                    //The blend face on top, in the neighbour's layer, mask UV interpolated across
                    //the tile. CityGeometry.cs:721-742, including the (0,0)..(1,1) traversal of the
                    //atlas cell: bOff + bDelta * (xi, yi), with U along +X and V along +Z.
                    if (useBlend && normalTile)
                    {
                        var bInd = blendVerts.Count;
                        blendVerts.Add(BlendVertex(position, normal, transparency, groundUv,
                            blendOffset + blendDelta * new Vector2(xi, yi)));
                        if (gx < subdiv && gy < subdiv)
                        {
                            blendIndices.Add(bInd);
                            blendIndices.Add(bInd + 1);
                            blendIndices.Add(bInd + side);
                            blendIndices.Add(bInd + side);
                            blendIndices.Add(bInd + 1);
                            blendIndices.Add(bInd + side + 1);
                            //One quad, counted as one. A subdivided tile's blend face is subdiv^2
                            //quads rather than the coarse walk's one, and the count is a quad count
                            //in both - which is why it is bumped per emitted index triple and not
                            //per vertex.
                            blendQuads++;
                            blendQuadsPerLayer[blendT]++;
                        }
                    }
                }
            }
            layerQuads[type] += subdiv * subdiv;
        }

        /// <summary>
        /// An elevation byte, clamped into its own row's diamond exactly as every other read of the
        /// field is - so the subdivided surface's grid corners are the same heights the
        /// un-subdivided levels use, and the two tessellations meet on the same points.
        /// </summary>
        private static float SampledElevation(byte[] elevation, int x, int y)
        {
            y = y < 0 ? 0 : (y > MapSize - 1 ? MapSize - 1 : y);
            var rowLeft = DiamondLeft(y);
            var rowRight = DiamondRight(y);
            var cx = Math.Min(Math.Max(rowLeft, x), rowRight - 1);
            return elevation[y * MapSize + cx];
        }

        /// <summary>
        /// <c>CityGeometry.Cubic</c>, verbatim: a Catmull-Rom through four samples, with the
        /// <c>continuity</c> argument flattening the tangents at the ends of a run of samples.
        ///
        /// <para>
        /// Reproduced rather than replaced by a "cleaner" cubic because the whole reason this
        /// builder subdivides is to agree with the city, and a different spline through the same
        /// samples is a different surface - which is exactly the kind of difference nobody sees in
        /// a diff and everybody sees where two levels meet. At <c>continuity = 0</c> the tangents
        /// are <c>(v2 - v0) / 4</c> and <c>(v3 - v1) / 4</c>, and at <c>mu = 0</c> and <c>mu = 1</c>
        /// the result is <c>v1</c> and <c>v2</c> exactly - which is the corner agreement above.
        /// </para>
        /// </summary>
        /// <param name="v0">The sample before the first.</param>
        /// <param name="v1">The first sample.</param>
        /// <param name="v2">The second sample.</param>
        /// <param name="v3">The sample after the second.</param>
        /// <param name="mu">Where in the span to evaluate, from 0 to 1.</param>
        /// <param name="continuity">Flattens the ends; zero is a plain Catmull-Rom.</param>
        public static float Cubic(float v0, float v1, float v2, float v3, float mu, float continuity = 0f)
        {
            var tension = 0.5f + continuity / 2;
            var bias = 0f;

            var mu2 = mu * mu;
            var mu3 = mu2 * mu;
            var m0 = (v1 - v0) * (1 + bias) * (1 - tension) * (1 + continuity) / 2;
            m0 += (v2 - v1) * (1 - bias) * (1 - tension) * (1 - continuity) / 2;
            var m1 = (v2 - v1) * (1 + bias) * (1 - tension) * (1 - continuity) / 2;
            m1 += (v3 - v2) * (1 - bias) * (1 - tension) * (1 + continuity) / 2;
            var a0 = 2 * mu3 - 3 * mu2 + 1;
            var a1 = mu3 - 2 * mu2 + mu;
            var a2 = mu3 - mu2;
            var a3 = -2 * mu3 + 3 * mu2;

            return a0 * v1 + a1 * m0 + a2 * m1 + a3 * v2;
        }

        /// <summary>
        /// The road quads one tile contributes, and the counters to bump. Shared by the full-detail
        /// walk and by <see cref="AddChunkRoads"/> so the two cannot disagree about which tiles
        /// carry a street.
        /// </summary>
        private static void AddRoadQuadsForTile(
            NeighbourhoodMap map, int j, int i,
            Vector3 p0, Vector3 n0, Vector3 p1, Vector3 n1, Vector3 p2, Vector3 n2, Vector3 p3, Vector3 n3,
            List<NeighbourhoodTerrainVertex> roadVerts, List<int> roadIndices,
            ref int roadQuads, ref int cornerQuads)
        {
            //a road only exists strictly inside the diamond: the fade skirt is a cosmetic edge and
            //a street hanging over it would look broken
            if (!IsInsideDiamond(j, i)) return;

            var rowLeft = DiamondLeft(i);
            var rowRight = DiamondRight(i);
            var x = Math.Min(Math.Max(rowLeft, j), rowRight - 1);
            var roadByte = map.RoadData[i * MapSize + x];
            var straight = roadByte & RoadMask.MaskBits;
            var corner = roadByte >> 4;
            if (straight > 0)
            {
                AddRoadQuad(roadVerts, roadIndices, NeighbourhoodRoadLayout.CellFor(straight, false),
                    p0, n0, p1, n1, p2, n2, p3, n3);
                roadQuads++;
            }
            if (corner > 0)
            {
                AddRoadQuad(roadVerts, roadIndices, NeighbourhoodRoadLayout.CellFor(corner, true),
                    p0, n0, p1, n1, p2, n2, p3, n3);
                cornerQuads++;
            }
        }

        /// <summary>
        /// Every road quad in one chunk's 16x16 core, at full per-tile detail.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Same walk as the full-detail level, by construction rather than by a promise.</b> The
        /// obvious way to write this is to re-derive the bounds, and that is how the first version
        /// came to disagree with the fine level by a thousand road quads: the fine walk's column
        /// range is the diamond <em>plus its fade skirt</em>, and this one clamped straight to the
        /// diamond, so every tile in the ten-tile skirt that carried a road was drawn on the coarse
        /// level and not on the fine one. A road is only <em>emitted</em> strictly inside the
        /// diamond either way, so the visible difference was nil and the counts disagreed anyway -
        /// which is exactly the kind of silent divergence the mesh's tests exist to catch.
        /// </para>
        /// <para>
        /// So the walk is shared: this asks the same row/col arithmetic the fine loop does, with the
        /// ground quad suppressed. A road quad is a mask-driven cell of the road atlas, so averaging
        /// four tiles' masks would produce a street that is not on the map - the ground coarsens, the
        /// street network does not, and every level ends up with byte-identical road geometry.
        /// </para>
        /// </remarks>
        private static void AddChunkRoads(
            NeighbourhoodMap map, int chunkLeft, int chunkRight, int chunkTop,
            List<NeighbourhoodTerrainVertex> roadVerts, List<int> roadIndices,
            ref int roadQuads, ref int cornerQuads)
        {
            var elevation = map.ElevationData;
            for (int i = chunkTop; i < chunkTop + ChunkSize; i++)
            {
                var rowLeft = DiamondLeft(i);
                var rowRight = DiamondRight(i);
                var nextLeft = DiamondLeft(i + 1);
                var nextRight = DiamondRight(i + 1);
                var y1 = Math.Min(i + 1, MapSize - 1);

                //the same column range the full-detail walk uses, skirt included
                var start = Math.Max(rowLeft - FadeRange, chunkLeft);
                var end = Math.Min(chunkRight, rowRight + FadeRange);
                if (end <= start) continue;

                for (int j = start; j < end; j++)
                {
                    var x0 = Math.Min(Math.Max(rowLeft, j), rowRight - 1);
                    var x1 = Math.Min(Math.Max(rowLeft, j + 1), rowRight - 1);
                    var x2 = Math.Min(Math.Max(nextLeft, j + 1), nextRight - 1);
                    var x3 = Math.Min(Math.Max(nextLeft, j), nextRight - 1);

                    AddRoadQuadsForTile(map, j, i,
                        new Vector3(j, WorldHeightFor(elevation[i * MapSize + x0]), i),
                        NormalAt(elevation, x0, i),
                        new Vector3(j + 1, WorldHeightFor(elevation[i * MapSize + x1]), i),
                        NormalAt(elevation, x1, i),
                        new Vector3(j + 1, WorldHeightFor(elevation[y1 * MapSize + x2]), i + 1),
                        NormalAt(elevation, x2, y1),
                        new Vector3(j, WorldHeightFor(elevation[y1 * MapSize + x3]), i + 1),
                        NormalAt(elevation, x3, y1),
                        roadVerts, roadIndices, ref roadQuads, ref cornerQuads);
                }
            }
        }

        /// <summary>
        /// The coarse ground for one chunk: one quad per <c>factor * factor</c> block of the chunk's
        /// own 16x16 core, at the mean height and mean normal of the block's corners.
        /// </summary>
        /// <remarks>
        /// <para>
        /// A block is emitted when any of its four corners is inside the diamond, and its corners
        /// carry the same <see cref="FadeTransparency"/> as the fine level's, so a coarse map edge
        /// fades out over the last few blocks rather than stopping in mid-air. The terrain type is
        /// the block's near corner clamped into the diamond, which is the same rule the full-detail
        /// walk uses for its tiles - a block is one land type, chosen the way a tile is.
        /// </para>
        /// <para>
        /// Quads stay strictly inside the chunk, so <c>LayerChunkIndexStarts</c> keeps the shape the
        /// cull and the draw-range coalescing rely on and no chunk is drawn twice.
        /// </para>
        /// </remarks>
        private static void AddCoarseChunkGround(
            NeighbourhoodMap map, int factor, int chunkLeft, int chunkTop,
            List<NeighbourhoodTerrainVertex>[] layerVerts, List<int>[] layerIndices, int[] layerQuads)
        {
            var terrain = map.TerrainType;
            var elevation = map.ElevationData;
            for (int by = 0; by < ChunkSize; by += factor)
            {
                var y0 = chunkTop + by;
                var y1 = Math.Min(y0 + factor, MapSize - 1);
                for (int bx = 0; bx < ChunkSize; bx += factor)
                {
                    var x0 = chunkLeft + bx;
                    var x1 = Math.Min(x0 + factor, MapSize - 1);

                    //the four corners, each clamped into its own row's diamond exactly as the
                    //full-detail walk clamps a tile's corners, so the two levels agree about where
                    //the map ends
                    var t00 = CoarseCornerAt(terrain, elevation, x0, y0);
                    var t10 = CoarseCornerAt(terrain, elevation, x1, y0);
                    var t01 = CoarseCornerAt(terrain, elevation, x0, y1);
                    var t11 = CoarseCornerAt(terrain, elevation, x1, y1);

                    var a0 = t00.Transparency;
                    var a1 = t10.Transparency;
                    var a2 = t11.Transparency;
                    var a3 = t01.Transparency;
                    //entirely in the skirt: nothing to draw
                    if (a0 >= 1f && a1 >= 1f && a2 >= 1f && a3 >= 1f) continue;
                    if (t00.Terrain >= LayerCount) continue;

                    var n = t00.Normal + t10.Normal + t01.Normal + t11.Normal;
                    if (n != Vector3.Zero) n.Normalize();
                    var height = (t00.Position.Y + t10.Position.Y + t01.Position.Y + t11.Position.Y) / 4f;

                    AddQuad(layerVerts[t00.Terrain], layerIndices[t00.Terrain],
                        new Vector3(x0, height, y0), n, a0, x0, y0,
                        new Vector3(x1, height, y0), n, a1, x1, y0,
                        new Vector3(x1, height, y1), n, a2, x1, y1,
                        new Vector3(x0, height, y1), n, a3, x0, y1);
                    layerQuads[t00.Terrain]++;
                }
            }
        }

        /// <summary>One corner of a coarse block, resolved into the world.</summary>
        private struct CoarseCorner
        {
            public Vector3 Position;
            public Vector3 Normal;
            public float Transparency;
            public byte Terrain;
        }

        /// <summary>
        /// A coarse block's corner, clamped into its row's diamond and resolved to a transparency
        /// and the terrain type of the tile it landed on.
        /// </summary>
        private static CoarseCorner CoarseCornerAt(byte[] terrain, byte[] elevation, int x, int y)
        {
            y = Math.Min(Math.Max(y, 0), MapSize - 1);
            var rowLeft = DiamondLeft(y);
            var rowRight = DiamondRight(y);
            var cx = Math.Min(Math.Max(rowLeft, x), rowRight - 1);
            return new CoarseCorner
            {
                Position = new Vector3(x, WorldHeightFor(elevation[y * MapSize + cx]), y),
                Normal = NormalAt(elevation, cx, y),
                Transparency = FadeTransparency(rowLeft, rowRight, x, y),
                Terrain = terrain[y * MapSize + cx]
            };
        }

        /// <summary>
        /// The transparency of a vertex from how far outside its row's diamond edge it is: fully
        /// opaque (<b>0</b>) inside, ramping to fully transparent (1) <see cref="FadeRange"/>
        /// tiles out.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The direction of the ramp is load-bearing and was once the wrong way round.</b> The
        /// value goes into <see cref="NeighbourhoodTerrainVertex.Transparency"/>, which
        /// <c>PixShader</c>'s <c>Fog</c> reads as <c>1 - transparency</c> - so the interior of the
        /// map has to be <b>zero</b> and the skirt has to be one. Returning the complement
        /// ("alpha", the same arithmetic <c>CityGeometry</c> uses internally before handing it to
        /// <c>trans</c>) produced a mesh that was one hundred per cent transparent everywhere, and
        /// a blank screen that the log cheerfully described as "315424 terrain triangles drawn".
        /// There is no way to see that from a triangle count; only the arithmetic can be asserted,
        /// which is why <c>NeighbourhoodTerrainVertexIsOpaqueInsideTheDiamond</c> exists.
        /// </para>
        /// </remarks>
        /// <param name="rowLeft">The row's left diamond edge.</param>
        /// <param name="rowRight">One past the row's right diamond edge.</param>
        /// <param name="x">The vertex's x.</param>
        public static float FadeTransparency(int rowLeft, int rowRight, int x)
        {
            var outside = Math.Max(rowLeft - x, x - rowRight);
            return Math.Min(1f, Math.Max(0f, outside * FadeRate));
        }

        /// <summary>
        /// The same fade along a column: zero for a vertex row inside the inset square, rising to
        /// one <see cref="FadeRange"/> rows above or below it. The diamond needed no vertical fade
        /// because its top and bottom were points; the square's are edges (see <see cref="ClipInset"/>).
        /// </summary>
        /// <param name="row">The vertex's row (0..MapSize inclusive).</param>
        public static float RowFadeTransparency(int row)
        {
            var outside = Math.Max(DiamondLeft(row) - row, row - DiamondRight(row));
            return Math.Min(1f, Math.Max(0f, outside * FadeRate));
        }

        /// <summary>A vertex's transparency from both its row's horizontal span and its row.</summary>
        /// <param name="rowLeft">The row's left edge.</param>
        /// <param name="rowRight">One past the row's right edge.</param>
        /// <param name="x">The vertex's x.</param>
        /// <param name="row">The vertex's row.</param>
        public static float FadeTransparency(int rowLeft, int rowRight, int x, int row)
        {
            return Math.Max(FadeTransparency(rowLeft, rowRight, x), RowFadeTransparency(row));
        }

        private static void AddQuad(
            List<NeighbourhoodTerrainVertex> verts, List<int> indices,
            Vector3 p0, Vector3 n0, float a0, int u0, int v0,
            Vector3 p1, Vector3 n1, float a1, int u1, int v1,
            Vector3 p2, Vector3 n2, float a2, int u2, int v2,
            Vector3 p3, Vector3 n3, float a3, int u3, int v3)
        {
            var first = verts.Count;
            verts.Add(new NeighbourhoodTerrainVertex
            {
                Position = p0,
                Normal = n0,
                Transparency = a0,
                TextureCoord = new Vector2(u0, v0) / TilesPerTextureRepeat,
                MaskTextureCoord = new Vector2(-1, -1)
            });
            verts.Add(new NeighbourhoodTerrainVertex
            {
                Position = p1,
                Normal = n1,
                Transparency = a1,
                TextureCoord = new Vector2(u1, v1) / TilesPerTextureRepeat,
                MaskTextureCoord = new Vector2(-1, -1)
            });
            verts.Add(new NeighbourhoodTerrainVertex
            {
                Position = p2,
                Normal = n2,
                Transparency = a2,
                TextureCoord = new Vector2(u2, v2) / TilesPerTextureRepeat,
                MaskTextureCoord = new Vector2(-1, -1)
            });
            verts.Add(new NeighbourhoodTerrainVertex
            {
                Position = p3,
                Normal = n3,
                Transparency = a3,
                TextureCoord = new Vector2(u3, v3) / TilesPerTextureRepeat,
                MaskTextureCoord = new Vector2(-1, -1)
            });

            indices.Add(first);
            indices.Add(first + 1);
            indices.Add(first + 2);
            indices.Add(first);
            indices.Add(first + 2);
            indices.Add(first + 3);
        }

        /// <summary>
        /// The blend face for one tile: the same four corners as the ground quad beneath it, in the
        /// neighbour's ground layer, with the mask's atlas cell in
        /// <c>MaskTextureCoord</c> rather than <c>(-1,-1)</c>.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The ground UV is the <em>tile's own</em> - the same <c>(x, y) / 4</c> the quad below uses -
        /// because the blend layer's texture is sampled with the tile's UV and the mask decides how
        /// much of it shows through. <c>CityGeometry.RegenMeshVerts</c>'s blend block
        /// (<c>CityGeometry.cs:310-344</c>) writes exactly that, including the corner order: the
        /// mask's U runs along +X and its V along +Z, so the cell is
        /// <c>(0,0), (dx,0), (dx,dy), (0,dy)</c> and not the other way round.
        /// </para>
        /// <para>
        /// The winding is the ground quad's winding, not reversed, and the transparency is the fade
        /// value rather than zero - so a blend face in the fade skirt fades too. Both are
        /// <c>CityGeometry</c>'s: the transparency at <c>CityGeometry.cs:314-315</c>, the indices at
        /// <c>CityGeometry.cs:346-351</c>.
        /// </para>
        /// </remarks>
        /// <param name="verts">The neighbour layer's vertex list.</param>
        /// <param name="indices">The neighbour layer's index list.</param>
        /// <param name="atlasOrigin">The mask cell's top-left corner in UV space.</param>
        /// <param name="p0">Corner at the tile's near-left, i.e. (j, i).</param>
        /// <param name="n0">Normal at that corner.</param>
        /// <param name="a0">Transparency at that corner.</param>
        /// <param name="p1">Corner at (j + 1, i).</param>
        /// <param name="n1">Normal at that corner.</param>
        /// <param name="a1">Transparency at that corner.</param>
        /// <param name="p2">Corner at (j + 1, i + 1).</param>
        /// <param name="n2">Normal at that corner.</param>
        /// <param name="a2">Transparency at that corner.</param>
        /// <param name="p3">Corner at (j, i + 1).</param>
        /// <param name="n3">Normal at that corner.</param>
        /// <param name="a3">Transparency at that corner.</param>
        /// <param name="j">The tile's column, for the ground UV.</param>
        /// <param name="i">The tile's row, for the ground UV.</param>
        private static void AddBlendQuad(
            List<NeighbourhoodTerrainVertex> verts, List<int> indices, Vector2 atlasOrigin,
            Vector3 p0, Vector3 n0, float a0,
            Vector3 p1, Vector3 n1, float a1,
            Vector3 p2, Vector3 n2, float a2,
            Vector3 p3, Vector3 n3, float a3,
            int j, int i)
        {
            var delta = NeighbourhoodBlendFlags.CellSize;
            var first = verts.Count;
            verts.Add(BlendVertex(p0, n0, a0, new Vector2(j, i), atlasOrigin));
            verts.Add(BlendVertex(p1, n1, a1, new Vector2(j + 1, i),
                new Vector2(atlasOrigin.X + delta.X, atlasOrigin.Y)));
            verts.Add(BlendVertex(p2, n2, a2, new Vector2(j + 1, i + 1),
                new Vector2(atlasOrigin.X + delta.X, atlasOrigin.Y + delta.Y)));
            verts.Add(BlendVertex(p3, n3, a3, new Vector2(j, i + 1),
                new Vector2(atlasOrigin.X, atlasOrigin.Y + delta.Y)));

            indices.Add(first);
            indices.Add(first + 1);
            indices.Add(first + 2);
            indices.Add(first);
            indices.Add(first + 2);
            indices.Add(first + 3);
        }

        private static NeighbourhoodTerrainVertex BlendVertex(
            Vector3 position, Vector3 normal, float transparency, Vector2 groundUv, Vector2 maskUv)
        {
            return new NeighbourhoodTerrainVertex
            {
                Position = position,
                Normal = normal,
                Transparency = transparency,
                TextureCoord = groundUv / TilesPerTextureRepeat,
                MaskTextureCoord = maskUv
            };
        }

        private static void AddRoadQuad(
            List<NeighbourhoodTerrainVertex> verts, List<int> indices, int cell,
            Vector3 p0, Vector3 n0, Vector3 p1, Vector3 n1,
            Vector3 p2, Vector3 n2, Vector3 p3, Vector3 n3)
        {
            var origin = NeighbourhoodRoadLayout.CellOrigin(cell);
            var delta = NeighbourhoodRoadLayout.CellSize;
            var first = verts.Count;
            //the road cells are authored so the cell's top-right is the tile's far corner, which
            //is why the u coordinate is offset by one and the v by none
            verts.Add(Road(p0, n0, origin.X + delta.X, origin.Y));
            verts.Add(Road(p1, n1, origin.X, origin.Y));
            verts.Add(Road(p2, n2, origin.X, origin.Y + delta.Y));
            verts.Add(Road(p3, n3, origin.X + delta.X, origin.Y + delta.Y));

            indices.Add(first);
            indices.Add(first + 1);
            indices.Add(first + 2);
            indices.Add(first);
            indices.Add(first + 2);
            indices.Add(first + 3);
        }

        private static NeighbourhoodTerrainVertex Road(Vector3 position, Vector3 normal, float u, float v)
        {
            return new NeighbourhoodTerrainVertex
            {
                Position = position,
                Normal = normal,
                //Zero, i.e. opaque, and not "the fade value of this tile": a road only ever exists
                //strictly inside the diamond, so its transparency is zero everywhere, which is also
                //what CityGeometry gets by never assigning the field at all. See
                //NeighbourhoodTerrainVertex.Transparency for why the direction of that zero is
                //load-bearing.
                Transparency = 0f,
                TextureCoord = new Vector2(u, v),
                MaskTextureCoord = new Vector2(-1, -1)
            };
        }

        /// <summary>
        /// What a tile blends into, from the four-bit adjacency mask, verbatim from
        /// <c>FSO.Client.Rendering.City.CityGeometry.GetBlend</c>
        /// (<c>CityGeometry.cs:41-80</c>).
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The whole cross-fade, in one function.</b> A tile is surrounded by four neighbours
        /// which may each be a higher-numbered terrain. Each one that is contributes a bit, the
        /// four bits name a cell of a 7x3 mask atlas, and a second quad is drawn on top of the
        /// tile's own in that mask's layer, alpha-blended by the mask. That is the entire
        /// mechanism by which a TSO city has a beach that fades into grass rather than a
        /// stair-stepped border between two textures, so it is reproduced rather than dropped -
        /// and the blend atlas it samples is generated (see
        /// <see cref="NeighbourhoodContent.BlendAtlas"/>) for the same reason the ground textures
        /// are: TSO's own masks are <c>gamedata/terrain/newformat/transa*.tga</c>, which are TSO
        /// game data and do not ship.
        /// </para>
        /// <para>
        /// <b>Three details are load-bearing and are reproduced exactly.</b> The mask bit order is
        /// not the edge order (see <see cref="NeighbourhoodBlendFlags"/>). A neighbour counts only
        /// if its terrain number is <em>strictly greater</em> than this tile's and is not 255 - so
        /// a tile never blends "downhill" and never blends into nothing. And the blend layer is the
        /// <em>lowest</em> qualifying neighbour, not the highest: <c>maxEdge</c> starts at 4 and
        /// only ever moves down, so grass next to both sand and rock blends into sand. The variable
        /// name is the reference's; the behaviour is what matters.
        /// </para>
        /// <para>
        /// The first neighbour read uses <c>Math.Abs((i - 1) * 512 + j)</c> upstream, which is a
        /// wrapped index for row zero. The <c>(i - 1 &gt;= 0)</c> guard discards the value in
        /// exactly that case, so the read is guarded here rather than reproduced: the answer is
        /// identical and the out-of-range index is not.
        /// </para>
        /// </remarks>
        /// <param name="terrain">The map's terrain type bytes, row-major over a 512x512 square.</param>
        /// <param name="i">The tile's row.</param>
        /// <param name="j">The tile's column, already clamped into the row's diamond.</param>
        public static NeighbourhoodBlend GetBlend(byte[] terrain, int i, int j)
        {
            // -1 is "this edge has no higher-priority neighbour", and it is the initial value
            // rather than a computed one, which is what makes an absent edge fall out of the mask
            // arithmetic below without a special case.
            var edges = new[] { -1, -1, -1, -1 };
            var sample = terrain[i * MapSize + j];

            if (i - 1 >= 0)
            {
                var t = terrain[(i - 1) * MapSize + j];
                if (t > sample && t != NeighbourhoodTerrain.Nothing) edges[0] = t;
            }
            if (j + 1 < MapSize)
            {
                var t = terrain[i * MapSize + j + 1];
                if (t > sample && t != NeighbourhoodTerrain.Nothing) edges[1] = t;
            }
            if (i + 1 < MapSize)
            {
                var t = terrain[Math.Min(i + 1, MapSize - 1) * MapSize + j];
                if (t > sample && t != NeighbourhoodTerrain.Nothing) edges[2] = t;
            }
            if (j - 1 >= 0)
            {
                var t = terrain[i * MapSize + j - 1];
                if (t > sample && t != NeighbourhoodTerrain.Nothing) edges[3] = t;
            }

            // A bit is set when the edge is *absent*, which is why this is a chain of ternaries on
            // "> -1" rather than a sum of the edges themselves. CityGeometry.cs:60-64.
            var binary =
                ((edges[0] > -1) ? 0 : 2) |
                ((edges[1] > -1) ? 0 : 1) |
                ((edges[2] > -1) ? 0 : 8) |
                ((edges[3] > -1) ? 0 : 4);

            // Starts at water and only moves down, so a tile with no qualifying neighbour blends
            // into water and a tile whose lowest higher neighbour is sand blends into sand.
            // CityGeometry.cs:69-72.
            var maxEdge = 4;
            for (int x = 0; x < 4; x++)
            {
                if (edges[x] < maxEdge && edges[x] != -1) maxEdge = edges[x];
            }

            return new NeighbourhoodBlend(binary,
                NeighbourhoodBlendFlags.AtlasPositionFor(binary), maxEdge);
        }

        /// <summary>
        /// The surface normal at a tile, from the central differences of the elevation field on the
        /// two axes, exactly as <c>CityGeometry.GetNormalAt</c> computes it.
        /// </summary>
        /// <remarks>
        /// The <c>x &gt; 1</c> and <c>y &gt; 1</c> guards rather than <c>&gt; 0</c> are the upstream
        /// ones and are reproduced deliberately: they skip the second sample on the two tiles next
        /// to the border, which is invisible and would otherwise read a mirrored elevation. A
        /// <c>Vector3.Zero</c> result is left alone so a flat tile's normal is straight up, which is
        /// what the diffuse term wants.
        /// </remarks>
        public static Vector3 NormalAt(byte[] elevation, int x, int y)
        {
            var sum = Vector3.Zero;

            if (x < MapSize - 1)
            {
                var v = new Vector3(1f, ElevationAt(elevation, x + 1, y) - ElevationAt(elevation, x, y), 0f);
                sum += Vector3.Transform(v, RotationToNormalXY);
            }
            if (x > 1)
            {
                var v = new Vector3(1f, ElevationAt(elevation, x, y) - ElevationAt(elevation, x - 1, y), 0f);
                sum += Vector3.Transform(v, RotationToNormalXY);
            }
            if (y < MapSize - 1)
            {
                var v = new Vector3(0f, ElevationAt(elevation, x, y + 1) - ElevationAt(elevation, x, y), 1f);
                sum += Vector3.Transform(v, RotationToNormalZY);
            }
            if (y > 1)
            {
                var v = new Vector3(0f, ElevationAt(elevation, x, y) - ElevationAt(elevation, x, y - 1), 1f);
                sum += Vector3.Transform(v, RotationToNormalZY);
            }

            if (sum != Vector3.Zero) sum.Normalize();
            return sum;
        }

        private static float ElevationAt(byte[] elevation, int x, int y)
        {
            var cx = x < 0 ? 0 : (x > MapSize - 1 ? MapSize - 1 : x);
            var cy = y < 0 ? 0 : (y > MapSize - 1 ? MapSize - 1 : y);
            return NormalRiseFor(elevation[cy * MapSize + cx]);
        }

        /// <summary>
        /// The average ground height of a map, in world units - what the camera should look at, so
        /// a hilly neighbourhood is not viewed from below its own horizon.
        /// </summary>
        /// <remarks>
        /// The mean is taken in bytes and converted once, by <see cref="WorldHeightFor"/>, rather
        /// than in tiles: the division is the same either way and this way there is only one of it.
        /// </remarks>
        /// <param name="map">The generated map.</param>
        public static float MeanGroundHeight(NeighbourhoodMap map)
        {
            if (map?.ElevationData == null || map.ElevationData.Length == 0) return 0f;
            long total = 0;
            for (int i = 0; i < map.ElevationData.Length; i++) total += map.ElevationData[i];
            return WorldHeightFor((float)(total / (double)map.ElevationData.Length));
        }
    }

    /// <summary>
    /// A second, independent derivation of what the mesh builder should produce, for a test to
    /// check the builder against.
    ///
    /// <para>
    /// The builder walks the map the way TSO's city geometry does - 32x32 chunks, a ten-tile fade
    /// skirt past the isometric diamond, one quad per surviving tile - and that loop is exactly
    /// the sort of arithmetic that is wrong in a way nothing notices until somebody looks at a
    /// picture. So the interesting numbers are also derivable another way: the diamond's area is
    /// analytic, and whether a tile produced a quad is a per-tile predicate with no state. Where
    /// the two derivations disagree, one of them has a bug, and the test says which.
    /// </para>
    ///
    /// <para>
    /// It is also the artifact that records what the reuse of <c>CityGeometry</c> would have done,
    /// since that is no longer callable from here. The diamond constants, the fade, the elevation
    /// scale and the road cell tables all came from it, and they are the four things Scope A would
    /// have inherited for free by reusing it.
    /// </para>
    /// </summary>
    public static class NeighbourhoodCityMeshProbe
    {
        /// <summary>
        /// The first terrain type byte <paramref name="map"/> holds that is neither one of the five
        /// drawable terrains nor 255, or <c>-1</c>.
        /// </summary>
        /// <remarks>
        /// This is the one way a generated map can break a mesh builder - the terrain type is a
        /// raw byte used as a layer index, and there are exactly five layers - and 255 is fine
        /// because it is the documented "emit nothing" value and is skipped before the index is
        /// used. <see cref="NeighbourhoodTerrain.IsDrawable"/> is the same rule on the write side,
        /// so a value that survives this could only have come from outside the generator.
        /// </remarks>
        /// <param name="map">The map to scan, or <c>null</c>.</param>
        public static int FirstUnmeshableTerrainType(NeighbourhoodMap map)
        {
            if (map == null) return -1;
            var types = map.TerrainType;
            for (int i = 0; i < types.Length; i++)
            {
                var t = types[i];
                if (!NeighbourhoodTerrain.IsDrawable(t) && t != NeighbourhoodTerrain.Nothing) return t;
            }
            return -1;
        }

        /// <summary>
        /// The number of map rows the diamond spans, and its total tile count, computed from the
        /// clip's arithmetic rather than by walking chunks.
        /// </summary>
        public readonly struct DiamondSpan
        {
            /// <summary>How many map rows the diamond covers.</summary>
            public int Rows { get; }

            /// <summary>How many tiles the diamond contains.</summary>
            public int Tiles { get; }

            /// <summary>The widest row, in tiles.</summary>
            public int WidestRow { get; }

            internal DiamondSpan(int rows, int tiles, int widestRow)
            {
                Rows = rows;
                Tiles = tiles;
                WidestRow = widestRow;
            }
        }

        /// <summary>
        /// The diamond's extent, derived analytically: the sum of <c>right - left</c> over every
        /// row, which is also the per-tile upper bound on the builder's own tile count.
        /// </summary>
        public static DiamondSpan AnalyticDiamond()
        {
            long tiles = 0;
            var widest = 0;
            var rows = 0;
            for (int row = 0; row < NeighbourhoodCityMeshBuilder.MapSize; row++)
            {
                var left = NeighbourhoodCityMeshBuilder.DiamondLeft(row);
                var right = NeighbourhoodCityMeshBuilder.DiamondRight(row);
                var span = right > left ? right - left : 0;
                if (span > 0) rows++;
                if (span > widest) widest = span;
                tiles += span;
            }
            return new DiamondSpan(rows, (int)tiles, widest);
        }

        /// <summary>
        /// What the mesh builder should produce for this map, counted a tile at a time.
        /// </summary>
        /// <param name="map">The generated map.</param>
        public static NeighbourhoodCityMeshProbeResult Probe(NeighbourhoodMap map)
        {
            if (map == null) throw new ArgumentNullException(nameof(map));

            var result = new NeighbourhoodCityMeshProbeResult();
            if (map.Width != NeighbourhoodCityMeshBuilder.MapSize || map.Height != NeighbourhoodCityMeshBuilder.MapSize)
            {
                result.Refusal = "map is " + map.Width + "x" + map.Height
                    + ", not " + NeighbourhoodCityMeshBuilder.MapSize + "x" + NeighbourhoodCityMeshBuilder.MapSize;
                return result;
            }

            var terrain = map.TerrainType;
            var road = map.RoadData;

            for (int row = 0; row < NeighbourhoodCityMeshBuilder.MapSize; row++)
            {
                var rowLeft = NeighbourhoodCityMeshBuilder.DiamondLeft(row);
                var rowRight = NeighbourhoodCityMeshBuilder.DiamondRight(row);
                if (rowRight <= rowLeft) continue;

                //inside the diamond, every tile yields a quad; the fade skirt is walked tile by
                //tile here rather than chunk by chunk, which is the whole point of this derivation
                for (int x = 0; x < NeighbourhoodCityMeshBuilder.MapSize; x++)
                {
                    //Exactly the builder's per-row window, which is [left - Fade, right + Fade) -
                    //ten tiles of skirt on the left *inclusive* of the outermost, and nine on the
                    //right *exclusive* of it. The asymmetry is the city clip's own (its chunk loop
                    //clamps the right edge against the chunk boundary), and reproducing it here is
                    //the whole point: a probe that "tidied" it would disagree with the builder by a
                    //tile on every one of 512 rows.
                    if (x < rowLeft - NeighbourhoodCityMeshBuilder.FadeRange) continue;
                    if (x >= rowRight + NeighbourhoodCityMeshBuilder.FadeRange) continue;

                    var inside = NeighbourhoodCityMeshBuilder.IsInsideDiamond(x, row);
                    var clampedX = Math.Min(Math.Max(rowLeft, x), rowRight - 1);
                    var type = terrain[row * NeighbourhoodCityMeshBuilder.MapSize + clampedX];
                    if (type >= NeighbourhoodCityMeshBuilder.LayerCount)
                    {
                        result.UnmeshableTiles++;
                        if (result.FirstUnmeshableType < 0) result.FirstUnmeshableType = type;
                        continue;
                    }

                    result.VisitedTiles++;
                    result.LayerQuads[type]++;

                    //And the blend face, on the same inside-the-diamond rule the builder uses.
                    //Counted here so the probe keeps describing the whole mesh rather than only its
                    //ground, and derived from GetBlend rather than from the builder, so a change to
                    //the mask arithmetic that the builder does not share is a failing test.
                    if (!inside) continue;

                    var blend = NeighbourhoodCityMeshBuilder.GetBlend(terrain, row, clampedX);
                    if (blend.NeedsBlend && blend.MaxEdge < NeighbourhoodCityMeshBuilder.LayerCount)
                    {
                        result.BlendQuads++;
                        result.BlendQuadsPerLayer[blend.MaxEdge]++;
                        result.NoteBlend(blend);
                    }

                    var roadByte = road[row * NeighbourhoodCityMeshBuilder.MapSize + clampedX];
                    var straight = roadByte & RoadMask.MaskBits;
                    var corner = roadByte >> 4;
                    if (straight > 0)
                    {
                        result.RoadQuads++;
                        result.NoteRoadCell(NeighbourhoodRoadLayout.CellFor(straight, false));
                    }
                    if (corner > 0)
                    {
                        result.CornerQuads++;
                        result.NoteRoadCell(NeighbourhoodRoadLayout.CellFor(corner, true));
                    }
                }
            }

            return result;
        }
    }

    /// <summary>What <see cref="NeighbourhoodCityMeshProbe.Probe"/> found.</summary>
    public sealed class NeighbourhoodCityMeshProbeResult
    {
        /// <summary>Ground quads per layer, indexed by <see cref="NeighbourhoodTerrain"/> value.</summary>
        public readonly int[] LayerQuads = new int[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>Straight/T/cross road quads, from the low nibble.</summary>
        public int RoadQuads;

        /// <summary>Turn/T-junction quads, from the high nibble.</summary>
        public int CornerQuads;

        /// <summary>
        /// Blend faces: one per strictly-inside tile with a higher-numbered neighbour and a mask
        /// below <see cref="NeighbourhoodBlendFlags.NoBlend"/>.
        /// </summary>
        public int BlendQuads;

        /// <summary>
        /// Blend faces per ground layer they land in, so a layer buffer's triangle count can be
        /// checked as <c>LayerQuads[type] + BlendQuadsPerLayer[type]</c>.
        /// </summary>
        public readonly int[] BlendQuadsPerLayer = new int[NeighbourhoodCityMeshBuilder.LayerCount];

        /// <summary>Distinct blend masks the map reaches.</summary>
        public readonly HashSet<int> BlendMasks = new HashSet<int>();

        /// <summary>Distinct blend atlas cells the map reaches, from <see cref="NeighbourhoodBlendFlags.FlagLayout"/>.</summary>
        public readonly HashSet<int> BlendAtlasCells = new HashSet<int>();

        /// <summary>Distinct tiles the mesh covers.</summary>
        public int VisitedTiles;

        /// <summary>
        /// Tiles whose terrain type could not name a layer. Anything above zero is a refusal, not
        /// a warning.
        /// </summary>
        public int UnmeshableTiles;

        /// <summary>The first offending terrain type, or -1.</summary>
        public int FirstUnmeshableType = -1;

        /// <summary>Set instead of the counts when the map is the wrong size.</summary>
        public string Refusal;

        /// <summary>Every ground quad, all five layers.</summary>
        public int TotalLayerQuads
        {
            get
            {
                int total = 0;
                for (int i = 0; i < LayerQuads.Length; i++) total += LayerQuads[i];
                return total;
            }
        }

        /// <summary>Every quad the mesh should hold, blends included.</summary>
        public int TotalQuads
        {
            get { return TotalLayerQuads + BlendQuads + RoadQuads + CornerQuads; }
        }

        /// <summary>
        /// Records a blend and flags it if its mask or its atlas cell is not one the atlas has.
        /// </summary>
        /// <remarks>
        /// The atlas-cell check is the same shape as <see cref="NoteRoadCell"/>: a mask that remaps
        /// outside the 7x3 grid would make the blend sample a neighbouring cell or wrap the atlas,
        /// and the symptom is a blend in the wrong place, which no screenshot attributes to a
        /// table.
        /// </remarks>
        /// <param name="blend">The blend a tile resolved to.</param>
        public void NoteBlend(NeighbourhoodBlend blend)
        {
            if (blend.Binary < 0 || blend.Binary >= NeighbourhoodBlendFlags.MaskCount)
            {
                OutOfAtlasCells++;
                return;
            }
            var cell = NeighbourhoodBlendFlags.FlagLayout[blend.Binary];
            BlendMasks.Add(blend.Binary);
            if (cell < 0 || cell >= NeighbourhoodBlendFlags.AtlasWidth * NeighbourhoodBlendFlags.AtlasHeight)
            {
                OutOfAtlasCells++;
                return;
            }
            BlendAtlasCells.Add(cell);
        }

        /// <summary>Road atlas cells the map actually uses.</summary>
        public readonly HashSet<int> RoadCells = new HashSet<int>();

        /// <summary>Road atlas cells that do not exist. Anything above zero is a refusal.</summary>
        public int OutOfAtlasCells;

        /// <summary>Whether the map is meshable at all.</summary>
        public bool IsMeshable
        {
            get { return Refusal == null && UnmeshableTiles == 0 && VisitedTiles > 0; }
        }

        /// <summary>
        /// Records a road atlas cell and flags it if the 8x4 atlas has no such cell.
        /// </summary>
        /// <param name="cell">The cell index out of the layout tables.</param>
        public void NoteRoadCell(int cell)
        {
            if (!NeighbourhoodRoadLayout.IsRealCell(cell))
            {
                OutOfAtlasCells++;
                return;
            }
            RoadCells.Add(cell);
        }

        public override string ToString()
        {
            if (Refusal != null) return "not meshable: " + Refusal;
            var text = new System.Text.StringBuilder();
            text.Append("visited ").Append(VisitedTiles).Append(" tiles, quads");
            for (int i = 0; i < LayerQuads.Length; i++)
            {
                text.Append(' ').Append('[').Append(i).Append("]=").Append(LayerQuads[i]);
            }
            text.Append(" road ").Append(RoadQuads).Append(" corner ").Append(CornerQuads)
                .Append(" cells ").Append(RoadCells.Count);
            if (UnmeshableTiles > 0) text.Append(" UNMESHABLE ").Append(UnmeshableTiles);
            if (OutOfAtlasCells > 0) text.Append(" OUT_OF_ATLAS ").Append(OutOfAtlasCells);
            return text.ToString();
        }
    }
}
