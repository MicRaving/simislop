using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FSO.Files.Formats.DBPF;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="DBPFFile"/> against archives built byte by byte here.
    /// </summary>
    ///
    /// <para>
    /// DBPF is how every sound, every Sim body and every CAS item in a The Sims Online install is
    /// packed, and it had no test at all. The reader's own header parsing walks a dozen
    /// version-gated fields, and the manifest's entry count was an unbounded loop reading
    /// twenty bytes per iteration off the file - so four bytes of noise where a count should be
    /// meant a multi-gigabyte loop or an <see cref="EndOfStreamException"/> from the middle of a
    /// content load, and neither said anything about the file.
    /// </para>
    ///
    /// <para>
    /// The fixtures are hand-built for the same reason as the IFF ones: a round trip through the
    /// class's own writer would only prove the writer agrees with the reader. There is no writer
    /// here, so the bytes come from the format.
    /// </para>
    public sealed class DbpfFileTests : IDisposable
    {
        private readonly string _root;

        public DbpfFileTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-dbpf-" + Guid.NewGuid().ToString("N"));
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

        private sealed class EntrySpec
        {
            public DBPFTypeID Type;
            public DBPFGroupID Group = DBPFGroupID.Multiplayer;
            public uint Instance;
            public byte[] Data = new byte[0];
        }

        /// <summary>
        /// A version 2.0 archive.
        /// </summary>
        /// <remarks>
        /// The field order below is the reader's, not a guess at the format's, and it is not the
        /// order a 1.0 file has. For 2.0 the reader does <em>not</em> read the two date fields at
        /// all - they are behind <c>version == 1.0</c> - it reads the index size unconditionally
        /// where a 1.0 file has its own index offset, and it reads the index offset only after the
        /// index minor version. Every one of those is load-bearing: get the order wrong and the
        /// archive still "loads", it just seeks to offset zero and lists the magic as an entry.
        ///
        /// <para>
        /// 2.0 is what every retail TSO <c>.package</c> is, which is why it is the one built here;
        /// <see cref="WriteVersion1Archive"/> covers the older shape, whose date fields are the
        /// only thing in the format's header a caller can observe.
        /// </para>
        /// </remarks>
        private string WriteArchive(string name, params EntrySpec[] entries)
        {
            var path = Path.Combine(_root, name);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(Encoding.ASCII.GetBytes("DBPF"));    //  0
                w.Write((uint)2);                           //  4  major
                w.Write((uint)0);                           //  8  minor
                w.Write(new byte[12]);                      // 12  unknown
                //no date fields: the reader does not read them at 2.0
                w.Write((uint)entries.Length);              // 24  entry count
                w.Write((uint)(entries.Length * DBPFEntry.Size));   // 28  index size
                w.Write((uint)0);                           // 32  index minor version

                //this is the one the reader seeks to, and the one patched back below
                var indexOffsetSpot = stream.Position;      // 36
                w.Write((uint)0);
                w.Write(new byte[36]);                      // 40  four skipped, then thirty-two of padding

                var offsets = new uint[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    offsets[i] = (uint)stream.Position;
                    w.Write(entries[i].Data);
                }

                var indexOffset = (uint)stream.Position;
                stream.Position = indexOffsetSpot;
                w.Write(indexOffset);
                stream.Position = indexOffset;

                for (int i = 0; i < entries.Length; i++)
                {
                    w.Write((uint)entries[i].Type);
                    w.Write((uint)entries[i].Group);
                    w.Write(entries[i].Instance);
                    w.Write(offsets[i]);
                    w.Write((uint)entries[i].Data.Length);
                }
            }
            return path;
        }

        /// <summary>
        /// A version 1.0 archive, which is the only shape that carries the two date fields.
        /// </summary>
        /// <remarks>
        /// The reader's own gates put the dates immediately after the twelve unknown bytes, then
        /// an index major version, then the entry count, then the index offset - and then four
        /// fields of 1.0-only rubbish (a trash entry count, its offset and size, and an index
        /// minor version) before the padding. Getting that rubbish wrong writes a perfectly good
        /// 1.0 archive the reader seeks past the end of.
        /// </remarks>
        private string WriteVersion1Archive(string name, params EntrySpec[] entries)
        {
            var path = Path.Combine(_root, name);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new BinaryWriter(stream))
            {
                w.Write(Encoding.ASCII.GetBytes("DBPF"));    //  0
                w.Write((uint)1);                           //  4  major
                w.Write((uint)0);                           //  8  minor
                w.Write(new byte[12]);                      // 12  unknown
                w.Write((uint)0x12345678);                  // 24  date created
                w.Write((uint)0x12345679);                  // 28  date modified
                w.Write((uint)0);                           // 32  index major version
                w.Write((uint)entries.Length);              // 36  entry count
                var indexOffsetSpot = stream.Position;      // 40  index offset
                w.Write((uint)0);
                w.Write((uint)(entries.Length * DBPFEntry.Size));   // 44  index size
                w.Write((uint)0);                           // 48  trash entry count
                w.Write((uint)0);                           // 52  trash index offset
                w.Write((uint)0);                           // 56  trash index size
                w.Write((uint)0);                           // 60  index minor version
                w.Write(new byte[32]);                      // 64  padding

                var offsets = new uint[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    offsets[i] = (uint)stream.Position;
                    w.Write(entries[i].Data);
                }

                var indexOffset = (uint)stream.Position;
                stream.Position = indexOffsetSpot;
                w.Write(indexOffset);
                stream.Position = indexOffset;

                for (int i = 0; i < entries.Length; i++)
                {
                    w.Write((uint)entries[i].Type);
                    w.Write((uint)entries[i].Group);
                    w.Write(entries[i].Instance);
                    w.Write(offsets[i]);
                    w.Write((uint)entries[i].Data.Length);
                }
            }
            return path;
        }

        private static byte[] Payload(byte fill, int length)
            => Enumerable.Repeat(fill, length).ToArray();

        #endregion

        #region the header

        [Fact]
        public void AnEntryWhoseTypeIdHasItsTopBitSetIsStillFoundByItsCompositeId()
        {
            // Worth pinning because it is not obvious and it is load-bearing. The reader composes
            // an entry's lookup id as `(ulong)InstanceID << 32 + (ulong)TypeID`, and the cast only
            // works because `DBPFTypeID` is declared over **uint** - WAV is 0xBB7051F5, MP3 is
            // 0x3CEC2B47, TRK is 0x5D73A611, all of them with the top bit set. Over a signed int
            // they would sign-extend to 0xFFFFxxxx on the way in and every one of them would be
            // unreachable by GetItemByID, which is the way a caller looks an entry up.
            var path = WriteArchive("highid.package",
                new EntrySpec { Type = DBPFTypeID.WAV, Instance = 1, Data = Payload(0x11, 4) });

            using (var dbpf = new DBPFFile(path))
            {
                Assert.Equal(0x80000000u, (uint)DBPFTypeID.WAV & 0x80000000u);
                Assert.Equal(Payload(0x11, 4),
                    dbpf.GetItemByID(((ulong)1 << 32) | (uint)DBPFTypeID.WAV));
                Assert.Single(dbpf.GetItemsByType(DBPFTypeID.WAV));
            }
        }

        [Fact]
        public void TheDatesComeFromAOnePointZeroFileAndOnlyFromOne()
        {
            // the reader's own gates put the two date fields behind `version == 1.0`, so a 2.0
            // archive - which is every retail TSO package - has neither and the reader must not
            // go looking for them. Both halves of that are worth pinning, because getting it
            // backwards shifts the entry count two fields early and the archive "loads" with a
            // count of 0x12345678.
            var v1 = WriteVersion1Archive("a-v1.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            using (var dbpf = new DBPFFile(v1))
            {
                Assert.Equal(0x12345678, dbpf.DateCreated);
                Assert.Equal(0x12345679, dbpf.DateModified);
            }

            var v2 = WriteArchive("a-v2.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            using (var dbpf = new DBPFFile(v2))
            {
                Assert.Equal(0, dbpf.DateCreated);
                Assert.Equal(0, dbpf.DateModified);
                //and the entry list is still right, which is the half that would break loudly
                Assert.Equal(Payload(0x11, 4), dbpf.GetItemByID(((ulong)1 << 32) | (uint)DBPFTypeID.XA));
            }
        }

        [Fact]
        public void AFileThatIsNotADbpfIsRejected()
        {
            var path = Path.Combine(_root, "not.package");
            File.WriteAllBytes(path, Encoding.ASCII.GetBytes("XXXX" + new string('x', 200)));

            Assert.ThrowsAny<Exception>(() => new DBPFFile(path));
        }

        [Fact]
        public void AFileThatDoesNotExistIsRejectedRatherThanReadAsEmpty()
        {
            Assert.Throws<FileNotFoundException>(
                () => new DBPFFile(Path.Combine(_root, "missing.package")));
        }

        #endregion

        #region the manifest, and the entries in it

        [Fact]
        public void AnEntryIsFoundByTypeAndIdAndComesBackByteForByte()
        {
            var path = WriteArchive("b.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 7, Data = Payload(0xAB, 64) },
                new EntrySpec { Type = DBPFTypeID.UTK, Instance = 8, Data = Payload(0xCD, 33) });

            using (var dbpf = new DBPFFile(path))
            {
                //the id is (instance << 32) + type
                var byId = (ulong)7 << 32 | (uint)DBPFTypeID.XA;
                Assert.Equal(Payload(0xAB, 64), dbpf.GetItemByID(byId));
                Assert.Null(dbpf.GetItemByID(((ulong)9 << 32) | (uint)DBPFTypeID.XA));
            }
        }

        [Fact]
        public void EveryEntryOfATypeComesBackWithItsInstanceId()
        {
            var path = WriteArchive("c.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 8) },
                new EntrySpec { Type = DBPFTypeID.UTK, Instance = 2, Data = Payload(0x22, 4) },
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 3, Data = Payload(0x33, 16) });

            using (var dbpf = new DBPFFile(path))
            {
                var sounds = dbpf.GetItemsByType(DBPFTypeID.XA);

                Assert.Equal(2, sounds.Count);
                Assert.Equal(1u, sounds[0].Key);
                Assert.Equal(Payload(0x11, 8), sounds[0].Value);
                Assert.Equal(3u, sounds[1].Key);
                Assert.Equal(Payload(0x33, 16), sounds[1].Value);
            }
        }

        [Fact]
        public void AskingForATypeTheArchiveDoesNotHoldIsAnEmptyAnswerNotAKeyNotFound()
        {
            // it used to be m_EntriesByType[Type], so a caller probing for what a package
            // contains got an exception that read as "this archive is broken"
            var path = WriteArchive("d.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            using (var dbpf = new DBPFFile(path))
            {
                Assert.Empty(dbpf.GetItemsByType(DBPFTypeID.HIT));
            }
        }

        [Fact]
        public void AnEntryOfNoBytesIsAnEmptyArrayRatherThanNull()
        {
            var path = WriteArchive("e.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = new byte[0] });

            using (var dbpf = new DBPFFile(path))
            {
                var byId = (ulong)1 << 32 | (uint)DBPFTypeID.XA;
                var data = dbpf.GetItemByID(byId);
                Assert.NotNull(data);
                Assert.Empty(data);
            }
        }

        [Fact]
        public void ReadingTwoEntriesInSequenceDoesNotDisturbTheOther()
        {
            // one shared reader, so this is the property that a seek per entry has to preserve:
            // the second read must start where the first one's entry ended, not where the first
            // one's *size* says
            var path = WriteArchive("f.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 100) },
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 2, Data = Payload(0x22, 7) },
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 3, Data = Payload(0x33, 512) });

            using (var dbpf = new DBPFFile(path))
            {
                var all = dbpf.GetItemsByType(DBPFTypeID.XA);
                Assert.Equal(Payload(0x11, 100), all[0].Value);
                Assert.Equal(Payload(0x22, 7), all[1].Value);
                Assert.Equal(Payload(0x33, 512), all[2].Value);
            }
        }

        [Fact]
        public void ConcurrentEntryReadsEachGetTheirOwnBytes()
        {
            // the reason GetEntry takes a lock. It seeks the one shared reader, so two threads
            // interleaving their seeks each come away with a mixture of both - silently, because
            // nothing about the result looks wrong. A sound file that is half one sample and half
            // another is a support ticket nobody can reproduce.
            //
            // The comparison is per-iteration and private. It used to share one `expected` array
            // that every iteration Array.Fill'd while the others SequenceEqual'd it, so the test
            // was measuring its own race as often as the reader's - about one run in six, in
            // isolation, with no lock under test at all. The claim being made is about the bytes
            // one reader hands one caller, so each caller now builds the array it compares
            // against and nothing is shared but the archive itself, which is the thing with the
            // lock in it.
            var path = WriteArchive("g.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4096) },
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 2, Data = Payload(0x22, 4096) },
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 3, Data = Payload(0x33, 4096) },
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 4, Data = Payload(0x44, 4096) });

            using (var dbpf = new DBPFFile(path))
            {
                var failures = 0;

                System.Threading.Tasks.Parallel.For(0, 400, i =>
                {
                    var instance = (uint)((i % 4) + 1);
                    var data = dbpf.GetItemByID(((ulong)instance << 32) | (uint)DBPFTypeID.XA);
                    var expected = new byte[4096];
                    Array.Fill(expected, (byte)(0x11 * instance));
                    if (!data.SequenceEqual(expected))
                    {
                        System.Threading.Interlocked.Increment(ref failures);
                    }
                });

                Assert.Equal(0, failures);
            }
        }

        #endregion

        #region re-reading the same instance

        [Fact]
        public void ReadingASecondArchiveIntoTheSameInstanceDiscardsTheFirstIndexes()
        {
            // m_EntriesByType was not reset, so a second Read accumulated into the first one's
            // per-type lists: GetItemsByType then returned entries whose FileOffset pointed into
            // the *new* stream, and read them from a reader sitting wherever the previous entry
            // left it. Garbage, silently.
            var first = WriteArchive("h1.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 64) });
            var second = WriteArchive("h2.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 2, Data = Payload(0x22, 16) });

            var dbpf = new DBPFFile(first);
            var secondStream = File.OpenRead(second);
            try
            {
                dbpf.Read(secondStream);

                var sounds = dbpf.GetItemsByType(DBPFTypeID.XA);
                Assert.Single(sounds);
                Assert.Equal(2u, sounds[0].Key);
                Assert.Equal(Payload(0x22, 16), sounds[0].Value);
            }
            finally
            {
                //the second stream is the caller's, not the archive's, so it is closed here
                secondStream.Dispose();
                dbpf.Dispose();
            }
        }

        #endregion

        #region the handle

        [Fact]
        public void DisposingTheArchiveReleasesTheFileItOpened()
        {
            // The stream used to be a local in the path constructor, so it went out of scope the
            // moment the constructor returned. Dispose called IoBuffer.Dispose, which is an empty
            // method, so the handle stayed open for the life of the process - and DBPFFile
            // implements IDisposable, which promised a release it never performed. On a full TSO
            // install that is hundreds of permanently open files.
            var path = WriteArchive("i.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            var dbpf = new DBPFFile(path);
            Assert.Equal(Payload(0x11, 4), dbpf.GetItemByID(((ulong)1 << 32) | (uint)DBPFTypeID.XA));

            dbpf.Dispose();

            //an exclusive open now succeeds, which it could not while the reader held it
            using (var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Assert.True(exclusive.Length > 0);
            }
        }

        [Fact]
        public void DisposingTwiceIsHarmless()
        {
            // Dispose is reachable from a finally and from a using, and both can run
            var path = WriteArchive("j.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            var dbpf = new DBPFFile(path);
            dbpf.Dispose();
            dbpf.Dispose();
        }

        [Fact]
        public void ReadingAnEntryAfterDisposingIsRefusedRatherThanReturningNothing()
        {
            var path = WriteArchive("k.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            var dbpf = new DBPFFile(path);
            dbpf.Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => dbpf.GetItemByID(((ulong)1 << 32) | (uint)DBPFTypeID.XA));
        }

        #endregion

        #region damage

        [Fact]
        public void AnEntryCountTheFileCannotHoldIsRefusedBeforeTheLoopStarts()
        {
            // THE regression. NumEntries drove `for (int i = 0; i < NumEntries; i++)` reading
            // twenty bytes per iteration with nothing checked: four bytes of noise where the
            // count should be meant a multi-gigabyte loop, or an EndOfStreamException from the
            // middle of a content load with the window already up.
            var path = WriteArchive("l.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            //the count is the fourth uint of a 2.0 header: magic(4) major(4) minor(4) + twelve
            //unknown, and no date fields, so 4 + 4 + 4 + 12 = 24
            PatchUInt32(path, EntryCountOffset, 100_000);

            var thrown = Assert.Throws<DbpfFormatException>(() => new DBPFFile(path));
            Assert.Contains("entries", thrown.Message);
            Assert.Contains("corrupt", thrown.Message);
        }

        [Fact]
        public void AnEntryCountOfAllOnesIsRefusedTheSameWay()
        {
            var path = WriteArchive("m.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });
            PatchUInt32(path, EntryCountOffset, uint.MaxValue);

            Assert.Throws<DbpfFormatException>(() => new DBPFFile(path));
        }

        [Fact]
        public void AnEntryPointingPastTheEndOfTheFileIsRefused()
        {
            // both the offset and the size are file-supplied and both used to go straight into a
            // seek and a read. An offset past the end seeks successfully and reads nothing, so
            // the caller was handed a zero-length buffer for a resource that exists and parsed it
            // as complete; a size of 0xFFFFFFFF casts to -1 and is an ArgumentOutOfRangeException.
            var path = WriteArchive("n.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            //the index is the last 20 bytes: offset at +12, size at +16
            var length = new FileInfo(path).Length;
            PatchUInt32(path, (int)(length - DBPFEntry.Size) + 12, 0xFFFFFF00);
            PatchUInt32(path, (int)(length - DBPFEntry.Size) + 16, 0x1000);

            using (var dbpf = new DBPFFile(path))
            {
                var thrown = Assert.Throws<DbpfFormatException>(
                    () => dbpf.GetItemByID(((ulong)1 << 32) | (uint)DBPFTypeID.XA));
                Assert.Contains("corrupt", thrown.Message);
            }
        }

        [Fact]
        public void AnEntrySizeLargerThanTheFileIsRefused()
        {
            var path = WriteArchive("o.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            var length = new FileInfo(path).Length;
            PatchUInt32(path, (int)(length - DBPFEntry.Size) + 16, uint.MaxValue);

            using (var dbpf = new DBPFFile(path))
            {
                var thrown = Assert.Throws<DbpfFormatException>(
                    () => dbpf.GetItemByID(((ulong)1 << 32) | (uint)DBPFTypeID.XA));
                Assert.Contains("corrupt", thrown.Message);
            }
        }

        [Fact]
        public void AskingForNoEntryAtAllIsAnArgumentErrorRatherThanANullReference()
        {
            var path = WriteArchive("p.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });

            using (var dbpf = new DBPFFile(path))
            {
                Assert.Throws<ArgumentNullException>(() => dbpf.GetEntry(null));
            }
        }

        [Fact]
        public void ACorruptPackageIsDistinguishableFromAPackageThatIsNotThere()
        {
            // the distinction a caller makes: a corrupt package is worth reporting and falling
            // back from, and a missing one is worth retrying. Both used to be a bare Exception
            // or a FileNotFoundException with no shared type to switch on.
            var path = WriteArchive("q.package",
                new EntrySpec { Type = DBPFTypeID.XA, Instance = 1, Data = Payload(0x11, 4) });
            PatchUInt32(path, EntryCountOffset, 100_000);

            var corrupt = Assert.Throws<DbpfFormatException>(() => new DBPFFile(path));
            Assert.IsNotType<FileNotFoundException>(corrupt);
            Assert.IsNotType<EndOfStreamException>(corrupt);

            var missing = Assert.Throws<FileNotFoundException>(
                () => new DBPFFile(Path.Combine(_root, "absent.package")));
            Assert.IsNotType<DbpfFormatException>(missing);
        }

        #endregion

        #region helpers

        /// <summary>
        /// Where the entry count sits in a 2.0 header. Spelled out because it is the whole reason
        /// a fixture is right or wrong: put it four or eight bytes off and the archive loads with
        /// a count of one of the header's other fields.
        /// </summary>
        private const int EntryCountOffset = 24;

        /// <summary>Overwrites the little-endian uint at <paramref name="offset"/>.</summary>
        private static void PatchUInt32(string path, int offset, uint value)
        {
            var bytes = File.ReadAllBytes(path);
            bytes[offset] = (byte)value;
            bytes[offset + 1] = (byte)(value >> 8);
            bytes[offset + 2] = (byte)(value >> 16);
            bytes[offset + 3] = (byte)(value >> 24);
            File.WriteAllBytes(path, bytes);
        }

        #endregion
    }
}
