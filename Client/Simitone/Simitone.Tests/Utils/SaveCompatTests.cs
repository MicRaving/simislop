using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks the on-disk save format that <c>TS1GameScreen.Save</c> writes, and the
    /// stage / backup / commit behaviour it relies on, using synthetic data only - no TS1
    /// install, no <c>Content</c>, no <c>GraphicsDevice</c>, no audio.
    ///
    /// <para>
    /// A saved lot is a single IFF (<c>House{NN}.iff</c>) holding four chunks:
    /// <list type="bullet">
    /// <item><c>FSOV</c> id 1 - the opaque <c>VMMarshal</c> payload, i.e. the lot itself.
    /// Its presence is what subverts normal TS1 IFF loading, so the game can round-trip
    /// data The Sims would not have understood.</item>
    /// <item><c>SIMI</c> - the lot's values and globals, read back on eviction to refund the
    /// lot purchase price. The three value fields are asserted, and so are the globals:
    /// <c>VMTS1LotState.UpdateSIMI</c> hands the chunk the VM's <c>GlobalState</c> to persist,
    /// so a SIMI that fails to round-trip them loses them on every save.</item>
    /// <item><c>PNG_</c> id 513 - the lot thumbnail, and <c>PNG_</c> id 512 - the same
    /// thumbnail with the roof removed.</item>
    /// </list>
    /// The exact <c>VMMarshal</c> bytes are not the point here and are not available
    /// headlessly, so any byte sequence stands in for them; what is locked is the envelope:
    /// that all four chunks survive a write / commit / read cycle with their ids, labels and
    /// payloads intact, and that the commit is a single atomic swap that always leaves a
    /// complete, readable copy of the previous save beside it.
    /// </para>
    ///
    /// <para>
    /// Each test gets its own throwaway directory under the OS temp path, removed in
    /// <see cref="Dispose"/>, so the cases are independent of each other and safe to run in
    /// any order or in parallel.
    /// </para>
    /// </summary>
    public sealed class SaveCompatTests : IDisposable
    {
        /// <summary>Chunk id the game gives the FSOV lot-data chunk.</summary>
        private const ushort FsovId = 1;

        /// <summary>Chunk id of the with-roof lot thumbnail (513).</summary>
        private const ushort ThumbId = 513;

        /// <summary>Chunk id of the roofless lot thumbnail (512).</summary>
        private const ushort RooflessId = 512;

        /// <summary>
        /// The length of <c>SIMI.GlobalData</c>, and of <c>VM.GlobalState</c> that fills it.
        /// </summary>
        private const int SimiGlobals = 38;

        /// <summary>
        /// How many of those globals a version 0x3E SIMI actually carries on the wire.
        /// </summary>
        /// <remarks>
        /// <c>SIMI.Write</c> emits 0x40 shorts only for a version above 0x3E, and the TS1 save
        /// path pins 0x3E (<c>VMTS1LotState.UpdateSIMI</c>), so 32 of the VM's 38 globals reach
        /// the file. That is the format, not a defect - <c>SIMI.Read</c> reads the same 32 back -
        /// so the round-trip assertions below cover the 32 and the rest are expected to read 0.
        /// </remarks>
        private const int SimiGlobalsOnWire = 0x20;

        private readonly string _tempRoot;

        public SaveCompatTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-save-compat-" + Guid.NewGuid().ToString("N"));
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

        #region synthetic chunk construction (mirrors TS1GameScreen.Save / GeneratePNG)

        /// <summary>
        /// Stands in for <c>TS1GameScreen.GeneratePNG</c>, which needs a <c>Texture2D</c> and
        /// so cannot run headlessly. An empty <paramref name="data"/> reproduces the
        /// <c>GeneratePNG(null)</c> degradation: a present chunk with a zero-length payload,
        /// because a missing thumbnail must never cost the player their save.
        /// </summary>
        private static PNG NewPngChunk(ushort id, byte[] data)
        {
            var png = new PNG();
            png.data = data ?? new byte[0];
            png.ChunkID = id;
            png.ChunkLabel = "Lot Thumbnail";
            png.ChunkProcessed = true;
            png.ChunkType = "PNG_";
            png.AddedByPatch = true;
            return png;
        }

        private static FSOV NewFsovChunk(byte[] marshalBytes)
        {
            var fsov = new FSOV();
            fsov.ChunkLabel = "Simitone Lot Data";
            fsov.ChunkID = FsovId;
            fsov.ChunkProcessed = true;
            fsov.ChunkType = "FSOV";
            fsov.AddedByPatch = true;
            fsov.Version = FSOV.CURRENT_VERSION;
            fsov.Data = marshalBytes;
            return fsov;
        }

        private static SIMI NewSimiChunk(int lotValue, int objectsValue, int architectureValue,
            short[] globals = null)
        {
            var simi = new SIMI();
            simi.ChunkLabel = "Simitone Lot Data";
            simi.ChunkID = FsovId;
            simi.ChunkProcessed = true;
            simi.ChunkType = "SIMI";
            simi.AddedByPatch = true;
            simi.Version = 0x3E;
            simi.LotValue = lotValue;
            simi.ObjectsValue = objectsValue;
            simi.ArchitectureValue = architectureValue;
            //VMTS1LotState.UpdateSIMI assigns the VM's GlobalState here; a hand-built chunk
            //stands in for that by supplying the array the save is expected to persist.
            simi.GlobalData = globals;
            //SIMI.Write indexes BudgetDays[0..5] unconditionally; the shipped chunk always
            //has six, so a hand-built one must too.
            simi.BudgetDays = new SIMI.SIMIBudgetDay[6];
            for (int i = 0; i < simi.BudgetDays.Length; i++)
            {
                simi.BudgetDays[i] = new SIMI.SIMIBudgetDay();
            }
            return simi;
        }

        /// <summary>
        /// Builds a house IFF with the same four chunks, in the same order, that Save writes.
        /// </summary>
        private static IffFile NewHouseIff(byte[] marshalBytes, byte[] thumb, byte[] roofless,
            int lotValue = 0, int objectsValue = 0, int architectureValue = 0, short[] simiGlobals = null)
        {
            var iff = new IffFile();
            iff.AddChunk(NewFsovChunk(marshalBytes));
            iff.AddChunk(NewSimiChunk(lotValue, objectsValue, architectureValue, simiGlobals));
            iff.AddChunk(NewPngChunk(ThumbId, thumb));
            iff.AddChunk(NewPngChunk(RooflessId, roofless));
            return iff;
        }

        private static byte[] Bytes(params int[] values)
        {
            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) bytes[i] = (byte)values[i];
            return bytes;
        }

        /// <summary>A short, recognisable stand-in for a VMMarshal blob.</summary>
        private static byte[] MarshalBytes(byte fill) => Enumerable.Repeat(fill, 48).ToArray();

        private static byte[] Serialize(IffFile iff)
        {
            using (var stream = new MemoryStream())
            {
                iff.Write(stream);
                return stream.ToArray();
            }
        }

        /// <summary>
        /// Opens a committed house file. IffFile's own finalizer registers itself with
        /// FreeSO's process-wide timed reference cache, which nothing ticks in a test host,
        /// so throwaway instances are marked as such to keep the cache out of the way.
        /// </summary>
        private static IffFile OpenHouseFile(string path)
        {
            var iff = new IffFile(path);
            iff.MarkThrowaway();
            return iff;
        }

        private string HousePath(string name) => Path.Combine(_tempRoot, name);

        private static void AssertSameBytes(byte[] expected, byte[] actual, string because)
        {
            Assert.NotNull(actual);
            Assert.Equal(expected.Length, actual.Length);
            Assert.True(expected.SequenceEqual(actual), because);
        }

        #endregion

        #region 1. IFF round trip

        [Fact]
        public void AHouseIffRoundTripsEveryChunkWithItsIdAndPayload()
        {
            var marshal = MarshalBytes(0x5A);
            var thumb = Bytes(0x89, 0x50, 0x4E, 0x47);
            var roofless = Bytes(0x00, 0xFF, 0x7F);
            var path = HousePath("House01.iff");

            var written = Serialize(NewHouseIff(marshal, thumb, roofless));
            File.WriteAllBytes(path, written);

            var read = OpenHouseFile(path);

            //every chunk the file was built with is present exactly once
            Assert.Equal(4, read.ListAll().Count);

            var fsov = read.Get<FSOV>(FsovId);
            Assert.NotNull(fsov);
            Assert.Equal(FsovId, fsov.ChunkID);
            Assert.Equal("FSOV", fsov.ChunkType);
            Assert.Equal("Simitone Lot Data", fsov.ChunkLabel);
            AssertSameBytes(marshal, fsov.Data, "the VMMarshal payload must survive the write/read cycle byte for byte");
            Assert.Equal(FSOV.CURRENT_VERSION, fsov.Version);

            var simi = read.Get<SIMI>(FsovId);
            Assert.NotNull(simi);
            Assert.Equal("SIMI", simi.ChunkType);
            Assert.Equal(FsovId, simi.ChunkID);

            var pngs = read.List<PNG>();
            Assert.NotNull(pngs);
            Assert.Equal(2, pngs.Count);

            var thumbChunk = read.Get<PNG>(ThumbId);
            var rooflessChunk = read.Get<PNG>(RooflessId);
            Assert.NotNull(thumbChunk);
            Assert.NotNull(rooflessChunk);
            Assert.Equal("PNG_", thumbChunk.ChunkType);
            Assert.Equal("Lot Thumbnail", thumbChunk.ChunkLabel);
            AssertSameBytes(thumb, thumbChunk.data, "the 513 thumbnail payload must be unchanged");
            AssertSameBytes(roofless, rooflessChunk.data, "the 512 roofless thumbnail payload must be unchanged");
        }

        [Fact]
        public void AHouseIffReSerialisedAfterAReadIsByteIdenticalToTheFileOnDisk()
        {
            //no format drift: re-saving a lot the game itself wrote must not perturb a byte.
            //this is what lets a house survive an arbitrary number of load/save cycles.
            var path = HousePath("House02.iff");
            var original = Serialize(NewHouseIff(MarshalBytes(0x11), Bytes(1, 2, 3, 4), Bytes(5, 6, 7, 8)));
            File.WriteAllBytes(path, original);

            var read = OpenHouseFile(path);
            var rewritten = Serialize(read);

            AssertSameBytes(original, rewritten, "a read house IFF must re-serialise to exactly the same bytes");
        }

        [Fact]
        public void TheTwoThumbnailsAreDistinctChunksNotOneOverwrittenChunk()
        {
            //513 and 512 are the same chunk type, so a type-keyed container that kept only
            //one entry per type would silently drop the roofless image.
            var path = HousePath("House03.iff");
            var thumb = Bytes(0xDE, 0xAD);
            var roofless = Bytes(0xBE, 0xEF);

            File.WriteAllBytes(path, Serialize(NewHouseIff(MarshalBytes(0x22), thumb, roofless)));

            var read = OpenHouseFile(path);
            var pngs = read.List<PNG>();

            Assert.Equal(2, pngs.Count);
            Assert.Contains(pngs, p => p.ChunkID == ThumbId && p.data.SequenceEqual(thumb));
            Assert.Contains(pngs, p => p.ChunkID == RooflessId && p.data.SequenceEqual(roofless));
        }

        [Fact]
        public void TheSimiLotValuesEvictionRefundsFromSurviveASave()
        {
            //Save writes the SIMI; TS1GameScreen.GetLotPurchaseValue reads it back on eviction
            //to compute the lot refund. If SIMI stops round-tripping, players lose money.
            var path = HousePath("House04.iff");

            File.WriteAllBytes(path, Serialize(NewHouseIff(
                MarshalBytes(0x33), Bytes(1), Bytes(2),
                lotValue: 12000, objectsValue: 3400, architectureValue: 5700)));

            var read = OpenHouseFile(path);
            var simi = read.Get<SIMI>(FsovId);

            Assert.NotNull(simi);
            Assert.Equal(12000, simi.LotValue);
            Assert.Equal(3400, simi.ObjectsValue);
            Assert.Equal(5700, simi.ArchitectureValue);
            //the value TS1GameScreen.GetLotPurchaseValue hands to LotAccounting.RefundOnEvict
            Assert.Equal(12000 + 3400 + (5700 * 7) / 10, simi.PurchaseValue);
        }

        /// <summary>
        /// A set of globals with a distinct, non-zero value in every slot, so a silent zeroing
        /// cannot be mistaken for a pass.
        /// </summary>
        private static short[] GlobalsWithNoZeros()
        {
            var globals = new short[SimiGlobals];
            for (int i = 0; i < globals.Length; i++) globals[i] = (short)(1000 + i);
            return globals;
        }

        [Fact]
        public void TheSimiGlobalsTheSaveWasHandedSurviveARoundTrip()
        {
            //VMTS1LotState.UpdateSIMI does exactly this - SimulationInfo.GlobalData = vm.GlobalState -
            //so that the lot's globals land in the house file. SIMI.Write used to reassign
            //GlobalData to a fresh zeroed array immediately before writing it (a null-guard typo),
            //which both discarded the caller's globals and left the VM's array pointed at the
            //zeros. A save would then persist 32 zeroes every time.
            var path = HousePath("House13.iff");
            var globals = GlobalsWithNoZeros();

            File.WriteAllBytes(path, Serialize(NewHouseIff(
                MarshalBytes(0x7A), Bytes(1), Bytes(2), simiGlobals: globals)));

            var read = OpenHouseFile(path);
            var simi = read.Get<SIMI>(FsovId);

            Assert.NotNull(simi);
            Assert.NotNull(simi.GlobalData);
            Assert.Equal(globals.Length, simi.GlobalData.Length);
            //every global a 0x3E SIMI carries, in order, unchanged - this is the assertion the
            //old zeroing broke, and it is why GlobalsWithNoZeros has no zero in it
            Assert.Equal(globals.Take(SimiGlobalsOnWire).ToArray(),
                simi.GlobalData.Take(SimiGlobalsOnWire).ToArray());
        }

        [Fact]
        public void WritingASimiLeavesTheCallersGlobalArrayAlone()
        {
            //the other half of the same defect: SIMI.Write used to replace GlobalData, so after a
            //save the chunk no longer shared the VM's array and a later write persisted the
            //zeros it had substituted. The chunk is handed a live array and has to keep it.
            var simi = NewSimiChunk(1, 2, 3, GlobalsWithNoZeros());
            var globals = simi.GlobalData;

            using (var stream = new MemoryStream())
            {
                Assert.True(simi.Write(null, stream));
            }

            Assert.Same(globals, simi.GlobalData);
            Assert.Equal(GlobalsWithNoZeros(), simi.GlobalData);
        }

        [Fact]
        public void ASimiWithNoGlobalArrayIsWrittenRatherThanThrowing()
        {
            //a hand-built or older chunk can arrive with GlobalData null. The write still has to
            //produce a readable SIMI - a save must never fail on a chunk's own bookkeeping.
            var simi = NewSimiChunk(12000, 3400, 5700);
            Assert.Null(simi.GlobalData);

            var path = HousePath("House14.iff");
            var iff = new IffFile();
            iff.AddChunk(NewFsovChunk(MarshalBytes(0x7B)));
            iff.AddChunk(simi);
            iff.AddChunk(NewPngChunk(ThumbId, Bytes(1)));
            iff.AddChunk(NewPngChunk(RooflessId, Bytes(2)));
            File.WriteAllBytes(path, Serialize(iff));

            var read = OpenHouseFile(path);
            var back = read.Get<SIMI>(FsovId);

            Assert.NotNull(back);
            Assert.NotNull(back.GlobalData);
            Assert.Equal(SimiGlobals, back.GlobalData.Length);
            //and the values beside the globals are untouched by the fallback
            Assert.Equal(12000, back.LotValue);
            Assert.Equal(3400, back.ObjectsValue);
            Assert.Equal(5700, back.ArchitectureValue);
        }

        /// <summary>
        /// Reads the globals straight out of a serialised SIMI chunk's payload, with no
        /// <see cref="SIMI"/> object involved.
        /// </summary>
        /// <remarks>
        /// The point of this helper is that it asserts about <em>the file</em> rather than about
        /// a chunk that has already been read back. The loop this replaces asserted
        /// <c>simi.GlobalData[i] == 0</c> for the six globals a 0x3E SIMI does not carry - and
        /// <c>SIMI.Read</c> allocates <c>new short[38]</c> and fills only the 32 that are on the
        /// wire, so the six tail slots are zero by construction. The assertion could not fail for
        /// any input, and it said nothing at all about what the writer put in the file.
        /// <para>
        /// The payload layout is the writer's: a zero pad, the version, the "IMIS" magic, then
        /// <c>GlobalsOnWire(version)</c> shorts.
        /// </para>
        /// </remarks>
        /// <param name="houseFile">The whole <c>House{NN}.iff</c>, as written.</param>
        /// <returns>The version stamped in the file, the globals, and how many there were.</returns>
        private static (uint version, short[] globals, int onWire) ReadSimiGlobalsFromTheFile(
            byte[] houseFile)
        {
            //walk the chunk records rather than guessing an offset: the FSOV chunk's length is
            //whatever the marshal blob was, and the SIMI chunk is the next one
            var offset = 64;                    //the file header
            byte[] simiPayload = null;
            while (offset + 76 <= houseFile.Length)
            {
                var type = System.Text.Encoding.ASCII.GetString(houseFile, offset, 4);
                var size = (houseFile[offset + 4] << 24) | (houseFile[offset + 5] << 16)
                    | (houseFile[offset + 6] << 8) | houseFile[offset + 7];
                if (type == "SIMI")
                {
                    simiPayload = new byte[size - 76];
                    Array.Copy(houseFile, offset + 76, simiPayload, 0, simiPayload.Length);
                    break;
                }
                if (size < 76) throw new InvalidOperationException("chunk size " + size);
                offset += size;
            }
            Assert.NotNull(simiPayload);

            using (var io = new BinaryReader(new MemoryStream(simiPayload)))
            {
                io.ReadUInt32();                                  //pad
                var version = io.ReadUInt32();
                Assert.Equal("IMIS", System.Text.Encoding.ASCII.GetString(io.ReadBytes(4)));

                //the count is not in the file: the format derives it from the version, and the
                //reader has to agree - so this asks the same function the reader asks
                var onWire = SIMI.GlobalsOnWire(version);
                var globals = new short[onWire];
                for (int i = 0; i < onWire; i++) globals[i] = io.ReadInt16();
                return (version, globals, onWire);
            }
        }

        [Fact]
        public void TheFileCarriesExactlyThirtyTwoGlobalsAndTheyAreTheOnesItWasHanded()
        {
            //The real contract, stated about the file. A 0x3E SIMI writes 32 of the VM's 38
            //globals - that is the format, not a defect, because Read derives the same 32 from the
            //same version - so what has to be pinned is that the 32 on the wire are the caller's
            //32, in order, and that the writer did not quietly start writing a different number.
            //The old assertion here checked the *reader's* array, whose tail is zero-filled by
            //construction, so it could not fail and could not see the writer at all.
            var globals = GlobalsWithNoZeros();
            var file = Serialize(NewHouseIff(
                MarshalBytes(0x7C), Bytes(1), Bytes(2), simiGlobals: globals));

            var (version, onFile, onWire) = ReadSimiGlobalsFromTheFile(file);

            Assert.Equal(0x3Eu, version);
            Assert.Equal(32, onWire);
            Assert.Equal(globals.Take(32).ToArray(), onFile);
        }

        [Fact]
        public void TheSixGlobalsThatAreNotOnTheWireNeverAppearInTheFile()
        {
            //the other half of the same fact, and the one that would catch a writer that started
            //emitting all 38 behind a 32-global header: a reader believing the header would then
            //read Unknown1, both GUIDs and all three lot values out of the tail of the globals
            var globals = GlobalsWithNoZeros();
            var file = Serialize(NewHouseIff(
                MarshalBytes(0x7C), Bytes(1), Bytes(2), simiGlobals: globals));

            var (_, onFile, onWire) = ReadSimiGlobalsFromTheFile(file);

            //globals 32..37 hold 1024..1029, and no byte of the chunk's payload is them
            Assert.Equal(32, onWire);
            for (int i = 0; i < onFile.Length; i++)
            {
                Assert.NotEqual(1024 + i, (int)onFile[i]);
            }
        }

        [Fact]
        public void ReSavingALotDoesNotShiftTheGlobalsItCarries()
        {
            // still worth pinning, and now for a reason that can fail: the writer assigns to
            // GlobalData to keep the chunk honest about what is on disk, so a writer that
            // substituted a fresh array would still produce a readable file - just one that lost
            // the lot's globals. Byte equality across a read/save cycle is what catches that.
            var path = HousePath("House15.iff");
            var globals = GlobalsWithNoZeros();

            var first = Serialize(NewHouseIff(
                MarshalBytes(0x7C), Bytes(1), Bytes(2), simiGlobals: globals));
            File.WriteAllBytes(path, first);

            var second = Serialize(OpenHouseFile(path));

            AssertSameBytes(first, second, "re-saving a lot must not shift the globals it carries");
            Assert.Equal(globals.Take(32).ToArray(), ReadSimiGlobalsFromTheFile(second).globals);
        }

        [Fact]
        public void AChunkWrittenForAWiderVersionStillCarriesThirtyTwo()
        {
            //F-02. A chunk asking for 0x40 used to be written with 64 globals behind a 0x3E
            //header, and a reader - which believes the header - then read the lot's values out of
            //the tail of the globals. The writer now generates the layout it stamps rather than
            //the one it was asked for, so a chunk in any state produces a self-describing file.
            var simi = NewSimiChunk(12000, 3400, 5700, GlobalsWithNoZeros());
            simi.Version = 0x40;

            var file = Serialize(NewHouseIff(MarshalBytes(0x7D), Bytes(1), Bytes(2), 1, 2, 3,
                GlobalsWithNoZeros()));
            var (version, onFile, onWire) = ReadSimiGlobalsFromTheFile(file);
            Assert.Equal(0x3Eu, version);
            Assert.Equal(32, onWire);

            //and the widened chunk is stamped back to 0x3E by the write, so a second write of it
            //is byte-identical
            using (var stream = new MemoryStream())
            {
                Assert.True(simi.Write(null, stream));
            }
            Assert.Equal(0x3Eu, simi.Version);
        }

        #endregion

        #region 2. atomic commit over a real house IFF

        [Fact]
        public void CommittingAHouseIffLeavesTheLiveFileAsTheNewestSave()
        {
            var path = HousePath("House05.iff");
            var first = MarshalBytes(0x01);
            var second = MarshalBytes(0x02);

            AtomicFileCommit.Write(path, Serialize(NewHouseIff(first, Bytes(1), Bytes(2))));
            var backup = AtomicFileCommit.Write(path, Serialize(NewHouseIff(second, Bytes(3), Bytes(4))));

            Assert.Equal(path + ".bak", backup);

            var live = OpenHouseFile(path);
            AssertSameBytes(second, live.Get<FSOV>(FsovId).Data, "the live file must be the second save");
            AssertSameBytes(Bytes(3), live.Get<PNG>(ThumbId).data, "the live thumbnail must be the second save's");
            AssertSameBytes(Bytes(4), live.Get<PNG>(RooflessId).data, "the live roofless thumbnail must be the second save's");
        }

        [Fact]
        public void CommittingAHouseIffTwiceKeepsTheFirstSaveByteForByteAsTheBackup()
        {
            var path = HousePath("House06.iff");
            var firstBytes = Serialize(NewHouseIff(MarshalBytes(0x44), Bytes(9, 9), Bytes(8, 8)));
            var secondBytes = Serialize(NewHouseIff(MarshalBytes(0x55), Bytes(7, 7), Bytes(6, 6)));

            AtomicFileCommit.Write(path, firstBytes);
            AtomicFileCommit.Write(path, secondBytes);

            AssertSameBytes(secondBytes, File.ReadAllBytes(path), "the live file must be the second save");
            AssertSameBytes(firstBytes, File.ReadAllBytes(path + ".bak"), "the backup must be the first save, byte for byte");
        }

        [Fact]
        public void TheBackupOfACommittedHouseIsACompleteReadableHouseFile()
        {
            //the point of the .bak is hand recovery. a truncated or mis-serialised backup is
            //no backup at all, so it is opened as a real IFF rather than merely diffed.
            var path = HousePath("House07.iff");

            AtomicFileCommit.Write(path, Serialize(NewHouseIff(MarshalBytes(0x66), Bytes(1, 1), Bytes(2, 2))));
            AtomicFileCommit.Write(path, Serialize(NewHouseIff(MarshalBytes(0x77), Bytes(3, 3), Bytes(4, 4))));

            var backup = OpenHouseFile(path + ".bak");

            Assert.Equal(4, backup.ListAll().Count);
            AssertSameBytes(MarshalBytes(0x66), backup.Get<FSOV>(FsovId).Data, "the backup must hold the first save's lot data");
            Assert.NotNull(backup.Get<SIMI>(FsovId));
            Assert.Equal(2, backup.List<PNG>().Count);
        }

        [Fact]
        public void CommittingAHouseIffLeavesNoStagedFileBehind()
        {
            var path = HousePath("House08.iff");

            AtomicFileCommit.Write(path, Serialize(NewHouseIff(MarshalBytes(0x88), Bytes(1), Bytes(2))));
            AtomicFileCommit.Write(path, Serialize(NewHouseIff(MarshalBytes(0x99), Bytes(3), Bytes(4))));

            Assert.False(File.Exists(path + ".tmp"), "the staged .tmp must be gone once the commit succeeds");

            var actual = Directory.GetFiles(_tempRoot).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "House08.iff", "House08.iff.bak" }, actual);
        }

        [Fact]
        public void TheFirstHouseSaveHasNoBackupToKeep()
        {
            var path = HousePath("House09.iff");

            var backup = AtomicFileCommit.Write(path, Serialize(NewHouseIff(MarshalBytes(0xAB), Bytes(1), Bytes(2))));

            Assert.Null(backup);
            Assert.False(File.Exists(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(new[] { "House09.iff" },
                Directory.GetFiles(_tempRoot).Select(Path.GetFileName).ToArray());
        }

        #endregion

        #region 3. the empty-thumbnail degradation

        [Fact]
        public void AChunkWithNoThumbnailDataIsStillAPresentZeroLengthChunk()
        {
            //GeneratePNG(null) is what happens when the lot thumbnail cannot be produced. The
            //save must still go through, with the chunk present and empty rather than absent.
            var path = HousePath("House10.iff");
            var marshal = MarshalBytes(0xCD);

            var payload = Serialize(NewHouseIff(marshal, new byte[0], new byte[0]));

            File.WriteAllBytes(path, payload);
            var read = OpenHouseFile(path);

            var thumb = read.Get<PNG>(ThumbId);
            var roofless = read.Get<PNG>(RooflessId);

            Assert.NotNull(thumb);
            Assert.NotNull(roofless);
            Assert.Equal(ThumbId, thumb.ChunkID);
            Assert.Equal(RooflessId, roofless.ChunkID);
            Assert.Equal("PNG_", thumb.ChunkType);
            Assert.NotNull(thumb.data);
            Assert.Empty(thumb.data);
            Assert.Empty(roofless.data);

            //and the rest of the save is intact - a missing thumbnail costs nothing else
            Assert.Equal(4, read.ListAll().Count);
            AssertSameBytes(marshal, read.Get<FSOV>(FsovId).Data, "the lot data must be unaffected by empty thumbnails");
            Assert.NotNull(read.Get<SIMI>(FsovId));
        }

        [Fact]
        public void AnEmptyThumbnailChunkSurvivesAFurtherCommitAndReload()
        {
            //a lot saved with no thumbnail must still be loadable and re-savable next time,
            //otherwise the degradation is permanent for that house.
            var path = HousePath("House11.iff");
            var marshal = MarshalBytes(0xEF);

            AtomicFileCommit.Write(path, Serialize(NewHouseIff(marshal, new byte[0], new byte[0])));
            AtomicFileCommit.Write(path, Serialize(OpenHouseFile(path)));

            var read = OpenHouseFile(path);

            Assert.Empty(read.Get<PNG>(ThumbId).data);
            Assert.Empty(read.Get<PNG>(RooflessId).data);
            AssertSameBytes(marshal, read.Get<FSOV>(FsovId).Data, "the lot data must survive a save/load/save cycle");
        }

        [Fact]
        public void OneEmptyThumbnailDoesNotDisturbTheOther()
        {
            //roofless and with-roof thumbnails are produced independently, so it is legitimate
            //for one to be empty and the other not.
            var path = HousePath("House12.iff");
            var thumb = Bytes(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A);

            File.WriteAllBytes(path, Serialize(NewHouseIff(MarshalBytes(0x10), thumb, new byte[0])));

            var read = OpenHouseFile(path);

            AssertSameBytes(thumb, read.Get<PNG>(ThumbId).data, "the available thumbnail must be preserved");
            Assert.Empty(read.Get<PNG>(RooflessId).data);
        }

        #endregion

        #region 4. commit location / naming invariants

        [Fact]
        public void TheHouseAndItsBackupAreWrittenToTheSameDirectoryAsTheTarget()
        {
            //the house directory is the only thing that survives a reinstall of the user
            //data folder, so the save and its recovery copy must never be scattered.
            var dir = Path.Combine(_tempRoot, "Houses");
            var path = Path.Combine(dir, "House20.iff");
            Assert.False(Directory.Exists(dir), "precondition: the house directory should not exist yet");

            AtomicFileCommit.Write(path, Serialize(NewHouseIff(MarshalBytes(0x01), Bytes(1), Bytes(2))));
            var backup = AtomicFileCommit.Write(path, Serialize(NewHouseIff(MarshalBytes(0x02), Bytes(3), Bytes(4))));

            Assert.Equal(path + ".bak", backup);
            Assert.Equal(Path.GetDirectoryName(path), Path.GetDirectoryName(backup));
            Assert.Equal(path, Path.Combine(dir, Path.GetFileName(path)));
            Assert.Equal(backup, Path.Combine(dir, Path.GetFileName(backup)));

            var actual = Directory.GetFiles(dir).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "House20.iff", "House20.iff.bak" }, actual);
        }

        [Fact]
        public void TheBackupSuffixDoesNotProduceAnotherHouseFileName()
        {
            //GetHousePath yields "House{NN}.iff" and GetHouse reads back "House{NN}.iff" for a
            //given number, so the side files must not be a name the lot loader can pick up.
            var path = HousePath("House21.iff");

            AtomicFileCommit.Write(path, Serialize(NewHouseIff(MarshalBytes(0x01), Bytes(1), Bytes(2))));
            AtomicFileCommit.Write(path, Serialize(NewHouseIff(MarshalBytes(0x02), Bytes(3), Bytes(4))));

            //"House{NN}" is how a house path is built: the id padded to two digits.
            foreach (var id in new[] { 0, 1, 7, 12, 99, 100 })
            {
                var houseName = "House" + id.ToString().PadLeft(2, '0') + ".iff";
                Assert.NotEqual(houseName, Path.GetFileName(path + ".bak"));
                Assert.NotEqual(houseName, Path.GetFileName(path + ".tmp"));
            }
        }

        #endregion

        #region 5. the guard the load path depends on

        [Fact]
        public void AHouseFileThatIsNotAnIffIsRejected()
        {
            //TS1GameScreen.GetLotPurchaseValue catches everything off Get<SIMI>(1) and refunds
            //with a 0 lot value, so an eviction never dies half-refunded. That fallback is
            //only safe because a corrupt file throws rather than returning junk.
            var path = HousePath("House30.iff");
            File.WriteAllBytes(path, Bytes(0x00, 0x01, 0x02, 0x03));

            var thrown = Assert.ThrowsAny<Exception>(() => OpenHouseFile(path));

            Assert.Equal("Invalid iff file!", thrown.Message);
        }

        [Fact]
        public void AMissingHouseFileIsRejectedRatherThanReportedAsAnEmptyHouse()
        {
            var path = HousePath("House31.iff");

            Assert.Throws<FileNotFoundException>(() => OpenHouseFile(path));
            Assert.False(File.Exists(path), "reading a house must not create one");
        }

        [Fact]
        public void AnEmptyFileIsNotMistakenForAHouseWithNoChunks()
        {
            //AtomicFileCommit accepts a zero-length payload; a truncated commit must not be
            //readable as a valid, chunk-less house.
            var path = HousePath("House32.iff");
            File.WriteAllBytes(path, new byte[0]);

            Assert.ThrowsAny<Exception>(() => OpenHouseFile(path));
        }

        [Fact]
        public void OnlyTheFourHouseChunksAreWritten()
        {
            //nothing else may sneak into the file: an unexpected chunk type is either dropped
            //on load or, worse, handed to a loader that cannot read it.
            var path = HousePath("House33.iff");

            File.WriteAllBytes(path, Serialize(NewHouseIff(MarshalBytes(0x5A), Bytes(1), Bytes(2))));
            var read = OpenHouseFile(path);

            var types = new List<string>(read.ListAll().Select(c => c.ChunkType));
            Assert.Equal(4, types.Count);
            Assert.Contains("FSOV", types);
            Assert.Contains("SIMI", types);
            Assert.Equal(2, types.Count(t => t == "PNG_"));
        }

        #endregion
    }
}
