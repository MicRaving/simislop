using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The structural halves of the entity fixes, checked where they are written rather than run.
    ///
    /// <para>
    /// The companion rule tests (<c>VMMultitileGroupTests</c>, <c>VMEntitySlotTests</c>,
    /// <c>VMEntityLoadTests</c>, <c>VMAvatarDataTests</c>) drive the decisions themselves - which
    /// entries a rebuilt group keeps, which slot numbers a container honours, what a missing master
    /// resolves to, how a 50-slot table is filled. What none of them can reach is the half that
    /// was actually broken: that the rule is <em>consulted</em>, and consulted on the right list.
    /// A test process cannot build a lot - no content, no graphics device, and a running VM is
    /// four walls and a scheduler - so these assertions read the sources.
    /// </para>
    ///
    /// <para>
    /// They are deliberately coarse: a call's name appearing in the right method, a list walk not
    /// becoming an index. They would not survive a refactor that renamed things, which is the
    /// intended trade - a rename fails loudly and a human reads the message, where a silently
    /// detached guard fails in a playtest. Comments are stripped before any of this, so a
    /// paragraph explaining the fix cannot satisfy an assertion about it.
    /// </para>
    /// </summary>
    public sealed class VMEntitiesWiringTests
    {
        // ---- a multitile group rebuilt from a save (E-01, E-33) ----------------------------

        public static TheoryData<string, string> GroupLoads => new TheoryData<string, string>
        {
            { "Load", "public virtual void Load(VMMultitileGroupMarshal input, VMContext context)" },
            { "LoadGhost", "public void LoadGhost(VMMultitileGroupMarshal input, VMContext context, VMEntity[] objs)" },
        };

        [Theory]
        [MemberData(nameof(GroupLoads))]
        public void BothLoadsRebuildTheGroupThroughTheResolveRule(string name, string signature)
        {
            var body = MethodBody(Multitile(), signature);

            Assert.True(body.Contains("ResolveOffsets<VMEntity>"),
                "VMMultitileGroup." + name + " walks the save's id list directly, so Objects ends"
                + " up shorter than the list anything downstream indexes it with.");
        }

        [Theory]
        [MemberData(nameof(GroupLoads))]
        public void BothLoadsResetTheOffsetsAlongsideTheObjects(string name, string signature)
        {
            // Objects was rebuilt and Offsets was appended to, so a second load on the same group
            // left it longer than Objects - and BaseObject, ChangePosition and SetVisualPosition
            // all index the two in step.
            var body = MethodBody(Multitile(), signature);

            Assert.True(body.Contains("Offsets = new List<LotTilePos>()"),
                "VMMultitileGroup." + name + " resets Objects but not Offsets.");
        }

        [Fact]
        public void TheBrokenMultitileSmokeRunsOverTheResolvedObjectsAndOnlyOverObjects()
        {
            var load = MethodBody(Multitile(), "public virtual void Load(VMMultitileGroupMarshal input, VMContext context)");

            Assert.True(load.Contains("ForEachGameObject<VMEntity, VMGameObject>"),
                "the broken-multitile smoke no longer goes through the type filter, so it can run"
                + " over something that is not a game object again.");

            // The original line: a loop bounded by the save's id count indexing the *filtered*
            // list, and an unconditional cast of whatever came back.
            Assert.DoesNotContain("((VMGameObject)Objects[i])", load);
            Assert.False(Regex.IsMatch(load, @"for\s*\(\s*int\s+i\s*=\s*0;\s*i\s*<\s*input\.Objects\.Length"),
                "VMMultitileGroup.Load still bounds a loop by the save's id count, which is not the"
                + " length of the list it indexes.");
        }

        [Fact]
        public void AGroupNeverCountsItsObjectsWithTheLinqExtension()
        {
            // Objects.Count() allocates an enumerator on every loop iteration, which made
            // BaseObject - called from ChangePosition, SetVisualPosition and GhostCopyGroup -
            // O(n^2) with an allocation per step.
            var offenders = Regex.Matches(Multitile(), @"Objects\.Count\(\s*\)").Count;

            Assert.Equal(0, offenders);
        }

        // ---- merging two groups (E-19) ------------------------------------------------------

        [Fact]
        public void AMergeRotatesTheOffsetItStores()
        {
            // The original line called Vector3.Transform and threw the result away, so a dynamic
            // multitile assembled facing anything but north combined at un-rotated offsets. XNA's
            // Transform returns a new vector, so the only way to lose the rotation is to not use
            // it - which is to say, every call has to be assigned to something.
            Assert.True(MethodBody(Multitile(), "public void Combine(VMMultitileGroup other)")
                    .Contains("LocalOffset(bObj.Position, bObj.Direction, obj.Position, leadOff)"),
                "VMMultitileGroup.Combine computes the merged offset itself again, rather than"
                + " through the shared rule that rotates it.");

            var localOffset = MethodBody(Multitile(),
                "public static LotTilePos LocalOffset(LotTilePos basePos, Direction baseDir, LotTilePos worldPos, LotTilePos baseOffset)");

            var calls = Regex.Matches(localOffset, @"InverseRotate\(");
            Assert.NotEmpty(calls);
            foreach (Match call in calls)
            {
                var before = localOffset.Substring(0, call.Index).TrimEnd();
                Assert.True(before.EndsWith("=", StringComparison.Ordinal),
                    "the multitile merge offset rotates the world delta without using the return"
                    + " value again, so merged tiles land at their un-rotated world offsets.");
            }
        }

        [Fact]
        public void TheMergeUndoesTheSameRotationThePlacementPathsApply()
        {
            // The two halves of one equation, and the whole regression. `Offsets` live in the base
            // tile's own frame and ChangePosition/SetVisualPosition rotate them *forward*; a merge
            // has to rotate a world delta *back*. When the two were written independently - a
            // hand-rolled Matrix.CreateRotationZ on each side - they agreed only for NORTH, which
            // is why this only ever showed up as "placing on other elements only sometimes works".
            //
            // So: the placement paths go through ForwardRotate, the merge goes through
            // InverseRotate, and nothing outside the two rotation helpers builds a matrix.
            var change = MethodBody(Multitile(),
                "public VMPlacementResult ChangePosition(LotTilePos pos, Direction direction, VMContext context, VMPlaceRequestFlags flags)");
            var visual = MethodBody(Multitile(),
                "public void SetVisualPosition(Vector3 pos, Direction direction, VMContext context)");

            Assert.Contains("ForwardRotate(", change);
            Assert.Contains("ForwardRotate(", visual);
            Assert.DoesNotContain("CreateRotationZ(", change);
            Assert.DoesNotContain("CreateRotationZ(", visual);
            Assert.DoesNotContain("rotMat", change);
            Assert.DoesNotContain("rotMat", visual);

            var inverse = MethodBody(Multitile(), "public static Vector3 InverseRotate(Vector3 worldDelta, Direction direction)");
            Assert.Contains("InverseRotation(direction)", inverse);

            //and the rotation itself is built in exactly one place per direction.
            var matrixBuilders = Regex.Matches(Multitile(), @"Matrix\.CreateRotationZ\(").Count;
            Assert.Equal(2, matrixBuilders);
        }

        [Fact]
        public void AMergeTakesTheEntitiesOutOfTheGroupItIsFoldingIn()
        {
            var combine = MethodBody(Multitile(), "public void Combine(VMMultitileGroup other)");

            // They did not used to, so the old group went on listing tiles that now belong to this
            // one and both groups claimed the same entities - which is what let a later
            // UpdateDynamicMultitile merge them a second time.
            Assert.True(combine.Contains("other.RemoveObject(obj)"),
                "VMMultitileGroup.Combine adds the entities to this group without removing them"
                + " from the one it is folding in.");
        }

        [Fact]
        public void AnEmptiedGroupStillHasABase()
        {
            // Combine drains the donor, and an emptied group used to answer null from BaseObject -
            // which GUID, two members up, dereferences. So the base has to be noted before the
            // first entity leaves, and BaseObject has to fall back to it.
            var combine = MethodBody(Multitile(), "public void Combine(VMMultitileGroup other)");

            var remember = combine.IndexOf("other.RememberBaseObject()", StringComparison.Ordinal);
            var firstMove = combine.IndexOf("other.RemoveObject(obj)", StringComparison.Ordinal);
            Assert.True(remember >= 0,
                "VMMultitileGroup.Combine empties the group it is folding in without noting its base"
                + " first, so BaseObject - and GUID through it - answers null for it afterwards.");
            Assert.True(remember < firstMove, "the donor's base has to be noted before the drain, not"
                + " after it - afterwards there is nothing left to note.");

            var baseObject = MethodBody(Multitile(), "public VMEntity BaseObject");
            Assert.Contains("found ?? lastBaseObject", baseObject);

            var find = MethodBody(Multitile(), "private VMEntity FindBaseObject()");
            Assert.Contains("Objects.Count == 0) return null", find);
        }

        [Fact]
        public void TheLeadOffsetCannotIndexOffTheEndOfTheList()
        {
            // Objects.IndexOf(BaseObject) answers -1 whenever the base is not a member, which after
            // a merge is what a reader of the folded-into group sees. The old line indexed
            // Offsets with it unguarded, on the placement path.
            var lead = MethodBody(Multitile(), "private LotTilePos LeadOffset()");

            Assert.DoesNotContain("Offsets[", lead);
            Assert.Contains("LeadOffsetAt(", lead);

            var at = MethodBody(Multitile(),
                "public static LotTilePos LeadOffsetAt(IList<LotTilePos> offsets, int baseIndex)");
            Assert.Contains("baseIndex < 0", at);
            Assert.Contains("baseIndex >= offsets.Count", at);
        }

        [Fact]
        public void TheCallerOfAMergeTakesItsOwnCopyOfTheOldGroup()
        {
            // Combine now empties the group it merges in, so the list the caller was about to walk
            // has to be a copy taken first - otherwise the recompute of the moved entities' own
            // adjacency flags never runs.
            var update = MethodBody(Entity(), "public void UpdateDynamicMultitile(VMContext context)");

            Assert.True(update.Contains("new List<VMEntity>(ent.MultitileGroup.Objects)"),
                "VMEntity.UpdateDynamicMultitile hands Combine the live list and then walks it;"
                + " Combine empties that list now, so the walk has to be over a copy.");
        }

        // ---- entity load paths (E-02, E-06, E-13) -------------------------------------------

        [Fact]
        public void AMissingMultitileMasterIsReportedRatherThanDereferenced()
        {
            var load = MethodBody(Entity(), "public virtual void Load(VMEntityMarshal input)");

            Assert.True(load.Contains("ResolveMaster"),
                "VMEntity.Load no longer resolves the master through the shared rule.");
            Assert.True(load.Contains("masterDef == null"),
                "VMEntity.Load dereferences the master with no null check, so a save naming an"
                + " uninstalled multitile master still kills the lot.");

            var window = Window(load, "var masterDef = ResolveMaster(", "ContainerSlot = input.ContainerSlot;");
            Assert.True(window.IndexOf("masterDef == null", StringComparison.Ordinal)
                        < window.IndexOf("UseTreeTableOf(masterDef)", StringComparison.Ordinal),
                "UseTreeTableOf runs before the missing-master check in VMEntity.Load, and it"
                + " dereferences the master too.");
        }

        [Fact]
        public void TheLockoutCountReadDoesNotReachThroughTheThreadBeforeTestingIt()
        {
            // The old code dereferenced Thread.Context two lines above the Thread == null test that
            // was supposed to cover it, and every ghost image - and every entity between
            // construction and Init - has no thread.
            var window = Window(Entity(), "case VMStackObjectVariable.LockoutCount:", "case VMStackObjectVariable.CurrentValue:");

            Assert.DoesNotContain("Thread.Context", window);
            Assert.Contains("CurrentTickID", window);
        }

        [Fact]
        public void NoSetValueCaseDereferencesTheThreadWithoutATest()
        {
            // The same shape in three more places: the Burning reset, the "safe to delete" query,
            // and the outfit change. Each was a guard the code below it had to be able to rely on.
            var setValue = MethodBody(Entity(), "public virtual bool SetValue(VMStackObjectVariable var, short value)");

            Assert.True(setValue.Contains("this.Reset(Thread?.Context)"),
                "the Burning reset is unguarded again.");
            Assert.True(setValue.Contains("Thread == null || IsInUse(Thread.Context"),
                "the safe-to-delete query is unguarded again.");
        }

        [Fact]
        public void AHeadlineFromASaveDoesNotRequireAHeadlineProvider()
        {
            // VM.Headline is supplied by the host. A dedicated server, the sandbox and a test VM all
            // have none, and LoadCrossRef ran on every save load.
            var crossRef = MethodBody(Entity(), "public virtual void LoadCrossRef(VMEntityMarshal input, VMContext context)");

            Assert.Contains("context.VM.Headline?.Get(Headline)", crossRef);
            Assert.True(MethodBody(Avatar(), "public void ShowMoneyHeadline(int value)")
                    .Contains("VM?.Headline?.Get(Headline)"),
                "VMAvatar.ShowMoneyHeadline asks a headline provider it may not have, from a thread it"
                + " may not have - the same unguarded deref as the load path.");
        }

        [Fact]
        public void TheUseCountScanSurvivesAnAvatarThatIsNotRunning()
        {
            var getUsers = MethodBody(Entity(), "public HashSet<VMEntity> GetUsers(VMContext context, HashSet<VMEntity> users)");

            Assert.True(getUsers.Contains("Math.Min(avaThread.ActiveQueueBlock, avaThread.Queue.Count - 1)"),
                "the use-count scan bounds itself by the active block without clamping it to the"
                + " queue, so a torn save that names a missing entry throws out of a stack read.");
        }

        // ---- a reset that cannot stick (E-23) ------------------------------------------------

        [Fact]
        public void ResetAlwaysClearsItsReentrancyFlag()
        {
            // Once InReset sticks, every later Reset on that entity returns immediately - and that
            // is exactly the path VMThread.Tick's exception handler uses to recover a sim. The old
            // body returned early on a null thread without clearing it.
            var reset = MethodBody(Entity(), "public virtual void Reset(VMContext context)");

            Assert.Contains("finally", reset);
            Assert.Contains("InReset = false;", reset);
        }

        // ---- the two container implementations (E-05, E-38) ---------------------------------

        public static TheoryData<string, string> SlotMethods => new TheoryData<string, string>
        {
            { "VMAvatar", "public override VMEntity GetSlot(int slot)" },
            { "VMAvatar", "public override int GetSlotHeight(int slot)" },
            { "VMAvatar", "public override void ClearSlot(int slot)" },
            { "VMAvatar", "public override bool PlaceInSlot(VMEntity obj, int slot, bool cleanOld, VMContext context)" },
            { "VMGameObject", "public override VMEntity GetSlot(int slot)" },
            { "VMGameObject", "public override int GetSlotHeight(int slot)" },
            { "VMGameObject", "public override void ClearSlot(int slot)" },
        };

        [Theory]
        [MemberData(nameof(SlotMethods))]
        public void BothContainersGoThroughTheSameSlotRule(string owner, string signature)
        {
            // The avatar version indexed Contained directly where the object version bounds-checked
            // and null-checked; the guarded one is the template, so both now share one rule.
            var body = MethodBody(owner == "VMAvatar" ? Avatar() : GameObject(), signature);

            Assert.True(body.Contains("VMEntitySlots"),
                owner + "." + signature + " no longer goes through the shared slot rule.");
        }

        [Fact]
        public void TheAvatarRefusesToPutAnObjectInASlotItDoesNotHave()
        {
            // GetSlot returns null for a slot the avatar does not have, which is the same answer it
            // gives for an empty one - so without a separate check, the write below indexed it.
            var place = MethodBody(Avatar(), "public override bool PlaceInSlot(VMEntity obj, int slot, bool cleanOld, VMContext context)");

            Assert.Contains("IsValidIndex(Contained, slot)", place);
        }

        [Fact]
        public void AnObjectAnswersWithTheHeightOfTheSlotItWasAskedAbout()
        {
            // The bug: GetSlotHeight always read Slots.Slots[0][0] - the first slot in the object -
            // for every slot asked about. Invisible only because both callers pass 0, and it
            // dereferenced a null SLOT chunk and an empty slot list on the way.
            //
            // The read is now a named helper, so that is where the assertion is: what matters is
            // that the chunk is consulted by slot number through a lookup that can say "not
            // described", and that nothing anywhere still answers with slot 0's first entry.
            var height = MethodBody(GameObject(), "public override int GetSlotHeight(int slot)");
            var described = MethodBody(GameObject(), "private int DescribedSlotHeight(int slot)");

            Assert.Contains("ResolveSlotHeight", height);
            Assert.Contains("TryGetValue((ushort)slot", described);
            Assert.DoesNotContain("Slots.Slots[0][0]", described);

            Assert.DoesNotContain("Slots.Slots[0][0]", GameObject());
        }

        // ---- an avatar's own data (E-10, E-11, E-26, E-35, E-37, E-43, E-44) ---------------

        [Fact]
        public void AnAnimationTimePropertyIsNeverParsedWithAParserThatThrows()
        {
            Assert.False(Regex.IsMatch(Avatar(), @"\bshort\.Parse\("),
                "VMAvatar parses a time property with short.Parse again. HandleTimePropsEvent runs"
                + " from Tick, which is not inside the VM's exception funnel, so this escapes into"
                + " the caller of VM.PreDraw.");
        }

        [Fact]
        public void AnAvatarWithNoAnimationsDoesNotQueueAnAnimationEvent()
        {
            // CurrentAnimationState is documented to be null when Animations is empty, which Turn,
            // the END_TURN handler, Reset and a blank saved animation string all bring about.
            var handle = MethodBody(Avatar(), "private void HandleTimePropsEvent(TimePropertyListItem tp)");

            Assert.Contains("if (state != null)", handle);
        }

        [Fact]
        public void TheAnimationTablesAreFilledThroughTheBoundedCopy()
        {
            var init = MethodBody(Avatar(), "public void InitBodyData(VMContext context)");

            Assert.True(init.Contains("CopyAnimationStrings"),
                "InitBodyData fills the fixed 50-slot tables by hand again, so a global STR longer"
                + " than 50 entries is a lot-load crash.");
            Assert.True(init.Contains("if (GlobWalk != null)") && init.Contains("if (GlobSwim != null)"),
                "InitBodyData dereferences the global walk/swim tables without a null check.");
        }

        [Fact]
        public void EveryPersonDataCaseStoresTheValue()
        {
            // The bug: the RenderDisplayFlags case ran its change handler and returned, so person
            // data 74 never reached the array and never reached the save. Invisible in retail
            // because the only writer of 74 in the whole install re-asserts its own value.
            var window = Window(Avatar(), "case VMPersonDataVariable.RenderDisplayFlags:", "case VMPersonDataVariable.SkillLock:");

            Assert.DoesNotContain("return", window);
        }

        [Fact]
        public void InheritingANeighbourDoesNotSwapThePersonDataArrayOut()
        {
            var inherit = MethodBody(Avatar(), "public void InheritNeighbor(Neighbour neigh, FAMI current)");

            Assert.Contains("CopyPersonData(PersonData, neigh.PersonData)", inherit);
            Assert.False(Regex.IsMatch(inherit, @"PersonData\s*=\s*neigh\.PersonData"),
                "VMAvatar.InheritNeighbor replaces its whole person-data array with the"
                + " neighbour's, so a short neighbour array leaves later reads out of range.");
        }

        [Fact]
        public void TheMotiveRateTableIsTickedOverItsOwnLength()
        {
            // The deserialiser does not floor this array, so a save with a short motive table used
            // to throw on every tick of every avatar. VMMotiveChange.ClearOne already bounds-checks;
            // the other three consumers did not.
            var tick = MethodBody(Avatar(), "public override void Tick()");

            Assert.True(tick.Contains("MotiveChanges?.Length ?? 0"),
                "VMAvatar.Tick walks sixteen motive entries regardless of how many the avatar has.");
            Assert.False(Regex.IsMatch(tick, @"MotiveChanges\[i\]\s*\.Tick\("),
                "VMAvatar.Tick dereferences a motive entry with no null or length check again.");
        }

        [Fact]
        public void TheRenderBlendIsNotABareDivision()
        {
            var fractional = MethodBody(Avatar(), "public void FractionalAnim(float fraction)");

            Assert.True(fractional.Contains("BlendWeight"),
                "VMAvatar.FractionalAnim divides a state weight by the total again, so a 0/0 blend"
                + " is NaN on the way into the animator.");
        }

        [Fact]
        public void LeavingTheLotAsksTheQueueOnce()
        {
            // The old predicate called Thread.Queue.IndexOf(x) for every entry, and read
            // x.ActionRoutine for a queue entry whose routine never resolved.
            var leave = MethodBody(Avatar(), "public void UserLeaveLot()");

            Assert.False(Regex.IsMatch(leave, @"IndexOf\("),
                "VMAvatar.UserLeaveLot searches the queue with IndexOf inside its own predicate"
                + " again, which is quadratic in the queue length.");
            Assert.True(leave.Contains("thread == null"),
                "VMAvatar.UserLeaveLot dereferences a thread it does not have again.");
        }

        // ---- helpers -----------------------------------------------------------------------

        /// <summary>A source file with its comments removed, so prose cannot satisfy an assertion.</summary>
        private static string Avatar() => Source("VMAvatar.cs");
        private static string Entity() => Source("VMEntity.cs");
        private static string GameObject() => Source("VMGameObject.cs");
        private static string Multitile() => Source("VMMultitileGroup.cs");

        private static string Source(string fileName)
        {
            var path = Path.Combine(new[]
            {
                FreeSoDir(), "FreeSO", "TSOClient", "tso.simantics", "Entities", fileName,
            });
            Assert.True(File.Exists(path), "could not find " + path);

            var text = File.ReadAllText(path);
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

        /// <summary>
        /// A C# method body, located by its opening anchors. The anchors are matched
        /// <em>in order</em>, so a signature that is a prefix of another cannot satisfy both.
        /// </summary>
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

        /// <summary>
        /// The text between two markers, exclusive. For a construct that has no braces of its own -
        /// a switch case body, say - so the next brace in the file belongs to something else.
        /// </summary>
        private static string Window(string source, string from, string to)
        {
            var start = source.IndexOf(from, StringComparison.Ordinal);
            Assert.True(start >= 0, "no '" + from + "' in the source under test");
            start += from.Length;

            var end = source.IndexOf(to, start, StringComparison.Ordinal);
            Assert.True(end >= 0, "no '" + to + "' after '" + from + "'");

            return source.Substring(start, end - start);
        }
    }
}
