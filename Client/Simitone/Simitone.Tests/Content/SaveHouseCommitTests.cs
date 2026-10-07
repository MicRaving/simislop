using System;
using System.IO;
using System.Linq;
using FSO.Content.TS1;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using Xunit;

namespace Simitone.Tests.Content
{
    /// <summary>
    /// <c>TS1NeighborhoodProvider.SaveHouse</c> - the write of a lot's own
    /// <c>Houses/House{NN}.iff</c>, the file that carries the <c>FSOV</c> lot data.
    /// </summary>
    ///
    /// <para>
    /// <b>Why this needed a test of its own.</b> It was the last write of a player's data in the
    /// provider that did not go through <see cref="AtomicFileWrite"/>:
    /// <c>new FileStream(GetHousePath(houseID), FileMode.Create)</c> and then
    /// <c>file.Write(stream)</c> straight into it. <c>FileMode.Create</c> truncates <em>at
    /// open</em>, so the player's home lot was emptied before a single chunk had been asked for
    /// its bytes, and any failure after that point - a chunk's own <c>Write</c> throwing, a full
    /// disk, antivirus interference, a power cut - left a truncated <c>House{NN}.iff</c> with no
    /// recovery copy. The neighbourhood, the travel slot and the client's own house save had all
    /// been moved off that pattern; this one had not.
    /// </para>
    ///
    /// <para>
    /// <b>What is reachable headlessly.</b> <see cref="TS1NeighborhoodProvider"/>'s constructor
    /// loads a whole neighbourhood out of a TS1 install, so the write is reached through the two
    /// statics it was split into - <see cref="TS1NeighborhoodProvider.HousePathFor"/> for the path
    /// and <see cref="TS1NeighborhoodProvider.CommitHouseFile"/> for the commit. That split is
    /// what makes this file possible at all, and it is the same shape the class already uses for
    /// <c>CopyFolder</c> and <c>TS1AvatarSavePath.Resolve</c>: expose the contract, not the
    /// mechanism.
    /// </para>
    public sealed class SaveHouseCommitTests : IDisposable
    {
        private readonly string _root;

        public SaveHouseCommitTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-savehouse-" + Guid.NewGuid().ToString("N"));
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

        #region helpers

        /// <summary>Where a lot number lives inside the throwaway user data folder.</summary>
        private string HousePath(int houseId)
            => Path.GetFullPath(TS1NeighborhoodProvider.HousePathFor(_root, houseId));

        /// <summary>What <c>SaveHouse</c> does, minus the neighbourhood it needs for its path.</summary>
        private string Save(int houseId, IffFile lot)
            => TS1NeighborhoodProvider.CommitHouseFile(HousePath(houseId), lot);

        /// <summary>A lot IFF shaped like the one the game writes: FSOV plus a thumbnail.</summary>
        private static IffFile Lot(byte fill)
        {
            var iff = new IffFile();

            var fsov = new FSOV
            {
                ChunkLabel = "Simitone Lot Data",
                ChunkID = 1,
                ChunkProcessed = true,
                ChunkType = "FSOV",
                AddedByPatch = true,
                Data = Enumerable.Repeat(fill, 32).ToArray(),
            };
            iff.AddChunk(fsov);

            var thumb = new PNG
            {
                data = Enumerable.Repeat(fill, 8).ToArray(),
                ChunkLabel = "Lot Thumbnail",
                ChunkID = 513,
                ChunkProcessed = true,
                ChunkType = "PNG_",
                AddedByPatch = true,
            };
            iff.AddChunk(thumb);

            return iff;
        }

        private IffFile Open(string path)
        {
            var iff = new IffFile(path);
            iff.MarkThrowaway();
            return iff;
        }

        #endregion

        #region the path

        [Fact]
        public void ALotNumberBecomesTheFileTheLoaderLooksFor()
        {
            // Path.GetFullPath rather than a string comparison, because the production path is
            // built with a forward slash inside Combine and a test that asserts on separators
            // would be asserting on the platform, not on the contract. The contract is the three
            // parts: the user data folder, "Houses", and "House07.iff".
            var full = HousePath(7);

            Assert.Equal("House07.iff", Path.GetFileName(full));
            Assert.Equal("Houses", Path.GetFileName(Path.GetDirectoryName(full)));
            Assert.Equal(Path.GetFullPath(_root), Path.GetDirectoryName(Path.GetDirectoryName(full)));
        }

