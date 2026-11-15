using FSO.Files.Formats.IFF.Chunks;
using FSO.LotView.Model;
using FSO.SimAntics;
using FSO.SimAntics.Engine;
using FSO.SimAntics.Utils;
using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

namespace Simitone.Tests.Engine
{
    /// <summary>
    /// The engine-side tail of <c>docs/AUDIT_ENGINE.md</c> - E-09, E-12, E-14, E-16, E-29, E-30,
    /// E-31, E-42, E-47, E-48, E-49, E-50, E-51 and the E-53 dead-code rows that live in
    /// <c>Engine/</c>, <c>Primitives/</c>, <c>Utils/</c> and <c>VMContext.cs</c>.
    ///
    /// <para>
    /// They come in two kinds and both are here. The rules that are functions of a number -
    /// whether a slot's resolution can drive an enumeration, whether a route may turn at a portal,
    /// what a 64x64 house plane becomes at a lot size of 32 - are <em>run</em>, because they take
    /// their inputs as parameters precisely so that no lot, no content manager and no graphics
    /// device is needed. Everything else is a decision about how a method is written, and a test
    /// process cannot stand up a VM thread, route a sim to a chair or load a house file, so those
    /// assertions read the source - the same trade <c>VMEntitiesWiringTests</c> makes.
    /// </para>
    ///
    /// <para>
    /// The source assertions are deliberately coarse: a call's name appearing in the right method,
    /// a list walk not becoming an index, a restore not living outside a <c>finally</c>. They
    /// would not survive a rename, which is the intended trade - a rename fails loudly and a human
    /// reads the message, where a silently detached guard fails in a playtest. Comments are
    /// stripped before any of this, so a paragraph explaining a fix cannot satisfy an assertion
    /// about it.
    /// </para>
    /// </summary>
    public sealed class VMEngineWiringTests
    {
        // ====================================================================================
        // E-14 · a SLOT resolution that cannot drive the enumeration
        // ====================================================================================

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void ASlotWithNoUsableResolutionIsRefusedRatherThanDividedBy(int resolution)
        {
            // SLOT.Resolution is a field of the SLOT chunk, read straight out of the object
            // definition. It is the divisor that bounds the search and the increment the loop head
            // adds, so zero was a DivideByZeroException on the first routing attempt and, had it got
            // past that, `x += 0` was an infinite loop.
            var slot = new SLOTItem { Resolution = resolution, MinProximity = 1, MaxProximity = 32 };

            Assert.False(VMSlotParser.HasUsableResolution(slot));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(8)]
        [InlineData(16)]
        [InlineData(64)]
        public void ASlotWithAPositiveResolutionCanDriveTheEnumeration(int resolution)
        {
            Assert.True(VMSlotParser.HasUsableResolution(new SLOTItem { Resolution = resolution }));
        }

        [Fact]
        public void AMissingSlotIsNotAUsableResolution()
        {
            Assert.False(VMSlotParser.HasUsableResolution(null));
        }

        [Fact]
        public void TheRangeEnumerationConsultsTheGuardBeforeItDivides()
        {
            // The rule above only helps if the loop asks it first: the division and the `x +=`
            // are the two lines the guard exists to keep unreachable.
            var body = MethodBody(SlotParser(), "private void SLOTEnumerationFunction (Callback<int, int, double> outputFunc)");

            var guard = body.IndexOf("HasUsableResolution(Slot)", StringComparison.Ordinal);
            var divide = body.IndexOf("MaxProximity / Slot.Resolution", StringComparison.Ordinal);

            Assert.True(guard >= 0, "VMSlotParser.SLOTEnumerationFunction no longer consults the"
                + " resolution rule, so a SLOT with a zero resolution is divided by again.");
            Assert.True(divide < 0 || guard < divide, "the resolution guard runs after the division"
                + " it is meant to keep from happening.");
        }

        // ====================================================================================
        // E-09 · the walk animation blend, and E-49 · CanPortalTurn
        // ====================================================================================

        [Theory]
        [InlineData(VMRoutingFrameState.WALKING, 0, 0, true)]
        [InlineData(VMRoutingFrameState.ROOM_PORTAL, 0, 0, true)]
        [InlineData(VMRoutingFrameState.ROOM_PORTAL, 0, 1, false)]
        [InlineData(VMRoutingFrameState.ROOM_PORTAL, 7, 7, false)]
        [InlineData(VMRoutingFrameState.END_TURN, 3, 0, true)]
        [InlineData(VMRoutingFrameState.INITIAL, 2, 2, true)]
        public void APortalRouteMayTurnOnceAndAnyOtherRouteMayTurnAlways(
            VMRoutingFrameState state, int calls, int turnsTaken, bool expected)
        {
            // A route that is walking through a portal may turn onto the far side once. A route
            // that is not may turn whenever it likes - which is the half the old expression got
            // right and the half its `PortalTurns++` was destroying by counting calls.
            var portalCalls = calls;
            var actual = turnsTaken;

            Assert.Equal(expected, VMRoutingFrame.AllowsPortalTurn(state, ref portalCalls, ref actual));
            //and asking never changes the answer, whether or not it is allowed.
            Assert.Equal(expected, VMRoutingFrame.AllowsPortalTurn(state, ref portalCalls, ref actual));
        }

