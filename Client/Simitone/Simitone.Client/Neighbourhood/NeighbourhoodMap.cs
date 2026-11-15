/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Xna.Framework;
using Simitone.Client.Utils;

namespace Simitone.Client.Neighbourhood
{
    /// <summary>
    /// The terrain type indices a generated neighbourhood map uses, and the colour each one is
    /// written as in <see cref="NeighbourhoodMap.TerrainTypeColorData"/>.
    ///
    /// <para>
    /// <b>Parity with <c>FSO.Client.Rendering.City.CityMapData</c>.</b> These five values and the
    /// five colours are the exact inverse of the private <c>TerrainTypeMap</c> dictionary that
    /// <c>CityMapData.Load</c> uses to turn a city's <c>terraintype</c> bitmap into the
    /// <c>TerrainType</c> byte array. Read the dictionary backwards and it is this table:
    /// </para>
    /// <list type="table">
    ///   <item><description><c>(0,255,0)</c> pure green</description><description>0 grass</description></item>
    ///   <item><description><c>(255,255,0)</c> yellow</description><description>1 sand</description></item>
    ///   <item><description><c>(255,0,0)</c> red</description><description>2 rock</description></item>
    ///   <item><description><c>(255,255,255)</c> white</description><description>3 snow</description></item>
    ///   <item><description><c>(12,0,255)</c> blue</description><description>4 water</description></item>
    ///   <item><description><c>(0,0,0)</c> black</description><description>255 nothing</description></item>
    /// </list>
    /// <para>
    /// 255 is the important one: <c>CityGeometry.RegenMeshVerts</c> skips any tile whose
    /// <c>TerrainType</c> is 255, so it means "no terrain here, do not emit a quad". A generated
    /// neighbourhood never uses it - every tile of the square gets a real type, because
    /// <c>CityGeometry</c> already clips the square to TSO's isometric diamond on its own (the
    /// <c>xStart = 306 - i</c> / <c>xEnd = 307 + i</c> clip), and the square's four corners are
    /// outside that diamond and are never visited. Clipping here as well would only hide the
    /// result from the debug dump.
    /// </para>
    /// <para>
    /// The indices are what <c>CityContent</c>'s five <c>TerrainTextures</c> slots are indexed by
    /// ("grass, sand, rock, snow, water" - see <c>CityContent.LoadContent</c>), so a renderer may
    /// use them directly to pick a layer buffer.
    /// </para>
    /// </summary>
    public static class NeighbourhoodTerrain
    {
        /// <summary>Grass. The default for TS1 residential ground, and index 0 in every atlas.</summary>
        public const byte Grass = 0;

        /// <summary>Sand. Used by the beach lots (28, 29, 46-48 in <c>VMTS1Activator.HouseNumToType</c>).</summary>
        public const byte Sand = 1;

        /// <summary>Bare rock.</summary>
        public const byte Rock = 2;

        /// <summary>Snow. Used by the alpine lots (40-42).</summary>
        public const byte Snow = 3;

        /// <summary>Water. Never used for lot interiors; only by the optional water mask.</summary>
        public const byte Water = 4;

        /// <summary>
        /// "Nothing" - the index <c>CityGeometry</c> skips without emitting geometry. A generated
        /// map does not produce it, but it is named here so the parity with <c>CityMapData</c> is
        /// complete and so a future mask (a void outside a hand-authored outline) has a home.
        /// </summary>
        public const byte Nothing = 255;

        /// <summary>The highest index that names a real terrain texture.</summary>
        public const byte MaxDrawable = Water;

        private static readonly Color[] ByIndex =
        {
            new Color(0, 255, 0),     // grass
            new Color(255, 255, 0),   // sand
            new Color(255, 0, 0),     // rock
            new Color(255, 255, 255), // snow
            new Color(12, 0, 255),    // water
        };

        /// <summary>
        /// The colour <c>CityMapData.Load</c>'s <c>TerrainTypeMap</c> maps
        /// <paramref name="type"/> to, or black for anything else (which is the same
        /// <c>(0,0,0) -> 255</c> entry, i.e. "nothing, don't blend into this").
        /// </summary>
        /// <param name="type">A terrain type index.</param>
        public static Color ColorFor(byte type)
        {
            return type <= MaxDrawable ? ByIndex[type] : new Color(0, 0, 0);
        }

