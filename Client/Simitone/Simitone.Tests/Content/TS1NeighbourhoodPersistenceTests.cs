using FSO.Content.TS1;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

//this file's own namespace is Simitone.Tests.Content, so the unqualified name "Content" is
//this namespace rather than FSO.Content.Content.
using FSOContent = FSO.Content.Content;
using GameObjectReference = FSO.Content.GameObjectReference;
using AtomicFileWrite = FSO.Content.Utils.AtomicFileWrite;

namespace Simitone.Tests.Content
{
    /// <summary>
    /// The three ways <see cref="TS1NeighborhoodProvider"/> could destroy or silently truncate a
    /// player's save, pinned against synthetic folders. No TS1 install, no <c>Content.Init</c>,
    /// no graphics device, no VM - and no UI.
    ///
    /// <para>
    /// The provider is the only writer of <c>Neighborhood.iff</c>, of the per-character IFFs and
    /// of the first-run folder copy, and none of the three had any coverage at all. The
    /// neighbourhood file is the most valuable one in a save - every family, every budget, the
    /// magicoins inventory - and it was the only one of the game's four persisted files written
    /// by opening it <c>FileMode.Create</c> and serialising straight into it, while the far less
    /// valuable <c>House{NN}.iff</c>, the travel slot and the JSON sidecar all went through
    /// <c>AtomicFileCommit</c>.
    /// </para>
    ///
    /// <para>
    /// The provider is reached without its constructor. <c>TS1NeighborhoodProvider(Content)</c>
    /// immediately calls <c>InitSpecific(0)</c>, which needs a whole TS1 install, so the
    /// instance is allocated uninitialised and the two fields the save path actually reads -
    /// <c>UserPath</c> and <c>MainResource</c> - are set by hand. That is not a mock: it is the
    /// real object, the real <c>SaveNeighbourhood</c> and the real <c>LoadCharacters</c>, with
    /// the file discovery they depend on left out. <c>Content</c> is private-constructed and does
    /// the same thing, since the provider only ever reads <c>Content.WorldObjects</c>.
    /// </para>
    /// </summary>
    public sealed class TS1NeighbourhoodPersistenceTests : IDisposable
    {
        private readonly string _root;
        private readonly string _userPath;

        public TS1NeighbourhoodPersistenceTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-neigh-persist-" + Guid.NewGuid().ToString("N"));
            _userPath = Path.Combine(_root, "UserData");
            Directory.CreateDirectory(_userPath);
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

        #region the provider, without a TS1 install

        private static TS1NeighborhoodProvider NewProvider(string userPath, IffFile mainResource, TS1ObjectProvider objects = null)
        {
            var provider = (TS1NeighborhoodProvider)RuntimeHelpers.GetUninitializedObject(typeof(TS1NeighborhoodProvider));
            provider.UserPath = userPath;
            provider.MainResource = mainResource;
            provider.ContentManager = NewContent(objects);
            return provider;
        }

        private static FSOContent NewContent(TS1ObjectProvider objects)
        {
            var content = (FSOContent)RuntimeHelpers.GetUninitializedObject(typeof(FSOContent));
            if (objects != null) content.WorldObjects = objects;
            return content;
        }

        private static TS1ObjectProvider NewObjectProvider()
        {
            var objects = new TS1ObjectProvider(null, null);
            //Init() is what normally fills this, and LoadCharacters is a no-op without it.
            objects.Entries = new Dictionary<ulong, GameObjectReference>();
            return objects;
        }

        private string NeighborhoodPath => Path.Combine(_userPath, "Neighborhood.iff");

        private string CharactersPath
        {
            get
            {
                var path = Path.Combine(_userPath, TS1AvatarSavePath.CharactersFolderName);
                Directory.CreateDirectory(path);
                return path;
            }
        }