        [Fact]
        public void AskingForAPortalTurnCountsTheAskAndNotTheTurn()
        {
            // Two questions, one number each - and the split is the fix. `PortalTurns` is a count of
            // the times the question was *asked* while in a portal, because that is what
            // AttemptWalk's `parent.PortalTurns > 0` reads: "has this route been through a portal
            // at all", which has to be true for a route that crossed a doorway without turning.
            // `PortalTurnsActual` is a count of turns actually started, because that is what the
            // predicate is about.
            var calls = 0;
            var turns = 0;

            //asked twice in a portal, no turn taken either time.
            Assert.True(VMRoutingFrame.AllowsPortalTurn(VMRoutingFrameState.ROOM_PORTAL, ref calls, ref turns));
            Assert.True(VMRoutingFrame.AllowsPortalTurn(VMRoutingFrameState.ROOM_PORTAL, ref calls, ref turns));
            Assert.Equal(2, calls);
            Assert.Equal(0, turns);

            //outside a portal the old `rf.PortalTurns++ == 0` short circuited before the
            //increment, and it has to keep doing that: a route that never meets a portal has to
            // come out of this at zero, or every route it parents pins its entry direction.
            Assert.True(VMRoutingFrame.AllowsPortalTurn(VMRoutingFrameState.WALKING, ref calls, ref turns));
            Assert.Equal(2, calls);

            //the turn happens, and now it cannot happen again.
            Assert.True(VMRoutingFrame.AllowsPortalTurn(VMRoutingFrameState.ROOM_PORTAL, ref calls, ref turns));
            turns++;
            Assert.False(VMRoutingFrame.AllowsPortalTurn(VMRoutingFrameState.ROOM_PORTAL, ref calls, ref turns));
            //...but the asks kept being counted, which is the number the entry-direction pin reads.
            Assert.Equal(4, calls);
        }

        [Fact]
        public void ARouteThatIsNotAPortalLegNeverCountsAnAsk()
        {
            var calls = 0;
            var turns = 0;
            for (int i = 0; i < 30; i++)
            {
                Assert.True(VMRoutingFrame.AllowsPortalTurn(VMRoutingFrameState.WALKING, ref calls, ref turns));
            }

            Assert.Equal(0, calls);
            Assert.Equal(0, turns);
        }

        [Theory]
        [InlineData(VMRoutingFrameState.ROOM_PORTAL, 0, true)]
        [InlineData(VMRoutingFrameState.ROOM_PORTAL, 1, true)]
        [InlineData(VMRoutingFrameState.ROOM_PORTAL, 9, true)]
        [InlineData(VMRoutingFrameState.WALKING, 0, false)]
        [InlineData(VMRoutingFrameState.WALKING, 1, true)]
        [InlineData(VMRoutingFrameState.INITIAL, 4, true)]
        [InlineData(VMRoutingFrameState.BEGIN_TURN, 1, true)]
        [InlineData(VMRoutingFrameState.TURN_ONLY, 1, true)]
        public void ARouteThatHasBeenThroughAPortalPinsItsPathEntry(
            VMRoutingFrameState parentState, int parentCalls, bool expected)
        {
            // "Has this route already gone through a portal, so pin the path's entry direction."
            // It does not mean "has it turned": the doorway walk that happened to already be facing
            // the right way is the common case, and it is exactly the one that lost its pin when
            // the counter was redefined to mean turns. Without the pin the sim walks in facing an
            // arbitrary direction, the chair slot is approached from the wrong notch,
            // VMSlotParser finds no reachable location, and the eat takes the route-fail path.
            Assert.Equal(expected, VMRoutingFrame.PinsEntryDirection(parentState, parentCalls));
        }

        [Fact]
        public void ARouteWithNoPortalHistoryDoesNotPinItsPathEntry()
        {
            // The other half, so the pin cannot become unconditional: a first walk with no portal
            // behind it is free to pick its own approach.
            Assert.False(VMRoutingFrame.PinsEntryDirection(VMRoutingFrameState.INITIAL, 0));
            Assert.False(VMRoutingFrame.PinsEntryDirection(VMRoutingFrameState.WALKING, 0));
            Assert.False(VMRoutingFrame.PinsEntryDirection(VMRoutingFrameState.BEGIN_TURN, 0));
        }

        [Fact]
        public void ThePortalTurnPredicateIsAskedOfTheRouteAndBothCountersAreToldApart()
        {
            // CanPortalTurn is a thin wrapper now, and what it must not be is the place the turn is
            // recorded - that stays CountPortalTurn, and it stays the only writer of
            // PortalTurnsActual.
            var body = MethodBody(RoutingFrame(), "private bool CanPortalTurn()");

            Assert.Contains("AllowsPortalTurn(", body);
            Assert.Contains("ref frame.PortalTurns", body);
            Assert.Contains("ref frame.PortalTurnsActual", body);
            Assert.DoesNotContain("++", body);

            var count = MethodBody(RoutingFrame(), "private void CountPortalTurn()");
            Assert.Contains("PortalTurnFrame().PortalTurnsActual++;", count);
            Assert.DoesNotContain("PortalTurns++;", count);
        }

        [Fact]
        public void ThereIsExactlyOnePlaceInTheFrameThatCountsAPortalTurnAndOneThatCountsTheAsks()
        {
            // `PortalTurnsActual` is what the predicate reads, so it can only be written where a turn
            // is started - twice, in BeginWalk and EndWalk. `PortalTurns` is the saved field and the
            // one AttemptWalk reads, so it can only be written inside the rule, which is the one
            // place every ask goes through.
            var source = RoutingFrame();

            Assert.Single(Regex.Matches(source, @"PortalTurnsActual\+\+"));
            Assert.Empty(Regex.Matches(source, @"(?<!Actual)PortalTurns\+\+"));
            Assert.Equal(2, Regex.Matches(source, @"CountPortalTurn\(\);").Count);
        }