        /// <summary>
        /// Whether <paramref name="type"/> names one of the five drawable terrains. Anything
        /// else - notably <see cref="Nothing"/> and any out-of-range byte from a corrupt input -
        /// is rejected, so an input lot cannot inject an index that would index past a five-entry
        /// texture array.
        /// </summary>
        /// <param name="type">A terrain type index.</param>
        public static bool IsDrawable(byte type)
        {
            return type <= MaxDrawable;
        }
    }

    /// <summary>
    /// A generated neighbourhood world model: five parallel 512x512 arrays whose element
    /// semantics are those of <c>FSO.Client.Rendering.City.CityMapData</c>.
    ///
    /// <para>
    /// <b>Why this type exists instead of just returning a <c>CityMapData</c>.</b>
    /// <c>Simitone.Client</c> does not reference <c>tso.client</c> - <c>CityMapData</c> and
    /// <c>CityContent</c> are in the TSO client, which Simitone deliberately does not depend on
    /// (see the project references in <c>Simitone.Client.csproj</c>). M2 is also a *pure*,
    /// headless, no-graphics-device milestone: its tests must run without a GPU, and
    /// <c>CityMapData</c>'s only constructors either take a <c>Texture2D</c> loader or
    /// <c>SaveTex</c> allocates a <c>Texture2D</c> and marshals onto the game thread. So the
    /// generator fills this instead, and the adapter that hands the arrays to
    /// <c>CityGeometry</c> is M3's job.
    /// </para>
    ///
    /// <para><b>Field-by-field parity, and the one difference that matters.</b></para>
    /// <list type="bullet">
    ///   <item><description>
    ///     <see cref="TerrainType"/> - one byte per tile, row-major with
    ///     <c>index = y * Width + x</c> (identical to <c>CityMapData</c>, which indexes
    ///     <c>terrainType[(i * 512) + ex]</c>). Values are <see cref="NeighbourhoodTerrain"/>
    ///     indices; 255 means "emit nothing".
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="TerrainTypeColorData"/> - the colour each type was authored with, laid out
    ///     exactly as <c>terraintype.bmp</c> is. Only read by code that wants TSO's hand-painted
    ///     per-tile tint (<c>CityContent.ForceSnow</c> reads it); a renderer that draws a flat
    ///     per-layer texture ignores it.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="ElevationData"/> - one byte per tile, same layout.
    ///     <c>CityGeometry</c> divides it by 12 when it places vertices and by 6 when it computes
    ///     normals, so one unit is 1/12 of a tile of height. <b>Inputs are already on this
    ///     scale</b>: a lot's <c>ARRY(0)</c> heights are quantised onto it before they reach the
    ///     generator, which is why <see cref="NeighbourhoodLotInput.BaseHeight"/> is a byte and
    ///     not a float.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="RoadData"/> - one byte per tile, two nibbles. <b>Both nibbles are the same
    ///     4-bit neighbour mask</b> and both are used directly as an index into
    ///     <c>CityContent.RoadLayout</c> (low) and <c>CityContent.RoadCLayout</c> (high); see
    ///     <see cref="RoadMask"/> for the bit order and the derivation. The low nibble is 0 on a
    ///     tile with no road. The high nibble is 0 on a tile whose mask is a straight, a dead end
    ///     or a four-way cross, and the mask itself on a turn or a T-junction.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="ForestDensityData"/> - one byte per tile, 0 = no foliage, 255 = densest.
    ///   </description></item>
    ///   <item><description>
    ///     <see cref="ForestTypeData"/> - the foliage tint for that tile, sampled from a small
    ///     seeded palette by the Poisson-disk pass so a wood reads as one species.
    ///   </description></item>
    /// </list>
    /// <para>
    /// <b>The difference from <c>CityMapData</c>:</b> <c>IsInBounds</c> there is hardcoded to 512
    /// (and has an off-by-one, <c>x &gt; -0</c>). This one asks <see cref="Width"/> /
    /// <see cref="Height"/>, so the same generator produces a small map for a test and a
    /// 512x512 one for the game. <see cref="DefaultSize"/> is 512 to match.
    /// </para>
    /// <para>
    /// Nothing here allocates a graphics resource. <see cref="SavePng"/> writes the same five
    /// files <c>CityMapData.Save</c> does, with the same names, through <c>PngCodec</c> - so the debug
    /// dump works headless in a test and a player (or M3) can drop the arrays into a
    /// <c>CityMapData</c> and use its own writer instead.
    /// </para>
    /// </summary>
    public sealed class NeighbourhoodMap
    {
        /// <summary>
        /// The side length a real generated map uses, and the only size <c>CityGeometry</c>'s
        /// hardcoded 512-wide indexing accepts.
        /// </summary>
        public const int DefaultSize = 512;

