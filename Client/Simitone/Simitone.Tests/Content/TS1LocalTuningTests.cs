using FSO.Content.TS1;
using FSO.Files;
using System;
using System.IO;
using System.Linq;
using Xunit;

namespace Simitone.Tests.Content
{
    /// <summary>
    /// <c>Content/tuning.json</c>: the local, hand-editable tuning file a single-player TS1 lot
    /// can use in place of a shard, and the two different things it can feed.
    ///
    /// <para>
    /// <b>Why there are two sections.</b> FreeSO's tuning reaches the simulation through two
    /// unrelated types. <see cref="FSO.Files.Tuning"/> is the <c>tuning.dat</c> shape - named
    /// entries of string key/value pairs, read once at content load, exposed as
    /// <c>Content.GlobalTuning</c>. <c>FSO.Common.Model.DynamicTuning</c> is a
    /// type/table/index float table pushed into a running lot at join time, exposed as
    /// <c>vm.Tuning</c>. TSO gets both from a server. A single-player lot has no server, so
    /// both have to come off the disk, and the whole modding surface is one small file.
    /// </para>
    ///
    /// <para>
    /// <b>Failure has to be a value.</b> A typo in a modding file must not stop a lot from
    /// opening, and an unreadable file must not be indistinguishable from an empty one - those
    /// are different states and they are tested apart. Nothing here needs a TS1 install, a
    /// content manager or a graphics device: the parser is pure, and the file lookup is exercised
    /// against a temporary directory.
    /// </para>
    /// </summary>
    public sealed class TS1LocalTuningTests : IDisposable
    {
        private readonly string root;

        public TS1LocalTuningTests()
        {
            root = Path.Combine(Path.GetTempPath(), "simitone-tuning-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            catch (IOException)
            {
                //a leftover temp directory is not a test failure
            }
        }

        private void WriteFile(string relative, string contents)
        {
            var path = Path.Combine(root, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, contents);
        }

        #region absence

        [Fact]
        public void NoFileIsNotAProblemItIsJustNoFile()
        {
            var local = TS1LocalTuning.LoadFromBasePath(root);

            Assert.False(local.Loaded);
            Assert.Null(local.Entries);
            Assert.Null(local.Dynamic);
            Assert.Empty(local.Problems);
        }

        [Fact]
        public void AnAbsentContentManagerIsAlsoSimplyAbsent()
        {
            var local = TS1LocalTuning.LoadFromBasePath(null);

            Assert.False(local.Loaded);
            Assert.Null(local.Dynamic);
        }

        [Fact]
        public void AMissingFileIsNotThrownAt()
        {
            var local = TS1LocalTuning.Load(Path.Combine(root, "nope.json"));

            Assert.False(local.Loaded);
            Assert.NotEmpty(local.Problems);
        }

        #endregion

        #region the entries section

        [Fact]
        public void NamedEntriesBecomeTuningEntries()
        {
            var local = TS1LocalTuning.Parse(
                "{\"entries\":{\"lotmotives\":{\"None_HungerWeight\":\"1.0\",\"None_FunWeight\":\"2.5\"}}}");

            Assert.True(local.Loaded);
            Assert.Null(local.Dynamic);
            Assert.Empty(local.Problems);

            Assert.True(local.Entries.TryGetEntry("lotmotives", out var entry));
            Assert.Equal(2u, entry.KeyValueCount);
            Assert.Equal(1.0f, entry.GetNum("None_HungerWeight"));
            Assert.Equal(2.5f, entry.GetNum("None_FunWeight"));
        }

        [Fact]
        public void EntryNamesFoldCaseSoAFileCanSpellThemAnyWay()
        {
            var local = TS1LocalTuning.Parse("{\"entries\":{\"LotMotives\":{\"a\":\"1\"}}}");

            Assert.True(local.Entries.TryGetEntry("lotmotives", out _));
        }

        [Fact]
        public void NumbersInAnEntryKeepTheirInvariantText()
        {
            var local = TS1LocalTuning.Parse("{\"entries\":{\"t\":{\"a\":2,\"b\":2.50,\"c\":true,\"d\":null}}}");

            Assert.True(local.Entries.TryGetEntry("t", out var entry));
            Assert.Equal("2", entry.KeyValues["a"]);
            Assert.Equal("2.5", entry.KeyValues["b"]);
            Assert.Equal("true", entry.KeyValues["c"]);
            Assert.Null(entry.KeyValues["d"]);
        }

        /// <summary>
        /// The invariant-culture contract, tested under a culture whose decimal separator is a
        /// comma. A tuning file that reads as <c>"0,5"</c> on one machine and <c>"0.5"</c> on
        /// another would produce different balance depending on the player's locale, and the
        /// numbers would not even be readable as numbers by the thing that consumes them.
        /// </summary>
        [Fact]
        public void ACommaDecimalSeparatorLocaleDoesNotCorruptTheNumbers()
        {
            var original = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture =
                    new System.Globalization.CultureInfo("de-DE");

                var local = TS1LocalTuning.Parse(
                    "{\"entries\":{\"t\":{\"a\":2.50}},\"dynamic\":{\"d\":{\"0\":{\"1\":0.5}}}}");

                Assert.True(local.Entries.TryGetEntry("t", out var entry));
                Assert.Equal("2.5", entry.KeyValues["a"]);
                Assert.Equal(2.5f, entry.GetNum("a"));
                Assert.Equal(0.5f, local.Dynamic.GetTuning("d", 0, 1));
            }
            finally
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = original;
            }
        }