        [Fact]
        public void APortalTurnIsCountedWhereATurnIsActuallyStarted()
        {
            // Not in the predicate. `PortalTurnsActual` is the number `AllowsPortalTurn` reads, and
            // it can only mean something if it is a count of turns.
            var begin = MethodBody(RoutingFrame(), "private void BeginWalk()");
            var end = MethodBody(RoutingFrame(), "private bool EndWalk()");

            Assert.Contains("CountPortalTurn();", begin);
            Assert.Contains("CountPortalTurn();", end);
        }

        [Fact]
        public void TheEntryDirectionPinReadsTheCallCountAndThePredicateReadsTheTurnCount()
        {
            // The wiring that makes the split mean anything: if AttemptWalk read
            // PortalTurnsActual, or the predicate read PortalTurns, the two fields would be
            // interchangeable and the split would be decoration.
            var attempt = MethodBody(RoutingFrame(), "private bool AttemptWalk()");

            Assert.Contains("PinsEntryDirection(parent.State, parent.PortalTurns)", attempt);
            Assert.DoesNotContain("PortalTurnsActual", attempt);

            var rule = MethodBody(RoutingFrame(),
                "public static bool AllowsPortalTurn(VMRoutingFrameState state, ref int portalCalls, ref int portalTurnsTaken)");
            Assert.Contains("portalTurnsTaken == 0", rule);
            Assert.Contains("portalCalls++", rule);
        }

        [Fact]
        public void ARestoredRouteDoesNotGetASecondPortalTurn()
        {
            // Only the call count is on the wire, and it is the count of asks - so a restored route
            // cannot know how many of them turned. "At least one ask in a portal" is exactly the
            // condition the old single counter encoded, so answering 1 reproduces the old predicate
            // for every save rather than handing a mid-walk route a turn it already spent.
            var load = MethodBody(RoutingFrame(),
                "public override void Load(VMStackFrameMarshal input, VMContext context)");

            Assert.Contains("PortalTurns = inR.PortalTurns;", load);
            Assert.Contains("PortalTurnsActual = inR.PortalTurns > 0 ? 1 : 0;", load);
        }

        [Fact]
        public void BothWalkAnimationBranchesAskForTheAnimationsTheyAreAboutToIndex()
        {
            // The blend is three fixed writes into avatar.Animations[0..2]. The list is cleared by
            // Turn, by the END_TURN handler and by VMAvatar.Reset, and it is empty from the start
            // for any avatar a TS1 save gave a blank animation string. Only the WaitTime branch
            // used to call StartWalkAnimation first, and the asymmetry between the two branches is
            // the whole evidence that the other one was missing it.
            var tick = MethodBody(RoutingFrame(), "public VMPrimitiveExitCode Tick()");

            var writes = Regex.Matches(tick, @"avatar\.Animations\[[012]\]\.Weight").Count;
            Assert.Equal(6, writes);

            var guards = Regex.Matches(tick, @"EnsureWalkAnimations\(avatar\)").Count;
            Assert.Equal(2, guards);
        }

        [Fact]
        public void BothWalkAnimationWritesAreGuardedInTheBranchThatHoldsThem()
        {
            var tick = MethodBody(RoutingFrame(), "public VMPrimitiveExitCode Tick()");

            // In order, the six writes are: three in the WaitTime branch, three in WALKING. The
            // guard has to precede each group, and one guard at the top of the method would not do:
            // Turn clears the list, and the END_TURN branch runs in between.
            var firstGuard = tick.IndexOf("EnsureWalkAnimations(avatar)", StringComparison.Ordinal);
            var walkBranch = tick.IndexOf("case VMRoutingFrameState.WALKING:", StringComparison.Ordinal);
            var lastGuard = tick.LastIndexOf("EnsureWalkAnimations(avatar)", StringComparison.Ordinal);
            var writes = Regex.Matches(tick, @"avatar\.Animations\[[012]\]\.Weight").Cast<Match>().ToArray();

            Assert.True(firstGuard >= 0 && lastGuard > firstGuard, "both branches have to reach the"
                + " same guard, and there has to be a second one to do it.");
            Assert.True(firstGuard < walkBranch, "the WALKING branch's first weight write is no longer"
                + " preceded by the guard that gives the blend three animations to write to.");
            Assert.True(lastGuard < writes[3].Index, "the WALKING branch's three weight writes are"
                + " no longer preceded by the guard that gives them three animations to write to.");
        }

        // ---- E-42 · an obstacle per candidate slot, and E-49 · mutable statics ---------------

        [Fact]
        public void ADestinationTestDoesNotBuildItsOwnObstacleBox()
        {
            // Runs from VMSlotParser.VerifyAndAddLocation for every candidate slot of every
            // routing frame of every avatar: a route to a chair with twenty candidates and eight
            // avatars on the lot is a hundred and sixty boxes, plus a hundred and sixty Points.
            var body = MethodBody(RoutingFrame(), "public bool IntersectsOurDestination(VMFindLocationResult target)");

            Assert.DoesNotMatch(@"var\s+\w+\s*=\s*new VMObstacle\(", body);
            Assert.Contains("DestinationObstacle", body);
        }

        [Fact]
        public void TheRoutingTuningValuesAreConstantsRatherThanMutableStatics()
        {
            var source = RoutingFrame();

            foreach (var name in new[] { "ROUTE_FAIL_TREE", "GOTO_GUID", "SHOO_INTERACTION", "SHOO_TREE", "WAIT_TIMEOUT", "MAX_RETRIES" })
            {
                Assert.False(Regex.IsMatch(source, @"private\s+static\s+\w+\s+" + name + @"\b"),
                    name + " is a mutable static again. It is a tuning value with no writer, which is"
                    + " a `const` with extra steps and a name that invites an assignment nobody wanted.");
            }

            // DEBUG_DRAW is the exception and stays a field: it is a public debug switch, and a
            // `readonly` one could never be turned on.
            Assert.Contains("public static bool DEBUG_DRAW", source);
        }

