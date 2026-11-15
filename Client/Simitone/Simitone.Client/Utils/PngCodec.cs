using System;
using System.IO;
using System.IO.Compression;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// A decoded image: tightly packed R,G,B,A bytes, top-down, straight (non-premultiplied)
    /// alpha, <c>Pixels.Length == Width * Height * 4</c>.
    /// </summary>
    public sealed class RgbaImage
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Pixels;

        public RgbaImage(int width, int height, byte[] pixels)
        {
            Width = width;
            Height = height;
            Pixels = pixels;
        }

        /// <summary>The largest pixel count any codec here will allocate for (256 Mpx = 1 GiB RGBA).</summary>
        internal const long MaxPixels = 1L << 28;

        internal static void CheckSize(long width, long height)
        {
            if (width <= 0 || height <= 0 || width * height > MaxPixels)
                throw new InvalidDataException("image dimensions out of range");
        }
    }

    /// <summary>
    /// A small, dependency-free PNG reader and writer built on <see cref="ZLibStream"/>.
    ///
    /// <para>Reads every standard colour type and bit depth (greyscale, RGB, palette,
    /// grey+alpha, RGBA at 1-16 bits; tRNS transparency; Adam7 interlacing) to straight 8-bit
    /// RGBA. 16-bit samples are reduced the same way libpng-style decoders do,
    /// <c>(v * 255 + 32895) &gt;&gt; 16</c>. The CRC of each chunk is not verified: the zlib
    /// Adler-32 already catches a corrupt pixel stream, and a bad ancillary-chunk CRC is not a
    /// reason to refuse a texture.</para>
    ///
    /// <para>Writes 8-bit RGBA (colour type 6) or 8-bit greyscale (type 0), no interlacing.</para>
    /// </summary>
    public static class PngCodec
    {
        private static readonly byte[] Signature = { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A };

        public static bool HasSignature(byte[] b)
        {
            if (b == null || b.Length < Signature.Length) return false;
            for (int i = 0; i < Signature.Length; i++) if (b[i] != Signature[i]) return false;
            return true;
        }

        // ------------------------------------------------------------------ decode

        /// <summary>Decodes a PNG. Throws <see cref="InvalidDataException"/> on anything malformed.</summary>
        public static RgbaImage Decode(byte[] data)
        {
            if (!HasSignature(data)) throw new InvalidDataException("not a PNG");

            int width = 0, height = 0, depth = 0, colorType = -1, interlace = 0;
            byte[] palette = null;
            byte[] trns = null;
            bool haveHeader = false;
            var idat = new MemoryStream();

            int pos = 8;
            while (pos + 8 <= data.Length)
            {
                uint len = ReadU32(data, pos);
                string type = new string(new[] { (char)data[pos + 4], (char)data[pos + 5], (char)data[pos + 6], (char)data[pos + 7] });
                int body = pos + 8;
                if (len > int.MaxValue || body + (long)len > data.Length) throw new InvalidDataException("truncated chunk");
                int n = (int)len;

                if (type == "IHDR")
                {
                    if (n < 13) throw new InvalidDataException("bad IHDR");
                    long w = ReadU32(data, body), h = ReadU32(data, body + 4);
                    RgbaImage.CheckSize(w, h);
                    width = (int)w; height = (int)h;
                    depth = data[body + 8];
                    colorType = data[body + 9];
                    interlace = data[body + 12];
                    if (data[body + 10] != 0 || data[body + 11] != 0 || interlace > 1)
                        throw new InvalidDataException("unsupported PNG method");
                    if (!ValidDepth(colorType, depth)) throw new InvalidDataException("bad PNG depth/colour type");
                    haveHeader = true;
                }
                else if (!haveHeader)
                {
                    throw new InvalidDataException("IHDR missing");
                }
                else if (type == "PLTE")
                {
                    if (n % 3 != 0 || n > 768) throw new InvalidDataException("bad PLTE");
                    palette = new byte[n];
                    Buffer.BlockCopy(data, body, palette, 0, n);
                }
                else if (type == "tRNS")
                {
                    trns = new byte[n];
                    Buffer.BlockCopy(data, body, trns, 0, n);
                }
                else if (type == "IDAT")
                {
                    idat.Write(data, body, n);
                }
                else if (type == "IEND")
                {
                    break;
                }

                pos = body + n + 4; // + CRC
            }

            if (!haveHeader || idat.Length == 0) throw new InvalidDataException("no image data");
            if (colorType == 3 && palette == null) throw new InvalidDataException("palette missing");

            int channels = colorType == 0 ? 1 : colorType == 2 ? 3 : colorType == 3 ? 1 : colorType == 4 ? 2 : 4;
            int bitsPerPixel = channels * depth;
            int bpp = Math.Max(1, bitsPerPixel / 8);

            // Pass layout: (xStart, yStart, xStep, yStep), or one full-frame pass.
            int[][] passes = interlace == 0
                ? new[] { new[] { 0, 0, 1, 1 } }
                : new[]
                {
                    new[] { 0, 0, 8, 8 }, new[] { 4, 0, 8, 8 }, new[] { 0, 4, 4, 8 }, new[] { 2, 0, 4, 4 },
                    new[] { 0, 2, 2, 4 }, new[] { 1, 0, 2, 2 }, new[] { 0, 1, 1, 2 },
                };

            var rgba = new byte[width * height * 4];
            idat.Position = 0;
            using (var inflate = new ZLibStream(idat, CompressionMode.Decompress))
            {
                foreach (var pass in passes)
                {
                    int pw = (width - pass[0] + pass[2] - 1) / pass[2];
                    int ph = (height - pass[1] + pass[3] - 1) / pass[3];
                    if (pw <= 0 || ph <= 0) continue;

                    int stride = (int)(((long)pw * bitsPerPixel + 7) / 8);
                    var prev = new byte[stride];
                    var cur = new byte[stride];
                    for (int py = 0; py < ph; py++)
                    {
                        int filter = inflate.ReadByte();
                        if (filter < 0) throw new InvalidDataException("truncated PNG data");
                        ReadFully(inflate, cur, stride);
                        Unfilter(filter, cur, prev, bpp);

                        int y = pass[1] + py * pass[3];
                        for (int px = 0; px < pw; px++)
                        {
                            int x = pass[0] + px * pass[2];
                            WritePixel(rgba, (y * width + x) * 4, cur, px, colorType, depth, palette, trns);
                        }

                        var t = prev; prev = cur; cur = t;
                    }
                }
            }

            return new RgbaImage(width, height, rgba);
        }

        private static bool ValidDepth(int colorType, int depth)
        {
            switch (colorType)
            {
                case 0: return depth == 1 || depth == 2 || depth == 4 || depth == 8 || depth == 16;
                case 3: return depth == 1 || depth == 2 || depth == 4 || depth == 8;
                case 2:
                case 4:
                case 6: return depth == 8 || depth == 16;
                default: return false;
            }
        }

        private static uint ReadU32(byte[] b, int o)
            => ((uint)b[o] << 24) | ((uint)b[o + 1] << 16) | ((uint)b[o + 2] << 8) | b[o + 3];

        private static void ReadFully(Stream s, byte[] buf, int count)
        {
            int got = 0;
            while (got < count)
            {
                int r = s.Read(buf, got, count - got);
                if (r <= 0) throw new InvalidDataException("truncated PNG data");
                got += r;
            }
        }

        private static void Unfilter(int filter, byte[] cur, byte[] prev, int bpp)
        {
            switch (filter)
            {
                case 0: return;
                case 1:
                    for (int i = bpp; i < cur.Length; i++) cur[i] = (byte)(cur[i] + cur[i - bpp]);
                    return;
                case 2:
                    for (int i = 0; i < cur.Length; i++) cur[i] = (byte)(cur[i] + prev[i]);
                    return;
                case 3:
                    for (int i = 0; i < cur.Length; i++)
                    {
                        int left = i >= bpp ? cur[i - bpp] : 0;
                        cur[i] = (byte)(cur[i] + ((left + prev[i]) >> 1));
                    }
                    return;
                case 4:
                    for (int i = 0; i < cur.Length; i++)
                    {
                        int a = i >= bpp ? cur[i - bpp] : 0;
                        int b = prev[i];
                        int c = i >= bpp ? prev[i - bpp] : 0;
                        int p = a + b - c;
                        int pa = Math.Abs(p - a), pb = Math.Abs(p - b), pc = Math.Abs(p - c);
                        int pred = (pa <= pb && pa <= pc) ? a : (pb <= pc ? b : c);
                        cur[i] = (byte)(cur[i] + pred);
                    }
                    return;
                default:
                    throw new InvalidDataException("bad PNG filter");
            }
        }

        /// <summary>Reads sample <paramref name="index"/> (in units of <paramref name="depth"/> bits) from a row.</summary>
        private static int Sample(byte[] row, int index, int depth)
        {
            switch (depth)
            {
                case 8: return row[index];
                case 16: return (row[index * 2] << 8) | row[index * 2 + 1];
                default:
                    int bit = index * depth;
                    int shift = 8 - depth - (bit & 7);
                    return (row[bit >> 3] >> shift) & ((1 << depth) - 1);
            }
        }

        private static byte To8(int v, int depth)
        {
            switch (depth)
            {
                case 8: return (byte)v;
                case 16: return (byte)((v * 255 + 32895) >> 16);
                default: return (byte)(v * 255 / ((1 << depth) - 1));
            }
        }

        private static void WritePixel(byte[] dst, int o, byte[] row, int px, int colorType, int depth, byte[] palette, byte[] trns)
        {
            switch (colorType)
            {
                case 0:
                {
                    int v = Sample(row, px, depth);
                    byte g = To8(v, depth);
                    dst[o] = g; dst[o + 1] = g; dst[o + 2] = g;
                    dst[o + 3] = (trns != null && trns.Length >= 2 && v == ((trns[0] << 8) | trns[1])) ? (byte)0 : (byte)255;
                    break;
                }
                case 2:
                {
                    int r = Sample(row, px * 3, depth), g = Sample(row, px * 3 + 1, depth), b = Sample(row, px * 3 + 2, depth);
                    dst[o] = To8(r, depth); dst[o + 1] = To8(g, depth); dst[o + 2] = To8(b, depth);
                    bool keyed = trns != null && trns.Length >= 6
                        && r == ((trns[0] << 8) | trns[1]) && g == ((trns[2] << 8) | trns[3]) && b == ((trns[4] << 8) | trns[5]);
                    dst[o + 3] = keyed ? (byte)0 : (byte)255;
                    break;
                }
                case 3:
                {
                    int i = Sample(row, px, depth);
                    if (i * 3 + 2 < palette.Length)
                    {
                        dst[o] = palette[i * 3]; dst[o + 1] = palette[i * 3 + 1]; dst[o + 2] = palette[i * 3 + 2];
                    }
                    else
                    {
                        dst[o] = 0; dst[o + 1] = 0; dst[o + 2] = 0;
                    }
                    dst[o + 3] = (trns != null && i < trns.Length) ? trns[i] : (byte)255;
                    break;
                }
                case 4:
                {
                    byte g = To8(Sample(row, px * 2, depth), depth);
                    dst[o] = g; dst[o + 1] = g; dst[o + 2] = g;
                    dst[o + 3] = To8(Sample(row, px * 2 + 1, depth), depth);
                    break;
                }
                default:
                {
                    dst[o] = To8(Sample(row, px * 4, depth), depth);
                    dst[o + 1] = To8(Sample(row, px * 4 + 1, depth), depth);
                    dst[o + 2] = To8(Sample(row, px * 4 + 2, depth), depth);
                    dst[o + 3] = To8(Sample(row, px * 4 + 3, depth), depth);
                    break;
                }
            }
        }

        // ------------------------------------------------------------------ encode

        /// <summary>Encodes tightly packed R,G,B,A bytes as an 8-bit RGBA PNG.</summary>
        public static byte[] EncodeRgba(int width, int height, byte[] rgba)
            => Encode(width, height, rgba, 6, 4);

        /// <summary>Encodes one byte per pixel as an 8-bit greyscale PNG.</summary>
        public static byte[] EncodeGray(int width, int height, byte[] gray)
            => Encode(width, height, gray, 0, 1);

        private static byte[] Encode(int width, int height, byte[] pixels, int colorType, int channels)
        {
            RgbaImage.CheckSize(width, height);
            if (pixels == null || pixels.Length < (long)width * height * channels)
                throw new ArgumentException("pixel buffer is smaller than width * height * channels");

            using (var output = new MemoryStream())
            {
                output.Write(Signature, 0, Signature.Length);

                var ihdr = new byte[13];
                WriteU32(ihdr, 0, (uint)width);
                WriteU32(ihdr, 4, (uint)height);
                ihdr[8] = 8;
                ihdr[9] = (byte)colorType;
                WriteChunk(output, "IHDR", ihdr, 0, ihdr.Length);

                int stride = width * channels;
                var raw = new MemoryStream(height * (stride + 1));
                using (var z = new ZLibStream(raw, CompressionLevel.Optimal, true))
                {
                    for (int y = 0; y < height; y++)
                    {
                        z.WriteByte(0); // filter: none
                        z.Write(pixels, y * stride, stride);
                    }
                }
                var idat = raw.GetBuffer();
                WriteChunk(output, "IDAT", idat, 0, (int)raw.Length);
                WriteChunk(output, "IEND", new byte[0], 0, 0);
                return output.ToArray();
            }
        }

        /// <summary>Atomically-ish writes a PNG: encodes first, so a failure never leaves a half-written file.</summary>
        public static void SaveRgba(string path, int width, int height, byte[] rgba)
            => File.WriteAllBytes(path, EncodeRgba(width, height, rgba));

        public static void SaveGray(string path, int width, int height, byte[] gray)
            => File.WriteAllBytes(path, EncodeGray(width, height, gray));

        private static void WriteU32(byte[] b, int o, uint v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static void WriteChunk(Stream s, string type, byte[] data, int offset, int count)
        {
            var head = new byte[8];
            WriteU32(head, 0, (uint)count);
            for (int i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
            s.Write(head, 0, 8);
            s.Write(data, offset, count);

            uint crc = Crc32(0xFFFFFFFFu, head, 4, 4);
            crc = Crc32(crc, data, offset, count) ^ 0xFFFFFFFFu;
            var tail = new byte[4];
            WriteU32(tail, 0, crc);
            s.Write(tail, 0, 4);
        }

        private static readonly uint[] CrcTable = BuildCrcTable();

        private static uint[] BuildCrcTable()
        {
            var t = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                uint c = n;
                for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                t[n] = c;
            }
            return t;
        }

        private static uint Crc32(uint crc, byte[] b, int offset, int count)
        {
            for (int i = 0; i < count; i++) crc = CrcTable[(crc ^ b[offset + i]) & 0xFF] ^ (crc >> 8);
            return crc;
        }
    }
}