        /// <summary>
        /// A stand-in for a neighbourhood resource: a few families, a neighbour list and a
        /// type-attribute table, which is what <c>Neighborhood.iff</c> actually holds. Every
        /// chunk is handed over already marked processed, because <c>IffFile.Write</c> would
        /// otherwise try to decode a chunk that has no raw bytes behind it.
        /// </summary>
        private static IffFile MakeNeighborhood(params (int chunkId, int houseNumber, int budget)[] families)
        {
            var iff = new IffFile();
            foreach (var family in families)
            {
                iff.AddChunk(new FAMI
                {
                    ChunkID = (ushort)family.chunkId,
                    ChunkType = "FAMI",
                    ChunkLabel = "Family",
                    ChunkProcessed = true,
                    HouseNumber = family.houseNumber,
                    FamilyNumber = family.chunkId,
                    Budget = family.budget,
                });
            }
            return iff;
        }

        private static void WriteIff(IffFile iff, string path)
        {
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
                iff.Write(stream);
        }

        private static IffFile Open(string path)
        {
            //IffFile's finalizer registers it with FreeSO's process-wide timed reference cache,
            //which nothing ticks in a test host; throwaways keep it out of the way.
            var iff = new IffFile(path);
            iff.MarkThrowaway();
            return iff;
        }

        private static int[] HouseNumbers(string path)
        {
            var families = Open(path).List<FAMI>();
            return families == null ? new int[0] : families.Select(f => f.HouseNumber).OrderBy(h => h).ToArray();
        }

        #endregion

        #region F-03 - Neighborhood.iff is committed, not truncated

        /// <summary>
        /// The regression. The file used to be opened <c>FileMode.Create</c>, which truncates at
        /// open, and then have every family, neighbour, inventory and TATT record serialised
        /// straight into it. Any failure part way through - disk full, a chunk's writer throwing,
        /// AV interference, a power cut - left a truncated neighbourhood with no recovery copy,
        /// and there is no in-game route back from that.
        /// </summary>
        [Fact]
        public void SavingTheNeighbourhoodTwiceLeavesAReadableBackupOfTheFirstSave()
        {
            var provider = NewProvider(_userPath, MakeNeighborhood((1, 30, 20000), (2, 31, 5000)));

            Assert.True(provider.SaveNeighbourhood(false));
            Assert.Equal(new[] { 30, 31 }, HouseNumbers(NeighborhoodPath));

            //the family buys a lot, and the neighbourhood is saved again
            provider.MainResource = MakeNeighborhood((1, 30, 20000), (2, 31, 5000), (3, 32, 750));
            Assert.True(provider.SaveNeighbourhood(false));

            //the live file is the newest save...
            Assert.Equal(new[] { 30, 31, 32 }, HouseNumbers(NeighborhoodPath));
            //...and the previous one is intact and readable beside it, which is the entire
            //point of the backup: it is what a player recovers by hand.
            Assert.True(File.Exists(NeighborhoodPath + ".bak"), "a second save must leave a backup");
            Assert.Equal(new[] { 30, 31 }, HouseNumbers(NeighborhoodPath + ".bak"));
        }