        // ====================================================================================
        // E-16 · two unguarded avatar casts in the queue
        // ====================================================================================

        [Fact]
        public void EndingAnInteractionNeverCastsTheEntityToAnAvatar()
        {
            // The block above already tested `Entity is VMAvatar` for two statements; the priority
            // write sat two lines below it and outside, and cast. EndCurrentInteraction is reached
            // from AbortCurrentInteraction and from the cancel path, and EnqueueAction reaches
            // EvaluateQueuePriorities - so a game object with a queued interaction threw.
            //
            // The shape asserted is the one that cannot drift: the entity is bound once, near the
            // top, and there is no cast anywhere in the method for a future statement to reach past.
            var body = MethodBody(Thread(), "private void EndCurrentInteraction()");

            Assert.Contains("Entity as VMAvatar", body);
            Assert.DoesNotContain("(VMAvatar)Entity", body);
        }

        [Fact]
        public void QueuePrioritiesAreNotEvaluatedForAnEntityThatIsNotAnAvatar()
        {
            var body = MethodBody(Thread(), "private void EvaluateQueuePriorities()");

            var test = body.IndexOf("Entity is not VMAvatar", StringComparison.Ordinal);
            var read = body.IndexOf("GetPersonData(VMPersonDataVariable.Priority)", StringComparison.Ordinal);

            Assert.True(test >= 0, "EvaluateQueuePriorities reads person data off whatever the thread"
                + " belongs to again. The current priority is an avatar-only value and the method is"
                + " reached from EnqueueAction, which a game object can be the subject of.");
            Assert.True(read < 0 || test < read, "the avatar test has to come before the person-data"
                + " read, not after it.");
            Assert.DoesNotContain("(VMAvatar)Entity", body);
        }

        // ====================================================================================
        // E-50 · RunInMyStack's early path, and the E-53 CA2200 rethrows
        // ====================================================================================

        [Fact]
        public void AThreadIsGivenBackWholeOrNotAtAll()
        {
            // The early return put back the stack and the queue and left IsCheck true for good.
            // VMThread.Tick returns immediately at `if (IsCheck) return;`, so the sim's main tree
            // never ran again - from a bhav that happened to push nothing.
            var body = MethodBody(Thread(), "public bool RunInMyStack(VMRoutine routine, GameObject CodeOwner, short[] passVars, VMEntity stackObj)");

            var restoreAt = body.IndexOf("finally", StringComparison.Ordinal);
            Assert.True(restoreAt >= 0, "RunInMyStack has no finally, so a child tree that throws"
                + " or a bhav that pushes nothing hands the thread back in pieces.");

            foreach (var field in new[] { "Stack = OldStack;", "Queue = OldQueue;", "IsCheck = OldCheck;", "ActiveQueueBlock = OldQueueBlock;" })
            {
                Assert.True(body.IndexOf(field, StringComparison.Ordinal) > restoreAt,
                    "RunInMyStack restores '" + field + "' outside the finally. It saved all four"
                    + " together and every way out has to give all four back.");
            }
        }

        [Fact]
        public void AChildQueueThatWasJustCreatedDoesNotGetItsOwnFirstEntryAddedToIt()
        {
            // `Queue = new List<VMQueuedAction>(); if (Queue.Count > 0) Queue.Add(Queue[0]);` - the
            // condition is false two lines after the assignment, always. It only said twice that the
            // child queue is empty.
            var body = MethodBody(Thread(), "public bool RunInMyStack(VMRoutine routine, GameObject CodeOwner, short[] passVars, VMEntity stackObj)");

            Assert.DoesNotContain("if (Queue.Count > 0) Queue.Add(Queue[0]);", body);
        }

        [Fact]
        public void AnEmptyChildBhavIsAnsweredRatherThanIndexedOffTheStack()
        {
            // VMThread.Push refuses a routine with no instructions, so ExecuteSubRoutine can leave
            // the child stack empty and there is no caller to take back off. The line after the
            // early return above assumes there is one.
            var body = MethodBody(Thread(), "public bool RunInMyStack(VMRoutine routine, GameObject CodeOwner, short[] passVars, VMEntity stackObj)");

            var asked = body.IndexOf("if (Stack.Count > 0) Stack.RemoveAt(0);", StringComparison.Ordinal);
            Assert.True(asked >= 0, "RunInMyStack removes the caller off the child stack without"
                + " asking whether the bhav pushed anything.");
        }

        [Fact]
        public void AThreadAbortIsRethrownWithItsStackTraceIntact()
        {
            // CA2200: `throw e` resets the stack trace of the exception being rethrown, which is the
            // opposite of what a handler that rethrows a ThreadAbortException is for.
            Assert.DoesNotContain("throw e;", Thread());
        }

        // ====================================================================================
        // E-51 · hitParentEnd, assigned in two branches and read in one of them
        // ====================================================================================

        [Fact]
        public void ThePushTailBranchIsNotACopyOfTheBranchThatReadsItsParentEndFlag()
        {
            var enqueue = MethodBody(Thread(), "public void EnqueueAction(VMQueuedAction invocation)");

            // Anchors have to survive comment stripping, so neither of them names an `else` and the
            // general branch is opened on the one statement that is now unique to it.
            var tail = Window(enqueue, "TTABFlags.FSOPushTail | leapfrog)) > 0", "bool hitParentEnd = (invocation.Mode != VMQueueMode.ParentIdle);");
            var general = Window(enqueue, "bool hitParentEnd = (invocation.Mode != VMQueueMode.ParentIdle);", "EvaluateQueuePriorities();");

            // It is the evidence, not the defect: a local that is assigned and never read is a
            // branch that was written as a copy of the one below and then trimmed, and the trim is
            // the bug - this walk used to step straight past the VMQueueMode.ParentExit entries
            // the general walk stops at.
            Assert.DoesNotContain("hitParentEnd", tail);
            Assert.Contains("hitParentEnd", general);
        }

