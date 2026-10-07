using FSO.Content.TS1;
using FSO.Files;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Content
{
    /// <summary>
    /// The named-tuning table in TS1 mode: the population path added to
    /// <see cref="FSO.Files.Tuning"/> for a game with no <c>tuning.dat</c>, and the adapter that
    /// republishes TS1's positional <c>BCON</c> table through it.
    ///
    /// <para>
    /// <b>Why this needed a new path at all.</b> <c>FSO.Files.Tuning</c> had exactly two
    /// constructors, both of which parse the TSO <c>tuning.dat</c> and both of which throw if
    /// there is no file. TS1 has no <c>tuning.dat</c> - its tuning is a positional
    /// <see cref="FSO.Files.Formats.IFF.Chunks.BCON"/> array of unsigned shorts with no names -
    /// so there was no way to give <c>Content.GlobalTuning</c> a value in TS1 mode at all, and it
    /// stayed null. The alternative, leaving it null and null-checking at every call site, is
    /// exactly the "is the table there?" question the fallback is supposed to answer.
    /// </para>
    ///
    /// <para>
    /// <b>What the adapter is not allowed to do.</b> It republishes the raw table under a
    /// mechanical name. It does not invent TSO-style names (<c>lotmotives</c>, <c>simmotives</c>)
    /// for individual constants, because nothing in the install says which constant is which, and
    /// a name that looks authoritative but is a guess is worse than a miss. That is pinned below,
    /// because it is the one property a future change could quietly undo.
    /// </para>
    ///
    /// <para>
    /// Everything here is pure: a raw constant array in, a table out. No TS1 install, no
    /// <c>Global.far</c>, no content manager, no graphics device.
    /// </para>
    /// </summary>
    public sealed class TuningTableTests
    {
        /// <summary>
        /// A global tuning table with recognisable contents, so a test can tell "answered from
        /// data" from "answered from the default" without either number being special.
        /// </summary>
        private static ushort[] Table(int length)
        {
            var table = new ushort[length];
            for (int i = 0; i < length; i++) table[i] = (ushort)(1000 + i);
            return table;
        }

        #region the hand-built table

        [Fact]
        public void AHandBuiltTableStartsEmptyRatherThanThrowing()
        {
            var tuning = new Tuning();

            Assert.NotNull(tuning.EntriesByName);
            Assert.Empty(tuning.EntriesByName);
            Assert.Equal(0u, tuning.EntryCount);
        }

        /// <summary>
        /// The file parser stores entry names lowercased, and callers rely on that. A
        /// programmatically built table has to fold case the same way or the two would disagree
        /// about what an entry is called.
        /// </summary>
        [Fact]
        public void EntryNamesFoldCaseTheSameWayTheFileParserDoes()
        {
            var tuning = new Tuning();

            tuning.AddEntry("LotMotives");

            Assert.Equal(new[] { "lotmotives" }, tuning.EntriesByName.Keys.ToArray());
            Assert.True(tuning.TryGetEntry("LOTMOTIVES", out TuningEntry found));
            Assert.Equal("LotMotives", found.EntryName);
        }

        [Fact]
        public void AddingAnExistingNameReplacesTheEntryAndDoesNotInflateTheCount()
        {
            var tuning = new Tuning();

            tuning.AddEntry("a");
            tuning.AddEntry("b");
            tuning.AddEntry("A");

            Assert.Equal(2u, tuning.EntryCount);
            Assert.Equal(2, tuning.EntriesByName.Count);
        }

        [Fact]
        public void TryGetEntryReportsAMissInsteadOfThrowing()
        {
            var tuning = new Tuning();

            Assert.False(tuning.TryGetEntry("lotmotives", out TuningEntry entry));
            Assert.Null(entry.KeyValues);
            Assert.Null(entry.EntryName);
        }

        [Fact]
        public void SettingAValueKeepsTheDeclaredKeyCountHonest()
        {
            var tuning = new Tuning();

            tuning.SetValue("simmotives", "HungerDecrementRatio", "0.0021");
            tuning.SetValue("simmotives", "WakeHours", "16");
            tuning.SetValue("simmotives", "WakeHours", "20");

            Assert.True(tuning.TryGetEntry("simmotives", out TuningEntry entry));
            //three writes, two keys: the count describes the table, not the write log
            Assert.Equal(2u, entry.KeyValueCount);
            Assert.Equal(2, entry.KeyValues.Count);
            Assert.Equal("20", entry.KeyValues["WakeHours"]);
        }

        [Fact]
        public void SetNumWritesInvariantTextSoTheValueSurvivesAnyLocale()
        {
            var tuning = new Tuning();

            tuning.SetNum("t", "k", 0.5f);

            Assert.True(tuning.TryGetEntry("t", out TuningEntry entry));
            Assert.Equal(0.5f, entry.GetNum("k"));
        }

        #endregion

        #region positional tables

        /// <summary>
        /// A <c>BCON</c> chunk id is a <c>ushort</c>, so the name is four hex digits wide. The
        /// parameter is a <c>ushort</c> rather than an <c>int</c> on purpose: an <c>int</c> would
        /// let a caller with a wider number silently truncate its way to a different table's
        /// name. TS1's three scopes sit at <c>256</c>, <c>4096</c> and <c>8192</c>, all of which
        /// fit.
        /// </summary>
        [Theory]
        [InlineData((ushort)256, "bcon_0100")]
        [InlineData((ushort)0, "bcon_0000")]
        [InlineData((ushort)4096, "bcon_1000")]
        [InlineData((ushort)8192, "bcon_2000")]
        [InlineData((ushort)0x1234, "bcon_1234")]
        [InlineData((ushort)0xFFFF, "bcon_ffff")]
        public void ThePositionalEntryNameIsFourHexDigits(ushort tableId, string expected)
        {
            Assert.Equal(expected, Tuning.PositionalTableName(tableId));
        }

        [Fact]
        public void ThePositionalEntryNameTakesAChunkIdAndNothingWider()
        {
            //a wider parameter would silently truncate 0x1_0100 to 0x0100, which is a real
            //table's name
            var method = typeof(Tuning).GetMethod(nameof(Tuning.PositionalTableName),
                new[] { typeof(ushort) });

            Assert.NotNull(method);
            Assert.Equal(typeof(string), method.ReturnType);
            Assert.Equal(new[] { typeof(ushort) },
                typeof(Tuning).GetMethods()
                    .Where(m => m.Name == nameof(Tuning.PositionalTableName))
                    .SelectMany(m => m.GetParameters())
                    .Select(p => p.ParameterType)
                    .ToArray());
        }

        [Fact]
        public void APositionalTableIsPublishedUnderDecimalIndexKeys()
        {
            var tuning = new Tuning();

            int published = tuning.AddPositionalTable(Tuning.PositionalTableName(256), Table(4));

            Assert.Equal(4, published);
            Assert.True(tuning.TryGetEntry("bcon_0100", out TuningEntry entry));
            Assert.Equal(4u, entry.KeyValueCount);
            Assert.Equal(new[] { "0", "1", "2", "3" }, entry.KeyValues.Keys.OrderBy(k => k).ToArray());
            Assert.Equal(1002f, entry.GetNum("2"));
        }

        /// <summary>
        /// A <c>BCON</c> constant is a <c>ushort</c>, so the published text is unsigned: a constant
        /// of <c>0xFFFF</c> has to read back as <c>65535</c>, not as <c>-1</c>. The positional
        /// <i>accessor</i> narrows to <c>short</c>, which is what the engine's own
        /// <c>GameIffResource.TuningCache</c> and <c>TS1GlobalTuning.TryGetGlobalTuning</c> do, so
        /// a caller of either form sees the same number.
        /// </summary>
        [Fact]
        public void APositionalTablePreservesTheUnsignednessOfItsConstants()
        {
            var tuning = new Tuning();
            tuning.AddPositionalTable("bcon_0100", new ushort[] { 0xFFFF, 0x8000 });

            var entry = tuning.EntriesByName["bcon_0100"];
            Assert.Equal("65535", entry.KeyValues["0"]);
            Assert.Equal("32768", entry.KeyValues["1"]);
            Assert.Equal(65535f, entry.GetNum("0"));
            //and the short accessor agrees with the engine's own narrowing
            Assert.Equal(unchecked((short)0xFFFF), tuning.GetPositional("bcon_0100", 0));
            Assert.Equal(unchecked((short)0x8000), tuning.GetPositional("bcon_0100", 1));
        }

        [Fact]
        public void APositionalTableCanBePublishedAtAnOffset()
        {
            var tuning = new Tuning();

            tuning.AddPositionalTable("bcon_0104", Table(3), 5);

            Assert.Equal(3u, tuning.TryGetEntry("bcon_0104", out TuningEntry entry) ? entry.KeyValueCount : 0);
            Assert.Equal(1000f, tuning.EntriesByName["bcon_0104"].GetNum("5"));
            Assert.Equal(1002f, tuning.EntriesByName["bcon_0104"].GetNum("7"));
        }

        [Fact]
        public void ANullConstantArrayPublishesNothingRatherThanThrowing()
        {
            var tuning = new Tuning();

            Assert.Equal(0, tuning.AddPositionalTable("bcon_0100", null));
            Assert.Empty(tuning.EntriesByName["bcon_0100"].KeyValues);
        }

        [Fact]
        public void APastTheEndPositionalLookupIsAMissNotAZero()
        {
            var tuning = new Tuning();
            tuning.AddPositionalTable("bcon_0100", Table(4));

            Assert.False(tuning.TryGetPositional("bcon_0100", 9, out short value));
            Assert.Equal(0, value);
            //and the convenience form answers the same thing, as VMMemory.GetTuningVariable does
            Assert.Equal(0, tuning.GetPositional("bcon_0100", 9));
        }

        [Fact]
        public void APositionalLookupInAMissingEntryIsAMiss()
        {
            var tuning = new Tuning();

            Assert.False(tuning.TryGetPositional("bcon_0200", 0, out _));
        }

        #endregion

        #region TryGetNum agrees with GetNum

        [Theory]
        [InlineData("0")]
        [InlineData("1.5")]
        [InlineData("-2.25")]
        [InlineData("1,000.5")]
        [InlineData("1e3")]
        public void TheSafeAccessorAcceptsExactlyWhatTheThrowingOneDoes(string raw)
        {
            var entry = new TuningEntry { KeyValues = new Dictionary<string, string> { ["k"] = raw } };

            Assert.True(entry.TryGetNum("k", out float safe));
            Assert.Equal(float.Parse(raw, System.Globalization.CultureInfo.InvariantCulture), safe);
        }

        [Fact]
        public void AMissingKeyIsAMissRatherThanAKeyNotFoundException()
        {
            var entry = new TuningEntry { KeyValues = new Dictionary<string, string>() };

            Assert.False(entry.TryGetNum("nope", out float value));
            Assert.Equal(0f, value);
            Assert.Throws<KeyNotFoundException>(() => entry.GetNum("nope"));
        }

        [Fact]
        public void AnUnparseableValueIsAMissRatherThanAFormatException()
        {
            var entry = new TuningEntry { KeyValues = new Dictionary<string, string> { ["k"] = "not a number" } };

            Assert.False(entry.TryGetNum("k", out _));
            Assert.Throws<FormatException>(() => entry.GetNum("k"));
        }

        [Fact]
        public void AnEntryWithNoDictionaryAtAllIsAMissRatherThanANullReference()
        {
            //TuningEntry.DEFAULT and a default(TuningEntry) both look like this
            var entry = new TuningEntry();

            Assert.False(entry.TryGetNum("k", out _));
            Assert.False(entry.TryGetNum(0, out _));
        }

        [Fact]
        public void TheIndexOverloadIsTheSameLookupByDecimalKey()
        {
            var entry = new TuningEntry { KeyValues = new Dictionary<string, string> { ["12"] = "3.5" } };

            Assert.True(entry.TryGetNum(12, out float byIndex));
            Assert.True(entry.TryGetNum("12", out float byName));
            Assert.Equal(byName, byIndex);
        }

        #endregion

        #region the TS1 adapter

        [Fact]
        public void TheAdapterNameIsTheNameTheProducerSpells()
        {
            //the two spellings live in two assemblies; if either changes alone the producer and
            //the reader silently stop finding each other
            Assert.Equal(Tuning.PositionalTableName(TS1GlobalTuning.GlobalTuningTableId),
                TS1GlobalTuning.GlobalTuningEntryName);
            Assert.Equal("bcon_0100", TS1GlobalTuning.GlobalTuningEntryName);
        }

        /// <summary>
        /// The whole point of the change: a TS1 lot with no readable data still has a table, and
        /// asking it a question is a miss rather than a null dereference.
        /// </summary>
        [Fact]
        public void WithNoDataAtAllTheAdapterStillYieldsAQueryableEmptyTable()
        {
            var tuning = new TS1GlobalTuning().ToNamedTuning();

            Assert.NotNull(tuning);
            Assert.Empty(tuning.EntriesByName);
            Assert.False(tuning.TryGetEntry(TS1GlobalTuning.GlobalTuningEntryName, out _));
            Assert.False(TS1GlobalTuning.TryGetNamedTuning(tuning, 16385, out _));
            Assert.Equal(0, TS1GlobalTuning.TryGetNamedTuning(tuning, 16385, out short v) ? 1 : 0);
        }

        [Fact]
        public void ALoadedGlobalTableIsPublishedUnderItsOwnTableId()
        {
            var source = new TS1GlobalTuning(Table(200));

            var tuning = source.ToNamedTuning();

            Assert.Equal(1u, tuning.EntryCount);
            Assert.True(tuning.TryGetEntry("bcon_0100", out TuningEntry entry));
            Assert.Equal(200u, entry.KeyValueCount);
            Assert.Equal(1000f, entry.GetNum("0"));
            Assert.Equal(1199f, entry.GetNum("199"));
        }

        /// <summary>
        /// The loaded table and the republished one must agree, index for index, or the named form
        /// is a different table rather than the same table spelled another way.
        /// </summary>
        [Theory]
        [InlineData(16384)]
        [InlineData(16385)]
        [InlineData(16511)]
        [InlineData(16897)]   //0x104[1] - a table this instance did not load
        [InlineData(24575)]
        [InlineData(16383)]   //one below the global scope
        [InlineData(24576)]   //one above it
        public void TheRepublishedTableAnswersTheSameIndicesAsTheOriginal(int tuningIndex)
        {
            var source = new TS1GlobalTuning(Table(200));
            var tuning = source.ToNamedTuning();

            bool direct = source.TryGetGlobalTuning((ushort)tuningIndex, out short fromSource);
            bool republished = TS1GlobalTuning.TryGetNamedTuning(tuning, (ushort)tuningIndex, out short fromTable);

            Assert.Equal(direct, republished);
            Assert.Equal(fromSource, fromTable);
        }

        [Fact]
        public void TheAdapterMergesIntoAnExistingTableRatherThanReplacingIt()
        {
            var target = new Tuning();
            target.SetValue("lotmotives", "None_HungerWeight", "1.0");

            var result = new TS1GlobalTuning(Table(8)).ToNamedTuning(target);

            Assert.Same(target, result);
            Assert.True(result.TryGetEntry("lotmotives", out _));
            Assert.True(result.TryGetEntry("bcon_0100", out _));
        }

        /// <summary>
        /// The honesty pin. If a named TSO-style entry ever appears here, somebody has guessed at
        /// which <c>BCON</c> constant is which, and every consumer of that name would start
        /// believing the guess.
        /// </summary>
        [Fact]
        public void NoTSOStyleEntryNameIsInventedForThePositionalTable()
        {
            var tuning = new TS1GlobalTuning(Table(200)).ToNamedTuning();

            Assert.False(tuning.TryGetEntry("lotmotives", out _));
            Assert.False(tuning.TryGetEntry("simmotives", out _));
            Assert.False(tuning.TryGetEntry("harvest component table", out _));
            Assert.Equal(new[] { "bcon_0100" }, tuning.EntriesByName.Keys.ToArray());
        }

        [Fact]
        public void ThePublishedTableAnswersAnIndexDirectlyToo()
        {
            var tuning = new TS1GlobalTuning(Table(200)).ToNamedTuning();

            //16385 is 0x100[1], so key 1 of the published table
            Assert.True(tuning.TryGetPositional("bcon_0100", 1, out short value));
            Assert.Equal(1001, value);
        }

        [Fact]
        public void ANullTableIsAMissRatherThanANullReference()
        {
            Assert.False(TS1GlobalTuning.TryGetNamedTuning(null, 16385, out _));
        }

        [Fact]
        public void TheLoadedConstantCountIsZeroWithoutData()
        {
            Assert.Equal(0, new TS1GlobalTuning().LoadedConstantCount);
            Assert.Equal(8, new TS1GlobalTuning(Table(8)).LoadedConstantCount);
        }

        #endregion
    }
}
