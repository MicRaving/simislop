using System;
using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// Decodes a BMP/PNG stream into the tightly packed RGBA byte buffer that
    /// FreeSO's <c>ImageLoaderHelpers.BitmapFunction</c> hook expects.
    ///
    /// This replaces a <c>System.Drawing</c> (GDI+) implementation. GDI+ is Windows-only and
    /// unsupported on modern .NET for anything but a subset of scenarios; ImageSharp is managed,
    /// cross-platform, and maintained. It also removes Simitone's last hard dependency on the
    /// legacy drawing stack, which is what allowed the hook to be moved into
    /// <c>Simitone.Client</c> (and therefore unit-tested) instead of living inline in the WinForms
    /// entry point.
    ///
    /// <para><b>The buffer contract is not interchangeable with MonoGame's.</b> The returned bytes
    /// are <b>R, G, B, A</b> per pixel, in that order, top-down, with <i>no</i> row padding:
    /// <c>data.Length == width * height * 4</c>. That is a different layout from
    /// <c>Microsoft.Xna.Framework.Graphics.Color</c> / <c>Microsoft.Xna.Framework.Color</c>'s
    /// in-memory form only in name - the struct really is R,G,B,A - but it is a different layout
    /// from the <c>Texture2D.SetData(byte[])</c> expectations and from GDI+'s
    /// <c>Format32bppArgb</c> (which is BGRA in memory).</para>
    ///
    /// <para>Why the order matters, concretely - these are the consumers that would silently
    /// render wrong rather than throw if the order or the alpha mode drifted:</para>
    /// <list type="bullet">
    /// <item><description><c>ImageLoader.GetColorData</c> blits the buffer straight over a
    /// <c>Color[]</c> (<c>Marshal.Copy</c>), which only yields correct colours because both agree
    /// on R-first.</description></item>
    /// <item><description><c>ImageLoader.ManualTextureMaskData</c> reads <c>buffer[i]</c> as the red
    /// channel and <c>buffer[i + 2]</c> as blue to detect the "magic pink" mask used by TS1
    /// neighbourhood/lot thumbnails. Swapping the channels would make it mask nothing (or mask
    /// real content) instead of raising an error.</description></item>
    /// <item><description><c>ImageLoader.Premultiply</c> scales <c>buffer[i..i+2]</c> by
    /// <c>buffer[i + 3]</c>, which is only correct for straight (non-premultiplied) alpha. The
    /// hook must therefore hand over <i>unpremultiplied</i> colour values: the caller - not this
    /// method - decides whether the texture wants premultiplication (see <c>PremultiplyPNG</c> and
    /// the <c>premult</c> argument threaded through <c>WinDataFromStreamP</c>).</description></item>
    /// </list>
    ///
    /// <para>The old GDI+ implementation applied a colour matrix to swap R and B and then asked for
    /// <c>Format32bppArgb</c>; the two swaps cancelled out and the net buffer that FreeSO actually
    /// relies on was R,G,B,A. This implementation therefore does no channel swapping at all: it
    /// asks ImageSharp for <see cref="Rgba32"/> and copies the pixels out verbatim.</para>
    /// </summary>
    public static class BitmapDecoder
    {
        /// <summary>
        /// Decodes <paramref name="stream"/> into a tightly packed R,G,B,A buffer.
        /// </summary>
        /// <param name="stream">
        /// A readable, positioned image stream. The caller owns the stream: it is read from its
        /// current position and is neither rewound nor disposed here. FreeSO seeks to 0 before
        /// invoking the hook and keeps using the stream afterwards, so moving the position or
        /// closing it out from under the caller would corrupt the surrounding load.
        /// </param>
        /// <returns>
        /// A tuple of (data, width, height) where <c>data.Length == width * height * 4</c> and
        /// bytes are ordered R, G, B, A per pixel with straight (non-premultiplied) alpha; or
        /// <c>null</c> if the stream could not be decoded.
        /// </returns>
        /// <remarks>
        /// A malformed or unrecognised image returns <c>null</c> instead of throwing. The callers
        /// in <c>ImageLoader.WinDataFromStreamP</c> treat <c>null</c> as "not an image" and fall
        /// through to their own error handling, and some of them do so from inside a catch block,
        /// so a decoder that threw would either be swallowed into a wrong answer or escape as a
        /// startup crash. Indexed/palettized, 24bpp and 32bpp BMPs and PNG all decode
        /// through the same path - ImageSharp normalises the container and the caller sees RGBA.
        /// Only BMP and PNG decode (see <c>DecoderOptionsBmpPng</c>); any other format, TIFF
        /// included, returns <c>null</c>.
        /// </remarks>
        public static Tuple<byte[], int, int> Read(Stream stream)
        {
            if (stream == null) return null;

            try
            {
                // Buffer the stream so the BMP header fix-up below can rewrite the colour-table
                // offset before ImageSharp parses it. ImageSharp reads the whole stream anyway, so
                // this does not change how much of the caller's stream is consumed.
                var bytes = ReadToEnd(stream);
                NormalizeBmpColorTableOffset(bytes);

                using (var ms = new MemoryStream(bytes, false))
                using (Image<Rgba32> image = Image.Load<Rgba32>(DecoderOptionsBmpPng, ms))
                {
                    // Rgba32 is exactly 4 bytes per pixel, so the buffer is tightly packed and
                    // matches FreeSO's "no row padding" assumption without any stride fixup.
                    var data = new byte[image.Width * image.Height * 4];
                    image.CopyPixelDataTo(data);
                    return new Tuple<byte[], int, int>(data, image.Width, image.Height);
                }
            }
            catch (Exception)
            {
                // Deliberately broad: ImageSharp signals a bad file as UnknownImageFormatException
                // or ImageFormatException, but a truncated/garbage stream can surface as almost
                // anything from a decoder, and a half-read file must not take the game down.
                return null;
            }
        }

        /// <summary>
        /// Only BMP and PNG are registered. ImageSharp 3.1.12 is pinned with advisories in its
        /// TIFF/ICC/HistogramEqualization paths (see Directory.Build.props); a Configuration that
        /// does not know TIFF means a TIFF stream is never parsed at all - format detection fails
        /// and <see cref="Read"/> returns null. TS1 content is BMP (cursors, thumbnails) and PNG;
        /// TGA is decoded by FreeSO before this hook is reached, and no JPEG/GIF ships.
        /// </summary>
        private static readonly DecoderOptions DecoderOptionsBmpPng = new DecoderOptions
        {
            Configuration = new Configuration(new PngConfigurationModule(), new BmpConfigurationModule())
        };

        private static byte[] ReadToEnd(Stream stream)
        {
            using (var buffer = new MemoryStream())
            {
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
        }

        /// <summary>
        /// Rewrites an under-reported BMP <c>bfOffBits</c> (the offset where pixel data starts).
        ///
        /// The TS1 cursor loader (<c>CurLoader.LoadCursor</c>) wraps a .cur's embedded bitmap in a
        /// synthetic BMP file header and always writes <c>bfOffBits = 14</c>, even for 1bpp/4bpp
        /// images that carry a colour table. GDI+ located the palette from the DIB header and
        /// ignored the field, but ImageSharp honours it, so the palette was parsed as pixel data
        /// and the file was rejected ("Bitmap does not have a valid format"). That turned every
        /// cursor into a <c>null</c> decode and crashed startup inside FreeSO's cursor manager.
        ///
        /// The offset is only raised when it is demonstrably too small for the declared DIB header
        /// and colour table, so a well-formed BMP is never touched.
        /// </summary>
        private static void NormalizeBmpColorTableOffset(byte[] b)
        {
            if (b == null || b.Length < 54) return;
            if (b[0] != (byte)'B' || b[1] != (byte)'M') return;

            int bfOffBits = BitConverter.ToInt32(b, 10);
            int biSize = BitConverter.ToInt32(b, 14);
            if (biSize < 12 || biSize > b.Length - 14) return;

            int paletteEntrySize;
            int colors;
            if (biSize == 12)
            {
                // BITMAPCOREHEADER: no clrUsed field, palette entries are 3 bytes (RGBTRIPLE).
                paletteEntrySize = 3;
                int bpp = BitConverter.ToUInt16(b, 14 + 10);
                colors = bpp <= 8 ? (1 << bpp) : 0;
            }
            else
            {
                // BITMAPINFOHEADER and later: 4-byte palette entries (RGBQUAD), optional clrUsed.
                paletteEntrySize = 4;
                int bpp = BitConverter.ToUInt16(b, 14 + 14);
                int clrUsed = BitConverter.ToInt32(b, 14 + 32);
                colors = clrUsed != 0 ? clrUsed : (bpp <= 8 ? (1 << bpp) : 0);
            }

            int expected = 14 + biSize + colors * paletteEntrySize;
            if (bfOffBits < expected && expected <= b.Length)
            {
                BitConverter.GetBytes(expected).CopyTo(b, 10);
            }
        }
    }
}