        [Fact]
        public void AWrongShapedSectionIsReportedAndSkipped()
        {
            var local = TS1LocalTuning.Parse("{\"entries\":5}");

            Assert.False(local.Loaded);
            Assert.Null(local.Entries);
            Assert.Contains(local.Problems, p => p.Contains("\"entries\" is not an object"));
        }

        [Fact]
        public void AWrongShapedEntryIsReportedAndTheRestStillLoads()
        {
            var local = TS1LocalTuning.Parse("{\"entries\":{\"good\":{\"a\":\"1\"},\"bad\":7}}");

            Assert.True(local.Loaded);
            Assert.True(local.Entries.TryGetEntry("good", out _));
            Assert.False(local.Entries.TryGetEntry("bad", out _));
            Assert.Contains(local.Problems, p => p.Contains("entry \"bad\""));
        }

        #endregion

        #region the dynamic section

        /// <summary>
        /// Pinned against the exact keys the simulation reads, from
        /// <c>VMContext.InitSpecialTuning</c> and <c>VMRoutingFrame</c>. A file that answered
        /// these correctly is the whole point of the section.
        /// </summary>
        [Theory]
        [InlineData("special", 0, 1)]
        [InlineData("special", 0, 2)]
        [InlineData("special", 0, 3)]
        [InlineData("aprilfools", 0, 2019)]
        [InlineData("platformer", 0, 0)]
        [InlineData("feature", 0, 1)]
        public void TheDynamicSectionAnswersTheKeysTheSimulationReads(string type, int table, int index)
        {
            var local = TS1LocalTuning.Parse(
                "{\"dynamic\":{\"" + type + "\":{\"" + table + "\":{\"" + index + "\":0.75}}}}");

            Assert.True(local.Loaded);
            Assert.Equal(0.75f, local.Dynamic.GetTuning(type, table, index));
        }

        [Fact]
        public void AnAbsentKeyIsStillAbsentRatherThanZero()
        {
            //the null is load-bearing: every consumer is `GetTuning(..) ?? default`, so a zero
            //written into the file would switch a feature off rather than leave it unset
            var local = TS1LocalTuning.Parse("{\"dynamic\":{\"special\":{\"0\":{\"1\":0}}}}");

            Assert.Equal(0f, local.Dynamic.GetTuning("special", 0, 1));
            Assert.Null(local.Dynamic.GetTuning("special", 0, 2));
            Assert.Null(local.Dynamic.GetTuning("feature", 0, 1));
        }

        [Fact]
        public void AWholeTableIsQueryableAtOnce()
        {
            var local = TS1LocalTuning.Parse("{\"dynamic\":{\"overfill\":{\"0\":{\"4\":150,\"6\":120}}}}");

            var table = local.Dynamic.GetTable("overfill", 0);

            Assert.NotNull(table);
            Assert.Equal(2, table.Count);
            Assert.Equal(150f, table[(int)4]);
        }

        [Fact]
        public void ValuesMayBeWrittenAsNumbersOrAsStrings()
        {
            var local = TS1LocalTuning.Parse("{\"dynamic\":{\"t\":{\"0\":{\"1\":0.5,\"2\":\"0.25\"}}}}");

            Assert.Equal(0.5f, local.Dynamic.GetTuning("t", 0, 1));
            Assert.Equal(0.25f, local.Dynamic.GetTuning("t", 0, 2));
        }

