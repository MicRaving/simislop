/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.IO;
using FSO.Common;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// The backend of the Neighbourhood Management page: the slot convention, the pure name
    /// helpers, and the copy / create / delete / rename operations run against a throwaway tree.
    ///
    /// <para>
    /// Three things here are worth stating up front, because they are the whole point of the
    /// class and all three are silent when they break.
    /// </para>
    ///
    /// <para>
    /// <b>The slot convention is the provider's.</b> <c>TS1NeighborhoodProvider.InitSpecific</c>
    /// builds its path as <c>"UserData" + (id == 0 ? "" : id + 1)</c>, so
    /// <see cref="NeighbourhoodManager.SlotFolderName"/> is asserted against that exact
    /// expression rather than against a hand-copied table - a change on either side of the
    /// boundary now fails here.
    /// </para>
    ///
    /// <para>
    /// <b>Only one install folder is a neighbourhood.</b> A stock retail install has
    /// <c>Neighborhood.iff</c> in <c>TemplateUserData</c> and in every <c>UserData*</c>, and in
    /// none of the expansion templates - <c>TemplateDowntown</c> and friends are lot overlays
    /// (house files plus a <c>*Desc.iff</c>). Copying one of those produces a folder the
    /// provider cannot open, so the tests pin that a copy either yields a real neighbourhood or
    /// fails and leaves nothing behind.
    /// </para>
    ///
    /// <para>
    /// <b>Nothing destroys the primary.</b> <c>UserData</c> is what Simitone booted from and the
    /// only slot the content manager opened, so the copy/create helpers refuse to write into it
    /// and the delete helper refuses to remove it, with the reason logged.
    /// </para>
    ///
    /// <para>
    /// Every failing case here logs through <see cref="SimitoneLog"/>, which resolves its path
    /// from the process-global <c>FSOEnvironment.UserDir</c>. This class therefore redirects that
    /// global at a temp directory for its lifetime and runs in
    /// <see cref="NonParallelCollection"/>, exactly as <c>SimitoneLogTests</c> does - otherwise
    /// these log lines would race the line counts asserted there.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class NeighbourhoodManagerTests : IDisposable
    {
        private readonly string _originalUserDir;
        private readonly string _originalInstallPath;
        private readonly string _root;

        /// <summary>Stand-in for <c>FSOEnvironment.UserDir</c>: where the slots are copied to.</summary>
        private string _userDir;

        /// <summary>Stand-in for a The Sims install directory: where slots are copied from.</summary>
        private string _install;

        public NeighbourhoodManagerTests()
        {
            _originalUserDir = FSOEnvironment.UserDir;
            _originalInstallPath = NeighbourhoodManager.DefaultInstallPath;

            _root = Path.Combine(Path.GetTempPath(), "simitone-nhood-" + Guid.NewGuid().ToString("N"));
            _userDir = Path.Combine(_root, "Simitone");
            _install = Path.Combine(_root, "The Sims");
            Directory.CreateDirectory(_userDir);

            FSOEnvironment.UserDir = _userDir;
            NeighbourhoodManager.DefaultInstallPath = null;

            BuildFakeInstall();
        }

        public void Dispose()
        {
            FSOEnvironment.UserDir = _originalUserDir;
            NeighbourhoodManager.DefaultInstallPath = _originalInstallPath;
            try
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, true);
            }
            catch (IOException)
            {
                //best effort - a leftover temp dir must never fail the run
            }
        }

        /// <summary>
        /// A miniature install with the two shapes that matter:
        /// <c>TemplateUserData</c> is a complete neighbourhood, and <c>TemplateDowntown</c> is a
        /// bare lot overlay with no <c>Neighborhood.iff</c> - the real shape of the retail
        /// expansion templates.
        /// </summary>
        private void BuildFakeInstall()
        {
            WriteFile(_install, "TemplateUserData/Neighborhood.iff", "base-neighborhood");
            WriteFile(_install, "TemplateUserData/LotLocations.iff", "base-lots");
            WriteFile(_install, "TemplateUserData/Houses/NeighborhoodDesc.iff", "base-desc");
            WriteFile(_install, "TemplateUserData/Characters/Placeholder.iff", "base-character");

            //deliberately no Neighborhood.iff - mirrors the real TemplateDowntown
            WriteFile(_install, "TemplateDowntown/House21.iff", "downtown-lot");
            WriteFile(_install, "TemplateDowntown/DTDesc.iff", "downtown-desc");

            //a neighbourhood the player actually played in The Sims, in the third slot
            WriteFile(_install, "UserData3/Neighborhood.iff", "magictown-neighborhood");
            WriteFile(_install, "UserData3/Characters/User00001.iff", "magictown-sim");
        }

        private static void WriteFile(string root, string relative, string content)
        {
            var full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            File.WriteAllText(full, content);
        }

        private string SlotDir(int id)
        {
            return Path.Combine(_userDir, NeighbourhoodManager.SlotFolderName(id));
        }

        /// <summary>
        /// The slot folders present under the user directory. The glob rather than a plain listing
        /// because the logger is pointed at this same directory and puts a "logs" folder in it.
        /// </summary>
        private string[] SlotFolders()
        {
            return Directory.GetDirectories(_userDir, "UserData*");
        }

        /// <summary>Materialises a slot folder with one marker file, standing in for a played neighbourhood.</summary>
        private void SeedSlot(int id, string marker = "seeded")
        {
            WriteFile(_userDir, NeighbourhoodManager.SlotFolderName(id) + "/Neighborhood.iff", marker);
        }

        // ================================================================= slot model

        [Fact]
        public void ThereAreNineSlots()
        {
            Assert.Equal(9, NeighbourhoodManager.SlotCount);
            Assert.Equal(9, NeighbourhoodManager.AllSlots.Count);
        }

        [Theory]
        [InlineData(0, "UserData")]
        [InlineData(1, "UserData2")]
        [InlineData(2, "UserData3")]
        [InlineData(3, "UserData4")]
        [InlineData(4, "UserData5")]
        [InlineData(5, "UserData6")]
        [InlineData(6, "UserData7")]
        [InlineData(7, "UserData8")]
        [InlineData(8, "UserData9")]
        public void ASlotIdResolvesToTheFolderTheProviderWouldUse(int id, string expected)
        {
            //asserted against the provider's own expression - "UserData" + (id == 0 ? "" : id + 1)
            //from TS1NeighborhoodProvider.InitSpecific - rather than against a hand-copied table, so
            //a change on either side of that boundary fails here instead of in a live session.
            Assert.Equal(expected, NeighbourhoodManager.SlotFolderName(id));
        }

        [Fact]
        public void OnlyNineSlotIdsAreKnown()
        {
            Assert.False(NeighbourhoodManager.IsKnownSlot(-1));
            Assert.False(NeighbourhoodManager.IsKnownSlot(9));
            Assert.False(NeighbourhoodManager.IsKnownSlot(int.MaxValue));
            Assert.False(NeighbourhoodManager.IsKnownSlot(int.MinValue));

            for (var id = 0; id < 9; id++) Assert.True(NeighbourhoodManager.IsKnownSlot(id));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(9)]
        public void AnUnknownSlotHasNoFolderAndNoTemplate(int id)
        {
            //null, not a guess: a bad id is the one mistake that turns a folder name into a path
            Assert.Null(NeighbourhoodManager.SlotFolderName(id));
            Assert.Null(NeighbourhoodManager.TemplateFolderName(id));
            Assert.Null(NeighbourhoodManager.DefaultSlotName(id));
            Assert.Null(NeighbourhoodManager.AreaNameFor(id));
            Assert.Null(NeighbourhoodManager.SlotPath(_userDir, id));
        }

        [Theory]
        [InlineData(0, "TemplateUserData")]
        [InlineData(1, "TemplateDowntown")]
        [InlineData(2, "TemplateMagictown")]
        [InlineData(3, "TemplateStudiotown")]
        [InlineData(4, "TemplateVacation")]
        [InlineData(5, "TemplateCommunity")]
        [InlineData(6, "TemplateUserData")]
        [InlineData(7, "TemplateUserData")]
        [InlineData(8, "TemplateUserData")]
        public void ASlotNamesTheAreaTemplateItIsSeededFrom(int id, string expected)
        {
            Assert.Equal(expected, NeighbourhoodManager.TemplateFolderName(id));
        }

        [Fact]
        public void TheSlotTableDescribesEverySlotConsistently()
        {
            for (var id = 0; id < 9; id++)
            {
                var slot = NeighbourhoodManager.AllSlots[id];
                Assert.Equal(id, slot.Id);
                Assert.Equal(NeighbourhoodManager.SlotFolderName(id), slot.FolderName);
                Assert.Equal(NeighbourhoodManager.TemplateFolderName(id), slot.TemplateFolderName);
                Assert.Equal(NeighbourhoodManager.DefaultSlotName(id), slot.DefaultName);
                Assert.Equal(NeighbourhoodManager.AreaNameFor(id), slot.AreaName);
            }
        }

        [Fact]
        public void OnlyThePrimarySlotIsMarkedPrimary()
        {
            Assert.Equal(0, NeighbourhoodManager.PrimarySlotId);
            Assert.True(NeighbourhoodManager.AllSlots[0].IsPrimary);
            for (var id = 1; id < 9; id++) Assert.False(NeighbourhoodManager.AllSlots[id].IsPrimary);
        }

        [Fact]
        public void ThePrimarySlotFolderMatchesTheOneTheBootstrapCopies()
        {
            //TS1UserData.NeighborhoodFolderName is what Program.cs names in its pre-flight
            //message and what the startup copy seeds; the two must not drift.
            Assert.Equal(TS1UserData.NeighborhoodFolderName, NeighbourhoodManager.PrimaryFolderName);
            Assert.Equal(NeighbourhoodManager.PrimaryFolderName, NeighbourhoodManager.SlotFolderName(0));
        }

        [Theory]
        [InlineData(0, "Neighbourhood 1")]
        [InlineData(7, "Neighbourhood 8")]
        [InlineData(8, "Neighbourhood 9")]
        public void AnUnrenamedSlotIsNumberedTheWayTheSimsNumberedIt(int id, string expected)
        {
            //The Sims numbers neighbourhoods 1..9 while the folder names run UserData..UserData8
            Assert.Equal(expected, NeighbourhoodManager.DefaultSlotName(id));
        }

        [Theory]
        [InlineData(0, "Residential")]
        [InlineData(1, "Downtown")]
        [InlineData(2, "Magictown")]
        [InlineData(5, "Community")]
        [InlineData(6, null)]
        [InlineData(8, null)]
        public void AnAreaNameIsALabelOnlyAndIsAbsentWhereTheGameHasNone(int id, string expected)
        {
            Assert.Equal(expected, NeighbourhoodManager.AreaNameFor(id));
        }

        // ================================================================= names

        [Theory]
        [InlineData("Springfield")]
        [InlineData("Simpsons")]
        [InlineData("A")]
        [InlineData("  leading and trailing  ")]
        [InlineData("Ogdenville Heights 2")]
        [InlineData("PlSimpsons - The Family")]
        [InlineData("Neighborhood #1")]
        public void AnOrdinaryNameIsValid(string name)
        {
            Assert.True(NeighbourhoodManager.IsValidName(name));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("\t\n")]
        [InlineData(".")]
        [InlineData("..")]
        [InlineData("with/slash")]
        [InlineData("with\\backslash")]
        [InlineData("C:drive")]
        [InlineData("star*")]
        [InlineData("question?")]
        [InlineData("quote\"")]
        [InlineData("less<than")]
        [InlineData("greater>than")]
        [InlineData("pipe|")]
        [InlineData("bell\u0007char")]
        [InlineData("null\0char")]
        public void ANameThatCouldBeMistakenForAPathIsNotValid(string name)
        {
            Assert.False(NeighbourhoodManager.IsValidName(name));
        }

        [Fact]
        public void ANameLongerThanTheBoundIsNotValid()
        {
            var exact = new string('a', NeighbourhoodManager.MaxNameLength);
            var tooLong = new string('a', NeighbourhoodManager.MaxNameLength + 1);

            Assert.True(NeighbourhoodManager.IsValidName(exact));
            Assert.False(NeighbourhoodManager.IsValidName(tooLong));

            //the bound is on the trimmed name, so padding does not buy extra characters
            Assert.False(NeighbourhoodManager.IsValidName("  " + tooLong + "  "));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", null)]
        [InlineData("     ", null)]
        [InlineData("///", null)]
        [InlineData("..", null)]
        public void TextWithNothingUsableInItSanitisesToNothing(string raw, string expected)
        {
            //null, not "", so "no usable name" is distinguishable from "the empty name"
            Assert.Equal(expected, NeighbourhoodManager.SanitizeName(raw));
        }

        [Theory]
        [InlineData("  Willow  ", "Willow")]
        [InlineData("Ogdenville Heights", "Ogdenville Heights")]
        [InlineData("a   b", "a b")]
        [InlineData("Springfield\tHeights", "Springfield Heights")]
        [InlineData("with/slash", "with slash")]
        [InlineData("with\\backslash", "with backslash")]
        [InlineData("C:drive", "C drive")]
        [InlineData("***", null)]
        [InlineData("a/b\\c", "a b c")]
        public void SanitisingFoldsTheUnusableIntoSpacesAndTrimsTheRest(string raw, string expected)
        {
            Assert.Equal(expected, NeighbourhoodManager.SanitizeName(raw));
        }

        [Fact]
        public void SanitisingNeverReturnsSomethingInvalidAndIsIdempotent()
        {
            //the UI's "is this usable" path and the storage path both go through this, so the two
            //have to agree - which is exactly what these two loops assert
            var samples = new[]
            {
                null, "", "  ", "///", ".", "..", "Willow", "  a   b  ", "x/y\\z", "C:x", "\u0001",
                "a\u0007b", new string('a', 100), "  " + new string('b', 100) + "  ", "Ok \u00e9\u00e8"
            };

            foreach (var sample in samples)
            {
                var once = NeighbourhoodManager.SanitizeName(sample);
                Assert.Equal(once, NeighbourhoodManager.SanitizeName(once));

                if (once == null) continue;
                Assert.True(NeighbourhoodManager.IsValidName(once), "'" + once + "' should be storable");
                Assert.True(once.Length <= NeighbourhoodManager.MaxNameLength);
            }
        }

        [Fact]
        public void SanitisingTruncatesAtTheBoundAndNotOnACutWordBoundaryProblem()
        {
            var overlong = "Springfield" + new string('x', NeighbourhoodManager.MaxNameLength);
            var clean = NeighbourhoodManager.SanitizeName(overlong);

            Assert.Equal(NeighbourhoodManager.MaxNameLength, clean.Length);
            Assert.Equal("Springfield" + new string('x', NeighbourhoodManager.MaxNameLength - "Springfield".Length), clean);
        }

        [Fact]
        public void ADisplayNameFallsBackToTheDefaultWhenThereIsNoCustomName()
        {
            Assert.Equal("Neighbourhood 4", NeighbourhoodManager.SlotName(3, null));
            Assert.Equal("Neighbourhood 4", NeighbourhoodManager.SlotName(3, ""));
            Assert.Equal("Neighbourhood 4", NeighbourhoodManager.SlotName(3, "   "));
            Assert.Equal("Neighbourhood 4", NeighbourhoodManager.SlotName(3, "///"));
        }

        [Fact]
        public void ADisplayNamePrefersTheCustomName()
        {
            Assert.Equal("Ogdenville", NeighbourhoodManager.SlotName(1, "Ogdenville"));
            //and normalises it, so a hand-edited sidecar cannot put a separator in a list control
            Assert.Equal("a b", NeighbourhoodManager.SlotName(1, "a/b"));
        }

        // ================================================================= paths

        [Fact]
        public void SlotPathsNestUnderTheUserDirectory()
        {
            Assert.Equal(Path.Combine(_userDir, "UserData"), NeighbourhoodManager.SlotPath(_userDir, 0));
            Assert.Equal(Path.Combine(_userDir, "UserData5"), NeighbourhoodManager.SlotPath(_userDir, 4));
        }

        [Fact]
        public void ATrailingSeparatorOnTheUserDirectoryDoesNotDoubleUp()
        {
            //FSOEnvironment.UserDir is ".../Simitone/" in the real game, and the provider builds
            //its path with the same Path.Combine - so the mixed-separator result is the one the
            //game already produces, and what matters is that it still names one folder.
            var withSlash = NeighbourhoodManager.SlotPath(_userDir + "/", 2);

            Assert.Equal(
                Path.GetFullPath(NeighbourhoodManager.SlotPath(_userDir, 2)),
                Path.GetFullPath(withSlash));
            Assert.DoesNotContain("//", withSlash.Replace("\\\\", "/"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NoUserDirectoryMeansNoPaths(string userDir)
        {
            Assert.Null(NeighbourhoodManager.SlotPath(userDir, 0));
            Assert.Null(NeighbourhoodManager.NamesPath(userDir));
            Assert.False(NeighbourhoodManager.Exists(userDir, 0));
            Assert.Equal(-1, NeighbourhoodManager.FirstEmptySlot(userDir));
        }

        [Fact]
        public void TheNamesSidecarLivesInItsOwnFolderBesideTheSlots()
        {
            Assert.Equal(
                Path.Combine(_userDir, "Neighbourhoods", "names.json"),
                NeighbourhoodManager.NamesPath(_userDir));
        }

        [Fact]
        public void ATemplatePathIsTheSlotAreaTemplateInTheInstall()
        {
            Assert.Equal(Path.Combine(_install, "TemplateDowntown"), NeighbourhoodManager.TemplatePath(_install, 1));
            Assert.Null(NeighbourhoodManager.TemplatePath(null, 1));
            Assert.Null(NeighbourhoodManager.TemplatePath(_install, 9));
        }

        [Fact]
        public void TheSameFolderIsTheSameFolderHoweverItIsSpelled()
        {
            var dir = Path.Combine(_root, "Some Folder");

            Assert.True(NeighbourhoodManager.IsSameFolder(dir, dir));
            Assert.True(NeighbourhoodManager.IsSameFolder(dir, dir + Path.DirectorySeparatorChar));
            Assert.True(NeighbourhoodManager.IsSameFolder(dir, dir + "/"));
            Assert.True(NeighbourhoodManager.IsSameFolder(dir, dir.Replace('\\', '/')));
            Assert.True(NeighbourhoodManager.IsSameFolder(dir.ToUpperInvariant(), dir));
            //an unnormalised "x/./x" still resolves to the same absolute folder
            Assert.True(NeighbourhoodManager.IsSameFolder(dir, Path.Combine(_root, ".", "Some Folder")));
        }

        [Fact]
        public void DifferentFoldersAreNotTheSameFolder()
        {
            var a = Path.Combine(_root, "A");
            var b = Path.Combine(_root, "B");

            Assert.False(NeighbourhoodManager.IsSameFolder(a, b));
            //a prefix is not a match: ".../A" and ".../AB" are different folders
            Assert.False(NeighbourhoodManager.IsSameFolder(a, a + "B"));
            //and the user directory is not the slot inside it
            Assert.False(NeighbourhoodManager.IsSameFolder(_userDir, NeighbourhoodManager.SlotPath(_userDir, 0)));
        }

        [Theory]
        [InlineData(null, null)]
        [InlineData("", "")]
        [InlineData(null, "C:\\Sims")]
        [InlineData("C:\\Sims", null)]
        [InlineData("", "C:\\Sims")]
        [InlineData("C:\\Sims", "")]
        public void AnUnusablePathIsSimplyNotTheSameFolder(string first, string second)
        {
            Assert.False(NeighbourhoodManager.IsSameFolder(first, second));
        }

        // ================================================================= existence

        [Fact]
        public void ExistsIsFalseForAFreshUserDirectory()
        {
            for (var id = 0; id < 9; id++) Assert.False(NeighbourhoodManager.Exists(_userDir, id));
        }

        [Fact]
        public void ExistsFollowsTheFolder()
        {
            SeedSlot(2);

            Assert.True(NeighbourhoodManager.Exists(_userDir, 2));
            Assert.False(NeighbourhoodManager.Exists(_userDir, 3));
            Assert.False(NeighbourhoodManager.Exists(_userDir, 9));
        }

        [Fact]
        public void FirstEmptySlotIsTheLowestUnusedIdAndMinusOneWhenTheyAreAllTaken()
        {
            Assert.Equal(0, NeighbourhoodManager.FirstEmptySlot(_userDir));

            SeedSlot(0);
            Assert.Equal(1, NeighbourhoodManager.FirstEmptySlot(_userDir));

            for (var id = 1; id < 9; id++) SeedSlot(id);
            Assert.Equal(-1, NeighbourhoodManager.FirstEmptySlot(_userDir));
        }

        // ================================================================= copy from install

        [Fact]
        public void CopyingAPlayedNeighbourhoodTakesTheUserDataFolderNotTheTemplate()
        {
            Assert.True(NeighbourhoodManager.CopyFromInstall(_install, _userDir, 2));

            var dest = SlotDir(2);
            Assert.True(NeighbourhoodManager.Exists(_userDir, 2));
            Assert.Equal("magictown-neighborhood", File.ReadAllText(Path.Combine(dest, "Neighborhood.iff")));
            //the Sims came with it, which is the whole reason to prefer the UserData folder
            Assert.Equal("magictown-sim", File.ReadAllText(Path.Combine(dest, "Characters", "User00001.iff")));
            //and the base template did not bleed in behind it
            Assert.False(File.Exists(Path.Combine(dest, "LotLocations.iff")));
        }

        [Fact]
        public void CopyingASlotWithNoPlayedNeighbourhoodFallsBackToTheBaseTemplate()
        {
            //UserData4 is not in this install, and TemplateStudiotown is not either, so the chain
            //ends on TemplateUserData
            Assert.True(NeighbourhoodManager.CopyFromInstall(_install, _userDir, 3));

            var dest = SlotDir(3);
            Assert.Equal("base-neighborhood", File.ReadAllText(Path.Combine(dest, "Neighborhood.iff")));
            Assert.Equal("base-desc", File.ReadAllText(Path.Combine(dest, "Houses", "NeighborhoodDesc.iff")));
        }

        [Fact]
        public void AnAreaTemplateWithNoNeighborhoodFileIsNotACopySource()
        {
            //TemplateDowntown is a lot overlay in a stock retail install: house files and a
            //DTDesc.iff, and no Neighborhood.iff. Copying it "successfully" would hand the
            //provider a folder that cannot be opened, so the rung is rejected and the chain moves
            //on - here there is nowhere left to move on to, so the copy fails and writes nothing.
            var bare = Path.Combine(_root, "Bare Install");
            WriteFile(bare, "TemplateDowntown/House21.iff", "downtown-lot");

            Assert.False(NeighbourhoodManager.CopyFromInstall(bare, _userDir, 1));
            Assert.False(NeighbourhoodManager.Exists(_userDir, 1));
            Assert.False(Directory.Exists(SlotDir(1)));
        }

        [Fact]
        public void CopyingFromAMissingInstallFailsAndCreatesNothing()
        {
            Assert.False(NeighbourhoodManager.CopyFromInstall(Path.Combine(_root, "No Such Install"), _userDir, 1));
            Assert.False(Directory.Exists(SlotDir(1)));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void CopyingWithNoInstallPathFailsAndCreatesNothing(string install)
        {
            Assert.False(NeighbourhoodManager.CopyFromInstall(install, _userDir, 1));
            Assert.False(Directory.Exists(SlotDir(1)));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(9)]
        public void CopyingIntoAnUnknownSlotIsRefusedAndCreatesNothing(int id)
        {
            Assert.False(NeighbourhoodManager.CopyFromInstall(_install, _userDir, id));
            Assert.Empty(SlotFolders());
        }

        [Fact]
        public void CopyingNeverOverwritesAnExistingSlot()
        {
            SeedSlot(1, "already-mine");
            File.WriteAllText(Path.Combine(SlotDir(1), "marker.txt"), "keep me");

            Assert.False(NeighbourhoodManager.CopyFromInstall(_install, _userDir, 1));

            //nothing was added and nothing was replaced
            Assert.Equal("already-mine", File.ReadAllText(Path.Combine(SlotDir(1), "Neighborhood.iff")));
            Assert.True(File.Exists(Path.Combine(SlotDir(1), "marker.txt")));
            Assert.False(File.Exists(Path.Combine(SlotDir(1), "LotLocations.iff")));
        }

        [Fact]
        public void CopyingWillNotOverwriteThePrimaryNeighbourhood()
        {
            SeedSlot(0, "the bootstrap data");

            Assert.False(NeighbourhoodManager.CopyFromInstall(_install, _userDir, 0));

            //the primary is the one folder nothing may write into
            Assert.Equal("the bootstrap data", File.ReadAllText(Path.Combine(SlotDir(0), "Neighborhood.iff")));
        }

        // ================================================================= create blank

        [Fact]
        public void ABlankNeighbourhoodIsTheBaseTemplateAndIsImmediatelyOpenable()
        {
            Assert.True(NeighbourhoodManager.CreateBlank(_userDir, 5, _install));

            var dest = SlotDir(5);
            Assert.True(NeighbourhoodManager.Exists(_userDir, 5));
            //a neighbourhood, not a lot overlay: this is the file the provider opens first
            Assert.True(File.Exists(Path.Combine(dest, "Neighborhood.iff")));
            Assert.Equal("base-neighborhood", File.ReadAllText(Path.Combine(dest, "Neighborhood.iff")));
            //with the sub-folders InitSpecific and LoadCharacters enumerate
            Assert.True(Directory.Exists(Path.Combine(dest, "Houses")));
            Assert.True(Directory.Exists(Path.Combine(dest, "Characters")));
            Assert.True(File.Exists(Path.Combine(dest, "Houses", "NeighborhoodDesc.iff")));
        }

        [Fact]
        public void ABlankNeighbourhoodUsesTheDefaultInstallPathWhenTheCallerGivesNone()
        {
            Assert.False(NeighbourhoodManager.CreateBlank(_userDir, 4));

            NeighbourhoodManager.DefaultInstallPath = _install;
            Assert.True(NeighbourhoodManager.CreateBlank(_userDir, 4));
            Assert.True(File.Exists(Path.Combine(SlotDir(4), "Neighborhood.iff")));
        }

        [Fact]
        public void ABlankNeighbourhoodNeedsTheBaseTemplateAndSaysSoWhenItIsMissing()
        {
            var bare = Path.Combine(_root, "No Template Install");
            WriteFile(bare, "UserData3/Neighborhood.iff", "only-a-played-neighbourhood");

            //the base template is the one folder CreateBlank can seed from; a played UserData
            //folder for some other slot is not a substitute
            Assert.False(NeighbourhoodManager.CreateBlank(_userDir, 1, bare));
            Assert.False(Directory.Exists(SlotDir(1)));
        }

        [Fact]
        public void CreatingBlankWillNotOverwriteThePrimaryNeighbourhood()
        {
            SeedSlot(0, "the bootstrap data");

            Assert.False(NeighbourhoodManager.CreateBlank(_userDir, 0, _install));

            Assert.Equal("the bootstrap data", File.ReadAllText(Path.Combine(SlotDir(0), "Neighborhood.iff")));
        }

        [Fact]
        public void CreatingBlankWillNotOverwriteAnExistingSlot()
        {
            SeedSlot(6, "already-mine");

            Assert.False(NeighbourhoodManager.CreateBlank(_userDir, 6, _install));

            Assert.Equal("already-mine", File.ReadAllText(Path.Combine(SlotDir(6), "Neighborhood.iff")));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(9)]
        public void CreatingBlankInAnUnknownSlotIsRefusedAndCreatesNothing(int id)
        {
            Assert.False(NeighbourhoodManager.CreateBlank(_userDir, id, _install));
            Assert.Empty(SlotFolders());
        }

        // ================================================================= delete

        [Fact]
        public void DeletingRemovesTheFolderAndForgetsTheName()
        {
            SeedSlot(2);
            Assert.True(NeighbourhoodManager.Rename(_userDir, 2, "Magictown"));
            Assert.Equal("Magictown", NeighbourhoodManager.GetName(_userDir, 2));

            Assert.True(NeighbourhoodManager.Delete(_userDir, 2));

            Assert.False(Directory.Exists(SlotDir(2)));
            Assert.False(NeighbourhoodManager.Exists(_userDir, 2));
            //a label for a neighbourhood that no longer exists is just noise
            Assert.Null(NeighbourhoodManager.GetName(_userDir, 2));
        }

        [Fact]
        public void DeletingLeavesEveryOtherSlotAlone()
        {
            SeedSlot(0, "primary");
            SeedSlot(1, "one");
            SeedSlot(4, "four");

            Assert.True(NeighbourhoodManager.Delete(_userDir, 1));

            Assert.Equal("primary", File.ReadAllText(Path.Combine(SlotDir(0), "Neighborhood.iff")));
            Assert.False(Directory.Exists(SlotDir(1)));
            Assert.Equal("four", File.ReadAllText(Path.Combine(SlotDir(4), "Neighborhood.iff")));
            //and the user directory itself is very much still there
            Assert.True(Directory.Exists(_userDir));
        }

        [Fact]
        public void ThePrimaryNeighbourhoodCanNeverBeDeleted()
        {
            SeedSlot(0, "the bootstrap data");

            Assert.False(NeighbourhoodManager.Delete(_userDir, 0));
            Assert.False(NeighbourhoodManager.Delete(_userDir, 0, 3));

            Assert.True(Directory.Exists(SlotDir(0)));
            Assert.Equal("the bootstrap data", File.ReadAllText(Path.Combine(SlotDir(0), "Neighborhood.iff")));
        }

        [Fact]
        public void TheNeighbourhoodThatIsLoadedCanNeverBeDeleted()
        {
            SeedSlot(3);

            //the caller owns this: only it knows what the running session has open
            Assert.False(NeighbourhoodManager.Delete(_userDir, 3, 3));
            Assert.True(Directory.Exists(SlotDir(3)));

            //a different loaded slot does not block it
            Assert.True(NeighbourhoodManager.Delete(_userDir, 3, 4));
            Assert.False(Directory.Exists(SlotDir(3)));
        }

        [Fact]
        public void DeletingWithoutASessionOpenIsUnaffected()
        {
            SeedSlot(5);

            Assert.True(NeighbourhoodManager.Delete(_userDir, 5, null));
            Assert.False(Directory.Exists(SlotDir(5)));
        }

        [Fact]
        public void DeletingASlotThatIsNotThereFails()
        {
            Assert.False(NeighbourhoodManager.Delete(_userDir, 7));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(9)]
        public void DeletingAnUnknownSlotIsRefusedAndDeletesNothing(int id)
        {
            SeedSlot(0);
            SeedSlot(1);

            Assert.False(NeighbourhoodManager.Delete(_userDir, id));

            Assert.True(Directory.Exists(SlotDir(0)));
            Assert.True(Directory.Exists(SlotDir(1)));
        }

        [Fact]
        public void ADeletedSlotCanBeCreatedAgain()
        {
            Assert.True(NeighbourhoodManager.CreateBlank(_userDir, 7, _install));
            Assert.True(NeighbourhoodManager.Delete(_userDir, 7));
            Assert.False(NeighbourhoodManager.Exists(_userDir, 7));

            Assert.True(NeighbourhoodManager.CreateBlank(_userDir, 7, _install));
            Assert.True(NeighbourhoodManager.Exists(_userDir, 7));
        }

        // ================================================================= names sidecar

        [Fact]
        public void RenamingRoundTripsThroughTheSidecar()
        {
            SeedSlot(1);

            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville"));

            Assert.Equal("Ogdenville", NeighbourhoodManager.GetName(_userDir, 1));
            Assert.Equal("Ogdenville", NeighbourhoodManager.GetDisplayName(_userDir, 1));
        }

        [Fact]
        public void TheSidecarIsWrittenWhereTheFormatSaysItIs()
        {
            SeedSlot(1);
            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville"));

            var path = Path.Combine(_userDir, "Neighbourhoods", "names.json");
            Assert.True(File.Exists(path));

            var json = File.ReadAllText(path);
            Assert.Contains("\"version\": 1", json);
            Assert.Contains("\"1\": \"Ogdenville\"", json);
        }

        [Fact]
        public void RenamingAgainKeepsTheOtherEntries()
        {
            SeedSlot(1);
            SeedSlot(4);

            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville"));
            Assert.True(NeighbourhoodManager.Rename(_userDir, 4, "Springfield"));
            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville Heights"));

            var names = NeighbourhoodManager.GetNames(_userDir);
            Assert.Equal(2, names.Count);
            Assert.Equal("Ogdenville Heights", names[1]);
            Assert.Equal("Springfield", names[4]);
        }

        [Fact]
        public void ARenamedSlotKeepsItsNameAcrossTheCopyThatCreatedIt()
        {
            //the sidecar is independent of the folders, which is why a slot can be deleted and
            //recreated without the manager having to migrate anything
            Assert.True(NeighbourhoodManager.CreateBlank(_userDir, 2, _install));
            Assert.True(NeighbourhoodManager.Rename(_userDir, 2, "New Start"));

            Assert.Equal("New Start", NeighbourhoodManager.GetName(_userDir, 2));
            Assert.Equal("New Start", NeighbourhoodManager.GetDisplayName(_userDir, 2));
        }

        [Fact]
        public void AnUnrenamedSlotDisplaysItsDefaultLabel()
        {
            SeedSlot(3);

            Assert.Null(NeighbourhoodManager.GetName(_userDir, 3));
            Assert.Equal("Neighbourhood 4", NeighbourhoodManager.GetDisplayName(_userDir, 3));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void RenamingToNothingClearsTheCustomName(string empty)
        {
            SeedSlot(1);
            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville"));

            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, empty));

            Assert.Null(NeighbourhoodManager.GetName(_userDir, 1));
            Assert.Equal("Neighbourhood 2", NeighbourhoodManager.GetDisplayName(_userDir, 1));
        }

        [Fact]
        public void ClearingIsTheSameAsRenamingToNothing()
        {
            SeedSlot(1);
            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville"));

            Assert.True(NeighbourhoodManager.ClearName(_userDir, 1));
            Assert.Null(NeighbourhoodManager.GetName(_userDir, 1));
        }

        [Fact]
        public void RenamingNormalisesWhatItStores()
        {
            SeedSlot(1);

            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "   Ogdenville   Heights   "));

            Assert.Equal("Ogdenville Heights", NeighbourhoodManager.GetName(_userDir, 1));
        }

        [Theory]
        [InlineData("with/slash")]
        [InlineData("with\\backslash")]
        [InlineData("C:drive")]
        [InlineData("..")]
        [InlineData(".")]
        [InlineData("///")]
        [InlineData("way too long ")]
        public void AnUnstorableNameIsRefusedAndChangesNothing(string bad)
        {
            SeedSlot(1);
            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville"));

            if (bad == "way too long ")
            {
                bad = new string('a', NeighbourhoodManager.MaxNameLength + 1);
            }

            //the manager gates rather than rewrites: what the player typed is not quietly turned
            //into something else, so the UI is the one that has to sanitize a text box
            Assert.False(NeighbourhoodManager.Rename(_userDir, 1, bad));
            Assert.Equal("Ogdenville", NeighbourhoodManager.GetName(_userDir, 1));
        }

        [Fact]
        public void RenamingASlotThatIsNotThereFails()
        {
            Assert.False(NeighbourhoodManager.Rename(_userDir, 4, "Nowhere"));
            Assert.Null(NeighbourhoodManager.GetName(_userDir, 4));
        }

        [Theory]
        [InlineData(-1)]
        [InlineData(9)]
        public void RenamingAnUnknownSlotIsRefusedAndWritesNothing(int id)
        {
            SeedSlot(1);
            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville"));
            var before = File.ReadAllText(NeighbourhoodManager.NamesPath(_userDir));

            Assert.False(NeighbourhoodManager.Rename(_userDir, id, "Nowhere"));

            //the sidecar is byte-for-byte what it was, and the real entry survives
            Assert.Equal(before, File.ReadAllText(NeighbourhoodManager.NamesPath(_userDir)));
            Assert.Equal("Ogdenville", NeighbourhoodManager.GetName(_userDir, 1));
        }

        [Fact]
        public void ANameSurvivesTheRestOfTheSidecarBeingDamaged()
        {
            SeedSlot(1);
            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville"));

            //AtomicFileCommit keeps the previous copy beside the target; that is the whole reason
            //a rename cannot be lost by whatever happens next.
            var json = File.ReadAllText(NeighbourhoodManager.NamesPath(_userDir));
            File.WriteAllText(NeighbourhoodManager.NamesPath(_userDir), json.Replace("Ogdenville", "Springfield"));

            Assert.Equal("Springfield", NeighbourhoodManager.GetName(_userDir, 1));
        }

        [Fact]
        public void ANamesFileForASlotWithNoFolderIsStillReported()
        {
            //the sidecar outlives the folders it names; Delete is what removes an entry
            SeedSlot(1);
            Assert.True(NeighbourhoodManager.Rename(_userDir, 1, "Ogdenville"));
            Directory.Delete(SlotDir(1), true);

            Assert.Equal("Ogdenville", NeighbourhoodManager.GetName(_userDir, 1));
        }

        // ================================================================= the sidecar is hostile input

        [Fact]
        public void NoSidecarMeansNoCustomNames()
        {
            SeedSlot(1);

            Assert.Empty(NeighbourhoodManager.GetNames(_userDir));
            Assert.Null(NeighbourhoodManager.GetName(_userDir, 1));
        }

        [Theory]
        [InlineData("")]
        [InlineData("{ not json")]
        [InlineData("[]")]
        [InlineData("\"a string\"")]
        [InlineData("null")]
        [InlineData("{\"version\": 1}")]
        [InlineData("{\"version\": 1, \"names\": []}")]
        public void ADamagedOrEmptySidecarYieldsNoNamesRatherThanThrowing(string content)
        {
            SeedSlot(1);
            WriteFile(_userDir, "Neighbourhoods/names.json", content);

            Assert.Empty(NeighbourhoodManager.GetNames(_userDir));
            Assert.Null(NeighbourhoodManager.GetName(_userDir, 1));
            //and the default label is what the page shows
            Assert.Equal("Neighbourhood 2", NeighbourhoodManager.GetDisplayName(_userDir, 1));
        }

        [Fact]
        public void ASidecarFromAFutureVersionIsRefusedOutright()
        {
            SeedSlot(1);
            WriteFile(_userDir, "Neighbourhoods/names.json", "{\"version\": 99, \"names\": {\"1\": \"Ogdenville\"}}");

            Assert.Null(NeighbourhoodManager.GetName(_userDir, 1));
        }

        [Fact]
        public void ASidecarWithoutAVersionIsReadAsTheFirstOne()
        {
            SeedSlot(1);
            WriteFile(_userDir, "Neighbourhoods/names.json", "{\"names\": {\"1\": \"Ogdenville\"}}");

            Assert.Equal("Ogdenville", NeighbourhoodManager.GetName(_userDir, 1));
        }

        [Fact]
        public void EntriesThatCannotBeASlotAreDropped()
        {
            SeedSlot(1);
            WriteFile(_userDir, "Neighbourhoods/names.json",
                "{\"version\": 1, \"names\": {\"99\": \"Nope\", \"-1\": \"Nope\", \"one\": \"Nope\", " +
                "\"1\": \"Ogdenville\", \"2\": 7, \"3\": null}}");

            var names = NeighbourhoodManager.GetNames(_userDir);

            //only the one well-formed entry for a real slot survives
            Assert.Single(names);
            Assert.Equal("Ogdenville", names[1]);
        }

        [Fact]
        public void StoredNamesAreNormalisedOnTheWayOutToo()
        {
            //a hand-edited sidecar cannot put a separator or an over-long string into a list control
            SeedSlot(1);
            WriteFile(_userDir, "Neighbourhoods/names.json",
                "{\"version\": 1, \"names\": {\"1\": \"  a/b  \", \"2\": \"" + new string('z', 200) + "\"}}");

            Assert.Equal("a b", NeighbourhoodManager.GetName(_userDir, 1));
            Assert.Equal(NeighbourhoodManager.MaxNameLength, NeighbourhoodManager.GetName(_userDir, 2).Length);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void WithNoUserDirectoryThereAreNoNamesToReadOrWrite(string userDir)
        {
            SeedSlot(1);

            Assert.Empty(NeighbourhoodManager.GetNames(userDir));
            Assert.Null(NeighbourhoodManager.GetName(userDir, 1));
            Assert.False(NeighbourhoodManager.Rename(userDir, 1, "Ogdenville"));

            //the default label needs no disk at all, so the row still has something to show
            Assert.Equal("Neighbourhood 2", NeighbourhoodManager.GetDisplayName(userDir, 1));
        }
    }
}
