using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using FSO.Common.Content;
using FSO.Content.Framework;
using FSO.Content.TS1;
using FSO.Files.FAR3;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Content
{
    /// <summary>
    /// The three content providers, against the three things they disagreed about and the two
    /// they latched their initialisation flag on.
    ///
    /// <para>
    /// They are built against a <c>Content</c> allocated uninitialised with just a
    /// <c>BasePath</c>, because <c>GetPath</c> is a <c>Path.Combine</c> and the real constructor
    /// does a whole content scan. <c>Init</c> is the real one in every case, so the indexes, the
    /// caches and the archive handles below are all built by the production code.
    /// </para>
    ///
    /// <para>
    /// No install, no VM, no graphics device: the archives here are about forty bytes each and are
    /// built by hand, which is also what lets a test make one of them fail.
    /// </para>
    /// </summary>
    public sealed class ContentProviderLifetimeTests : IDisposable
    {
        private readonly string _root;

        public ContentProviderLifetimeTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-providers-" + Guid.NewGuid().ToString("N"));
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

        #region fixture

        private sealed class RawCodec : IContentCodec<byte[]>
        {
            public override object GenDecode(Stream stream)
            {
                using (var buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    return buffer.ToArray();
                }
            }
        }

        private static byte[] Blob(byte fill, int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = fill;
            return data;
        }

        /// <summary>
        /// A minimal FAR3 archive: header, version, manifest offset, payloads, manifest.
        /// </summary>
        private string WriteFar3(string name, uint typeId, params (string entryName, byte[] data)[] entries)
        {
            var path = Path.Combine(_root, name);
            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("FAR!byAZ"));
                writer.Write((uint)3);
                writer.Write((uint)0);                        // manifest offset, patched below

                var offsets = new uint[entries.Length];
                for (int i = 0; i < entries.Length; i++)
                {
                    offsets[i] = (uint)stream.Position;
                    writer.Write(entries[i].data);
                }

                var manifestOffset = (uint)stream.Position;
                stream.Position = 12;
                writer.Write(manifestOffset);
                stream.Position = manifestOffset;

                writer.Write((uint)entries.Length);
                for (int i = 0; i < entries.Length; i++)
                {
                    var fileName = Encoding.ASCII.GetBytes(entries[i].entryName);
                    writer.Write((uint)entries[i].data.Length);   // DecompressedFileSize
                    //and the three-byte compressed size, little-endian - three bytes, not the
                    //payload. Writing the payload here is how a hand-built FAR3 goes wrong.
                    var size = entries[i].data.Length;
                    writer.Write((byte)size);
                    writer.Write((byte)(size >> 8));
                    writer.Write((byte)(size >> 16));
                    writer.Write((byte)0);                        // DataType
                    writer.Write(offsets[i]);                     // DataOffset
                    writer.Write((byte)0);                        // IsCompressed
                    writer.Write((byte)0);                        // AccessNumber
                    writer.Write((ushort)fileName.Length);
                    writer.Write(typeId);                         // TypeID
                    writer.Write((uint)(i + 1));                  // FileID
                    writer.Write(fileName);
                }
            }
            return path;
        }

        private static FSO.Content.Content NewContent(string basePath, params string[] allFiles)
        {
            var content = (FSO.Content.Content)RuntimeHelpers.GetUninitializedObject(typeof(FSO.Content.Content));
            content.BasePath = basePath;
            //the real constructor fills these by scanning the install; an uninitialised one has
            //them null, and FileProvider.Init reads AllFiles unconditionally
            content.AllFiles = allFiles;
            content.TS1AllFiles = new string[0];
            content.ContentFiles = new string[0];
            return content;
        }

        #endregion

        #region F-24 - the type id is a fact about the archive, not about a Dictionary

        [Fact]
        public void TheEstimatedTypeIdIsTheFirstOneTheArchiveActuallyHolds()
        {
            // `EntriesById.FirstOrDefault().Key` was the old answer, and dictionary enumeration
            // order is unspecified and changes with insertion and resize history - so the same
            // archive estimated differently depending on how the provider had been built.
            WriteFar3("avatar.far3", 0xABCD1234, ("a.dat", Blob(1, 4)), ("b.dat", Blob(2, 4)));

            var provider = new FAR3Provider<byte[]>(NewContent(_root), new RawCodec(), "avatar.far3");
            provider.Init();

            Assert.Equal(0xABCD1234u, provider.EstimateTypeId());
        }

        [Fact]
        public void TheEstimatedTypeIdIsTheSameEveryTimeTheProviderIsBuilt()
        {
            // The regression the ordering claim rests on: a rebuild of the same archive must give
            // the same answer, whatever the dictionary does with its buckets.
            WriteFar3("stable.far3", 0x0000BEEF,
                ("a.dat", Blob(1, 4)), ("b.dat", Blob(2, 4)), ("c.dat", Blob(3, 4)),
                ("d.dat", Blob(4, 4)), ("e.dat", Blob(5, 4)));

            uint first = 0;
            for (int i = 0; i < 5; i++)
            {
                var provider = new FAR3Provider<byte[]>(NewContent(_root), new RawCodec(), "stable.far3");
                provider.Init();
                var id = provider.EstimateTypeId();
                if (i == 0) first = id;
                else Assert.Equal(first, id);
            }
            Assert.Equal(0x0000BEEFu, first);
        }

        [Fact]
        public void AProviderWithNothingInItHasNoTypeIdToEstimate()
        {
            // 0 was the old answer for an empty provider, and it is indistinguishable from "this
            // archive really does hold type 0". Not being able to answer is the honest answer.
            var provider = new FAR3Provider<byte[]>(NewContent(_root), new RawCodec());

            Assert.Equal(0u, provider.EstimateTypeId());
        }

        #endregion

        #region F-20 - the "initialised" flag is set when the work is done

        [Fact]
        public void AProviderWhoseArchiveCannotBeOpenedIsNotLeftClaimingToBeReady()
        {
            // The flag used to be set on the line before the work. One corrupt .dat anywhere in the
            // install therefore left the provider permanently, silently uninitialised: Init could
            // never be called again, and every lookup answered default(T) for the rest of the
            // session with nothing in the log.
            var provider = new FAR3Provider<byte[]>(NewContent(_root), new RawCodec(), "not-an-archive.far3");

            Assert.ThrowsAny<Exception>(() => provider.Init());
            Assert.False(provider.Initialized, "a failed Init must not leave the flag set");

            //and fixing the file and retrying is therefore possible, which is the whole point
            WriteFar3("not-an-archive.far3", 0x11, ("a.dat", Blob(7, 4)));
            provider.Init();

            Assert.True(provider.Initialized);
            Assert.NotNull(provider.Get("a.dat"));
        }

        [Fact]
        public void InitialisingTwiceIsANoOpAndDoesNotReopenTheArchives()
        {
            WriteFar3("twice.far3", 0x22, ("a.dat", Blob(3, 8)));

            var provider = new FAR3Provider<byte[]>(NewContent(_root), new RawCodec(), "twice.far3");
            provider.Init();
            provider.Init();
            provider.Init();

            Assert.Equal(1, provider.List().Count);
            Assert.Equal(Blob(3, 8), provider.Get("a.dat"));
        }

        [Fact]
        public void UninitClosesTheArchivesTheProviderOpened()
        {
            // F-25. A FAR3Archive is IDisposable and nothing in the codebase called it, so every
            // archive in a full install was a permanently open handle. `Init` closes the previous
            // set, so re-initialising replaces rather than stacks.
            WriteFar3("reinit.far3", 0x33, ("a.dat", Blob(4, 8)));

            var provider = new FAR3Provider<byte[]>(NewContent(_root), new RawCodec(), "reinit.far3");
            provider.Init();
            var archive = provider.List().Cast<Far3ProviderEntry<byte[]>>().First().Archive;
            Assert.NotNull(archive);

            provider.Init();
            Assert.Throws<FAR3Exception>(() => archive.GetItemByID(0x33000001u));

            provider.Uninit();
            Assert.False(provider.Initialized);
            Assert.Empty(provider.List());
        }

        [Fact]
        public void UninitOnAProviderThatWasNeverInitialisedIsHarmless()
        {
            var provider = new FAR3Provider<byte[]>(NewContent(_root), new RawCodec());
            provider.Uninit();
            provider.Uninit();
        }

        #endregion

        #region F-28 - all three providers answer the same way for a name

        [Theory]
        [InlineData("a.dat")]
        [InlineData("A.DAT")]
        [InlineData("a.Dat")]
        public void TheFar3ProviderFoldsCaseAndToleratesANullName(string asked)
        {
            WriteFar3("case3.far3", 0x44, ("A.dat", Blob(5, 8)));

            var provider = new FAR3Provider<byte[]>(NewContent(_root), new RawCodec(), "case3.far3");
            provider.Init();

            Assert.Equal(Blob(5, 8), provider.Get(asked));
            Assert.Null(provider.Get((string)null));
            Assert.Null(provider.Get("not-there.dat"));
        }

        [Theory]
        [InlineData("a.txt")]
        [InlineData("A.TXT")]
        [InlineData("a.Txt")]
        public void TheFileProviderFoldsCaseAndToleratesANullName(string asked)
        {
            File.WriteAllBytes(Path.Combine(_root, "A.txt"), Blob(9, 8));

            var provider = new FileProvider<byte[]>(
                NewContent(_root, "A.txt"), new RawCodec(),
                new System.Text.RegularExpressions.Regex(@"\.txt$"));
            provider.Init();

            Assert.Equal(Blob(9, 8), provider.Get(asked));
            Assert.Null(provider.Get("not-there.txt"));
            //a null name is a miss, not a NullReferenceException out of content init
            Assert.Null(provider.Get((string)null));
            Assert.Null(provider.ThrowawayGet((string)null));
        }

        [Fact]
        public void TwoFilesWithTheSameNameResolveToOneAndTheLookupIsStable()
        {
            // Which of two same-named files a lookup returns is filesystem enumeration order. That
            // is a load-order assumption rather than a policy, so the policy is written down: the
            // first one found is the one a lookup returns. Init now says so in the log rather than
            // overwriting in silence.
            var sub = Path.Combine(_root, "sub");
            Directory.CreateDirectory(sub);
            File.WriteAllBytes(Path.Combine(sub, "dup.txt"), Blob(0xAA, 4));
            File.WriteAllBytes(Path.Combine(sub, "zebra.txt"), Blob(0xBB, 4));
            File.WriteAllBytes(Path.Combine(_root, "dup.txt"), Blob(0xCC, 4));

            var provider = new FileProvider<byte[]>(
                NewContent(_root, "dup.txt", "sub/dup.txt", "sub/zebra.txt"), new RawCodec(),
                new System.Text.RegularExpressions.Regex(@"dup\.txt$"));
            provider.Init();

            //both references are still listed...
            Assert.Equal(2, provider.GetEntriesForExtension(".txt").Count(x => x.Name == "dup.txt"));

            //...and the name resolves to one file's bytes, not a mixture, and to the same one
            //every time it is asked
            var resolved = provider.Get("dup.txt");
            Assert.True(resolved.SequenceEqual(Blob(0xAA, 4)) || resolved.SequenceEqual(Blob(0xCC, 4)),
                "a name must resolve to one file's bytes, not a mixture");
            Assert.Equal(resolved, provider.Get("dup.txt"));
        }

        #endregion

        #region F-33 - a LotZoning.iff line out of the player's own save

        [Theory]
        [InlineData("50, residential", 50, false)]
        [InlineData("54, community", 54, true)]
        [InlineData("1, residential", 1, false)]
        [InlineData("130, community", 130, true)]
        [InlineData(" 7 , community", 7, true)]
        public void AWellFormedZoningLineIsReadAsItsLotAndZone(string line, int lot, bool community)
        {
            Assert.True(TS1NeighborhoodProvider.TryParseZoningLine(line, out var parsed, out var isCommunity));
            Assert.Equal((short)lot, parsed);
            Assert.Equal(community, isCommunity);
        }

        /// <summary>
        /// The regression. The line used to be split on commas and fed straight to
        /// <c>short.Parse(split[0])</c> with <c>split[1]</c> read on the next statement and
        /// nothing in between: a blank line, a line with no comma, or a first field that is not a
        /// number was an <c>IndexOutOfRangeException</c> or a <c>FormatException</c> out of
        /// <c>InitSpecific</c> - out of content init, with the window already up, naming a file
        /// the player never created.
        /// </summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("50")]
        [InlineData("50 residential")]
        [InlineData("nonsense, residential")]
        [InlineData(", residential")]
        [InlineData("99999999, residential")]
        public void ALineThatIsNotAZoningLineIsSkippedRatherThanCrashingTheLoad(string line)
        {
            Assert.False(TS1NeighborhoodProvider.TryParseZoningLine(line, out _, out _));
        }

        /// <summary>
        /// A signed lot number is a line this reader accepted before the fix and must still
        /// accept: <c>short.Parse</c> took a sign, so tightening the parse to
        /// <c>NumberStyles.None</c> would change what an existing <c>LotZoning.iff</c> means for
        /// a locale that spells its sign differently. Only the throwing became a <c>false</c>.
        /// </summary>
        [Theory]
        [InlineData("+54, community", 54, true)]
        [InlineData("-1, residential", -1, false)]
        public void ASignedLotNumberStillReadsTheWayItAlwaysDid(string line, int lot, bool community)
        {
            Assert.True(TS1NeighborhoodProvider.TryParseZoningLine(line, out var parsed, out var isCommunity));
            Assert.Equal((short)lot, parsed);
            Assert.Equal(community, isCommunity);
        }

        /// <summary>
        /// The one line the tolerant reader still refuses, pinned so the tolerance above is not
        /// mistaken for "accepts anything with a comma in it".
        /// </summary>
        [Fact]
        public void ALineWithAnEmptySecondHalfIsResidentialRatherThanAError()
        {
            //"50," is one the old code read as residential (split[1] is not " community"), so it
            //still is. The distinction from "50" - no comma at all - is the load-bearing one.
            Assert.True(TS1NeighborhoodProvider.TryParseZoningLine("50,", out var lot, out var isCommunity));
            Assert.Equal((short)50, lot);
            Assert.False(isCommunity);
        }

        /// <summary>
        /// What the client writes and what the provider reads, end to end - the round trip the
        /// audit says the culture coupling rests on. <c>LotZoning</c> is a client type, so this
        /// lives here as the half that consumes the file.
        /// </summary>
        [Theory]
        [InlineData(1, false)]
        [InlineData(54, true)]
        [InlineData(130, true)]
        public void ALineTheClientWritesIsALineTheProviderReads(int lot, bool community)
        {
            var line = LotZoning.Format(lot,
                community ? LotZoning.ZoneCommunity : LotZoning.ZoneResidential);

            Assert.True(TS1NeighborhoodProvider.TryParseZoningLine(line, out var parsed, out var isCommunity));
            Assert.Equal((short)lot, parsed);
            Assert.Equal(community, isCommunity);
        }

        #endregion
    }
}