        [Theory]
        [InlineData(0, "House00.iff")]
        [InlineData(7, "House07.iff")]
        [InlineData(12, "House12.iff")]
        [InlineData(99, "House99.iff")]
        [InlineData(100, "House100.iff")]
        public void TheNumberIsPaddedToTwoDigitsAndNoFurther(int houseId, string expectedName)
        {
            // "House7" and "House07" are different files, and the padding is the same expression
            // the reader side uses - which is the only reason a one-digit lot loads at all
            Assert.Equal(expectedName, Path.GetFileName(HousePath(houseId)));
        }

        [Fact]
        public void TheHouseFolderIsCreatedWhenItIsNotThereYet()
        {
            // a first save in a fresh user data folder, which is exactly when there is no Houses
            // directory. The old code did not create it and failed with DirectoryNotFoundException.
            Assert.False(Directory.Exists(Path.Combine(_root, "Houses")));

            Save(1, Lot(0x22));

            Assert.True(File.Exists(HousePath(1)));
        }

        [Theory]
        [InlineData(0)]
        [InlineData(7)]
        [InlineData(12)]
        [InlineData(99)]
        [InlineData(100)]
        public void EveryLotNumberGetsAFileThatCanBeReadBack(int houseId)
        {
            Save(houseId, Lot(0x33));

            Assert.Equal(Enumerable.Repeat((byte)0x33, 32).ToArray(),
                Open(HousePath(houseId)).Get<FSOV>(1).Data);
        }

        #endregion

        #region the lot survives the write

        [Fact]
        public void TheLotDataComesBackOutOfTheFileUnchanged()
        {
            var expected = Enumerable.Repeat((byte)0x7E, 32).ToArray();

            Save(3, Lot(0x7E));

            var fsov = Open(HousePath(3)).Get<FSOV>(1);
            Assert.NotNull(fsov);
            Assert.Equal(expected, fsov.Data);
        }

        [Fact]
        public void TheSavedFileHoldsEveryChunkTheLotWasBuiltWith()
        {
            Save(4, Lot(0x44));

            Assert.Equal(2, Open(HousePath(4)).ListAll().Count);
        }

        #endregion

        #region the commit contract

        [Fact]
        public void TheFirstSaveOfALotLeavesNoBackupBecauseThereWasNothingToBackUp()
        {
            Assert.Null(Save(5, Lot(0x55)));

            Assert.False(File.Exists(HousePath(5) + ".bak"));
            Assert.False(File.Exists(HousePath(5) + ".tmp"));
        }

        [Fact]
        public void TheSecondSaveOfALotKeepsTheFirstAsABackup()
        {
            // THE regression, stated as a fact about the file rather than about the code: the
            // old code truncated the previous lot at open, so after two saves there was no first
            // save anywhere to recover.
            Save(6, Lot(0x01));
            var firstBytes = File.ReadAllBytes(HousePath(6));

            var backup = Save(6, Lot(0x02));

            Assert.Equal(HousePath(6) + ".bak", backup);
            Assert.Equal(firstBytes, File.ReadAllBytes(HousePath(6) + ".bak"));

            //and the backup is a readable lot, not a truncated one - which is the point of it
            var read = Open(backup);
            Assert.Equal(Enumerable.Repeat((byte)0x01, 32).ToArray(), read.Get<FSOV>(1).Data);
        }

        [Fact]
        public void TheBackupAlwaysHoldsTheSaveBeforeTheCurrentOne()
        {
            // a single slot, so not the first and not an accumulation
            for (int i = 0; i < 5; i++)
            {
                Save(8, Lot((byte)(0x10 + i)));
            }

            Assert.Equal(Enumerable.Repeat((byte)0x14, 32).ToArray(), Open(HousePath(8)).Get<FSOV>(1).Data);
            Assert.Equal(Enumerable.Repeat((byte)0x13, 32).ToArray(),
                Open(HousePath(8) + ".bak").Get<FSOV>(1).Data);
        }

        [Fact]
        public void ACommitOfAnUnchangedPayloadStillRotatesTheBackup()
        {
            // nothing about the commit depends on the bytes having changed, so "save" always
            // leaves a recovery copy even when the player did nothing
            Save(9, Lot(0x66));
            var backup = Save(9, Lot(0x66));

            Assert.NotNull(backup);
            Assert.True(File.Exists(backup));
        }

