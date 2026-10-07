using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FSO.Files.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="IoBuffer"/> - the byte-order-aware wrapper every TS1 chunk parser in the game
    /// reads through.
    /// </summary>
    ///
    /// <para>
    /// <b>Why this file exists.</b> It had no test at all, which is not surprising and is
    /// nevertheless the reason four separate defects could sit in it for years: it is the least
    /// glamorous class in the codebase, it is reached through thirty chunk readers rather than
    /// called directly, and every one of those chunk readers is itself untested. What is pinned
    /// here is the contract the chunk readers assume - a length is checked, a truncation is
    /// reported, a byte order is honoured - so that a change to it has to be deliberate.
    /// </para>
    ///
    /// <para>
    /// The fixtures are hand-built byte arrays, deliberately. A test that writes a field with
    /// <c>IoWriter</c> and reads it back with <c>IoBuffer</c> would pass even if both had the
    /// same misunderstanding of the format, and the big-endian default is exactly the kind of
    /// thing two halves of one library can agree on wrongly.
    /// </para>
    public sealed class IoBufferTests
    {
        #region helpers

        private static IoBuffer Over(byte[] bytes, ByteOrder order = ByteOrder.BIG_ENDIAN)
            => IoBuffer.FromBytes(bytes, order);

        private static byte[] Bytes(params int[] values)
        {
            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) bytes[i] = (byte)values[i];
            return bytes;
        }

        #endregion

        #region byte order - the property everything else depends on

        [Fact]
        public void TheDefaultIsBigEndianBecauseThatIsWhatTheFormatIs()
        {
            // A default of little-endian would read every TS1 IFF as noise, so the default is
            // part of the contract rather than a preference.
            Assert.Equal(ByteOrder.BIG_ENDIAN, new IoBuffer(new MemoryStream(new byte[0])).ByteOrder);
        }

        [Theory]
        [InlineData(0x12, 0x34)]
        [InlineData(0xFF, 0x00)]
        [InlineData(0x00, 0x01)]
        [InlineData(0x80, 0x7F)]
        public void A16BitReadIsBigEndianByDefault(int first, int second)
        {
            using (var io = Over(Bytes(first, second)))
            {
                Assert.Equal((ushort)(first << 8 | second), io.ReadUInt16());
            }
        }

        [Fact]
        public void A32BitReadIsBigEndianByDefault()
        {
            // eight bytes, because the two four-byte reads below each consume four
            using (var io = Over(Bytes(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08)))
            {
                Assert.Equal(0x01020304, io.ReadInt32());
            }
            using (var io = Over(Bytes(0x01, 0x02, 0x03, 0x04)))
            {
                Assert.Equal(0x01020304u, io.ReadUInt32());
            }
        }

        [Fact]
        public void LittleEndianIsTheByteForByteReverse()
        {
            // DBPF is little-endian and its reader asks for it by name. If the two orders were
            // not exact reverses, every DBPF field would be wrong in a way no caller would notice.
            using (var io = Over(Bytes(0x01, 0x02, 0x03, 0x04), ByteOrder.LITTLE_ENDIAN))
            {
                Assert.Equal(0x04030201, io.ReadInt32());
            }
            using (var io = Over(Bytes(0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08),
                ByteOrder.LITTLE_ENDIAN))
            {
                Assert.Equal(0x0807060504030201L, io.ReadInt64());
            }
        }

        [Fact]
        public void ASignedReadIsSigned()
        {
            // 0xFFFF is 65535 as a ushort and -1 as a short, and the sign matters: several chunk
            // formats use -1 as a sentinel. In big-endian order 0x8000 is short.MinValue.
            using (var io = Over(Bytes(0xFF, 0xFF)))
            {
                Assert.Equal(ushort.MaxValue, io.ReadUInt16());
            }
            using (var io = Over(Bytes(0xFF, 0xFF)))
            {
                Assert.Equal(-1, io.ReadInt16());
            }
            using (var io = Over(Bytes(0x80, 0x00)))
            {
                Assert.Equal(short.MinValue, io.ReadInt16());
            }
        }

        #endregion

        #region HasBytes - the loop guard, including its two old edges

        [Fact]
        public void HasBytesCountsWhatIsActuallyLeft()
        {
            using (var io = Over(Bytes(1, 2, 3, 4, 5)))
            {
                Assert.True(io.HasBytes(5));
                Assert.True(io.HasBytes(4));
                Assert.True(io.HasBytes(1));
                Assert.False(io.HasBytes(6));
            }
        }

        [Fact]
        public void HasBytesIsFalseAtTheEndOfTheStream()
        {
            // the case IffFile.Read loops on, and the one that used to be answered with
            // `Position < Length - (count - 1)`, which is right for any count above zero
            using (var io = Over(Bytes(1, 2, 3, 4)))
            {
                io.Skip(4);
                Assert.True(io.HasMore == false);
                Assert.False(io.HasBytes(1));
                Assert.False(io.HasBytes(2));
            }
        }

        [Fact]
        public void HasBytesDoesNotOverflowForAnAbsurdCount()
        {
            // `count - 1` on int.MaxValue is int.MaxValue, and the old comparison
            // `Position < Length - (count - 1)` was then asking whether a length of a few bytes
            // was greater than a length minus two billion - which it is not, so a caller asking
            // for two billion bytes was told it had them.
            using (var io = Over(Bytes(1, 2, 3, 4)))
            {
                Assert.False(io.HasBytes(int.MaxValue));
            }
        }

        [Fact]
        public void HasBytesForZeroIsOnlyTrueWhenThereIsSomethingLeft()
        {
            // a caller looping `while (HasBytes(0))` over an exhausted stream would otherwise
            // never stop: the old expression returned true for count == 0 even at EOF.
            using (var io = Over(Bytes(1, 2)))
            {
                Assert.True(io.HasBytes(0));
                io.Skip(2);
                Assert.False(io.HasBytes(0));
            }
        }

        #endregion

        #region ReadVarLen - seven bits per byte, high bit means "more"

        [Theory]
        [InlineData(new byte[] { 0x00 }, 0u)]
        [InlineData(new byte[] { 0x01 }, 1u)]
        [InlineData(new byte[] { 0x7F }, 127u)]
        [InlineData(new byte[] { 0x80, 0x01 }, 128u)]
        [InlineData(new byte[] { 0x81, 0x00 }, 1u)]
        [InlineData(new byte[] { 0x80, 0x80, 0x01 }, 16384u)]
        [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x0F }, 0xFFFFFFFFu)]
        public void AVariableLengthIntegerIsSevenBitsPerByteLittleEndian(byte[] encoded, uint expected)
        {
            // least significant group first - the one encoding the PIFF chunk's patch offsets use
            using (var io = Over(encoded, ByteOrder.LITTLE_ENDIAN))
            {
                Assert.Equal(expected, io.ReadVarLen());
            }
        }

        [Fact]
        public void AVariableLengthIntegerConsumesExactlyItsOwnBytes()
        {
            // a read that consumed the wrong number would silently shift everything after it,
            // which in a chunk full of var-lens is a file that "loads" and is wrong
            using (var io = Over(Bytes(0x81, 0x02, 0xAA, 0xBB), ByteOrder.LITTLE_ENDIAN))
            {
                //0x01 | (0x02 << 7) = 0x101: seven bits per byte, least significant group first
                Assert.Equal(0x101u, io.ReadVarLen());
                Assert.Equal(2, io.Position);
                //little-endian, matching the buffer, so these two bytes are 0xBBAA - the point is
                //only that the two trailing bytes are the ones that got read
                Assert.Equal(0xBBAA, io.ReadUInt16());
            }
        }

        [Fact]
        public void ATruncatedVariableLengthIntegerIsReportedRatherThanRunOffTheEnd()
        {
            // the last byte still has its high bit set, so the format says "more follow" and the
            // stream has ended. Unbounded, that looped until the stream did.
            using (var io = Over(Bytes(0x80, 0x80, 0x80), ByteOrder.LITTLE_ENDIAN))
            {
                var thrown = Assert.Throws<IoFormatException>(() => io.ReadVarLen());
                Assert.Contains("truncated", thrown.Message);
            }
        }

        [Fact]
        public void AVariableLengthIntegerWithTooManyBytesIsRefused()
        {
            // five bytes is all a 32-bit value can use, and the sixth would be a shift of 35 -
            // which C# masks to 3, so the seventh group of bits was being OR'd in at shift 3 and
            // the number that came out was a plausible-looking wrong answer.
            var tooLong = Bytes(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x01);
            using (var io = Over(tooLong, ByteOrder.LITTLE_ENDIAN))
            {
                var thrown = Assert.Throws<IoFormatException>(() => io.ReadVarLen());
                Assert.Contains("continuation bytes", thrown.Message);
            }
        }

        [Fact]
        public void FiveBytesIsStillAcceptedBecauseA32BitValueCanNeedIt()
        {
            using (var io = Over(Bytes(0xFF, 0xFF, 0xFF, 0xFF, 0x0F), ByteOrder.LITTLE_ENDIAN))
            {
                Assert.Equal(uint.MaxValue, io.ReadVarLen());
            }
        }

        #endregion

        #region ReadCString - and the short read that used to be invisible

        [Fact]
        public void ACStringKeepsItsTrailingZeroUnlessAskedOtherwise()
        {
            using (var io = Over(Bytes(0x41, 0x42, 0x43, 0x00)))
            {
                Assert.Equal("ABC\0", io.ReadCString(4));
            }
            using (var io = Over(Bytes(0x41, 0x42, 0x43, 0x00)))
            {
                Assert.Equal("ABC", io.ReadCString(4, true));
            }
        }

        [Fact]
        public void ACStringTrimsAtTheFirstZeroWhenAsked()
        {
            // not the last one: a label with an embedded zero is "everything before the zero"
            using (var io = Over(Bytes(0x41, 0x00, 0x42, 0x00)))
            {
                Assert.Equal("A", io.ReadCString(4, true));
            }
        }

        [Fact]
        public void ACStringIsPlainAsciiAndNotUtf8()
        {
            // a chunk label is four or sixty-four bytes of ASCII. Reading it as UTF-8 would turn
            // a high byte into a replacement character and lose the type name.
            using (var io = Over(Bytes(0xE9, 0x41)))
            {
                Assert.Equal(2, io.ReadCString(2).Length);
            }
        }

        [Fact]
        public void TheLenientCStringReturnsWhatIsThereRatherThanFailing()
        {
            // IffFile.Read reads the sixty-byte magic with the lenient one, and a four-byte file
            // has to come back as "not an IFF" rather than as an exception about a short read.
            // So the short read stays, and is pinned here so nobody "fixes" it and breaks that.
            using (var io = Over(Bytes(0x41, 0x42)))
            {
                Assert.Equal("AB", io.ReadCString(60));
            }
        }

        [Fact]
        public void TheCheckedCStringRefusesAFileThatEndsInsideIt()
        {
            // this is the one used for a chunk's four-byte type and sixty-four-byte label, where
            // a short string is the difference between a chunk and a truncated file. The old
            // BinaryReader.ReadBytes reports EOF by returning fewer bytes and says nothing, so a
            // caller handed a short label had no way to know.
            using (var io = Over(Bytes(0x41, 0x42, 0x43)))
            {
                var thrown = Assert.Throws<IoFormatException>(() => io.ReadCStringChecked(64));
                Assert.Contains("truncated", thrown.Message);
            }
        }

        [Fact]
        public void TheCheckedCStringIsTheSameAsTheLenientOneOnACompleteString()
        {
            using (var io = Over(Bytes(0x41, 0x42, 0x43, 0x00)))
            {
                Assert.Equal("ABC", io.ReadCStringChecked(4, true));
            }
        }

        #endregion

        #region Require - one place that can say "this file ends here"

        [Fact]
        public void RequirePassesForSomethingThatIsThere()
        {
            using (var io = Over(Bytes(1, 2, 3, 4)))
            {
                io.Require(4, "four bytes");
                io.Require(0, "nothing");
            }
        }

        [Fact]
        public void RequireNamesWhatRanOut()
        {
            using (var io = Over(Bytes(1, 2)))
            {
                var thrown = Assert.Throws<IoFormatException>(() => io.Require(8, "a wall pattern name"));
                Assert.Contains("2 bytes", thrown.Message);
                Assert.Contains("a wall pattern name", thrown.Message);
            }
        }

        [Fact]
        public void AFormatExceptionIsAnIoExceptionSoTheExistingGuardsStillCatchIt()
        {
            // deliberate, and the reason this derives from IOException rather than from
            // FormatException: most of the content layer's guards are `catch (IOException)`
            // around a chunk read, because that is what BinaryReader threw. This has to land
            // in the same hands.
            Assert.IsAssignableFrom<IOException>(new IoFormatException("x"));
        }

        #endregion

        #region ReadBytes

        [Fact]
        public void ReadBytesReturnsExactlyWhatWasAskedForWhenItIsThere()
        {
            using (var io = Over(Bytes(1, 2, 3, 4, 5)))
            {
                Assert.Equal(Bytes(1, 2), io.ReadBytes(2));
                Assert.Equal(2, io.Position);
            }
        }

        [Fact]
        public void ReadBytesOfNothingIsAnEmptyArrayNotNull()
        {
            // several chunk parsers ask for a zero-length payload and then enumerate it
            using (var io = Over(Bytes(1, 2)))
            {
                var none = io.ReadBytes(0);
                Assert.NotNull(none);
                Assert.Empty(none);
            }
        }

        [Fact]
        public void ReadBytesReturnsWhatIsThereWhenTheFileEndsFirst()
        {
            // BinaryReader.ReadBytes reports a short read by returning a short array, and that is
            // left alone deliberately: callers that need to know ask Require first. What is
            // pinned is that the behaviour is understood rather than accidental.
            using (var io = Over(Bytes(1, 2)))
            {
                Assert.Equal(Bytes(1, 2), io.ReadBytes(8));
            }
        }

        [Fact]
        public void AnUnsignedReadLengthAboveIntMaxValueIsRefusedRatherThanWrapped()
        {
            // (int)num on a uint of 0xFFFFFFFF is -1, which is an ArgumentOutOfRangeException
            // from ReadBytes with nothing in the message about which field asked. A chunk whose
            // size field is corrupt reaches this.
            using (var io = Over(Bytes(1, 2, 3, 4)))
            {
                var thrown = Assert.Throws<IoFormatException>(() => io.ReadBytes(uint.MaxValue));
                Assert.Contains("corrupt", thrown.Message);
            }
        }

        [Fact]
        public void TheUnsignedAndSignedLengthOverloadsAgree()
        {
            // two buffers, because each read moves the position: the uint overload is the one
            // IffFile used to call (chunkDataSize was a uint), the int one is what the chunks use,
            // and both must return the same bytes for the same count.
            using (var signedLength = Over(Bytes(1, 2, 3, 4)))
            using (var unsignedLength = Over(Bytes(1, 2, 3, 4)))
            {
                Assert.Equal(signedLength.ReadBytes(3), unsignedLength.ReadBytes(3u));
                Assert.Equal(3, signedLength.Position);
                Assert.Equal(3, unsignedLength.Position);
            }
        }

        #endregion

        #region the string readers with a length prefix

        [Fact]
        public void ALongPascalStringIsItsTwoByteLengthFollowedByThatManyBytes()
        {
            //the prefix is a signed 16-bit read, so it follows the buffer's byte order: little
            //endian here, which is how DBPF and the PIFF chunk read it
            using (var io = Over(Bytes(0x05, 0x00, 0x48, 0x65, 0x6C, 0x6C, 0x6F), ByteOrder.LITTLE_ENDIAN))
            {
                Assert.Equal("Hello", io.ReadLongPascalString());
            }
        }

        [Fact]
        public void ALongPascalStringOfNoLengthIsAnEmptyString()
        {
            using (var io = Over(Bytes(0x00, 0x00), ByteOrder.LITTLE_ENDIAN))
            {
                Assert.Equal("", io.ReadLongPascalString());
            }
        }

        [Fact]
        public void ALongPascalStringWithANegativeLengthIsRefused()
        {
            // 0xFFFF is -1 as a short, and ReadBytes(-1) is an ArgumentOutOfRangeException that
            // says nothing about the string.
            using (var io = Over(Bytes(0xFF, 0xFF), ByteOrder.LITTLE_ENDIAN))
            {
                var thrown = Assert.Throws<IoFormatException>(() => io.ReadLongPascalString());
                Assert.Contains("not a possible number", thrown.Message);
            }
        }

        [Fact]
        public void ALongPascalStringLongerThanTheFileIsRefused()
        {
            using (var io = Over(Bytes(0x10, 0x00, 0x41), ByteOrder.LITTLE_ENDIAN))
            {
                var thrown = Assert.Throws<IoFormatException>(() => io.ReadLongPascalString());
                Assert.Contains("truncated", thrown.Message);
            }
        }

        [Fact]
        public void APascalStringIsAOneByteLengthPrefix()
        {
            using (var io = Over(Bytes(0x03, 0x41, 0x42, 0x43)))
            {
                Assert.Equal("ABC", io.ReadPascalString());
            }
        }

        [Fact]
        public void ANullTerminatedStringStopsAtItsTerminator()
        {
            using (var io = Over(Bytes(0x41, 0x42, 0x00, 0xFF, 0xFF)))
            {
                Assert.Equal("AB", io.ReadNullTerminatedString());
                Assert.Equal(3, io.Position);
            }
        }

        [Fact]
        public void ANullTerminatedStringIsAsciiOneBytePerCharacter()
        {
            // each character is widened from a single byte, so a byte above 0x7F becomes a
            // two-character string of two control characters rather than one character - which is
            // why this reader exists alongside ReadNullTerminatedUTF8 and not instead of it
            using (var io = Over(Bytes(0x41, 0x00)))
            {
                Assert.Equal("A", io.ReadNullTerminatedString());
            }
        }

        [Fact]
        public void AnEmptyNullTerminatedStringIsTheTerminatorAlone()
        {
            using (var io = Over(Bytes(0x00, 0x41)))
            {
                Assert.Equal("", io.ReadNullTerminatedString());
                Assert.Equal(1, io.Position);
            }
        }

        [Fact]
        public void AnUnterminatedStringIsReportedAsTruncatedRatherThanThrowingFromAByteRead()
        {
            // the old code's ReadByte at the end raised EndOfStreamException, which says nothing
            // about which string ran off the end. The type is still an IOException, so the
            // content layer's existing guards catch it unchanged.
            using (var io = Over(Bytes(0x41, 0x42, 0x43)))
            {
                var thrown = Assert.Throws<IoFormatException>(() => io.ReadNullTerminatedString());
                Assert.Contains("terminator", thrown.Message);
                Assert.Contains("truncated", thrown.Message);
            }
        }

        [Fact]
        public void TheUtf8VariantStopsAtItsTerminatorToo()
        {
            // 0xC3 0xA9 is one two-byte character; the reader collects the bytes and lets
            // Encoding.UTF8 turn them into it, which is the whole point of the UTF-8 variant.
            using (var io = Over(Bytes(0xC3, 0xA9, 0x00)))
            {
                Assert.Equal("é", io.ReadNullTerminatedUTF8());
            }
            using (var io = Over(Bytes(0x41, 0x42)))
            {
                var thrown = Assert.Throws<IoFormatException>(() => io.ReadNullTerminatedUTF8());
                Assert.Contains("terminator", thrown.Message);
            }
        }

        [Fact]
        public void AnEmptyUtf8StringIsTheTerminatorAlone()
        {
            using (var io = Over(Bytes(0x00, 0x41)))
            {
                Assert.Equal("", io.ReadNullTerminatedUTF8());
            }
        }

        [Fact]
        public void AVariableLengthPascalStringIsTheDotNetStringEncoding()
        {
            // which is not ASCII: it is a seven-bit length in bytes then UTF-8, and a chunk
            // comment can carry anything
            var text = "Lot Thumbnail";
            var payload = new List<byte> { (byte)text.Length };
            payload.AddRange(Encoding.UTF8.GetBytes(text));
            payload.AddRange(Bytes(0xAA, 0xBB));

            using (var io = Over(payload.ToArray()))
            {
                Assert.Equal(text, io.ReadVariableLengthPascalString());
                Assert.Equal(1 + text.Length, io.Position);
            }
        }

        #endregion

        #region positioning

        [Fact]
        public void SkipMovesForwardAndBackwardAndPositionFollows()
        {
            using (var io = Over(Bytes(1, 2, 3, 4, 5, 6, 7, 8)))
            {
                io.Skip(4);
                Assert.Equal(4, io.Position);
                io.Seek(SeekOrigin.Begin, 1);
                Assert.Equal(1, io.Position);
                Assert.Equal(2, io.ReadByte());
            }
        }

        [Fact]
        public void ABufferDoesNotCloseTheStreamItWasHanded()
        {
            // thirty chunk parsers do `using (var io = IoBuffer.FromStream(stream))` on the
            // strength of Dispose releasing something, and IoBuffer never closes the stream. That
            // is safe because every one of them owns the stream itself - and it has to stay that
            // way, or any parser that touches its stream after the block breaks. Pinned so a
            // "Dispose should really dispose" pass finds out here.
            var stream = new MemoryStream(Bytes(1, 2, 3, 4));
            using (var io = IoBuffer.FromStream(stream))
            {
                Assert.Equal(1, io.ReadByte());
            }
            Assert.True(stream.CanRead);
            Assert.Equal(2, stream.ReadByte());
            stream.Dispose();
        }

        #endregion
    }
}