        /// <summary>
        /// The staging file is gone once the commit succeeds, and the two files that remain are
        /// the two a player is meant to find: the save and its previous self.
        /// </summary>
        [Fact]
        public void ACommittedNeighbourhoodLeavesNoStagedFileBehind()
        {
            var provider = NewProvider(_userPath, MakeNeighborhood((1, 30, 20000)));

            provider.SaveNeighbourhood(false);
            provider.SaveNeighbourhood(false);

            Assert.False(File.Exists(NeighborhoodPath + ".tmp"));
            Assert.Equal(new[] { "Neighborhood.iff", "Neighborhood.iff.bak" },
                Directory.GetFiles(_userPath).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        }

        /// <summary>
        /// A first save has nothing to back up, and says so rather than inventing an empty one.
        /// </summary>
        [Fact]
        public void TheFirstSaveOfANeighbourhoodHasNoBackup()
        {
            var provider = NewProvider(_userPath, MakeNeighborhood((1, 30, 20000)));

            provider.SaveNeighbourhood(false);

            Assert.False(File.Exists(NeighborhoodPath + ".bak"));
            Assert.False(File.Exists(NeighborhoodPath + ".tmp"));
            Assert.Equal(new[] { "Neighborhood.iff" },
                Directory.GetFiles(_userPath).Select(Path.GetFileName).ToArray());
        }

        /// <summary>
        /// The backup is the whole of the durability story, so it has to be a real neighbourhood
        /// file and not merely the same length: a truncated or mis-serialised backup is no
        /// backup at all.
        /// </summary>
        [Fact]
        public void TheBackupIsACompleteReadableNeighbourhoodResource()
        {
            var provider = NewProvider(_userPath, MakeNeighborhood((1, 30, 20000), (2, 44, 111)));
            provider.SaveNeighbourhood(false);
            provider.SaveNeighbourhood(false);

            var backup = Open(NeighborhoodPath + ".bak");

            Assert.Equal(2, backup.ListAll().Count);
            Assert.Equal(2, backup.List<FAMI>().Count);
            Assert.Equal(20000, backup.Get<FAMI>(1).Budget);
            Assert.Equal(111, backup.Get<FAMI>(2).Budget);
        }

        /// <summary>
        /// A failure while serialising must leave the previous save exactly as it was. The
        /// serialisation is the part that can fail - any chunk's <c>Write</c> can throw - and
        /// doing it before the destination is opened is what makes that safe.
        /// </summary>
        [Fact]
        public void AFailedSerialisationLeavesThePreviousSaveUntouched()
        {
            var provider = NewProvider(_userPath, MakeNeighborhood((1, 30, 20000)));
            provider.SaveNeighbourhood(false);
            var good = File.ReadAllBytes(NeighborhoodPath);

            //a chunk whose Write throws, standing in for a corrupt chunk or a full disk
            provider.MainResource = MakeNeighborhood((1, 30, 20000));
            provider.MainResource.AddChunk(new ExplodingChunk
            {
                ChunkID = 99,
                ChunkType = "FAMI",
                ChunkLabel = "Bad",
                ChunkProcessed = true,
            });

            Assert.ThrowsAny<Exception>(() => provider.SaveNeighbourhood(false));

            //the live file is still the last good save, byte for byte
            Assert.Equal(good, File.ReadAllBytes(NeighborhoodPath));
            Assert.Equal(new[] { 30 }, HouseNumbers(NeighborhoodPath));
        }

        /// <summary>
        /// The same, one level down: the commit helper itself is all-or-nothing, and a
        /// neighbourhood save goes through it.
        /// </summary>
        [Fact]
        public void TheCommitHelperRejectsANullResourceAndANullPath()
        {
            Assert.Throws<ArgumentNullException>(() => AtomicFileWrite.WriteIff(NeighborhoodPath, null));
            Assert.Throws<ArgumentNullException>(() => AtomicFileWrite.Write(NeighborhoodPath, null));
            Assert.Throws<ArgumentNullException>(() => AtomicFileWrite.Write(null, new byte[1]));
        }

        private sealed class ExplodingChunk : IffChunk
        {
            public override bool Write(IffFile iff, System.IO.Stream stream)
            {
                throw new InvalidOperationException("this chunk cannot be written");
            }

            public override void Read(IffFile iff, System.IO.Stream stream)
            {
            }

            public override void Dispose()
            {
            }
        }

        #endregion

        #region F-05 - one foreign file must not delete every character after it

        /// <summary>
        /// The regression. The loop over the <c>Characters</c> folder used to <c>return</c> on
        /// the first file whose extension was not exactly <c>.iff</c>, which leaves the method
        /// rather than the iteration. The folder is not under the game's control: the side files
        /// a save leaves beside an avatar, a <c>.tmp</c> from an interrupted commit,
        /// <c>Thumbs.db</c>, or a legitimately upper-cased <c>.IFF</c> on a case-preserving
        /// filesystem. Any of them made every character after it in enumeration order vanish
        /// silently, for the rest of the session.
        /// </summary>
        [Fact]
        public void CharactersOnBothSidesOfAForeignFileAreStillLoaded()
        {
            //"AAA.txt" sorts before "User...", and "zzz-notes.txt" after, so the answer does not
            //depend on which way the filesystem enumerates: the correct answer is "all three
            //characters" whichever order they come back in.
            WriteCharacterIff("AAA.txt", "not a character at all - not even an IFF");
            WriteCharacterIff("User00001.iff", "first");
            WriteCharacterIff("User00002.iff", "second");
            WriteCharacterIff("zzz-notes.txt", "also not a character");
            //and an upper-cased extension, which is a character, not a foreign file
            WriteCharacterIff("User00005.IFF", "shouted but real");

            var objects = NewObjectProvider();
            var provider = NewProvider(_userPath, null, objects);

            provider.LoadCharacters(false);

            //NextSim is one past the highest character number seen, so it is the observable
            //that says "the loop got all the way to the end".
            Assert.Equal(6, provider.NextSim);
        }

        /// <summary>
        /// The same folder with nothing foreign in it must give the same answer - so the test
        /// above is measuring the fix and not the enumeration.
        /// </summary>
        [Fact]
        public void CharactersAreAllLoadedWhenTheFolderIsClean()
        {
            WriteCharacterIff("User00001.iff", "first");
            WriteCharacterIff("User00002.iff", "second");

            var objects = NewObjectProvider();
            var provider = NewProvider(_userPath, null, objects);

            provider.LoadCharacters(false);

            Assert.Equal(3, provider.NextSim);
        }

        /// <summary>
        /// The other half: a missing <c>Characters</c> folder used to throw
        /// <c>DirectoryNotFoundException</c> out of <c>InitSpecific</c>, i.e. out of content
        /// init with the window already up. A neighbourhood with no playable characters is a
        /// legitimate state, not an error.
        /// </summary>
        [Fact]
        public void AMissingCharactersFolderIsNotAnError()
        {
            Assert.False(Directory.Exists(CharactersPathProbe()));

            var objects = NewObjectProvider();
            var provider = NewProvider(_userPath, null, objects);

            provider.LoadCharacters(false);

            Assert.Equal(0, provider.NextSim);
        }

        /// <summary>
        /// One unreadable character file must cost the player that avatar and not the rest of
        /// the neighbourhood: the load used to propagate out of the loop and take the whole
        /// thing with it.
        /// </summary>
        [Fact]
        public void OneUnreadableCharacterFileDoesNotCostTheOthers()
        {
            WriteCharacterIff("User00001.iff", "first");
            File.WriteAllBytes(Path.Combine(CharactersPath, "User00002.iff"),
                new byte[] { 0x00, 0x01, 0x02, 0x03 });      // not an IFF
            WriteCharacterIff("User00007.iff", "third");

            var objects = NewObjectProvider();
            var provider = NewProvider(_userPath, null, objects);

            provider.LoadCharacters(false);

            Assert.Equal(8, provider.NextSim);
        }

        private string CharactersPathProbe()
        {
            return Path.Combine(_userPath, TS1AvatarSavePath.CharactersFolderName);
        }

        private void WriteCharacterIff(string name, string line)
        {
            var str = new STR();
            str.LanguageSets[0].Strings = new[]
            {
                new STRItem(line) { LanguageCode = (byte)STRLangCode.EnglishUS, Comment = "Sim Lane" },
            };
            var iff = new IffFile();
            iff.AddChunk(new STR
            {
                ChunkID = 1,
                ChunkLabel = "Character",
                ChunkProcessed = true,
                LanguageSets = str.LanguageSets,
            });
            WriteIff(iff, Path.Combine(CharactersPath, name));
        }

        #endregion

        #region F-06 - a folder that exists is not a folder that is a neighbourhood

        /// <summary>
        /// The regression. The first-run bootstrap skipped its copy when the destination folder
        /// merely existed, so a copy that had failed part way through - one unreadable
        /// subfolder, a full disk, a power cut - was accepted on the next launch as a seeded
        /// neighbourhood, and the very next line then built an IFF over a
        /// <c>Neighborhood.iff</c> that was never there. The player saw a crash naming a file
        /// they had never heard of.
        /// </summary>
        [Fact]
        public void AFolderThatExistsButHasNoNeighbourhoodIsNotOne()
        {
            var empty = Path.Combine(_root, "empty-userdata");
            Directory.CreateDirectory(empty);
            //The Sims ships a UserData folder that is present and empty, and expansion
            //templates hold lots with no neighbourhood resource at all.
            Assert.False(TS1NeighborhoodProvider.IsOpenableNeighborhood(empty));
            //and the first-run guard, which is the line that was wrong: it has to ask again
            Assert.True(TS1NeighborhoodProvider.NeedsBootstrap(empty),
                "an existing but incomplete folder still needs seeding, not skipping");

            File.WriteAllText(Path.Combine(empty, "LotLocations.iff"), "lots but no neighbourhood");
            Assert.False(TS1NeighborhoodProvider.IsOpenableNeighborhood(empty));
            Assert.True(TS1NeighborhoodProvider.NeedsBootstrap(empty));

            //only the resource makes it openable
            File.WriteAllText(Path.Combine(empty, "Neighborhood.iff"), "now it is one");
            Assert.True(TS1NeighborhoodProvider.IsOpenableNeighborhood(empty));
            Assert.False(TS1NeighborhoodProvider.NeedsBootstrap(empty));
        }

        [Fact]
        public void AFolderThatDoesNotExistIsNotANeighbourhood()
        {
            Assert.False(TS1NeighborhoodProvider.IsOpenableNeighborhood(
                Path.Combine(_root, "never-created")));
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        public void NothingAtAllIsNotANeighbourhood(string path)
        {
            Assert.False(TS1NeighborhoodProvider.IsOpenableNeighborhood(path));
        }

        /// <summary>
        /// The copy itself: all of it, or nothing. The enumerations used to run unguarded, so a
        /// single unreadable subfolder threw part way through and left a half-populated
        /// <c>UserData</c> in the player's Documents folder - which the next launch then
        /// accepted, per the finding above. Here the destination's own folder name is occupied
        /// by a file, so the directory creation fails the same way an ACL or a placeholder
        /// would.
        /// </summary>
        [Fact]
        public void AFailedCopyLeavesNoPartialNeighbourhoodBehind()
        {
            var source = Path.Combine(_root, "template");
            Directory.CreateDirectory(Path.Combine(source, "Houses"));
            File.WriteAllText(Path.Combine(source, "Neighborhood.iff"), "neighbourhood");
            File.WriteAllText(Path.Combine(source, "Houses", "House01.iff"), "house");

            //"Houses" is already a file where the copy needs a directory
            var destination = Path.Combine(_root, "seeded");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "Houses"), "in the way");

            Assert.ThrowsAny<Exception>(() =>
                TS1NeighborhoodProvider.CopyFolder(source, destination));

            //the destination held something before the copy: it must never be deleted
            Assert.True(Directory.Exists(destination));
            Assert.Equal("in the way", File.ReadAllText(Path.Combine(destination, "Houses")));
            Assert.False(File.Exists(Path.Combine(destination, "Neighborhood.iff")));
        }

