using FSO.Content.TS1;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Content
{
    /// <summary>
    /// The save-path decision in <see cref="TS1AvatarSavePath"/>, which is what stops
    /// <c>TS1NeighborhoodProvider.SaveNeighbourhood(true)</c> from writing an avatar IFF
    /// somewhere nothing will ever read it back from.
    ///
    /// <para>
    /// The bug it pins: the old code did <c>new FileStream(obj.Resource.Name, FileMode.Create)</c>.
    /// <c>GameObjectResource.Name</c> is the object's <i>reference</i> filename, and for a
    /// FAR-resident NPC that is a <b>bare archive entry name</b> - <c>maid.iff</c>,
    /// <c>Bones.iff</c>, <c>thief.iff</c>, all of which ship. <c>FileStream</c> resolves a
    /// relative name against the process working directory, and <c>FileMode.Create</c>
    /// <i>truncates</i>, so every save dropped a loose <c>.\maid.iff</c> into the working
    /// directory - destroying whatever already had that name if anything did - while the
    /// neighbourhood reader, which only ever enumerates <c>&lt;UserPath&gt;/Characters</c>,
    /// never looked there. The write was invisible and lossy at the same time.
    /// </para>
    ///
    /// <para>
    /// The rule these tests encode is the reader's, not the writer's convenience: a save may
    /// only write where <c>LoadCharacters</c> will look, which means a <c>.iff</c> that is a
    /// direct child of the neighbourhood's <c>Characters</c> folder. Everything else is skipped
    /// with a recorded reason, and "skipped" is the correct answer for retail NPCs - they are
    /// not user data and their IFF must not be rewritten in place either.
    /// </para>
    ///
    /// <para>
    /// This class answers <i>where may a save write</i>. What happens when it does - the commit
    /// contract of the neighbourhood save itself, and what the reader does with a file it is not
    /// expecting - is in <c>TS1NeighbourhoodPersistenceTests</c>.
    /// </para>
    ///
    /// <para>
    /// Only the file system touched is a throwaway temp tree created per test and removed in
    /// <see cref="Dispose"/>; no TS1 install, VM, content manager or graphics device is needed,
    /// and no test changes the process working directory (that would be a cross-test hazard for
    /// no extra coverage - the bare-name case is rejected before any path resolution happens).
    /// </para>
    /// </summary>
    public sealed class TS1AvatarSavePathTests : IDisposable
    {
        private readonly string _root;

        public TS1AvatarSavePathTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-ts1-avatarsave-" + Guid.NewGuid().ToString("N"));
            Characters = Path.Combine(_root, "UserData1", TS1AvatarSavePath.CharactersFolderName);
            Install = Path.Combine(_root, "The Sims");
            Directory.CreateDirectory(Characters);
            Directory.CreateDirectory(Install);
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

        private string Characters { get; }
        private string Install { get; }

        private string CharacterFile(string name) => Path.Combine(Characters, name);

        /// <summary>
        /// The folder literals the reader (<c>LoadCharacters</c>) and the new-character writer
        /// (<c>SaveNewNeighbour</c>) use. Renaming either without the other silently stops
        /// saves from round-tripping, so the two literals are pinned here.
        /// </summary>
        [Fact]
        public void TheCharactersFolderAndExtensionAreTheOnesTheReaderUses()
        {
            Assert.Equal("Characters", TS1AvatarSavePath.CharactersFolderName);
            Assert.Equal(".iff", TS1AvatarSavePath.IffExtension);
        }

        [Fact]
        public void AUserCharacterResolvesBackToTheFileItWasLoadedFrom()
        {
            var file = CharacterFile("User00001.iff");
            File.WriteAllText(file, "resident");

            var target = TS1AvatarSavePath.Resolve(Characters, file, Install);

            Assert.Equal(TS1AvatarSaveDecision.Write, target.Decision);
            Assert.True(target.ShouldWrite);
            Assert.Equal(Path.GetFullPath(file), target.Path);
        }

        /// <summary>
        /// The regression. A bare FAR entry name must be refused as FAR-resident <i>before</i>
        /// any attempt to turn it into a path, because that attempt is what used to resolve it
        /// against the working directory.
        /// </summary>
        [Theory]
        [InlineData("maid.iff")]
        [InlineData("Bones.iff")]
        [InlineData("thief.iff")]
        [InlineData("repairman.iff")]
        [InlineData("  maid.iff  ")]
        public void ABareArchiveEntryNameIsSkippedAsFarResidentAndNeverBecomesAPath(string name)
        {
            //same file, sitting in the reader's folder, so the "it is not in Characters" answer
            //is unambiguously about the name's shape rather than about a missing file.
            File.WriteAllText(CharacterFile("maid.iff"), "decoy");

            var target = TS1AvatarSavePath.Resolve(Characters, name, Install);

            Assert.Equal(TS1AvatarSaveDecision.SkipFarResident, target.Decision);
            Assert.False(target.ShouldWrite);
            Assert.Null(target.Path);
        }

        [Fact]
        public void ACustomContentObjectWithARelativePathIsSkippedWhenThereIsNoInstallToResolveItAgainst()
        {
            //this is the exact shape TS1ObjectProvider names a Downloads person with
            var target = TS1AvatarSavePath.Resolve(Characters, Path.Combine("Downloads", "MyPack", "Maid.iff"), null);

            Assert.Equal(TS1AvatarSaveDecision.SkipUnresolvable, target.Decision);
            Assert.Null(target.Path);
        }

        [Fact]
        public void ACustomContentObjectResolvedAgainstTheInstallIsStillSkippedBecauseItIsNotOurs()
        {
            var inInstall = Path.Combine(Install, "Downloads", "MyPack", "Maid.iff");
            Directory.CreateDirectory(Path.GetDirectoryName(inInstall));
            File.WriteAllText(inInstall, "retail cc");

            var target = TS1AvatarSavePath.Resolve(Characters, Path.Combine("Downloads", "MyPack", "Maid.iff"), Install);

            Assert.Equal(TS1AvatarSaveDecision.SkipNotRoundTrippable, target.Decision);
            Assert.Null(target.Path);
            //and emphatically not truncated
            Assert.Equal("retail cc", File.ReadAllText(inInstall));
        }

        [Fact]
        public void AFileOutsideTheNeighbourhoodIsSkipped()
        {
            var elsewhere = Path.Combine(_root, "Somebody Elses Neighbourhood", "Characters", "User00002.iff");

            var target = TS1AvatarSavePath.Resolve(Characters, elsewhere, Install);

            Assert.Equal(TS1AvatarSaveDecision.SkipNotRoundTrippable, target.Decision);
            Assert.Null(target.Path);
        }

        /// <summary>
        /// <c>LoadCharacters</c> enumerates the <c>Characters</c> folder with
        /// <c>Directory.EnumerateFiles</c>, which is not recursive. A file one level down would
        /// therefore be written and never read back, so it is skipped.
        /// </summary>
        [Fact]
        public void AFileNestedBelowCharactersIsSkippedBecauseTheReaderDoesNotRecurse()
        {
            var nested = CharacterFile(Path.Combine("Sub", "User00003.iff"));

            var target = TS1AvatarSavePath.Resolve(Characters, nested, Install);

            Assert.Equal(TS1AvatarSaveDecision.SkipNotRoundTrippable, target.Decision);
            Assert.Null(target.Path);
        }

        /// <summary>
        /// A side file beside an avatar - a <c>.bak</c> from a commit, a <c>.tmp</c> from an
        /// interrupted one - is not something to write, whatever the reader does with it.
        /// </summary>
        /// <remarks>
        /// This used to carry the comment "LoadCharacters also returns out of the whole loop on
        /// a non-.iff, so writing one would be doubly pointless" - i.e. it documented the
        /// <c>return</c>-instead-of-<c>continue</c> bug as a reason for the rule and so pinned
        /// the surrounding logic around it. The reader now skips a foreign file and carries on
        /// to the next one (see <c>TS1NeighbourhoodPersistenceTests</c>), and the rule stands
        /// on its own: the reader would not read it back, so a save must not write it.
        /// </remarks>
        [Fact]
        public void ANonIffFileInCharactersIsSkipped()
        {
            var target = TS1AvatarSavePath.Resolve(Characters, CharacterFile("User00004.iff.bak"), Install);

            Assert.Equal(TS1AvatarSaveDecision.SkipNotRoundTrippable, target.Decision);
            Assert.Null(target.Path);
        }

        /// <summary>
        /// A naive "does the path start with the Characters folder" test would pass this one. It
        /// is not a naive test: <see cref="TS1AvatarSavePath.Resolve"/> runs
        /// <c>Path.GetFullPath</c> first, which collapses the <c>..</c>, and then compares the
        /// parent directory for equality rather than testing a prefix.
        /// </summary>
        [Fact]
        public void ARelativeEscapeOutOfTheCharactersFolderIsSkipped()
        {
            var escaping = CharacterFile(Path.Combine("..", "..", "Neighborhood.iff"));

            var target = TS1AvatarSavePath.Resolve(Characters, escaping, Install);

            Assert.Equal(TS1AvatarSaveDecision.SkipNotRoundTrippable, target.Decision);
            Assert.Null(target.Path);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void AResourceWithNoNameIsSkipped(string name)
        {
            var target = TS1AvatarSavePath.Resolve(Characters, name, Install);

            Assert.Equal(TS1AvatarSaveDecision.SkipNoName, target.Decision);
            Assert.Null(target.Path);
        }

        /// <summary>
        /// Folder and file names come off disk on Windows, so the comparison has to be
        /// case-insensitive, and the Characters path is built with a trailing <c>/</c>.
        /// </summary>
        [Fact]
        public void TheComparisonIgnoresCaseAndTrailingSeparators()
        {
            var file = CharacterFile("User00005.iff");
            var withTrailingSlash = Characters + "/";
            var shouted = Characters.ToUpperInvariant();

            Assert.True(TS1AvatarSavePath.Resolve(withTrailingSlash, file, Install).ShouldWrite);
            Assert.True(TS1AvatarSavePath.Resolve(shouted, file, Install).ShouldWrite);
            Assert.True(TS1AvatarSavePath.Resolve(Characters + Path.DirectorySeparatorChar, file, Install).ShouldWrite);
        }

        /// <summary>
        /// FAR entry names are joined with <c>/</c> in a few places in the TS1 providers, so a
        /// forward slash has to count as a separator too - otherwise a
        /// <c>Characters/User00006.iff</c>-shaped name would be mistaken for a bare archive entry.
        /// </summary>
        [Fact]
        public void ForwardSlashesCountAsSeparators()
        {
            var forward = (Characters + "/User00006.iff").Replace('\\', '/');

            Assert.Equal(TS1AvatarSaveDecision.Write, TS1AvatarSavePath.Resolve(Characters, forward, Install).Decision);
        }

        [Fact]
        public void NoDecisionButWriteEverCarriesAPath()
        {
            string[] names =
            {
                null, "", "maid.iff", Path.Combine("Downloads", "P", "Maid.iff"),
                Path.Combine(_root, "elsewhere.iff"), CharacterFile("Sub/x.iff"), CharacterFile("x.iff.bak"),
            };

            foreach (var name in names)
            {
                var target = TS1AvatarSavePath.Resolve(Characters, name, Install);
                if (target.Decision == TS1AvatarSaveDecision.Write)
                {
                    Assert.False(string.IsNullOrEmpty(target.Path));
                    Assert.True(target.ShouldWrite);
                }
                else
                {
                    Assert.Null(target.Path);
                    Assert.False(target.ShouldWrite);
                }
            }
        }

        /// <summary>
        /// The whole point, end to end and without a neighbourhood: an IFF written to the
        /// resolved destination is found again by exactly the filter <c>LoadCharacters</c> uses,
        /// and comes back out of the file unchanged.
        /// </summary>
        [Fact]
        public void AnIffWrittenToTheResolvedDestinationIsFoundByTheReadersOwnFilterAndReloadsIntact()
        {
            var source = CharacterFile("User00007.iff");
            var original = MakeIff("LoadCharacters must find me");
            WriteIff(original, source);

            //what SaveNeighbourhood's avatar loop does for this avatar
            var target = TS1AvatarSavePath.Resolve(Characters, source, Install);
            Assert.True(target.ShouldWrite);
            WriteIff(original, target.Path);

            //what LoadCharacters does on the next session
            var candidates = Directory.EnumerateFiles(Characters)
                .Where(f => Path.GetExtension(f) == TS1AvatarSavePath.IffExtension)
                .ToList();
            Assert.Contains(Path.GetFullPath(source), candidates.Select(p => Path.GetFullPath(p)).ToList());

            var reloaded = new IffFile(target.Path);
            var str = reloaded.Get<STR>(7);
            Assert.NotNull(str);
            Assert.Equal("LoadCharacters must find me", str.GetString(0));
        }

        private static IffFile MakeIff(string line)
        {
            var iff = new IffFile();
            var str = new STR();
            str.LanguageSets[0].Strings = new[]
            {
                new STRItem(line) { LanguageCode = (byte)STRLangCode.EnglishUS, Comment = "Sim Lane" },
            };
            iff.AddChunk(new STR
            {
                ChunkID = 7,
                ChunkLabel = "Saved avatar marker",
                ChunkProcessed = true,
                LanguageSets = str.LanguageSets,
            });
            return iff;
        }

        private static void WriteIff(IffFile iff, string path)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                iff.Write(stream);
        }
    }
}
