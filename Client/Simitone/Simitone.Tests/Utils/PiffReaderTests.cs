using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using FSO.Files.Formats.IFF;
using FSO.Files.Formats.IFF.Chunks;
using FSO.Files.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// <see cref="PIFF"/> as a <em>reader</em>, and <see cref="PIFFEntry.Apply"/> against a
    /// patch file that has been damaged.
    /// </summary>
    ///
    /// <para>
    /// <b>The gap.</b> <c>PiffDiffTests</c> covers the diff/apply pair for patches this build
    /// generates, which is the round trip that has to work. It does not cover the other
    /// direction: a patch file arriving from somewhere else. A PIFF is the format users install
    /// by hand - downloaded from a forum, dropped into the mod folder - and it is applied from
    /// <c>IffFile.SetFilename</c>, which is content init, before the game has a window to report
    /// anything in. Every count in it was a number off the wire with nothing checked, and every
    /// copy in <c>Apply</c> was a bare <c>Array.Copy</c> fed a <c>uint</c> subtraction that could
    /// wrap.
    /// </para>
    ///
    /// <para>
    /// <b>The fixtures are hand-built.</b> The piff's own framing is a version, two length-prefixed
    /// strings, an entry count and then per entry a type, an id, a comment, a mode and - for a
    /// patch - a label, flags, a new id, a new size, a patch count and the patches themselves.
    /// Written out here so a test can corrupt any one of those numbers and be sure which one it
    /// corrupted.
    /// </para>
    public sealed class PiffReaderTests
    {
        #region the format, laid out by hand

        /// <summary>
        /// A version 2 piff with one patch entry, written byte by byte.
        /// </summary>
        /// <param name="chunkType">The four-character chunk type the entry names.</param>
        /// <param name="patches">The patches, as (offset, size, mode, inline data).</param>
        /// <param name="newDataSize">
        /// The result size the entry declares. Overriding it is the damage.
        /// </param>
        /// <param name="patchCount">
        /// The patch count the entry declares. Overriding it is the damage.
        /// </param>
        private static byte[] Piff(
            string chunkType = "STR#",
            (uint Offset, uint Size, PIFFPatchMode Mode, byte[] Data)[] patches = null,
            uint? newDataSize = null,
            uint? patchCount = null,
            int entryCount = 1,
            ushort sourceChunkId = 7,
            ushort newChunkId = 7)
        {
            patches = patches ?? new (uint, uint, PIFFPatchMode, byte[])[0];
            var body = new List<byte>();

            //version 2, little-endian
            body.AddRange(UInt16(2));
            body.AddRange(PascalString("house01.iff"));     //SourceIff
            body.AddRange(PascalString("a patch"));        //Comment

            body.AddRange(UInt16((ushort)entryCount));
            if (entryCount == 0) return body.ToArray();

            //the entry: type, chunk id, comment, entry type
            body.AddRange(Encoding.ASCII.GetBytes(chunkType.PadRight(4).Substring(0, 4)));
            body.AddRange(UInt16(sourceChunkId));         //the piff chunk is little-endian
            body.AddRange(PascalString("patched"));
            body.Add((byte)PIFFEntryType.Patch);

            //label, flags, new chunk id, new data size
            body.AddRange(PascalString(""));
            body.AddRange(UInt16(0x0010));
            body.AddRange(UInt16(newChunkId));
            var declared = newDataSize ?? (uint)patches.Sum(p => p.Data?.Length ?? 0);
            body.AddRange(UInt32(declared));

            //the patch count
            body.AddRange(UInt32(patchCount ?? (uint)patches.Length));

            foreach (var p in patches)
            {
                body.AddRange(VarLen(p.Offset));
                body.AddRange(VarLen(p.Size));
                body.Add((byte)p.Mode);
                if (p.Mode == PIFFPatchMode.Add && p.Data != null) body.AddRange(p.Data);
            }

            return body.ToArray();
        }

        private static byte[] UInt32(uint value)
            => new byte[] { (byte)value, (byte)(value >> 8), (byte)(value >> 16), (byte)(value >> 24) };

        /// <summary>Little-endian, because the piff chunk is.</summary>
        private static byte[] UInt16(ushort value)
            => new byte[] { (byte)value, (byte)(value >> 8) };

        /// <summary>A .NET-style length-prefixed UTF-8 string: what the writer emits.</summary>
        private static byte[] PascalString(string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            return new[] { (byte)bytes.Length }.Concat(bytes).ToArray();
        }

        /// <summary>Seven bits per byte, least significant group first, high bit = "more".</summary>
        private static byte[] VarLen(uint value)
        {
            var bytes = new List<byte>();
            do
            {
                var b = (byte)(value & 0x7F);
                value >>= 7;
                if (value != 0) b |= 0x80;
                bytes.Add(b);
            } while (value != 0);
            return bytes.ToArray();
        }

        private static byte[] Bytes(params int[] values)
        {
            var bytes = new byte[values.Length];
            for (int i = 0; i < values.Length; i++) bytes[i] = (byte)values[i];
            return bytes;
        }

        /// <summary>Reads a piff's payload, the way IffFile would hand it to the chunk.</summary>
        private static PIFF Read(byte[] payload)
        {
            var piff = new PIFF { ChunkProcessed = false };
            using (var stream = new MemoryStream(payload))
            {
                piff.Read(null, stream);
            }
            return piff;
        }

        #endregion

        #region a real read

        [Fact]
        public void APatchEntryComesBackWithItsTypeItsIdAndItsPatches()
        {
            var payload = Piff("STR#", new[]
            {
                ((uint)0, (uint)2, PIFFPatchMode.Add, Bytes(0xAA, 0xBB)),
                ((uint)2, (uint)1, PIFFPatchMode.Remove, null),
            });

            var piff = Read(payload);

            Assert.Equal("house01.iff", piff.SourceIff);
            Assert.Equal("a patch", piff.Comment);
            var entry = Assert.Single(piff.Entries);
            Assert.Equal("STR#", entry.Type);
            Assert.Equal(7, entry.ChunkID);
            Assert.Equal(7, entry.NewChunkID);
            Assert.Equal(PIFFEntryType.Patch, entry.EntryType);
            Assert.Equal(2, entry.Patches.Length);
            Assert.Equal(Bytes(0xAA, 0xBB), entry.Patches[0].Data);
            Assert.Equal(PIFFPatchMode.Remove, entry.Patches[1].Mode);
        }

        [Fact]
        public void APatchEntryWithNoPatchesIsStillAnEntry()
        {
            // an empty diff is the normal shape of a patch that only changes a label
            var piff = Read(Piff());

            Assert.Single(piff.Entries);
            Assert.Empty(piff.Entries[0].Patches);
        }

        [Fact]
        public void APiffWithNoEntriesIsNotAnError()
        {
            // a piff that removes nothing adds nothing. It should apply as a no-op rather than
            // throw, because a user who saved a patch before changing their mind still has it
            var piff = Read(Piff(entryCount: 0));

            Assert.NotNull(piff);
            Assert.Empty(piff.Entries);
        }

        #endregion

        #region damage in the reader

        [Fact]
        public void APatchCountTheChunkCannotHoldIsRefused()
        {
            // four billion patches is a four-billion-element array on a file of a few kilobytes,
            // and a piff is untrusted input that is parsed before the game has a window
            var payload = Piff(patches: new[]
            {
                ((uint)0, (uint)1, PIFFPatchMode.Add, Bytes(0xAA)),
            }, patchCount: 0xFFFFFFFF);

            var thrown = Assert.Throws<IffFormatException>(() => Read(payload));
            Assert.Contains("patches", thrown.Message);
            Assert.Contains("house01.iff", thrown.Message);
        }

        [Fact]
        public void APatchCountOfAllOnesWithNoPatchesBehindItIsRefusedTheSameWay()
        {
            // the plausible-but-wrong shape: a plausible number, in a chunk with nothing in it
            var payload = Piff(patchCount: 50_000);

            var thrown = Assert.Throws<IffFormatException>(() => Read(payload));
            Assert.Contains("patches", thrown.Message);
        }

        [Fact]
        public void AnAddedPatchLongerThanTheChunkIsRefusedRatherThanReadShort()
        {
            // BinaryReader.ReadBytes reports a short read by returning fewer bytes, so the patch
            // used to arrive with a data array shorter than its own declared size - and a patch
            // that reconstructs the wrong chunk is worse than one that is refused
            var payload = Piff(patches: new[]
            {
                ((uint)0, (uint)64, PIFFPatchMode.Add, Bytes(0xAA, 0xBB)),
            });

            var thrown = Assert.Throws<IoFormatException>(() => Read(payload));
            Assert.Contains("payload", thrown.Message);
        }

        [Fact]
        public void ATruncatedPiffIsReportedRatherThanReadAsAnEmptyOne()
        {
            var payload = Piff(patches: new[]
            {
                ((uint)0, (uint)2, PIFFPatchMode.Add, Bytes(0xAA, 0xBB)),
            });

            //cut inside the patch's inline data
            Assert.ThrowsAny<Exception>(() => Read(payload.Take(payload.Length - 1).ToArray()));
        }

        #endregion

        #region damage in Apply

        [Fact]
        public void APatchThatStartsBehindTheWritePointerIsRefused()
        {
            // THE regression. p.Offset - destPtr is unsigned: a patch that starts behind where the
            // writer already is wraps to about four billion, and that went into Array.Copy as a
            // length. The result was an ArgumentException carrying an index and nothing else, for
            // a player who had installed the wrong patch file.
            var entry = new PIFFEntry
            {
                Type = "STR#",
                ChunkID = 1,
                NewDataSize = 16,
                Patches = new[]
                {
                    new PIFFPatch { Offset = 4, Size = 4, Mode = PIFFPatchMode.Remove, Data = null },
                    //starts behind the write pointer, which after the first patch is at 4
                    new PIFFPatch { Offset = 0, Size = 1, Mode = PIFFPatchMode.Remove, Data = null },
                },
            };

            var thrown = Assert.Throws<IffFormatException>(() => entry.Apply(new byte[16]));
            Assert.Contains("behind", thrown.Message);
        }

        [Fact]
        public void APatchThatStartsPastTheEndOfTheResultIsRefused()
        {
            var entry = new PIFFEntry
            {
                Type = "STR#",
                ChunkID = 1,
                NewDataSize = 8,
                Patches = new[] { new PIFFPatch { Offset = 64, Size = 0, Mode = PIFFPatchMode.Remove } },
            };

            var thrown = Assert.Throws<IffFormatException>(() => entry.Apply(new byte[64]));
            Assert.Contains("past the end", thrown.Message);
        }

        [Fact]
        public void APatchThatRunsPastTheEndOfTheResultIsRefused()
        {
            var entry = new PIFFEntry
            {
                Type = "STR#",
                ChunkID = 1,
                NewDataSize = 4,
                Patches = new[]
                {
                    new PIFFPatch { Offset = 2, Size = 8, Mode = PIFFPatchMode.Add, Data = new byte[8] },
                },
            };

            var thrown = Assert.Throws<IffFormatException>(() => entry.Apply(new byte[8]));
            Assert.Contains("runs past the end", thrown.Message);
        }

        [Fact]
        public void AResultSizeThisBuildCannotAllocateIsRefused()
        {
            // NewDataSize is a uint off the file used as an int array length, so anything at or
            // above 2^31 was an OverflowException
            var entry = new PIFFEntry
            {
                Type = "STR#",
                ChunkID = 1,
                NewDataSize = uint.MaxValue,
                Patches = new PIFFPatch[0],
            };

            var thrown = Assert.Throws<IffFormatException>(() => entry.Apply(new byte[0]));
            Assert.Contains("above what this build can hold", thrown.Message);
        }

        [Fact]
        public void ATrailingRemoveThatRunsPastTheEndOfTheOldDataStillApplies()
        {
            // Deliberate, and the reason Remove is clamped rather than checked. The encoder
            // legitimately emits a remove for the tail of the source that has no counterpart in
            // the result - see PiffEncoder's `if (oldData.Length > i)` case, where the new data
            // ran out first - and refusing that would reject a patch this build generated, which is
            // the one thing the diff/apply pair must never do. A Remove only moves a pointer and
            // copies nothing, so there is nothing to read out of bounds.
            var original = Bytes(1, 2, 3, 4, 5, 6, 7, 8);
            var entry = new PIFFEntry
            {
                Type = "STR#",
                ChunkID = 1,
                NewDataSize = 4,
                Patches = new[] { new PIFFPatch { Offset = 4, Size = 64, Mode = PIFFPatchMode.Remove } },
            };

            Assert.Equal(Bytes(1, 2, 3, 4), entry.Apply(original));
        }

        [Fact]
        public void APatchThatNeedsBytesPastTheEndOfTheChunkIsRefusedAtTheCopy()
        {
            // the other half of the same decision: a Remove on its own is harmless, but a Remove
            // that leaves the source pointer short of where the next copy has to start is not
            var entry = new PIFFEntry
            {
                Type = "STR#",
                ChunkID = 1,
                NewDataSize = 8,
                Patches = new[]
                {
                    new PIFFPatch { Offset = 0, Size = 6, Mode = PIFFPatchMode.Remove },
                    new PIFFPatch { Offset = 6, Size = 2, Mode = PIFFPatchMode.Remove },
                },
            };

            var thrown = Assert.Throws<IffFormatException>(() => entry.Apply(new byte[4]));
            Assert.Contains("0 in a 4-byte source", thrown.Message);
            Assert.Contains("does not belong to this chunk", thrown.Message);
        }

        [Fact]
        public void APatchThatNeedsAChunkWhichHasNoneSaysSo()
        {
            // IffFile.Patch calls Apply(chunk.ChunkData ?? chunk.OriginalData), and for a chunk the
            // game patched in place rather than read from a file both are null. A pure-Add patch
            // still works; one that has to copy from the original cannot, and used to be an
            // ArgumentNullException from Array.Copy.
            var addOnly = new PIFFEntry
            {
                Type = "STR#",
                ChunkID = 1,
                NewDataSize = 3,
                Patches = new[]
                {
                    new PIFFPatch { Offset = 0, Size = 3, Mode = PIFFPatchMode.Add, Data = Bytes(1, 2, 3) },
                },
            };

            Assert.Equal(Bytes(1, 2, 3), addOnly.Apply(null));

            var needsTheOriginal = new PIFFEntry
            {
                Type = "STR#",
                ChunkID = 1,
                NewDataSize = 3,
                Patches = new[] { new PIFFPatch { Offset = 0, Size = 0, Mode = PIFFPatchMode.Remove } },
            };

            var thrown = Assert.Throws<IffFormatException>(() => needsTheOriginal.Apply(null));
            Assert.Contains("has none", thrown.Message);
        }

        #endregion

        #region applying a patch to a file

        [Fact]
        public void APatchNamingAChunkTypeThisBuildDoesNotHaveIsSkippedRatherThanFatal()
        {
            // The one that stopped the game starting. IffFile.Patch and IffFile.Revert both did an
            // unguarded CHUNK_TYPES[e.Type] lookup on a string that came out of a patch file, and
            // they are reached from SetFilename - which is content init, before the window is up.
            // A third-party patch built for a newer FreeSO therefore made the game refuse to
            // launch, with a KeyNotFoundException naming a chunk the player had never heard of.
            // An unknown chunk type is skipped here exactly as an unknown chunk in a data file is.
            var target = new IffFile();
            target.AddChunk(new PNG
            {
                ChunkID = 512,
                ChunkLabel = "Lot Thumbnail",
                ChunkProcessed = true,
                ChunkType = "PNG_",
                data = Bytes(1, 2, 3, 4),
            });

            var patch = new IffFile();
            patch.AddChunk(new PIFF
            {
                ChunkID = 256,
                ChunkProcessed = true,
                SourceIff = "target.iff",
                Entries = new[]
                {
                    new PIFFEntry
                    {
                        Type = "ZZZZ",          //not in CHUNK_TYPES
                        ChunkID = 512,
                        NewChunkID = 512,
                        EntryType = PIFFEntryType.Patch,
                        NewDataSize = 4,
                        Patches = new PIFFPatch[0],
                    },
                },
            });

            target.Patch(patch);

            //the chunk that *was* understood is untouched
            Assert.Equal(Bytes(1, 2, 3, 4), target.Get<PNG>(512).data);
        }

        [Fact]
        public void APatchFileWithNoPiffChunkInItIsIgnoredRatherThanFatal()
        {
            // List<PIFF>()[0] with no guard, twice. A patch file with no PIFF in it - a truncated
            // download, or a file that was never a patch - was an ArgumentOutOfRangeException out
            // of content init.
            var target = new IffFile();
            target.AddChunk(new PNG
            {
                ChunkID = 512,
                ChunkProcessed = true,
                ChunkType = "PNG_",
                data = Bytes(1, 2, 3, 4),
            });

            target.Patch(new IffFile());

            Assert.Equal(Bytes(1, 2, 3, 4), target.Get<PNG>(512).data);
        }

        [Fact]
        public void AChunkWhoseOriginalDataIsUnreadableNowHashesRatherThanThrowing()
        {
            // InitHash chose the data with a fallback and its length without one, so a BHAV or
            // BCON still holding only OriginalData - the normal state for a content editor, which
            // sets RetainChunkData - threw a NullReferenceException from the middle of the hash.
            var iff = new IffFile();
            var chunk = new BHAV { ChunkID = 1, ChunkLabel = "script", ChunkProcessed = true };
            chunk.OriginalData = Bytes(1, 2, 3, 4, 5, 6, 7, 8);
            chunk.ChunkData = null;
            iff.AddChunk(chunk);

            iff.InitHash();

            Assert.NotEqual(0u, iff.ExecutableHash);
        }

        #endregion
    }
}