        [Fact]
        public void TheGeneralQueueWalkStillStopsAtAParentExit()
        {
            var enqueue = MethodBody(Thread(), "public void EnqueueAction(VMQueuedAction invocation)");

            Assert.Contains("Queue[i].Mode == VMQueueMode.ParentExit", enqueue);
            Assert.Contains("hitParentEnd = true;", enqueue);
        }

        // ====================================================================================
        // E-48 · two magic "not out of world" room numbers
        // ====================================================================================

        [Fact]
        public void TheNotOutOfWorldRoomIsOneConstantAndItsSetterFormIsDerivedFromIt()
        {
            // Two sites said this and both wrote the number. SetRoom stores `room - 1` in object
            // data, so the setter's argument is one below the value the renderer reads - which is
            // why it is written as a subtraction rather than as a second literal somebody has to
            // keep in step by hand.
            Assert.Equal((ushort)65535, VMContext.RoomNotInWorld);
            Assert.Equal(VMContext.RoomNotInWorld - 1, VMContext.RoomNotInWorldSetter);
            Assert.Equal((ushort)65534, VMContext.RoomNotInWorldSetter);
        }

        [Fact]
        public void AGroupOfGhostsSaysNotOutOfWorldThroughTheNamedConstant()
        {
            var ghost = MethodBody(Context(), "public VMMultitileGroup GhostCopyGroup(VMMultitileGroup group)");

            Assert.Contains("SetRoom(RoomNotInWorldSetter)", ghost);
            Assert.DoesNotMatch(@"SetRoom\(\s*\d+\s*\)", ghost);
        }

        // ====================================================================================
        // E-12, E-29, E-30, E-31, E-47 · the older TS1 activator
        // ====================================================================================

        [Fact]
        public void TheRetailLoaderCopiesTheSimisGlobalsRatherThanAdoptingThem()
        {
            // `simi` is also handed to VM.TS1State.SimulationInfo, and VMTS1LotState.UpdateSIMI
            // writes that chunk back out on save - so aliasing it put the engine-owned game edition
            // word inside the lot's own file and shipped it back out with the three writes below.
            var load = MethodBody(LegacyActivator(), "public Blueprint LoadFromIff(IffFile iff)");

            Assert.Contains("(short[])simi.GlobalData.Clone()", load);
            Assert.DoesNotContain("VM.GlobalState = simi.GlobalData;", load);
        }

        [Fact]
        public void TheRetailLoaderPublishesTheGameEditionThroughTheConstantThatPinsIt()
        {
            // The two TS1 load paths had drifted here: the marshal path hardcoded nothing and used
            // the constant, this one hardcoded 255 in one place and 20 in another. Changing
            // VM.GameEditionGlobalValue has to move both, and the constant is pinned by
            // VMGameEditionGlobalTests.
            var load = MethodBody(LegacyActivator(), "public Blueprint LoadFromIff(IffFile iff)");

            Assert.Contains("VM.GlobalState[VM.GameEditionGlobal] = VM.GameEditionGlobalValue;", load);
            Assert.DoesNotMatch(@"VM\.GlobalState\[\d+\]\s*=\s*255", load);
        }

        [Fact]
        public void TheRetailLoaderDropsTheEntityListThroughTheSharedRule()
        {
            // It used to replace Entities and the scheduler and leave ObjectsById and
            // SoundEntities alone, so GetObjectById - every script-supplied object id, every
            // find_best_object_for_function, the relationship subsystem - answered from the previous
            // lot's entities for the lifetime of the VM, and the first AddEntity of the new lot
            // could be handed an id the stale map already had.
            var load = MethodBody(LegacyActivator(), "public Blueprint LoadFromIff(IffFile iff)");

            Assert.Contains("VM.ResetEntityCollections();", load);
            Assert.DoesNotContain("VM.Entities = new List<VMEntity>();", load);
            Assert.DoesNotContain("VM.Scheduler = new Engine.VMScheduler(VM);", load);
        }

        [Fact]
        public void EveryLoadPathDropsTheEntityListThroughTheSameRule()
        {
            // One rule, three callers, so the next load path cannot hand-pick its own subset again.
            Assert.Contains("ResetEntityCollections();", MethodBody(VM(), "public void LoadAsync(VMMarshal input)"));

            var rule = MethodBody(VM(), "public void ResetEntityCollections()");
            foreach (var member in new[] { "SoundEntities = new HashSet<VMEntity>();", "Entities = new List<VMEntity>();", "Scheduler.Reset();", "ObjectsById = new Dictionary<short, VMEntity>();", "ObjectId = 1;" })
            {
                Assert.Contains(member, rule);
            }
        }

        [Fact]
        public void AnObjectsPositionIsNotDecidedByWhetherTwoOfItsCoordinatesAreNonZero()
        {
            // ArryX and ArryY default to 0 for an instance the ARRY 3/103 plane never mentioned,
            // and "both non-zero" was standing in for "the plane gave this object a tile". The
            // origin tile is inside the buildable area, so a lot object that legitimately sits
            // there was left OUT_OF_WORLD - invisible and unroutable.
            var load = MethodBody(LegacyActivator(), "public Blueprint LoadFromIff(IffFile iff)");

            Assert.False(Regex.IsMatch(load, @"ArryX\s*!=\s*0\s*&&\s*obj\.ArryY\s*!=\s*0"),
                "the TS1 retail loader asks whether an object's coordinates are non-zero instead of"
                + " whether it has a position.");

            // both placement sites, not just the first
            Assert.Equal(2, Regex.Matches(load, @"positioned\.Contains\(obj\.ObjectID\)").Count);
            Assert.Contains("positioned.Add(targ.ObjectID);", load);
        }

