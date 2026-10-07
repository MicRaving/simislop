using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The bounded count reader that <c>IoBuffer</c> now shares, and the one chunk whose counts
    /// were large enough to need it in two different places.
    ///
    /// <para>
    /// A count in a binary format is four bytes that came out of a file, used immediately as an
    /// array bound. That is a claim, not a length, and a file the player merely opened - a
    /// neighbourhood cut short by a full disk, a <c>.dat</c> with one bad byte, a patch from a
    /// third-party site - makes the claim whatever noise sits in those four bytes. The two failure
    /// shapes are <see cref="OverflowException"/> for a negative and
    /// <see cref="OutOfMemoryException"/> for a large one, both raised from the middle of a parse
    /// and neither saying which field asked.
    /// </para>
    ///
    /// <para>
    /// The tests below pin the three tests in order of how much they can tell, because that order
    /// is the design: not negative, not above a ceiling no real file reaches, and not more than
    /// the bytes actually left could hold. The third is the one that catches plausible-but-wrong
    /// values, and it is the reason a count of 40,000 in a stream with 300 bytes behind it is
    /// refused before there is an allocation to make.
    /// </para>
    /// </summary>
    public sealed class BinaryCountBoundsTests
    {
        #region helpers

        /// <summary>A little-endian int32, which is what all three count readers consume.</summary>
        private static byte[] Le32(int value)
        {
            return new[]
            {
                (byte)value,
                (byte)(value >> 8),
                (byte)(value >> 16),
                (byte)(value >> 24),
            };
        }

        private static byte[] Le32(uint value)
        {
            return new[]
            {
                (byte)value,
                (byte)(value >> 8),
                (byte)(value >> 16),
                (byte)(value >> 24),
            };
        }

        private static byte[] Le16(int value)
        {
            return new[] { (byte)value, (byte)(value >> 8) };
        }

        private static IoBuffer Reader(byte[] bytes)
        {
            return IoBuffer.FromBytes(bytes, ByteOrder.LITTLE_ENDIAN);
        }

        private static byte[] Concat(params byte[][] parts)
        {
            return parts.SelectMany(p => p).ToArray();
        }

        #endregion

        #region the shared reader

        [Fact]
        public void ACountOfZeroIsAnEmptyCollectionNotAMissingOne()
        {
            Assert.Equal(0, Reader(Le32(0)).ReadCount("thing count"));
        }

        [Fact]
        public void ACountTheBytesCanBackIsRead()
        {
            //four items, four bytes each: exactly enough. The boundary the "too large" test below
            //has to respect, so it is pinned explicitly.
            Assert.Equal(4, Reader(Concat(Le32(4), new byte[16])).ReadCount("thing count", 4));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(-2)]
        [InlineData(int.MinValue)]
        public void ANegativeCountIsRefusedAndNamed(int count)
        {
            var io = Reader(Concat(Le32(count), new byte[64]));
            var ex = Assert.Throws<IoFormatException>(() => io.ReadCount("thing count"));
            Assert.Contains("thing count", ex.Message);
            Assert.Contains("not a possible number", ex.Message);
        }

        [Theory]
        [InlineData(int.MaxValue)]
        [InlineData(0x40000000)]
        [InlineData(IoBuffer.MaxCount + 1)]
        public void ACountAboveTheCeilingIsRefusedAndNamed(int count)
        {
            //A ceiling, and it is only a ceiling: the file is empty, so the remaining-bytes test
            //would catch these too. The point is that the count is refused on its own account, so
            //the message is the ceiling's and not the byte count's.
            var io = Reader(Le32(count));
            var ex = Assert.Throws<IoFormatException>(() => io.ReadCount("thing count"));
            Assert.Contains("thing count", ex.Message);
            Assert.Contains("above", ex.Message);
        }

        [Fact]
        public void ACountTheBytesLeftCannotHoldIsRefusedAndNamed()
        {
            //The one that matters: 40000 is a plausible number, and in a stream with 300 bytes
            //behind it it is not a big file - it is a corrupt one. Refused before the allocation.
            var io = Reader(Concat(Le32(40000), new byte[300]));
            var ex = Assert.Throws<IoFormatException>(() => io.ReadCount("thing count", 1));
            Assert.Contains("thing count", ex.Message);
            Assert.Contains("only 300 bytes left", ex.Message);
        }

        [Fact]
        public void TheBytesPerItemIsWhatDecidesWhetherTheCountFits()
        {
            //The same count and the same 300 bytes: fitting at one byte per item, not at ten. If
            //the factor were ignored the second of these would be accepted.
            var fitting = Reader(Concat(Le32(300), new byte[300]));
            Assert.Equal(300, fitting.ReadCount("thing count", 1));

            var notFitting = Reader(Concat(Le32(300), new byte[300]));
            Assert.Throws<IoFormatException>(() => notFitting.ReadCount("thing count", 10));
        }

        [Fact]
        public void ACountIsReadLittleEndianLikeEverythingElseInTheFormat()
        {
            //A count that has been byte-swapped is the shape the audit named for FAR3's size
            //field: a plausible-looking number that means something else entirely. Sixteen is
            //0x10 - the same four bytes read the other way round are 0x00100000, which the
            //ceiling rejects outright.
            var correct = Reader(new byte[] { 0x10, 0x00, 0x00, 0x00, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16 });
            Assert.Equal(16, correct.ReadCount("thing count", 1));

            var swapped = Reader(new byte[] { 0x00, 0x00, 0x10, 0x00, 1, 2, 3, 4 });
            Assert.Throws<IoFormatException>(() => swapped.ReadCount("thing count", 1));
        }

        [Fact]
        public void TheUnsignedOverloadIsNotACastOfTheSignedOne()
        {
            //0xFFFFFFFF is -1 once it has been through an int, and the test that has to reject it
            //is "not negative". An unsigned overload is the only way to get there.
            var io = Reader(Le32(uint.MaxValue));
            var ex = Assert.Throws<IoFormatException>(() => io.ReadCountUnsigned("thing count"));
            Assert.Contains("thing count", ex.Message);
        }

        [Fact]
        public void TheSixteenBitOverloadIsCheckedAgainstTheBytesToo()
        {
            //Bounded by construction at 65535, so the only test that can fire is the
            //remaining-bytes one - and it is the one worth having: 60000 in a chunk with forty
            //bytes left used to walk off the end of the payload one field at a time.
            var io = Reader(Concat(Le16(60000), new byte[40]));
            var ex = Assert.Throws<IoFormatException>(() => io.ReadCount16("thing count"));
            Assert.Contains("thing count", ex.Message);
        }

        [Fact]
        public void ACountDoesNotAlwaysHaveToBeBackedByBytes()
        {
            //Where a truncated tail is deliberately tolerated - a neighbourhood that should open
            //with the neighbours it does have - the ceiling still applies and the backing-bytes
            //test does not. Both halves of that are pinned, because the flag is what a future
            //change would get wrong.
            var tolerating = Reader(Le32(9));
            Assert.Equal(9, tolerating.ReadCountUnsigned("neighbour count", 27, requireBackingBytes: false));

            var stillRefusing = Reader(Le32(0x40000000));
            Assert.Throws<IoFormatException>(
                () => stillRefusing.ReadCountUnsigned("neighbour count", 27, requireBackingBytes: false));
        }

        [Fact]
        public void AConditionRefusedByACountIsAnIoExceptionSoTheExistingGuardsStillCatchIt()
        {
            //Every caller in the content layer catches IOException around file reads. A count
            //refusal has to be caught by those, or the guards around saves stop working.
            var ex = Record.Exception(() => Reader(Le32(-1)).ReadCount("thing count"));
            Assert.IsAssignableFrom<IOException>(ex);
        }

        #endregion

        #region SPR2

        /// <summary>
        /// A version-1000 SPR2 header: version, sprite count, palette, and the offset table.
        /// </summary>
        private static byte[] Spr2V1000(uint spriteCount, uint palette, params uint[] offsets)
        {
            var parts = new System.Collections.Generic.List<byte[]>
            {
                Le32(1000u),
                Le32(spriteCount),
                Le32(palette),
            };
            foreach (var offset in offsets) parts.Add(Le32(offset));
            return Concat(parts.ToArray());
        }

        [Theory]
        [InlineData(0xFFFFFFFFu)]
        [InlineData(0x40000000u)]
        public void ASpriteCountTheChunkCannotHoldIsRefusedBeforeTheArraysExist(uint count)
        {
            //`new uint[spriteCount]` and `new SPR2Frame[spriteCount]` off the wire, twice, with
            //no check. A corrupt .dat or a truncated chunk made a four-billion-element array.
            var payload = Spr2V1000(count, 0xA3A3);
            var chunk = new SPR2();

            var ex = Assert.Throws<IoFormatException>(() => chunk.Read(null, new MemoryStream(payload)));
            Assert.Contains("sprite count", ex.Message);
        }

        [Fact]
        public void AFrameOffsetPastTheEndOfTheChunkIsRefusedRatherThanSeekedTo()
        {
            //A seek past the end of a MemoryStream succeeds, and the frame then read whatever
            //followed - nothing, or another file's bytes - into a sprite the renderer would draw.
            var payload = Spr2V1000(1, 0xA3A3, 9999);
            var chunk = new SPR2();

            var ex = Assert.Throws<IoFormatException>(() => chunk.Read(null, new MemoryStream(payload)));
            Assert.Contains("9999", ex.Message);
        }

        [Fact]
        public void AnOffsetTableThatGoesBackwardsIsRefused()
        {
            //The guessed frame size is the difference between consecutive offsets, so a table that
            //decreases makes it underflow to nearly four billion - and `ReadBytes` of that is a
            //negative count. Sixteen bytes of filler behind the table so the count check is not
            //what fires: this test is about the ordering, not the size.
            var parts = new List<byte[]>();
            for (int i = 0; i < 16; i++) parts.Add(new byte[] { (byte)i });
            var payload = Concat(Spr2V1000(2, 0xA3A3, 24, 12), Concat(parts.ToArray()));
            var chunk = new SPR2();

            var ex = Assert.Throws<IoFormatException>(() => chunk.Read(null, new MemoryStream(payload)));
            Assert.Contains("before frame 0", ex.Message);
        }

        [Fact]
        public void ASpr2WithNoFramesIsAnSpr2WithNoFrames()
        {
            //The boundary the count tests above have to respect: zero frames, one of which is
            //what an object with no graphics looks like.
            var payload = Spr2V1000(0, 0xA3A3);
            var chunk = new SPR2();

            chunk.Read(null, new MemoryStream(payload));

            Assert.Empty(chunk.Frames);
        }

        [Fact]
        public void AVersionOneThousandAndOneSpriteCountIsCheckedTheSameWay()
        {
            //The second layout has no offset table, so a frame is at least its fixed header and
            //the count needs its own test.
            var payload = Concat(Le32(1001u), Le32(0xA3A3u), Le32(0x40000000u));
            var chunk = new SPR2();

            var ex = Assert.Throws<IoFormatException>(() => chunk.Read(null, new MemoryStream(payload)));
            Assert.Contains("sprite count", ex.Message);
        }

        #endregion
    }
}
