using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Utils;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// <see cref="VMTS1LotLoadGuard"/> - the bounds rules on the TS1 lot-load path.
    ///
    /// <para>
    /// <c>VMTS1ActivatorNew</c> turns a retail <c>House*.iff</c> / <c>OBJM</c> save into engine
    /// state, out of a file written by some version of The Sims 1, by a house editor, or by an
    /// interrupted write. Six of the indices it derived from that file were used unchecked, so one
    /// malformed save took the whole lot down at load - which is the one moment a lot cannot be
    /// allowed to fail, because there is nothing to recover to.
    /// </para>
    ///
    /// <para>
    /// The checks are here rather than inline so they can be driven without a lot: no VM, no
    /// content manager, no graphics device, no game install. The <c>ARRY</c> case is the one with
    /// a real chunk behind it - <c>ARRY 8</c>, the fine-buildable flags plane, is the chunk a
    /// hand-built or third-party house file is most likely to be missing, and the loader
    /// null-checked the <c>ARRY 11</c> on the very next line and not this one.
    /// </para>
    /// </summary>
    public sealed class VMTS1LotLoadGuardTests
    {
        private const int PlaneLength = VMTS1LotLoadGuard.HousePlaneSize * VMTS1LotLoadGuard.HousePlaneSize;

        /// <summary>
        /// A house file with one 4x4 plane in it, marked already-read so the IFF does not try to
        /// parse bytes it was never given.
        /// </summary>
        private static IffFile HouseWithPlane(ushort id, params byte[] data)
        {
            var plane = new ARRY
            {
                ChunkID = id,
                ChunkType = "Arry",
                Width = 4,
                Height = 4,
                Type = ARRYType.RLEFloor,
                Data = data,
                ChunkProcessed = true,
            };
            var iff = new IffFile();
            iff.AddChunk(plane);
            return iff;
        }

        // ---- ARRY: a missing plane is a degraded lot, not a dead one ---------------------

        [Fact]
        public void AMissingFlagsPlaneIsAZeroedPlaneRatherThanANullReference()
        {
            // ARRY 8, the case the finding names. `iff.Get<ARRY>(8).TransposeData` threw here while
            // the line above it null-checked ARRY 11, and the lot would not open.
            var plane = VMTS1LotLoadGuard.ReadArry(new IffFile(), 8);

            Assert.Equal(PlaneLength, plane.Length);
            Assert.All(plane, b => Assert.Equal(0, b));
        }

        [Fact]
        public void NoFileAtAllIsAlsoAZeroedPlane()
        {
            var plane = VMTS1LotLoadGuard.ReadArry(null, 8);

            Assert.Equal(PlaneLength, plane.Length);
        }

        [Fact]
        public void AnOutOfRangePlaneIdIsAZeroedPlaneAndNotAnIndexOfTheIffsTable()
        {
            // Get<T> takes a ushort, so -1 and 65536 would have been the engine's problem to avoid.
            Assert.Equal(PlaneLength, VMTS1LotLoadGuard.ReadArry(new IffFile(), -1).Length);
            Assert.Equal(PlaneLength, VMTS1LotLoadGuard.ReadArry(new IffFile(), 65536).Length);
        }

        [Fact]
        public void AShortPlaneIsPaddedSoTheResizersCanReadItAt64x64()
        {
            // Every decoder downstream (VMTS1Activator.ResizeFlags and its three siblings) walks
            // its source as 64x64, so "absent" and "short" have to produce the same shape or the
            // resize pass just moves the crash. The plane's own bytes are kept, in the order the
            // decoders expect them - TransposeData is the transposed plane, not the raw one.
            var data = new byte[16];
            for (int i = 0; i < data.Length; i++) data[i] = (byte)(i + 1);
            var iff = HouseWithPlane(8, data);
            var transposed = iff.Get<ARRY>(8).TransposeData;

            var plane = VMTS1LotLoadGuard.ReadArry(iff, 8);

            Assert.Equal(PlaneLength, plane.Length);
            for (int i = 0; i < transposed.Length; i++) Assert.Equal(transposed[i], plane[i]);
            Assert.All(plane.Skip(transposed.Length), b => Assert.Equal(0, b));
        }

        [Fact]
        public void AFullSizePlaneIsPassedThroughUntouched()
        {
            // The `size >= 64` short-circuits in the resize passes return the source array as-is,
            // so padding must not quietly change a well-formed file's plane length.
            var data = new byte[PlaneLength];
            data[0] = 42;

            var plane = VMTS1LotLoadGuard.ReadArry(HouseWithPlane(6, data), 6);

            Assert.Equal(PlaneLength, plane.Length);
            Assert.Equal((byte)42, plane[0]);
        }

        // ---- the pool/water overlay ------------------------------------------------------

        [Fact]
        public void TheOverlayWalksOnlyAsFarAsItsPlanesAgree()
        {
            // ARRY 9 (pools), ARRY 10 (water) and the decoded floor array are three separate
            // chunks of one untrusted file, and the loop used to walk pools.Length over all three.
            Assert.Equal(3, VMTS1LotLoadGuard.SharedLength(new byte[3], new byte[5]));
            Assert.Equal(0, VMTS1LotLoadGuard.SharedLength(new byte[0], new byte[5]));
            Assert.Equal(4, VMTS1LotLoadGuard.SharedLength(new byte[4], new byte[4]));
            Assert.Equal(2, VMTS1LotLoadGuard.SharedLength(new byte[4], new byte[4], new byte[2]));
            Assert.Equal(4, VMTS1LotLoadGuard.SharedLength(new byte[4]));
            Assert.Equal(0, VMTS1LotLoadGuard.SharedLength());
        }

        // ---- the saved code owner (a one-based OBJT index) --------------------------------

        [Fact]
        public void OwnerTypeZeroIsTheIndexMinusOneAndIsRefused()
        {
            // The finding: `objt.Entries[frame.CodeOwnerObjType - 1]` with a 0 in the file.
            var objt = new OBJT { Entries = new List<OBJTEntry>() };
            objt.Entries.Add(Uninitialised<OBJTEntry>());

            Assert.False(VMTS1LotLoadGuard.IsOwnerTypeValid(objt, 0));
            Assert.Null(VMTS1LotLoadGuard.OwnerEntry(objt, 0));
        }

        [Fact]
        public void OwnerTypeIsOneBasedAndBoundedByTheEntryCount()
        {
            var objt = new OBJT { Entries = new List<OBJTEntry>() };
            var first = Uninitialised<OBJTEntry>();
            var second = Uninitialised<OBJTEntry>();
            objt.Entries.Add(first);
            objt.Entries.Add(second);

            Assert.True(VMTS1LotLoadGuard.IsOwnerTypeValid(objt, 1));
            Assert.True(VMTS1LotLoadGuard.IsOwnerTypeValid(objt, 2));
            Assert.False(VMTS1LotLoadGuard.IsOwnerTypeValid(objt, 3));
            Assert.Same(first, VMTS1LotLoadGuard.OwnerEntry(objt, 1));
            Assert.Same(second, VMTS1LotLoadGuard.OwnerEntry(objt, 2));
        }

        [Fact]
        public void AnUnreadObjtIsAMissNotACrash()
        {
            Assert.False(VMTS1LotLoadGuard.IsOwnerTypeValid(null, 1));
            Assert.False(VMTS1LotLoadGuard.IsOwnerTypeValid(new OBJT(), 1));
            Assert.Null(VMTS1LotLoadGuard.OwnerEntry(new OBJT(), 1));
        }

        // ---- ObjectUses.StackLength (a count, used as an index) --------------------------

        [Theory]
        [InlineData(0, 3, false)]   // the index -1
        [InlineData(-1, 3, false)]
        [InlineData(1, 3, true)]
        [InlineData(3, 3, true)]    // the last frame
        [InlineData(4, 3, false)]   // past the end of the restored stack
        public void AStackLengthMustAddressARestoredFrame(int stackLength, int stackCount, bool expected)
        {
            Assert.Equal(expected, VMTS1LotLoadGuard.IsStackSlotValid(stackLength, stackCount));
        }

        [Fact]
        public void AnEmptyStackHasNoValidUse()
        {
            Assert.False(VMTS1LotLoadGuard.IsStackSlotValid(1, 0));
        }

        // ---- a dangling TargetID ---------------------------------------------------------

        [Fact]
        public void ATargetTheSaveDoesNotContainIsAMissAndNotAKeyNotFound()
        {
            // The finding: `objm.ObjectData[action.TargetID]` on a queue entry or an object use
            // naming an object the file has no record of. The object it pointed at is gone, so
            // the entry is dropped rather than the lot refused.
            var objm = new OBJM { ObjectData = new Dictionary<int, OBJM.MappedObject>() };
            objm.ObjectData[5] = new OBJM.MappedObject(new OBJMInstance());

            Assert.True(VMTS1LotLoadGuard.TryGetInstance(objm, 5, out _));
            Assert.False(VMTS1LotLoadGuard.TryGetInstance(objm, 6, out var missing));
            Assert.False(VMTS1LotLoadGuard.TryGetInstance(null, 5, out _));
            Assert.False(VMTS1LotLoadGuard.TryGetInstance(new OBJM(), 5, out _));
            Assert.Equal(default, missing);
        }

        // ---- motive deltas ---------------------------------------------------------------

        [Theory]
        [InlineData(0, true)]
        [InlineData(15, true)]
        [InlineData(16, false)]
        [InlineData(-1, false)]
        [InlineData(17, false)]
        public void OnlyTheSixteenMotiveSlotsAreAddressable(int motive, bool expected)
        {
            Assert.Equal(expected, VMTS1LotLoadGuard.IsMotiveSlotValid(motive));
        }

        [Fact]
        public void TheMotiveSlotCountIsTheLengthOfTheArrayTheDeltasAreAppliedTo()
        {
            // ConvertAvatar builds the array at this count and then indexes it by delta.Motive, so
            // the two have to be the same number.
            Assert.Equal(16, VMTS1LotLoadGuard.MotiveSlotCount);
        }

        // ---- a restored frame's instruction ---------------------------------------------

        [Fact]
        public void AnUnresolvableRoutineOrAnOutOfRangeNodeIsNotAnInstruction()
        {
            // Both halves of the finding on `routine.Instructions[callingFrame.InstructionPointer]`:
            // the routine is whatever the content manager resolved at load time (and can be null
            // for a GLOB-less object's semi-global call), and the node id is a ushort the save chose.
            var routine = new VMRoutine { Instructions = new VMInstruction[3] };

            Assert.True(VMTS1LotLoadGuard.TryGetInstruction(routine, 0, out var first));
            Assert.True(VMTS1LotLoadGuard.TryGetInstruction(routine, 2, out _));

            Assert.False(VMTS1LotLoadGuard.TryGetInstruction(routine, 3, out var past));
            Assert.False(VMTS1LotLoadGuard.TryGetInstruction(routine, 0xFFFF, out _));
            Assert.False(VMTS1LotLoadGuard.TryGetInstruction(routine, -1, out _));
            Assert.False(VMTS1LotLoadGuard.TryGetInstruction(null, 0, out _));
            Assert.False(VMTS1LotLoadGuard.TryGetInstruction(new VMRoutine(), 0, out _));

            Assert.Same(routine.Instructions[0], first);
            Assert.Null(past);
        }

        [Fact]
        public void TheAnswerIsTheInstructionsOwnEntrySoTheFixupCanReadItsOpcode()
        {
            // The only consumer is the SpecialResult fixup in ConvertThread, which switches on
            // Opcode - so a non-null answer has to be the real instruction, not a stand-in.
            var wanted = new VMInstruction { Opcode = 30 };
            var routine = new VMRoutine { Instructions = new VMInstruction[] { new VMInstruction(), wanted } };

            Assert.True(VMTS1LotLoadGuard.TryGetInstruction(routine, 1, out var found));
            Assert.Same(wanted, found);
            Assert.Equal(30, found.Opcode);
        }

        // ---- fixed-record plane decoders -------------------------------------------------

        [Theory]
        [InlineData(0, 2, 0)]
        [InlineData(-1, 2, 0)]
        [InlineData(1, 2, 0)]     // half a record
        [InlineData(2, 2, 1)]
        [InlineData(5, 2, 2)]     // the finding: two records, and a read of data[5] out of five
        [InlineData(9, 8, 1)]
        [InlineData(10, 8, 1)]
        [InlineData(15, 14, 1)]
        [InlineData(4, 4, 1)]
        [InlineData(8, 0, 0)]     // no stride at all
        public void APlaneIsWalkedAsWholeRecordsAndNeverPastItsOwnEnd(int length, int stride, int expected)
        {
            Assert.Equal(expected, VMTS1LotLoadGuard.WholeRecords(length, stride));
        }

        [Fact]
        public void EveryRetailPlaneIsAWholeNumberOfRecordsSoNothingIsLost()
        {
            // The point of the guard is that it costs nothing on shipped data: 4096 one-byte
            // entries, 8192 for the advanced planes' two bytes per entry, 114688 for fourteen.
            foreach (var (length, stride) in new[] { (4096, 1), (8192, 2), (4096, 8), (4096, 14), (4096, 4) })
            {
                Assert.Equal(length / stride, VMTS1LotLoadGuard.WholeRecords(length, stride));
            }
        }

        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(5)]        // an odd advanced-floor plane
        [InlineData(9)]        // a nine-byte wall plane
        [InlineData(15)]       // a fifteen-byte advanced-wall plane
        [InlineData(3)]
        public void TheDecodersStopAtTheLastRecordTheyCanRead(int length)
        {
            // The defect: the output array was sized `data.Length / stride` while the loop ran to
            // `data.Length`, so a length that is not a whole multiple of the stride asked for one
            // record more than existed and then read past the end of the source.
            var data = new byte[length];
            for (int i = 0; i < data.Length; i++) data[i] = 0xFF;

            var expected = length / 2;
            var objIDs = VMTS1Activator.DecodeObjID(data);
            Assert.Equal(expected, VMTS1Activator.DecodeAdvFloors(data).Length);
            Assert.Equal(expected, objIDs.Length);
            Assert.Equal(length / 4, VMTS1Activator.DecodeHeights(data).Length);
            Assert.Equal(length / 8, VMTS1Activator.DecodeWalls(data).Length);
            Assert.Equal(length / 14, VMTS1Activator.DecodeAdvWalls(data).Length);
        }

        [Fact]
        public void APlaneDecoderGivenNothingDecodesNothingRatherThanThrowing()
        {
            // `DecodeObjID` answers null for a missing plane - its caller checks for that - and the
            // other three used to dereference the null. The house file decides.
            Assert.Empty(VMTS1Activator.DecodeAdvFloors(null));
            Assert.Empty(VMTS1Activator.DecodeWalls(null));
            Assert.Empty(VMTS1Activator.DecodeAdvWalls(null));
            Assert.Empty(VMTS1Activator.DecodeHeights(null));
            Assert.Empty(VMTS1Activator.DecodeFloors(null));
            Assert.Null(VMTS1Activator.DecodeObjID(null));
        }

        [Fact]
        public void ATruncatedAdvancedFloorPlaneStillDecodesTheRecordsItHas()
        {
            // The values, not just the count: a whole record still reads little endian, and the
            // partial tail is dropped rather than half-read.
            var data = new byte[] { 0x34, 0x12, 0x78, 0x56, 0x00 };

            var decoded = VMTS1Activator.DecodeAdvFloors(data);

            Assert.Equal(2, decoded.Length);
            Assert.Equal((ushort)0x1234, decoded[0].Pattern);
            Assert.Equal((ushort)0x5678, decoded[1].Pattern);
        }

        [Fact]
        public void FloorRemappingWalksPastTheEndOfAShorterFlagsPlane()
        {
            // ARRY 8 is padded to 4096 and the advanced-floor plane is read raw from ARRY 11, so a
            // house file can make the floor plane the longer of the two. A tile with no flag to read
            // is treated as buildable, which is what the padding already says for a missing plane,
            // and the road flip still happens - it is not a reason to stop at the shorter array.
            var floors = new[]
            {
                new FloorTile { Pattern = 10 },
                new FloorTile { Pattern = 11 },
                new FloorTile { Pattern = 30 },
            };
            var flags = new byte[] { 0, 0 };  // two entries for three tiles

            var remapped = VMTS1Activator.RemapFloors(true, floors, new Dictionary<byte, ushort>(), flags);

            Assert.Equal(3, remapped.Length);
            Assert.Equal((ushort)11, remapped[0].Pattern);   // flipped, and past the flags plane
            Assert.Equal((ushort)10, remapped[1].Pattern);   // flipped, and inside it
            Assert.Equal((ushort)30, remapped[2].Pattern);
        }

        [Fact]
        public void WallRemappingWalksPastTheEndOfAShorterFloorPlane()
        {
            // `floors` is Architecture.Floors[0], which on the advanced path comes from ARRY 11
            // rather than from a padded plane. The diagonal fallback is the only read of it, and a
            // wall with no floor behind it gets pattern 0 rather than an index off the end.
            var walls = new[]
            {
                new WallTile { Segments = WallSegments.AnyDiag },
                new WallTile { Segments = WallSegments.AnyDiag },
            };
            var floors = new[] { new FloorTile { Pattern = 40 } };

            var remapped = VMTS1Activator.RemapWalls(walls, new Dictionary<byte, ushort>(),
                new Dictionary<byte, ushort>(), floors);

            Assert.Equal(2, remapped.Length);
            Assert.Equal((ushort)40, remapped[0].TopLeftPattern);
            Assert.Equal((ushort)0, remapped[1].TopLeftPattern);
        }

        /// <summary>
        /// <c>OBJTEntry</c> only has the constructor that reads it out of a file, and none of what
        /// the guard looks at is anything that constructor would set.
        /// </summary>
        private static T Uninitialised<T>() where T : class
            => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));
    }
}