        [Fact]
        public void NothingIsLeftStagedAfterASuccessfulSave()
        {
            Save(10, Lot(0x66));
            Save(10, Lot(0x77));

            var actual = Directory.GetFiles(Path.Combine(_root, "Houses"))
                .Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();
            Assert.Equal(new[] { "House10.iff", "House10.iff.bak" }, actual);
        }

        [Fact]
        public void TheBackupAndTheStagedFileAreNotNamesTheLotLoaderCouldPickUp()
        {
            // HousePathFor builds "House{NN}.iff" from a number, so neither side file may be a
            // name a lot number produces - or a stray side file becomes a loadable lot
            Save(11, Lot(0x11));
            Save(11, Lot(0x22));

            foreach (var id in new[] { 0, 1, 7, 12, 99, 100 })
            {
                var houseName = Path.GetFileName(HousePath(id));
                Assert.NotEqual(houseName, Path.GetFileName(HousePath(11) + ".bak"));
                Assert.NotEqual(houseName, Path.GetFileName(HousePath(11) + ".tmp"));
            }
        }

        #endregion

        #region a failure part way through

        /// <summary>
        /// A chunk whose own writer throws - the ordinary case for a half-broken object, whose
        /// <c>Test_Fail</c> primitive is to throw.
        /// </summary>
        private sealed class ExplodingChunk : IffChunk
        {
            public override void Read(IffFile iff, Stream stream) { }
            public override bool Write(IffFile iff, Stream stream)
                => throw new InvalidOperationException("this object is broken");
        }

        private static IffFile BrokenLot()
        {
            var iff = new IffFile();
            //processed, so that the only thing standing between a broken object and the caller's
            //destination file is its own writer
            iff.AddChunk(new ExplodingChunk
            {
                ChunkID = 1,
                ChunkType = "FSOV",
                ChunkLabel = "x",
                ChunkProcessed = true,
            });
            return iff;
        }

        [Fact]
        public void AChunkThatThrowsWhileSerialisingLeavesThePreviousLotAsTheFileOnDisk()
        {
            // The property the whole reordering exists for. The payload is serialised in full
            // before the destination is touched, so an exception from a chunk's writer - which is
            // not an IOException, and so was never seen by a `catch (IOException)` - leaves the
            // player's lot exactly as it was.
            Save(12, Lot(0xAB));
            var firstBytes = File.ReadAllBytes(HousePath(12));

            Assert.Throws<InvalidOperationException>(() => Save(12, BrokenLot()));

            Assert.Equal(firstBytes, File.ReadAllBytes(HousePath(12)));
            Assert.Equal(Enumerable.Repeat((byte)0xAB, 32).ToArray(),
                Open(HousePath(12)).Get<FSOV>(1).Data);
        }

        [Fact]
        public void AFailedSaveOfALotThatHadNoneLeavesNoTruncatedFileBehind()
        {
            // the same guarantee with nothing to preserve: a crash on the very first save of a
            // lot must not leave a half-written House{NN}.iff for the loader to try to open
            Assert.Throws<InvalidOperationException>(() => Save(13, BrokenLot()));

            Assert.False(File.Exists(HousePath(13)),
                "a save that never completed must not leave a file where the lot should be");
        }

        [Fact]
        public void AFailedSaveDoesNotDisturbTheLotNextDoor()
        {
            // the write is per-lot, so one broken object cannot cost the player a different lot
            Save(14, Lot(0xEE));
            Save(15, Lot(0xFF));

            Assert.Throws<InvalidOperationException>(() => Save(15, BrokenLot()));

            Assert.Equal(Enumerable.Repeat((byte)0xFF, 32).ToArray(),
                Open(HousePath(15)).Get<FSOV>(1).Data);
            Assert.Equal(Enumerable.Repeat((byte)0xEE, 32).ToArray(),
                Open(HousePath(14)).Get<FSOV>(1).Data);
        }

        #endregion

        #region the arguments

        [Fact]
        public void ANullResourceIsAnArgumentErrorRatherThanAnEmptyFile()
        {
            // a null IFF would otherwise become a zero-byte House{NN}.iff, which the loader then
            // reads as a lot with no chunks
            Assert.Throws<ArgumentNullException>(() => Save(17, null));

            Assert.False(File.Exists(HousePath(17)));
        }

        #endregion
    }
}
