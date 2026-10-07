using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FSO.Files.FAR1;
using FSO.Files.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <c>FAR1Archive</c> - the reader behind every The Sims <c>.far</c>, and behind
    /// <c>Global.far</c>, which is where a TS1 install's BCON tuning tables come from.
    ///
    /// <para>
    /// It had no test at all, and three things in it were untested and unfixed: every count,
    /// offset and length in the file table was used as-is, a failed open leaked the
    /// <see cref="FileStream"/> it had taken, and every read went through one shared stream with
    /// one shared position and no lock - so two threads asking for two entries got each other's
    /// bytes. Its FAR3 twin had already been given all three.
    /// </para>
    ///
    /// <para>
    /// The archives here are built by hand - about forty bytes for the simplest one - rather than
    /// checked in, so a test that wants a corrupt file can corrupt exactly the four bytes it is
    /// about and no other. Real <c>.far</c> fixtures would be the better long-term answer; see
    /// the note at the end of the class.
    /// </para>
    /// </summary>
    public sealed class Far1ArchiveTests : IDisposable
    {
        private readonly string _tempRoot;

        public Far1ArchiveTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-far1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, true);
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

        #region building archives by hand

        private static void Le32(List<byte> into, uint value)
        {
            into.Add((byte)value);
            into.Add((byte)(value >> 8));
            into.Add((byte)(value >> 16));
            into.Add((byte)(value >> 24));
        }

        private static void Le32(List<byte> into, int value) => Le32(into, (uint)value);

        private static void Le16(List<byte> into, short value)
        {
            into.Add((byte)value);
            into.Add((byte)(value >> 8));
        }

        /// <summary>
        /// A v1b archive whose file table starts at <paramref name="tableOffset"/>, holding the
        /// given names and payloads. The payloads sit at the end, so the table and the data never
        /// overlap.
        /// </summary>
        private byte[] BuildArchive(IDictionary<string, byte[]> entries, uint tableOffset = 16)
        {
            var table = new List<byte>();
            var tableSize = 4 /*count*/ + entries.Count * (12 + 2);
            foreach (var name in entries.Keys) tableSize += name.Length;
            var dataStart = tableOffset + tableSize;
            var data = new List<byte>();
            var offsets = new Dictionary<string, uint>();

            //two passes: the offsets have to be known before the table can be written
            foreach (var kv in entries)
            {
                offsets[kv.Key] = (uint)(dataStart + data.Count);
                data.AddRange(kv.Value);
            }

            Le32(table, (uint)entries.Count);
            foreach (var kv in entries)
            {
                Le32(table, kv.Value.Length);       // DataLength
                Le32(table, kv.Value.Length);       // DataLength2
                Le32(table, offsets[kv.Key]);      // DataOffset
                Le16(table, (short)kv.Key.Length);  // FilenameLength
                table.AddRange(Encoding.ASCII.GetBytes(kv.Key));
            }

            var file = new byte[dataStart + data.Count];
            var header = new List<byte>();
            header.AddRange(Encoding.ASCII.GetBytes("FAR!byAZ"));
            Le32(header, 1);             // version
            Le32(header, tableOffset);
            header.ToArray().CopyTo(file, 0);
            table.ToArray().CopyTo(file, tableOffset);
            data.ToArray().CopyTo(file, dataStart);
            return file;
        }

        private string WriteArchive(byte[] bytes, string name = "Global.far")
        {
            var path = Path.Combine(_tempRoot, name);
            File.WriteAllBytes(path, bytes);
            return path;
        }

        private static byte[] Payload(params int[] values)
        {
            return values.Select(v => (byte)v).ToArray();
        }

        #endregion

        [Fact]
        public void AnArchiveComesBackWithEveryEntryAndItsBytes()
        {
            var path = WriteArchive(BuildArchive(new Dictionary<string, byte[]>
            {
                { "Global.iff", Payload(1, 2, 3, 4, 5) },
                { "walking.iff", Payload(9, 8, 7) },
            }));

            using (var archive = new FAR1Archive(path, true))
            {
                Assert.Equal(2u, archive.NumFiles);
                Assert.Equal(16u, archive.ManifestOffset);
                Assert.Equal(path, archive.ArchivePath);

                var entries = archive.GetAllFarEntries();
                Assert.Equal(2, entries.Count);
                Assert.Equal(new[] { "Global.iff", "walking.iff" }, entries.Select(e => e.Filename));

                Assert.Equal(Payload(1, 2, 3, 4, 5), archive.GetEntry(entries[0]));
                Assert.Equal(Payload(9, 8, 7), archive.GetEntry(entries[1]));
            }
        }

        [Fact]
        public void AnEntryIsAlsoFoundByNamePair()
        {
            var path = WriteArchive(BuildArchive(new Dictionary<string, byte[]>
            {
                { "Build.iff", Payload(7) },
            }));

            using (var archive = new FAR1Archive(path, true))
            {
                Assert.Equal(Payload(7), archive.GetEntry(new KeyValuePair<string, byte[]>("Build.iff", null)));
                //and an entry that is not in the archive is a miss, not an empty answer
                Assert.Null(archive.GetEntry(new KeyValuePair<string, byte[]>("Nope.iff", null)));
            }
        }

        [Fact]
        public void EveryEntryOfTheArchiveComesBackAtOnce()
        {
            var path = WriteArchive(BuildArchive(new Dictionary<string, byte[]>
            {
                { "a.iff", Payload(1, 1) },
                { "b.iff", Payload(2, 2, 2) },
            }));

            using (var archive = new FAR1Archive(path, true))
            {
                var all = archive.GetAllEntries();
                Assert.Equal(2, all.Count);
                Assert.Equal(Payload(1, 1), all.Single(e => e.Key == "a.iff").Value);
                Assert.Equal(Payload(2, 2, 2), all.Single(e => e.Key == "b.iff").Value);
            }
        }

        [Fact]
        public void AnArchiveWithNoEntriesIsAnArchiveWithNoEntries()
        {
            var path = WriteArchive(BuildArchive(new Dictionary<string, byte[]>()));

            using (var archive = new FAR1Archive(path, true))
            {
                Assert.Equal(0u, archive.NumFiles);
                Assert.Empty(archive.GetAllFarEntries());
                Assert.Empty(archive.GetAllEntries());
            }
        }

        [Fact]
        public void AFileThatIsNotAnArchiveIsRejected()
        {
            var path = WriteArchive(Encoding.ASCII.GetBytes("NOTAFAR1butlongenough"));

            Assert.ThrowsAny<Exception>(() => new FAR1Archive(path, true));
        }

        [Fact]
        public void AFileThatDoesNotExistIsRejected()
        {
            Assert.ThrowsAny<Exception>(
                () => new FAR1Archive(Path.Combine(_tempRoot, "missing.far"), true));
        }

        [Fact]
        public void AFileTablePastTheEndOfTheFileIsRefusedAndNamed()
        {
            //The manifest offset used to be seeked to blind. A seek past the end of a MemoryStream
            //succeeds, so the reads after it came from nothing at all.
            var bytes = BuildArchive(new Dictionary<string, byte[]> { { "a.iff", Payload(1) } });
            bytes[12] = 0xFF; bytes[13] = 0xFF; bytes[14] = 0x00; bytes[15] = 0x00;

            var ex = Assert.Throws<IoFormatException>(
                () => new FAR1Archive(WriteArchive(bytes), true));
            Assert.Contains("file table", ex.Message);
        }

        [Fact]
        public void AFileCountTheFileCannotHoldIsRefusedBeforeTheLoopStarts()
        {
            var bytes = BuildArchive(new Dictionary<string, byte[]> { { "a.iff", Payload(1) } });
            //the count is the first four bytes of the file table, at the manifest offset
            bytes[16] = 0xFF; bytes[17] = 0xFF; bytes[18] = 0x00; bytes[19] = 0x00;

            var ex = Assert.Throws<IoFormatException>(
                () => new FAR1Archive(WriteArchive(bytes), true));
            Assert.Contains("entries", ex.Message);
        }

        /// <summary>
        /// Offsets inside one file-table entry, relative to the manifest offset:
        /// DataLength, DataLength2, DataOffset, then the two-byte FilenameLength.
        /// </summary>
        private const int EntryDataLength = 4;
        private const int EntryDataOffset = 12;
        private const int EntryFilenameLength = 16;

        [Fact]
        public void ANegativeFilenameLengthIsRefusedAndNamed()
        {
            //`ReadBytes(negative)` used to be an ArgumentOutOfRangeException out of the
            //constructor, with nothing in the message about which entry or which archive.
            var bytes = BuildArchive(new Dictionary<string, byte[]> { { "a.iff", Payload(1) } });
            bytes[16 + EntryFilenameLength] = 0xFF;
            bytes[16 + EntryFilenameLength + 1] = 0xFF;

            var ex = Assert.Throws<IoFormatException>(
                () => new FAR1Archive(WriteArchive(bytes), true));
            Assert.Contains("filename length", ex.Message);
        }

        [Fact]
        public void AnEntryPointingPastTheEndOfTheFileIsRefusedWhenTheArchiveIsOpened()
        {
            //An offset and a length are four bytes each in the table, and a corrupt one used to be
            //used: the read then returned a short array with nothing said about it, and the caller
            //parsed that as a mesh, a texture or a global table.
            var bytes = BuildArchive(new Dictionary<string, byte[]> { { "a.iff", Payload(1, 2, 3) } });
            bytes[16 + EntryDataOffset] = 0xFF;
            bytes[16 + EntryDataOffset + 1] = 0xFF;
            bytes[16 + EntryDataOffset + 2] = 0x00;
            bytes[16 + EntryDataOffset + 3] = 0x00;

            var ex = Assert.Throws<IoFormatException>(
                () => new FAR1Archive(WriteArchive(bytes), true));
            Assert.Contains("not inside the file", ex.Message);
        }

        [Fact]
        public void ARejectedArchiveDoesNotLeaveItsFileHandleOpen()
        {
            //Every open is a handle. The player may be halfway through a hundred of them, and a
            //throw used to leave the handle behind - the constructor had no try/catch at all.
            var bytes = BuildArchive(new Dictionary<string, byte[]> { { "a.iff", Payload(1) } });
            bytes[16] = 0xFF; bytes[16 + 1] = 0xFF; bytes[16 + 2] = 0x00; bytes[16 + 3] = 0x00;
            var path = WriteArchive(bytes);

            Assert.Throws<IoFormatException>(() => new FAR1Archive(path, true));

            //the only way to observe it: with the handle still open this rename is refused
            var moved = path + ".moved";
            File.Move(path, moved);
            Assert.True(File.Exists(moved));
        }

        [Fact]
        public void AnArchiveCanBeClosedAndReadAgainFromDisk()
        {
            //The contract F-25 asked for: FAR1Archive is IDisposable, and FAR1Provider has
            //something to call. Without it, a TS1 install left a handle open per .far.
            var path = WriteArchive(BuildArchive(new Dictionary<string, byte[]>
            {
                { "a.iff", Payload(1, 2) },
            }));

            var archive = new FAR1Archive(path, true);
            Assert.Equal(Payload(1, 2), archive.GetEntry(archive.GetAllFarEntries()[0]));

            archive.Dispose();
            Assert.True(archive.IsClosed);

            //a read after close says so, rather than dereferencing a null reader
            var ex = Assert.Throws<IOException>(() => archive.GetEntry(archive.GetAllFarEntries()[0]));
            Assert.Contains("closed", ex.Message);

            //and closing twice is harmless
            archive.Dispose();
            archive.Close();
        }

        [Fact]
        public void ReadingEveryEntryInSequenceDoesNotDisturbTheOthers()
        {
            //One reader, one stream, one position. Every GetEntry seeks, so the sequence is safe -
            //what was not safe was two threads doing it at once, which is the next test.
            var path = WriteArchive(BuildArchive(new Dictionary<string, byte[]>
            {
                { "a.iff", Payload(1, 1, 1, 1) },
                { "b.iff", Payload(2, 2, 2, 2, 2, 2) },
                { "c.iff", Payload(3, 3) },
            }));

            using (var archive = new FAR1Archive(path, true))
            {
                var entries = archive.GetAllFarEntries();
                for (int round = 0; round < 3; round++)
                {
                    Assert.Equal(Payload(1, 1, 1, 1), archive.GetEntry(entries[0]));
                    Assert.Equal(Payload(2, 2, 2, 2, 2, 2), archive.GetEntry(entries[1]));
                    Assert.Equal(Payload(3, 3), archive.GetEntry(entries[2]));
                }
            }
        }

        [Fact]
        public void ConcurrentReadsEachGetTheirOwnBytes()
        {
            //Content is loaded from more than one thread - a lot's meshes, a thumbnail, an avatar
            //- and this is the shape that made them corrupt: thread A seeks, thread B seeks, both
            //read. Each reader here expects its own entry, so any interleaving shows up as the
            //wrong bytes rather than as a crash.
            var entries = new Dictionary<string, byte[]>();
            for (int i = 0; i < 24; i++)
            {
                entries["entry" + i + ".iff"] = Enumerable.Repeat((byte)(i + 1), 64).ToArray();
            }
            var path = WriteArchive(BuildArchive(entries));

            using (var archive = new FAR1Archive(path, true))
            {
                var all = archive.GetAllFarEntries();
                var failures = new List<string>();
                var threads = Enumerable.Range(0, 8).Select(t => new System.Threading.Thread(() =>
                {
                    for (int round = 0; round < 40; round++)
                    {
                        foreach (var entry in all)
                        {
                            var expected = entries[entry.Filename];
                            var got = archive.GetEntry(entry);
                            if (!expected.SequenceEqual(got))
                            {
                                lock (failures) failures.Add(entry.Filename);
                                return;
                            }
                        }
                    }
                })).ToList();

                foreach (var thread in threads) thread.Start();
                foreach (var thread in threads) thread.Join();

                Assert.Empty(failures);
            }
        }

        // <para>
        // A checked-in 2 KB synthetic .far pair - one well formed, one deliberately corrupt -
        // would be the better long-term answer here and is the prerequisite the audit named for
        // the rest of the format layer. What is here instead is built from the format's own
        // description above, which is what lets a test corrupt exactly the four bytes it is about.
        // </para>
    }
}
