/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using Microsoft.Xna.Framework;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The five tiling ground textures a generated neighbourhood is drawn with, as pixel data.
    ///
    /// <para><b>Why these are generated rather than shipped.</b> §9 is explicit that the road and
    /// tree atlases come out of <c>tso.content</c> and already ship, and equally explicit that the
    /// terrain base textures do not: TSO's own grass/sand/rock/snow/water come from
    /// <c>gamedata/terrain/newformat/*.tga</c>, which is TSO game data and is exactly what Scope A
    /// is trying not to need ("this is what makes Scope A tractable without shipping TSO assets").
    /// The only ground texture in the tree is <c>autumn.png</c>, which is one of the five. So four
    /// of the five are made here, from a named base colour and a deterministic value noise, and the
    /// first version is deliberately flat-per-layer anyway - §0 decision 3 and §7.1 both drop the
    /// blend-mask cross-fades, so a texture is only ever seen at one scale, tiling every four
    /// tiles, with no per-tile tint over it.
    /// </para>
    ///
    /// <para><b>Why 64x64 and tileable.</b> <c>CityGeometry</c> writes the terrain UVs as
    /// <c>(x, y) / 4</c> with a wrapping sampler, so one texture repeat covers four tiles. 64x64
    /// over four tiles is 16 pixels per tile, which is enough to read as ground and small enough
    /// that five of them cost 80 KB.
    /// </para>
    ///
    /// <para><b>Tileable means the lattice is periodic, and it has to be.</b> The obvious way to
    /// write this - hash a lattice point with an FNV mix and interpolate - produces a texture whose
    /// opposite edges are unrelated, because the lattice value at index <c>n + 4</c> has nothing
    /// to do with the one at index <c>n</c>. That is fine for a texture drawn once and not fine
    /// for one drawn a hundred and twenty-eight times across a 512-tile map: every repeat draws a
    /// hard discontinuity, and the whole ground reads as a grid of glitches. So the lattice is
    /// looked up <em>modulo its period</em> - <see cref="LatticePeriod"/> cells across for the
    /// coarse octave and <c>LatticePeriod * FineOctave</c> for the fine one - and the texture then
    /// tiles exactly, which <see cref="NoiseAt"/> at <c>Size</c> versus <c>0</c> lets a test assert without a device.
    /// </para>
    ///
    /// <para><b>Pure, so it can be asserted.</b> This is the one piece of the renderer's content
    /// that a test can check without a graphics device: that every terrain index produces exactly
    /// <see cref="Size"/>x<see cref="Size"/> opaque pixels, that the noise is deterministic, and
    /// that the texture tiles (its opposite edges agree).
    /// </para>
    /// </summary>
    public static class NeighbourhoodTerrainTextures
    {
        /// <summary>Edge length of one ground texture, in pixels.</summary>
        public const int Size = 64;

        /// <summary>How many tiles one texture repeat covers; <c>CityGeometry</c>'s <c>/ 4</c>.</summary>
        public const int TilesPerRepeat = 4;

        /// <summary>
        /// How many lattice cells the coarse octave spans across the texture. The coarse octave
        /// runs at four cells across <see cref="Size"/> pixels, so four is also its period, and
        /// wrapping the lattice modulo four is what makes the texture tile.
        /// </summary>
        public const int LatticePeriod = 4;

        /// <summary>
        /// How many times the fine octave is finer than the coarse one. Its period is therefore
        /// <c>LatticePeriod * FineOctave</c>, and it has to be wrapped at that rather than at
        /// <see cref="LatticePeriod"/> or the fine detail gets a seam of its own.
        /// </summary>
        public const int FineOctave = 3;

        /// <summary>
        /// The base colour of each terrain, before the noise is applied. Ordered by
        /// <see cref="NeighbourhoodTerrain"/> value so the array can be indexed by it directly.
        /// </summary>
        private static readonly Color[] BaseColors =
        {
            new Color(0x5E, 0x8C, 0x3C),     // grass - a TS1-ish mid green
            new Color(0xD8, 0xC8, 0x8E),     // sand
            new Color(0x84, 0x80, 0x7C),     // rock
            new Color(0xEC, 0xF2, 0xF6),     // snow
            new Color(0x2E, 0x62, 0x9E)      // water
        };

        /// <summary>
        /// How far the noise may push a channel away from the base colour, per channel. Small on
        /// purpose: a neighbourhood seen from above wants readable ground, not a texture that
        /// fights the roads drawn on top of it.
        /// </summary>
        public const int NoiseAmplitude = 14;

        /// <summary>The number of drawable ground textures.</summary>
        public const int Count = 5;

        /// <summary>The base colour of one terrain, before noise.</summary>
        /// <param name="terrainType">A <see cref="NeighbourhoodTerrain"/> value.</param>
        public static Color BaseColorFor(byte terrainType)
        {
            return terrainType < Count ? BaseColors[terrainType] : new Color(0xFF, 0x00, 0xFF);
        }

        /// <summary>
        /// The pixel data of one ground texture, row-major, bottom-up as
        /// <c>Texture2D.SetData</c> wants it.
        /// </summary>
        /// <remarks>
        /// The noise is a two-octave wrapped value noise on a small integer lattice, chosen over
        /// <c>System.Random</c> for the reason <c>NeighbourhoodRng</c> gives: it is specified
        /// arithmetic, so the same texture comes out on every machine and every run, and it can be
        /// wrapped by construction rather than by blending the edges afterwards.
        /// </remarks>
        /// <param name="terrainType">A <see cref="NeighbourhoodTerrain"/> value.</param>
        public static Color[] PixelsFor(byte terrainType)
        {
            var baseColor = BaseColorFor(terrainType);
            var pixels = new Color[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    pixels[y * Size + x] = Shade(baseColor, NoiseAt(x, y, terrainType));
                }
            }
            return pixels;
        }

        private static Color Shade(Color baseColor, int noise)
        {
            return new Color(
                Clamp(baseColor.R + noise),
                Clamp(baseColor.G + (noise * 3) / 4),
                Clamp(baseColor.B + noise / 2),
                (byte)255);
        }

        private static int Wrap(int value, int period)
        {
            if (period <= 0) return 0;
            var wrapped = value % period;
            return wrapped < 0 ? wrapped + period : wrapped;
        }

        /// <summary>
        /// The signed noise at a pixel, in <c>[-NoiseAmplitude, NoiseAmplitude]</c>.
        /// </summary>
        /// <param name="x">Pixel x.</param>
        /// <param name="y">Pixel y.</param>
        /// <param name="terrainType">
        /// The terrain, which shifts the lattice phase so two terrains do not share a pattern and
        /// so grass and sand are not the same noise wearing different colours.
        /// </param>
        public static int NoiseAt(int x, int y, byte terrainType)
        {
            //the coordinate the noise is sampled at. The terrain offset is what keeps two
            //terrains off the same pattern; it is inside the phase rather than multiplied onto
            //the result, so it does not disturb the periodicity below
            var fx = (float)x / Size * LatticePeriod + terrainType * 3.7f;
            var fy = (float)y / Size * LatticePeriod + terrainType * 5.3f;

            //Wrapping the lattice is what makes the texture tile. Sampling at x = Size has to
            //give exactly what sampling at x = 0 gives, and the only way to arrange that with a
            //hash is to reduce the lattice index modulo the octave's own period - four cells for
            //the coarse octave, twelve for the one three times finer.
            var coarse = SmoothNoise(fx, fy, LatticePeriod);
            var fine = SmoothNoise(fx * FineOctave, fy * FineOctave, LatticePeriod * FineOctave);
            var combined = coarse * 0.65f + fine * 0.35f; // -1..1
            return (int)Math.Round(combined * NoiseAmplitude);
        }

        private static float SmoothNoise(float x, float y, int period)
        {
            var x0 = (int)Math.Floor(x);
            var y0 = (int)Math.Floor(y);
            var tx = Fade(x - x0);
            var ty = Fade(y - y0);

            var v00 = Lattice(Wrap(x0, period), Wrap(y0, period));
            var v10 = Lattice(Wrap(x0 + 1, period), Wrap(y0, period));
            var v01 = Lattice(Wrap(x0, period), Wrap(y0 + 1, period));
            var v11 = Lattice(Wrap(x0 + 1, period), Wrap(y0 + 1, period));

            var top = v00 + (v10 - v00) * tx;
            var bottom = v01 + (v11 - v01) * tx;
            return top + (bottom - top) * ty;
        }

        /// <summary>
        /// A deterministic hash of a lattice point. FNV-style over the two mixed coordinates, so
        /// the same point is the same value in every process - the same reason
        /// <c>NeighbourhoodRng.Fingerprint</c> does not use <c>string.GetHashCode</c>.
        /// </summary>
        private static float Lattice(int x, int y)
        {
            unchecked
            {
                var h = 2166136261u ^ (uint)x;
                h = (h ^ (uint)y) * 16777619u;
                h ^= h >> 13;
                h = (h ^ (h << 7)) * 2654435761u;
                var unit = (h & 0xFFFF) / 32767.5f; //0..2, centre 1
                return unit - 1f;
            }
        }

        private static float Fade(float t)
        {
            return t * t * (3f - 2f * t);
        }

        private static byte Clamp(int value)
        {
            if (value < 0) return 0;
            if (value > 255) return 255;
            return (byte)value;
        }
    }

    //NeighbourhoodFoliageTint used to live here. It is about which of the shipped Content/3D
    //tree models a tile draws, which is the foliage pass's business and not the ground's, and it
    //now carries the table of the eighteen files those models are - so it lives beside its only
    //consumer, in NeighbourhoodFoliage.cs. Same namespace, same public names.

    /// <summary>
    /// The blend-mask atlases, as pixel data: a 7x3 grid of cells, one per adjacency mask, each
    /// cell a soft ramp that is opaque on the edges with a higher-priority neighbour and clear in
    /// the middle of the tile.
    ///
    /// <para>
    /// <b>The layout is <c>CityContent.CreateTransparencyAtlas</c>'s, exactly.</b> 7x3 cells, the
    /// <see cref="NeighbourhoodBlendFlags.FlagLayout"/> remap deciding which cell a mask occupies,
    /// and the whole atlas cleared to black first so a mask that names no cell reads as "no blend"
    /// - which is what the reference's explicit <c>spriteBatch.Draw(black, ...)</c> at
    /// <c>CityContent.cs:287-290</c> is doing.
    /// </para>
    ///
    /// <para>
    /// <b>Only the pixels are ours.</b> TSO's own masks are sixty
    /// <c>gamedata/terrain/newformat/trans[ab]*.tga</c> files of game data that do not ship, so
    /// these are generated - the same substitution <see cref="NeighbourhoodTerrainTextures"/> makes
    /// for the ground textures and for the same reason (§9.3). The geometry's contract is
    /// untouched: the same mask, the same cell, the same <c>(1/7, 1/3)</c> deltas and the same
    /// per-layer atlas index, so a mask that lands in the wrong cell is still a bug here rather than
    /// a content difference.
    /// </para>
    ///
    /// <para>
    /// <b>What a ramp is.</b> For each edge the mask names as <em>having</em> a higher-priority
    /// neighbour, the alpha falls from one at the tile's edge to zero at its middle; the cell is
    /// the maximum of those. A mask naming two opposite edges is a band across the tile, one naming
    /// two adjacent edges is a corner, and one naming all four is solid - which is the set of shapes
    /// <c>PixShader.fx</c>'s <c>GetNCityColor</c> is built to composite (<c>PixShader.fx:379-391</c>).
    /// </para>
    /// </remarks>
    public static class NeighbourhoodBlendTextures
    {
        /// <summary>Edge length of one mask cell, in pixels.</summary>
        public const int CellSize = 64;

        /// <summary>Cells across; <c>NeighbourhoodBlendFlags.AtlasWidth</c>.</summary>
        public const int AtlasWidth = NeighbourhoodBlendFlags.AtlasWidth * CellSize;

        /// <summary>Cells down; <c>NeighbourhoodBlendFlags.AtlasHeight</c>.</summary>
        public const int AtlasHeight = NeighbourhoodBlendFlags.AtlasHeight * CellSize;

        /// <summary>
        /// How far into the tile each edge's ramp reaches, as a fraction of the half-tile. One is a
        /// straight ramp to the middle; smaller leaves a clear core.
        /// </summary>
        public const float RampReach = 0.5f;

        /// <summary>
        /// How wide each atlas's ramp is, per atlas index. The four differ so the four ground layers
        /// do not all cross-fade at one rate, which is the distinction the four atlases exist for -
        /// <c>CityContent</c> has four because TransA and TransB are two painted mask sets, each in
        /// an a and a b variant.
        /// </summary>
        private static readonly float[] RampWidths = { 0.85f, 1f, 1.15f, 1.3f };

        /// <summary>The pixel data of one blend atlas, row-major, bottom-up as <c>Texture2D.SetData</c> wants it.</summary>
        /// <param name="atlas">Which of the four atlases, 0 to 3.</param>
        public static Color[] PixelsFor(int atlas)
        {
            var width = RampWidths[Math.Max(0, Math.Min(RampWidths.Length - 1, atlas))];
            var pixels = new Color[AtlasWidth * AtlasHeight];
            for (int y = 0; y < AtlasHeight; y++)
            {
                for (int x = 0; x < AtlasWidth; x++)
                {
                    var mask = MaskAt(x, y);
                    var alpha = mask < 0 ? 0f : AlphaFor(mask, x, y, width);
                    var level = (byte)Math.Max(0, Math.Min(255, (int)Math.Round(alpha * 255f)));
                    //White, because the shader reads the red channel as the alpha
                    // (PixShader.fx:383) and multiplying a colour by a tinted mask would tint the
                    // ground. The alpha channel is set as well so a debug view of the atlas is
                    // readable rather than opaque.
                    pixels[y * AtlasWidth + x] = new Color(level, level, level, level);
                }
            }
            return pixels;
        }

        /// <summary>
        /// Which mask a pixel of the atlas belongs to, or <c>-1</c> for a cell no mask names.
        /// </summary>
        /// <remarks>
        /// The inverse of <see cref="NeighbourhoodBlendFlags.FlagLayout"/>, walked rather than
        /// searched so a cell claimed by two masks is impossible by construction. The six unnamed
        /// cells of the 7x3 grid come back <c>-1</c> and stay black, which is the reference's
        /// behaviour.
        /// </remarks>
        /// <param name="x">Pixel x within the atlas.</param>
        /// <param name="y">Pixel y within the atlas.</param>
        public static int MaskAt(int x, int y)
        {
            var cx = Math.Max(0, Math.Min(NeighbourhoodBlendFlags.AtlasWidth - 1, x / CellSize));
            var cy = Math.Max(0, Math.Min(NeighbourhoodBlendFlags.AtlasHeight - 1, y / CellSize));
            var cell = cx + cy * NeighbourhoodBlendFlags.AtlasWidth;
            for (int m = 0; m < NeighbourhoodBlendFlags.MaskCount; m++)
            {
                if (NeighbourhoodBlendFlags.FlagLayout[m] == cell) return m;
            }
            return -1;
        }

        /// <summary>
        /// The alpha a cell's pixel holds: the strongest of the ramps for the edges that have a
        /// higher-priority neighbour.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Mask zero is solid, and that is a decision rather than a consequence.</b> A mask naming
        /// one edge is a ramp from that edge to the tile's middle; a mask naming all four is the
        /// union of four such ramps, which leaves the exact centre uncovered - a point of zero area,
        /// but a handful of texels that read as four dim corners meeting. The reference sidesteps the
        /// question: mask 0 is the "this tile is entirely surrounded by higher terrain" case, there
        /// is no base terrain left for it to fade from, and its painted mask is opaque. So mask 0 is
        /// solid here too, and the test says so rather than leaving it to be discovered as a bright
        /// dot in the middle of a fully-blended patch.
        /// </para>
        /// <para>
        /// <b>The bit order is <see cref="NeighbourhoodBlendFlags"/>'s, not the edge order.</b> Bit
        /// 0 is edge 1 (+X), bit 1 is edge 0 (-Z), bit 2 is edge 3 (-X) and bit 3 is edge 2 (+Z) -
        /// because a set bit names an <em>absent</em> edge, so a cell ramps on the edges whose bits
        /// are <em>clear</em>. Getting that backwards produces a ramp on the wrong sides of the
        /// tile, which reads as a plausible-looking but wrong blend rather than as an error.
        /// </para>
        /// <para>
        /// <b>Which side of the cell each edge is on.</b> The mask's U runs along the tile's +X and
        /// its V along its +Z, with <c>(0,0)</c> at the tile's <c>(j, i)</c> corner -
        /// <c>CityGeometry.cs:316-343</c> walks the four corners as <c>bOff</c>,
        /// <c>bOff + (dx,0)</c>, <c>bOff + (dx,dy)</c>, <c>bOff + (0,dy)</c> for
        /// <c>(j,i)</c>, <c>(j+1,i)</c>, <c>(j+1,i+1)</c>, <c>(j,i+1)</c>. So:
        /// <list type="bullet">
        /// <item><description>edge 1 (+X) is the cell's <b>right</b> edge, at u = 1</description></item>
        /// <item><description>edge 0 (-Z) is the cell's <b>bottom</b>, at v = 0</description></item>
        /// <item><description>edge 3 (-X) is the cell's <b>left</b>, at u = 0</description></item>
        /// <item><description>edge 2 (+Z) is the cell's <b>top</b>, at v = 1</description></item>
        /// </list>
        /// and <see cref="Ramp"/> takes a distance <em>from</em> the edge, so each of those is
        /// <c>1 - u</c>, <c>v</c>, <c>u</c> and <c>1 - v</c> respectively. Every one of the four is
        /// measured from its own edge, so the four lines are the same shape and a sign error in one
        /// of them puts a ramp on the wrong side of the tile.
        /// </para>
        /// <para>
        /// The cell's V is not flipped against the pixel row, and that is deliberate: the vertex
        /// buffer's V of 0 is the tile's low-Z edge and <c>Texture2D.SetData</c>'s row 0 is the
        /// texture's own v of 0, so row 0 of a cell <em>is</em> the low-Z edge. Flipping one and not
        /// the other is how a mask ends up mirrored about the tile's diagonal - a blend that looks
        /// entirely reasonable and fades the wrong way on two of the four sides.
        /// </para>
        /// </remarks>
        /// <param name="mask">A four-bit adjacency mask.</param>
        /// <param name="x">Pixel x within the atlas.</param>
        /// <param name="y">Pixel y within the atlas.</param>
        /// <param name="width">The ramp width for this atlas.</param>
        public static float AlphaFor(int mask, int x, int y, float width)
        {
            if ((mask & 0x0F) == 0) return 1f;

            var u = (x % CellSize) / (float)CellSize;
            var v = (y % CellSize) / (float)CellSize;

            var alpha = 0f;
            //edge 1: +X, at u = 1. Bit 0 clear means present.
            if ((mask & 1) == 0) alpha = Math.Max(alpha, Ramp(1f - u, width));
            //edge 0: -Z, at v = 0. Bit 1 clear means present.
            if ((mask & 2) == 0) alpha = Math.Max(alpha, Ramp(v, width));
            //edge 3: -X, at u = 0. Bit 2 clear means present.
            if ((mask & 4) == 0) alpha = Math.Max(alpha, Ramp(u, width));
            //edge 2: +Z, at v = 1. Bit 3 clear means present.
            if ((mask & 8) == 0) alpha = Math.Max(alpha, Ramp(1f - v, width));
            return alpha;
        }

        /// <summary>
        /// One edge's ramp: opaque at <c>t = 0</c> (the tile's edge) and clear by
        /// <see cref="RampReach"/>.
        /// </summary>
        /// <remarks>
        /// Smoothstepped rather than linear, because a linear ramp has a visible crease where it
        /// meets the clear interior, and the whole point of the mask is that the boundary between
        /// two terrains is not a line. <c>CityContent</c> gets this for free from a painted alpha
        /// ramp; a generated one has to say it.
        /// </remarks>
        /// <param name="t">Distance from the edge, 0 at the edge and 1 at the far side of the tile.</param>
        /// <param name="width">The ramp width for this atlas; one reaches the tile's middle.</param>
        private static float Ramp(float t, float width)
        {
            if (t <= 0f) return 1f;
            var reach = RampReach * width;
            if (t >= reach) return 0f;
            var s = 1f - (t / reach);
            return s * s * (3f - 2f * s);
        }
    }
}
