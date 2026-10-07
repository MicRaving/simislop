using FSO.Files.Formats;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="PiffEncoder.MakeChunkDiff(IffChunk)"/> and the <see cref="PIFFEntry.Apply(byte[])"/>
    /// that undoes it - the diff/apply pair that is the format for every user content patch the
    /// game can write, and which had no test at all.
    ///
    /// <para>
    /// The gap mattered because <c>OriginalData</c> is only populated when
    /// <c>IffFile.RETAIN_CHUNK_DATA</c> is set, and that defaults to <c>false</c>. So for any
    /// normal user the diff walked straight into a <c>NullReferenceException</c> on the first
    /// chunk it was asked about, and <c>ChangeManager.SaveChange</c> - the one caller that
    /// matters - threw for every content edit. The only reason anything appeared to work was
    /// that <c>PIFFEntryType.Patch</c> is the enum's zero value, so an unset
    /// <c>EntryType</c> happened to be right; reordering the enum would have silently turned
    /// every patch into a delete.
    /// </para>
    ///
    /// <para>
    /// A <see cref="STR"/> is the vehicle because it round-trips through a real file, so a chunk
    /// read from disk has both the raw bytes to diff against and the decoded form to edit -
    /// which is exactly the state <c>SaveChange</c> operates on.
    /// </para>
    /// </summary>
    public sealed class PiffDiffTests : IDisposable
    {
        private readonly string _root;

        public PiffDiffTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-piff-" + Guid.NewGuid().ToString("N"));
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
                //best effort
            }
            catch (UnauthorizedAccessException)
            {
                //same
            }
        }

        #region helpers

        private static IffFile MakeStringIff(string value, string comment = "a comment")
        {
            var str = new STR();
            str.LanguageSets[0].Strings = new[] { new STRItem(value) { Comment = comment } };
            var iff = new IffFile();
            iff.AddChunk(new STR
            {
                ChunkID = 7,
                ChunkLabel = "Patched strings",
                ChunkProcessed = true,
                LanguageSets = str.LanguageSets,
            });
            return iff;
        }

        private static byte[] Serialize(IffFile iff)
        {
            using (var stream = new MemoryStream())
            {
                iff.Write(stream);
                return stream.ToArray();
            }
        }

        private static IffFile Deserialize(byte[] bytes)
        {
            var iff = new IffFile();
            using (var stream = new MemoryStream(bytes))
            {
                iff.Read(stream);
            }
            return iff;
        }

        /// <summary>A chunk's serialised bytes, which is what a diff is against.</summary>
        private static byte[] Written(STR chunk, IffFile parent)
        {
            using (var stream = new MemoryStream())
            {
                Assert.True(chunk.Write(parent, stream));
                return stream.ToArray();
            }
        }

        private static void AssertSameBytes(byte[] expected, byte[] actual, string because)
        {
            Assert.NotNull(actual);
            Assert.True(expected.Length == actual.Length,
                because + " (expected " + expected.Length + " bytes, got " + actual.Length + ")");
            Assert.True(expected.SequenceEqual(actual), because);
        }

        #endregion

        #region the regression: no original data

        /// <summary>
        /// The regression, at the level the game reaches it. <c>OriginalData</c> is null unless
        /// the game was built with chunk retention on, so this is the state every chunk a user
        /// has ever edited is in - and the diff used to throw before it emitted a single patch.
        /// </summary>
        [Fact]
        public void AChunkWithNoOriginalDataStillProducesAPatch()
        {
            var iff = MakeStringIff("the original text");
            var str = iff.List<STR>().Single();
            Assert.Null(str.OriginalData);

            str.LanguageSets[0].Strings[0].Value = "the edited text";
            str.RuntimeInfo = ChunkRuntimeState.Modified;

            var entry = PiffEncoder.MakeChunkDiff(str);

            Assert.NotNull(entry);
            Assert.Equal(PIFFEntryType.Patch, entry.EntryType);
            Assert.NotNull(entry.Patches);
            Assert.NotEmpty(entry.Patches);
            //a chunk built in memory has no OriginalID, and the entry is addressed by it
            Assert.Equal(str.OriginalID, entry.ChunkID);
            Assert.Equal(str.ChunkID, entry.NewChunkID);
            //the type label is what the patch is applied by, and an in-memory chunk does not
            //carry one of its own
            Assert.Equal("STR#", entry.Type);
            //with no original to diff against, the whole payload is one add
            Assert.Equal(Written(str, iff).Length, (int)entry.NewDataSize);
            AssertSameBytes(Written(str, iff), entry.Apply(new byte[0]),
                "a patch with no original must reconstruct the chunk from nothing");
        }

        /// <summary>
        /// The same, through the entry point <c>ChangeManager.SaveChange</c> uses. Before the
        /// fix this threw, which meant no user patch could be written at all.
        /// </summary>
        [Fact]
        public void AUserPatchCanBeGeneratedForAChunkThatWasNeverReadFromAFile()
        {
            var iff = MakeStringIff("before");
            var str = iff.List<STR>().Single();
            str.LanguageSets[0].Strings[0].Value = "after";
            str.RuntimeInfo = ChunkRuntimeState.Modified;

            var piff = PiffEncoder.GeneratePiff(iff, null, null, null);

            Assert.NotNull(piff);
            var entry = Assert.Single(piff.List<PIFF>().Single().Entries);
            Assert.Equal(PIFFEntryType.Patch, entry.EntryType);
            //and it is writable, which is the whole point of generating it
            Assert.NotNull(Serialize(piff));
        }

        /// <summary>
        /// The null check sat <i>after</i> the first dereference of its argument, so it could
        /// never fire. A null chunk is a "remove this" answer.
        /// </summary>
        [Fact]
        public void ANullChunkIsARemovalRatherThanANullReference()
        {
            var entry = PiffEncoder.MakeChunkDiff(null);

            Assert.NotNull(entry);
            Assert.Equal(PIFFEntryType.Remove, entry.EntryType);
        }

        #endregion

        #region diff and apply, both directions of change

        /// <summary>
        /// The contract the whole format rests on: applying a diff to the bytes the chunk
        /// originally had produces exactly the bytes the chunk now has. A mid-string edit, a
        /// truncation and a growth, because those are the three shapes of patch the diff
        /// encoding has to get right - a change inside a block, a shorter result, and a longer
        /// one.
        /// </summary>
        [Theory]
        [InlineData("the original text", "the EDITED text", "a mid-string edit")]
        [InlineData("the original text which is quite long indeed", "short", "a truncation")]
        [InlineData("short", "a considerably longer replacement that grows the payload well past the original", "a growth")]
        public void ApplyingADiffToTheOriginalProducesTheEditedBytes(string before, string after, string shape)
        {
            var path = Path.Combine(_root, "source.iff");
            File.WriteAllBytes(path, Serialize(MakeStringIff(before)));

            //retainData, so the chunk carries the bytes it was read from - the state the
            //game's own editing path works in
            var iff = new IffFile(path, true);
            var str = iff.Get<STR>(7);
            Assert.NotNull(str.OriginalData);
            Assert.False(before == after, "precondition: the test must actually change something (" + shape + ")");

            var original = str.OriginalData;
            str.LanguageSets[0].Strings[0].Value = after;
            str.RuntimeInfo = ChunkRuntimeState.Modified;

            var entry = PiffEncoder.MakeChunkDiff(str);
            var expected = Written(str, iff);

            Assert.NotEqual(original, expected);
            AssertSameBytes(expected, entry.Apply(original),
                "a diff applied to the original must reconstruct the edited chunk (" + shape + ")");
        }

        /// <summary>
        /// The same, through the file: a diff written into a PIFF, read back out of one, and
        /// applied. This is the path a third-party patch actually takes, and it is where a
        /// field that the in-memory diff sets but the serialiser drops would show up.
        /// </summary>
        [Fact]
        public void ADiffSurvivesAPiffWriteAndReadAndStillReconstructsTheChunk()
        {
            var path = Path.Combine(_root, "source2.iff");
            File.WriteAllBytes(path, Serialize(MakeStringIff("the original text, long enough to be worth diffing")));

            var iff = new IffFile(path, true);
            var str = iff.Get<STR>(7);
            var original = str.OriginalData;
            str.LanguageSets[0].Strings[0].Value = "the EDITED text, still long enough to be worth diffing";
            str.RuntimeInfo = ChunkRuntimeState.Modified;
            var expected = Written(str, iff);

            var diff = PiffEncoder.MakeChunkDiff(str);
            var piffFile = new IffFile();
            piffFile.AddChunk(new PIFF
            {
                ChunkID = 256,
                ChunkLabel = "source.iff patch",
                ChunkProcessed = true,
                SourceIff = "source.iff",
                Comment = "a test patch",
                Entries = new[] { diff },
            });

            var readBack = Deserialize(Serialize(piffFile));
            var entry = Assert.Single(readBack.Get<PIFF>(256).Entries);

            Assert.Equal(PIFFEntryType.Patch, entry.EntryType);
            Assert.Equal("STR#", entry.Type);
            Assert.Equal(7, entry.ChunkID);
            AssertSameBytes(expected, entry.Apply(original),
                "a diff that has been through a PIFF file must still reconstruct the chunk");
        }

        /// <summary>
        /// A patch is a patch, not a delete - spelled out rather than inherited from the enum's
        /// zero value, which is the only reason this ever worked.
        /// </summary>
        [Fact]
        public void TheEntryTypeIsPatchAndNotWhateverTheEnumHappensToDefaultTo()
        {
            Assert.Equal(0, (int)PIFFEntryType.Patch);

            var path = Path.Combine(_root, "source3.iff");
            File.WriteAllBytes(path, Serialize(MakeStringIff("before")));

            var iff = new IffFile(path, true);
            var str = iff.Get<STR>(7);
            str.LanguageSets[0].Strings[0].Value = "after";
            str.RuntimeInfo = ChunkRuntimeState.Modified;

            var entry = PiffEncoder.MakeChunkDiff(str);

            Assert.Equal(PIFFEntryType.Patch, entry.EntryType);
        }

        /// <summary>
        /// A chunk that is not modified produces no entry at all, and
        /// <c>GeneratePiff</c> reports "no patch data" rather than writing an empty patch file
        /// over a good one.
        /// </summary>
        [Fact]
        public void AnUnmodifiedFileProducesNoPatch()
        {
            var iff = MakeStringIff("unchanged");

            Assert.Null(PiffEncoder.GeneratePiff(iff, null, null, null));
        }

        /// <summary>
        /// A chunk whose writer declines (<c>Write</c> returning false) means "keep the
        /// original", so there is nothing to diff and the caller is told so.
        /// </summary>
        [Fact]
        public void AChunkThatDeclinesToWriteHasNoDiff()
        {
            var iff = new IffFile();
            iff.AddChunk(new UnwritableChunk
            {
                ChunkID = 3,
                ChunkType = "STR#",
                ChunkLabel = "nope",
                ChunkProcessed = true,
                OriginalData = new byte[] { 1, 2, 3 },
            });

            Assert.Null(PiffEncoder.MakeChunkDiff(iff.ListAll().Single()));
        }

        private sealed class UnwritableChunk : IffChunk
        {
            public override bool Write(IffFile iff, Stream stream) { return false; }
            public override void Read(IffFile iff, Stream stream) { }
            public override void Dispose() { }
        }

        #endregion
    }
}
