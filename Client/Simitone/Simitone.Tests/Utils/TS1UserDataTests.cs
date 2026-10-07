using System;
using System.IO;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks in the startup pre-flight that decides whether Simitone can seed its first
    /// neighbourhood from a The Sims install.
    ///
    /// <para>
    /// The rule being pinned is that either of two sources is enough: the played
    /// <c>UserData</c>, or the <c>TemplateUserData</c> every store install ships. The second is
    /// the one that matters - The Sims never writes <c>UserData</c> during installation, so
    /// requiring it locked out every never-launched install of every edition behind a "please
    /// launch The Sims first" message. The <c>Neighborhood.iff</c> file is still required at both
    /// rungs, because a present-but-empty <c>UserData</c> folder (what Legacy Collection looks
    /// like before first launch) copies an empty neighbourhood and then fails one line later.
    /// </para>
    /// </summary>
    public sealed class TS1UserDataTests
    {
        // ---- pure path helpers -------------------------------------------------------------

        [Fact]
        public void InstallNeighborhoodPath_CombinesTheInstallAndTheFolder()
        {
            Assert.Equal(
                Path.Combine("C:", "Sims", "UserData"),
                TS1UserData.InstallNeighborhoodPath(Path.Combine("C:", "Sims")));
        }

        [Fact]
        public void InstallNeighborhoodPath_SupportsTheNumberedSiblings()
        {
            Assert.Equal(
                Path.Combine("C:", "Sims", "UserData4"),
                TS1UserData.InstallNeighborhoodPath(Path.Combine("C:", "Sims"), "UserData4"));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void InstallNeighborhoodPath_IsNullForAnEmptyInstall(string install)
        {
            // null is the "not found" sentinel the caller passes straight to File.Exists
            Assert.Null(TS1UserData.InstallNeighborhoodPath(install));
        }

        [Fact]
        public void InstallTemplatePath_NamesTheBaseTemplateInsideTheInstall()
        {
            Assert.Equal(
                Path.Combine("C:", "Sims", "TemplateUserData"),
                TS1UserData.InstallTemplatePath(Path.Combine("C:", "Sims")));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void InstallTemplatePath_IsNullForAnEmptyInstall(string install)
        {
            Assert.Null(TS1UserData.InstallTemplatePath(install));
        }

        [Fact]
        public void BaseTemplateFolderName_IsTheFolderTheManagementPageAlsoUses()
        {
            // The provider, the management page and this pre-flight all have to name the same
            // folder; a rename on one side that misses the other breaks the first-run bootstrap
            // without failing a build. Asserted rather than trusted, because the two constants
            // live in different types for good reasons (one is engine-facing, one is UI-facing).
            Assert.Equal("TemplateUserData", TS1UserData.BaseTemplateFolderName);
            Assert.Equal(TS1UserData.BaseTemplateFolderName, NeighbourhoodManager.BaseTemplateFolderName);
        }

        [Fact]
        public void SteamSavesRoot_MatchesTheLegacyCollectionLocation()
        {
            Assert.Equal(
                Path.Combine("C:", "Users", "x", "Saved Games", "Electronic Arts", "The Sims 25"),
                TS1UserData.SteamSavesRoot(Path.Combine("C:", "Users", "x")));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void SteamSavesRoot_IsNullForAnEmptyProfile(string profile)
        {
            Assert.Null(TS1UserData.SteamSavesRoot(profile));
        }

        [Fact]
        public void SteamNeighborhoodPath_NestsTheNeighborhoodUnderTheSavesRoot()
        {
            var expected = Path.Combine("C:", "Users", "x", "Saved Games", "Electronic Arts", "The Sims 25", "UserData");
            Assert.Equal(expected, TS1UserData.SteamNeighborhoodPath(Path.Combine("C:", "Users", "x")));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void SteamNeighborhoodPath_IsNullForAnEmptyProfile(string profile)
        {
            Assert.Null(TS1UserData.SteamNeighborhoodPath(profile));
        }

        // ---- HasNeighborhood (file IO against a temp tree) ----------------------------------

        [Fact]
        public void HasNeighborhood_RequiresTheFileNotJustTheFolder()
        {
            var root = NewTempDir();
            try
            {
                var folder = Path.Combine(root, "UserData");
                Directory.CreateDirectory(folder);
                Assert.False(TS1UserData.HasNeighborhood(folder));

                File.WriteAllBytes(Path.Combine(folder, TS1UserData.NeighborhoodResourceName), new byte[] { 1, 2, 3 });
                Assert.True(TS1UserData.HasNeighborhood(folder));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void HasNeighborhood_IsFalseForAnEmptyFolderPath(string folder)
        {
            Assert.False(TS1UserData.HasNeighborhood(folder));
        }

        [Fact]
        public void HasNeighborhood_IsFalseForAFolderThatDoesNotExist()
        {
            var root = NewTempDir();
            try
            {
                Assert.False(TS1UserData.HasNeighborhood(Path.Combine(root, "nope")));
            }
            finally { Directory.Delete(root, true); }
        }

        // ---- IsBootstrappable (file IO against a temp tree) --------------------------------

        [Fact]
        public void IsBootstrappable_RequiresTheNeighborhoodFileNotJustTheFolder()
        {
            var root = NewTempDir();
            try
            {
                var install = Path.Combine(root, "The Sims");
                Directory.CreateDirectory(Path.Combine(install, "UserData"));

                // empty folder: exactly the Legacy Collection first-run shape
                Assert.False(TS1UserData.IsBootstrappable(install));

                File.WriteAllBytes(Path.Combine(install, "UserData", "Neighborhood.iff"), new byte[] { 1, 2, 3 });
                Assert.True(TS1UserData.IsBootstrappable(install));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void IsBootstrappable_IsFalseForAMissingInstallFolder()
        {
            var root = NewTempDir();
            try
            {
                Assert.False(TS1UserData.IsBootstrappable(Path.Combine(root, "nope")));
            }
            finally { Directory.Delete(root, true); }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void IsBootstrappable_IsFalseForAnEmptyPath(string install)
        {
            Assert.False(TS1UserData.IsBootstrappable(install));
        }

        // ---- IsBootstrappable: the TemplateUserData fallback --------------------------------

        [Fact]
        public void IsBootstrappable_AcceptsAVirginInstallThatOnlyShipsTheTemplate()
        {
            // The headline case. The Sims does not create UserData during installation, so a
            // store install nobody has played has only TemplateUserData. Requiring UserData here
            // is what made the game refuse to start and tell the player to launch The Sims once.
            var root = NewTempDir();
            try
            {
                var install = Path.Combine(root, "The Sims");
                var template = Path.Combine(install, TS1UserData.BaseTemplateFolderName);
                Directory.CreateDirectory(template);
                File.WriteAllBytes(Path.Combine(template, TS1UserData.NeighborhoodResourceName), new byte[] { 1, 2, 3 });

                Assert.False(Directory.Exists(Path.Combine(install, TS1UserData.NeighborhoodFolderName)));
                Assert.True(TS1UserData.IsBootstrappable(install));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void IsBootstrappable_AcceptsAnEmptyUserDataFolderAlongsideTheTemplate()
        {
            // The Legacy Collection first-run shape, exactly: a UserData folder the game created
            // but has not filled in, plus the shipped template.
            var root = NewTempDir();
            try
            {
                var install = Path.Combine(root, "The Sims");
                Directory.CreateDirectory(Path.Combine(install, TS1UserData.NeighborhoodFolderName));
                var template = Path.Combine(install, TS1UserData.BaseTemplateFolderName);
                Directory.CreateDirectory(template);
                File.WriteAllBytes(Path.Combine(template, TS1UserData.NeighborhoodResourceName), new byte[] { 1, 2, 3 });

                Assert.True(TS1UserData.IsBootstrappable(install));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void IsBootstrappable_IsFalseWhenTheTemplateHasNoNeighborhoodResource()
        {
            // A Template* folder without Neighborhood.iff is a lot overlay (or an empty folder), and
            // copying it produces a neighbourhood the provider cannot open. The template rung is
            // held to the same file requirement as the UserData rung for that reason.
            var root = NewTempDir();
            try
            {
                var install = Path.Combine(root, "The Sims");
                Directory.CreateDirectory(Path.Combine(install, TS1UserData.BaseTemplateFolderName));
                File.WriteAllBytes(Path.Combine(install, TS1UserData.BaseTemplateFolderName, "StreetNames.iff"), new byte[] { 1 });

                Assert.False(TS1UserData.IsBootstrappable(install));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void IsBootstrappable_PrefersAPlayedUserDataButAcceptsEither()
        {
            // Order is not observable through this predicate (either source answers true), so
            // what is asserted is that adding a playable neighbourhood on top of the shipped
            // template does not stop the install being usable.
            var root = NewTempDir();
            try
            {
                var install = Path.Combine(root, "The Sims");
                Write(Path.Combine(install, TS1UserData.NeighborhoodFolderName));
                Write(Path.Combine(install, TS1UserData.BaseTemplateFolderName));

                Assert.True(TS1UserData.IsBootstrappable(install));
            }
            finally { Directory.Delete(root, true); }
        }

        [Fact]
        public void IsBootstrappable_AndTheSteamSavesCheckTogetherCoverBothEditions()
        {
            // What Program.cs actually asks: "is there a neighbourhood in the install directory
            // (UserData or the shipped template) OR in the Saved Games folder?". A never-launched
            // Legacy Collection install has only the template; a played one has only Saved Games.
            // Both halves of the disjunction have to hold, or the pre-flight would still lock out
            // one of the two - which is precisely the bug this feature set out to remove.
            var root = NewTempDir();
            try
            {
                // A Steam library path is what tells the two locations apart; the string shape is
                // all the edition decision has to go on for a -path the player typed.
                var steamInstall = Path.Combine(root, "steamapps", "common", "The Sims");
                var emptyInstall = Path.Combine(root, "empty", "The Sims");
                Directory.CreateDirectory(emptyInstall);

                var virginProfile = Path.Combine(root, "virgin");
                var virginSaves = TS1UserData.SteamNeighborhoodPath(virginProfile);

                // Never launched: no Saved Games at all, only the template the game shipped.
                Write(Path.Combine(steamInstall, TS1UserData.BaseTemplateFolderName));
                Assert.False(TS1UserData.HasNeighborhood(virginSaves));
                Assert.True(TS1UserData.IsBootstrappable(steamInstall)
                            || TS1UserData.HasNeighborhood(virginSaves));

                // Played: the saves are in Saved Games, and the install has no template either
                // (a trimmed or modded library), so the saves rung is the only thing left.
                var playedProfile = Path.Combine(root, "played");
                var playedSaves = TS1UserData.SteamNeighborhoodPath(playedProfile);
                Write(playedSaves);
                Assert.False(TS1UserData.IsBootstrappable(emptyInstall));
                Assert.True(TS1UserData.IsBootstrappable(emptyInstall) || TS1UserData.HasNeighborhood(playedSaves));

                // Neither: an install with no neighbourhood anywhere is the one case the pre-flight
                // still refuses, because there is genuinely nothing to copy.
                Assert.False(TS1UserData.IsBootstrappable(emptyInstall)
                             || TS1UserData.HasNeighborhood(Path.Combine(root, "nobody", "Saves")));
            }
            finally { Directory.Delete(root, true); }
        }

        private static void Write(string neighborhoodFolder)
        {
            Directory.CreateDirectory(neighborhoodFolder);
            File.WriteAllBytes(
                Path.Combine(neighborhoodFolder, TS1UserData.NeighborhoodResourceName),
                new byte[] { 1, 2, 3 });
        }

        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "simitone-userdata-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }
    }
}
