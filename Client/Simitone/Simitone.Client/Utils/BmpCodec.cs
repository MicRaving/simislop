using System;
using System.IO;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// A small, dependency-free Windows BMP reader (decode only; nothing in Simitone writes BMPs).
    ///
    /// <para>Handles what TS1 and its mods ship and the standard variants around it: 1/4/8-bit
    /// palettised, 16/24/32-bit, bottom-up and top-down, <c>BI_RGB</c>, <c>BI_RLE8</c>,
    /// <c>BI_RLE4</c> and <c>BI_BITFIELDS</c>, with BITMAPCOREHEADER and the 40-byte-and-later
    /// info headers. Output is straight R,G,B,A; images without alpha come out opaque.
    /// Pixels an RLE stream skips over are opaque black, which is what the previous decoder
    /// produced. Throws <see cref="InvalidDataException"/> on anything malformed or truncated.</para>
    /// </summary>
    public static class BmpCodec
    {
        public static bool HasSignature(byte[] b) => b != null && b.Length >= 2 && b[0] == (byte)'B' && b[1] == (byte)'M';

        public static RgbaImage Decode(byte[] b)
        {
            if (!HasSignature(b) || b.Length < 26) throw new InvalidDataException("not a BMP");

            int dataOffset = BitConverter.ToInt32(b, 10);
            int headerSize = BitConverter.ToInt32(b, 14);
            if (headerSize < 12 || headerSize > b.Length - 14) throw new InvalidDataException("bad BMP header");

            int width, height, bpp, compression = 0, clrUsed = 0, paletteEntry;
            if (headerSize == 12)
            {
                width = BitConverter.ToUInt16(b, 18);
                height = BitConverter.ToUInt16(b, 20);
                bpp = BitConverter.ToUInt16(b, 24);
                paletteEntry = 3;
            }
            else
            {
                if (headerSize < 40 || b.Length < 54) throw new InvalidDataException("bad BMP header");
                width = BitConverter.ToInt32(b, 18);
                height = BitConverter.ToInt32(b, 22);
                bpp = BitConverter.ToUInt16(b, 28);
                compression = BitConverter.ToInt32(b, 30);
                clrUsed = BitConverter.ToInt32(b, 46);
                paletteEntry = 4;
            }

            bool topDown = height < 0;
            if (height == int.MinValue) throw new InvalidDataException("bad BMP height");
            if (topDown) height = -height;
            RgbaImage.CheckSize(width, height);

            if (bpp != 1 && bpp != 4 && bpp != 8 && bpp != 16 && bpp != 24 && bpp != 32)
                throw new InvalidDataException("unsupported BMP depth");
            if ((compression == 1 && bpp != 8) || (compression == 2 && bpp != 4)
                || compression == 3 && bpp != 16 && bpp != 32
                || compression == 6 && bpp != 16 && bpp != 32
                || compression < 0 || compression > 6 || compression == 4 || compression == 5)
                throw new InvalidDataException("unsupported BMP compression");
            if (dataOffset < 0 || dataOffset > b.Length) throw new InvalidDataException("bad BMP data offset");

            // ---- palette (<= 8 bpp)
            byte[] palette = null; // R,G,B per entry
            int paletteCount = 0;
            if (bpp <= 8)
            {
                int maxColors = 1 << bpp;
                paletteCount = clrUsed > 0 ? Math.Min(clrUsed, maxColors) : maxColors;
                int start = 14 + headerSize;
                palette = new byte[paletteCount * 3];
                for (int i = 0; i < paletteCount; i++)
                {
                    int o = start + i * paletteEntry;
                    if (o + paletteEntry > b.Length) throw new InvalidDataException("truncated BMP palette");
                    palette[i * 3] = b[o + 2];
                    palette[i * 3 + 1] = b[o + 1];
                    palette[i * 3 + 2] = b[o];
                }
            }

            var rgba = new byte[width * height * 4];

            if (compression == 1 || compression == 2)
            {
                DecodeRle(b, dataOffset, width, height, topDown, compression == 2, palette, paletteCount, rgba);
                return new RgbaImage(width, height, rgba);
            }

            long stride = (((long)width * bpp + 31) >> 5) << 2;
            if (dataOffset + stride * height > b.Length) throw new InvalidDataException("truncated BMP pixel data");

            // ---- 16/32-bit masks
            uint rMask = 0, gMask = 0, bMask = 0, aMask = 0;
            if (bpp == 16 || bpp == 32)
            {
                if (compression == 3 || compression == 6)
                {
                    const int m = 54; // masks always start right after the 40-byte info header
                    if (m + 12 > b.Length) throw new InvalidDataException("truncated BMP masks");
                    rMask = BitConverter.ToUInt32(b, m);
                    gMask = BitConverter.ToUInt32(b, m + 4);
                    bMask = BitConverter.ToUInt32(b, m + 8);
                    if (headerSize >= 56 && m + 16 <= b.Length) aMask = BitConverter.ToUInt32(b, m + 12);
                    else if (compression == 6 && m + 16 <= b.Length) aMask = BitConverter.ToUInt32(b, m + 12);
                }
                else if (bpp == 16)
                {
                    rMask = 0x7C00; gMask = 0x03E0; bMask = 0x001F; // 5-5-5
                }
                else
                {
                    rMask = 0x00FF0000; gMask = 0x0000FF00; bMask = 0x000000FF;
                    if (bpp == 32 && compression == 0) aMask = 0xFF000000; // the previous decoder honoured the fourth byte
                }
            }

            for (int row = 0; row < height; row++)
            {
                int srcRow = topDown ? row : height - 1 - row;
                int rs = (int)(dataOffset + stride * srcRow);
                int dst = row * width * 4;
                for (int x = 0; x < width; x++, dst += 4)
                {
                    switch (bpp)
                    {
                        case 1:
                        case 4:
                        case 8:
                        {
                            int idx;
                            if (bpp == 8) idx = b[rs + x];
                            else if (bpp == 4) idx = (b[rs + (x >> 1)] >> ((x & 1) == 0 ? 4 : 0)) & 0xF;
                            else idx = (b[rs + (x >> 3)] >> (7 - (x & 7))) & 1;
                            SetPalette(rgba, dst, palette, paletteCount, idx);
                            break;
                        }
                        case 24:
                        {
                            int o = rs + x * 3;
                            rgba[dst] = b[o + 2]; rgba[dst + 1] = b[o + 1]; rgba[dst + 2] = b[o]; rgba[dst + 3] = 255;
                            break;
                        }
                        case 16:
                        {
                            uint v = BitConverter.ToUInt16(b, rs + x * 2);
                            Masked(rgba, dst, v, rMask, gMask, bMask, aMask);
                            break;
                        }
                        default: // 32
                        {
                            uint v = BitConverter.ToUInt32(b, rs + x * 4);
                            Masked(rgba, dst, v, rMask, gMask, bMask, aMask);
                            break;
                        }
                    }
                }
            }

            return new RgbaImage(width, height, rgba);
        }

        private static void SetPalette(byte[] dst, int o, byte[] palette, int count, int idx)
        {
            if (idx < count)
            {
                dst[o] = palette[idx * 3]; dst[o + 1] = palette[idx * 3 + 1]; dst[o + 2] = palette[idx * 3 + 2];
            }
            else
            {
                dst[o] = 0; dst[o + 1] = 0; dst[o + 2] = 0;
            }
            dst[o + 3] = 255;
        }

        private static void Masked(byte[] dst, int o, uint v, uint r, uint g, uint b, uint a)
        {
            dst[o] = Channel(v, r, 255);
            dst[o + 1] = Channel(v, g, 255);
            dst[o + 2] = Channel(v, b, 255);
            dst[o + 3] = Channel(v, a, 255);
        }

        /// <summary>Extracts a masked channel and scales it to 0-255; an empty mask yields <paramref name="whenAbsent"/>.</summary>
        private static byte Channel(uint v, uint mask, byte whenAbsent)
        {
            if (mask == 0) return whenAbsent;
            int shift = 0;
            while (((mask >> shift) & 1) == 0) shift++;
            uint max = mask >> shift;
            uint val = (v & mask) >> shift;
            // Bit replication (0b11111 -> 0xFF), which is what the previous decoder produced.
            int bits = 0;
            while ((max >> bits) != 0) bits++;
            if (bits >= 8) return (byte)(val >> (bits - 8));
            uint scaled = val << (8 - bits);
            uint result = scaled;
            for (int s = bits; s < 8; s += bits) result |= scaled >> s;
            return (byte)result;
        }

        private static void DecodeRle(byte[] b, int pos, int width, int height, bool topDown, bool rle4,
            byte[] palette, int paletteCount, byte[] rgba)
        {
            // Skipped pixels stay opaque black.
            for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 255;

            int x = 0, y = 0; // y counts rows in file order (bottom row first unless top-down)
            while (pos + 1 < b.Length)
            {
                int count = b[pos++];
                int value = b[pos++];
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        int idx = rle4 ? ((i & 1) == 0 ? value >> 4 : value & 0xF) : value;
                        Put(rgba, width, height, topDown, x++, y, palette, paletteCount, idx);
                    }
                    continue;
                }

                if (value == 0) { x = 0; y++; }
                else if (value == 1) { return; }
                else if (value == 2)
                {
                    if (pos + 1 >= b.Length) throw new InvalidDataException("truncated BMP RLE data");
                    x += b[pos++];
                    y += b[pos++];
                }
                else
                {
                    int bytes = rle4 ? (value + 1) / 2 : value;
                    if (pos + bytes > b.Length) throw new InvalidDataException("truncated BMP RLE data");
                    for (int i = 0; i < value; i++)
                    {
                        int idx = rle4
                            ? ((i & 1) == 0 ? b[pos + (i >> 1)] >> 4 : b[pos + (i >> 1)] & 0xF)
                            : b[pos + i];
                        Put(rgba, width, height, topDown, x++, y, palette, paletteCount, idx);
                    }
                    pos += bytes + (bytes & 1);
                }
            }
        }

        private static void Put(byte[] rgba, int width, int height, bool topDown, int x, int y,
            byte[] palette, int paletteCount, int idx)
        {
            if (x < 0 || x >= width || y < 0 || y >= height) return;
            int row = topDown ? y : height - 1 - y;
            SetPalette(rgba, (row * width + x) * 4, palette, paletteCount, idx);
        }
    }
}
