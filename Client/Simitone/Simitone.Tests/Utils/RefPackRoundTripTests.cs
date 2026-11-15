using System;
using System.Linq;
using FSO.Files.FAR3;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="Decompresser"/> against itself: <c>Decompress(Compress(x)) == x</c>.
    ///
    /// <para>
    /// This is the one test that could not be written before. The class has no writer anywhere
    /// else in the codebase - <c>FAR3Archive</c> only ever decompresses - so the reader's
    /// correctness was pinned by hand-building RefPack streams in
    /// <see cref="Far3ArchiveTests"/>, using only the literal-run encoding, with a comment
    /// explaining that the obvious thing (call <c>Compress</c>) was not possible. That comment
    /// was accurate: <c>Compress</c> read its match map with <c>cmpmap2[mapindex]</c> instead
    /// of looking the key up, so it threw <c>KeyNotFoundException</c> on essentially any input
    /// and the writer had no test at all. Every command form except the literal run - both
    /// back-reference forms and the three-byte and four-byte variants - was therefore
    /// unexercised in either direction.
    /// </para>
    ///
    /// <para>
    /// The other half is the contract the reader did not have. It returned a buffer of the
    /// declared decompressed size whose tail was zero-filled whenever the command stream ran
    /// out first, so a truncated entry came back as a plausible-looking array of the right
    /// length. That is the dangerous shape of wrong: it is what a corrupt texture looks like
    /// when nothing has reported an error. These tests assert the opposite - either the exact
    /// bytes come back, or the stream is rejected.
    /// </para>
    /// </summary>
    public sealed class RefPackRoundTripTests
    {
        /// <summary>The nine bytes of header a compressed FAR3 entry carries ahead of its
        /// commands: a 4-byte compressed size, the 0xFB10 magic, a 3-byte big-endian
        /// decompressed size. <c>FAR3Archive.GetEntry</c> steps over all nine.</summary>
        private const int RefPackHeaderLength = 9;

        /// <summary>
        /// Compress, then decompress, with the header stripped exactly the way
        /// <c>FAR3Archive.GetEntry</c> strips it. Returns the round-tripped bytes, and hands
        /// the declared sizes to the caller so a test can assert on them too.
        /// </summary>
        private static byte[] RoundTrip(byte[] data, out byte[] compressedBlock, out long declaredSize)
        {
            var writer = new Decompresser();
            compressedBlock = writer.Compress(data);
            declaredSize = writer.DecompressedSize;

            //the same nine bytes, read off the block the same way the archive reads them: a
            //4-byte compressed size, the 0xFB10 magic, and the 3-byte big-endian
            //decompressed size, so the size is at bytes 6..8
            Assert.Equal((uint)(compressedBlock.Length - RefPackHeaderLength), BitConverter.ToUInt32(compressedBlock, 0));
            Assert.Equal((ushort)0xFB10, BitConverter.ToUInt16(compressedBlock, 4));
            Assert.Equal((uint)data.Length, (uint)((compressedBlock[6] << 16)
                | (compressedBlock[7] << 8) | compressedBlock[8]));

            var reader = new Decompresser();
            reader.CompressedSize = compressedBlock.Length - RefPackHeaderLength;
            reader.DecompressedSize = declaredSize;

            var commands = new byte[compressedBlock.Length - RefPackHeaderLength];
            Array.Copy(compressedBlock, RefPackHeaderLength, commands, 0, commands.Length);
            return reader.Decompress(commands);
        }

        private static byte[] RoundTrip(byte[] data)
        {
            return RoundTrip(data, out _, out _);
        }

        #region the round trip

        /// <summary>
        /// The regression, at the size the audit named: over 64 KB, so all three bytes of the
        /// size field are non-zero and a wrong byte order cannot pass by accident.
        /// </summary>
        [Fact]
        public void LargeIncompressibleDataComesBackByteForByte()
        {
            var data = PseudoRandom(0x012345);
            var actual = RoundTrip(data);
            AssertSameBytes(data, actual, "a 74,565 byte pseudo-random block must round trip");
        }

        /// <summary>
        /// The same for the shapes a real install actually contains. Incompressible bytes
        /// exercise the literal-run encoding only; long runs and repeating structures exercise
        /// every back-reference form, including the three- and four-byte commands whose
        /// distances are the ones the encodability guards are about.
        /// </summary>
        /// <remarks>
        /// The payloads are named by number and built by <see cref="MakePayload"/> rather than
        /// passed as <c>MemberData</c> rows, because a theory argument has to be serialisable
        /// for the test host to enumerate it - and a payload is up to 300 KB of bytes.
        /// </remarks>
        [Theory]
        [InlineData(0, 1024)]        // incompressible, small enough that the whole run is one
        [InlineData(1, 16384)]       // incompressible, over 64 KB so every size byte is set
        [InlineData(2, 300000)]      // incompressible, past the old buffer slack
        [InlineData(3, 70000)]       // one long run of zeroes
        [InlineData(4, 9000)]        // one long run of 0xFF
        [InlineData(5, 4096)]        // a single repeated byte
        [InlineData(6, 40000)]       // a long run bracketed by noise: literal run, then match
        [InlineData(7, 40000)]       // an eight-byte period: every match at one small distance
        [InlineData(8, 80000)]       // a 300-byte period: the match length climbs through the
                                     // 3..10, 4..0x43 and 5..0x404 length ranges in turn
        [InlineData(9, 8202)]        // two distinct 0x401-byte blocks: a 3-byte match one byte
                                     // past the widest distance the 0x00-0x7F form reaches
        [InlineData(10, 8192)]       // the same at exactly 0x400, which it does reach
        [InlineData(11, 32800)]      // one 0x401 block repeated: a long run at that distance
        [InlineData(12, 72000)]      // a long run at 0x4001, past the 0x80-0xBF distance
                                     // field as well, so the four-byte form must be used
        public void EveryPayloadShapeComesBackByteForByte(int shape, int length)
        {
            var data = MakePayload(shape, length);
            var actual = RoundTrip(data, out var block, out var declared);

            Assert.Equal((long)data.Length, declared);
            //the block is nine header bytes plus the commands - never a padded buffer, and
            //never longer than the input, since a compressible payload must actually shrink
            Assert.True(block.Length >= RefPackHeaderLength, "a block needs its header");
            AssertSameBytes(data, actual, "payload shape " + shape + " must round trip");
        }

        /// <summary>
        /// A payload the writer cannot compress at all still has to come back whole, and the
        /// block has to be describable. This is the case that walks the control bytes off the
        /// end of a staging buffer sized for the compressed estimate rather than the worst
        /// case: a long incompressible input is a literal run in a hundred and twelve byte
        /// chunks, and the old slack only covered an input of about 114 KB.
        /// </summary>
        [Fact]
        public void AnIncompressiblePayloadLargerThanTheOldBufferSlackStillRoundTrips()
        {
            //the old staging buffer was Data.Length + 0x404, and the direct control-byte
            //writes were not covered by ArrayCopy2's growth
            var data = PseudoRandom(2_000_000);
            AssertSameBytes(data, RoundTrip(data), "2 MB of incompressible bytes must round trip");
        }

        /// <summary>
        /// Inputs at and below the six-byte cut-off are returned uncompressed by
        /// <see cref="Decompresser.Compress"/>, and a decompressor handed them hands them
        /// back. Both directions of that contract, so a later change to the threshold cannot
        /// quietly start compressing something the reader will not.
        /// </summary>
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(6)]
        public void TinyInputIsPassedThroughUnchanged(int length)
        {
            var data = PseudoRandom(length);
            var block = new Decompresser().Compress(data);
            Assert.Equal(data, block);
        }

        [Fact]
        public void ANullPayloadIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new Decompresser().Compress(null));
            Assert.Throws<ArgumentNullException>(() =>
            {
                var d = new Decompresser { DecompressedSize = 4 };
                d.Decompress(null);
            });
        }

        #endregion

        #region a damaged stream is reported, not zero-padded

        /// <summary>
        /// The regression. Every early exit out of the command loop - the full-buffer breaks
        /// and the end-of-input exit - returned an array of the declared size with the tail
        /// left at zero. Chopping the end off a block and asking for the same number of bytes
        /// back is exactly what a partially-downloaded or bit-rotted entry looks like, and the
        /// old answer was a buffer of the right length and the wrong content.
        /// </summary>
        /// <remarks>
        /// The cuts are all well clear of the end, because a stream whose last command happens
        /// to complete the output is not truncated - the end marker is optional, and dropping
        /// it is legal. A literal run is at most 113 bytes, so a cut 200 bytes from the end
        /// is always inside a run. A compressible payload is included because its tail is one
        /// long back-reference rather than a run of literals, and cutting a command in half
        /// fails differently - which is the point of doing both.
        /// </remarks>
        [Theory]
        [InlineData(0)]    // incompressible: the tail is literal runs
        [InlineData(1)]    // one repeated byte: the tail is a back-reference
        public void ATruncatedStreamIsRejectedRatherThanZeroPadded(int shape)
        {
            var data = shape == 0 ? PseudoRandom(20000) : Enumerable.Repeat((byte)0x5A, 20000).ToArray();
            RoundTrip(data, out var block, out _);
            var commands = Tail(block);

            foreach (var keep in new[] { commands.Length / 2, (commands.Length * 3) / 4, commands.Length - 200 })
            {
                if (keep < 8) continue;
                var cut = new byte[keep];
                Array.Copy(commands, cut, cut.Length);

                var reader = new Decompresser { DecompressedSize = data.Length };
                Assert.Throws<RefPackException>(() => reader.Decompress(cut));
            }
        }

        /// <summary>
        /// A declared size no stream of this length could produce - the three-byte size field
        /// off a truncated or byte-swapped header - must be refused before the allocation. This
        /// is the <c>OutOfMemoryException</c> the audit described: a value near
        /// <c>int.MaxValue</c> asked for a two-gigabyte array for a file of a few kilobytes.
        /// </summary>
        [Fact]
        public void AnImpossibleDeclaredSizeIsRefusedBeforeItIsAllocated()
        {
            var commands = new byte[64];
            var reader = new Decompresser { DecompressedSize = int.MaxValue };

            var thrown = Assert.Throws<RefPackException>(() => reader.Decompress(commands));
            Assert.Contains("cannot be produced", thrown.Message);
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(long.MinValue)]
        public void ANegativeDeclaredSizeIsRefused(long declared)
        {
            var reader = new Decompresser { DecompressedSize = declared };
            Assert.Throws<RefPackException>(() => reader.Decompress(new byte[64]));
        }

        /// <summary>
        /// A command that claims more output than was declared. The old code grew the
        /// destination instead, so a corrupt command produced a result <i>longer</i> than the
        /// header claimed, which is a different file from the one the header describes.
        /// </summary>
        [Fact]
        public void ACommandThatWouldOverrunTheDeclaredSizeIsRejected()
        {
            //0xFB: copy ((0xFB & 0x1F) << 2) + 4 = 112 literal bytes. Only 8 are declared.
            var reader = new Decompresser { DecompressedSize = 8 };
            Assert.Throws<RefPackException>(() => reader.Decompress(new byte[] { 0xFB, 0, 0, 0, 0, 0, 0, 0, 0, 0 }));
        }

        /// <summary>
        /// A back-reference that points behind the start of the output. The old
        /// <c>OffsetCopy</c> did <c>srcPos = destPos - srcPos</c> with no check, so this threw
        /// <c>IndexOutOfRangeException</c> from the middle of the loop - or, on a stream
        /// crafted so the index landed in range, copied bytes from the wrong place and
        /// returned them as though they were the file's contents.
        /// </summary>
        [Fact]
        public void ABackReferenceOutsideTheOutputIsRejected()
        {
            //0x00 is a two-byte command with no literals and a length of three; 0xFF as the
            //second byte makes it a distance of 256, and nothing has been decompressed yet.
            var reader = new Decompresser { DecompressedSize = 16 };
            var thrown = Assert.Throws<RefPackException>(
                () => reader.Decompress(new byte[] { 0x00, 0xFF, 0, 0, 0, 0, 0 }));
            Assert.Contains("back-reference", thrown.Message);
        }

        /// <summary>
        /// A command whose trailing control bytes are not there. This used to be an
        /// <see cref="IndexOutOfRangeException"/> out of a <c>while</c> loop over a file the
        /// player merely opened.
        /// </summary>
        [Fact]
        public void ACommandThatRunsOffTheEndOfTheStreamIsRejected()
        {
            //0xFB copies 112 literal bytes, which the 113 following bytes supply. The last of
            //them is 0x00: a two-byte command whose second control byte is not there.
            var data = new byte[114];
            data[0] = 0xFB;
            data[113] = 0x00;

            var reader = new Decompresser { DecompressedSize = 115 };
            var thrown = Assert.Throws<RefPackException>(() => reader.Decompress(data));
            Assert.Contains("runs past the end", thrown.Message);
        }

        /// <summary>
        /// A stream of nothing but control bytes must terminate rather than spin. A declared
        /// size of zero is the one legal empty answer, and it is the same whether the stream
        /// says so with an end marker or not.
        /// </summary>
        [Fact]
        public void AnEmptyOutputIsAnEmptyArrayAndNotAThrow()
        {
            var withMarker = new Decompresser { DecompressedSize = 0 };
            Assert.Empty(withMarker.Decompress(new byte[] { 0xFC, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }));

            var without = new Decompresser { DecompressedSize = 0 };
            Assert.Empty(without.Decompress(Enumerable.Repeat((byte)0xE0, 8).ToArray()));
        }

        /// <summary>
        /// The end marker is optional: a stream that has produced exactly the declared number
        /// of bytes and then runs out is complete, and must not be reported as damage. The
        /// writer always emits the marker, so this is exercised directly.
        /// </summary>
        [Fact]
        public void AStreamThatFillsTheOutputAndThenEndsIsNotDamage()
        {
            //0xE1: copy 8 literal bytes, and no end marker
            var commands = new byte[] { 0xE1, 1, 2, 3, 4, 5, 6, 7, 8 };
            var reader = new Decompresser { DecompressedSize = 8 };
            Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, reader.Decompress(commands));
        }

        #endregion

        #region helpers

        private static byte[] Tail(byte[] block)
        {
            var commands = new byte[block.Length - RefPackHeaderLength];
            Array.Copy(block, RefPackHeaderLength, commands, 0, commands.Length);
            return commands;
        }

        /// <summary>
        /// Deterministic pseudo-random bytes. A fixed seed, so a failure is reproducible.
        /// </summary>
        private static byte[] PseudoRandom(int length)
        {
            var data = new byte[length];
            uint state = 0x1234ABCD;
            for (int i = 0; i < length; i++)
            {
                state = (state * 1664525u) + 1013904223u;
                data[i] = (byte)(state >> 24);
            }
            return data;
        }

        private static byte[] Concat(params byte[][] parts)
        {
            return parts.SelectMany(p => p).ToArray();
        }

        /// <summary>
        /// The payload shapes named by <see cref="EveryPayloadShapeComesBackByteForByte"/>.
        /// The numbers are matched to the list of <c>InlineData</c> rows beside it; each one
        /// is here because a real install contains it, not because it is easy to generate.
        /// </summary>
        private static byte[] MakePayload(int shape, int length)
        {
            switch (shape)
            {
                case 0:
                case 1:
                case 2:
                    return PseudoRandom(length);
                case 3:
                    return new byte[length];                                    // one long zero run
                case 4:
                    return Enumerable.Repeat((byte)0xFF, length).ToArray();
                case 5:
                    return Enumerable.Repeat((byte)0x41, length).ToArray();
                case 6:
                    //noise, a long run, noise: a literal run followed by a back-reference
                    return Concat(PseudoRandom(5000), Enumerable.Repeat((byte)7, length - 10000).ToArray(), PseudoRandom(5000));
                case 7:
                    return Periodic(8, length);
                case 8:
                    return Periodic(300, length);
                case 9:
                    //two distinct blocks: the only match available is the 0x401-byte boundary
                    //itself, and it is one byte too far for the two-byte command form
                    return Concat(PseudoRandom(0x401), PseudoRandom(0x401));
                case 10:
                    //...at exactly 0x400, which is the widest the two-byte form reaches
                    return Concat(PseudoRandom(0x400), PseudoRandom(0x400));
                case 11:
                    //one block repeated, so the same unencodable distance carries a long run
                    return Twice(PseudoRandom(0x401), length);
                case 12:
                    //a match well past 0x4000, beyond the 0x80-0xBF form's distance field too
                    return Twice(PseudoRandom(0x4001), length);
                default:
                    throw new ArgumentOutOfRangeException(nameof(shape));
            }
        }

        private static byte[] Twice(byte[] block, int length)
        {
            var result = new byte[length];
            for (int i = 0; i < length; i++) result[i] = block[i % block.Length];
            return result;
        }

        private static byte[] Periodic(int period, int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = (byte)((i * 37 + (i / period) * 11) & 0xFF);
            return data;
        }

        private static void AssertSameBytes(byte[] expected, byte[] actual, string because)
        {
            Assert.NotNull(actual);
            if (expected.Length != actual.Length)
            {
                Assert.Fail(because + " (expected " + expected.Length + " bytes, got " + actual.Length + ")");
            }
            for (int i = 0; i < expected.Length; i++)
            {
                if (expected[i] != actual[i])
                {
                    Assert.Fail(because + " (first difference at byte " + i + ": expected "
                        + expected[i] + ", got " + actual[i] + ")");
                }
            }
        }

        #endregion
    }
}