        [Fact]
        public void TheParentChainWalkTerminatesEvenWhenTheSaveDoesNot()
        {
            // The walk's own exit condition is a ParentID of 0, and a save with a cycle in that
            // field never reaches it: no exception, no log, and a lot load that does not come back.
            var load = MethodBody(LegacyActivator(), "public Blueprint LoadFromIff(IffFile iff)");

            var walk = Window(load, "MappedObject src = obj;", "LotTilePos pos = LotTilePos.OUT_OF_WORLD;");

            Assert.Contains("hops++ < objm.ObjectData.Count", walk);
            Assert.Contains("if (!followedParents.Add(src.ParentID)) break;", walk);
        }

        [Fact]
        public void EveryHousePlaneIsReadThroughTheGuardThatPadsAMissingOne()
        {
            // ARRY 8 - the fine-buildable flags plane - was dereferenced with no check on the line
            // under a null check of ARRY 11, and it is the chunk a hand-built or third-party house
            // file is most likely to be missing. A missing plane is a degraded lot; a lot that does
            // not open is not an answer.
            var load = MethodBody(LegacyActivator(), "public Blueprint LoadFromIff(IffFile iff)");

            // The two object planes are the exception and stay a bare read: DecodeObjID answers
            // null for a missing plane and the loop that consumes it skips a null, so "no object
            // plane" is already the same as "no object has a position".
            var bare = Regex.Matches(load, @"iff\.Get<ARRY>\([^)]*\)\.TransposeData");
            Assert.Empty(bare);

            Assert.Contains("VMTS1LotLoadGuard.ReadArry(iff, 8)", load);
            Assert.Contains("VMTS1LotLoadGuard.SharedLength(pools, water)", load);
        }

        [Fact]
        public void ThereIsOneResizePassAndAllFourPlanesGoThroughIt()
        {
            // Four byte-for-byte copies of the same nested loop, with a different element type, and
            // all four of them returning the 64x64 source unchanged for `size >= 64` - so a lot
            // larger than 64 got a plane whose length did not match the architecture it was
            // installed into.
            var source = LegacyActivator();

            var wrappers = new[]
            {
                "public static bool[] ResizeFlags(byte[] flags, int size)",
                "public static FloorTile[] ResizeFloors(FloorTile[] floors, int size)",
                "public static WallTile[] ResizeWalls(WallTile[] walls, int size)",
                "public static byte[] ResizeGrass(byte[] data, int size)",
            };

            foreach (var signature in wrappers)
            {
                var body = MethodBody(source, signature);

                Assert.Contains("ResizePlane(", body);
                Assert.DoesNotContain("if (size >= 64)", body);
            }

            Assert.Single(Regex.Matches(source, @"public static \w+\[\] ResizePlane<"));
        }

        // ---- E-24 · one argument/locals rule, three call sites -----------------------------

        [Fact]
        public void EveryFrameArrayIsSizedThroughTheOneHelperThatStatesTheRule()
        {
            // The finding was a rule written twice and disagreeing: Push sized Locals as
            // max(Locals, Arguments) and Load sized it as Locals, and a third copy of the
            // max(Arguments, 4) argument rule sat in ExecuteSubRoutine. A helper that only two of
            // the three callers use is the drift the finding is about, arriving again.
            var push = MethodBody(Thread(), "public bool Push(VMStackFrame frame)");
            var sub = MethodBody(Thread(), "public void ExecuteSubRoutine(VMStackFrame frame, VMRoutine routine, GameObject codeOwner, VMSubRoutineOperand args)");
            var load = MethodBody(Source("Engine", "VMStackFrame.cs"), "public virtual void Load(VMStackFrameMarshal input, VMContext context)");

            Assert.Contains("VMStackFrame.SizeLocals(", push);
            Assert.Contains("VMStackFrame.SizeArgs(", sub);
            Assert.Contains("Locals = SizeLocals(", load);
            Assert.Contains("Args = SizeArgs(", load);

            // and no hand-rolled copy of either rule is left to drift
            Assert.DoesNotMatch(@"new\s+short\[\s*\(\s*\w+\.Arguments\s*>\s*4\s*\)", sub);
            Assert.DoesNotContain("Math.Max(frame.Routine.Locals, frame.Routine.Arguments)", push);
        }

        [Fact]
        public void TheRetailLoaderHasNoUnusedCounterLeftOver()
        {
            // CS0219: `int j = 0;` in front of the OBJT naming loop, never read.
            var load = MethodBody(LegacyActivator(), "public Blueprint LoadFromIff(IffFile iff)");

            Assert.DoesNotMatch(@"\bint\s+j\s*=\s*0\s*;", load);
        }

        [Fact]
        public void BothTs1LoadersPublishTheGameEditionThroughTheSameConstant()
        {
            // The marshal path was the copy that got left behind: it hardcoded the index 20 and the
            // value 255 while the retail loader named both. VM.GameEditionGlobalValue is a pinned
            // policy rather than a tunable, and 118 shipped `Global[20] >= N` self-delete guards sit
            // on the number - so "only one of the two load paths moved" is a lot that removes its
            // own expansion catalogue, not a refactor.
            foreach (var file in new[] { "Utils/VMTS1Activator.cs", "Utils/VMTS1ActivatorNew.cs" })
            {
                var load = MethodBody(Source(file), "LoadFromIff(IffFile iff)");

                Assert.Contains("VM.GameEditionGlobal]", load);
                Assert.Contains("VM.GameEditionGlobalValue", load);
                Assert.DoesNotMatch(@"GlobalState\[\d+\]\s*=\s*255", load);
            }
        }

