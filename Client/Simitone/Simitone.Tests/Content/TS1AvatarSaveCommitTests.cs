using FSO.Content.TS1;
using FSO.Content.Utils;
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
using GameObjectResource = FSO.Content.GameObjectResource;
using GameObjectReference = FSO.Content.GameObjectReference;
using GameObjectSource = FSO.Content.GameObjectSource;

namespace Simitone.Tests.Content
{
    /// <summary>
    /// The per-character writes, against a synthetic <c>Characters</c> folder: a dirty avatar's
    /// IFF has to be committed rather than truncated, a failure on one of them has to be
    /// survivable, and it has to be reported somewhere a windowed game can be asked to look
    /// afterwards.
    ///
    /// <para>
    /// The regression, in one line: <c>SaveAvatarIff</c> opened
    /// <c>Characters/User000NN.iff</c> with <c>FileMode.Create</c> - which empties the file at
    /// the moment of the open - and then asked every chunk in it for its bytes in turn, and
    /// <c>SaveNewNeighbour</c> did the same to a name the folder might already be using. A
    /// failure part way through left a truncated character with no recovery copy, and a
    /// character is a person.
    /// </para>
    ///
    /// <para>
    /// The second half of the finding is quieter and worse. The two <c>catch</c> clauses were
    /// <see cref="IOException"/> and <see cref="UnauthorizedAccessException"/>, and a chunk
    /// whose own <c>Write</c> throws is neither - that is the ordinary case, it is what a
    /// half-broken object does, and an unregistered chunk type has long been a
    /// <c>NullReferenceException</c> out of <c>IffFile.WriteChunk</c>. So the exception left
    /// <c>SaveAvatarIff</c>, left <c>SaveNeighbourhood</c>, and the neighbourhood file - every
    /// family, every budget, the magicoins inventory - was never written, in exchange for one
    /// character. And every one of those branches reported to <c>Console.WriteLine</c>, which in
    /// a shipped windowed executable is not a hidden log but no log at all.
    /// </para>
    ///
    /// <para>
    /// The provider is reached without its constructor, as in
    /// <see cref="TS1NeighbourhoodPersistenceTests"/>: <c>TS1NeighborhoodProvider(Content)</c>
    /// calls <c>InitSpecific(0)</c>, which needs a whole TS1 install. What is <b>not</b> faked is
    /// the object provider - the avatar is a real character IFF written to the folder, read back
    /// by the real <c>TS1ObjectProvider</c> through the real
    /// <c>AbstractObjectProvider.Get</c>, so the file the save path resolves and commits is
    /// the file a player's would be.
    /// </para>
    /// </summary>
    [Collection(NonParallelCollection.Name)]
    public sealed class TS1AvatarSaveCommitTests : IDisposable
    {
        /// <summary>The GUID the synthetic character's OBJD carries.</summary>
        private const uint AvatarGuid = 0x1234;

        private readonly string _root;
        private readonly string _userPath;
        private readonly Action<ContentLogLevel, string, Exception> _previousSink;
        private readonly List<(ContentLogLevel level, string message, Exception error)> _logged = new();

        /// <summary>Re-installs the capturing sink, for a test that had to take it away.</summary>
        private Action<ContentLogLevel, string, Exception> Capture =>
            (level, message, error) => { lock (_logged) _logged.Add((level, message, error)); };

        public TS1AvatarSaveCommitTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-avatar-commit-" + Guid.NewGuid().ToString("N"));
            _userPath = Path.Combine(_root, "UserData");
            CharactersDirectory = Path.Combine(_userPath, TS1AvatarSavePath.CharactersFolderName);
            Directory.CreateDirectory(CharactersDirectory);

            //the logger's sink is process-global, so the whole class is serialised
            _previousSink = ContentLog.Sink;
            ContentLog.Sink = Capture;
        }

