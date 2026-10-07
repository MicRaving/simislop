using System;
using System.IO;
using System.Linq;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="IffFile.Read"/> against files built byte by byte here.
    /// </summary>
    ///
    /// <para>
    /// <b>Why hand-built files.</b> Every other IFF test in this suite builds a file with
    /// <c>IffFile.Write</c> and reads it back, which is a round trip of the <em>writer</em>. It
    /// proves the writer and the reader agree with each other, which is not the same as either
    /// being right about the format - and the reader is what every TS1 install and every player's
    /// <c>House{NN}.iff</c> goes through. The bytes below are laid out by hand, so a test can
    /// assert on the reader alone.
    /// </para>
    ///
    /// <para>
    /// <b>Why PNG_ for the "known" chunks.</b> <c>ListAll()</c> hands every chunk to its own
    /// parser on the way out, so a fixture chunk whose payload is not a real FSOV or a real SIMI
    /// throws from that chunk's reader and the test measures the wrong thing.
    /// <c>PNG_</c>'s payload is opaque by definition - it is the image - so any bytes parse. The
    /// container is what is under test here; the chunks have their own tests.
    /// </para>
    ///
    /// <para>
    /// <b>What is pinned.</b> The header, the chunk record, the damage, and the round trip: a
    /// size below the header (F-10's wrap), a size past the end of the file, a payload cut short,
    /// a file that stops inside a chunk header, an unknown chunk type, a type that is not ASCII,
    /// and a read file that re-serialises to the same bytes.
    /// </para>
    public sealed class IffFileReaderTests : IDisposable
    {
        private readonly string _root;

        public IffFileReaderTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-iff-reader-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                //best effort - a leftover temp dir must never fail the test run
            }
            catch (UnauthorizedAccessException)
            {
                //same: a locked handle on Windows must not turn into a test failure
            }
        }

        #region the format, laid out by hand

        /// <summary>Bytes a chunk spends before its payload: size, id, flags, label.</summary>
        private const int HeaderSize = 76;

        /// <summary>The 2.5 magic, as it sits in a file: sixty bytes, no terminator needed.</summary>
        private const string Magic25 = "IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1";

        /// <summary>The 2.0 magic, which house11.iff uses and which "seems to read fine".</summary>
        private const string Magic20 = "IFF FILE 2.0:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1";

        /// <summary>Bytes of the file header: the magic padded to sixty, then a four-byte offset.</summary>
        private const int FileHeaderSize = 64;

        /// <summary>
        /// The sixty-byte header plus the four-byte resource map offset, which is zero in
        /// everything the game writes and which the reader reads and ignores.
        /// </summary>
        private static byte[] Header(string magic = Magic25)
        {
            var bytes = new byte[FileHeaderSize];
            var text = System.Text.Encoding.ASCII.GetBytes(magic);
            Array.Copy(text, bytes, Math.Min(text.Length, 60));
            return bytes;
        }

        /// <summary>
        /// One chunk record: the four-byte type, the declared size (which counts the header), the
        /// id, the flags, a sixty-four-byte label, and the payload.
        /// </summary>
        /// <param name="type">The four-character chunk type, as ASCII.</param>
        /// <param name="payload">The payload.</param>
        /// <param name="declaredSize">
        /// Overrides the size field. This is the damage: the parameter that made a corrupt size
        /// field wrap to four billion instead of being refused.
        /// </param>
        private static byte[] Chunk(string type, byte[] payload, uint? declaredSize = null,
            ushort id = 1, string label = "chunk", ushort? flags = 0x0010)
        {
            var chunk = new byte[HeaderSize + payload.Length];
            var typeBytes = System.Text.Encoding.ASCII.GetBytes(type.PadRight(4).Substring(0, 4));
            Array.Copy(typeBytes, 0, chunk, 0, 4);

            //big-endian, because the whole IFF container is big-endian
            var size = declaredSize ?? (uint)(HeaderSize + payload.Length);
            chunk[4] = (byte)(size >> 24);
            chunk[5] = (byte)(size >> 16);
            chunk[6] = (byte)(size >> 8);
            chunk[7] = (byte)size;

            chunk[8] = (byte)(id >> 8);
            chunk[9] = (byte)id;
            chunk[10] = (byte)((flags ?? 0) >> 8);
            chunk[11] = (byte)(flags ?? 0);

            var labelBytes = System.Text.Encoding.ASCII.GetBytes(label);
            Array.Copy(labelBytes, 0, chunk, 12, Math.Min(labelBytes.Length, 64));
            Array.Copy(payload, 0, chunk, HeaderSize, payload.Length);
            return chunk;
        }

        /// <summary>
        /// A chunk record whose type is four raw bytes rather than four characters, for the
        /// non-ASCII-label case. Everything else is identical to <see cref="Chunk"/>.
        /// </summary>
        private static byte[] ChunkWithRawType(byte[] type, byte[] payload, uint? declaredSize = null,
            ushort id = 1, string label = "chunk")
        {
            var sized = Chunk("ZZZZ", payload, declaredSize, id, label);
            Array.Copy(type, 0, sized, 0, 4);
            return sized;
        }

        private static byte[] Concat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();

        private static byte[] Bytes(params int[] values)
        {
            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) bytes[i] = (byte)values[i];
            return bytes;
        }

        private IffFile Read(byte[] file)
        {
            var path = Path.Combine(_root, "House01.iff");
            File.WriteAllBytes(path, file);
            //IffFile's own finalizer registers it with FreeSO's process-wide timed reference
            //cache, which nothing ticks in a test host
            var iff = new IffFile(path);
            iff.MarkThrowaway();
            return iff;
        }

        private byte[] Serialise(IffFile iff)
        {
            using (var stream = new MemoryStream())
            {
                iff.Write(stream);
                return stream.ToArray();
            }
        }

        #endregion

        #region the header

        [Fact]
        public void AWellFormedFileWithNoChunksIsAFileWithNoChunks()
        {
            // the empty case, and the one a freshly-created or freshly-evicted lot produces
            var iff = Read(Header());

            Assert.Empty(iff.ListAll());
        }

        [Fact]
        public void TheTwoPointZeroHeaderIsStillAccepted()
        {
            // house11.iff in a real install uses it, with a comment to the effect that it reads
            // fine. Pinned so a "tighten the magic check" pass cannot break that lot.
            var iff = Read(Concat(Header(Magic20), Chunk("PNG_", Bytes(1, 2, 3, 4))));

            Assert.Single(iff.ListAll());
        }

        [Theory]
        [InlineData(0)]
        [InlineData(4)]
        [InlineData(59)]
        [InlineData(64)]
        public void AFileThatCannotHoldTheMagicAndAnOffsetIsNotAnIff(int length)
        {
            // 0, 4, 59 and 64 bytes. Each comes out of Read as "Invalid iff file!", which is the
            // message callers and their tests already depend on - so it is asserted, and the
            // lenient sixty-byte magic read is what keeps a short file a *rejection* rather than
            // an exception about a short read.
            var short1 = Enumerable.Repeat((byte)0x41, length).ToArray();

            var thrown = Assert.ThrowsAny<Exception>(() => Read(short1));

            Assert.Equal("Invalid iff file!", thrown.Message);
        }

        [Fact]
        public void AFileThatIsNotAnIffAtAllIsRejected()
        {
            var thrown = Assert.ThrowsAny<Exception>(() => Read(Bytes(0x00, 0x01, 0x02, 0x03)));

            Assert.Equal("Invalid iff file!", thrown.Message);
        }

        #endregion

        #region a real read

        [Fact]
        public void SeveralChunksComeBackInOrderWithTheirIdsLabelsAndFlags()
        {
            var file = Concat(
                Header(),
                Chunk("PNG_", Bytes(0x89, 0x50, 0x4E, 0x47), id: 513, label: "Lot Thumbnail"),
                Chunk("PNG_", new byte[0], id: 512, label: "Lot Thumbnail"),
                Chunk("PNG_", Bytes(0xDE, 0xAD, 0xBE, 0xEF), id: 513, label: "second"));

            var chunks = Read(file).SilentListAll();

            Assert.Equal(3, chunks.Count);
            Assert.Equal(new[] { "PNG_", "PNG_", "PNG_" }, chunks.Select(c => c.ChunkType));
            Assert.Equal(new ushort[] { 513, 512, 513 }, chunks.Select(c => c.ChunkID));
            Assert.Equal(new[] { "Lot Thumbnail", "Lot Thumbnail", "second" },
                chunks.Select(c => c.ChunkLabel));
            Assert.All(chunks, c => Assert.Equal(0x10, c.ChunkFlags));

            //the payloads, including the empty one - the degradation a lot with no thumbnail hits
            Assert.Equal(Bytes(0x89, 0x50, 0x4E, 0x47), chunks[0].ChunkData);
            Assert.Empty(chunks[1].ChunkData);
            Assert.Equal(Bytes(0xDE, 0xAD, 0xBE, 0xEF), chunks[2].ChunkData);
        }

        [Fact]
        public void AChunkIsHeldAsItsRawBytesUntilSomethingAsksForItToBeParsed()
        {
            // This is the "non-trivial read". The payload is read, but the chunk is NOT handed to
            // its own parser, because a caller that never asks for it should not pay for parsing
            // a lot's worth of BHAV. This unparsed state is what the whole save path depends on.
            var file = Concat(Header(), Chunk("PNG_", Bytes(0x5A, 0x5B, 0x5C), id: 1, label: "lot"));

            var chunk = (PNG)Read(file).SilentListAll()[0];

            Assert.False(chunk.ChunkProcessed);
            Assert.Equal(Bytes(0x5A, 0x5B, 0x5C), chunk.ChunkData);
            Assert.Equal("PNG_", chunk.ChunkType);
        }

        [Fact]
        public void AskingForAChunkByTypeAndIdParsesItAndReleasesTheRawBytes()
        {
            var file = Concat(Header(), Chunk("PNG_", Bytes(0x5A, 0x5B, 0x5C), id: 7, label: "lot"));

            var iff = Read(file);
            var chunk = iff.Get<PNG>(7);

            Assert.NotNull(chunk);
            Assert.Equal(Bytes(0x5A, 0x5B, 0x5C), chunk.data);
            Assert.True(chunk.ChunkProcessed);
            //the raw bytes are dropped once the chunk owns its own, so a big BHAV is not held
            //twice for the life of the file
            Assert.Null(chunk.ChunkData);
        }

        [Fact]
        public void ListingEverythingParsesEveryChunkInTheFile()
        {
            // ListAll is what Write calls, so this is the path a load-then-save takes
            var file = Concat(Header(), Chunk("PNG_", Bytes(1, 2), id: 1), Chunk("PNG_", Bytes(3, 4), id: 2));

            var chunks = Read(file).ListAll();

            Assert.Equal(2, chunks.Count);
            Assert.All(chunks, c => Assert.True(c.ChunkProcessed));
        }

        [Fact]
        public void AFileThatDoesNotHoldAChunkTypeIsNotAnError()
        {
            // every real .iff in the game holds types this build does not know about, so a miss
            // is the normal case and must not throw
            var iff = Read(Concat(Header(), Chunk("PNG_", Bytes(1, 2, 3), id: 1)));

            Assert.Null(iff.Get<OBJD>(1));
            Assert.DoesNotContain(iff.SilentListAll(), c => c.ChunkType == "OBJD");
        }

        [Fact]
        public void AChunkLabelOfFewerThanSixtyFourCharactersIsPaddedWithZeros()
        {
            // a short read would have handed the chunk a label with fewer characters and no way
            // of knowing, which is why AddChunk uses the checked string read for the label
            var file = Concat(Header(), Chunk("PNG_", Bytes(1), id: 1, label: "ab"));

            Assert.Equal("ab", Read(file).SilentListAll()[0].ChunkLabel);
        }

        [Fact]
        public void TwoChunksOfTheSameTypeAndDifferentIdsAreBothKeptAndBothFound()
        {
            // ByChunkId is a per-type index and ByChunkType is a list, and a house file's two
            // PNG_ thumbnails are distinguished only by id (513 and 512) - so a type-keyed
            // container that kept one entry per type would silently drop the roofless image
            var file = Concat(
                Header(),
                Chunk("PNG_", Bytes(0xAA, 0xBB), id: 513),
                Chunk("PNG_", Bytes(0xCC, 0xDD), id: 512));

            var iff = Read(file);

            Assert.Equal(2, iff.SilentListAll().Count);
            Assert.Equal(Bytes(0xAA, 0xBB), iff.Get<PNG>(513).data);
            Assert.Equal(Bytes(0xCC, 0xDD), iff.Get<PNG>(512).data);
        }

        #endregion

        #region F-10: a size below the header used to wrap

        [Theory]
        [InlineData(0u)]
        [InlineData(1u)]
        [InlineData(75u)]
        public void AChunkSmallerThanItsOwnHeaderIsRefused(uint declaredSize)
        {
            // THE regression. chunkSize is a uint and 76 was an int literal, so
            // `chunkSize - 76` was unsigned arithmetic: anything under 76 wrapped to about 4.29
            // billion, became IoBuffer.ReadBytes(uint) -> Reader.ReadBytes((int)num) with a
            // NEGATIVE count, and threw ArgumentOutOfRangeException out of Read for a file the
            // player merely opened. A truncated tail looks exactly like this.
            var file = Concat(Header(), Chunk("PNG_", Bytes(1, 2, 3, 4), declaredSize: declaredSize));

            var thrown = Assert.Throws<IffFormatException>(() => Read(file));
            Assert.Contains("PNG_", thrown.Message);
            Assert.Contains(declaredSize.ToString(), thrown.Message);
        }

        [Fact]
        public void TheMessageForABadSizeNamesTheFileSoAPlayersReportIsAnswerable()
        {
            var file = Concat(Header(), Chunk("PNG_", Bytes(1), declaredSize: 0));

            var thrown = Assert.Throws<IffFormatException>(() => Read(file));

            Assert.Contains("House01.iff", thrown.Message);
        }

        [Fact]
        public void ASizeOfExactlyTheHeaderIsAnEmptyChunkAndIsLegal()
        {
            // the boundary: 76 is the smallest size that describes a real chunk, which is one
            // with no payload. If this is refused, the PNG_ chunk of a lot whose thumbnail failed
            // to render - a real and tested case - becomes unloadable.
            var iff = Read(Concat(Header(), Chunk("PNG_", new byte[0], id: 1)));

            Assert.Single(iff.SilentListAll());
            Assert.Empty(iff.SilentListAll()[0].ChunkData);
        }

        #endregion

        #region a chunk that claims more than the file holds

        [Fact]
        public void AChunkClaimingMoreDataThanTheFileHasIsRefused()
        {
            // BinaryReader.ReadBytes reports a short read by returning fewer bytes and saying
            // nothing, so the chunk used to be built from a payload that simply stopped, and the
            // next thing to hear about it was whatever parser the chunk itself runs - a crash
            // from somewhere in the middle of loading a lot.
            var file = Concat(Header(), Chunk("PNG_", Bytes(1, 2, 3, 4), declaredSize: 4000));

            var thrown = Assert.Throws<IffFormatException>(() => Read(file));
            Assert.Contains("truncated", thrown.Message);
            Assert.Contains("PNG_", thrown.Message);
        }

        [Fact]
        public void AChunkSizeOfAllOnesIsRefusedTheSameWay()
        {
            // what a chunk size field full of noise looks like. A size of 0xFFFFFFFF is *past* the
            // end rather than below the header, so it is the truncation check that catches it -
            // which is why this one asserts only that it is refused and says why.
            var file = Concat(Header(), Chunk("PNG_", Bytes(1, 2), declaredSize: 0xFFFFFFFF));

            var thrown = Assert.Throws<IffFormatException>(() => Read(file));
            Assert.Contains("truncated", thrown.Message);
        }

        [Fact]
        public void AFileThatStopsInsideAChunkHeaderIsRefused()
        {
            // ten bytes of a seventy-six-byte header. The read loop used to run on "are there two
            // more bytes", so it read four of type, four of size, two of id and sixty-four of
            // label out of nothing - and the label read short, silently, producing a chunk with a
            // five-character name.
            var file = Concat(Header(), Bytes(0x50, 0x4E, 0x47, 0x5F, 0x00, 0x00, 0x00, 0x4C, 0x02, 0x01));

            var thrown = Assert.Throws<IffFormatException>(() => Read(file));
            Assert.Contains("truncated", thrown.Message);
            Assert.Contains("76", thrown.Message);
        }

        [Fact]
        public void OneStrayByteAtTheEndIsIgnoredRatherThanReported()
        {
            // deliberate. Skipping an unknown chunk clamps to the last byte, so a file that ends
            // in an unknown chunk must still load; if a single trailing byte were an error, every
            // such file would stop loading.
            var file = Concat(Header(), Chunk("PNG_", Bytes(1, 2, 3, 4), id: 1), Bytes(0x00));

            Assert.Single(Read(file).SilentListAll());
        }

        #endregion

        #region unknown chunk types - the skip path

        [Fact]
        public void AnUnknownChunkInTheMiddleIsSkippedAndTheRestIsRead()
        {
            var file = Concat(
                Header(),
                Chunk("PNG_", Bytes(1, 2, 3, 4), id: 1),
                Chunk("ZZZZ", Bytes(9, 9, 9, 9), id: 2),          //not in CHUNK_TYPES
                Chunk("PNG_", Bytes(0x11, 0x22), id: 2));

            var chunks = Read(file).SilentListAll();

            Assert.Equal(2, chunks.Count);
            Assert.Equal(Bytes(1, 2, 3, 4), chunks[0].ChunkData);
            Assert.Equal(Bytes(0x11, 0x22), chunks[1].ChunkData);
        }

        [Fact]
        public void AnUnknownChunkAtTheEndOfTheFileIsSkippedRatherThanThrowing()
        {
            // walls2.iff ships a chunk whose declared size is past the end of the file. It is
            // unknown to this build, so it is skipped and clamped - which is the only reason the
            // rest of that file loads at all.
            var file = Concat(Header(), Chunk("PNG_", Bytes(1, 2, 3, 4), id: 1),
                Chunk("ZZZZ", Bytes(7), declaredSize: 0x10000));

            Assert.Single(Read(file).SilentListAll());
        }

        [Fact]
        public void AnUnknownChunkClaimingMoreThanTheFileHoldsDoesNotLoop()
        {
            // the skip is clamped to the bytes that are actually there. It used to be
            // `Math.Min(chunkDataSize, stream.Length - stream.Position - 1)`, and at EOF that
            // subtraction is -1, so Math.Min returned -1 and IoBuffer.Skip sought BACKWARDS - and
            // the read loop then re-read the bytes it had just read, forever. A hang inside
            // content load, with the game window already up.
            var file = Concat(Header(), Chunk("ZZZZ", new byte[0], declaredSize: 0x7FFFFFFF));

            Assert.Empty(Read(file).SilentListAll());
        }

        [Fact]
        public void AnUnknownChunkTypeOfFourBytesThatIsNotTextIsStillJustUnknown()
        {
            // a non-ASCII chunk label: 0x00 0x80 0xFF 0x7F is not a name, and it is not in
            // CHUNK_TYPES, so it is skipped. What matters is that it is neither treated as a
            // chunk id nor allowed to stop the rest of the file loading.
            var file = Concat(
                Header(),
                Chunk("PNG_", Bytes(1, 2, 3, 4), id: 1),
                ChunkWithRawType(new byte[] { 0x00, 0x80, 0xFF, 0x7F }, Bytes(1, 2)),
                Chunk("PNG_", Bytes(0x33), id: 2));

            var chunks = Read(file).SilentListAll();

            Assert.Equal(2, chunks.Count);
            Assert.Equal(Bytes(0x33), chunks[1].ChunkData);
        }

        #endregion

        #region the round trip, against a hand-built file

        [Fact]
        public void AHandBuiltFileReSerialisedIsTheSameFile()
        {
            // the check that the hand-built fixture is actually a legal IFF, and the property
            // that makes "save a lot, load it, save it again" safe: nothing in the round trip may
            // drift a byte. The label and the flags are the two that would, because the writer
            // substitutes a 0x10 flag of its own and pads the label to sixty-four.
            var file = Concat(
                Header(),
                Chunk("PNG_", Bytes(0x89, 0x50, 0x4E, 0x47), id: 513, label: "Lot Thumbnail"),
                Chunk("PNG_", Bytes(0xDE, 0xAD, 0xBE, 0xEF), id: 512, label: "Lot Thumbnail"));

            var rewritten = Serialise(Read(file));

            Assert.Equal(file.Length, rewritten.Length);
            Assert.True(file.SequenceEqual(rewritten),
                "a read IFF must re-serialise to the same bytes it was read from");
        }

        [Fact]
        public void AChunkWithNoFlagsIsStampedWithTheOnesTheWriterUses()
        {
            // 0 is not what a chunk on disk carries - the writer substitutes 0x10 - so a chunk
            // with flags of zero is legal to read and comes back normalised on the way out
            var file = Concat(Header(), Chunk("PNG_", Bytes(1, 2, 3), id: 513, flags: 0));

            Assert.Equal(0, Read(file).SilentListAll()[0].ChunkFlags);
            Assert.Equal(0x10, Serialise(Read(file))[FileHeaderSize + 11]);
        }

        /// <summary>
        /// A chunk that does not override <c>Write</c>, so the base class's "no, keep the original"
        /// is what happens - the state of any chunk this build has no writer for, and of a
        /// content-edited chunk in general, since <c>IffFile.RETAIN_CHUNK_DATA</c> defaults to
        /// false and so <c>OriginalData</c> is normally null too.
        /// </summary>
        private sealed class OpaqueChunk : IffChunk
        {
            public override void Read(IffFile iff, Stream stream) { }
        }

        [Fact]
        public void AChunkThatDeclinesToWriteWritesAnEmptyPayloadRatherThanThrowing()
        {
            // WriteChunk asks each chunk for its bytes and takes OriginalData when the answer is
            // no. With neither, the payload is empty - and the file is still a well-formed IFF
            // with a correct size field, rather than an exception from the middle of a save.
            var iff = new IffFile();
            iff.AddChunk(new OpaqueChunk
            {
                ChunkID = 1,
                ChunkLabel = "x",
                ChunkType = "PNG_",
                ChunkProcessed = true,      //nothing to parse, so no payload to lose
            });

            var rewritten = Serialise(iff);

            Assert.Equal(FileHeaderSize + HeaderSize, rewritten.Length);
            Assert.Equal((uint)HeaderSize, ((uint)rewritten[FileHeaderSize + 4] << 24)
                | ((uint)rewritten[FileHeaderSize + 5] << 16)
                | ((uint)rewritten[FileHeaderSize + 6] << 8)
                | rewritten[FileHeaderSize + 7]);
        }

        [Fact]
        public void AChunkThatDeclinesToWriteKeepsItsOriginalDataWhenThereIsSome()
        {
            // the other branch, and the one the content editor relies on: RetainChunkData is on
            // there, so OriginalData holds what the chunk was read from and that is what a
            // re-save writes back
            var iff = new IffFile();
            var chunk = new OpaqueChunk
            {
                ChunkID = 1,
                ChunkLabel = "x",
                ChunkType = "PNG_",
                ChunkProcessed = true,
                OriginalData = Bytes(1, 2, 3, 4),
            };
            iff.AddChunk(chunk);

            var rewritten = Serialise(iff);

            Assert.Equal(FileHeaderSize + HeaderSize + 4, rewritten.Length);
            Assert.Equal(Bytes(1, 2, 3, 4), rewritten.Skip(FileHeaderSize + HeaderSize).ToArray());
        }

        #endregion
    }
}
