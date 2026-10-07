using FSO.Content.TS1;
using FSO.SimAntics.Entities;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Content
{
    /// <summary>
    /// The TS1 global tuning load and the load/fallback decision in
    /// <see cref="TS1GlobalTuning"/>.
    ///
    /// <para>
    /// <b>Why there is a decision to test at all.</b> The audit that reported the hard-coded
    /// motive decay observed that <c>Content.GlobalTuning</c> is only ever assigned in the
    /// non-TS1 branch of the <c>Content</c> constructor, and inferred from the TSO code shape
    /// that TS1 must have had a tuning file to load. Following that up: the chunk the engine
    /// actually reads for tuning is <c>BCON</c> (there is no <c>TUNI</c>/<c>TINI</c> chunk in any
    /// IFF table in this tree, and none in a retail install), the TS1 global scope of it is
    /// <c>GameData/Global/Global.far</c> &rarr; <c>Global.iff</c>, and that file is already opened
    /// by <c>WorldGlobalProvider</c> for the interaction-score curves. So the table is loaded
    /// now, from the place the engine itself reads.
    /// </para>
    ///
    /// <para>
    /// The audit also recorded - marked as its own unverified gap - that the retail
    /// <i>motive-decay</i> constants are not in the install's data files at all ("there is no
    /// readable one in the install"; <c>Sims.exe</c> is packed). That is consistent with what is
    /// on disk: the decay constants are fractions such as <c>0.0021</c> and cannot be
    /// <c>ushort</c> <c>BCON</c> entries. So those named constants keep falling back to the
    /// values the engine has always used, and the fallback is pinned here against
    /// <c>VMTS1MotiveDecay.Constants</c> - the array that is the whole reason the constants
    /// cannot drift away from today's behaviour.
    /// </para>
    ///
    /// <para>
    /// Everything here is pure: the tests build the tuning instance over a raw constant array,
    /// so no TS1 install, <c>Global.far</c>, content manager or graphics device is involved.
    /// </para>
    /// </summary>
    public sealed class TS1GlobalTuningTests
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

        private static IDictionary<TS1MotiveDecayKey, TS1MotiveDecaySource> Sources(
            params (TS1MotiveDecayKey key, ushort index, float scale)[] entries)
        {
            var map = new Dictionary<TS1MotiveDecayKey, TS1MotiveDecaySource>();
            foreach (var e in entries)
                map[e.key] = new TS1MotiveDecaySource(e.index, e.scale);
            return map;
        }

        #region the load/fallback decision

        [Fact]
        public void NothingLoadedIsAFallback()
        {
            var tuning = new TS1GlobalTuning();

            Assert.Equal(TS1TuningSource.FallbackDefaults, tuning.Source);
            Assert.False(tuning.UsesRetailTable);
        }

        [Fact]
        public void ALoadedTableIsData()
        {
            var tuning = new TS1GlobalTuning(Table(128));

            Assert.Equal(TS1TuningSource.GlobalIffBcon, tuning.Source);
            Assert.True(tuning.UsesRetailTable);
        }

        [Fact]
        public void AReadableGlobalIffWithNoTableInItIsStillAFallback()
        {
            //the table is the data, not the file
            var tuning = new TS1GlobalTuning(new ushort[0], null, true);

            Assert.Equal(TS1TuningSource.FallbackDefaults, tuning.Source);
        }

        [Theory]
        [InlineData(false, false, TS1TuningSource.FallbackDefaults)]
        [InlineData(false, true, TS1TuningSource.FallbackDefaults)]
        [InlineData(true, false, TS1TuningSource.FallbackDefaults)]
        [InlineData(true, true, TS1TuningSource.GlobalIffBcon)]
        public void BothFactsAreRequiredToClaimTheDataPath(bool globalIffLoaded, bool tableLoaded, TS1TuningSource expected)
        {
            Assert.Equal(expected, TS1GlobalTuning.DecideSource(globalIffLoaded, tableLoaded));
        }

        #endregion

        #region Tuning[] index decode

        /// <summary>
        /// Pinned against the two independent facts the shipped trees and the audit's own
        /// <c>bcon</c> command lines give for the global scope:
        /// <c>Tuning[16385]</c> is <c>Global.iff BCON 0x100[1]</c> and
        /// <c>Tuning[16897]</c>/<c>[16898]</c> are <c>0x104[1]</c>/<c>[2]</c>. A decode that
        /// disagreed with those would mis-route every <c>Tuning[]</c> read in TS1.
        /// </summary>
        [Theory]
        [InlineData(16384, 256, 0)]
        [InlineData(16385, 256, 1)]
        [InlineData(16511, 256, 127)]
        [InlineData(16512, 257, 0)]
        [InlineData(16896, 260, 0)]
        [InlineData(16897, 260, 1)]
        [InlineData(16898, 260, 2)]
        [InlineData(24575, 319, 127)]
        public void GlobalScopeIndicesDecodeToTheTablesTheEngineReads(int tuningIndex, int tableID, int keyID)
        {
            Assert.True(TS1GlobalTuning.TryDecodeGlobalTuningIndex((ushort)tuningIndex, out ushort table, out ushort key));
            Assert.Equal(tableID, table);
            Assert.Equal(keyID, key);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(16383)]    //last index of the semi-global scope
        [InlineData(24576)]    //one past the end of the global scope
        [InlineData(32767)]
        public void IndicesOutsideTheGlobalScopeAreNotGlobalScope(int tuningIndex)
        {
            Assert.False(TS1GlobalTuning.TryDecodeGlobalTuningIndex((ushort)tuningIndex, out _, out _));
        }

        [Fact]
        public void TheGlobalScopeBoundariesAreTheOnesTheEngineUses()
        {
            Assert.Equal(256, TS1GlobalTuning.GlobalTuningTableId);
            Assert.Equal(128 << 7, TS1GlobalTuning.GlobalScopeTuningBase);
            Assert.Equal(192 << 7, TS1GlobalTuning.GlobalScopeTuningEnd);
        }

        #endregion

        #region reading the table

        [Fact]
        public void ALoadedTableAnswersItsOwnGlobalScopeIndices()
        {
            var table = Table(200);
            var tuning = new TS1GlobalTuning(table);

            Assert.True(tuning.TryGetGlobalTuning(16385, out short value));
            Assert.Equal((short)table[1], value);
        }

        [Fact]
        public void AnIndexOnADifferentTableIsAMissNotAZero()
        {
            //16897 is 0x104[1]; only 0x100 is loaded, and reporting 0 for a table we never read
            //would be a claim, not an absence
            var tuning = new TS1GlobalTuning(Table(200));

            Assert.False(tuning.TryGetGlobalTuning(16897, out short value));
            Assert.Equal(0, value);
        }

        [Fact]
        public void AKeyPastTheEndOfTheTableIsAMiss()
        {
            var tuning = new TS1GlobalTuning(Table(4));

            Assert.False(tuning.TryGetGlobalTuning(16390, out _));
        }

        [Fact]
        public void WithNoTableEveryLookupMissesAndReadsAsZero()
        {
            //the answer VMMemory.GetTuningVariable gives for an unknown constant
            var tuning = new TS1GlobalTuning();

            Assert.False(tuning.TryGetGlobalTuning(16385, out _));
            Assert.Equal(0, tuning.GetGlobalTuning(16385));
        }

        [Fact]
        public void TheConvenienceAccessorAgreesWithTheTryForm()
        {
            var tuning = new TS1GlobalTuning(Table(200));

            Assert.True(tuning.TryGetGlobalTuning(16385, out short a));
            Assert.Equal(a, tuning.GetGlobalTuning(16385));
            Assert.Equal(0, tuning.GetGlobalTuning(16897));
        }

        #endregion

        #region the named motive-decay constants

        [Fact]
        public void TheEnumIsContiguousSoItCanIndexTheConstantTable()
        {
            //the array in the class and the array in VMTS1MotiveDecay are both indexed by this
            //enum, so a gap or a renumbering would silently compare the wrong constants
            var values = (TS1MotiveDecayKey[])Enum.GetValues(typeof(TS1MotiveDecayKey));

            Assert.Equal(17, values.Length);
            for (int i = 0; i < values.Length; i++)
                Assert.Equal(i, (int)values[i]);
        }

        /// <summary>
        /// The documented fallback has to <i>be</i> today's constants, not an approximation of
        /// them. This is the test that makes "falls back to today's constants" a checkable
        /// claim rather than a comment.
        /// </summary>
        [Fact]
        public void TheFallbackTableIsExactlyTheConstantsTheEngineUsesToday()
        {
            var defaults = TS1GlobalTuning.RetailMotiveDecayDefaults;
            var current = VMTS1MotiveDecay.Constants;

            Assert.Equal(current.Length, defaults.Length);
            for (int i = 0; i < current.Length; i++)
                Assert.Equal(current[i], defaults[i]);
        }

        [Fact]
        public void WithNoRegisteredSourcesEveryNamedConstantFallsBackToTheRetailValue()
        {
            var tuning = new TS1GlobalTuning();

            foreach (TS1MotiveDecayKey key in Enum.GetValues(typeof(TS1MotiveDecayKey)))
            {
                Assert.False(tuning.TryGetMotiveDecay(key, out _));
                Assert.Equal(TS1GlobalTuning.RetailMotiveDecayDefaults[(int)key], tuning.GetMotiveDecay(key));
            }
        }

        [Fact]
        public void ARegisteredIndexAnswersTheNamedConstantFromTheData()
        {
            //table[1] == 1001, scale 1/1000 => 1.001, which is nothing like the retail default
            var tuning = new TS1GlobalTuning(Table(200),
                Sources((TS1MotiveDecayKey.HungerDecrementRatio, 16385, 0.001f)), true);

            Assert.True(tuning.TryGetMotiveDecay(TS1MotiveDecayKey.HungerDecrementRatio, out float value));
            Assert.Equal(1001 * 0.001f, value, 6);
            Assert.Equal(1001 * 0.001f, tuning.GetMotiveDecay(TS1MotiveDecayKey.HungerDecrementRatio), 6);
        }

        [Fact]
        public void TheScaleIsPartOfTheAnswerSoAnIndexCannotSilentlyMeanTheWrongUnits()
        {
            var half = new TS1GlobalTuning(Table(200),
                Sources((TS1MotiveDecayKey.HungerDecrementRatio, 16385, 0.0005f)), true);
            var whole = new TS1GlobalTuning(Table(200),
                Sources((TS1MotiveDecayKey.HungerDecrementRatio, 16385, 0.001f)), true);

            Assert.Equal(2 * half.GetMotiveDecay(TS1MotiveDecayKey.HungerDecrementRatio),
                whole.GetMotiveDecay(TS1MotiveDecayKey.HungerDecrementRatio), 6);
        }

        /// <summary>
        /// A registered index whose key is missing from the table must fall back, not read as
        /// zero: a zero decay rate reads as "this sim's motives never move", not as "we could
        /// not find that constant".
        /// </summary>
        [Fact]
        public void ARegisteredIndexWithNoKeyInTheTableFallsBackRatherThanReadingZero()
        {
            //table holds keys 0..3; the registered index asks for key 10
            var tuning = new TS1GlobalTuning(Table(4),
                Sources((TS1MotiveDecayKey.HungerDecrementRatio, 16394, 0.001f)), true);

            Assert.False(tuning.TryGetMotiveDecay(TS1MotiveDecayKey.HungerDecrementRatio, out _));
            Assert.Equal(TS1GlobalTuning.RetailMotiveDecayDefaults[(int)TS1MotiveDecayKey.HungerDecrementRatio],
                tuning.GetMotiveDecay(TS1MotiveDecayKey.HungerDecrementRatio));
        }

        /// <summary>
        /// The converse: a key the table <i>does</i> hold, however small the table, is data and
        /// not a fallback. Guards the previous test being "passed" by simply never answering.
        /// </summary>
        [Fact]
        public void ARegisteredIndexTheTableDoesHoldIsDataEvenInASmallTable()
        {
            var tuning = new TS1GlobalTuning(Table(4),
                Sources((TS1MotiveDecayKey.HungerDecrementRatio, 16385, 0.001f)), true);

            Assert.True(tuning.TryGetMotiveDecay(TS1MotiveDecayKey.HungerDecrementRatio, out float value));
            Assert.Equal(1001 * 0.001f, value, 6);
        }

        [Fact]
        public void ARegisteredIndexIsIgnoredEntirelyWhenNoTableIsLoaded()
        {
            var tuning = new TS1GlobalTuning(null,
                Sources((TS1MotiveDecayKey.HungerDecrementRatio, 16385, 0.001f)), false);

            Assert.False(tuning.TryGetMotiveDecay(TS1MotiveDecayKey.HungerDecrementRatio, out _));
            Assert.Equal(TS1GlobalTuning.RetailMotiveDecayDefaults[(int)TS1MotiveDecayKey.HungerDecrementRatio],
                tuning.GetMotiveDecay(TS1MotiveDecayKey.HungerDecrementRatio));
        }

        [Fact]
        public void OnlyTheRegisteredConstantsComeFromTheData()
        {
            var tuning = new TS1GlobalTuning(Table(200),
                Sources((TS1MotiveDecayKey.HungerDecrementRatio, 16385, 0.001f)), true);

            var answered = Enum.GetValues(typeof(TS1MotiveDecayKey))
                .Cast<TS1MotiveDecayKey>()
                .Where(key => tuning.TryGetMotiveDecay(key, out _))
                .ToList();

            Assert.Equal(new[] { TS1MotiveDecayKey.HungerDecrementRatio }, answered);
        }

        /// <summary>
        /// The default registry is empty, and that is the honest state of the evidence rather
        /// than an oversight: the retail decay constants are not present in any install data
        /// file. Pinned so that populating it is a deliberate, visible change.
        /// </summary>
        [Fact]
        public void NoConstantIsWiredToATuningIndexByDefault()
        {
            Assert.Empty(TS1GlobalTuning.MotiveDecaySources);
        }

        #endregion
    }
}
