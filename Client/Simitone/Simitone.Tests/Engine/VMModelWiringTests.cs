using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The structural halves of the capture, validator and routing-name fixes, checked where they
    /// are written rather than run.
    ///
    /// <para>
    /// <c>VMCaptureSnapshotTests</c> drives the decisions themselves - what a copy of a motive rate
    /// change, a decay, a lot state and an architecture actually contains. What it cannot reach is
    /// the half that was broken: that the rule is <em>consulted</em> at the capture site, and
    /// that the guard which motivated it is not type-narrowed into meaninglessness. A test process
    /// cannot build a lot - no content, no graphics device, no running VM - so these assertions
    /// read the sources.
    /// </para>
    ///
    /// <para>
    /// Comments are stripped before any of this, so a paragraph explaining the fix cannot satisfy
    /// an assertion about it. The assertions are deliberately coarse: a call's name in the right
    /// method, a signature that is wide enough for the test inside it.
    /// </para>
    /// </summary>
    public sealed class VMModelWiringTests
    {
        // ---- the avatar capture (E-22) --------------------------------------------------------

        [Fact]
        public void EveryStructureTheAvatarHandsOverIsCopiedAtTheCapture()
        {
            // The five that were handed over by reference - MotiveChanges, MotiveDecay,
            // DefaultSuits, DynamicSuits, Decoration - plus the two outfit references, which the
            // audit did not list but which are the same kind of alias: SetAvatarBodyStrings replaces
            // DefaultSuits.Daywear, and VMNetSetOutfitCmd writes each Decoration field.
            //
            // The shape that is refused is "field = field": a marshal holding the very object the
            // avatar is still using.
            var save = MethodBody(Avatar(), "public VMAvatarMarshal Save()");

            foreach (var field in new[]
            {
                "MotiveChanges", "MotiveDecay", "DefaultSuits", "DynamicSuits", "Decoration",
                "BodyOutfit", "HeadOutfit",
            })
            {
                Assert.False(Regex.IsMatch(save, @"(?m)^\s*" + field + @"\s*=\s*" + field + @"\s*,"),
                    "VMAvatar.Save hands the marshal its live " + field + " again, so a capture"
                    + " drifts as the sim runs.");
            }
        }

        [Fact]
        public void TheMotiveRateTableIsCopiedThroughTheElementWiseRule()
        {
            // A single (VMMotiveChange[])Clone() would look like a copy and alias sixteen live
            // objects, because the elements are classes.
            var save = MethodBody(Avatar(), "public VMAvatarMarshal Save()");

            Assert.Contains("SnapshotMotiveChanges(MotiveChanges)", save);
            Assert.Contains("SnapshotMotiveDecay(MotiveDecay)", save);
        }

        [Fact]
        public void TheOutfitAndDecorationHoldersEachKnowHowToCopyThemselves()
        {
            // The alternative - a switch in the caller - would have to remember every holder, and
            // would quietly hand back the live object for one it did not know.
            foreach (var holder in new[]
            {
                "public VMAvatarDefaultSuits Snapshot()",
                "public VMAvatarDynamicSuits Snapshot()",
                "public VMAvatarDecoration Snapshot()",
            })
            {
                Assert.True(MethodBody(Avatar(), holder).Contains("new VMAvatar"),
                    "VMAvatar's " + holder + " does not build a new holder.");
            }
        }

        [Fact]
        public void EveryMotiveDecayImplementationCanBeCopied()
        {
            // An interface member rather than a type test, so a new platform's decay cannot reach
            // VMAvatar.Save without deciding what a snapshot of it is. The interface is declared
            // beside the TSO decay and implemented by both.
            var interfaceSource = VMAvatarMotiveDecay();
            var ts1Source = VMTS1MotiveDecay();

            Assert.Contains("VMIMotiveDecay Snapshot();", interfaceSource);

            foreach (var source in new[] { interfaceSource, ts1Source })
            {
                var snapshot = MethodBody(source, "public VMIMotiveDecay Snapshot()");
                Assert.Contains("MotiveFractions = (short[])MotiveFractions.Clone()", snapshot);
            }
        }

        // ---- the lot state half of a capture (E-39) ------------------------------------------

        [Fact]
        public void TheLotStateInACaptureIsASnapshotAndNotTheLotsOwnObject()
        {
            // The consumer that makes this matter is VMServerDriver.PrepareSync: it captures on the
            // lot thread and serializes the state sync under Task.Run, so a live lot state is read
            // while the lot thread is still writing to it.
            var save = MethodBody(VM(), "public VMMarshal Save(bool user = false)");

            Assert.Contains("PlatformState?.Snapshot()", save);
            Assert.False(Regex.IsMatch(save, @"(?m)^\s*PlatformState\s*=\s*PlatformState\s*,"),
                "VM.Save hands the marshal the lot's own platform state again.");
        }

        [Fact]
        public void TheArchitectureIsStillSnapshottedAtSaveTime()
        {
            // The tile arrays are handed over by reference on purpose - there are too many of them
            // to deep copy - and Preserialize is what makes the capture safe: it writes the lot to
            // Preserialized on the lot thread, and SerializeInto then returns those bytes without
            // touching a tile. VMCaptureSnapshotTests proves the bytes do not move; this asserts
            // the call is still there, because losing it would silently un-prove that.
            var save = MethodBody(Architecture(), "public virtual VMArchitectureMarshal Save()");

            Assert.Contains("marshal.Preserialize()", save);
        }

        [Fact]
        public void EveryPlatformLotStateCanBeSnapshotted()
        {
            Assert.Contains("public override VMAbstractLotState CreateSnapshot(int version)", TSOLotState());
            Assert.Contains("public override VMAbstractLotState CreateSnapshot(int version)", TS1LotState());
        }

        [Fact]
        public void TheAvatarDatabaseSaveIsASnapshotToo()
        {
            // VMNetAvatarPersistState.Save carried the avatar's own default-suits object, with a
            // //todo: clone? on the line. Same root cause as VMAvatar.Save: a capture that moves.
            var save = MethodBody(PersistState(), "public void Save(VMAvatar avatar)");

            Assert.Contains("avatar.DefaultSuits?.Snapshot()", save);
            Assert.False(Regex.IsMatch(save, @"DefaultSuits\s*=\s*avatar\.DefaultSuits\s*;"),
                "the avatar persist state captures the sim's live default suits again.");
        }

        // ---- the motive tables an avatar loads (E-36) ---------------------------------------

        [Fact]
        public void BothMotiveTablesAreFlooredWhereTheAvatarTakesThemFromASave()
        {
            var load = MethodBody(Avatar(), "public virtual void Load(VMAvatarMarshal input)");

            Assert.Contains("FloorMotiveChanges(input.MotiveChanges)", load);
            Assert.Contains("FloorMotiveTable(input.MotiveData)", load);
            Assert.Contains("FloorMotiveTable(dat)", MethodBody(Avatar(), "public void ReplaceMotiveData(short[] dat)"));
        }

        [Fact]
        public void AMotiveTheAvatarDoesNotHaveIsRefusedRatherThanIndexed()
        {
            // The floor above is the root fix; these are the backstops for any path that assigns
            // the table directly - VMTS1ActivatorNew.ConvertAvatar does.
            var get = MethodBody(Avatar(), "public virtual short GetMotiveData(VMMotive variable)");
            var set = MethodBody(Avatar(), "public virtual bool SetMotiveData(VMMotive variable, short value)");

            Assert.Contains("index >= MotiveData.Length", get);
            Assert.Contains("index >= MotiveData.Length", set);
        }

        // ---- the over-limit rule and its dead twin (E-37) ------------------------------------

        [Fact]
        public void AMotiveRateChangeAsksTheSharedRuleAndDoesNotClamp()
        {
            var tick = MethodBody(MotiveChange(), "public void Tick(VMAvatar avatar)");

            Assert.Contains("IsPastLimit(motive, MaxValue, rate)", tick);
            Assert.True(Regex.Matches(tick, @"IsPastLimit\(").Count == 1,
                "VMMotiveChange.Tick tests the over-limit predicate more than once. The second test"
                + " used to be the clamp that could never fire, because the first had already"
                + " returned on the identical predicate.");

            Assert.Contains("AppliesTo(Motive)", tick);
        }

        // ---- the validator's dead guard (E-32) ------------------------------------------------

        [Fact]
        public void TheAsyncSaleValidatorIsGuardedAgainstAPersonAndCanActuallySeeOne()
        {
            // The bug: the parameter was declared VMGameObject, so `obj is VMAvatar` was a constant
            // false - CS0184, on every build, for years - and "a person cannot be put up for sale"
            // guarded nothing. Widening the parameter to VMEntity is what makes the guard mean
            // something; the same class uses the identical test in GetDeleteMode, where the
            // parameter is already a VMEntity.
            Assert.Contains("public abstract bool CanManageAsyncSale(VMAvatar ava, VMEntity obj)",
                Validator());
            Assert.Contains("public override bool CanManageAsyncSale(VMAvatar ava, VMEntity obj)",
                DefaultValidator());
            Assert.Contains("public override bool CanManageAsyncSale(VMAvatar ava, VMEntity obj)",
                CommunityValidator());

            var body = MethodBody(DefaultValidator(), "public override bool CanManageAsyncSale(VMAvatar ava, VMEntity obj)");
            Assert.Contains("obj is VMAvatar", body);
        }

        [Fact]
        public void NoValidatorDeclaresASaleCheckOverAnObjectItCannotHoldAPersonIn()
        {
            Assert.True(Regex.Matches(Validator(), @"CanManageAsyncSale\(VMAvatar ava, VMGameObject").Count == 0,
                "the abstract signature is type-narrowed again, which is what made the person test"
                + " below it constant false.");
        }

        // ---- one name for "not out of world" (E-48) -------------------------------------------

        [Fact]
        public void TheMultitileGroupNamesTheRoomConstantInsteadOfWritingItsNumber()
        {
            // SetVisualPosition puts the value straight on an ObjectComponent, and the ghost path
            // says the same thing through SetRoom, which stores room - 1. The two are deliberately
            // one number apart, and they used to be two literals with no relationship recorded.
            var source = Multitile();

            Assert.Contains("VMContext.RoomNotInWorld", source);
            Assert.True(Regex.Matches(source, @"\b6553[3-5]\b").Count == 0,
                "VMMultitileGroup writes a bare room number again where VMContext has a name for it.");
        }

        // ---- one name for the routing node (E-53) --------------------------------------------

        [Fact]
        public void TheRoutingNodeTypeIsNotNamedTheSameAsTheTriangleSetNode()
        {
            // FSO.Common.Model has a VMObstacleSetNode holding a BaseMeshTriangle, and
            // VMObstacleSet.cs imports that namespace: every reference to the routing node
            // produced CS0436 and the compiler picked one by a rule nobody wrote down. The two are
            // legitimately different types - VMObstacle against BaseMeshTriangle - so the routing
            // one is renamed rather than the two being merged.
            var source = ObstacleSet();

            Assert.Contains("struct VMObstacleSetNodeRouting", source);
            Assert.True(Regex.Matches(source, @"(?<!Routing)\bVMObstacleSetNode\b").Count == 0,
                "an unqualified VMObstacleSetNode is back in the routing set, where the compiler"
                + " has to choose between it and the one in FSO.Common.");
        }

        // ====================================================================================
        // helpers
        // ====================================================================================

        private static string Avatar() => Source("Entities", "VMAvatar.cs");
        private static string Multitile() => Source("Entities", "VMMultitileGroup.cs");
        private static string MotiveChange() => Source("Model", "VMMotiveChange.cs");
        private static string Validator() => Source("Model", "Platform", "VMAbstractValidator.cs");
        private static string DefaultValidator() => Source("Model", "Platform", "VMDefaultValidator.cs");
        private static string CommunityValidator() => Source("Model", "Platform", "VMFSOCommunityValidator.cs");
        private static string TSOLotState() => Source("Model", "TSOPlatform", "VMTSOLotState.cs");
        private static string TS1LotState() => Source("Model", "TS1Platform", "VMTS1LotState.cs");
        private static string ObstacleSet() => Source("Model", "Routing", "VMObstacleSet.cs");
        private static string VMAvatarMotiveDecay() => Source("Entities", "VMAvatarMotiveDecay.cs");
        private static string VMTS1MotiveDecay() => Source("Entities", "VMTS1MotiveDecay.cs");
        private static string PersistState() => Source("NetPlay", "Model", "VMNetAvatarPersistState.cs");
        private static string Architecture() => Source("VMArchitecture.cs");
        private static string VM() => Source("VM.cs");

        /// <summary>
        /// A tso.simantics source file with its comments removed, so prose cannot satisfy an
        /// assertion about the code.
        /// </summary>
        private static string Source(params string[] path)
        {
            var segments = new[] { FreeSoDir(), "FreeSO", "TSOClient", "tso.simantics" }.Concat(path);
            var full = Path.Combine(segments.ToArray());

            Assert.True(File.Exists(full), "could not find " + full);

            var text = File.ReadAllText(full);
            var withoutBlocks = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
        }

        private static string FreeSoDir()
        {
            var dir = new DirectoryInfo(ClientDir());
            for (var i = 0; i < 4 && dir != null; i++, dir = dir.Parent)
            {
                if (Directory.Exists(Path.Combine(dir.FullName, "FreeSO", "TSOClient"))) return dir.FullName;
            }

            throw new DirectoryNotFoundException("Could not find FreeSO/TSOClient above " + ClientDir());
        }

        private static string ClientDir()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Simitone.Windows", "Simitone.Windows.csproj"))) return dir.FullName;
                if (File.Exists(Path.Combine(dir.FullName, "Simitone.Windows.csproj"))) return dir.FullName;

                dir = dir.Parent;
            }

            throw new FileNotFoundException("Could not find Simitone.Windows.csproj above " + AppContext.BaseDirectory);
        }

        /// <summary>A C# method body, located by its opening anchor.</summary>
        private static string MethodBody(string source, string signature)
        {
            var at = source.IndexOf(signature, StringComparison.Ordinal);
            Assert.True(at >= 0, "no '" + signature + "' in the source under test");

            var open = source.IndexOf('{', at);
            Assert.True(open >= 0, "'" + signature + "' has no body");

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(open, i - open + 1);
            }

            throw new InvalidOperationException("unbalanced braces after '" + signature + "'");
        }
    }
}