        // ---- E-04 · fixed-record plane decoders ------------------------------------------

        [Fact]
        public void NoPlaneDecoderIsBoundedByTheByteCountItIsWalking()
        {
            // The output array was sized `data.Length / stride` while the loop ran to `data.Length`,
            // so a plane that is not a whole number of records asked for one record more than
            // existed and then read past the end of the source. These are the decoders with more
            // than one byte per record, and the advanced ones read ARRY 11's raw TransposeData
            // rather than through the padded-plane reader, so their length is the house file's.
            var source = LegacyActivator();

            foreach (var signature in new[]
            {
                "public static FloorTile[] DecodeAdvFloors(byte[] data)",
                "public static ushort[] DecodeObjID(byte[] data)",
                "public static byte[] DecodeHeights(byte[] heights)",
                "public static WallTile[] DecodeWalls(byte[] data)",
                "public static WallTile[] DecodeAdvWalls(byte[] data)",
            })
            {
                var body = MethodBody(source, signature);

                Assert.DoesNotMatch(@"i\s*<\s*data\.Length", body);
                Assert.DoesNotMatch(@"i\s*<\s*heights\.Length", body);
                Assert.Contains("VMTS1LotLoadGuard.WholeRecords(", body);
            }
        }

        [Fact]
        public void TheTwoRemapPassesDoNotIndexAPlaneTheHouseFileMadeShorter()
        {
            // ARRY 8 is padded to 4096 by the loader and the advanced-floor plane is read raw from
            // ARRY 11, so the floor array can be the longer of the two; and the wall pass reads
            // Architecture.Floors[0] only for its diagonal fallback, for the same reason.
            var source = LegacyActivator();
            var floors = MethodBody(source, "public static FloorTile[] RemapFloors(bool flipRoad, FloorTile[] floors, Dictionary<byte, ushort> dict, byte[] flags)");
            var walls = MethodBody(source, "public static WallTile[] RemapWalls(WallTile[] walls, Dictionary<byte, ushort> dict, Dictionary<byte, ushort> floorDict, FloorTile[] floors)");

            Assert.Contains("(flags?.Length ?? 0)) ? flags[i]", floors);
            Assert.Contains("(floors?.Length ?? 0)) ? floors[i]", walls);
            // the diagonal fallback reads the bound copy, never the array by index
            Assert.DoesNotMatch(@"ushort\s+diagID\s*=\s*floors\[", walls);

            // ...and the whole array is still walked. Truncating the loop instead would silently
            // skip the road flip and the pattern remap for every tile past the shorter plane.
            Assert.Contains("i<floors.Length;i++)", floors.Replace(" ", ""));
            Assert.Contains("i<walls.Length;i++)", walls.Replace(" ", ""));
        }

        // ---- E-41 · the LINQ Count() where a List's Count property was meant --------------

        [Fact]
        public void TheRoomInfoArrayIsSizedFromTheRoomListsOwnCount()
        {
            // The E-41 typo, surviving outside the file the audit named it in - and this one is on
            // the lot-load path, in RegeneratePortalInfo.
            var context = Context();

            Assert.Contains("new VMRoomInfo[Architecture.RoomData.Count]", context);
            Assert.DoesNotContain("Architecture.RoomData.Count()", context);
        }

        [Fact]
        public void AMultitileGroupIsNeverWalkedWithTheLinqCount()
        {
            // `Objects.Count()` walked the list and boxed an enumerator on every iteration, so
            // `BaseObject` - reached from ChangePosition, SetVisualPosition and GhostCopyGroup -
            // was quadratic in the number of members.
            var group = Source("Entities", "VMMultitileGroup.cs");

            Assert.DoesNotMatch(@"Objects\.Count\(\)", group);
            Assert.Matches(@"Objects\.Count\b", group);
        }

        // ---- the resize pass, run ------------------------------------------------------

        [Fact]
        public void AFullSizedPlaneComesBackUnchanged()
        {
            // The case every retail lot takes: 64 in, 64 out, same values. `OrderBy` is not
            // available on a byte[] without a comparer, so the identity is checked entry by entry.
            var plane = new byte[64 * 64];
            for (var i = 0; i < plane.Length; i++) plane[i] = (byte)(i % 251);

            var resized = VMTS1Activator.ResizePlane(plane, 64, x => x);

            Assert.Equal(64 * 64, resized.Length);
            for (var i = 0; i < plane.Length; i++) Assert.Equal(plane[i], resized[i]);
        }

        [Fact]
        public void ASmallerLotTakesTheTopLeftBlockAndNothingElse()
        {
            // The decoders read their source at 64 wide, so a 32-tile lot is the first 32 columns
            // and the first 32 rows of the plane - not the first 1024 entries, which is what a
            // naive "take the first size*size" would give.
            var plane = new byte[64 * 64];
            for (var y = 0; y < 64; y++)
                for (var x = 0; x < 64; x++)
                    plane[x + y * 64] = (byte)((y * 64 + x) % 251);

            var resized = VMTS1Activator.ResizePlane(plane, 32, x => x);

            Assert.Equal(32 * 32, resized.Length);
            for (var y = 0; y < 32; y++)
                for (var x = 0; x < 32; x++)
                    Assert.Equal(plane[x + y * 64], resized[x + y * 32]);
        }

