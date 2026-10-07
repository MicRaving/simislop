using Simitone.Client.Utils;
using System.Collections.Generic;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Covers the value the discovery layer hands to the installation selector: what a
    /// candidate is, how two spellings of the same directory are recognised as one, and which
    /// paths count as a Steam install.
    ///
    /// The path handling is the part worth pinning down. The four discovery probes do not
    /// agree on how to spell a path - the Maxis registry probe appends a backslash, the Steam
    /// probe appends one and then converts to forward slashes, and a portable install is a
    /// relative <c>../The Sims/</c> - so the roster has to be able to tell that
    /// <c>C:\Games\The Sims\</c> and <c>C:/Games/The Sims/</c> are one install. If it cannot,
    /// the selector offers the same directory twice, and a player who picks either row is
    /// told (via the edition that came with the row) the wrong thing about where their saves
    /// are.
    /// </summary>
    public sealed class SimsInstallCandidateTests
    {
        [Theory]
        [InlineData("C:/Games/The Sims/", "C:/Games/The Sims")]
        [InlineData("C:\\Games\\The Sims\\", "C:/Games/The Sims")]
        [InlineData("  C:/Games/The Sims/  ", "C:/Games/The Sims")]
        [InlineData("../The Sims/", "../The Sims")]
        public void CanonicalSpellingIsReducedToOneForm(string input, string expected)
        {
            Assert.Equal(expected, SimsInstallCandidate.Canonical(input));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AnAbsentPathCanonicalisesToNull(string input)
        {
            // Callers hand discovery results straight to File.Exists, which treats null as
            // "missing" but throws on an empty path in some overloads, so "nothing" has to
            // normalise to the one value that is safe everywhere.
            Assert.Null(SimsInstallCandidate.Canonical(input));
        }

        [Fact]
        public void ADriveRootIsAKeepableDirectoryNotATrailingSlash()
        {
            // "C:/" and "C:" are different paths on Windows (root versus drive-relative), and
            // folding them together would compare two genuinely different installs as one.
            Assert.Equal("C:/", SimsInstallCandidate.Canonical("C:/"));
            Assert.Equal("C:/", SimsInstallCandidate.Canonical("C:\\"));
            Assert.False(SimsInstallCandidate.IsSamePath("C:/", "C:/Games/The Sims/"));
        }

        [Theory]
        [InlineData("C:\\Games\\The Sims\\", "C:/Games/The Sims/")]
        [InlineData("C:/Games/The Sims", "C:/Games/The Sims/")]
        [InlineData("C:/GAMES/the sims/", "c:/games/THE SIMS/")]
        public void TheSameDirectoryInDifferentSpellingsIsTheSamePath(string a, string b)
        {
            Assert.True(SimsInstallCandidate.IsSamePath(a, b));
        }

        [Fact]
        public void TwoAbsentPathsAreTheSamePathAndAreNotAnyRealPath()
        {
            // The dedupe test depends on this: "not found" must equal "not found" so a probe
            // that missed never looks like a duplicate of another miss.
            Assert.True(SimsInstallCandidate.IsSamePath(null, ""));
            Assert.False(SimsInstallCandidate.IsSamePath(null, "C:/Games/The Sims/"));
            Assert.False(SimsInstallCandidate.IsSamePath("C:/Games/The Sims/", null));
        }

        [Fact]
        public void DifferentDirectoriesAreNotTheSamePath()
        {
            Assert.False(SimsInstallCandidate.IsSamePath("C:/Games/The Sims/", "D:/Games/The Sims/"));
            // Prefix, not equality: a folder that contains another install is not that install.
            Assert.False(SimsInstallCandidate.IsSamePath("C:/Games/", "C:/Games/The Sims/"));
        }

        [Theory]
        [InlineData("D:/Program Files/Steam/steamapps/common/The Sims Legacy Collection/", true)]
        [InlineData("C:/Program Files (x86)/Steam/steamapps/common/The Sims Legacy Collection", true)]
        [InlineData("C:/Games/The Sims/", false)]
        [InlineData("../The Sims/", false)]
        // The trailing slash on "common" is part of the layout Steam creates, so a path that
        // merely starts the same way is not a library game.
        [InlineData("C:/Games/steamapps/commoner/The Sims/", false)]
        [InlineData("C:/Games/steamapps/", false)]
        [InlineData(null, false)]
        public void SteamLibraryPathsAreRecognisedFromTheirLayout(string path, bool expected)
        {
            Assert.Equal(expected, SimsInstallCandidate.LooksLikeSteamLibraryPath(path));
        }

        [Fact]
        public void ACandidateWithoutADescriptionFallsBackToTheNameOfItsEdition()
        {
            // Every row in the selector needs a label, and only discovery knows the detail; the
            // per-edition default is what keeps a caller that passes no description from
            // rendering a blank row.
            var steam = new SimsInstallCandidate("D:/Steam/steamapps/common/The Sims Legacy Collection/", SimsInstallKind.Steam);
            var unknown = new SimsInstallCandidate("D:/Games/The Sims/", SimsInstallKind.Unknown);

            Assert.Equal(SimsInstallKinds.Describe(SimsInstallKind.Steam), steam.Description);
            Assert.Equal(SimsInstallKinds.Describe(SimsInstallKind.Unknown), unknown.Description);
            Assert.False(string.IsNullOrWhiteSpace(steam.Description));
        }

        [Fact]
        public void AnExplicitDescriptionIsKept()
        {
            var candidate = new SimsInstallCandidate("D:/Games/The Sims/", SimsInstallKind.Registry, "The Sims: Deluxe (C:\\Games)");

            Assert.Equal("The Sims: Deluxe (C:\\Games)", candidate.Description);
        }

        [Fact]
        public void EveryEditionHasItsOwnName()
        {
            // Two editions sharing a label would make the selector a coin flip, which is the
            // one thing the dialog exists to avoid.
            var labels = new[]
            {
                SimsInstallKinds.Describe(SimsInstallKind.Portable),
                SimsInstallKinds.Describe(SimsInstallKind.Registry),
                SimsInstallKinds.Describe(SimsInstallKind.Steam)
            };

            Assert.Equal(labels.Length, new HashSet<string>(labels).Count);
        }

        [Fact]
        public void OnlyTheSteamEditionIsSteam()
        {
            // This is the flag that sends the neighbourhood bootstrap to Saved Games, so a
            // mis-set value sends a retail install looking for save files that are not there.
            Assert.True(new SimsInstallCandidate("D:/Steam/steamapps/common/x/", SimsInstallKind.Steam).IsSteam);
            Assert.False(new SimsInstallCandidate("../The Sims/", SimsInstallKind.Portable).IsSteam);
            Assert.False(new SimsInstallCandidate("C:/Games/The Sims/", SimsInstallKind.Registry).IsSteam);
            Assert.False(new SimsInstallCandidate("C:/Games/The Sims/", SimsInstallKind.Unknown).IsSteam);
        }

        [Fact]
        public void ARosterTakesAProbeResultAndSkipsAMiss()
        {
            var roster = new List<SimsInstallCandidate>();

            Assert.True(SimsInstallCandidate.TryAddTo(roster, "C:/Games/The Sims/", SimsInstallKind.Registry));
            // A probe that found nothing, and a path that is nothing but whitespace, are the
            // same answer and neither may become a row.
            Assert.False(SimsInstallCandidate.TryAddTo(roster, null, SimsInstallKind.Steam));
            Assert.False(SimsInstallCandidate.TryAddTo(roster, "  ", SimsInstallKind.Steam));

            Assert.Single(roster);
            Assert.Equal("C:/Games/The Sims/", roster[0].Path);
        }

        [Fact]
        public void OneDirectoryReachedByTwoProbesIsOfferedOnce()
        {
            // Real case: a Steam install that is also registered by the Maxis installer, or a
            // portable folder that is a Steam library. Two rows for one directory means two
            // rows that carry different editions, and picking either would silently change
            // where saves are read from.
            var roster = new List<SimsInstallCandidate>();

            Assert.True(SimsInstallCandidate.TryAddTo(roster, "C:/Games/The Sims/", SimsInstallKind.Registry));
            Assert.False(SimsInstallCandidate.TryAddTo(roster, "C:\\Games\\The Sims\\", SimsInstallKind.Steam));
            Assert.False(SimsInstallCandidate.TryAddTo(roster, "C:/Games/The Sims", SimsInstallKind.Portable));

            Assert.Single(roster);
        }

        [Fact]
        public void TheFirstProbeToClaimADirectoryKeepsItsEdition()
        {
            // The roster is assembled in the same precedence order the boot ladder uses, so
            // the surviving entry is the one the game would have booted on by itself.
            var roster = new List<SimsInstallCandidate>();

            SimsInstallCandidate.TryAddTo(roster, "C:/Games/The Sims/", SimsInstallKind.Portable);
            SimsInstallCandidate.TryAddTo(roster, "C:/Games/The Sims/", SimsInstallKind.Registry);

            Assert.Equal(SimsInstallKind.Portable, roster[0].Kind);
        }

        [Fact]
        public void ARosterOfNothingIsNotAnError()
        {
            var roster = new List<SimsInstallCandidate>();

            Assert.False(SimsInstallCandidate.TryAddTo(null, "C:/Games/The Sims/", SimsInstallKind.Registry));
            Assert.Empty(roster);
        }

        [Fact]
        public void ARosterEntryCanBeFoundAgainByItsPath()
        {
            // How the caller recovers the edition (and the label) from a path the player
            // picked, which is a plain string by the time it comes back from a dialog.
            var registry = new SimsInstallCandidate("C:/Games/The Sims/", SimsInstallKind.Registry);
            var steam = new SimsInstallCandidate("D:/Steam/steamapps/common/The Sims Legacy Collection/", SimsInstallKind.Steam);
            var roster = new List<SimsInstallCandidate> { registry, steam };

            Assert.Same(steam, SimsInstallCandidate.FindByPath(roster, "D:\\Steam\\steamapps\\common\\The Sims Legacy Collection"));
            Assert.Same(registry, SimsInstallCandidate.FindByPath(roster, "C:/Games/The Sims/"));
            Assert.Null(SimsInstallCandidate.FindByPath(roster, "E:/Games/The Sims/"));
            Assert.Null(SimsInstallCandidate.FindByPath(roster, null));
            Assert.Null(SimsInstallCandidate.FindByPath(null, "C:/Games/The Sims/"));
        }

        [Fact]
        public void ACandidateDescribesItselfWithBothParts()
        {
            // Used in the log line that records what the player chose; a record that only has
            // one of the two is not enough to work out later what they picked.
            var text = new SimsInstallCandidate("D:/Games/The Sims/", SimsInstallKind.Registry).ToString();

            Assert.Contains("D:/Games/The Sims/", text);
            Assert.Contains(SimsInstallKinds.Describe(SimsInstallKind.Registry), text);
        }

        // ---- the two forms of a path, and which one is compared ---------------------------------

        [Theory]
        [InlineData("C:\\Games\\The Sims\\", "C:/Games/The Sims")]
        [InlineData("  D:/Steam/steamapps/common/The Sims Legacy Collection/  ", "D:/Steam/steamapps/common/The Sims Legacy Collection")]
        [InlineData("../The Sims/", "../The Sims")]
        [InlineData("C:/", "C:/")]
        [InlineData(null, null)]
        [InlineData("", null)]
        public void ACandidateCarriesTheReducedFormAlongsideTheOneTheGameRunsOn(string path, string expected)
        {
            // Path is what FSOEnvironment and GamePathArgument are handed, so it keeps the trailing
            // slash and the probe's own spelling. CanonicalPath is the same directory reduced to
            // one form, and it is what "are these the same install?" compares. They used to be one
            // value and a convention: the object held the raw path and every comparison had to
            // remember to reduce it first, so the reduction rule lived in three call sites.
            var candidate = new SimsInstallCandidate(path, SimsInstallKind.Registry);

            Assert.Equal(path, candidate.Path);
            Assert.Equal(expected, candidate.CanonicalPath);
            Assert.Equal(SimsInstallCandidate.Canonical(path), candidate.CanonicalPath);
        }

        [Fact]
        public void ComparingTwoCandidatesIsTheSameAsComparingTwoPaths()
        {
            // The pair-wise form has to agree with the string one in every direction, including
            // the "neither of them is anywhere" case the dedupe depends on: two probes that both
            // missed are the same answer, and neither is a real install.
            var roster = new List<SimsInstallCandidate>
            {
                new SimsInstallCandidate("C:\\Games\\The Sims\\", SimsInstallKind.Registry),
                new SimsInstallCandidate("c:/games/the sims/", SimsInstallKind.Steam),
                new SimsInstallCandidate("D:/Games/The Sims/", SimsInstallKind.Portable),
                new SimsInstallCandidate(null, SimsInstallKind.Steam),
                new SimsInstallCandidate("   ", SimsInstallKind.Steam),
            };

            for (int i = 0; i < roster.Count; i++)
            {
                for (int j = 0; j < roster.Count; j++)
                {
                    Assert.Equal(SimsInstallCandidate.IsSamePath(roster[i].Path, roster[j].Path),
                        SimsInstallCandidate.IsSameInstall(roster[i], roster[j]));
                }
            }

            //and the two probes that both missed are the same answer to it
            Assert.True(SimsInstallCandidate.IsSameInstall(roster[3], roster[4]));

            //while a null candidate is not an install, whatever the other side is - which is why
            //this is a separate name and not an overload: an overload would make the null case
            //ambiguous, and the string form's "two nulls are the same path" is a different answer
            Assert.False(SimsInstallCandidate.IsSameInstall(null, roster[0]));
            Assert.False(SimsInstallCandidate.IsSameInstall(roster[0], null));
            Assert.False(SimsInstallCandidate.IsSameInstall(null, null));
            Assert.True(SimsInstallCandidate.IsSamePath(null, null));
        }

        [Fact]
        public void ARosterBuiltThroughTryAddToHasNoTwoRowsForOneDirectory()
        {
            // The property, stated on its own rather than inferred from three separate tests: the
            // dedupe is a property of how a roster is built, so every entry in it - whatever
            // spelling its probe produced - is a directory no other entry names.
            var roster = new List<SimsInstallCandidate>();

            //Four probes, one directory, four spellings, in the order discovery consults them.
            Assert.True(SimsInstallCandidate.TryAddTo(roster, "C:\\Games\\The Sims\\", SimsInstallKind.Registry));
            Assert.False(SimsInstallCandidate.TryAddTo(roster, "C:/Games/The Sims/", SimsInstallKind.Steam));
            Assert.False(SimsInstallCandidate.TryAddTo(roster, "  C:/GAMES/THE SIMS  ", SimsInstallKind.Portable));
            Assert.True(SimsInstallCandidate.TryAddTo(roster, "D:/Games/The Sims/", SimsInstallKind.Registry));

            Assert.Equal(2, roster.Count);
            for (int i = 0; i < roster.Count; i++)
            {
                for (int j = i + 1; j < roster.Count; j++)
                {
                    Assert.False(SimsInstallCandidate.IsSameInstall(roster[i], roster[j]),
                        roster[i].Path + " and " + roster[j].Path + " are the same install offered twice");
                }
            }
        }
    }
}
