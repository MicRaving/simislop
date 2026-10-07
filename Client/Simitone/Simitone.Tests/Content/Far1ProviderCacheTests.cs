using FSO.Content.Framework;
using FSO.Files.FAR1;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Xunit;

namespace Simitone.Tests.Content
{
    /// <summary>
    /// <see cref="FAR1Provider{T}"/>'s cache, against two synthetic FAR1 archives that hold an
    /// entry of the <i>same</i> name. TS1 <c>.far</c> files routinely do: a
    /// <c>walking.iff</c> ships in more than one of them, an <c>Objects.iff</c> in several.
    ///
    /// <para>
    /// The provider used to key its cache on the bare entry filename while taking the value from
    /// the entry's own archive, so whichever archive was opened first claimed the name for the
    /// rest of the session and every later request for the other archive's copy returned the
    /// first archive's bytes. No error anywhere - a wrong mesh, a wrong texture, a wrong
    /// <c>Global.iff</c>. <c>FAR3Provider</c> never had the bug because it keys on a file id.
    /// </para>
    ///
    /// <para>
    /// The provider is built against a <c>Content</c> allocated uninitialised with just a
    /// <c>BasePath</c>, because <c>GetPath</c> is a <c>Path.Combine</c> and the real
    /// constructor does a whole content scan. <c>Init</c> is the real one, so the archives, the
    /// name index and the extension grouping are all built by the production code.
    /// </para>
    /// </summary>
    public sealed class Far1ProviderCacheTests : IDisposable
    {
        private readonly string _root;

        public Far1ProviderCacheTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "simitone-far1-" + Guid.NewGuid().ToString("N"));
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

        #region the fixture

        /// <summary>
        /// A codec that hands the raw bytes back, so the assertion is about which archive the
        /// bytes came from rather than about any real content format.
        /// </summary>
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

