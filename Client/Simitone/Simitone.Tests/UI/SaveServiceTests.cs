using System;
using System.IO;
using System.Linq;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView;
using FSO.SimAntics.Marshals;
using Simitone.Client.UI.Services;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the two pure pieces of <see cref="SaveService"/>: the save guard, and the
    /// construction of the <c>House{NN}.iff</c> payload.
    ///
    /// <para>
    /// <b>Why these and not the save itself.</b> <c>SaveService.Save</c> needs a live VM, a live
    /// world and a <c>GraphicsDevice</c> (it renders the lot thumbnail), so it cannot run here.
    /// Everything that decides <em>what</em> the save contains and <em>in what order</em> is
    /// separated out precisely so it can: <see cref="SaveService.BuildPngChunk"/> wraps thumbnail
    /// bytes, and <see cref="SaveService.BuildHouseFileBytes"/> lays out the four chunks. The bytes
    /// are asserted by writing the built file to a throwaway directory and reading it back with
    /// <see cref="IffFile"/>, so a reordering or a wrong chunk id shows up as a failed read rather
    /// than as a save the game can no longer load.
    /// </para>
    ///
    /// <para>
    /// The other invariant worth pinning is the one a "tidy up the null check" pass would break:
    /// a save with no VM, world or family must do nothing at all rather than half-attempt. That is
    /// what <see cref="SaveService.HasSaveState"/> gates, and it is reachable headlessly by passing
    /// nulls - a <c>World</c> cannot be constructed without a device, so the "all present" case is
    /// left to a playtest and the "anything missing" cases are pinned here.
    /// </para>
    ///
    /// <para>
    /// Synthetic data only: no TS1 install, no <c>Content</c>, no <c>GraphicsDevice</c>, no audio,
    /// no VM. <see cref="Simitone.Client.Utils.SitoneLog"/>'s never-throws behaviour is what lets
    /// the guard log safely from a host with no console.
    /// </para>
    /// </summary>
    public sealed class SaveServiceTests : IDisposable
    {
        /// <summary>Chunk id the game gives the FSOV lot-data chunk (and the SIMI chunk).</summary>
        private const ushort FsovId = 1;

        /// <summary>Chunk id of the with-roof lot thumbnail.</summary>
        private const ushort ThumbId = 513;

        /// <summary>Chunk id of the roofless lot thumbnail.</summary>
        private const ushort RooflessId = 512;

        private readonly string _tempRoot;

        public SaveServiceTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-save-service-" + Guid.NewGuid().ToString("N"));
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

        #region helpers

        private static byte[] Bytes(params int[] values)
        {
            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) bytes[i] = (byte)values[i];
            return bytes;
        }

        /// <summary>A short, recognisable stand-in for a VMMarshal blob.</summary>
        private static byte[] MarshalBytes(byte fill) => Enumerable.Repeat(fill, 48).ToArray();

        private static SIMI NewSimiChunk()
        {
            var simi = new SIMI();
            simi.ChunkLabel = "Simitone Lot Data";
            simi.ChunkID = FsovId;
            simi.ChunkProcessed = true;
            simi.ChunkType = "SIMI";
            simi.AddedByPatch = true;
            simi.Version = 0x3E;
            //SIMI.Write indexes BudgetDays[0..5] unconditionally; the shipped chunk always has
            //six, and the chunk the VM hands SaveService always does too. A hand-built stand-in has
            //to match or the write throws.
            simi.BudgetDays = new SIMI.SIMIBudgetDay[6];
            for (int i = 0; i < simi.BudgetDays.Length; i++)
            {
                simi.BudgetDays[i] = new SIMI.SIMIBudgetDay();
            }
            return simi;
        }

        /// <summary>
        /// Writes a built house file to a throwaway path and reads it back.
        /// IffFile's own finalizer registers itself with FreeSO's process-wide timed reference
        /// cache, which nothing ticks in a test host, so the throwaway instance is marked as such.
        /// </summary>
        private IffFile RoundTrip(byte[] houseBytes, string name)
        {
            var path = Path.Combine(_tempRoot, name);
            File.WriteAllBytes(path, houseBytes);

            var iff = new IffFile(path);
            iff.MarkThrowaway();
            return iff;
        }

        #endregion

        // ------------------------------------------------------------------- HasSaveState

        [Fact]
        public void HasSaveState_IsFalse_WithNoLotLoadedAtAll()
        {
            // what Save() hits when the player quits from the neighbourhood screen, or when a lot
            // failed to load: it must log and return, not touch the neighbourhood.
            Assert.False(SaveService.HasSaveState(null, null, null));
        }

        [Fact]
        public void HasSaveState_IsFalse_WithoutAFamily()
        {
            // a family is required: there is no neighbourhood purchase or SIMI to write without one.
            Assert.False(SaveService.HasSaveState(null, null, new FAMI()));
        }

        /// <summary>
        /// A <see cref="World"/> or a <see cref="VM"/> that is non-null without its constructor
        /// having run.
        /// </summary>
        /// <remarks>
        /// <c>HasSaveState</c> is a conjunction of three null checks and nothing else, and the
        /// other two of them are reachable - a <see cref="FAMI"/> is a plain data class. The
        /// <see cref="World"/> is the one that is not: it cannot exist headlessly, because its
        /// constructor wants a <c>GraphicsDevice</c>. That is the whole reason the two tests this
        /// replaced could only ever pass nulls and so could not observe the loss of a term.
        /// <para>
        /// A guard that only null-checks never dereferences these, so an instance with no field
        /// initialised is exactly as good as a real one here - and it is enough to drive the full
        /// truth table. If <c>HasSaveState</c> ever grew a check that actually reads the
        /// collaborators, this would fail loudly rather than quietly.
        /// </para>
        /// </remarks>
        private static T Present<T>() where T : class
            => (T)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(T));

        /// <summary>
        /// The guard is a plain conjunction over its three arguments - every combination, not
        /// only the all-null one.
        /// </summary>
        /// <remarks>
        /// The two tests this replaces asserted <c>HasSaveState(null, null, null)</c> is false,
        /// which is a byte-for-byte duplicate of the test above and can only ever detect the loss
        /// of the <i>first</i> term. The eight rows here detect the loss of any of them: drop
        /// <c>vm != null</c> and the third row goes true; drop <c>world != null</c> and the fifth
        /// does; drop <c>family != null</c> and the seventh does. A conjunction that quietly grew
        /// an <c>||</c> fails the same way.
        /// </remarks>
        [Theory]
        [InlineData(true, true, true, true)]
        [InlineData(true, true, false, false)]
        [InlineData(true, false, true, false)]
        [InlineData(true, false, false, false)]
        [InlineData(false, true, true, false)]
        [InlineData(false, true, false, false)]
        [InlineData(false, false, true, false)]
        [InlineData(false, false, false, false)]
        public void HasSaveState_IsTheConjunctionOfItsThreeArguments(
            bool hasVm, bool hasWorld, bool hasFamily, bool expected)
        {
            var answer = SaveService.HasSaveState(
                hasVm ? Present<FSO.SimAntics.VM>() : null,
                hasWorld ? Present<World>() : null,
                hasFamily ? new FAMI() : null);

            Assert.Equal(expected, answer);
        }

        [Fact]
        public void HasSaveState_AnswersFromItsArgumentsRatherThanFromAnyCachedAnswer()
        {
            // The screen loads lots in and out all session, so a "have I been initialised" flag
            // would go stale: the second call here is the same guard with one argument gone and
            // has to answer differently, from the arguments and nothing else.
            var vm = Present<FSO.SimAntics.VM>();
            var world = Present<World>();
            var family = new FAMI();

            Assert.True(SaveService.HasSaveState(vm, world, family));

            //and back again, which is what loading a second lot looks like
            Assert.False(SaveService.HasSaveState(vm, world, null));
            Assert.True(SaveService.HasSaveState(vm, world, new FAMI()));
        }

        // -------------------------------------------------------------------- BuildPngChunk

        [Fact]
        public void BuildPngChunk_MarksTheChunkAsALotThumbnail()
        {
            var png = SaveService.BuildPngChunk(Bytes(1, 2, 3));

            Assert.Equal("Lot Thumbnail", png.ChunkLabel);
            Assert.Equal("PNG_", png.ChunkType);
            Assert.True(png.ChunkProcessed);
            Assert.True(png.AddedByPatch);
        }

        [Fact]
        public void BuildPngChunk_KeepsTheEncodedBytesVerbatim()
        {
            var bytes = Bytes(0x89, 0x50, 0x4E, 0x47, 0x0D);

            var png = SaveService.BuildPngChunk(bytes);

            Assert.Equal(bytes, png.data);
        }

        [Fact]
        public void BuildPngChunk_TurnsANullTextureIntoAnEmptyChunkRatherThanThrowing()
        {
            // the degradation the roofless-thumbnail path relies on: GetLotThumb's callback can
            // leave the roofless texture null, and a missing thumbnail must never cost the player
            // their save.
            var png = SaveService.BuildPngChunk(null);

            Assert.NotNull(png.data);
            Assert.Empty(png.data);
        }

        [Fact]
        public void BuildPngChunk_TreatsAnEmptyPayloadTheSameAsAMissingOne()
        {
            var png = SaveService.BuildPngChunk(new byte[0]);

            Assert.NotNull(png.data);
            Assert.Empty(png.data);
        }

        [Fact]
        public void BuildPngChunk_LeavesTheChunkIdToTheCaller()
        {
            // 513 and 512 mean "with roof" and "roofless"; deciding that here would make it
            // impossible to tell which of the two was being built.
            Assert.Equal(0, SaveService.BuildPngChunk(Bytes(1)).ChunkID);
        }

        // --------------------------------------------------------------- BuildHouseFileBytes

        [Fact]
        public void BuildHouseFileBytes_ProducesExactlyFourChunksInTheOrderTheFileIsRead()
        {
            var bytes = SaveService.BuildHouseFileBytes(
                MarshalBytes(0x5A), NewSimiChunk(),
                SaveService.BuildPngChunk(Bytes(0x89, 0x50)),
                SaveService.BuildPngChunk(Bytes(0x00, 0xFF)));

            var iff = RoundTrip(bytes, "House01.iff");
            var chunks = iff.ListAll();

            Assert.Equal(4, chunks.Count);
            Assert.Equal("FSOV", chunks[0].ChunkType);
            Assert.Equal("SIMI", chunks[1].ChunkType);
            Assert.Equal("PNG_", chunks[2].ChunkType);
            Assert.Equal("PNG_", chunks[3].ChunkType);
        }

        [Fact]
        public void BuildHouseFileBytes_GivesTheWithRoofThumbnailId513AndTheRooflessOneId512()
        {
            // the two thumbnails are the same picture with and without the roof; the game picks
            // between them purely on this id.
            var bytes = SaveService.BuildHouseFileBytes(
                MarshalBytes(0x5A), NewSimiChunk(),
                SaveService.BuildPngChunk(Bytes(0x11)),
                SaveService.BuildPngChunk(Bytes(0x22)));

            var iff = RoundTrip(bytes, "House02.iff");

            Assert.Equal(ThumbId, iff.ListAll()[2].ChunkID);
            Assert.Equal(RooflessId, iff.ListAll()[3].ChunkID);
        }

        [Fact]
        public void BuildHouseFileBytes_RoundTripsTheMarshalPayloadByteForByte()
        {
            // the FSOV chunk is the opaque VMMarshal: the whole lot. If its bytes came out
            // reordered, truncated or wrapped, the lot would come back subtly wrong.
            var marshal = MarshalBytes(0x5A);

            var bytes = SaveService.BuildHouseFileBytes(
                marshal, NewSimiChunk(),
                SaveService.BuildPngChunk(Bytes(0x11)),
                SaveService.BuildPngChunk(Bytes(0x22)));

            var fsov = RoundTrip(bytes, "House03.iff").Get<FSOV>(FsovId);

            Assert.NotNull(fsov);
            Assert.Equal(marshal, fsov.Data);
        }

        [Fact]
        public void BuildHouseFileBytes_KeepsBothThumbnailPayloadsApart()
        {
            // the two rows are easy to swap, and a swap is invisible until the roofless thumbnail
            // is displayed somewhere in the UI.
            var thumb = Bytes(0xAA, 0xBB);
            var roofless = Bytes(0xCC, 0xDD);

            var bytes = SaveService.BuildHouseFileBytes(
                MarshalBytes(0x5A), NewSimiChunk(),
                SaveService.BuildPngChunk(thumb),
                SaveService.BuildPngChunk(roofless));

            var iff = RoundTrip(bytes, "House04.iff");

            Assert.Equal(thumb, iff.Get<PNG>(ThumbId).data);
            Assert.Equal(roofless, iff.Get<PNG>(RooflessId).data);
        }

        [Fact]
        public void BuildHouseFileBytes_StampsTheSimiAsPatchAdded()
        {
            // the SIMI chunk comes out of the VM, so the save has to mark it itself - otherwise the
            // next load treats the lot's values as shipped data rather than the player's.
            var simi = new SIMI();
            simi.ChunkLabel = "Simitone Lot Data";
            simi.ChunkID = FsovId;
            simi.ChunkType = "SIMI";
            simi.Version = 0x3E;
            simi.ChunkProcessed = false;
            simi.AddedByPatch = false;
            simi.BudgetDays = new SIMI.SIMIBudgetDay[6];
            for (int i = 0; i < simi.BudgetDays.Length; i++)
            {
                simi.BudgetDays[i] = new SIMI.SIMIBudgetDay();
            }

            var bytes = SaveService.BuildHouseFileBytes(
                MarshalBytes(0x5A), simi,
                SaveService.BuildPngChunk(Bytes(0x11)),
                SaveService.BuildPngChunk(Bytes(0x22)));

            var written = RoundTrip(bytes, "House05.iff").Get<SIMI>(FsovId);

            //the flags are stamped on the chunk the VM handed us, before it goes into the file.
            //AddedByPatch is a runtime flag rather than a serialised one, so it is asserted on the
            //chunk object and not on the one read back.
            Assert.True(simi.ChunkProcessed);
            Assert.True(simi.AddedByPatch);
            Assert.NotNull(written);
            Assert.True(written.ChunkProcessed);
        }

        [Fact]
        public void BuildHouseFileBytes_StillProducesFourChunksWhenBothThumbnailsAreMissing()
        {
            // the worst realistic case: the lot rendered no thumbnail at all. The save must still
            // be a complete, readable file with two empty PNG chunks rather than three chunks or a
            // thrown exception.
            var bytes = SaveService.BuildHouseFileBytes(
                MarshalBytes(0x5A), NewSimiChunk(),
                SaveService.BuildPngChunk(null),
                SaveService.BuildPngChunk(null));

            var chunks = RoundTrip(bytes, "House06.iff").ListAll();

            Assert.Equal(4, chunks.Count);
            Assert.Empty(((PNG)chunks[2]).data);
            Assert.Empty(((PNG)chunks[3]).data);
        }

        [Fact]
        public void BuildHouseFileBytes_IsStableForTheSameInput()
        {
            // Pure determinism of the builder. On its own this asserts a property nothing reads,
            // which is what it used to be - its own comment said "the previous save is compared
            // byte for byte by nothing". It is kept because the next test is the one that needs
            // it, and because a builder that varied between calls would make both of them lie.
            var first = SaveService.BuildHouseFileBytes(
                MarshalBytes(0x5A), NewSimiChunk(),
                SaveService.BuildPngChunk(Bytes(0x11)),
                SaveService.BuildPngChunk(Bytes(0x22)));
            var second = SaveService.BuildHouseFileBytes(
                MarshalBytes(0x5A), NewSimiChunk(),
                SaveService.BuildPngChunk(Bytes(0x11)),
                SaveService.BuildPngChunk(Bytes(0x22)));

            Assert.Equal(first, second);
        }

        [Fact]
        public void ASaveOfUnchangedStateLeavesTheFileOnDiskByteForByteIdentical()
        {
            // The consumer for the test above. A "did this lot actually change?" check is the
            // thing that needs determinism, and it reads the committed file rather than the
            // builder's return value - so that is what this commits and compares: the same lot
            // state, saved twice through AtomicFileCommit, must produce a live file that is
            // identical to the one the first save produced.
            //
            // Before this, the determinism claim had no consumer at all, which is what the old
            // comment was quietly complaining about.
            var root = Path.Combine(Path.GetTempPath(), "simitone-save-stability-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var path = Path.Combine(root, "Houses", "House12.iff");

                //a *new* chunk per save, as SaveService is given: two calls that share a chunk
                //object would pass for the wrong reason
                var first = SaveService.BuildHouseFileBytes(
                    MarshalBytes(0x5A), NewSimiChunk(),
                    SaveService.BuildPngChunk(Bytes(0x11)),
                    SaveService.BuildPngChunk(Bytes(0x22)));
                AtomicFileCommit.Write(path, first);

                var second = SaveService.BuildHouseFileBytes(
                    MarshalBytes(0x5A), NewSimiChunk(),
                    SaveService.BuildPngChunk(Bytes(0x11)),
                    SaveService.BuildPngChunk(Bytes(0x22)));
                AtomicFileCommit.Write(path, second);

                Assert.Equal(first, second);
                Assert.Equal(first, File.ReadAllBytes(path));
            }
            finally
            {
                try
                {
                    if (Directory.Exists(root)) Directory.Delete(root, true);
                }
                catch (IOException)
                {
                    //best effort - a leftover temp dir must never fail the test run
                }
            }
        }
    }
}