        public void Dispose()
        {
            ContentLog.Sink = _previousSink;
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

        #region the fixture

        private string CharactersDirectory { get; }

        private string AvatarPath => Path.Combine(CharactersDirectory, "User00001.iff");

        private string NeighborhoodPath => Path.Combine(_userPath, "Neighborhood.iff");

        private static TS1NeighborhoodProvider NewProvider(string userPath, IffFile mainResource, TS1ObjectProvider objects)
        {
            var provider = (TS1NeighborhoodProvider)RuntimeHelpers.GetUninitializedObject(typeof(TS1NeighborhoodProvider));
            provider.UserPath = userPath;
            provider.MainResource = mainResource;
            provider.ContentManager = NewContent(objects);
            //InitSpecific is what normally clears and refills this; the save path only ever
            //adds to it and reads it back
            provider.DirtyAvatars = new HashSet<uint>();
            return provider;
        }

        private static FSOContent NewContent(TS1ObjectProvider objects)
        {
            var content = (FSOContent)RuntimeHelpers.GetUninitializedObject(typeof(FSOContent));
            content.WorldObjects = objects;
            return content;
        }

        private static TS1ObjectProvider NewObjectProvider()
        {
            var objects = new TS1ObjectProvider(null, null);
            //Init() is what normally fills this, and it needs a whole TS1 install.
            objects.Entries = new Dictionary<ulong, GameObjectReference>();
            return objects;
        }

        private static IffFile MakeNeighborhood(params (int chunkId, int houseNumber)[] families)
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

        /// <summary>
        /// A character IFF as <c>LoadCharacters</c> would find it: one OBJD, so the object
        /// provider can find the object inside it, plus a name-bearing STR so the file is
        /// recognisably a character and not merely a container.
        /// </summary>
        private static IffFile MakeCharacter(uint guid, string name)
        {
            var iff = new IffFile();
            iff.AddChunk(new OBJD
            {
                ChunkID = 1,
                ChunkType = "OBJD",
                ChunkLabel = "Sim Lane",
                ChunkProcessed = true,
                GUID = guid,
                ObjectType = OBJDType.Person,
                MasterID = 7,
                SubIndex = 0,
            });
            var str = new STR();
            str.LanguageSets[0].Strings = new[]
            {
                new STRItem(name) { LanguageCode = (byte)STRLangCode.EnglishUS, Comment = "Sim Lane" },
            };
            iff.AddChunk(new STR
            {
                ChunkID = 1,
                ChunkType = "STR ",
                ChunkLabel = "Sim Lane",
                ChunkProcessed = true,
                LanguageSets = str.LanguageSets,
            });
            return iff;
        }

        /// <summary>
        /// The provider, and the object a dirty-avatar save looks up. The reference is
        /// registered by hand exactly as <c>LoadCharacters</c> registers one, with the absolute
        /// path the reader found - the only shape
        /// <see cref="TS1AvatarSavePath.Resolve"/> will agree to write back.
        /// </summary>
        private (TS1NeighborhoodProvider provider, FSO.Content.GameObject avatar) NewSave(string name = "before")
        {
            WriteIff(MakeCharacter(AvatarGuid, name), AvatarPath);

            var objects = NewObjectProvider();
            objects.Entries[AvatarGuid] = new GameObjectReference(objects)
            {
                ID = AvatarGuid,
                FileName = AvatarPath,
                Name = "Sim Lane",
                Source = GameObjectSource.User,
            };

            var provider = NewProvider(_userPath, MakeNeighborhood((1, 30)), objects);
            var avatar = objects.Get(AvatarGuid);
            Assert.NotNull(avatar);
            return (provider, avatar);
        }

        /// <summary>
        /// Makes this avatar impossible to serialise. A chunk's own <c>Write</c> throwing is
        /// the ordinary case the old <c>catch</c> clauses did not cover - it is the
        /// <c>Test_Fail</c> primitive of a half-broken object, and an unregistered chunk type
        /// is a <c>NullReferenceException</c> out of <c>IffFile.WriteChunk</c>.
        /// </summary>
        private static void BreakSerialisation(FSO.Content.GameObject avatar)
        {
            avatar.Resource.Iff.AddChunk(new ExplodingChunk
            {
                ChunkID = 99,
                ChunkType = "FAMI",
                ChunkLabel = "Bad",
                ChunkProcessed = true,
            });
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

        private static string[] NameOf(string path)
        {
            var str = Open(path).Get<STR>(1);
            return str?.LanguageSets?[0]?.Strings?.Select(s => s.Value).ToArray() ?? new string[0];
        }

        private (ContentLogLevel level, string message, Exception error)[] Logged()
        {
            lock (_logged) return _logged.ToArray();
        }

        #endregion

        #region F-07 - the character IFF is committed, not truncated

        /// <summary>
        /// The regression. The write was <c>new FileStream(target.Path, FileMode.Create)</c>
        /// followed by a serialise straight into it, so the player's character was emptied at
        /// the moment of the open and refilled chunk by chunk. It is committed now, and the
        /// previous character is left beside it - which is the only recovery route a
        /// <c>Characters</c> folder has, because nothing else in the save holds that Sim.
        /// </summary>
        [Fact]
        public void SavingADirtyAvatarLeavesAReadableBackupOfThePreviousCharacter()
        {
            var (provider, avatar) = NewSave();
            //the Sim was renamed in game, so the save has something new to write
            avatar.Resource.Iff.Get<STR>(1).LanguageSets[0].Strings[0].Value = "after";

            provider.AvatarChanged(AvatarGuid);
            Assert.True(provider.SaveNeighbourhood(true));

            Assert.Equal(new[] { "after" }, NameOf(AvatarPath));
            Assert.True(File.Exists(AvatarPath + ".bak"), "a second write must leave a backup");
            Assert.Equal(new[] { "before" }, NameOf(AvatarPath + ".bak"));
            Assert.False(File.Exists(AvatarPath + ".tmp"), "the staged copy must be committed away");
        }

        /// <summary>
        /// A first write over a name nothing is using is a plain creation, and must not invent
        /// a backup out of nothing.
        /// </summary>
        [Fact]
        public void ACharactersFirstWriteLeavesNoBackupAndNoStagedFile()
        {
            var (provider, _) = NewSave();
            File.Delete(AvatarPath);

            provider.AvatarChanged(AvatarGuid);
            Assert.True(provider.SaveNeighbourhood(true));

            Assert.True(File.Exists(AvatarPath));
            Assert.False(File.Exists(AvatarPath + ".bak"));
            Assert.False(File.Exists(AvatarPath + ".tmp"));
        }

        /// <summary>
        /// The hard case. The serialiser throws part way through, and the character must come
        /// out the other side exactly as it went in - not shortened, not zeroed, not gone. The
        /// old code had already truncated the file before it asked the first chunk for its
        /// bytes, so there was nothing to compare against.
        /// </summary>
        [Fact]
        public void AFailedAvatarSerialisationLeavesThePreviousCharacterByteForByte()
        {
            var (provider, avatar) = NewSave();
            var before = File.ReadAllBytes(AvatarPath);

            BreakSerialisation(avatar);
            provider.AvatarChanged(AvatarGuid);
            Assert.True(provider.SaveNeighbourhood(true));

            Assert.Equal(before, File.ReadAllBytes(AvatarPath));
            Assert.Equal(new[] { "before" }, NameOf(AvatarPath));
            Assert.False(File.Exists(AvatarPath + ".tmp"), "the staged copy must be cleaned up");
        }

        #endregion

        #region F-07 - one broken avatar must not cost the neighbourhood

        /// <summary>
        /// The worse half of the finding, and the one that loses a save. The two
        /// <c>catch</c> clauses were <see cref="IOException"/> and
        /// <see cref="UnauthorizedAccessException"/>; a chunk whose <c>Write</c> throws is
        /// neither, so the exception left <c>SaveAvatarIff</c>, left
        /// <c>SaveNeighbourhood</c>, and the <c>Neighborhood.iff</c> write immediately after
        /// never ran. Every family, every budget and the magicoins inventory, gone in exchange
        /// for one character.
        /// </summary>
        [Fact]
        public void AnAvatarThatCannotBeWrittenDoesNotCostTheNeighbourhoodSave()
        {
            var (provider, avatar) = NewSave();
            BreakSerialisation(avatar);

            provider.AvatarChanged(AvatarGuid);
            Assert.True(provider.SaveNeighbourhood(true));

            //the neighbourhood file is the one that matters, and it must be there
            Assert.True(File.Exists(NeighborhoodPath), "one broken avatar must not skip the neighbourhood save");
            Assert.Single(Open(NeighborhoodPath).List<FAMI>());

            //and the dirty set is still cleared, because the save did complete
            Assert.Empty(provider.DirtyAvatars);
        }

        /// <summary>
        /// A neighbourhood save with a broken avatar in it must still be a good neighbourhood
        /// save, and must still leave the previous neighbourhood as a backup - the same
        /// contract an avatar-free save has.
        /// </summary>
        [Fact]
        public void TheNeighbourhoodSavedAlongsideABrokenAvatarIsComplete()
        {
            var (provider, avatar) = NewSave();
            provider.SaveNeighbourhood(false);
            var good = File.ReadAllBytes(NeighborhoodPath);

            //the family buys a lot, and one of the Sims is half-broken
            provider.MainResource = MakeNeighborhood((1, 30), (2, 31));
            BreakSerialisation(avatar);
            provider.AvatarChanged(AvatarGuid);

            Assert.True(provider.SaveNeighbourhood(true));

            Assert.Equal(2, Open(NeighborhoodPath).List<FAMI>().Count);
            Assert.Equal(good, File.ReadAllBytes(NeighborhoodPath + ".bak"));
        }

        /// <summary>
        /// A destination the commit cannot replace at all - here, because the avatar's own name
        /// is occupied by a directory, so even the final rename fails. That is the branch the
        /// old code had, and it is reported rather than thrown, and the neighbourhood save
        /// still happens.
        /// </summary>
        [Fact]
        public void AnAvatarDestinationThatCannotBeReplacedIsReportedAndDoesNotStopTheSave()
        {
            var (provider, _) = NewSave();
            File.Delete(AvatarPath);
            Directory.CreateDirectory(AvatarPath);

            provider.AvatarChanged(AvatarGuid);
            Assert.True(provider.SaveNeighbourhood(true));

            var entry = Assert.Single(Logged().Where(l => l.message.Contains("could not write")));
            Assert.Equal(ContentLogLevel.Warn, entry.level);
            Assert.Contains(AvatarPath, entry.message);
            Assert.True(File.Exists(NeighborhoodPath),
                "an unwritable character must not skip the neighbourhood save");
        }

        /// <summary>
        /// An avatar whose destination is not ours to write is skipped with a reason, and the
        /// skip is reported rather than printed to a console the game does not have. This is
        /// the decision <see cref="TS1AvatarSavePath"/> makes, and the reason it was extracted:
        /// the "silence" case is a log line, not a gap.
        /// </summary>
        [Fact]
        public void AnAvatarThatIsNotOursToWriteIsSkippedWithAReason()
        {
            var (provider, avatar) = NewSave();
            //a bare archive entry name, which is how a FAR-resident NPC arrives
            avatar.Resource.Name = "maid.iff";

            provider.AvatarChanged(AvatarGuid);
            Assert.True(provider.SaveNeighbourhood(true));

            //nothing loose in the working directory, and the reason is on the record
            Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "maid.iff")));
            var entry = Assert.Single(Logged().Where(l => l.message.Contains("maid.iff")));
            Assert.Equal(ContentLogLevel.Info, entry.level);
            Assert.Contains("SkipFarResident", entry.message);
        }

        #endregion

        #region the failure is reported somewhere

        /// <summary>
        /// A save failure reported to <c>Console.WriteLine</c> is not a save failure report in
        /// a windowed Windows executable - there is no console, so the line is never written
        /// anywhere, and the player who reports "my Sim disappeared" has nothing to send. The
        /// message has to reach the log the game writes, with the file named so it can be found.
        /// </summary>
        [Fact]
        public void AFailedAvatarWriteIsReportedToTheContentLog()
        {
            var (provider, avatar) = NewSave();
            BreakSerialisation(avatar);

            provider.AvatarChanged(AvatarGuid);
            provider.SaveNeighbourhood(true);

            var entry = Assert.Single(Logged().Where(l => l.message.Contains("could not serialise")));
            Assert.Equal(ContentLogLevel.Error, entry.level);
            Assert.Contains(AvatarPath, entry.message);
            Assert.IsType<InvalidOperationException>(entry.error);
        }

        /// <summary>
        /// With no sink installed the entry goes to the log file under the user directory, in
        /// the same place and the same line format the client's own logger uses - because that
        /// is what makes it reach a player's diagnostics without the client having to know that
        /// this class exists.
        /// </summary>
        [Fact]
        public void WithNoSinkInstalledAnEntryLandsInTheGamesOwnLogFile()
        {
            var logRoot = Path.Combine(_root, "userdir");
            Directory.CreateDirectory(logRoot);
            var previous = FSO.Common.FSOEnvironment.UserDir;
            FSO.Common.FSOEnvironment.UserDir = logRoot;
            ContentLog.Sink = null;
            try
            {
                ContentLog.Warn("TS1 save: a warning that has to be findable.", new InvalidOperationException("why"));
            }
            finally
            {
                FSO.Common.FSOEnvironment.UserDir = previous;
                ContentLog.Sink = Capture;
            }

            var log = Path.Combine(logRoot, "logs", "simitone.log");
            Assert.True(File.Exists(log), "the default sink is the game's log file, not nowhere");
            var line = Assert.Single(File.ReadAllLines(log));
            Assert.Contains("[WARN]", line);
            Assert.Contains("a warning that has to be findable", line);
            Assert.Contains("InvalidOperationException: why", line);
        }

        /// <summary>
        /// And a log that cannot be written is not a second failure: it is only ever reached
        /// from a handler that is already reporting a problem, and a logger that turns one lost
        /// message into a crash loses the neighbourhood save as well. The user directory here is
        /// a path whose "logs" parent is a file, so nothing can be created under it.
        /// </summary>
        [Fact]
        public void ALogThatCannotBeWrittenDoesNotBecomeASecondFailure()
        {
            var (provider, avatar) = NewSave();
            BreakSerialisation(avatar);

            //a file where the logs folder would have to be
            var blocked = Path.Combine(_root, "blocked");
            File.WriteAllText(blocked, "not a directory");
            var previous = FSO.Common.FSOEnvironment.UserDir;
            FSO.Common.FSOEnvironment.UserDir = blocked;
            ContentLog.Sink = null;
            try
            {
                provider.AvatarChanged(AvatarGuid);
                Assert.True(provider.SaveNeighbourhood(true));
            }
            finally
            {
                FSO.Common.FSOEnvironment.UserDir = previous;
                ContentLog.Sink = Capture;
            }

            //the save still happened, which is the whole point
            Assert.True(File.Exists(NeighborhoodPath));
        }

        #endregion

        #region SaveNewNeighbour

        /// <summary>
        /// A new character is written to a name the folder might already be using -
        /// <c>NextSim</c> is a number, not a fact about the disk - and the write used to
        /// <c>FileMode.Create</c> straight into it, so a collision destroyed whatever was
        /// there. It is committed now, and the number is only consumed once the bytes are on
        /// disk.
        /// </summary>
        [Fact]
        public void WritingANewCharacterCommitsItAndConsumesTheNumberOnlyOnSuccess()
        {
            var (provider, avatar) = NewSave();
            var existing = File.ReadAllBytes(AvatarPath);
            provider.NextSim = 1;   //the name this resolves to is already taken

            provider.SaveNewNeighbour(avatar);

            Assert.Equal(2, provider.NextSim);
            Assert.Equal(new[] { "before" }, NameOf(AvatarPath));
            //the previous occupant of the name is recoverable
            Assert.True(File.Exists(AvatarPath + ".bak"));
            Assert.Equal(existing, File.ReadAllBytes(AvatarPath + ".bak"));
        }

        /// <summary>
        /// A new character that cannot be serialised leaves nothing behind. It used to leave a
        /// stub in a folder that <c>LoadCharacters</c> enumerates, so the next launch would
        /// register a character with no object data in it - and the slot number had already been
        /// spent, so the retry wrote a different name and left the stub behind for good.
        /// </summary>
        [Fact]
        public void ANewCharacterThatCannotBeWrittenLeavesNoStubAndKeepsItsNumber()
        {
            var (provider, avatar) = NewSave();
            BreakSerialisation(avatar);
            provider.NextSim = 7;
            var target = Path.Combine(CharactersDirectory, "User00007.iff");

            Assert.Throws<InvalidOperationException>(() => provider.SaveNewNeighbour(avatar));

            Assert.Equal(7, provider.NextSim);
            Assert.False(File.Exists(target));
            Assert.False(File.Exists(target + ".tmp"));
            //and it was reported, because the file is the only thing the caller cannot see
            Assert.Contains(Logged(), l => l.message.Contains("User00007.iff"));
        }

        [Fact]
        public void ANewCharacterWithNoResourceIsIgnoredRatherThanNullDereferenced()
        {
            var (provider, _) = NewSave();
            provider.SaveNewNeighbour(null);
            provider.SaveNewNeighbour(new FSO.Content.GameObject());
            Assert.Equal(0, provider.NextSim);
        }

        #endregion
    }
}