        [Fact]
        public void NonIntegerTableAndIndexNamesAreReportedAndSkipped()
        {
            var local = TS1LocalTuning.Parse(
                "{\"dynamic\":{\"t\":{\"x\":{\"1\":1.0},\"0\":{\"y\":1.0,\"2\":2.0}}}}");

            Assert.True(local.Loaded);
            Assert.Equal(2f, local.Dynamic.GetTuning("t", 0, 2));
            Assert.Contains(local.Problems, p => p.Contains("t/x"));
            Assert.Contains(local.Problems, p => p.Contains("t/0/y"));
        }

        [Fact]
        public void NonNumericValuesAreReportedAndSkipped()
        {
            var local = TS1LocalTuning.Parse("{\"dynamic\":{\"t\":{\"0\":{\"1\":\"lots\",\"2\":2.0}}}}");

            Assert.Equal(2f, local.Dynamic.GetTuning("t", 0, 2));
            Assert.Contains(local.Problems, p => p.Contains("t/0/1"));
        }

        [Fact]
        public void AWrongShapedDynamicSectionIsReportedAndSkipped()
        {
            var local = TS1LocalTuning.Parse("{\"dynamic\":[1,2,3]}");

            Assert.False(local.Loaded);
            Assert.Null(local.Dynamic);
            Assert.Contains(local.Problems, p => p.Contains("\"dynamic\" is not an object"));
        }

        [Fact]
        public void AnEmptyDynamicSectionIsStillADynamicTable()
        {
            //present but empty is different from absent: it says "the file was read and it has
            //nothing to say", and a caller pushing it would get today's behaviour exactly
            var local = TS1LocalTuning.Parse("{\"dynamic\":{}}");

            Assert.True(local.Loaded);
            Assert.NotNull(local.Dynamic);
            Assert.Empty(local.Dynamic.Tuning);
        }

        #endregion