        /// <summary>
        /// A minimal but real FAR1 archive: the magic, version 1, the manifest offset, the
        /// payloads, and a manifest of the records <c>FAR1Archive</c> reads.
        /// </summary>
        private string WriteFar(string name, params (string entryName, byte[] data)[] entries)
        {
            var path = Path.Combine(_root, name);

            using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("FAR!byAZ"));
                writer.Write((uint)1);
                writer.Write((uint)0);                        // patched below

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
                    writer.Write((int)entries[i].data.Length);     //DataLength
                    writer.Write((int)entries[i].data.Length);     //DataLength2
                    writer.Write((int)offsets[i]);                 //DataOffset
                    writer.Write((short)fileName.Length);           //v1b filename length
                    writer.Write(fileName);
                }
            }

            return path;
        }

        private static byte[] Blob(byte fill, int length)
        {
            var data = new byte[length];
            for (int i = 0; i < length; i++) data[i] = fill;
            return data;
        }

        private static FSO.Content.Content NewContent(string basePath)
        {
            var content = (FSO.Content.Content)RuntimeHelpers.GetUninitializedObject(typeof(FSO.Content.Content));
            content.BasePath = basePath;
            return content;
        }

        private FAR1Provider<byte[]> NewProvider(params string[] farFiles)
        {
            var provider = new FAR1Provider<byte[]>(NewContent(_root), new RawCodec(), farFiles);
            provider.Init();
            return provider;
        }

        #endregion

        /// <summary>
        /// The regression. Both archives hold a <c>shared.dat</c>; each request must return its
        /// own archive's copy.
        /// </summary>
        [Fact]
        public void TwoArchivesHoldingTheSameEntryNameEachReturnTheirOwnBytes()
        {
            var first = Blob(0xAA, 64);
            var second = Blob(0xBB, 96);
            WriteFar("Sims_1.1.far", ("shared.dat", first));
            WriteFar("Sims_2.far", ("shared.dat", second));

            var provider = NewProvider("Sims_1.1.far", "Sims_2.far");

            //both entries are indexed by extension, which is the only place both survive: the
            //by-name index is a lookup, not an enumeration
            var entries = provider.GetEntriesForExtension(".dat");
            Assert.Equal(2, entries.Count);

            var fromFirst = provider.Get(entries[0]);
            var fromSecond = provider.Get(entries[1]);

            Assert.Equal(first, fromFirst);
            Assert.Equal(second, fromSecond);
        }

        /// <summary>
        /// Asking twice for the same entry is still one cache hit - the fix has to keep the
        /// cache working, not disable it.
        /// </summary>
        [Fact]
        public void TheSameEntryIsStillServedFromTheCache()
        {
            var data = Blob(0x11, 128);
            WriteFar("only.far", ("entry.dat", data));

            var provider = NewProvider("only.far");
            var entry = Assert.Single(provider.GetEntriesForExtension(".dat"));

            var first = provider.Get(entry);
            var second = provider.Get(entry);

            Assert.Equal(data, first);
            Assert.Equal(data, second);
            Assert.Equal(FAR1Provider<byte[]>.CacheKeyFor(entry),
                FAR1Provider<byte[]>.CacheKeyFor(entry));
        }

        /// <summary>
        /// The key has to name the archive as well as the entry: the two are equal only by
        /// accident, and the object identity of the archive is not a safe stand-in for its path.
        /// </summary>
        [Fact]
        public void TheCacheKeyNamesTheArchiveAndTheEntry()
        {
            var firstArchive = new FAR1Archive(WriteFar("k1.far", ("shared.dat", Blob(1, 8))), true);
            var secondArchive = new FAR1Archive(WriteFar("k2.far", ("shared.dat", Blob(2, 8))), true);

            var firstEntry = new Far1ProviderEntry<byte[]>(null) { Archive = firstArchive, FarEntry = firstArchive.GetAllFarEntries()[0] };
            var secondEntry = new Far1ProviderEntry<byte[]>(null) { Archive = secondArchive, FarEntry = secondArchive.GetAllFarEntries()[0] };

            Assert.Equal("shared.dat", firstEntry.FarEntry.Filename);
            Assert.Equal("shared.dat", secondEntry.FarEntry.Filename);
            Assert.NotEqual(FAR1Provider<byte[]>.CacheKeyFor(firstEntry),
                FAR1Provider<byte[]>.CacheKeyFor(secondEntry));
        }

        /// <summary>
        /// Entries of <i>different</i> names in the same archive are still distinct keys, so the
        /// composite did not collapse the cache into one entry per archive.
        /// </summary>
        [Fact]
        public void DifferentNamesInOneArchiveAreDifferentKeys()
        {
            var archive = new FAR1Archive(
                WriteFar("k3.far", ("a.dat", Blob(1, 8)), ("b.dat", Blob(2, 8))), true);
            var entries = archive.GetAllFarEntries();

            var keys = entries.Select(e =>
                FAR1Provider<byte[]>.CacheKeyFor(new Far1ProviderEntry<byte[]>(null) { Archive = archive, FarEntry = e })).ToList();

            Assert.Equal(2, keys.Distinct().Count());
        }

        /// <summary>
        /// A provider over two archives is built by the production <c>Init</c> above, so this
        /// is only here to say what the name index itself is for: one entry per name, resolved
        /// to a single archive. That is a lookup policy and is not what the cache is for.
        /// </summary>
        [Fact]
        public void TheByNameIndexResolvesASharedNameToOneArchive()
        {
            WriteFar("n1.far", ("shared.dat", Blob(0xCC, 32)));
            WriteFar("n2.far", ("shared.dat", Blob(0xDD, 32)));

            var provider = NewProvider("n1.far", "n2.far");

            //whatever it resolves to, it is one of the two - and it is the same one every time
            var first = provider.Get("shared.dat");
            Assert.NotNull(first);
            Assert.Equal(first, provider.Get("shared.dat"));
        }

        /// <summary>
        /// The throwaway path never consults the cache at all, so it is unaffected - and worth
        /// saying, because it is the path walls and floors use and it has to keep working.
        /// </summary>
        [Fact]
        public void TheThrowawayPathIsUnaffectedByTheCache()
        {
            var first = Blob(0xAA, 64);
            var second = Blob(0xBB, 96);
            WriteFar("t1.far", ("shared.dat", first));
            WriteFar("t2.far", ("shared.dat", second));

            var provider = NewProvider("t1.far", "t2.far");
            var entries = provider.GetEntriesForExtension(".dat");

            Assert.Equal(first, provider.ThrowawayGet(entries[0]));
            Assert.Equal(second, provider.ThrowawayGet(entries[1]));
        }

        /// <summary>
        /// F-28. The by-name lookup folded case for its whole life; its two siblings - FAR3Provider
        /// and FileProvider - lowercased both the key asked for and the key stored, and this one
        /// did neither. TS1 object names reach a provider out of OBJD <c>FileName</c> fields
        /// written by hand and by mods, and the game works around it by lowercasing at the call
        /// site. Two providers folding case and one not is a content bug that only shows up for
        /// mixed-case content, which is exactly the content nobody tests.
        /// </summary>
        [Theory]
        [InlineData("shared.dat")]
        [InlineData("SHARED.DAT")]
        [InlineData("ShArEd.DaT")]
        public void TheByNameLookupFoldsCaseLikeItsTwoSiblings(string asked)
        {
            WriteFar("case.far", ("Shared.DAT", Blob(0xEF, 16)));

            var provider = NewProvider("case.far");

            Assert.NotNull(provider.Get(asked));
            Assert.Equal(Blob(0xEF, 16), provider.Get(asked));
            Assert.Equal(Blob(0xEF, 16), provider.ThrowawayGet(asked));
        }

        /// <summary>
        /// A name that is not in the index is a miss, and a null name is also a miss rather than a
        /// <c>NullReferenceException</c> out of content init. Both halves of the same question:
        /// "what do the three providers answer for a name they do not have?"
        /// </summary>
        [Fact]
        public void ANameThatIsNotInTheIndexIsAMissAndSoIsANull()
        {
            WriteFar("miss.far", ("present.dat", Blob(1, 8)));

            var provider = NewProvider("miss.far");

            Assert.Null(provider.Get("absent.dat"));
            Assert.Null(provider.Get((string)null));
            Assert.Null(provider.ThrowawayGet("absent.dat"));
            Assert.Null(provider.ThrowawayGet((string)null));
        }

        /// <summary>
        /// F-25. One <c>.far</c> is one open file handle, and the provider used to have no way to
        /// close any of them: a full The Sims install left a few hundred permanently open. This
        /// pins the contract that lets it be closed, and that <c>Init</c> closes the previous set
        /// before building a new one rather than stacking another set on top.
        /// </summary>
        [Fact]
        public void ReInitialisingClosesTheArchivesThePreviousRunOpened()
        {
            var data = Blob(0x77, 24);
            WriteFar("again.far", ("entry.dat", data));

            var provider = NewProvider("again.far");
            var first = provider.GetEntriesForExtension(".dat")[0].Archive;
            Assert.NotNull(first);
            Assert.False(first.IsClosed);

            provider.Init();
            var second = provider.GetEntriesForExtension(".dat")[0].Archive;

            Assert.True(first.IsClosed, "the archive from the previous Init must have been closed");
            Assert.False(second.IsClosed);
            Assert.Equal(data, provider.Get("entry.dat"));
        }

        [Fact]
        public void UninitClosesEveryArchiveAndTheProviderStopsAnswering()
        {
            WriteFar("bye.far", ("entry.dat", Blob(0x66, 24)));

            var provider = NewProvider("bye.far");
            var archive = provider.GetEntriesForExtension(".dat")[0].Archive;
            Assert.False(archive.IsClosed);

            provider.Uninit();

            Assert.True(archive.IsClosed);
            Assert.Null(provider.Get("entry.dat"));
            //the extension grouping answers "none" rather than a null list, which is what the
            //provider's own lookup contract is
            var ofType = provider.GetEntriesForExtension(".dat");
            Assert.True(ofType == null || ofType.Count == 0);
        }

        [Fact]
        public void UninitIsHarmlessToCallTwiceAndOnAProviderThatWasNeverInitialised()
        {
            var content = NewContent(_root);
            var provider = new FAR1Provider<byte[]>(content, new RawCodec(), "never.far");

            provider.Uninit();
            provider.Uninit();
        }
    }
}
