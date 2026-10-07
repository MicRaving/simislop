using FSO.Common.Enum;
using FSO.Common.Model;
using FSO.SimAntics;
using FSO.SimAntics.Entities;
using FSO.SimAntics.Model.Platform;
using FSO.SimAntics.Model.TSOPlatform;
using FSO.SimAntics.Model.TS1Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The lot category a TS1 lot answers with, and the null dereference that answering it
    /// removes.
    ///
    /// <para>
    /// <b>The hazard.</b> <c>vm.Tuning</c> is the <c>DynamicTuning</c> payload - the
    /// <c>Content/tuning.json</c> "dynamic" section in TS1 mode, a <c>VMNetTuningCmd</c> from a
    /// shard in TSO mode. It used to be null for the whole of a TS1 session, because nothing
    /// sends a TS1 lot a join command. It is no longer null: <see cref="VM.Init"/> hands the local
    /// payload to a TS1 VM. Two places read it, and both were relying on that null:
    /// </para>
    /// <list type="bullet">
    /// <item><c>VMRelationship</c> (opcode 26, a shared registration, so it runs on TS1):
    /// <c>context.VM.Tuning?.GetTuning("category_mul", 0, context.VM.TSOState.PropertyCategory)</c>.
    /// The <c>?.</c> suppresses the whole invocation, argument included, so with a null payload
    /// the <c>vm.TSOState</c> dereference behind it never happened. A non-null payload evaluates
    /// it, and <c>vm.TSOState</c> is <c>PlatformState as VMTSOLotState</c> - null on a TS1 lot,
    /// because the category lives on <see cref="VMTSOLotState.PropertyCategory"/> and a TS1 lot
    /// has a <see cref="VMTS1LotState"/> instead. That is a <c>NullReferenceException</c> on a
    /// live TS1 opcode.</item>
    /// <item><c>VMTuningCache.UpdateTuning</c>: <c>vm.Tuning?.GetTable("overfill", vm.TSOState.PropertyCategory)</c>,
    /// the same shape. It is safe, and only because of the <c>if (vm.TS1) return;</c> on its first
    /// line - a <c>?.</c> is not a guard when the argument is a dereference. Asserted by
    /// <see cref="TheTuningCacheStillRefusesATs1LotBeforeItLooksAnythingUp"/>.</item>
    /// </list>
    ///
    /// <para>
    /// <b>The decision, and where it lives.</b> A TS1 lot is a house, so it answers
    /// <see cref="LotCategory.residence"/> (10) - see
    /// <see cref="VMTS1LotState.PropertyCategory"/> for why not community and why not none. The
    /// accessor is <see cref="VM.LotCategoryFor"/>, a static over the lot state rather than a
    /// property on <c>VM</c>, because there is no headless way to construct a <c>VM</c> in this
    /// tree (<c>new VM(...)</c> dereferences the content manager) - so pinning the decision means
    /// pinning something reachable without one.
    /// </para>
    ///
    /// <para>
    /// <b>Why the value is safe to pick.</b> It is a <i>key</i> into
    /// <c>DynamicTuning.GetTuning("category_mul", 0, category)</c>, and a key nothing has an entry
    /// for reads as null, which the caller turns into a multiplier of 1 - which is what a TS1 lot
    /// does today. So the tests below assert both halves of that: the lookup is <i>total</i> for a
    /// TS1 lot (no throw, whatever the payload), and it is <i>inert</i> unless the payload asks
    /// for a change under key 10.
    /// </para>
    /// </summary>
    public sealed class VMTS1LotCategoryTests
    {
        /// <summary>The tuning type <c>VMRelationship</c> looks relationship drift up in.</summary>
        private const string CategoryMultiplierTable = "category_mul";

        /// <summary>The table index that call uses. Table 0 is the only one.</summary>
        private const int RelationshipTable = 0;

        /// <summary>
        /// The multiplier a TS1 lot gets when its tuning payload says nothing about it. This is
        /// the value the <c>?? 1f</c> in the primitive produces, and therefore the value every
        /// TS1 lot has always used.
        /// </summary>
        private const float RetailMultiplier = 1f;

        /// <summary>
        /// The one file allowed to keep a tuning lookup that dereferences a TSO-only lot member,
        /// because it refuses a TS1 lot on its first line.
        /// </summary>
        private const string GuardedCacheSource = "VMTuningCache.cs";

        // ---- the value ------------------------------------------------------------------------

        [Fact]
        public void ATs1LotIsAResidentialLot()
        {
            // The whole decision in one assertion. 10 is `FSO.Common.Enum.LotCategory.residence`
            // and it is written here as the enum rather than as a literal, so re-pointing the
            // constant at another category makes this test say so instead of going quiet.
            Assert.Equal((byte)LotCategory.residence, VMTS1LotState.PropertyCategory);
            Assert.Equal((byte)10, VMTS1LotState.PropertyCategory);
        }

        [Fact]
        public void TheAnswerIsAWellFormedKeyInTheTuningVocabulary()
        {
            // The category is used as an index, so "a real category" is a stronger claim than "a
            // number that happens not to crash". VMAvatarMotiveDecay is the other table in the
            // engine that indexes by it, and its name table has to have an entry - 0..10 is the
            // real range, and both of its consumers clamp `cat > 10` to 0 rather than guarding.
            var category = VMTS1LotState.PropertyCategory;

            Assert.InRange((int)category, 0, VMAvatarMotiveDecay.CategoryNames.Length - 1);
            Assert.Equal("Residence", VMAvatarMotiveDecay.CategoryNames[category]);
        }

        [Fact]
        public void TheAnswerIsNotTheCommunityCategory()
        {
            // 11 is the only other defensible answer and it is not a free one: the byte is read by
            // VMTSOLotState.ActivateValidator to install VMFSOCommunityValidator, and
            // CommunityLot is the donator/benefactor economy. Asserted because "TS1 is a
            // neighbourhood, so community" is the mistake this constant exists to prevent, and
            // nothing at the call site would complain - it would just be a different tuning key.
            Assert.NotEqual((byte)LotCategory.community, VMTS1LotState.PropertyCategory);
            Assert.False(new VMTSOLotState { PropertyCategory = VMTS1LotState.PropertyCategory }.CommunityLot);
        }

        [Fact]
        public void TheAnswerIsNotTheAbsentCategory()
        {
            // 0 is safe in exactly the sense that matters (it is a valid index, so it cannot
            // crash) and wrong in the one that does not: VMAvatarMotiveDecay.UpdateCategory would
            // weight a house as "None_*", and a tuning file keyed on 0 would be picked up by
            // every category at once.
            Assert.NotEqual((byte)LotCategory.none, VMTS1LotState.PropertyCategory);
        }

        [Fact]
        public void ATsoResidentialLotAgreesWithATs1Lot()
        {
            // The reason 10 is the right key and not merely a safe one: it is the answer a TSO
            // residential lot gives too. A tuning file authored against TSO's category_mul -
            // which is the realistic source of one, since that is where the table comes from - is
            // then already correct for a TS1 house under its residential entry, with no TS1-only
            // remapping layer to keep in step.
            var tso = new VMTSOLotState { PropertyCategory = (byte)LotCategory.residence };

            Assert.Equal(VMTS1LotState.PropertyCategory, tso.PropertyCategory);
            Assert.Equal(VMTS1LotState.PropertyCategory, VM.LotCategoryFor(tso));
        }

        // ---- the lookup is total for a TS1 lot -------------------------------------------------

        [Fact]
        public void TheOldWayOfAskingWasTheThrowAndItStillIs()
        {
            // Stated as a fact about the platform states rather than about a line of code that no
            // longer exists: `vm.TSOState` is `PlatformState as VMTSOLotState`, so on a TS1 lot it
            // is null and `.PropertyCategory` on it throws. This is the throw the `?.` used to
            // hide, and it is the reason a non-null `vm.Tuning` was not safe to push into a TS1 VM
            // on its own.
            var ts1 = (VMAbstractLotState)new VMTS1LotState();

            // `vm.TSOState` is `PlatformState as VMTSOLotState`, so the `as` yields null rather
            // than throwing, and the throw comes from the field read on the next token - which is
            // the shape the primitive's argument actually had.
            Assert.Null(ts1 as VMTSOLotState);                                   // == vm.TSOState
            Assert.Throws<NullReferenceException>(() => (ts1 as VMTSOLotState).PropertyCategory);
        }

        [Fact]
        public void TheNewWayOfAskingAnswersForATs1Lot()
        {
            Assert.Equal((byte)LotCategory.residence, VM.LotCategoryFor(new VMTS1LotState()));
            Assert.Equal((byte)LotCategory.residence, VM.LotCategoryFor(new VMTS1LotState(1)));
        }

        [Fact]
        public void TheRelationshipTuningLookupIsTotalForATs1Lot()
        {
            // The whole expression, over a real DynamicTuning and a real TS1 lot state, for every
            // shape the payload can be in. Before the fix the first and third of these threw a
            // NullReferenceException: with a null payload the `?.` hid the dereference, and with
            // a non-null one there was no dereference to hide.
            var lot = (VMAbstractLotState)new VMTS1LotState();

            Assert.Equal(RetailMultiplier, RelationshipMultiplier(null, lot));
            Assert.Equal(RetailMultiplier, RelationshipMultiplier(new DynamicTuning(new List<DynTuningEntry>()), lot));
            Assert.Equal(0.5f, RelationshipMultiplier(WithCategoryMultiplier(VMTS1LotState.PropertyCategory, 0.5f), lot));
        }

        [Fact]
        public void ACategoryTheLotIsNotDoesNotScaleIt()
        {
            // The inertness half. A payload carrying a TSO venue's multiplier - which is what a
            // tuning.dat-derived file looks like, it has an entry per category - must leave a TS1
            // house at 1.0, because 10 is the only key a TS1 lot can present. This is what makes
            // the choice of key a tuning decision rather than a behaviour change: picking a wrong
            // key is survivable and silent, which is why the tests below also pin the key.
            var lot = (VMAbstractLotState)new VMTS1LotState();
            var shopping = (byte)LotCategory.shopping;

            Assert.NotEqual(VMTS1LotState.PropertyCategory, shopping);
            Assert.Equal(RetailMultiplier, RelationshipMultiplier(WithCategoryMultiplier(shopping, 3f), lot));
        }

        [Fact]
        public void TheTS1AnswerIsTheSameForEveryLotStateTheEngineCanBuild()
        {
            // VMTS1LotState has a versioned constructor because the save format has history, and
            // `new VMTS1LotState(version)` is how a deserialised lot arrives. The category cannot
            // depend on the version, so all of them answer the same thing.
            var answers = Enumerable.Range(0, 20)
                .Select(v => VM.LotCategoryFor(new VMTS1LotState(v)))
                .Distinct()
                .ToArray();

            Assert.Single(answers);
            Assert.Equal((byte)LotCategory.residence, answers[0]);
        }

        // ---- TSO is unchanged ------------------------------------------------------------------

        [Theory]
        [InlineData((byte)0)]
        [InlineData((byte)1)]
        [InlineData((byte)4)]
        [InlineData((byte)5)]
        [InlineData((byte)7)]
        [InlineData((byte)10)]
        [InlineData((byte)11)]
        [InlineData((byte)255)]
        public void EveryTsoCategoryComesBackAsItself(byte category)
        {
            // The "nothing changed on TSO" half, over the whole byte range rather than the values
            // anyone happens to use: for a TSO lot the accessor is a straight read of the lot
            // state's own field, which is what the primitive did before.
            var lot = new VMTSOLotState { PropertyCategory = category };

            Assert.Equal(category, VM.LotCategoryFor(lot));
            Assert.Equal(lot.PropertyCategory, VM.LotCategoryFor(lot));
        }

        [Fact]
        public void EveryPossibleTsoCategoryComesBackAsItself()
        {
            for (int category = 0; category <= byte.MaxValue; category++)
            {
                var lot = new VMTSOLotState { PropertyCategory = (byte)category };
                Assert.Equal((byte)category, VM.LotCategoryFor(lot));
            }
        }

        [Fact]
        public void AMissingPlatformStateStillThrowsRatherThanTuningItselfResidential()
        {
            // The one input that is deliberately not total, pinned so that "total" is not read as
            // "returns a default for anything". Widening this to a fallback would mean a TSO VM
            // that somehow had no lot state would quietly use the residential key and say
            // nothing, where the old expression said nothing too - by crashing. VM.Init puts a
            // state on the VM and no script runs before that, so this is unreachable from a lot.
            Assert.Throws<NullReferenceException>(() => VM.LotCategoryFor(null));
        }

        // ---- the save format did not move ------------------------------------------------------

        [Fact]
        public void TheNewCategoryIsNotInTheTs1SaveFormat()
        {
            // TS1 saves are user data and adding a byte to them would invalidate every lot anyone
            // has. The category is a property of the platform - there is one kind of TS1 lot - so
            // it is a constant precisely so that there is nothing to round-trip. Pinned as bytes
            // rather than as "does not mention PropertyCategory", because the byte count is the
            // thing that actually breaks a load.
            var state = new VMTS1LotState();
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                state.SerializeInto(writer);
            }

            // SimulationInfo absent (one bool) + no family (0xFFFF), and nothing else.
            Assert.Equal(3, stream.Length);
            Assert.Equal(new byte[] { 0x00, 0xFF, 0xFF }, stream.ToArray());
        }

        [Fact]
        public void TheTs1LotStateStillRoundTripsWithTheNewConstantInPlace()
        {
            // The load half of the same claim, and a real end-to-end pass through the format rather
            // than a count. A default-constructed state is the degenerate case - no SIMI, no family
            // - which is what an empty lot actually serialises to.
            var state = new VMTS1LotState();
            byte[] bytes;
            var stream = new MemoryStream();
            using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            {
                state.SerializeInto(writer);
            }
            bytes = stream.ToArray();

            var loaded = new VMTS1LotState();
            using (var reader = new BinaryReader(new MemoryStream(bytes)))
            {
                loaded.Deserialize(reader);
            }

            Assert.Null(loaded.SimulationInfo);
            Assert.Null(loaded.CurrentFamily);
            // the point of the round trip: the category is answerable on a state that came off
            // disk, and it is the same answer a freshly built one gives
            Assert.Equal(VMTS1LotState.PropertyCategory, VM.LotCategoryFor(loaded));
        }

        // ---- the code keeps its end of the bargain --------------------------------------------

        [Fact]
        public void TheRelationshipPrimitiveAsksTheVmRatherThanTheTsoLotState()
        {
            // The regression guard for the actual fix. `VMRelationship` is the line that would
            // have thrown; a future edit that puts `TSOState.PropertyCategory` back - or reaches
            // for it in the operand of a new tuning call - fails here instead of on a TS1 lot
            // with a tuning.json.
            var source = ReadSource("Primitives", "VMRelationship.cs");
            var code = StripComments(source);

            Assert.DoesNotContain("TSOState.PropertyCategory", code);
            Assert.Contains(".LotCategory", code);
        }

        [Fact]
        public void TheTuningCacheStillRefusesATs1LotBeforeItLooksAnythingUp()
        {
            // The other `Tuning` + `TSOState` site, and the reason it is safe. It is safe only
            // because of the early return: `vm.Tuning?.GetTable("overfill", vm.TSOState.PropertyCategory)`
            // has a `?.` in front of the payload and a dereference in its argument, which is
            // exactly the pattern that is safe when the payload is null and throws when it is not.
            // Asserted structurally - the guard has to be the first thing the method does, ahead
            // of the table read - because a `?.` is not a guard and reads like one.
            var source = ReadSource("Model", GuardedCacheSource);
            var code = StripComments(source);
            var method = Between(code, "public void UpdateTuning", "public short GetLimit");

            Assert.NotNull(method);
            Assert.True(
                Regex.IsMatch(method, @"if\s*\(\s*vm\.TS1\s*\)\s*return;"),
                "VMTuningCache.UpdateTuning must open with `if (vm.TS1) return;` - the `?.` on the"
                + " tuning payload does not protect the argument");
            var guard = method.IndexOf("if (vm.TS1) return;", StringComparison.Ordinal);
            var read = method.IndexOf("GetTable", StringComparison.Ordinal);
            Assert.True(guard >= 0 && read > guard, "the guard has to come before the table read");
        }

        [Fact]
        public void ThePayloadIsOnlyAssignedToATs1Vm()
        {
            // The other half of the guard story: TSO's Tuning arrives over the wire from
            // VMNetTuningCmd, and `Content.DynamicTuning` is only ever populated in TS1 mode, so
            // an unguarded assignment would be a no-op today and a silent override tomorrow. The
            // `?.` matters too - a VM built with no content manager has to keep a null Tuning,
            // which is the state every tuning read in the tree already handles.
            var source = ReadSource("VM.cs");
            var code = StripComments(source);
            var init = Between(code, "public void Init()", "public void Reset()");

            Assert.NotNull(init);
            Assert.Contains("if (TS1) Tuning = Content.Content.Get()?.DynamicTuning;", init);
            // and the assignment is after the platform state, so LotCategory is answerable from
            // the moment Tuning is not null
            Assert.True(
                init.IndexOf("PlatformState =", StringComparison.Ordinal)
                    < init.IndexOf("if (TS1) Tuning", StringComparison.Ordinal),
                "the platform state has to exist before the tuning payload is pushed in, or a"
                + " consumer could ask for a category before there is a lot to ask about");
        }

        [Fact]
        public void NoTuningLookupInTheEngineReachesForATsoOnlyLotMemberOutsideTheGuardedCache()
        {
            // The whole hazard, stated as a property of the present tree rather than of one line
            // that has already been fixed. Anywhere that reads the tuning payload and, in the same
            // expression, dereferences a member of a TSO-only platform state, is a NullReference
            // waiting for a TS1 lot to arrive with a tuning.json - because `vm.TSOState` is
            // `PlatformState as VMTSOLotState` and is null there.
            //
            // A `?.` on the payload does not make such a line safe, which is the whole reason this
            // has to be a scan rather than a convention: `vm.Tuning?.GetTable(t, vm.TSOState.X)`
            // looks guarded and is not.
            var offenders = new List<string>();
            var excluded = new List<string>();

            foreach (var file in new DirectoryInfo(SemanticsDirectory()).EnumerateFiles("*.cs", SearchOption.AllDirectories))
            {
                var code = StripComments(File.ReadAllText(file.FullName));
                var lines = code.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (!Regex.IsMatch(lines[i], @"\.Tuning\b")) continue;
                    if (!Regex.IsMatch(lines[i], @"TSOState\s*\.")) continue;

                    if (file.Name == GuardedCacheSource)
                    {
                        excluded.Add(file.Name + ":" + (i + 1));
                        continue;
                    }
                    offenders.Add(file.Name + ":" + (i + 1) + ": " + lines[i].Trim());
                }
            }

            Assert.Empty(offenders);
            // the exclusion is a hole with a known size. If a second site appears in that file, or
            // the one there stops being the guarded read, the two tests above are what catch it -
            // and this keeps the exclusion from quietly growing to cover anything.
            Assert.Equal(new[] { GuardedCacheSource + ":12" }, excluded);
        }

        // ---- helpers ---------------------------------------------------------------------------

        /// <summary>
        /// The whole of the <c>VMRelationship</c> line, reproduced against real types. Written out
        /// rather than reached through the primitive because the primitive needs a live
        /// <c>VMStackFrame</c> on a running lot, and there is no headless harness for one in this
        /// tree - so the expression is pinned, and the two structural tests above pin that the
        /// primitive still uses it.
        /// </summary>
        private static float RelationshipMultiplier(DynamicTuning tuning, VMAbstractLotState lot)
        {
            return tuning?.GetTuning(CategoryMultiplierTable, RelationshipTable, VM.LotCategoryFor(lot)) ?? RetailMultiplier;
        }

        private static DynamicTuning WithCategoryMultiplier(byte category, float value)
        {
            return new DynamicTuning(new List<DynTuningEntry>
            {
                new DynTuningEntry
                {
                    tuning_type = CategoryMultiplierTable,
                    tuning_table = RelationshipTable,
                    tuning_index = category,
                    value = value
                }
            });
        }

        /// <summary>
        /// Comments are not code, and a guard is <i>documented</i> by naming the thing it
        /// replaced - which is exactly what the scan below searches for. Stripping them first is
        /// what lets the reasoning sit next to the code it is reasoning about.
        /// </summary>
        private static string StripComments(string source)
        {
            var code = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(code, @"//[^\n]*", "");
        }

        private static string Between(string source, string start, string end)
        {
            var from = source.IndexOf(start, StringComparison.Ordinal);
            if (from < 0) return null;
            var to = source.IndexOf(end, from, StringComparison.Ordinal);
            return to < 0 ? source.Substring(from) : source.Substring(from, to - from);
        }

        private static string ReadSource(params string[] relativePath)
        {
            var path = Path.Combine(new[] { SemanticsDirectory() }.Concat(relativePath).ToArray());
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Could not find " + path
                    + ". These tests read the engine sources on purpose, so they cannot pass by"
                    + " finding nothing.", path);
            }
            return File.ReadAllText(path);
        }

        /// <summary>
        /// The engine source folder, found by walking up out of the test output until the marker
        /// file turns up, so a change to the build layout does not turn this into "file not found".
        /// </summary>
        private static string SemanticsDirectory()
        {
            var marker = Path.Combine("FreeSO", "TSOClient", "tso.simantics", "FSO.SimAntics.csproj");
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, marker);
                if (File.Exists(candidate)) return Path.GetDirectoryName(candidate);
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not find the FreeSO engine sources (looked for " + marker + ") walking up from "
                + AppContext.BaseDirectory + ".");
        }
    }
}
