using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.UI.Services;
using System;
using System.IO;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// Tests for the pure decisions <see cref="LotLifecycleService"/> makes about a lot load.
    ///
    /// <para>
    /// The service itself drives a live VM - <c>ResumeFromMarshal</c> builds a world, attaches the
    /// local player and puts the frontend up, and <c>BlueprintReset</c> hands a blueprint to the
    /// VM - so neither is reachable headlessly. What is testable is the two questions they ask
    /// about the lot path, and both are answers a wrong guess silently corrupts a save into:
    /// <list type="bullet">
    /// <item><see cref="LotLifecycleService.IsSimless"/> - whether a freshly loaded lot runs
    /// with the speed pinned off. Wrong, and a lot with nobody in it either runs away or sits
    /// frozen.</item>
    /// <item><see cref="LotLifecycleService.SimlessForLoad"/> - the same question as the lot
    /// load actually asks it, with the family itself rather than a flag. This is the one the
    /// inversion bug lived in, so it is pinned separately from the truth table above.</item>
    /// <item><see cref="LotLifecycleService.JobLevelFromPath"/> - the job level a house file
    /// declares. Wrong, and the VM loads the lot at the wrong level (or at none).</item>
    /// <item><see cref="LotLifecycleService.CachedRebuildPath"/> - where the cached
    /// <c>.fsov</c> rebuild of a lot lives.</item>
    /// </list>
    /// </para>
    ///
    /// <para>
    /// The two path questions are both <em>total</em>, and used not to be.
    /// <c>JobLevelFromPath</c> threw for a name too short to carry a level, and
    /// <c>CachedRebuildPath</c>'s inline <c>Substring</c> did the same - and both of them are
    /// reached from inside <c>BlueprintReset</c>'s recovery block, the one place whose whole job is
    /// to be the safe branch. A malformed lot name therefore turned a lot reset that could have
    /// been logged and recovered from into an unhandled failure on the game thread. "No level
    /// declared" and "no cache file" are now answers rather than throws, and the caller logs
    /// which one it got; silence would still be worse than either, so the log line is what makes
    /// this a report rather than a shrug.
    /// </para>
    ///
    /// No graphics device, content, audio or VM.
    /// </summary>
    ///
    /// <para>
    /// In the non-parallel collection because the blueprint-reset and cache-path tests redirect
    /// <c>FSOEnvironment.UserDir</c> to a throwaway folder, which is a process-global that every
    /// other test touching a save path reads. See <see cref="NonParallelCollection"/>;
    /// <c>GlobalMutationGuardTests</c> checks the membership.
    /// </para>
    [Collection(NonParallelCollection.Name)]
    public sealed class LotLifecycleServiceTests
    {
        // ------------------------------------------------------------------------ IsSimless

        [Fact]
        public void IsSimless_IsTrue_ForALotWithNoFamilyAndNoSurroundingContext()
        {
            // a town lot: nothing to simulate, so the VM is held at speed -1 and global 32 is set
            // to 1 to mark it.
            Assert.True(LotLifecycleService.IsSimless(hasActiveFamily: false, isSurrounding: false));
        }

        [Fact]
        public void IsSimless_IsFalse_ForALotWithAFamilyInPlay()
        {
            Assert.False(LotLifecycleService.IsSimless(hasActiveFamily: true, isSurrounding: false));
        }

        [Fact]
        public void IsSimless_IsFalse_ForASurroundingLotEvenWithNoFamily()
        {
            // surrounding lots are driven by the lot they surround, not by a player, so they are
            // never pinned simless.
            Assert.False(LotLifecycleService.IsSimless(hasActiveFamily: false, isSurrounding: true));
        }

        [Fact]
        public void IsSimless_IsFalse_ForASurroundingLotWithAFamilyInPlay()
        {
            Assert.False(LotLifecycleService.IsSimless(hasActiveFamily: true, isSurrounding: true));
        }

        [Theory]
        [InlineData(false, false, true)]
        [InlineData(false, true, false)]
        [InlineData(true, false, false)]
        [InlineData(true, true, false)]
        public void IsSimless_IsExactlyNeitherCondition(bool hasFamily, bool isSurrounding, bool expected)
        {
            Assert.Equal(expected, LotLifecycleService.IsSimless(hasFamily, isSurrounding));
        }

        // --------------------------------------------------------------- SimlessForLoad

        /// <summary>
        /// A family that has just moved in, which is all <see cref="SimlessForLoad"/> looks at.
        /// A <c>FAMI</c> is a plain data chunk, so a bare instance is a real family as far as the
        /// decision is concerned - the point of the test is that the object is there at all.
        /// </summary>
        private static FAMI APlacedFamily()
        {
            return new FAMI
            {
                ChunkID = 4,
                ChunkLabel = "",
                ChunkType = "FAMI",
                HouseNumber = 5,
                Budget = 20000
            };
        }

        [Fact]
        public void SimlessForLoad_IsFalse_ForALotWithAFamilyInPlay()
        {
            // The regression. BlueprintReset used to call
            // IsSimless(_screen.ActiveFamily == null, isSurrounding), handing the truth table the
            // negation of the flag it asks for - so a family that had just moved in came up on a
            // lot marked simless: SpeedMultiplier pinned to -1, global 32 set to 1, the frontend
            // opening in Buy mode, UIDesktopUCP greying out the Live button and hiding the money
            // label. The whole economy read as unimplemented, and live mode could not be entered.
            Assert.False(LotLifecycleService.SimlessForLoad(APlacedFamily(), isSurrounding: false));
        }

        [Fact]
        public void SimlessForLoad_IsTrue_ForATownLotWithNobodyInPlay()
        {
            // ...and the inversion ran the other way too: an empty town lot was marked *not*
            // simless and left ticking at speed 1 with nobody in it.
            Assert.True(LotLifecycleService.SimlessForLoad(null, isSurrounding: false));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SimlessForLoad_IsFalse_ForASurroundingLot(bool withFamily)
        {
            // A surrounding lot is driven by the lot it surrounds, so it is never pinned simless -
            // and that holds whether or not a family happens to be in play.
            Assert.False(LotLifecycleService.SimlessForLoad(
                withFamily ? APlacedFamily() : null, isSurrounding: true));
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void SimlessForLoad_AgreesWithIsSimless_OnTheFamilyIsInPlay_Reading(bool isSurrounding)
        {
            // The call-site form is a thin adapter over the truth table; pinning the two together
            // means the adapter cannot quietly start answering a different question.
            Assert.Equal(
                LotLifecycleService.IsSimless(hasActiveFamily: true, isSurrounding: isSurrounding),
                LotLifecycleService.SimlessForLoad(APlacedFamily(), isSurrounding));

            Assert.Equal(
                LotLifecycleService.IsSimless(hasActiveFamily: false, isSurrounding: isSurrounding),
                LotLifecycleService.SimlessForLoad(null, isSurrounding));
        }

        [Fact]
        public void SimlessForLoad_TreatsAFamilyWithNoHouseYetAsAFamilyInPlay()
        {
            // A move-in sets HouseNumber before the lot is loaded (SetFamilyForHouse), but nothing
            // about the simless decision may depend on it: a family that is in play is in play.
            var family = APlacedFamily();
            family.HouseNumber = 0;
            Assert.False(LotLifecycleService.SimlessForLoad(family, isSurrounding: false));
        }

        // -------------------------------------------------------------------- IsIffPath

        [Theory]
        [InlineData("UserData/Houses/House21.iff", true)]
        [InlineData("House21.IFF", false)]     // case sensitive, as EndsWith always was
        [InlineData("House21.iff.tmp", false)] // the atomic-commit staging file is not a lot
        [InlineData("House21.fsov", false)]
        [InlineData("", false)]
        public void IsIffPath_MatchesOnlyAnIffExtension(string path, bool expected)
        {
            // this is what tells the VM to read the blueprint payload as IFF rather than as XML, so
            // it is computed independently of the job level - a file can be IFF and still declare
            // no level.
            Assert.Equal(expected, LotLifecycleService.IsIffPath(path));
        }

        // ---------------------------------------------------------------- JobLevelFromPath

        [Theory]
        [InlineData("UserData/Houses/House01.iff", 1)]
        [InlineData("UserData/Houses/House09.iff", 9)]
        [InlineData("UserData/Houses/House21.iff", 21)]
        [InlineData("UserData/Houses/House99.iff", 99)]
        [InlineData("House07.iff", 7)]
        public void JobLevelFromPath_ReadsTheTwoDigitsBeforeTheExtension(string path, short expected)
        {
            Assert.Equal(expected, LotLifecycleService.JobLevelFromPath(path));
        }

        [Theory]
        [InlineData("UserData/Houses/House21.fsov")]
        [InlineData("House21.xml")]
        [InlineData("House21.iff.tmp")]
        [InlineData("")]
        public void JobLevelFromPath_IsMinusOne_ForAPathThatIsNotAnIffLot(string path)
        {
            // no extension to read a level out of, and the command the VM gets leaves JobLevel at
            // -1 exactly as before.
            Assert.Equal(-1, LotLifecycleService.JobLevelFromPath(path));
        }

        [Fact]
        public void JobLevelFromPath_IsMinusOne_ForAnIffPathTooShortToCarryALevel()
        {
            // It used to throw ArgumentOutOfRangeException here, and threw FormatException one case
            // below. Both of those are reached from the blueprint-reset *recovery* path, inside the
            // block that is supposed to be the safe one, so a malformed lot name turned a lot reset
            // that could have been logged and recovered from into an unhandled failure on the game
            // thread. -1 is the same answer a non-IFF path has always given, and the caller logs.
            Assert.Equal(-1, LotLifecycleService.JobLevelFromPath(".iff"));
            Assert.Equal(-1, LotLifecycleService.JobLevelFromPath("a.iff"));
        }

        [Fact]
        public void JobLevelFromPath_IsMinusOne_ForAnIffPathWhoseLevelDigitsAreNotANumber()
        {
            Assert.Equal(-1, LotLifecycleService.JobLevelFromPath("ab.iff"));
            Assert.Equal(-1, LotLifecycleService.JobLevelFromPath("Housexx.iff"));
        }

        [Fact]
        public void JobLevelFromPath_AcceptsANegativeLevelAsWritten()
        {
            // A negative sign is allowed, and a hand-made lot file that declares one is honoured
            // rather than second-guessed. This is why IsIffPath is a separate question from the
            // level - and, note, -1 is also the "no level declared" answer, which the two share.
            Assert.Equal(-1, LotLifecycleService.JobLevelFromPath("House-1.iff"));
        }

        [Fact]
        public void JobLevelFromPath_RejectsLeadingWhitespaceTheProvidersParseWouldAccept()
        {
            // "House 7.iff" - the two digits are " 7" with a space in front, which a
            // culture-sensitive Parse would take and NumberStyles.AllowLeadingSign will not. The
            // point is that the answer is a value rather than an exception either way.
            Assert.Equal(-1, LotLifecycleService.JobLevelFromPath("House 7.iff"));
        }

        // ------------------------------------------------- CachedRebuildPath (the .fsov cache file)

        [Fact]
        public void CachedRebuildPath_SwapsTheIffExtensionForTheFsovCacheName()
        {
            // Built from Path.Combine rather than the old "LocalHouse/" string concat, so the
            // separator style is the platform's rather than a hard-coded slash - and the result is
            // the same path on Windows either way.
            var previous = FSO.Common.FSOEnvironment.UserDir;
            var root = Path.Combine(Path.GetTempPath(), "simitone-lotlife-" + Guid.NewGuid().ToString("N"));
            try
            {
                FSO.Common.FSOEnvironment.UserDir = root;
                var path = LotLifecycleService.CachedRebuildPath(
                    Path.Combine(root, "Houses", "House07.iff"));

                Assert.NotNull(path);
                Assert.Equal("House07.fsov", Path.GetFileName(path));
                Assert.Equal("LocalHouse", Path.GetFileName(Path.GetDirectoryName(path)));
            }
            finally
            {
                FSO.Common.FSOEnvironment.UserDir = previous;
                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch (IOException) { }
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("iff")]
        [InlineData(null)]
        public void CachedRebuildPath_IsNullForANameTooShortToHaveOne(string path)
        {
            // The old inline expression was `filename.Substring(0, filename.Length - 4)`, which
            // throws for anything shorter than four characters - on the path BlueprintReset walks
            // for every lot, before it even knows whether the cache file exists. "iff" is three
            // characters and carries no extension at all, so it is the clearest case.
            Assert.Null(LotLifecycleService.CachedRebuildPath(path));
        }

        [Fact]
        public void CachedRebuildPath_StillBuildsAPathForANameOfExactlyTheMinimumLength()
        {
            // ".iff" is four characters, so the Substring is legal (it yields an empty stem) and
            // the answer is a path rather than null. The guard is about not throwing, not about
            // deciding what a dotfile-shaped lot is called.
            var previous = FSO.Common.FSOEnvironment.UserDir;
            try
            {
                FSO.Common.FSOEnvironment.UserDir = Path.GetTempPath();
                Assert.NotNull(LotLifecycleService.CachedRebuildPath(".iff"));
            }
            finally
            {
                FSO.Common.FSOEnvironment.UserDir = previous;
            }
        }
    }
}