        #region malformed input

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{")]
        [InlineData("{\"entries\":{\"a\":\"1\"}")]     //missing brace
        [InlineData("not json at all")]
        public void UnusableTextIsReportedAndNeverThrownAt(string json)
        {
            var local = TS1LocalTuning.Parse(json);

            Assert.False(local.Loaded);
            Assert.Null(local.Entries);
            Assert.Null(local.Dynamic);
            Assert.NotEmpty(local.Problems);
        }

        [Fact]
        public void AFileWithNeitherSectionSaysSo()
        {
            var local = TS1LocalTuning.Parse("{\"something\":1}");

            Assert.False(local.Loaded);
            Assert.Contains(local.Problems, p => p.Contains("neither an \"entries\" nor a \"dynamic\""));
        }

        [Fact]
        public void AValidSectionSurvivesAMalformedNeighbour()
        {
            var local = TS1LocalTuning.Parse("{\"entries\":{\"t\":{\"a\":\"1\"}},\"dynamic\":\"nope\"}");

            Assert.True(local.Loaded);
            Assert.True(local.Entries.TryGetEntry("t", out _));
            Assert.Null(local.Dynamic);
        }

        #endregion

        #region where the file is looked for

        [Fact]
        public void TheNestedContentLocationIsFound()
        {
            WriteFile(Path.Combine("Content", TS1LocalTuning.FileName),
                "{\"dynamic\":{\"special\":{\"0\":{\"1\":1.0}}}}");

            var local = TS1LocalTuning.LoadFromBasePath(root);

            Assert.True(local.Loaded);
            Assert.Equal(1f, local.Dynamic.GetTuning("special", 0, 1));
        }

        [Fact]
        public void TheFlatLocationIsFoundToo()
        {
            //ObjectUpgradeProvider reads "Content/upgrades.json" off the working directory, so an
            //install whose root is the app dir may well have the file next to the executable
            WriteFile(TS1LocalTuning.FileName, "{\"dynamic\":{\"special\":{\"0\":{\"1\":2.0}}}}");

            var local = TS1LocalTuning.LoadFromBasePath(root);

            Assert.True(local.Loaded);
            Assert.Equal(2f, local.Dynamic.GetTuning("special", 0, 1));
        }

        [Fact]
        public void TheNestedLocationWinsWhenBothExist()
        {
            WriteFile(Path.Combine("Content", TS1LocalTuning.FileName),
                "{\"dynamic\":{\"special\":{\"0\":{\"1\":1.0}}}}");
            WriteFile(TS1LocalTuning.FileName,
                "{\"dynamic\":{\"special\":{\"0\":{\"1\":2.0}}}}");

            var local = TS1LocalTuning.LoadFromBasePath(root);

            Assert.Equal(1f, local.Dynamic.GetTuning("special", 0, 1));
        }

        #endregion

        #region merging into the named table

        [Fact]
        public void MergingCopiesTheFilesEntriesIntoTheTarget()
        {
            var local = TS1LocalTuning.Parse("{\"entries\":{\"lotmotives\":{\"None_FunWeight\":\"3\"}}}");
            var target = new Tuning();

            var result = local.MergeInto(target);

            Assert.Same(target, result);
            Assert.True(result.TryGetEntry("lotmotives", out var entry));
            Assert.Equal(3f, entry.GetNum("None_FunWeight"));
        }

        [Fact]
        public void MergingWithNoTargetMakesOne()
        {
            var local = TS1LocalTuning.Parse("{\"entries\":{\"t\":{\"a\":\"1\"}}}");

            var result = local.MergeInto(null);

            Assert.NotNull(result);
            Assert.True(result.TryGetEntry("t", out _));
        }

        [Fact]
        public void MergingReplacesAWholeEntryRatherThanFillingInMissingKeys()
        {
            var local = TS1LocalTuning.Parse("{\"entries\":{\"t\":{\"a\":\"2\"}}}");
            var target = new Tuning();
            target.SetValue("t", "b", "keep me");

            local.MergeInto(target);

            Assert.True(target.TryGetEntry("t", out var entry));
            Assert.Equal(2f, entry.GetNum("a"));
            Assert.False(entry.TryGetNum("b", out _));
        }

        [Fact]
        public void MergingWithNoEntriesSectionLeavesTheTargetAlone()
        {
            var local = TS1LocalTuning.Parse("{\"dynamic\":{}}");
            var target = new Tuning();
            target.SetValue("t", "a", "1");

            local.MergeInto(target);

            Assert.Equal(1u, target.EntryCount);
        }

        /// <summary>
        /// The reserved-prefix rule. The <c>bcon_*</c> entries are the install's own
        /// <c>Global.iff</c> constants - the interaction-score and happy-weight curves among them
        /// - and a modding file that could rewrite them by accident, with no unit and no scale,
        /// would be a footgun. It is dropped, and the file is told it happened.
        /// </summary>
        [Theory]
        [InlineData("bcon_0100")]
        [InlineData("BCON_0100")]
        [InlineData("bcon_0200")]
        public void AFileCannotClaimAReservedPositionalEntryName(string name)
        {
            var local = TS1LocalTuning.Parse("{\"entries\":{\"" + name + "\":{\"0\":\"5\"}}}");

            Assert.False(local.Entries.TryGetEntry(name, out _));
            Assert.Contains(local.Problems, p => p.Contains("reserved"));
        }

        [Fact]
        public void AFileClaimingAReservedNameStillLoadsEverythingElse()
        {
            var local = TS1LocalTuning.Parse(
                "{\"entries\":{\"bcon_0100\":{\"0\":\"5\"},\"lotmotives\":{\"a\":\"1\"}}}");

            Assert.True(local.Loaded);
            Assert.True(local.Entries.TryGetEntry("lotmotives", out _));
            Assert.False(local.Entries.TryGetEntry("bcon_0100", out _));
        }

        /// <summary>
        /// The composition the content manager performs: the data-derived table goes in, the local
        /// file is merged over it, and the install's constants come back out unchanged.
        /// </summary>
        [Fact]
        public void DataDerivedEntriesAndFileEntriesCoexist()
        {
            var data = new TS1GlobalTuning(new ushort[] { 1000, 1001, 1002 });
            var local = TS1LocalTuning.Parse("{\"entries\":{\"lotmotives\":{\"None_FunWeight\":\"3\"}}}");

            var tuning = data.ToNamedTuning(local.MergeInto(new Tuning()));

            Assert.True(tuning.TryGetEntry("bcon_0100", out var published));
            Assert.Equal(1001f, published.GetNum("1"));
            Assert.True(tuning.TryGetEntry("lotmotives", out var named));
            Assert.Equal(3f, named.GetNum("None_FunWeight"));
        }

        #endregion

        [Fact]
        public void TheSectionAndFileNamesAreTheOnesDocumentedInTheRemarks()
        {
            //spelled in three places - the XML docs, the file, and the two section keys - and a
            //change to one that missed the others would be invisible until someone wrote a file
            Assert.Equal("tuning.json", TS1LocalTuning.FileName);
            Assert.Equal("entries", TS1LocalTuning.EntriesSection);
            Assert.Equal("dynamic", TS1LocalTuning.DynamicSection);
        }
    }
}
