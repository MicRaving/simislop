using Simitone.Client.Utils;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks in which The Sims installation Simitone boots on, and where it reads that
    /// installation's save data from.
    ///
    /// <para>
    /// The rules used to be implied by the shape of <c>Program.Main</c> - a flag, a config
    /// lookup, a locator call - and the only one of them that had ever been reasoned about
    /// was "the first probe that hits". Everything that fires on an unusual machine
    /// (two installs, a remembered install that has moved, an empty machine) had no coverage at
    /// all, because each of them needs a different machine to happen on. They are pure
    /// functions of what discovery and the config file reported, so all of them are reachable
    /// here.
    /// </para>
    ///
    /// <para>
    /// The properties asserted most often below are the ones whose failure is silent and
    /// expensive: a remembered choice being ignored (a dialog every launch), a command line
    /// override being second-guessed (a scripted launch booting the wrong game), and a Steam
    /// install being treated as a boxed one (a first neighbourhood that does not exist).
    /// </para>
    /// </summary>
    public sealed class TS1InstallSelectionTests
    {
        private static readonly SimsInstallCandidate Boxed = new SimsInstallCandidate("C:/Games/The Sims/", SimsInstallKind.Registry);
        private static readonly SimsInstallCandidate Steam = new SimsInstallCandidate("D:/Steam/steamapps/common/The Sims Legacy Collection/", SimsInstallKind.Steam);
        private static readonly SimsInstallCandidate Portable = new SimsInstallCandidate("../The Sims/", SimsInstallKind.Portable);

        private static IReadOnlyList<SimsInstallCandidate> Roster(params SimsInstallCandidate[] candidates)
        {
            return candidates;
        }

        [Fact]
        public void ACommandLinePathWinsOverEverythingElse()
        {
            // Including a remembered choice and a full roster: -path is an instruction for this
            // run, and honouring it is the only thing that keeps a scripted launch predictable
            // on a machine that has since acquired a second install.
            var decision = TS1InstallSelection.Resolve(true, "E:/Games/The Sims/", true, "C:/Games/The Sims/",
                Roster(Boxed, Steam));

            Assert.Equal(SimsInstallDecisionKind.CommandLinePath, decision.Kind);
            Assert.Equal("E:/Games/The Sims/", decision.Path);
            Assert.True(decision.IsSettled);
            Assert.False(decision.NeedsPrompt);
        }

        [Fact]
        public void ACommandLinePathWinsEvenWithNothingElseToGoOn()
        {
            var decision = TS1InstallSelection.Resolve(true, "E:/Games/The Sims/", false, null, Roster());

            Assert.Equal(SimsInstallDecisionKind.CommandLinePath, decision.Kind);
            Assert.Equal("E:/Games/The Sims/", decision.Path);
        }

        [Fact]
        public void AnEmptyCommandLinePathIsNotAnInstruction()
        {
            // A bare -path (or one pointing at whitespace) is not a location, so it must not
            // become a boot path and must not suppress the normal resolution below it.
            var decision = TS1InstallSelection.Resolve(true, null, false, null, Roster(Boxed));

            Assert.Equal(SimsInstallDecisionKind.SingleCandidate, decision.Kind);
            Assert.Equal(Boxed.Path, decision.Path);
        }

        [Fact]
        public void ARememberedChoiceIsUsedWithoutAskingAgain()
        {
            // The whole point of remembering: a player with two installs who has already
            // answered is never asked a second time, even though discovery keeps offering two.
            var decision = TS1InstallSelection.Resolve(false, null, true, Steam.Path, Roster(Boxed, Steam));

            Assert.Equal(SimsInstallDecisionKind.RememberedPath, decision.Kind);
            Assert.Equal(Steam.Path, decision.Path);
            Assert.True(decision.IsSettled);
            Assert.False(decision.NeedsPrompt);
        }

        [Fact]
        public void ARememberedChoiceOutranksDiscoveryThatFindsNothingThisTime()
        {
            // Discovery can fail for reasons that have nothing to do with the install: Steam's
            // registry key locked down, a library on a drive that is not mounted. The
            // remembered directory is a validated Sims install, so re-asking the player about
            // it would be answering a question they already answered.
            var decision = TS1InstallSelection.Resolve(false, null, true, Boxed.Path, Roster());

            Assert.Equal(SimsInstallDecisionKind.RememberedPath, decision.Kind);
            Assert.Equal(Boxed.Path, decision.Path);
        }

        [Fact]
        public void OneInstallIsUsedWithoutAsking()
        {
            // The overwhelmingly common case. Any prompt here would be a dialog whose only
            // possible answer is the one the game was already going to use.
            var decision = TS1InstallSelection.Resolve(false, null, false, null, Roster(Boxed));

            Assert.Equal(SimsInstallDecisionKind.SingleCandidate, decision.Kind);
            Assert.Equal(Boxed.Path, decision.Path);
            Assert.True(decision.IsSettled);
            Assert.False(decision.NeedsPrompt);
            // The candidate comes back with the decision: the caller has to remember the
            // edition and the label, and the path alone cannot answer either.
            Assert.Single(decision.Candidates);
            Assert.Equal(SimsInstallKind.Registry, decision.Candidates[0].Kind);
        }

        [Fact]
        public void MoreThanOneInstallIsPutToThePlayer()
        {
            var decision = TS1InstallSelection.Resolve(false, null, false, null, Roster(Boxed, Steam));

            Assert.Equal(SimsInstallDecisionKind.MultipleCandidates, decision.Kind);
            Assert.Null(decision.Path);
            Assert.True(decision.NeedsPrompt);
            Assert.False(decision.IsSettled);
        }

        [Fact]
        public void TheRosterReachesTheSelectorInPrecedenceOrder()
        {
            // Order is the ladder's: the pre-selected row is the install the game would have
            // booted on by itself, so the order is not cosmetic.
            var decision = TS1InstallSelection.Resolve(false, null, false, null, Roster(Portable, Boxed, Steam));

            Assert.Equal(SimsInstallDecisionKind.MultipleCandidates, decision.Kind);
            Assert.Equal(new[] { Portable.Path, Boxed.Path, Steam.Path },
                new[] { decision.Candidates[0].Path, decision.Candidates[1].Path, decision.Candidates[2].Path });
        }

        [Fact]
        public void NothingFoundAndNothingRememberedMeansThePlayerHasToBeAskedToPointAtIt()
        {
            var decision = TS1InstallSelection.Resolve(false, null, false, null, Roster());

            Assert.Equal(SimsInstallDecisionKind.None, decision.Kind);
            Assert.Null(decision.Path);
            Assert.True(decision.NeedsPrompt);
            Assert.Empty(decision.Candidates);
        }

        [Fact]
        public void ANullRosterIsAnEmptyRoster()
        {
            // Discovery returning null rather than an empty list is a shape a caller should
            // not have to defend against separately, and must not turn into a null reference
            // at the "is there exactly one?" question.
            var decision = TS1InstallSelection.Resolve(false, null, false, null, null);

            Assert.Equal(SimsInstallDecisionKind.None, decision.Kind);
            Assert.NotNull(decision.Candidates);
            Assert.Empty(decision.Candidates);
        }

        [Fact]
        public void AConfigurationThatHasGoneStaleWithNothingToReplaceItIsDistinctFromNeverBeingConfigured()
        {
            // Both have no directory to boot on, but they are different problems for the
            // player: their Sims folder moved, versus they have never told us where it is.
            var stale = TS1InstallSelection.Resolve(false, null, true, null, Roster());

            Assert.Equal(SimsInstallDecisionKind.StaleRememberedPath, stale.Kind);
            Assert.True(stale.NeedsPrompt);
            Assert.Equal(SimsInstallDecisionKind.None,
                TS1InstallSelection.Resolve(false, null, false, null, Roster()).Kind);
        }

        [Fact]
        public void AStaleConfigurationFallsBackToTheRoster()
        {
            // Two installs and a remembered directory that has gone: the remembered one is no
            // longer a candidate, so this is a genuine choice again, not a decision already
            // made.
            var decision = TS1InstallSelection.Resolve(false, null, true, null, Roster(Boxed, Steam));

            Assert.Equal(SimsInstallDecisionKind.MultipleCandidates, decision.Kind);
            Assert.True(decision.NeedsPrompt);
        }

        [Fact]
        public void AStaleConfigurationWithOneInstallLeftIsNotAQuestion()
        {
            // There is only one install, so there is nothing to choose between however the old
            // choice turned out. Asking anyway would be a dialog with one row in it.
            var decision = TS1InstallSelection.Resolve(false, null, true, null, Roster(Steam));

            Assert.Equal(SimsInstallDecisionKind.SingleCandidate, decision.Kind);
            Assert.Equal(Steam.Path, decision.Path);
            Assert.False(decision.NeedsPrompt);
        }

        [Fact]
        public void TheConfiguredFlagAloneCountsAsConfigured()
        {
            Assert.True(TS1InstallSelection.IsAlreadyConfigured(true, null));
            Assert.True(TS1InstallSelection.IsAlreadyConfigured(true, Boxed.Path));
        }

        [Fact]
        public void AStoredPathThatStillValidatesCountsAsConfiguredEvenWithoutTheFlag()
        {
            // The flag is new in config.ini, so it reads false for every player who set Simitone
            // up before installation selection existed. Without this rule every one of them
            // gets a first-run dialog on their next launch, on a machine with one install and
            // nothing to decide - and with two installs, a question they have effectively
            // already answered.
            Assert.True(TS1InstallSelection.IsAlreadyConfigured(false, Boxed.Path));
        }

        [Fact]
        public void NeitherAFlagNorAStoredPathMeansNotConfigured()
        {
            // A fresh config carries the ini placeholder for the stored path, which is not an
            // install on anybody's machine - and which the caller is required to have run
            // past IsUsableInstallPath before passing it in, as below.
            Assert.False(TS1InstallSelection.IsAlreadyConfigured(false, null));
        }

        [Fact]
        public void AStoredPathOnlyCountsAsConfiguredOnceItStillValidates()
        {
            // The whole migration rule in one place: the flag is absent from every config
            // written before this feature, so "already configured" is decided by validating the
            // stored path. Compose it the way Program.Main does and the placeholder in a fresh
            // config is correctly not a configuration, while the same path once the sentinel is
            // there is.
            var install = Path.Combine(Path.GetTempPath(), "simitone-configured-" + System.Guid.NewGuid().ToString("N"));
            var gameData = Path.Combine(install, TS1InstallSelection.GameDataFolderName);
            try
            {
                Directory.CreateDirectory(gameData);
                Assert.False(TS1InstallSelection.IsUsableInstallPath(install));
                Assert.False(TS1InstallSelection.IsAlreadyConfigured(false,
                    TS1InstallSelection.IsUsableInstallPath(install) ? install : null));

                File.WriteAllBytes(Path.Combine(gameData, TS1InstallSelection.InstallSentinelName), new byte[] { 0 });
                Assert.True(TS1InstallSelection.IsUsableInstallPath(install));
                Assert.True(TS1InstallSelection.IsAlreadyConfigured(false,
                    TS1InstallSelection.IsUsableInstallPath(install) ? install : null));
            }
            finally
            {
                if (Directory.Exists(install)) Directory.Delete(install, true);
            }
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void NothingIsNotAUsableInstallPath(string path)
        {
            Assert.False(TS1InstallSelection.IsUsableInstallPath(path));
        }

        [Fact]
        public void AFolderThatIsNotATheSimsInstallIsRejected()
        {
            // The check that keeps a stale registry entry out of the roster, and a stored path
            // to a deleted install from being booted on.
            var folder = Path.Combine(Path.GetTempPath(), "simitone-notsims-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(Path.Combine(folder, TS1InstallSelection.GameDataFolderName));
                Assert.False(TS1InstallSelection.IsUsableInstallPath(folder));
            }
            finally
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, true);
            }
        }

        [Fact]
        public void ADiscoveredSteamInstallIsAlwaysTreatedAsSteam()
        {
            // The edition discovery reported is the answer; there is nothing to infer.
            Assert.True(TS1InstallSelection.IsSteamInstall(Steam.Path, discoveredAsSteam: true, pathCameFromThePlayer: false));
            // Even for a player-typed path: if discovery named the same directory Steam, the
            // saves are in Saved Games whichever way the player got there.
            Assert.True(TS1InstallSelection.IsSteamInstall(Steam.Path, discoveredAsSteam: true, pathCameFromThePlayer: true));
        }

        [Fact]
        public void APlayerNamedPathHasItsEditionInferredFromTheLayout()
        {
            // A -path argument (or a folder from the browser) has no roster entry behind it, so
            // the only evidence available is the shape of the path. A Steam library always
            // stores games under <library>/steamapps/common.
            Assert.True(TS1InstallSelection.IsSteamInstall(Steam.Path, discoveredAsSteam: false, pathCameFromThePlayer: true));
            Assert.False(TS1InstallSelection.IsSteamInstall(Boxed.Path, discoveredAsSteam: false, pathCameFromThePlayer: true));
        }

        [Fact]
        public void ADiscoveryPathIsNeverInferredAsSteam()
        {
            // Inference is for the player's own path only. Discovery names an edition for
            // everything it finds, so guessing on top of it could override a correct "boxed"
            // answer with a guess - and a Steam install bootstrapped from its own (empty) game
            // folder is the failure that guess produces.
            Assert.False(TS1InstallSelection.IsSteamInstall(Steam.Path, discoveredAsSteam: false, pathCameFromThePlayer: false));
        }

        [Fact]
        public void ABoxedInstallBootstrapsFromTheInstallDirectoryAndASteamOneFromSavedGames()
        {
            // The single fact that decides where the first neighbourhood is copied from. The
            // Legacy Collection writes its UserData outside the game folder entirely, so the
            // two are not interchangeable and neither directory is an acceptable substitute for
            // the other. Both are the neighbourhood folder itself, because that is the folder
            // the copy reads - the Saved Games root on its own would name a directory Simitone
            // never opens.
            Assert.Equal(Path.Combine(Boxed.Path, TS1UserData.NeighborhoodFolderName),
                TS1InstallSelection.BootstrapSourceDirectory(Boxed.Path, isSteamInstall: false, userProfile: "C:/Users/player"));
            Assert.Equal(TS1UserData.SteamNeighborhoodPath("C:/Users/player"),
                TS1InstallSelection.BootstrapSourceDirectory(Steam.Path, isSteamInstall: true, userProfile: "C:/Users/player"));
            Assert.NotEqual(
                TS1InstallSelection.BootstrapSourceDirectory(Boxed.Path, isSteamInstall: false, userProfile: "C:/Users/player"),
                TS1InstallSelection.BootstrapSourceDirectory(Steam.Path, isSteamInstall: true, userProfile: "C:/Users/player"));
        }

        [Fact]
        public void ThereIsNoSourceDirectoryWithoutAnInstallOrAProfile()
        {
            // Both are passed straight to File.Exists, which has to be able to see the "cannot
            // happen" case rather than an exception from inside the pre-flight.
            Assert.Null(TS1InstallSelection.BootstrapSourceDirectory(null, isSteamInstall: false, userProfile: "C:/Users/player"));
            Assert.Null(TS1InstallSelection.BootstrapSourceDirectory(Steam.Path, isSteamInstall: true, userProfile: null));
            Assert.False(TS1InstallSelection.IsBootstrappable(null, isSteamInstall: false, userProfile: "C:/Users/player"));
            Assert.False(TS1InstallSelection.IsBootstrappable(Steam.Path, isSteamInstall: true, userProfile: ""));
        }

        [Fact]
        public void BootstrapSourceIsWhereTheProviderActuallyReadsFrom()
        {
            // Ties the two together on a real directory: FSO's TS1NeighbourhoodProvider copies
            // <UserData>/Neighborhood.iff out of exactly this folder, and the one file whose
            // absence there turns into a DirectoryNotFoundException from inside content init.
            var install = Path.Combine(Path.GetTempPath(), "simitone-bootstrap-" + System.Guid.NewGuid().ToString("N"));
            var neighborhood = Path.Combine(install, TS1UserData.NeighborhoodFolderName);
            try
            {
                Directory.CreateDirectory(neighborhood);
                Assert.False(TS1InstallSelection.IsBootstrappable(install, isSteamInstall: false, userProfile: null));

                File.WriteAllBytes(Path.Combine(neighborhood, TS1UserData.NeighborhoodResourceName), new byte[] { 0 });
                Assert.True(TS1InstallSelection.IsBootstrappable(install, isSteamInstall: false, userProfile: null));
                Assert.Equal(Path.Combine(neighborhood, TS1UserData.NeighborhoodResourceName),
                    Path.Combine(TS1InstallSelection.BootstrapSourceDirectory(install, isSteamInstall: false, userProfile: null),
                        TS1UserData.NeighborhoodResourceName));
            }
            finally
            {
                if (Directory.Exists(install)) Directory.Delete(install, true);
            }
        }

        [Fact]
        public void ALegacyCollectionIsCheckedInSavedGamesRatherThanInItsOwnFolder()
        {
            // The regression this whole branch exists to prevent. A Legacy Collection install
            // has an essentially empty UserData in the game directory - the game writes its
            // saves to Saved Games - so the install-directory check fails for every Steam
            // player while their real saves sit right there.
            var profile = Path.Combine(Path.GetTempPath(), "simitone-profile-" + System.Guid.NewGuid().ToString("N"));
            var install = Path.Combine(Path.GetTempPath(), "simitone-steam-" + System.Guid.NewGuid().ToString("N"));
            try
            {
                // A complete-looking install with a UserData folder and nothing in it, which is
                // exactly what a never-launched Legacy Collection looks like.
                Directory.CreateDirectory(Path.Combine(install, TS1UserData.NeighborhoodFolderName));
                Assert.False(TS1InstallSelection.IsBootstrappable(install, isSteamInstall: true, userProfile: profile));

                var savedGames = Path.Combine(TS1UserData.SteamSavesRoot(profile), TS1UserData.NeighborhoodFolderName);
                Directory.CreateDirectory(savedGames);
                File.WriteAllBytes(Path.Combine(savedGames, TS1UserData.NeighborhoodResourceName), new byte[] { 0 });
                Assert.True(TS1InstallSelection.IsBootstrappable(install, isSteamInstall: true, userProfile: profile));
            }
            finally
            {
                if (Directory.Exists(install)) Directory.Delete(install, true);
                if (Directory.Exists(profile)) Directory.Delete(profile, true);
            }
        }
    }
}