        [Fact]
        public void ALotLargerThanThePlaneStillGetsAPlaneItsOwnSize()
        {
            // The old short-circuit handed back 4096 entries for a 65-tile lot's architecture, and
            // the first pass to index the two together went out of range. A retail lot is 64x64, so
            // this is the hand-edited-SIMI case - but the two arrays are meant to be the same size,
            // and now they are.
            var plane = new byte[64 * 64];
            plane[0] = 7;

            var resized = VMTS1Activator.ResizePlane(plane, 65, x => x);

            Assert.Equal(65 * 65, resized.Length);
            Assert.Equal((byte)7, resized[0]);
        }

        [Fact]
        public void AMissingOrShortPlaneIsADegradedLotRatherThanAnException()
        {
            Assert.Equal(16 * 16, VMTS1Activator.ResizePlane<byte, byte>(null, 16, x => x).Length);
            Assert.All(VMTS1Activator.ResizePlane<byte, byte>(null, 4, x => x), b => Assert.Equal(0, b));

            // Ten complete rows of the 64-wide source, so ten rows are copied and the rest of the
            // lot is bare. Rows and columns are bounded separately, which is the point: clamping
            // one number by both would have dropped the right-hand columns of every row present.
            var shortPlane = new byte[64 * 10];
            shortPlane[3] = 9;        // row 0, column 3   - inside the 16-tile lot
            shortPlane[64 + 4] = 7;   // row 1, column 4   - inside
            shortPlane[64 + 20] = 5;  // row 1, column 20  - outside the lot's 16 columns
            shortPlane[9 * 64 + 2] = 6; // row 9, column 2 - the last row the plane has
            var resized = VMTS1Activator.ResizePlane(shortPlane, 16, x => x);

            Assert.Equal(16 * 16, resized.Length);
            Assert.Equal((byte)9, resized[3 + 0 * 16]);
            Assert.Equal((byte)7, resized[4 + 1 * 16]);
            Assert.Equal((byte)6, resized[2 + 9 * 16]);
            Assert.Equal((byte)0, resized[20 + 1 * 16]);  // past the lot's columns
            Assert.Equal((byte)0, resized[2 + 10 * 16]); // past the plane's rows

            var copied = new byte[] { 0, 6, 7, 9 };
            Assert.All(resized, b => Assert.Contains(b, copied));

            // A plane with no full row at all is not row-major addressable, so nothing is copied.
            var notEvenARow = new byte[63];
            notEvenARow[7] = 1;
            var empty = VMTS1Activator.ResizePlane(notEvenARow, 16, x => x);
            Assert.Equal(16 * 16, empty.Length);
            Assert.All(empty, b => Assert.Equal(0, b));
        }

        [Fact]
        public void AllFourPlanesEndUpTheSizeOfTheLot()
        {
            // The four callers are the four element types, and the reason they were one loop four
            // times. The point of the assertion is that they cannot disagree about the length.
            var size = 48;
            var flags = new byte[64 * 64];
            var floors = new FloorTile[64 * 64];
            var walls = new WallTile[64 * 64];
            var grass = new byte[64 * 64];

            var resized = new[]
            {
                VMTS1Activator.ResizeFlags(flags, size).Length,
                VMTS1Activator.ResizeFloors(floors, size).Length,
                VMTS1Activator.ResizeWalls(walls, size).Length,
                VMTS1Activator.ResizeGrass(grass, size).Length,
            };

            Assert.All(resized, length => Assert.Equal(size * size, length));
        }

        [Fact]
        public void TheFlagsPlaneIsStillTheOnlyOneThatConvertsItsEntries()
        {
            // Flag 32 is "user buildable" and everything else in the byte is about the tile's
            // shape, so this plane is the one whose entries are not their own value - and the
            // conversion is the reason the shared pass is generic in two type parameters.
            var flags = new byte[64 * 64];
            flags[0] = 32;      // buildable
            flags[1] = 0;       // not buildable
            flags[2] = 33;      // buildable, with a bit we do not read
            flags[3] = 4;

            var resized = VMTS1Activator.ResizeFlags(flags, 4);

            Assert.Equal(4 * 4, resized.Length);
            Assert.False(resized[0]);
            Assert.True(resized[1]);
            Assert.False(resized[2]);
            Assert.True(resized[3]);
        }

        // ====================================================================================
        // helpers
        // ====================================================================================

        private static string LegacyActivator() => Source("Utils", "VMTS1Activator.cs");
        private static string SlotParser() => Source("Engine", "VMSlotParser.cs");
        private static string RoutingFrame() => Source("Engine", "VMRoutingFrame.cs");
        private static string Thread() => Source("Engine", "VMThread.cs");
        private static string Context() => Source("VMContext.cs");
        private static string VM() => Source("VM.cs");
        /// <summary>
        /// A tso.simantics source file with its comments removed, so prose cannot satisfy an
        /// assertion about the code. <paramref name="path"/> is relative to the tso.simantics root.
        /// </summary>
        private static string Source(string path)
        {
            // Path.Combine has no four-argument overload, so the fixed prefix and the relative
            // path are joined first and handed to the params overload in one go.
            var segments = new[] { FreeSoDir(), "FreeSO", "TSOClient", "tso.simantics" }
                .Concat(path.Replace('/', Path.DirectorySeparatorChar).Split(Path.DirectorySeparatorChar));
            var full = Path.Combine(segments.ToArray());

            Assert.True(File.Exists(full), "could not find " + full);

            var text = File.ReadAllText(full);
            var withoutBlocks = Regex.Replace(text, @"/\*.*?\*/", " ", RegexOptions.Singleline);
            return Regex.Replace(withoutBlocks, @"//[^\n]*", " ");
        }

        private static string Source(string directory, string fileName) => Source(directory + "/" + fileName);

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
        /// A C# method body, located by its opening anchor. The anchor is matched exactly, so a
        /// signature that is a prefix of another cannot satisfy both.
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
        /// an if/else chain, say - so the next brace in the file belongs to something else.
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
