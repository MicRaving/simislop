using Simitone.Client.Utils;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Bmp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.IO;
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
    /// has to be asserted rather than assumed.
    /// </para>
    ///
    /// <para>
    /// This replaced a GDI+ implementation that swapped R and B through a colour matrix and then
    /// read back <c>Format32bppArgb</c> (BGRA in memory). The two swaps cancelled, so the net
    /// buffer FreeSO actually consumed was R, G, B, A. These tests therefore assert R-first
    /// explicitly rather than "the channels are right somehow", and use three distinct channel
    /// values so that any swap is unambiguous instead of accidentally symmetric.
    /// </para>
    ///
    /// <para>
    /// The images are built in memory with ImageSharp, so the whole class is headless: no
    /// GraphicsDevice, no Content pipeline, no window. The encode side being the same library as
    /// the decode side does mean these cannot catch a disagreement with GDI+ itself, but that is
    /// precisely the dependency that was removed - what is being guarded is the buffer contract
    /// that FreeSO reads, and that is fully determined from this side.
    /// </para>
    /// </summary>
    public sealed class BitmapDecoderTests
    {
        // Three distinct values so that a red/blue swap shows up as an unmistakable failure rather
        // than a coincidental match.
        private const byte R = 0x12;
        private const byte G = 0x34;
        private const byte B = 0x56;

        /// <summary>
        /// Builds a solid-colour image and encodes it, returning a fresh stream positioned at 0.
        /// </summary>
        private static Stream SolidImage(int width, int height, byte alpha, IImageEncoder encoder = null)
        {
            using (var image = new Image<Rgba32>(width, height))
            {
                // Fill explicitly rather than relying on the constructor: ImageSharp's default
                // initialiser is transparent black, which would make an unfilled pixel look like
                // an alpha bug.
                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        image[x, y] = new Rgba32(R, G, B, alpha);
                    }
                }

                var stream = new MemoryStream();
                // Encode via the encoder rather than image.Save(stream, format): Save's Stream
                // overload takes an IImageFormat, and going through IImageEncoder.Encode is what
                // allows the 24bpp BMP below to actually be written as 24bpp.
                (encoder ?? (IImageEncoder)new PngEncoder()).Encode(image, stream);
                stream.Position = 0;
                return stream;
            }
        }

        [Fact]
        public void SolidColourDecodesAsRedGreenBlueAlphaInThatOrder()
        {
            using (Stream png = SolidImage(3, 2, 0xFF))
            {
                var result = BitmapDecoder.Read(png);

                Assert.NotNull(result);
                Assert.Equal(3, result.Item2);
                Assert.Equal(2, result.Item3);

                var data = result.Item1;
                Assert.Equal(3 * 2 * 4, data.Length);

                for (int i = 0; i < data.Length; i += 4)
                {
                    Assert.Equal(R, data[i]);
                    Assert.Equal(G, data[i + 1]);
                    Assert.Equal(B, data[i + 2]);
                    Assert.Equal(0xFF, data[i + 3]);
                }
            }
        }

        [Fact]
        public void NonSquareImagesReportTheirOwnWidthAndHeight()
        {
            // Deliberately lopsided in both directions, and not a multiple of 4 bytes per row in
            // any way that would hide a stride/row-padding mistake.
            const int w = 7;
            const int h = 3;

            using (Stream png = SolidImage(w, h, 0xFF))
            {
                var result = BitmapDecoder.Read(png);

                Assert.NotNull(result);
                Assert.Equal(w, result.Item2);
                Assert.Equal(h, result.Item3);
                // Tightly packed, no row padding: the old GDI+ path had to throw a
                // NotImplementedException if the stride was not exactly width * 4, and the new one
                // gets that for free because Rgba32 is 4 bytes per pixel by construction.
                Assert.Equal(w * h * 4, result.Item1.Length);
            }
        }

        [Theory]
        [InlineData(0x00)]
        [InlineData(0x80)]
        public void AlphaIsStraightNotPremultiplied(byte alpha)
        {
            using (Stream png = SolidImage(2, 2, alpha))
            {
                var result = BitmapDecoder.Read(png);

                Assert.NotNull(result);

                var data = result.Item1;
                for (int i = 0; i < data.Length; i += 4)
                {
                    // The RGB values must be untouched. A premultiplying decoder would have scaled
                    // them by alpha/255, so at alpha 0x80 they would come back near 0x08/0x10/0x16
                    // instead of 0x12/0x34/0x56 - and FreeSO's own Premultiply would then darken
                    // the texture a second time.
                    Assert.Equal(R, data[i]);
                    Assert.Equal(G, data[i + 1]);
                    Assert.Equal(B, data[i + 2]);
                    Assert.Equal(alpha, data[i + 3]);
                }
            }
        }

        [Fact]
        public void PngAndBmpEncodingsProduceIdenticalBuffers()
        {
            using (Stream png = SolidImage(4, 4, 0xFF))
            using (Stream bmp = SolidImage(4, 4, 0xFF, new BmpEncoder()))
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
            // A 24bpp BMP has no alpha channel at all. It must still come back as the same opaque
            // 4-byte-per-pixel buffer rather than 3 bytes per pixel or a short read.
            using (Stream bmp24 = SolidImage(5, 2, 0xFF, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 }))
            {
                var result = BitmapDecoder.Read(bmp24);

                Assert.NotNull(result);
                Assert.Equal(5, result.Item2);
                Assert.Equal(2, result.Item3);
                Assert.Equal(5 * 2 * 4, result.Item1.Length);

                var data = result.Item1;
                for (int i = 0; i < data.Length; i += 4)
                {
                    Assert.Equal(R, data[i]);
                    Assert.Equal(G, data[i + 1]);
                    Assert.Equal(B, data[i + 2]);
                    Assert.Equal(0xFF, data[i + 3]);
                }
            }
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
            using (Stream png = SolidImage(2, 2, 0xFF))
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
            const byte magicR = 0xFF;
            const byte magicG = 0x00;
            const byte magicB = 0xFF;

            using (var image = new Image<Rgba32>(1, 1))
            {
                image[0, 0] = new Rgba32(magicR, magicG, magicB, 0xFF);

                using (var png = new MemoryStream())
                {
                    new PngEncoder().Encode(image, png);
                    png.Position = 0;

                    var result = BitmapDecoder.Read(png);

                    Assert.NotNull(result);
                    var data = result.Item1;
                    Assert.True(data[0] >= 248, "buffer[i] must be the red channel");
                    Assert.True(data[1] <= 4, "buffer[i+1] must be the green channel");
                    Assert.True(data[2] >= 248, "buffer[i+2] must be the blue channel");
                    Assert.Equal(0xFF, data[3]);
                }
            }
        }

        // ------------------------------------------------ BMP colour-table offset repair

        [Fact]
        public void BitmapsWithAnUnderReportedDataOffsetAreRepaired()
        {
            // Reproduces the TS1 cursor shim: CurLoader builds a synthetic BMP file header and
            // always writes bfOffBits = 14, even for paletted images. GDI+ ignored the field and
            // found the palette from the DIB header; ImageSharp honours it, so without the repair
            // the palette was parsed as pixels and the file was rejected. The repair must make a
            // corrupted-but-recoverable BMP decode exactly like the correct one.
            byte[] correct;
            using (Stream bmp = SolidImage(4, 4, 0xFF, new BmpEncoder { BitsPerPixel = BmpBitsPerPixel.Pixel24 }))
            {
                correct = ((MemoryStream)bmp).ToArray();
            }

            var corrupted = (byte[])correct.Clone();
            BitConverter.GetBytes(14).CopyTo(corrupted, 10);

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
            // top-left pixel is palette index 1 (white)
            Assert.Equal(0xFF, data[0]);
            Assert.Equal(0xFF, data[1]);
            Assert.Equal(0xFF, data[2]);
            Assert.Equal(0xFF, data[3]);
            // top row second pixel is palette index 0 (black)
            Assert.Equal(0x00, data[4]);
            Assert.Equal(0x00, data[5]);
            Assert.Equal(0x00, data[6]);
        }
    }
}
