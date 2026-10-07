using System;
using System.IO;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks in the stage / backup / commit contract that <c>TS1GameScreen.Save</c> relies on to
    /// avoid truncating a player's house file if the write is interrupted.
    ///
    /// The tests only touch the file system in a throwaway temp directory (created per test and
    /// removed in <see cref="Dispose"/>), so no TS1 install, VM or graphics device is needed.
    /// </summary>
    ///
    /// <para>
    /// <b>Why this class is in the non-parallel collection.</b> It is the only place in the suite
    /// that replaces <c>AtomicFileCommit.Replace</c> - the static seam that makes the commit step
    /// itself reachable from a test, since nothing else can be made to fail at exactly that step.
    /// That is a process-global, and xUnit runs test classes in parallel, so while
    /// <c>ACommitStepThatThrowsLeavesTheTargetByteForByteIntact</c> has the seam pointing at a
    /// throwing delegate every other class that commits a file goes through it too. It showed up as
    /// <c>SaveCompatTests.TheHouseAndItsBackupAreWrittenToTheSameDirectoryAsTheTarget</c> failing
    /// with "injected commit failure" roughly one run in fifteen, in a test that has nothing to do
    /// with fault injection. The collection attribute is the fix; see
    /// <c>GlobalMutationGuardTests</c>, which is what should stop the next one being added.
    /// </para>
    [Collection(NonParallelCollection.Name)]
    public sealed class AtomicFileCommitTests : IDisposable
    {
        private readonly string _tempRoot;

        public AtomicFileCommitTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "simitone-atomic-commit-" + Guid.NewGuid().ToString("N"));
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

        private string Target(string name = "House12.iff") => Path.Combine(_tempRoot, name);

        private static byte[] Bytes(params int[] values)
        {
            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) bytes[i] = (byte)values[i];
            return bytes;
        }

        /// <summary>
        /// Asserts the directory holds exactly the expected files - no stray .tmp, and no
        /// other generations. Order from the file system is not relied upon.
        /// </summary>
        private void AssertOnlyFilesExist(params string[] expected)
        {
            AssertOnlyFilesExistIn(_tempRoot, expected);
        }

        private static void AssertOnlyFilesExistIn(string dir, params string[] expected)
        {
            var actual = Directory.GetFiles(dir);
            Assert.Equal(expected.Length, actual.Length);
            foreach (var file in expected)
            {
                Assert.Contains(file, actual);
            }
        }

        [Fact]
        public void FirstWrite_CreatesTheFileWithExactlyTheGivenBytes()
        {
            var path = Target();
            var payload = Bytes(1, 2, 3, 250);

            var backup = AtomicFileCommit.Write(path, payload);

            Assert.True(File.Exists(path));
            Assert.Equal(payload, File.ReadAllBytes(path));
            //nothing existed before, so there was nothing to back up
            Assert.Null(backup);
        }

        [Fact]
        public void FirstWrite_LeavesNoStagedOrBackupFilesBehind()
        {
            var path = Target();

            AtomicFileCommit.Write(path, Bytes(7, 7, 7));

            Assert.False(File.Exists(path + ".tmp"), "the staged file must be deleted once committed");
            Assert.False(File.Exists(path + ".bak"), "a first write has no previous save to back up");
            AssertOnlyFilesExist(path);
        }

        [Fact]
        public void Overwrite_KeepsThePreviousSaveAsABackup()
        {
            var path = Target();
            var first = Bytes(10, 11, 12);
            var second = Bytes(20, 21, 22);

            Assert.Null(AtomicFileCommit.Write(path, first));
            var backup = AtomicFileCommit.Write(path, second);

            Assert.Equal(path + ".bak", backup);
            Assert.Equal(second, File.ReadAllBytes(path));
            Assert.Equal(first, File.ReadAllBytes(path + ".bak"));
        }

        [Fact]
        public void Overwrite_LeavesNoStagedFileBehind()
        {
            var path = Target();
            AtomicFileCommit.Write(path, Bytes(1));
            AtomicFileCommit.Write(path, Bytes(2));

            Assert.False(File.Exists(path + ".tmp"), "the staged file must be deleted once committed");
            AssertOnlyFilesExist(path, path + ".bak");
        }

        [Fact]
        public void RepeatOverwrites_KeepOnlyTheMostRecentPreviousSave()
        {
            var path = Target();
            var a = Bytes(1, 1);
            var b = Bytes(2, 2);
            var c = Bytes(3, 3);

            AtomicFileCommit.Write(path, a);
            AtomicFileCommit.Write(path, b);
            AtomicFileCommit.Write(path, c);

            Assert.Equal(c, File.ReadAllBytes(path));
            //the backup is one generation deep, not a full history
            Assert.Equal(b, File.ReadAllBytes(path + ".bak"));
        }

        [Fact]
        public void NestedPath_CreatesTheMissingParentDirectory()
        {
            var path = Path.Combine(_tempRoot, "LocalHouse", "nested", "House7.iff");
            var payload = Bytes(9, 9, 9, 9);

            Assert.False(Directory.Exists(Path.GetDirectoryName(path)));

            var backup = AtomicFileCommit.Write(path, payload);

            Assert.Null(backup);
            Assert.True(Directory.Exists(Path.GetDirectoryName(path)), "the house directory should have been created");
            Assert.Equal(payload, File.ReadAllBytes(path));
        }

        [Fact]
        public void NestedOverwrite_StillBacksUpInsideTheCreatedDirectory()
        {
            var dir = Path.Combine(_tempRoot, "LocalHouse");
            var path = Path.Combine(dir, "House7.iff");

            AtomicFileCommit.Write(path, Bytes(1, 1));
            var backup = AtomicFileCommit.Write(path, Bytes(2, 2));

            Assert.Equal(path + ".bak", backup);
            AssertOnlyFilesExistIn(dir, path, path + ".bak");
        }

        [Fact]
        public void AZeroLengthPayload_IsWrittenAndCommitted()
        {
            var path = Target();

            var backup = AtomicFileCommit.Write(path, new byte[0]);

            Assert.Null(backup);
            Assert.True(File.Exists(path), "an empty payload is still a committed file");
            Assert.Empty(File.ReadAllBytes(path));
            Assert.False(File.Exists(path + ".tmp"));
        }

        [Fact]
        public void AZeroLengthPayload_OverwritesAnExistingFileAndBacksItUp()
        {
            var path = Target();
            var original = Bytes(5, 6, 7);
            AtomicFileCommit.Write(path, original);

            var backup = AtomicFileCommit.Write(path, new byte[0]);

            Assert.Equal(path + ".bak", backup);
            Assert.Empty(File.ReadAllBytes(path));
            Assert.Equal(original, File.ReadAllBytes(path + ".bak"));
        }

        [Fact]
        public void AnExistingDirectoryAtTheTargetPathIsNotTreatedAsAWrite()
        {
            // A directory in the way must blow up rather than silently be replaced by a file:
            // the whole point of the commit is that it never destroys what was there before.
            var path = Target();
            Directory.CreateDirectory(path);

            var thrown = Record.Exception(() => AtomicFileCommit.Write(path, Bytes(1, 2)));

            // `Assert.NotNull(thrown)` is what this used to be, and it answers a question nobody
            // asked: any exception satisfies it, so a change that made the commit fail for an
            // unrelated reason - a null check, a bad path, an encoding - would still pass. What the
            // contract says is that the *filesystem* refused, and `File.Move` is what refuses.
            Assert.IsAssignableFrom<IOException>(thrown);

            // and the directory is untouched, still a directory, still empty
            Assert.True(Directory.Exists(path), "the pre-existing directory must be left alone");
            Assert.Empty(Directory.GetFileSystemEntries(path));
        }

        [Fact]
        public void ABlockingDirectoryAtTheTargetPathLeavesThePayloadRecoverableInTheStagedFile()
        {
            // The other half of the contract, which the test above said nothing about: what is
            // left behind. Staging (step 2) happens before the commit (step 3) and succeeds, so
            // the commit is what fails - and the complete payload is sitting in the .tmp next to
            // it. That is the whole recovery story for a save whose target cannot be replaced, and
            // it was worth pinning explicitly.
            var path = Target();
            Directory.CreateDirectory(path);

            Record.Exception(() => AtomicFileCommit.Write(path, Bytes(4, 5, 6, 7, 8, 9)));

            Assert.True(File.Exists(path + ".tmp"),
                "a commit that could not replace the target must leave the complete staged payload");
            Assert.Equal(Bytes(4, 5, 6, 7, 8, 9), File.ReadAllBytes(path + ".tmp"));
            // and no backup, because nothing was ever replaced
            Assert.False(File.Exists(path + ".bak"));
        }

        [Fact]
        public void ABlockingDirectoryAtTheTargetPathIsNotAStageFailure()
        {
            // The two failures have to be told apart, because they mean different things to a
            // player: "the save is somewhere safe" (the commit) versus "the save was never
            // written" (staging). A directory in the way only ever blocks the commit, because the
            // staged file is "<path>.tmp" and that is not the path - so the presence of a complete
            // .tmp is what distinguishes the two, and it is worth saying so in a test rather than
            // in a comment on the implementation.
            var path = Target();
            Directory.CreateDirectory(path);

            Record.Exception(() => AtomicFileCommit.Write(path, Bytes(1)));

            Assert.True(File.Exists(path + ".tmp"), "staging must have succeeded");
            Assert.Equal(Bytes(1), File.ReadAllBytes(path + ".tmp"));
        }

        [Fact]
        public void ANullPayloadIsRejectedBeforeAnythingIsTouched()
        {
            var path = Target();

            Assert.Throws<ArgumentNullException>(() => AtomicFileCommit.Write(path, null));
            Assert.Throws<ArgumentNullException>(() => AtomicFileCommit.Write(null, Bytes(1)));
            Assert.Empty(Directory.GetFiles(_tempRoot));
        }

        [Fact]
        public void ARelativePathIsRefusedRatherThanResolvedAgainstTheWorkingDirectory()
        {
            // Program.Main sets the process working directory to the install folder, so a relative
            // save path would put a player's house file next to the executable. Every call site
            // builds its path from a user directory; reaching this is a programming error.
            var thrown = Record.Exception(() => AtomicFileCommit.Write("House12.iff", Bytes(1, 2)));

            Assert.IsType<ArgumentException>(thrown);
        }

        [Fact]
        public void ARelativePathIsRefusedEvenBeforeTheParentDirectoryIsCreated()
        {
            var previous = Directory.GetCurrentDirectory();
            Directory.SetCurrentDirectory(_tempRoot);
            try
            {
                Assert.Throws<ArgumentException>(() => AtomicFileCommit.Write("sub/House12.iff", Bytes(1)));
                Assert.Empty(Directory.GetFiles(_tempRoot));
            }
            finally
            {
                Directory.SetCurrentDirectory(previous);
            }
        }

        // ---- fault injection at the commit step -------------------------------------------------

        /// <summary>
        /// The commit step is the one failure a test cannot provoke from outside: everything it
        /// could provoke - an unwritable folder, a directory in the way, a locked handle - fails
        /// earlier, in staging or in the backup. Swapping in a throwing delegate is the only way to
        /// assert what the class promises when the swap itself fails, which is the promise the
        /// class exists for: the target is left exactly as it was, and the data is still there
        /// next to it in the .tmp.
        ///
        /// It is also the step that was <c>File.Copy(tmp, path, true)</c> - which opened the
        /// target for writing in place, so "the target was never truncated" used to be a claim
        /// about the data rather than about the file.
        /// </summary>
        [Fact]
        public void ACommitStepThatThrowsLeavesTheTargetByteForByteIntact()
        {
            var path = Target();
            var original = Bytes(1, 2, 3, 4, 5);
            var replacement = Bytes(9, 9, 9, 9, 9, 9, 9, 9, 9);
            AtomicFileCommit.Write(path, original);

            var realReplace = AtomicFileCommit.Replace;
            try
            {
                AtomicFileCommit.Replace = (src, dest, bak) =>
                    throw new IOException("injected commit failure");

                var thrown = Record.Exception(() => AtomicFileCommit.Write(path, replacement));
                Assert.IsType<IOException>(thrown);
            }
            finally
            {
                AtomicFileCommit.Replace = realReplace;
            }

            // the previous save is untouched...
            Assert.Equal(original, File.ReadAllBytes(path));
            // ...and the data that was not committed is still recoverable beside it.
            Assert.True(File.Exists(path + ".tmp"), "a failed commit must leave the staged data behind");
            Assert.Equal(replacement, File.ReadAllBytes(path + ".tmp"));
        }

        [Fact]
        public void AFailedCommitDoesNotPretendToHaveLeftABackup()
        {
            // The return value is the .bak of the *previous* contents, and callers log it. Reporting
            // a backup that the failed commit never wrote would be worse than reporting nothing.
            var path = Target();
            AtomicFileCommit.Write(path, Bytes(1, 2, 3));

            var realReplace = AtomicFileCommit.Replace;
            try
            {
                AtomicFileCommit.Replace = (src, dest, bak) =>
                    throw new IOException("injected commit failure");
                Assert.Throws<IOException>(() => AtomicFileCommit.Write(path, Bytes(4)));
            }
            finally
            {
                AtomicFileCommit.Replace = realReplace;
            }

            Assert.False(File.Exists(path + ".bak"), "no .bak can exist after a first successful write plus a failed commit");
        }

        [Fact]
        public void TheStagedFileIsNotLeakedOnASuccessfulCommit()
        {
            // The other half of the same shape: after the rename the .tmp is gone by construction,
            // which is what "the commit is a rename rather than a copy" means in practice.
            var path = Target();
            AtomicFileCommit.Write(path, Bytes(1));
            AtomicFileCommit.Write(path, Bytes(2));

            Assert.False(File.Exists(path + ".tmp"));
            AssertOnlyFilesExist(path, path + ".bak");
        }

        [Fact]
        public void AnExistingBackupIsReplacedRatherThanAppendedTo()
        {
            // File.Replace writes the backup as part of the swap, so a third save cannot leave the
            // second save's bytes behind under a name that claims to be the previous one.
            var path = Target();
            AtomicFileCommit.Write(path, Bytes(1, 1));
            AtomicFileCommit.Write(path, Bytes(2, 2));
            var secondBackup = AtomicFileCommit.Write(path, Bytes(3, 3));

            Assert.Equal(path + ".bak", secondBackup);
            Assert.Equal(Bytes(2, 2), File.ReadAllBytes(path + ".bak"));
            Assert.Equal(Bytes(3, 3), File.ReadAllBytes(path));
        }

        [Fact]
        public void ABlockingDirectoryAtTheBackupPathLeavesTheTargetIntact()
        {
            // A filesystem that cannot write the .bak must not be papered over with a second,
            // non-atomic write. The commit fails, the original survives, and the reason is the
            // exception rather than a silently half-updated save.
            var path = Target();
            var original = Bytes(7, 7, 7);
            AtomicFileCommit.Write(path, original);
            Directory.CreateDirectory(path + ".bak");

            var thrown = Record.Exception(() => AtomicFileCommit.Write(path, Bytes(8, 8)));

            Assert.IsAssignableFrom<IOException>(thrown);
            Assert.Equal(original, File.ReadAllBytes(path));
            //and the payload that could not be committed is still recoverable, same contract as
            //any other failure at the commit step
            Assert.True(File.Exists(path + ".tmp"));
            Assert.Equal(Bytes(8, 8), File.ReadAllBytes(path + ".tmp"));
        }
    }
}
