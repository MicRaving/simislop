using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using FSO.Files.FAR3;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="FAR3Archive"/> against synthetic archives built here, one entry at a time.
    ///
    /// <para>
    /// There was no test anywhere for the FAR3 layer, and it is the layer a TS1 install is read
    /// through: every <c>.dat</c> and <c>.far3</c> in the game is opened by this class. Two
    /// defects lived in it that no test could see, and both are silent by construction - one
    /// returns plausible-looking bytes, the other returns an object that looks fine and is
    /// empty.
    /// </para>
    ///
    /// <para>
    /// The fixture is deliberately hand-built rather than checked in. A FAR3 file is a
    /// 16-byte header, a blob of entry data and a manifest of fixed-width records, and
    /// building it in the test means the assertion is about the reader and not about whatever a
    /// checked-in binary happens to contain. The compressed entries carry a hand-built RefPack
    /// stream, which is what lets the size field's byte order be pinned at all: the reader
    /// takes that field off the entry's own header, and nothing else in the codebase writes
    /// it.
    /// </para>
    /// </summary>
    public sealed class Far3ArchiveTests : IDisposable
    {
        private readonly string _root;

        public Far3ArchiveTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-far3-" + Guid.NewGuid().ToString("N"));
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

        #region the fixture

        private sealed class EntrySpec
        {
            public string Name;
            public byte[] Data;
            public bool Compressed;
            public bool WrittenByCompressor;
            public uint FileID;
            public uint TypeID;
        }

        private static EntrySpec Entry(string name, byte[] data, bool compressed = false, uint fileId = 0)
        {
            return new EntrySpec
            {
                Name = name,
                Data = data,
                Compressed = compressed,
                FileID = fileId,
                TypeID = 0x00000001,
            };
        }

        /// <summary>
        /// A compressed entry whose block came out of <see cref="Decompresser.Compress"/>,
        /// rather than out of <see cref="LiteralOnlyRefPack"/>.
        /// </summary>
        private static EntrySpec CompressorEntry(string name, byte[] data, uint fileId)
        {
            return new EntrySpec
            {
                Name = name,
                Data = data,
                Compressed = true,
                WrittenByCompressor = true,
                FileID = fileId,
                TypeID = 0x00000001,
            };
        }

        /// <summary>
        /// A RefPack stream that holds <paramref name="data"/> verbatim, using the literal-run
        /// encoding and nothing else.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The obvious thing is to call <see cref="Decompresser.Compress(byte[])"/>, the class's
        /// own writer, and round trip through it - and
        /// <see cref="AFar3EntryBuiltByTheCompressorComesBackByteForByte"/> now does exactly
        /// that, for the format the tests here care about. This fixture stays for the tests
        /// that push up to sixteen megabytes through a compressed entry: it is linear where
        /// the match finder is not, and the point of those tests is the reader and the
        /// manifest, not the writer.
        /// </para>
        /// <para>
        /// The encoding is the simplest the decompressor accepts, which is what makes this a
        /// measurement of the reader rather than of a second implementation: a control byte in
        /// 0xE0..0xFB means "copy ((control &amp; 0x1F) &lt;&lt; 2) + 4 literal bytes",
        /// i.e. 4 to 112 in steps of four; 0xFC..0xFF means "copy (control &amp; 3) literal
        /// bytes"; and 0xFC on its own ends the stream.
        /// </para>
        /// </remarks>
        private static byte[] LiteralOnlyRefPack(byte[] data)
        {
            var packed = new List<byte>(data.Length + (data.Length / 100) + 16);
            int i = 0;
            while (data.Length - i >= 4)
            {
                int run = Math.Min(data.Length - i, 112);
                run -= run % 4;                     // a run is always a multiple of four
                if (run < 4) break;
                packed.Add((byte)(0xE0 + ((run - 4) / 4)));
                for (int k = 0; k < run; k++) packed.Add(data[i + k]);
                i += run;
            }
            while (i < data.Length)                 // one to three bytes of tail
            {
                int n = Math.Min(3, data.Length - i);
                packed.Add((byte)(0xFC + n));
                for (int k = 0; k < n; k++) packed.Add(data[i + k]);
                i += n;
            }
            packed.Add(0xFC);                       // the end marker
            return packed.ToArray();
        }

        /// <summary>
        /// The block a compressed entry's data offset points at: nine bytes of header the
        /// reader steps over, then the RefPack word - a 4-byte compressed size, the 0xFB10
        /// magic, and the 3-byte <i>big-endian</i> decompressed size.
        /// </summary>
        /// <remarks>
        /// Every multi-byte field in a FAR3 file is little-endian EXCEPT the RefPack/QFS
        /// decompressed size after the 0xFB10 magic, which is big-endian (the RefPack format,
        /// and upstream FreeSO's reader). No real FAR3 file is available locally to confirm it;
        /// this pins the specified layout.
        /// </remarks>
        private static byte[] CompressedBlock(byte[] data)
        {
            var stream = LiteralOnlyRefPack(data);
            //bytes 0..8 are the header the reader steps over; the RefPack word starts at 9
            var block = new byte[9 + 4 + 2 + 3 + stream.Length];
            var size = (uint)stream.Length;
            block[9] = (byte)(size & 0xFF);
            block[10] = (byte)((size >> 8) & 0xFF);
            block[11] = (byte)((size >> 16) & 0xFF);
            block[12] = (byte)((size >> 24) & 0xFF);
            block[13] = 0x10;                        // 0xFB10, little-endian
            block[14] = 0xFB;
            var length = (uint)data.Length;
            block[15] = (byte)((length >> 16) & 0xFF);   // big-endian
            block[16] = (byte)((length >> 8) & 0xFF);
            block[17] = (byte)(length & 0xFF);
            Array.Copy(stream, 0, block, 18, stream.Length);
            return block;
        }

        /// <summary>
        /// The same block, but produced by <see cref="Decompresser.Compress(byte[])"/> - the
        /// class's own writer - rather than assembled here.
        /// </summary>
        /// <remarks>
        /// The writer returns the RefPack word itself - a 4-byte compressed size, the 0xFB10
        /// magic, a 3-byte big-endian decompressed size, and the commands - and
        /// <c>FAR3Archive.GetEntry</c> reads that word from nine bytes into the entry, after
        /// stepping over whatever is in front of it. So the block is placed at offset nine,
        /// which is what the hand-built <see cref="CompressedBlock"/> above does too.
        ///
        /// <para>
        /// That round trip is the reason it is testable at all. <c>Compress</c> used to read its
        /// match map with <c>cmpmap2[mapindex]</c> instead of looking the key up, so it threw
        /// <c>KeyNotFoundException</c> on essentially any input; it wrote the decompressed size
        /// as a four-byte int where the format has three bytes, so its header was one byte too
        /// long for the reader; and it emitted its whole staging array rather than the commands
        /// it had written, so the block carried a tail of padding its declared size did not
        /// describe. Every back-reference form was unexercised in both directions as a result.
        /// </para>
        ///
        /// <para>
        /// It is used only at a size where the match finder is affordable, unlike
        /// <see cref="LiteralOnlyRefPack"/>, which is linear.
        /// </para>
        /// </remarks>
        private static byte[] WriterCompressedBlock(byte[] data)
        {
            var word = new Decompresser().Compress(data);
            var block = new byte[9 + word.Length];
            Array.Copy(word, 0, block, 9, word.Length);
            return block;
        }

        /// <summary>
        /// Writes a minimal but structurally real FAR3 archive.
        /// </summary>
        /// <remarks>
        /// The layout is the one <c>FAR3Archive</c> reads: <c>FAR!byAZ</c>, version 3, the
        /// manifest offset; then entry payloads; then the manifest - a count, then per entry a
        /// 4-byte decompressed size, a 3-byte <i>little-endian</i> compressed size, a type byte,
        /// a data offset, the compressed flag, an access number, a filename length, a type id, a
        /// file id and the filename.
        /// </remarks>
        private string WriteArchive(string fileName, params EntrySpec[] entries)
        {
            var path = Path.Combine(_root, fileName);

            var payloads = new List<byte[]>();
            foreach (var e in entries)
            {
                payloads.Add(e.Compressed
                    ? (e.WrittenByCompressor ? WriterCompressedBlock(e.Data) : CompressedBlock(e.Data))
                    : e.Data);
            }

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("FAR!byAZ"));
                writer.Write((uint)3);
                //patched below once the payload sizes are known
                writer.Write((uint)0);

                var offsets = new uint[payloads.Count];
                for (int i = 0; i < payloads.Count; i++)
                {
                    offsets[i] = (uint)stream.Position;
                    writer.Write(payloads[i]);
                }

                var manifestOffset = (uint)stream.Position;
                stream.Position = 12;
                writer.Write(manifestOffset);
                stream.Position = manifestOffset;

                writer.Write((uint)entries.Length);
                for (int i = 0; i < entries.Length; i++)
                {
                    var e = entries[i];
                    var name = Encoding.ASCII.GetBytes(e.Name);
                    var payload = payloads[i];

                    writer.Write((uint)e.Data.Length);
                    writer.Write((byte)(payload.Length & 0xFF));
                    writer.Write((byte)((payload.Length >> 8) & 0xFF));
                    writer.Write((byte)((payload.Length >> 16) & 0xFF));
                    writer.Write((byte)0);          //DataType
                    writer.Write(offsets[i]);       //DataOffset
                    writer.Write((byte)(e.Compressed ? 1 : 0));
                    writer.Write((byte)0);          //AccessNumber
                    writer.Write((ushort)name.Length);
                    writer.Write(e.TypeID);
                    writer.Write(e.FileID);
                    writer.Write(name);
                }
            }

            return path;
        }

        /// <summary>
        /// Deterministic pseudo-random bytes. A fixed seed, so a failure is reproducible, and a
        /// long enough run that the compressor's match finder has something to find.
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

        private static byte[] Periodic(int period, int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = (byte)((i * 37 + (i / period) * 11) & 0xFF);
            return data;
        }

        /// <summary>
        /// One block of incompressible bytes repeated, so the only match available is the
        /// boundary between two copies - i.e. exactly one match, at exactly
        /// <paramref name="distance"/>.
        /// </summary>
        private static byte[] RepeatedBlock(int distance)
        {
            var block = PseudoRandom(distance);
            var length = distance * 6;
            var data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = block[i % distance];
            return data;
        }

        private static void AssertSameBytes(byte[] expected, byte[] actual, string because)        {
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

        #region The RefPack decompressed size is big-endian

        /// <summary>
        /// Round trip with all three size bytes non-zero. The RefPack decompressed size after the
        /// 0xFB10 magic is big-endian (the format, and upstream); a wrong order turns 0x012345 into
        /// 0x452301 - an absurd buffer - so this fails loudly if the order is flipped.
        /// </summary>
        /// <remarks>
        /// Length 0x012345 (74,565) is chosen so that all three bytes differ: under 65,536 the high
        /// byte is zero and only the low two bytes are checked.
        /// </remarks>
        [Fact]
        public void ACompressedEntryComesBackByteForByte()
        {
            const int length = 0x012345;      // 74,565 - over 64 KB, all three size bytes set
            var data = PseudoRandom(length);
            Assert.Equal(0x45, data.Length & 0xFF);
            Assert.Equal(0x23, (data.Length >> 8) & 0xFF);
            Assert.Equal(0x01, (data.Length >> 16) & 0xFF);

            var path = WriteArchive("compressed.far3", Entry("big.dat", data, compressed: true, fileId: 1));

            using (var archive = new FAR3Archive(path))
            {
                var entry = Assert.Single(archive.GetAllFAR3Entries());
                AssertSameBytes(data, archive.GetEntry(entry),
                    "a compressed entry must decompress to exactly the bytes that went in");
            }
        }

        /// <summary>
        /// The same round trip at a size whose high size byte is zero, and one at a size that
        /// fills all three bytes to the top, so the assertion is not accidentally satisfied by a
        /// single correct byte.
        /// </summary>
        [Theory]
        [InlineData(0x000FFF)]
        [InlineData(0x00FFFF)]
        [InlineData(0x0F0F0F)]
        [InlineData(0xFFFFFF)]
        public void ACompressedEntryOfManySizesComesBackByteForByte(int length)
        {
            var data = PseudoRandom(length);
            var path = WriteArchive("sized-" + length.ToString("X") + ".far3",
                Entry("entry.dat", data, compressed: true, fileId: 1));

            using (var archive = new FAR3Archive(path))
            {
                var entry = Assert.Single(archive.GetAllFAR3Entries());
                AssertSameBytes(data, archive.GetEntry(entry), "size " + length + " must round trip");
            }
        }

        /// <summary>
        /// The whole way round, through the class's own writer. Everything above builds its
        /// RefPack stream by hand, which is the right call when the subject is the reader - but
        /// it means the writer had no test at all, and it was broken: it indexed its match map
        /// with <c>cmpmap2[mapindex]</c> rather than looking the key up, so it threw
        /// <c>KeyNotFoundException</c> on essentially any input, and the blocks it did produce
        /// began with nine zero bytes of a header it had reserved inside the command buffer and
        /// then written outside it.
        ///
        /// <para>
        /// The size is over 64 KB so all three bytes of the archive's size field
        /// are set, and the payload is a repeating pattern so the writer emits real
        /// back-references rather than only literal runs.
        /// </para>
        /// </summary>
        [Fact]
        public void AFar3EntryBuiltByTheCompressorComesBackByteForByte()
        {
            const int length = 0x012345;      // 74,565
            var data = Periodic(37, length);
            var path = WriteArchive("written.far3", CompressorEntry("written.dat", data, fileId: 1));

            using (var archive = new FAR3Archive(path))
            {
                var entry = Assert.Single(archive.GetAllFAR3Entries());
                AssertSameBytes(data, archive.GetEntry(entry),
                    "an entry compressed by Decompresser.Compress must decompress to exactly the bytes that went in");
            }
        }

        /// <summary>
        /// The same at a size with a zero high byte and at a size that fills all three, so the
        /// assertion is not satisfied by a single correct byte of the size field - and with a
        /// payload that has no matches in it at all, which is the writer's literal-run path.
        /// </summary>
        [Theory]
        [InlineData(0x000FFF)]
        [InlineData(0x0F0F0F)]
        public void ACompressorWrittenEntryOfManySizesComesBackByteForByte(int length)
        {
            var data = PseudoRandom(length);
            var path = WriteArchive("written-" + length.ToString("X") + ".far3",
                CompressorEntry("entry.dat", data, fileId: 1));

            using (var archive = new FAR3Archive(path))
            {
                var entry = Assert.Single(archive.GetAllFAR3Entries());
                AssertSameBytes(data, archive.GetEntry(entry), "size " + length + " must round trip");
            }
        }

        /// <summary>
        /// A back-reference in the writer's output that reaches past what the shortest command
        /// form can encode, so the four-byte form has to be used. These are the distances the
        /// two "is this encodable" guards are about, and neither of them had ever been
        /// exercised, in either direction.
        /// </summary>
        [Theory]
        [InlineData(0x401)]        // one byte past the 0x00-0x7F distance field
        [InlineData(0x4001)]       // and past the 0x80-0xBF one as well
        [InlineData(0x1FFFF)]      // the widest the four-byte form reaches
        public void ADistancePastTheShorterCommandFormsStillRoundTrips(int distance)
        {
            var data = RepeatedBlock(distance);
            var path = WriteArchive("distance-" + distance.ToString("X") + ".far3",
                CompressorEntry("entry.dat", data, fileId: 1));

            using (var archive = new FAR3Archive(path))
            {
                var entry = Assert.Single(archive.GetAllFAR3Entries());
                AssertSameBytes(data, archive.GetEntry(entry),
                    "a match at distance " + distance + " must round trip");
            }
        }

        /// <summary>
        /// An uncompressed entry, for the contrast: its length comes from the manifest, which
        /// was always a plain little-endian <c>uint</c>, so this path was never affected. It is
        /// here so that a future byte-order change cannot pass by only fixing the compressed
        /// side.
        /// </summary>
        [Fact]
        public void AnUncompressedEntryComesBackByteForByte()
        {
            var data = PseudoRandom(0x012345);
            var path = WriteArchive("plain.far3", Entry("plain.dat", data, fileId: 7));

            using (var archive = new FAR3Archive(path))
            {
                var entry = Assert.Single(archive.GetAllFAR3Entries());
                AssertSameBytes(data, archive.GetEntry(entry), "an uncompressed entry is a straight read");
            }
        }

        #endregion

        #region F-08 - one bad archive must not disable every later one

        /// <summary>
        /// The regression, and the worst failure mode in the class.
        /// <para>
        /// The constructor used to set a process-wide <c>static bool isReadingSomething</c> and
        /// only clear it on the way out, so a single malformed or truncated file anywhere in the
        /// install - the one thing a retail install always has - left the flag set for the rest
        /// of the process. Every subsequent archive construction then took the "someone else is
        /// already reading" branch, did nothing at all, and returned an object with a null
        /// reader and three empty dictionaries. No exception, no log; <c>Content.GetResource</c>
        /// cached that empty archive, and first use of it took an
        /// <c>ArgumentNullException</c> from <c>lock (m_Reader)</c> on the null. One bad file
        /// disabled all avatar and uigraphics content for the session.
        /// </para>
        /// </summary>
        [Fact]
        public void AFailedArchiveOpenDoesNotDisableEveryLaterArchive()
        {
            var data = PseudoRandom(0x4000);
            var good = WriteArchive("good.far3", Entry("good.dat", data, compressed: true, fileId: 1));

            //the first construction works
            using (var warm = new FAR3Archive(good))
            {
                Assert.Single(warm.GetAllFAR3Entries());
            }

            //one unrecognised file. 64 bytes is comfortably past the 12-byte header, so this is
            //the header check throwing - the throw that used to poison the process.
            var bad = Path.Combine(_root, "not-an-archive.dat");
            File.WriteAllBytes(bad, Enumerable.Repeat((byte)0xFF, 64).ToArray());
            Assert.Throws<FAR3Exception>(() => new FAR3Archive(bad));

            //and the next archive is a real one
            using (var archive = new FAR3Archive(good))
            {
                var entry = Assert.Single(archive.GetAllFAR3Entries());
                Assert.Equal("good.dat", entry.Filename);
                AssertSameBytes(data, archive.GetEntry(entry),
                    "an archive opened after a failed one must be fully readable");
            }
        }

        /// <summary>
        /// A truncated file - the other way a retail install goes wrong - must poison nothing
        /// either. It fails inside the manifest walk, after the reader is open, which is the
        /// one path the old code had no <c>finally</c> for at all.
        /// </summary>
        [Fact]
        public void ATruncatedArchiveDoesNotDisableEveryLaterArchive()
        {
            var data = PseudoRandom(0x2000);
            var good = WriteArchive("good2.far3", Entry("good.dat", data, fileId: 1));
            var full = File.ReadAllBytes(good);

            var truncated = Path.Combine(_root, "truncated.far3");
            File.WriteAllBytes(truncated, full.Take(full.Length - 40).ToArray());
            Assert.ThrowsAny<Exception>(() => new FAR3Archive(truncated));

            using (var archive = new FAR3Archive(good))
            {
                var entry = Assert.Single(archive.GetAllFAR3Entries());
                AssertSameBytes(data, archive.GetEntry(entry),
                    "an archive opened after a truncated one must be fully readable");
            }
        }

        /// <summary>
        /// The other half of the same defect: the guard was process-wide, so a second thread
        /// arriving while the first was inside the constructor silently got an empty archive
        /// rather than waiting for it. Content is loaded from the loader threads, so this is
        /// the ordinary case, not an exotic one.
        /// </summary>
        [Fact]
        public void ArchivesOpenedConcurrentlyAllGetTheirEntries()
        {
            const int threads = 16;
            var paths = new string[threads];
            var expected = new byte[threads][];
            for (int i = 0; i < threads; i++)
            {
                expected[i] = PseudoRandom(0x1000 + i);
                paths[i] = WriteArchive("concurrent" + i + ".far3",
                    Entry("entry.dat", expected[i], compressed: true, fileId: (uint)(i + 1)));
            }

            var start = new ManualResetEventSlim(false);
            var failures = new List<string>();
            var workers = new Thread[threads];

            for (int i = 0; i < threads; i++)
            {
                var index = i;
                workers[i] = new Thread(() =>
                {
                    start.Wait();
                    try
                    {
                        using (var archive = new FAR3Archive(paths[index]))
                        {
                            var entries = archive.GetAllFAR3Entries();
                            if (entries.Count != 1)
                            {
                                lock (failures) failures.Add("archive " + index + " had " + entries.Count + " entries");
                                return;
                            }
                            var actual = archive.GetEntry(entries[0]);
                            if (actual == null || !actual.SequenceEqual(expected[index]))
                            {
                                lock (failures) failures.Add("archive " + index + " read back the wrong bytes");
                            }
                        }
                    }
                    catch (Exception e)
                    {
                        lock (failures) failures.Add("archive " + index + " threw " + e.GetType().Name);
                    }
                });
                workers[i].IsBackground = true;
                workers[i].Start();
            }

            start.Set();
            foreach (var worker in workers)
            {
                Assert.True(worker.Join(TimeSpan.FromSeconds(60)),
                    "an archive read did not finish - concurrent opens must not serialise or deadlock");
            }

            Assert.Empty(failures);
        }

        /// <summary>
        /// A disposed archive says so, rather than handing back a raw IO exception from
        /// somewhere inside the read. The read path holds a per-instance monitor now, so it can
        /// report "this archive is not readable" instead of tripping over a closed stream.
        /// </summary>
        [Fact]
        public void ADisposedArchiveReportsThatItCannotBeRead()
        {
            var data = PseudoRandom(0x100);
            var path = WriteArchive("disposed.far3", Entry("entry.dat", data, fileId: 1));

            var archive = new FAR3Archive(path);
            var entry = Assert.Single(archive.GetAllFAR3Entries());
            AssertSameBytes(data, archive.GetEntry(entry), "precondition: readable before dispose");

            archive.Dispose();

            Assert.Throws<FAR3Exception>(() => archive.GetEntry(entry));
        }

        #endregion

        #region the manifest itself

        /// <summary>
        /// The reader's ordinary job, so the rest of these tests are known to be measuring the
        /// format and not a broken fixture.
        /// </summary>
        [Fact]
        public void TheManifestIsReadAndEveryLookupAgrees()
        {
            var first = PseudoRandom(0x800);
            var second = PseudoRandom(0x900);
            var path = WriteArchive("manifest.far3",
                Entry("first.dat", first, compressed: true, fileId: 11),
                Entry("second.dat", second, fileId: 22));

            using (var archive = new FAR3Archive(path))
            {
                Assert.Equal(2, archive.GetAllFAR3Entries().Count);
                Assert.Equal(2, archive.GetAllEntries().Count);

                var byName = archive.GetAllFAR3Entries();
                Assert.Equal("first.dat", byName[0].Filename);
                Assert.Equal("second.dat", byName[1].Filename);
                Assert.Equal((uint)first.Length, byName[0].DecompressedFileSize);
                Assert.Equal(1, byName[0].IsCompressed);
                Assert.Equal(0, byName[1].IsCompressed);

                AssertSameBytes(first, archive["first.dat"], "the filename index");
                AssertSameBytes(second, archive["second.dat"], "the filename index");
                AssertSameBytes(first, archive.GetItemByID(11u), "the file-id index");
                AssertSameBytes(second, archive.GetItemByID(22u), "the file-id index");

                //GetAllEntries keys by file id, not by name
                var all = archive.GetAllEntries();
                AssertSameBytes(first, all.Single(p => p.Key == 11u).Value, "the bulk walk");
                AssertSameBytes(second, all.Single(p => p.Key == 22u).Value, "the bulk walk");
            }
        }

        /// <summary>
        /// A file that is not an archive at all is rejected with an archive-specific error, not
        /// an IO or cast exception from the middle of the header read. A player with a corrupt
        /// download should get a diagnosable failure from one file, not an empty content system.
        /// </summary>
        [Theory]
        [InlineData(64)]
        [InlineData(12)]
        public void AFileThatIsNotAnArchiveIsRejected(int length)
        {
            var path = Path.Combine(_root, "garbage" + length + ".far3");
            File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, length).ToArray());

            Assert.Throws<FAR3Exception>(() => new FAR3Archive(path));
        }

        [Fact]
        public void AFileThatDoesNotExistIsRejected()
        {
            Assert.Throws<FAR3Exception>(() =>
                new FAR3Archive(Path.Combine(_root, "no-such-archive.far3")));
        }

        #endregion
    }
}