        /// <summary>Map width in tiles. Equals <see cref="Height"/> for every map built here.</summary>
        public int Width { get; }

        /// <summary>Map height in tiles.</summary>
        public int Height { get; }

        /// <summary>
        /// Terrain type index per tile (see <see cref="NeighbourhoodTerrain"/>), or 255 for "no
        /// terrain". Row-major: <c>y * Width + x</c>.
        /// </summary>
        public byte[] TerrainType { get; }

        /// <summary>
        /// The per-tile colour a TSO-style tint would sample. See the type remarks.
        /// </summary>
        public Color[] TerrainTypeColorData { get; }

        /// <summary>
        /// Height per tile in <c>CityMapData.ElevationData</c> units (1/12 of a tile per unit,
        /// because <c>CityGeometry</c> divides by 12). Row-major.
        /// </summary>
        public byte[] ElevationData { get; }

        /// <summary>
        /// The road nibble pair per tile: low nibble = the 4-bit neighbour mask indexing
        /// <c>CityContent.RoadLayout</c>, high nibble = the same mask indexing
        /// <c>CityContent.RoadCLayout</c> on a turn or T-junction, 0 otherwise. Row-major.
        /// </summary>
        public byte[] RoadData { get; }

        /// <summary>Foliage density per tile, 0-255. Row-major.</summary>
        public byte[] ForestDensityData { get; }

        /// <summary>Foliage tint per tile. Row-major.</summary>
        public Color[] ForestTypeData { get; }

        private NeighbourhoodMap(int width, int height)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            Width = width;
            Height = height;
            var count = width * height;
            TerrainType = new byte[count];
            TerrainTypeColorData = new Color[count];
            ElevationData = new byte[count];
            RoadData = new byte[count];
            ForestDensityData = new byte[count];
            ForestTypeData = new Color[count];
        }

        /// <summary>
        /// A blank map of the given side length: grass everywhere, height 0, no roads, no
        /// foliage, and the matching terrain colours written out.
        /// </summary>
        /// <param name="size">The side length; <see cref="DefaultSize"/> for a real map.</param>
        public static NeighbourhoodMap Create(int size)
        {
            var map = new NeighbourhoodMap(size, size);
            for (int i = 0; i < map.TerrainType.Length; i++)
            {
                map.TerrainType[i] = NeighbourhoodTerrain.Grass;
                map.TerrainTypeColorData[i] = NeighbourhoodTerrain.ColorFor(NeighbourhoodTerrain.Grass);
            }
            return map;
        }

        /// <summary>The row-major array index of a tile.</summary>
        /// <param name="x">Tile x, 0-based from the west.</param>
        /// <param name="y">Tile y, 0-based from the north.</param>
        public int Index(int x, int y)
        {
            return y * Width + x;
        }

        /// <summary>
        /// Whether a tile is inside the map. Unlike <c>CityMapData.IsInBounds</c> this reads the
        /// actual dimensions, so it is correct for a test-sized map.
        /// </summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public bool IsInBounds(int x, int y)
        {
            return x >= 0 && y >= 0 && x < Width && y < Height;
        }

        /// <summary>
        /// The road mask stored in a tile's low nibble, or 0 for a tile with no road.
        /// </summary>
        /// <param name="index">A row-major tile index.</param>
        public byte RoadMaskAt(int index)
        {
            return (byte)(RoadData[index] & RoadMask.MaskBits);
        }

        /// <summary>The road-corner nibble stored in a tile's high nibble, or 0.</summary>
        /// <param name="index">A row-major tile index.</param>
        public byte RoadCornerAt(int index)
        {
            return (byte)(RoadData[index] >> 4);
        }

        /// <summary>
        /// A deep copy. M3 applies a generated map to the GPU on the game thread while the worker
        /// that produced it is gone; the immutable-map half of that boundary is a copy the
        /// renderer can keep, so this exists rather than having callers remember to clone.
        /// </summary>
        public NeighbourhoodMap Clone()
        {
            var copy = new NeighbourhoodMap(Width, Height);
            Array.Copy(TerrainType, copy.TerrainType, TerrainType.Length);
            Array.Copy(ElevationData, copy.ElevationData, ElevationData.Length);
            Array.Copy(RoadData, copy.RoadData, RoadData.Length);
            Array.Copy(ForestDensityData, copy.ForestDensityData, ForestDensityData.Length);
            Array.Copy(TerrainTypeColorData, copy.TerrainTypeColorData, TerrainTypeColorData.Length);
            Array.Copy(ForestTypeData, copy.ForestTypeData, ForestTypeData.Length);
            return copy;
        }

