/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Files.Formats.IFF.Chunks;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Guards the reader for Makin' Magic's own string file, <c>GameData/magic.iff</c>, by
    /// building files in exactly the shape it reads.
    ///
    /// <remarks>
    /// <para>
    /// The file is a plain TS1 IFF - big-endian chunk headers, <c>STR#</c> payloads in
    /// little-endian - whose six string chunks are identified by their 64-byte <em>label</em>
    /// rather than by a number, and each of which holds twenty language blocks back to back with
    /// English (US) first. Nothing in the content pipeline opens it, so
    /// <see cref="MakingMagicTables"/> is the only thing in the game that reads it, and these
    /// tests are what stands between it and a silent regression: a mis-written header or a
    /// mis-sized payload produces an empty book and no exception at all.
    /// </para>
    ///
    /// <para>
    /// Written by hand rather than by taking a <c>STR</c> and calling <c>IffFile.Write</c>,
    /// because the reader has to agree with the bytes Maxis shipped rather than with the
    /// engine's own writer. The tests never need a game install.
    /// </para>
    /// </remarks>
    [Collection(NonParallelCollection.Name)]
    public sealed class MakingMagicTablesTests : IDisposable
    {
        /// <summary>Language code 1 is English (US), the first block in every chunk.</summary>
        private const byte English = 1;

        /// <summary>Language code 3 is French.</summary>
        private const byte French = 3;

        private readonly string tempFile = Path.Combine(Path.GetTempPath(),
            "simitone-magic-" + Guid.NewGuid().ToString("N") + ".iff");

        public void Dispose()
        {
            try
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
            catch (IOException)
            {
                // a leftover temp file is not worth failing a test over
            }
        }

        #region building a file in the shape magic.iff has

        /// <summary>Writes a <c>STR#</c> chunk: header, label, then the string pairs.</summary>
        /// <remarks>
        /// The chunk header is big-endian - that is the one part of a TS1 IFF the file format
        /// disagrees with itself about, because the payload that follows is little-endian - so
        /// every header field is written a byte at a time. Writing them with a
        /// <see cref="BinaryWriter"/> would put a chunk size of 117 into the file as
        /// <c>75 00 00 00</c>, which the reader takes as 0x75000000 and then reads the rest of
        /// the file as that chunk's payload.
        /// </remarks>
        private static void WriteStringChunk(BinaryWriter writer, ushort id, string label,
            params (byte Language, string Value)[] entries)
        {
            var payload = new MemoryStream();
            using (var inner = new BinaryWriter(payload))
            {
                // format code -3: language code, null-terminated value, null-terminated comment
                inner.Write((short)-3);
                inner.Write((ushort)entries.Length);
                foreach (var entry in entries)
                {
                    inner.Write(entry.Language);
                    inner.Write(Encoding.ASCII.GetBytes(entry.Value));
                    inner.Write((byte)0);
                    inner.Write(Encoding.ASCII.GetBytes(""));
                    inner.Write((byte)0);
                }
            }

            var data = payload.ToArray();
            writer.Write(Encoding.ASCII.GetBytes("STR#"));
            WriteBe32(writer, (uint)(data.Length + 76)); // total size, including the 76-byte header
            WriteBe16(writer, id);                        // chunk id
            WriteBe16(writer, 0x10);                     // chunk flags
            var labelBytes = new byte[64];
            var name = Encoding.ASCII.GetBytes(label);
            Array.Copy(name, labelBytes, Math.Min(name.Length, 63));
            writer.Write(labelBytes);
            writer.Write(data);
        }

        private static void WriteBe32(BinaryWriter writer, uint value)
        {
            writer.Write((byte)(value >> 24));
            writer.Write((byte)(value >> 16));
            writer.Write((byte)(value >> 8));
            writer.Write((byte)value);
        }

        private static void WriteBe16(BinaryWriter writer, ushort value)
        {
            writer.Write((byte)(value >> 8));
            writer.Write((byte)value);
        }

        /// <summary>Writes a whole IFF around the given chunks.</summary>
        private static void WriteIff(string path, Action<BinaryWriter> chunks)
        {
            using (var stream = File.Create(path))
            using (var writer = new BinaryWriter(stream))
            {
                // 60 bytes, with the NUL the identifier carries after "SIZE"
                var header = Encoding.ASCII.GetBytes("IFF FILE 2.5:TYPE FOLLOWED BY SIZE\0 JAMIE DOORNBOS & MAXIS 1");
                Assert.Equal(60, header.Length);
                writer.Write(header);
                writer.Write((uint)0x00000040); // resource map offset, read and ignored
                chunks(writer);
            }
        }

        #endregion

        [Fact]
        public void AMissingFileIsAnEmptySetRatherThanAnError()
        {
            // a build with no Makin' Magic installed has no magic.iff at all, and a book dialog
            // that threw in that case would take the dialog down with it
            var tables = MakingMagicTables.TryLoad(Path.Combine(Path.GetTempPath(), "no-such-magic.iff"));
            Assert.NotNull(tables);
            Assert.False(tables.Loaded);
            Assert.Equal("next", MakingMagic.DialogStringFallback(MakingMagicCaption.Next));
            Assert.Equal(MakingMagic.DialogStringFallback(MakingMagicCaption.Next), tables.Dialog(MakingMagicCaption.Next));
        }

        [Fact]
        public void AnUnreadableFileIsAnEmptySetRatherThanAnError()
        {
            File.WriteAllText(tempFile, "this is not an IFF, it is a sentence");
            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.NotNull(tables);
            Assert.False(tables.Loaded);
        }

        [Fact]
        public void NoPathIsAnEmptySetRatherThanAnError()
        {
            Assert.False(MakingMagicTables.TryLoad(null).Loaded);
            Assert.False(MakingMagicTables.TryLoad("").Loaded);
        }

        [Fact]
        public void TheStringTablesAreFoundByTheirLabels()
        {
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 12, MakingMagic.SpellTitlesLabel,
                    (English, "Unknown Charm"), (English, "Dish Wish"), (English, "Banish"));
                WriteStringChunk(writer, 8, MakingMagic.SpellDescriptionLabel,
                    (English, "Wishes your dishes away."), (English, "Forces the target to hop."));
                WriteStringChunk(writer, 1, MakingMagic.DialogStringsLabel,
                    (English, "%s Family Spellbook"), (English, "%s Family Cookbook"),
                    (English, "back"), (English, "next"), (English, "Page %d of %d"));
            });

            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.True(tables.Loaded);

            var titles = tables.Titles(MakingMagicBook.Spellbook);
            Assert.Equal(new[] { "Unknown Charm", "Dish Wish", "Banish" }, titles);
            Assert.Empty(tables.Titles(MakingMagicBook.Cookbook));
        }

        [Fact]
        public void ADescriptionIsFoundByTheEntrysOwnIndex()
        {
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 12, MakingMagic.SpellTitlesLabel,
                    (English, "Unknown Charm"), (English, "Dish Wish"), (English, "Banish"));
                WriteStringChunk(writer, 8, MakingMagic.SpellDescriptionLabel,
                    (English, "placeholder"), (English, "Wishes your dishes away."),
                    (English, "Forces the target to hop."));
            });

            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.Equal("Wishes your dishes away.", tables.Description(MakingMagicBook.Spellbook, 1));
            Assert.Equal("Forces the target to hop.", tables.Description(MakingMagicBook.Spellbook, 2));

            // out of range rather than null: the spell description table is one entry longer than
            // the title table, and the caller asks with the index it already has
            Assert.Equal("", tables.Description(MakingMagicBook.Spellbook, 99));
            Assert.Equal("", tables.Description(MakingMagicBook.Spellbook, -1));
            Assert.Equal("", tables.Description(MakingMagicBook.None, 0));
        }

        [Fact]
        public void TheFiveDialogCaptionsComeOutInOrder()
        {
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 1, MakingMagic.DialogStringsLabel,
                    (English, "%s Family Spellbook"), (English, "%s Family Cookbook"),
                    (English, "zurueck"), (English, "weiter"), (English, "Seite %d von %d"));
            });

            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.Equal("%s Family Spellbook", tables.Dialog(MakingMagicCaption.SpellbookTitle));
            Assert.Equal("%s Family Cookbook", tables.Dialog(MakingMagicCaption.CookbookTitle));
            Assert.Equal("zurueck", tables.Dialog(MakingMagicCaption.Back));
            Assert.Equal("weiter", tables.Dialog(MakingMagicCaption.Next));
            Assert.Equal("Seite %d von %d", tables.Dialog(MakingMagicCaption.PageOf));
        }

        [Fact]
        public void APartialDialogTableFallsBackPerEntry()
        {
            // the fallback is per entry, not per file: three of the five captions can come from
            // the game and the other two from the English in the code
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 1, MakingMagic.DialogStringsLabel,
                    (English, "%s Family Spellbook"), (English, "%s Family Cookbook"));
            });

            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.Equal("%s Family Spellbook", tables.Dialog(MakingMagicCaption.SpellbookTitle));
            Assert.Equal("next", tables.Dialog(MakingMagicCaption.Next));
            Assert.Equal("Page %d of %d", tables.Dialog(MakingMagicCaption.PageOf));
        }

        [Fact]
        public void AnEmptyStringInTheFileFallsBackRatherThanShowingNothing()
        {
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 1, MakingMagic.DialogStringsLabel,
                    (English, ""), (English, ""), (English, ""), (English, ""), (English, "Page %d of %d"));
            });

            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.Equal(MakingMagic.DialogStringFallback(MakingMagicCaption.Back), tables.Dialog(MakingMagicCaption.Back));
            Assert.Equal("Page %d of %d", tables.Dialog(MakingMagicCaption.PageOf));
        }

        [Fact]
        public void AnEmptyChunkDoesNotDisplaceAPopulatedOneOfTheSameName()
        {
            // the real file ships two chunks called "Spell Description": an empty one and the
            // 520-string one. Indexing by label means the order decides which wins, so an empty
            // chunk has to be skipped rather than stored.
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 2, MakingMagic.SpellDescriptionLabel);
                WriteStringChunk(writer, 8, MakingMagic.SpellDescriptionLabel,
                    (English, "the real one"));
            });

            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.Equal("the real one", tables.Description(MakingMagicBook.Spellbook, 0));
        }

        [Fact]
        public void TheFirstPopulatedChunkOfANameWins()
        {
            // a file with two populated chunks under one name is a file we do not understand;
            // taking the first is the same choice the reader makes for an empty first chunk, and
            // it is deterministic either way
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 8, MakingMagic.SpellDescriptionLabel, (English, "first"));
                WriteStringChunk(writer, 9, MakingMagic.SpellDescriptionLabel, (English, "second"));
            });

            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.Equal("first", tables.Description(MakingMagicBook.Spellbook, 0));
        }

        [Fact]
        public void AnUnlabelledChunkIsIgnored()
        {
            // a label is the only handle the reader has, so a chunk without one cannot be found
            // and must not be stored under the empty string where it would shadow a real lookup
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 99, "", (English, "anonymous"));
                WriteStringChunk(writer, 12, MakingMagic.SpellTitlesLabel, (English, "Banish"));
            });

            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.Equal(new[] { "Banish" }, tables.Titles(MakingMagicBook.Spellbook));
            Assert.Empty(tables.Table(null));
            Assert.Empty(tables.Table(""));
        }

        [Fact]
        public void EveryLanguageBlockIsReadAndThePlayersLanguageIsUsed()
        {
            // the file stores twenty blocks with English first; STR resolves which one to hand
            // back through STR.DefaultLangCode, which the game sets from
            // GlobalSettings.LanguageCode. This asserts the blocks are all there and that
            // switching the default really does switch the language.
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 12, MakingMagic.SpellTitlesLabel,
                    (English, "Banish"), (French, "Bannir"), (English, "Enchant"));
            });

            var previous = STR.DefaultLangCode;
            try
            {
                STR.DefaultLangCode = STRLangCode.EnglishUS;
                var english = MakingMagicTables.TryLoad(tempFile);
                Assert.Equal(new[] { "Banish", "Enchant" }, english.Titles(MakingMagicBook.Spellbook));

                STR.DefaultLangCode = STRLangCode.French;
                var french = MakingMagicTables.TryLoad(tempFile);
                // a language block is allowed to be shorter than the English one, and the array
                // is sized from English, so the missing entry is a blank rather than a hole -
                // a caption is never handed a null
                Assert.Equal(new[] { "Bannir", "" }, french.Titles(MakingMagicBook.Spellbook));
            }
            finally
            {
                STR.DefaultLangCode = previous;
            }
        }

        [Fact]
        public void ALanguageWithNoBlockOfItsOwnFallsBackToEnglish()
        {
            // the same fallback STR.GetLanguageSet does, and the reason a book is never blank
            // just because the player picked the fifteenth language
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 12, MakingMagic.SpellTitlesLabel,
                    (English, "Banish"), (English, "Enchant"));
            });

            var previous = STR.DefaultLangCode;
            try
            {
                STR.DefaultLangCode = STRLangCode.German;
                var tables = MakingMagicTables.TryLoad(tempFile);
                Assert.Equal(new[] { "Banish", "Enchant" }, tables.Titles(MakingMagicBook.Spellbook));
            }
            finally
            {
                STR.DefaultLangCode = previous;
            }
        }

        [Fact]
        public void TheSharedInstanceIsNeverNull()
        {
            // read before the game has content initialised, which is what happens if a book
            // dialog is ever raised during startup
            Assert.NotNull(MakingMagicTables.Shared);
            Assert.NotNull(MakingMagicTables.Shared.Titles(MakingMagicBook.Spellbook));
            Assert.NotNull(MakingMagicTables.Shared.Dialog(MakingMagicCaption.Back));
        }

        [Fact]
        public void ACookbookUsesItsOwnTables()
        {
            WriteIff(tempFile, writer =>
            {
                WriteStringChunk(writer, 12, MakingMagic.SpellTitlesLabel, (English, "Banish"));
                WriteStringChunk(writer, 14, MakingMagic.RecipeTitlesLabel,
                    (English, "not used"), (English, "Magic Nectar"));
                WriteStringChunk(writer, 10, MakingMagic.RecipeDescriptionLabel,
                    (English, "unused"), (English, "Semi-secret, effervescent, zingy."));
            });

            var tables = MakingMagicTables.TryLoad(tempFile);
            Assert.Equal(new[] { "not used", "Magic Nectar" }, tables.Titles(MakingMagicBook.Cookbook));
            Assert.Equal("Semi-secret, effervescent, zingy.", tables.Description(MakingMagicBook.Cookbook, 1));
            Assert.Equal("Banish", tables.Titles(MakingMagicBook.Spellbook)[0]);
        }
    }
}
