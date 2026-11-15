using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks in the byte-level contract <see cref="BitmapDecoder"/> owes FreeSO's image pipeline.
    ///
    /// <para>
    /// <c>ImageLoaderHelpers.BitmapFunction</c> hands its result straight to three consumers
    /// that all assume the same layout and cannot report a violation:
    /// <c>GetColorData</c> blits the buffer over a <c>Color[]</c> with <c>Marshal.Copy</c>
    /// (correct only if the bytes are R-first), <c>ManualTextureMaskData</c> reads
    /// <c>buffer[i]</c> as red and <c>buffer[i+2]</c> as blue to strip the "magic pink" mask, and
    /// <c>Premultiply</c> scales <c>buffer[i..i+2]</c> by <c>buffer[i+3]</c> (correct only for
    /// straight alpha). Every one of those fails silently - a swapped channel order or a
    /// premultiplied buffer produces wrong colours on screen, not an exception - so the contract
    /// has to be asserted rather than assumed. Three distinct channel values are used so that any
    /// swap is unambiguous instead of accidentally symmetric.
    /// </para>
    ///
    /// <para>
    /// Fixtures are built in memory by a tiny BMP writer and by <see cref="PngCodec"/> (plus a
    /// hand-rolled PNG for the filter / palette / interlace cases), so the whole class is
    /// headless: no GraphicsDevice, no Content pipeline, no window. The BMP and PNG layouts the
    /// fixtures use are written out byte by byte below rather than produced by the code under
    /// test, so a decoder bug cannot cancel against an encoder bug.
    /// </para>
    /// </summary>
    public sealed class BitmapDecoderTests
    {
        private const byte R = 0x12;
        private const byte G = 0x34;
        private const byte B = 0x56;

        // ------------------------------------------------------------ fixture builders

        private static Stream SolidPng(int width, int height, byte alpha)
        {
            var rgba = new byte[width * height * 4];
            for (int i = 0; i < rgba.Length; i += 4)
            {
                rgba[i] = R; rgba[i + 1] = G; rgba[i + 2] = B; rgba[i + 3] = alpha;
            }
            return new MemoryStream(PngCodec.EncodeRgba(width, height, rgba));
        }

        /// <summary>A bottom-up BMP with a 40-byte header. <paramref name="paletteBgr0"/> is RGBQUAD order (B,G,R,0).</summary>
        private static byte[] Bmp(int width, int height, int bpp, byte[] paletteBgr0, byte[] pixelData,
            int compression = 0, int bfOffBits = -1, int clrUsed = 0)
        {
            int palette = paletteBgr0?.Length ?? 0;
            int offset = 14 + 40 + palette;
            var b = new byte[offset + pixelData.Length];
            b[0] = (byte)'B'; b[1] = (byte)'M';
            BitConverter.GetBytes(b.Length).CopyTo(b, 2);
            BitConverter.GetBytes(bfOffBits >= 0 ? bfOffBits : offset).CopyTo(b, 10);
            BitConverter.GetBytes(40).CopyTo(b, 14);
            BitConverter.GetBytes(width).CopyTo(b, 18);
            BitConverter.GetBytes(height).CopyTo(b, 22);
            BitConverter.GetBytes((short)1).CopyTo(b, 26);
            BitConverter.GetBytes((short)bpp).CopyTo(b, 28);
            BitConverter.GetBytes(compression).CopyTo(b, 30);
            BitConverter.GetBytes(pixelData.Length).CopyTo(b, 34);
            BitConverter.GetBytes(clrUsed).CopyTo(b, 46);
            if (palette > 0) paletteBgr0.CopyTo(b, 54);
            pixelData.CopyTo(b, offset);
            return b;
        }

        /// <summary>A 24bpp BMP of one solid colour (rows padded to 4 bytes).</summary>
        private static byte[] SolidBmp24(int width, int height)
        {
            int stride = (width * 3 + 3) & ~3;
            var data = new byte[stride * height];
            for (int y = 0; y < height; y++)
                for (int x = 0; x < width; x++)
                {
                    data[y * stride + x * 3] = B; data[y * stride + x * 3 + 1] = G; data[y * stride + x * 3 + 2] = R;
                }
            return Bmp(width, height, 24, null, data);
        }

        private static void AssertPixel(byte[] rgba, int index, byte r, byte g, byte b, byte a = 0xFF)
        {
            Assert.Equal(new[] { r, g, b, a }, new[] { rgba[index * 4], rgba[index * 4 + 1], rgba[index * 4 + 2], rgba[index * 4 + 3] });
        }

        /// <summary>Wraps raw (already filtered, filter byte included) scanlines into a PNG file.</summary>
        private static byte[] RawPng(int width, int height, int depth, int colorType, int interlace,
            byte[] scanlines, byte[] plte = null, byte[] trns = null)
        {
            var ms = new MemoryStream();
            ms.Write(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A }, 0, 8);
            var ihdr = new byte[13];
            WriteBE(ihdr, 0, width); WriteBE(ihdr, 4, height);
            ihdr[8] = (byte)depth; ihdr[9] = (byte)colorType; ihdr[12] = (byte)interlace;
            Chunk(ms, "IHDR", ihdr);
            if (plte != null) Chunk(ms, "PLTE", plte);
            if (trns != null) Chunk(ms, "tRNS", trns);
            var z = new MemoryStream();
            using (var zs = new ZLibStream(z, CompressionLevel.Fastest, true)) zs.Write(scanlines, 0, scanlines.Length);
            Chunk(ms, "IDAT", z.ToArray());
            Chunk(ms, "IEND", new byte[0]);
            return ms.ToArray();
        }

        private static void WriteBE(byte[] b, int o, int v)
        {
            b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16); b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
        }

        private static void Chunk(Stream s, string type, byte[] data)
        {
            var len = new byte[4]; WriteBE(len, 0, data.Length);
            s.Write(len, 0, 4);
            foreach (char c in type) s.WriteByte((byte)c);
            s.Write(data, 0, data.Length);
            s.Write(new byte[4], 0, 4); // CRC is not verified by the reader
        }

        // ------------------------------------------------------------ contract

        [Fact]
        public void SolidColourDecodesAsRedGreenBlueAlphaInThatOrder()
        {
            using (Stream png = SolidPng(3, 2, 0xFF))
            {
                var result = BitmapDecoder.Read(png);

                Assert.NotNull(result);
                Assert.Equal(3, result.Item2);
                Assert.Equal(2, result.Item3);

                var data = result.Item1;
                Assert.Equal(3 * 2 * 4, data.Length);
                for (int i = 0; i < 6; i++) AssertPixel(data, i, R, G, B);
            }
        }

        [Fact]
        public void NonSquareImagesReportTheirOwnWidthAndHeight()
        {
            // Lopsided in both directions, so a stride/row-padding mistake cannot hide.
            const int w = 7;
            const int h = 3;

            using (Stream png = SolidPng(w, h, 0xFF))
            {
                var result = BitmapDecoder.Read(png);

                Assert.NotNull(result);
                Assert.Equal(w, result.Item2);
                Assert.Equal(h, result.Item3);
                Assert.Equal(w * h * 4, result.Item1.Length);
            }
        }

        [Theory]
        [InlineData(0x00)]
        [InlineData(0x80)]
        public void AlphaIsStraightNotPremultiplied(byte alpha)
        {
            using (Stream png = SolidPng(2, 2, alpha))
            {
                var result = BitmapDecoder.Read(png);

                Assert.NotNull(result);
                // A premultiplying decoder would have scaled the RGB by alpha/255 and FreeSO's own
                // Premultiply would then darken the texture a second time.
                for (int i = 0; i < 4; i++) AssertPixel(result.Item1, i, R, G, B, alpha);
            }
        }

        [Fact]
        public void PngAndBmpEncodingsProduceIdenticalBuffers()
        {
            using (Stream png = SolidPng(4, 4, 0xFF))
            using (Stream bmp = new MemoryStream(SolidBmp24(4, 4)))
            {
                var fromPng = BitmapDecoder.Read(png);
                var fromBmp = BitmapDecoder.Read(bmp);

                Assert.NotNull(fromPng);
                Assert.NotNull(fromBmp);
                Assert.Equal(fromPng.Item2, fromBmp.Item2);
                Assert.Equal(fromPng.Item3, fromBmp.Item3);
                Assert.Equal(fromPng.Item1, fromBmp.Item1);
            }
        }

        [Fact]
        public void TwentyFourBitBitmapsDecodeToTheSameBuffer()
        {
            // A 24bpp BMP has no alpha channel at all. It must still come back as an opaque
            // 4-byte-per-pixel buffer rather than 3 bytes per pixel or a short read. Width 5 makes
            // the rows need padding (15 -> 16 bytes).
            var result = BitmapDecoder.Read(new MemoryStream(SolidBmp24(5, 2)));

            Assert.NotNull(result);
            Assert.Equal(5, result.Item2);
            Assert.Equal(2, result.Item3);
            Assert.Equal(5 * 2 * 4, result.Item1.Length);
            for (int i = 0; i < 10; i++) AssertPixel(result.Item1, i, R, G, B);
        }

        [Fact]
        public void BmpRowsAreFlippedToTopDown()
        {
            // 2x2 24bpp, bottom-up in the file: bottom row (red, green) first, then top row (blue, white).
            var data = new byte[]
            {
                0, 0, 255,   0, 255, 0,   0, 0,        // bottom: red, green (+2 pad)
                255, 0, 0,   255, 255, 255, 0, 0,       // top: blue, white
            };
            var result = BitmapDecoder.Read(new MemoryStream(Bmp(2, 2, 24, null, data)));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 0, 0, 255);
            AssertPixel(result.Item1, 1, 255, 255, 255);
            AssertPixel(result.Item1, 2, 255, 0, 0);
            AssertPixel(result.Item1, 3, 0, 255, 0);
        }

        [Fact]
        public void EightBitPalettisedBitmapsUseTheColourTable()
        {
            // 4x1, palette of three colours, clrUsed = 3; rows pad to 4 bytes.
            var palette = new byte[] { 0, 0, 255, 0, /* red */ 0, 255, 0, 0, /* green */ 255, 0, 0, 0 /* blue */ };
            var result = BitmapDecoder.Read(new MemoryStream(Bmp(4, 1, 8, palette, new byte[] { 2, 1, 0, 2 }, clrUsed: 3)));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 0, 0, 255);
            AssertPixel(result.Item1, 1, 0, 255, 0);
            AssertPixel(result.Item1, 2, 255, 0, 0);
            AssertPixel(result.Item1, 3, 0, 0, 255);
        }

        [Fact]
        public void FourBitPalettisedBitmapsDecodeHighNibbleFirst()
        {
            var palette = new byte[16 * 4];
            palette[4 * 1 + 2] = 255; // index 1 = red
            palette[4 * 2 + 1] = 255; // index 2 = green
            // 3x1: indices 1,2,1 -> bytes 0x12, 0x10 (+2 pad)
            var result = BitmapDecoder.Read(new MemoryStream(Bmp(3, 1, 4, palette, new byte[] { 0x12, 0x10, 0, 0 })));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 255, 0, 0);
            AssertPixel(result.Item1, 1, 0, 255, 0);
            AssertPixel(result.Item1, 2, 255, 0, 0);
        }

        [Fact]
        public void ThirtyTwoBitBitmapsKeepTheirAlphaByte()
        {
            // 1x1 B,G,R,A with alpha 0x40: the game's own Icon.bmp relies on this.
            var result = BitmapDecoder.Read(new MemoryStream(Bmp(1, 1, 32, null, new byte[] { B, G, R, 0x40 })));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, R, G, B, 0x40);
        }

        [Fact]
        public void SixteenBitBitmapsExpandFiveBitChannels()
        {
            // 5-5-5: 0x7FFF is white, 0x001F pure blue.
            var data = new byte[] { 0xFF, 0x7F, 0x1F, 0x00 };
            var result = BitmapDecoder.Read(new MemoryStream(Bmp(2, 1, 16, null, data)));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 255, 255, 255);
            AssertPixel(result.Item1, 1, 0, 0, 255);
        }

        [Fact]
        public void RunLengthEncodedEightBitBitmapsDecode()
        {
            // 4x2, RLE8. File order is bottom row first:
            // [4 x idx1][EOL][1 x idx0][absolute run of 3: 0,1,0 + pad byte][EOL][EOF]
            var palette = new byte[] { 0, 0, 0, 0, 0, 0, 255, 0 }; // 0 = black, 1 = red
            var rle = new byte[] { 4, 1, 0, 0, 1, 0, 0, 3, 0, 1, 0, 0, 0, 0, 0, 1 };
            var result = BitmapDecoder.Read(new MemoryStream(Bmp(4, 2, 8, palette, rle, compression: 1, clrUsed: 2)));

            Assert.NotNull(result);
            // top row (second in the stream): black, black, red, black
            AssertPixel(result.Item1, 0, 0, 0, 0);
            AssertPixel(result.Item1, 1, 0, 0, 0);
            AssertPixel(result.Item1, 2, 255, 0, 0);
            AssertPixel(result.Item1, 3, 0, 0, 0);
            // bottom row: four reds
            for (int x = 0; x < 4; x++) AssertPixel(result.Item1, 4 + x, 255, 0, 0);
        }

        [Fact]
        public void ARunLengthStreamThatEndsWithoutAnEndOfLineStillWritesItsLastRow()
        {
            // The old decoder (ImageSharp) left the final row black when the stream ended with
            // EOF instead of EOL; one shipped mod BMP (easelpaintbrush.bmp) does exactly that.
            var palette = new byte[] { 0, 0, 0, 0, 0, 0, 255, 0 };
            var rle = new byte[] { 2, 1, 0, 1 }; // one row: two reds, EOF
            var result = BitmapDecoder.Read(new MemoryStream(Bmp(2, 1, 8, palette, rle, compression: 1, clrUsed: 2)));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 255, 0, 0);
            AssertPixel(result.Item1, 1, 255, 0, 0);
        }

        // ------------------------------------------------------------ PNG variants

        [Fact]
        public void PngFiltersAreUndone()
        {
            // 2x2 RGB. Row 0 uses Sub (filter 1), row 1 uses Paeth (filter 4); the expected values
            // are worked out by hand so the filters are verified, not just exercised.
            //   row0 pixels: (10,20,30) (15,25,35)  -> Sub stores 10,20,30, 5,5,5
            //   row1 pixels: (12,22,32) (17,27,37)  -> Paeth predicts from left/up/up-left
            // Row 1, px0: a=0, b=row0 px0, c=0 -> Paeth picks b, stored 12-10, 22-20, 32-30 = 2.
            // Row 1, px1 (red): a=12, b=15, c=10 -> p=17, pa=5, pb=2, pc=7 -> picks b=15, stored 2.
            var scan = new byte[]
            {
                1, 10, 20, 30, 5, 5, 5,
                4, 2, 2, 2, 2, 2, 2,
            };
            var result = BitmapDecoder.Read(new MemoryStream(RawPng(2, 2, 8, 2, 0, scan)));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 10, 20, 30);
            AssertPixel(result.Item1, 1, 15, 25, 35);
            AssertPixel(result.Item1, 2, 12, 22, 32);
            AssertPixel(result.Item1, 3, 17, 27, 37);
        }

        [Fact]
        public void PalettisedPngsApplyTheTransparencyChunk()
        {
            // 2-bit palette, 4x1: indices 0,1,2,3 packed into one byte 0b00_01_10_11 = 0x1B.
            var plte = new byte[] { 255, 0, 0, 0, 255, 0, 0, 0, 255, 9, 9, 9 };
            var trns = new byte[] { 0, 128 }; // index 0 fully transparent, 1 half; the rest opaque
            var result = BitmapDecoder.Read(new MemoryStream(RawPng(4, 1, 2, 3, 0, new byte[] { 0, 0x1B }, plte, trns)));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 255, 0, 0, 0);
            AssertPixel(result.Item1, 1, 0, 255, 0, 128);
            AssertPixel(result.Item1, 2, 0, 0, 255, 255);
            AssertPixel(result.Item1, 3, 9, 9, 9, 255);
        }

        [Fact]
        public void GreyscalePngsExpandToOpaqueRgb()
        {
            // 1-bit grey, 3x1, bits 1,0,1 -> 0b101_00000
            var result = BitmapDecoder.Read(new MemoryStream(RawPng(3, 1, 1, 0, 0, new byte[] { 0, 0xA0 })));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 255, 255, 255);
            AssertPixel(result.Item1, 1, 0, 0, 0);
            AssertPixel(result.Item1, 2, 255, 255, 255);
        }

        [Fact]
        public void SixteenBitPngsAreReducedToEightBits()
        {
            // 1x1 RGBA16: (0xFFFF, 0x8000, 0x0000, 0xFFFF)
            var scan = new byte[] { 0, 0xFF, 0xFF, 0x80, 0x00, 0x00, 0x00, 0xFF, 0xFF };
            var result = BitmapDecoder.Read(new MemoryStream(RawPng(1, 1, 16, 6, 0, scan)));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 255, 128, 0, 255);
        }

        [Fact]
        public void AdamSevenInterlacedPngsAreReassembled()
        {
            // 2x2 8-bit grey. Adam7 passes that exist at this size: pass1 (0,0), pass6 (1,0), pass7 (0,1),(1,1).
            //   pass 1: pixel (0,0)=10        pass 6: x=1,y=0 -> pixel (1,0)=20
            //   pass 7: row y=1: pixels (0,1)=30, (1,1)=40
            var scan = new byte[]
            {
                0, 10,        // pass 1
                0, 20,        // pass 6 (4-wide columns step 2: only x=1)
                0, 30, 40,    // pass 7
            };
            var result = BitmapDecoder.Read(new MemoryStream(RawPng(2, 2, 8, 0, 1, scan)));

            Assert.NotNull(result);
            AssertPixel(result.Item1, 0, 10, 10, 10);
            AssertPixel(result.Item1, 1, 20, 20, 20);
            AssertPixel(result.Item1, 2, 30, 30, 30);
            AssertPixel(result.Item1, 3, 40, 40, 40);
        }

        [Fact]
        public void PngCodecRoundTripsRgba()
        {
            var rgba = new byte[5 * 3 * 4];
            for (int i = 0; i < rgba.Length; i++) rgba[i] = (byte)(i * 7 + 3);

            var decoded = PngCodec.Decode(PngCodec.EncodeRgba(5, 3, rgba));

            Assert.Equal(5, decoded.Width);
            Assert.Equal(3, decoded.Height);
            Assert.Equal(rgba, decoded.Pixels);
        }

        [Fact]
        public void PngCodecWritesGreyscaleThatDecodesToOpaqueGrey()
        {
            var decoded = PngCodec.Decode(PngCodec.EncodeGray(2, 1, new byte[] { 7, 200 }));

            AssertPixel(decoded.Pixels, 0, 7, 7, 7);
            AssertPixel(decoded.Pixels, 1, 200, 200, 200);
        }

        // ------------------------------------------------------------ rejection

        [Fact]
        public void TiffAndOtherUnregisteredFormatsAreRejected()
        {
            // Magic bytes only: none of these may reach a parser, which is what makes the decoder's
            // attack surface BMP and PNG alone. Padding keeps them from being rejected for length.
            var tiffLittle = new byte[] { (byte)'I', (byte)'I', 42, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
            var tiffBig = new byte[] { (byte)'M', (byte)'M', 0, 42, 0, 0, 0, 8, 0, 0, 0, 0, 0, 0, 0, 0 };
            var bigTiff = new byte[] { (byte)'I', (byte)'I', 43, 0, 8, 0, 0, 0, 16, 0, 0, 0, 0, 0, 0, 0 };
            var gif = System.Text.Encoding.ASCII.GetBytes("GIF89a\x01\x00\x01\x00\x00\x00\x00;");
            var jpeg = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 0, 16, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0, 1, 1, 0, 0, 1, 0, 1, 0, 0 };
            var tga = new byte[18 + 4];

            foreach (var bytes in new[] { tiffLittle, tiffBig, bigTiff, gif, jpeg, tga })
            {
                Assert.Null(BitmapDecoder.Read(new MemoryStream(bytes)));
            }
        }

        [Fact]
        public void TruncatedAndCorruptImagesReturnNull()
        {
            var png = ((MemoryStream)SolidPng(8, 8, 0xFF)).ToArray();
            var bmp = SolidBmp24(8, 8);

            Assert.Null(BitmapDecoder.Read(new MemoryStream(png, 0, png.Length / 2)));
            Assert.Null(BitmapDecoder.Read(new MemoryStream(bmp, 0, bmp.Length - 10)));
            Assert.Null(BitmapDecoder.Read(new MemoryStream(bmp, 0, 30)));
        }

        [Fact]
        public void AnAbsurdlyLargeHeaderIsRejectedWithoutAllocating()
        {
            var bmp = SolidBmp24(2, 2);
            BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bmp, 18); // width
            BitConverter.GetBytes(0x7FFFFFF0).CopyTo(bmp, 22); // height

            Assert.Null(BitmapDecoder.Read(new MemoryStream(bmp)));
        }

        [Fact]
        public void AnEmptyStreamReturnsNullInsteadOfThrowing()
        {
            using (var empty = new MemoryStream())
            {
                Assert.Null(BitmapDecoder.Read(empty));
            }
        }

        [Fact]
        public void GarbageBytesReturnNullInsteadOfThrowing()
        {
            using (var garbage = new MemoryStream(new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 }))
            {
                // ImageLoader.WinDataFromStreamP already wraps the hook in a try/catch, but the
                // contract is null-on-failure, and a decoder that throws would either be reported
                // as a fatal error or converted into a 1x1 placeholder by one of the two branches.
                Assert.Null(BitmapDecoder.Read(garbage));
            }
        }

        [Fact]
        public void ANullStreamReturnsNullInsteadOfThrowing()
        {
            Assert.Null(BitmapDecoder.Read(null));
        }

        [Fact]
        public void TheCallersStreamIsNeitherDisposedNorConsumed()
        {
            // FreeSO keeps using the stream after the hook returns, so the decoder must leave it
            // open. It is allowed to have been read from - the contract is "not rewound, not
            // disposed" - which is what this asserts.
            using (Stream png = SolidPng(2, 2, 0xFF))
            {
                BitmapDecoder.Read(png);

                Assert.True(png.CanRead, "the decoder must not dispose the caller's stream");
                png.Position = 0;
                Assert.True(png.Length > 0);
            }
        }

        [Fact]
        public void TheDecodedBufferIsExactlyWhatFreeSosMagicPinkMaskExpects()
        {
            // "Magic pink" is the colour FreeSO treats as an empty mask pixel: high red, low green,
            // high blue. ManualTextureMaskData reads buffer[i] as red and buffer[i+2] as blue, so
            // this is the one concrete colour whose channel order can be verified against a real
            // FreeSO predicate rather than against our own encoder.
            using (var png = new MemoryStream(PngCodec.EncodeRgba(1, 1, new byte[] { 0xFF, 0x00, 0xFF, 0xFF })))
            {
                var result = BitmapDecoder.Read(png);

                Assert.NotNull(result);
                var data = result.Item1;
                Assert.True(data[0] >= 248, "buffer[i] must be the red channel");
                Assert.True(data[1] <= 4, "buffer[i+1] must be the green channel");
                Assert.True(data[2] >= 248, "buffer[i+2] must be the blue channel");
                Assert.Equal(0xFF, data[3]);
            }
        }

        // ------------------------------------------------ BMP colour-table offset repair

        [Fact]
        public void BitmapsWithAnUnderReportedDataOffsetAreRepaired()
        {
            // Reproduces the TS1 cursor shim: CurLoader builds a synthetic BMP file header and
            // always writes bfOffBits = 14, even for paletted images. GDI+ ignored the field and
            // found the palette from the DIB header; a strict reader honours it, so without the
            // repair the palette was parsed as pixels. The repair must make a
            // corrupted-but-recoverable BMP decode exactly like the correct one.
            var palette = new byte[] { 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 0 };
            var correct = Bmp(3, 1, 8, palette, new byte[] { 0, 1, 2, 0 }, clrUsed: 3);
            var corrupted = Bmp(3, 1, 8, palette, new byte[] { 0, 1, 2, 0 }, bfOffBits: 14, clrUsed: 3);

            var fromCorrect = BitmapDecoder.Read(new MemoryStream(correct));
            var fromCorrupted = BitmapDecoder.Read(new MemoryStream(corrupted));

            Assert.NotNull(fromCorrect);
            Assert.NotNull(fromCorrupted);
            Assert.Equal(fromCorrect.Item2, fromCorrupted.Item2);
            Assert.Equal(fromCorrect.Item3, fromCorrupted.Item3);
            Assert.Equal(fromCorrect.Item1, fromCorrupted.Item1);
        }

        [Fact]
        public void CursorStyleOneBitPalettedBitmapsDecode()
        {
            // A 1bpp, 4x2 image with a two-entry colour table and bfOffBits = 14, matching what
            // CurLoader.LoadCursor produces for every TS1 .cur. Rows are bottom-up and padded to a
            // 4-byte boundary; the top row is 1,0,1,0 and the bottom row 0,1,0,1.
            var bmp = new byte[14 + 40 + 8 + 8];
            bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
            BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
            BitConverter.GetBytes(14).CopyTo(bmp, 10);       // bfOffBits - deliberately wrong
            BitConverter.GetBytes(40).CopyTo(bmp, 14);       // biSize
            BitConverter.GetBytes(4).CopyTo(bmp, 18);        // biWidth
            BitConverter.GetBytes(2).CopyTo(bmp, 22);        // biHeight
            BitConverter.GetBytes((short)1).CopyTo(bmp, 26); // biPlanes
            BitConverter.GetBytes((short)1).CopyTo(bmp, 28); // biBitCount
            BitConverter.GetBytes(8).CopyTo(bmp, 34);        // biSizeImage
            // palette: index 0 black, index 1 white (RGBQUAD, reserved byte 0 as TS1 writes it)
            bmp[54 + 4] = 0xFF; bmp[54 + 5] = 0xFF; bmp[54 + 6] = 0xFF;
            // pixel data (bottom row first)
            bmp[62] = 0x50; bmp[66] = 0xA0;

            var result = BitmapDecoder.Read(new MemoryStream(bmp));

            Assert.NotNull(result);
            Assert.Equal(4, result.Item2);
            Assert.Equal(2, result.Item3);
            Assert.Equal(4 * 2 * 4, result.Item1.Length);

            var data = result.Item1;
            AssertPixel(data, 0, 255, 255, 255); // top-left is palette index 1 (white)
            AssertPixel(data, 1, 0, 0, 0);       // top row second pixel is index 0 (black)
        }
    }
}