        /// <summary>
        /// Whether two maps hold identical bytes and colours in all six arrays. This is the
        /// determinism assertion: same seed plus same input must give a map that compares equal
        /// element by element, not merely one that looks the same.
        /// </summary>
        /// <param name="other">The map to compare with, or <c>null</c>.</param>
        public bool ContentEquals(NeighbourhoodMap other)
        {
            if (other == null) return false;
            if (other.Width != Width || other.Height != Height) return false;
            return Equal(TerrainType, other.TerrainType)
                && Equal(ElevationData, other.ElevationData)
                && Equal(RoadData, other.RoadData)
                && Equal(ForestDensityData, other.ForestDensityData)
                && EqualColors(TerrainTypeColorData, other.TerrainTypeColorData)
                && EqualColors(ForestTypeData, other.ForestTypeData);
        }

        private static bool Equal(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private static bool EqualColors(Color[] a, Color[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        /// <summary>
        /// Every array of this map as <c>(name, histogram)</c> text: how many tiles hold each
        /// distinct byte or colour. The cheap half of a debug dump - it is what a reviewer reads
        /// to see at a glance that a map has roads in it and that the roads did not eat the lots.
        /// </summary>
        public string WriteDebugSummary()
        {
            var sb = new StringBuilder();
            sb.Append("map ").Append(Width).Append('x').Append(Height)
              .Append(" (").Append(TerrainType.Length).Append(" tiles)").Append('\n');
            AppendHistogram(sb, "terrainType", TerrainType);
            AppendElevation(sb);
            AppendRoad(sb);
            AppendHistogram(sb, "forestDensity buckets", BucketForestDensity());
            AppendColorHistogram(sb, "forestType", ForestTypeData);
            return sb.ToString();
        }

        /// <summary>
        /// One tile as text, in the same field order as <see cref="WriteDebugSummary"/>. Handy
        /// when a failing assertion is about a single tile.
        /// </summary>
        /// <param name="x">Tile x.</param>
        /// <param name="y">Tile y.</param>
        public string Describe(int x, int y)
        {
            if (!IsInBounds(x, y)) return "out of bounds";
            var i = Index(x, y);
            var road = RoadData[i];
            return string.Format(
                CultureInfo.InvariantCulture,
                "({0},{1}) terrain={2} elev={3} road={4} mask=0x{5:X} corner={6} forest={7} tint={8}",
                x, y, TerrainType[i], ElevationData[i], road, road & RoadMask.MaskBits,
                road >> 4, ForestDensityData[i], ForestTypeData[i]);
        }

        /// <summary>
        /// Writes the five PNGs <c>CityMapData.Save</c> writes, under the same names, into
        /// <paramref name="directory"/> (created if missing). Pure managed I/O through <c>PngCodec</c> -
        /// no <c>Texture2D</c>, no graphics device, no game thread - so it works from a test and
        /// from a headless build server.
        ///
        /// <para>
        /// The bytes are written raw, exactly as <c>CityMapData.Save</c> writes them, so a dump
        /// from here and a dump from a real city are directly comparable. The consequence is that
        /// the elevation image is nearly black for real data: a TS1 lot's heights occupy a handful
        /// of the 0-255 range, because <c>CityGeometry</c> divides by 12. Pass
        /// <paramref name="stretchElevation"/> when the point is to look at the shape of the ground
        /// rather than to compare bytes.
        /// </para>
        /// </summary>
        /// <param name="directory">Destination folder.</param>
        /// <param name="stretchElevation">
        /// Whether to rescale the elevation image so its lowest tile is black and its highest is
        /// white. Off by default, so the default dump is byte-comparable.
        /// </param>
        public void SavePng(string directory, bool stretchElevation)
        {
            if (string.IsNullOrEmpty(directory)) throw new ArgumentNullException(nameof(directory));
            Directory.CreateDirectory(directory);

            SaveGray(Path.Combine(directory, "roadmap.png"), RoadData, 1);
            SaveGray(Path.Combine(directory, "elevation.png"), ElevationData, stretchElevation ? 255 : 0);
            SaveGray(Path.Combine(directory, "forestdensity.png"), ForestDensityData, 0);
            SaveColor(Path.Combine(directory, "foresttype.png"), ForestTypeData);
            SaveColor(Path.Combine(directory, "terraintype.png"), TerrainTypeColorData);
        }

        /// <summary>
        /// Writes the five PNGs byte-for-byte as <c>CityMapData.Save</c> would.
        /// </summary>
        /// <param name="directory">Destination folder.</param>
        public void SavePng(string directory)
        {
            SavePng(directory, false);
        }

        /// <summary>
        /// Writes one greyscale PNG. <paramref name="mode"/> is 0 for raw bytes, 1 for the road
        /// pair, and 255 for "stretch to the full range", which is what makes an elevation image
        /// worth looking at.
        /// </summary>
        private void SaveGray(string path, byte[] data, int mode)
        {
            byte lowest = 255, highest = 0;
            if (mode == 255)
            {
                for (int i = 0; i < data.Length; i++)
                {
                    if (data[i] < lowest) lowest = data[i];
                    if (data[i] > highest) highest = data[i];
                }
            }
            var span = highest - lowest;

            var pixels = new byte[Width * Height];
            for (int i = 0; i < pixels.Length; i++)
            {
                var raw = data[i];
                byte v;
                if (mode == 1)
                {
                    // The roadmap holds a nibble pair. Showing only one would hide
                    // half of it, so the road nibble is expanded across the grey
                    // channel rather than squashed into it.
                    v = (byte)((raw & RoadMask.MaskBits) * 17);
                }
                else if (mode == 255 && span > 0)
                {
                    v = (byte)(((raw - lowest) * 255) / span);
                }
                else
                {
                    v = raw;
                }
                pixels[i] = v;
            }
            PngCodec.SaveGray(path, Width, Height, pixels);
        }

        private void SaveColor(string path, Color[] data)
        {
            var pixels = new byte[Width * Height * 4];
            for (int i = 0; i < Width * Height; i++)
            {
                var c = data[i];
                pixels[i * 4] = c.R;
                pixels[i * 4 + 1] = c.G;
                pixels[i * 4 + 2] = c.B;
                pixels[i * 4 + 3] = 255;
            }
            PngCodec.SaveRgba(path, Width, Height, pixels);
        }

        private static void AppendHistogram(StringBuilder sb, string name, byte[] data)
        {
            var counts = new Dictionary<byte, int>();
            for (int i = 0; i < data.Length; i++)
            {
                int n;
                counts.TryGetValue(data[i], out n);
                counts[data[i]] = n + 1;
            }
            var keys = new List<byte>(counts.Keys);
            keys.Sort();
            sb.Append(name).Append(": ");
            for (int i = 0; i < keys.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(keys[i]).Append('=').Append(counts[keys[i]]);
            }
            sb.Append('\n');
        }

        private void AppendElevation(StringBuilder sb)
        {
            byte min = 255, max = 0;
            for (int i = 0; i < ElevationData.Length; i++)
            {
                if (ElevationData[i] < min) min = ElevationData[i];
                if (ElevationData[i] > max) max = ElevationData[i];
            }
            sb.Append("elevation: min=").Append(min).Append(" max=").Append(max)
              .Append(" (CityGeometry divides by 12)").Append('\n');
        }

        private void AppendRoad(StringBuilder sb)
        {
            int tiles = 0, corners = 0;
            var shapes = new int[16];
            for (int i = 0; i < RoadData.Length; i++)
            {
                var mask = (byte)(RoadData[i] & RoadMask.MaskBits);
                if (mask == 0) continue;
                tiles++;
                shapes[mask]++;
                if ((RoadData[i] >> 4) != 0) corners++;
            }
            sb.Append("road: tiles=").Append(tiles).Append(" cornerNibbles=").Append(corners)
              .Append(" masks=");
            for (int m = 1; m < 16; m++)
            {
                if (shapes[m] != 0) sb.Append(m).Append("x").Append(shapes[m]).Append(' ');
            }
            sb.Append('\n');
        }

        private byte[] BucketForestDensity()
        {
            var buckets = new byte[ForestDensityData.Length];
            for (int i = 0; i < buckets.Length; i++)
            {
                buckets[i] = (byte)(ForestDensityData[i] >> 4);
            }
            return buckets;
        }

        private static void AppendColorHistogram(StringBuilder sb, string name, Color[] data)
        {
            var counts = new Dictionary<Color, int>();
            for (int i = 0; i < data.Length; i++)
            {
                int n;
                counts.TryGetValue(data[i], out n);
                counts[data[i]] = n + 1;
            }
            sb.Append(name).Append(": ").Append(counts.Count).Append(" distinct");
            foreach (var pair in counts)
            {
                if (pair.Value > data.Length / 8) sb.Append(' ').Append(pair.Key).Append('=').Append(pair.Value);
            }
            sb.Append('\n');
        }
    }
}