        /// <summary>
        /// A failure part way through removes only what this call created: the player's existing
        /// file survives, the file the copy had already written is gone, and a destination that
        /// did not exist before the call is removed again.
        /// </summary>
        [Fact]
        public void AFailedCopyRemovesOnlyWhatItCreated()
        {
            var source = Path.Combine(_root, "template-lock");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "a.iff"), "a");
            var locked = Path.Combine(source, "z.iff");
            File.WriteAllText(locked, "z");

            var destination = Path.Combine(_root, "seeded-lock");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "keep.iff"), "mine");

            var fresh = Path.Combine(_root, "seeded-fresh");

            using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsAny<Exception>(() => TS1NeighborhoodProvider.CopyFolder(source, destination));
                Assert.ThrowsAny<Exception>(() => TS1NeighborhoodProvider.CopyFolder(source, fresh));
            }

            Assert.Equal("mine", File.ReadAllText(Path.Combine(destination, "keep.iff")));
            Assert.False(File.Exists(Path.Combine(destination, "a.iff")),
                "a file this copy created must be rolled back");
            Assert.False(Directory.Exists(fresh), "a destination this copy created must be removed again");
        }

        /// <summary>
        /// The data-loss case: a player's folder with saved houses but no Neighborhood.iff. Seeding
        /// adds what is missing and leaves every existing file byte-for-byte as it was.
        /// </summary>
        [Fact]
        public void SeedingNeverOverwritesAnExistingHouse()
        {
            var source = Path.Combine(_root, "template-keep");
            Directory.CreateDirectory(Path.Combine(source, "Houses"));
            File.WriteAllText(Path.Combine(source, "Neighborhood.iff"), "template neighbourhood");
            File.WriteAllText(Path.Combine(source, "Houses", "House01.iff"), "template house");
            File.WriteAllText(Path.Combine(source, "Houses", "House02.iff"), "template house 2");

            var destination = Path.Combine(_root, "seeded-keep");
            Directory.CreateDirectory(Path.Combine(destination, "Houses"));
            var mine = new byte[] { 1, 2, 3, 0, 255, 42 };
            var housePath = Path.Combine(destination, "Houses", "House01.iff");
            File.WriteAllBytes(housePath, mine);

            Assert.True(TS1NeighborhoodProvider.NeedsBootstrap(destination));
            TS1NeighborhoodProvider.CopyFolder(source, destination);

            Assert.Equal(mine, File.ReadAllBytes(housePath));
            Assert.Equal("template neighbourhood", File.ReadAllText(Path.Combine(destination, "Neighborhood.iff")));
            Assert.Equal("template house 2", File.ReadAllText(Path.Combine(destination, "Houses", "House02.iff")));
        }

        /// <summary>
        /// A source that is not there is reported as such, and nothing is created - the old
        /// code let <c>Directory.GetFiles</c> raise a bare <c>DirectoryNotFoundException</c>
        /// naming only a path the player has never seen.
        /// </summary>
        [Fact]
        public void CopyingFromAMissingSourceCreatesNothing()
        {
            var destination = Path.Combine(_root, "seeded-missing");

            var thrown = Assert.Throws<DirectoryNotFoundException>(() =>
                TS1NeighborhoodProvider.CopyFolder(Path.Combine(_root, "no-such-template"), destination));

            Assert.Contains("no-such-template", thrown.Message);
            Assert.False(Directory.Exists(destination));
        }

        /// <summary>
        /// The happy path, including the shape of the tree - a neighbourhood is nested, and a
        /// copy that flattened or dropped a level would be indistinguishable from a broken one
        /// until much later.
        /// </summary>
        [Fact]
        public void ACompleteCopyReproducesTheWholeTree()
        {
            var source = Path.Combine(_root, "template2");
            Directory.CreateDirectory(Path.Combine(source, "Houses"));
            Directory.CreateDirectory(Path.Combine(source, "Characters"));
            File.WriteAllText(Path.Combine(source, "Neighborhood.iff"), "neighbourhood");
            File.WriteAllText(Path.Combine(source, "LotLocations.iff"), "lots");
            //a file with no extension: the old pattern was "*.*", which silently dropped these
            File.WriteAllText(Path.Combine(source, "no-extension"), "still a file");
            File.WriteAllText(Path.Combine(source, "Houses", "House01.iff"), "house");
            File.WriteAllText(Path.Combine(source, "Characters", "User00001.iff"), "character");

            var destination = Path.Combine(_root, "seeded2");
            TS1NeighborhoodProvider.CopyFolder(source + Path.DirectorySeparatorChar, destination + "/");

            Assert.True(TS1NeighborhoodProvider.IsOpenableNeighborhood(destination));
            foreach (var relative in new[]
            {
                "Neighborhood.iff", "LotLocations.iff", "no-extension",
                "Houses/House01.iff", "Characters/User00001.iff",
            })
            {
                Assert.True(File.Exists(Path.Combine(destination, relative)),
                    "'" + relative + "' must survive the copy");
            }
        }

        /// <summary>
        /// A destination that already holds a previous attempt is completed rather than skipped
        /// (missing files are added, existing ones kept): the guard is "is this openable", not
        /// "is this empty".
        /// </summary>
        [Fact]
        public void SeedingOverAnIncompleteDestinationCompletesIt()
        {
            var source = Path.Combine(_root, "template3");
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "Neighborhood.iff"), "neighbourhood");
            File.WriteAllText(Path.Combine(source, "LotLocations.iff"), "lots");

            var destination = Path.Combine(_root, "seeded3");
            Directory.CreateDirectory(destination);
            File.WriteAllText(Path.Combine(destination, "junk.txt"), "left by a failed attempt");

            TS1NeighborhoodProvider.CopyFolder(source, destination);

            Assert.True(TS1NeighborhoodProvider.IsOpenableNeighborhood(destination));
            Assert.True(File.Exists(Path.Combine(destination, "LotLocations.iff")));
        }

        #endregion
    }
}
