using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FSO.SimAntics.Engine;
using Simitone.Client.UI.Panels;
using Xunit;

namespace Simitone.Tests.UI
{
    /// <summary>
    /// <see cref="InteractionQueueLayout"/> - the queue display's cancel-click rule and its slot
    /// rule, both lifted out of <c>UIInteractionQueue</c>.
    ///
    /// <para>
    /// The click rule is the half of the display the player can see.
    /// <c>InteractionClicked</c> sends <c>VMNetInteractionCancelCmd</c> only when the entry is
    /// still in the queue and the engine has not already cancelled it - which is the retail gate,
    /// <c>!(NotifyIdle &amp;&amp; priority &lt;= 0)</c>. Queue UIDs are a wrapping 16-bit counter,
    /// so a cancel sent against an entry that has gone, or against one the engine is already
    /// unwinding, can land on whatever action holds that UID next. The tests below pin both of those
    /// answers, and the last one pins the wiring in the source so neither gate can come back
    /// through a copy of itself.
    /// </para>
    ///
    /// <para>
    /// The slot rule is what keeps the display honest: an entry that is gone must not be drawn, and
    /// the entry behind it has to move up rather than sit in a slot nothing else agrees is free. It
    /// used to be written out twice in <c>Update</c> - once to place the entries on screen, once to
    /// move them - and the two copies were the only definition of "visible".
    /// </para>
    ///
    /// <para>
    /// None of this needs a graphics device: the rules are about the queue's shape, which is a list
    /// of <see cref="VMQueueMode"/>s. The parts that do need one - the X fading in, the entry
    /// sliding to its new slot - are animation of a decision made here.
    /// </para>
    /// </summary>
    public sealed class InteractionQueueLayoutTests
    {
        // ---- the click -------------------------------------------------------------------------

        [Fact]
        public void AClickOnAnEntryThatIsStillQueuedSendsTheCancel()
        {
            // The ordinary case: the player clicks the X on something the sim has not started.
            Assert.Equal(InteractionQueueLayout.ClickOutcome.SendCancel,
                InteractionQueueLayout.OnEntryClicked(entryStillInQueue: true, notifyIdle: false, priority: 50));
        }

        [Fact]
        public void AClickOnAnEntryTheEngineHasAlreadyCancelledSendsNothing()
        {
            // The retail gate. `NotifyIdle` with a priority of 0 is the shape
            // `VMThread.CancelAction` leaves behind when it interrupts a queue entry rather than
            // dropping it, and there is nothing left for the sim to do about it - the player has one
            // button and it is not "cancel it again", it is the X on the entry the sim has already
            // taken in hand.
            Assert.Equal(InteractionQueueLayout.ClickOutcome.Ignore,
                InteractionQueueLayout.OnEntryClicked(entryStillInQueue: true, notifyIdle: true, priority: 0));
        }

        [Fact]
        public void TheEngineFlaggingAnEntryWithoutZeroingItsPriorityStillSendsTheCancel()
        {
            // The gate is on the *pair*, not on either half alone. `NotifyIdle` on its own is also
            // how a parent exit behind a cancelled parent idle is made to "appear like it is
            // cancelled" while it keeps a real priority, and that one has not been cancelled - it is
            // the object's release, and a cancel aimed at it still has to go through.
            Assert.Equal(InteractionQueueLayout.ClickOutcome.SendCancel,
                InteractionQueueLayout.OnEntryClicked(entryStillInQueue: true, notifyIdle: true, priority: 30));
        }

        [Fact]
        public void ACancelIsSafeToSendTwice()
        {
            // A double-click can easily be two clicks before the queue has seen the first one, so
            // the cancel is sent twice - which is not a state change, because
            // `VMThread.CancelAction` looks the UID up and does nothing when it is not in the queue.
            // Once the engine has taken the entry in hand, though, the next click stops sending.
            Assert.Equal(InteractionQueueLayout.ClickOutcome.SendCancel,
                InteractionQueueLayout.OnEntryClicked(entryStillInQueue: true, notifyIdle: false, priority: 50));
            Assert.Equal(InteractionQueueLayout.ClickOutcome.SendCancel,
                InteractionQueueLayout.OnEntryClicked(entryStillInQueue: true, notifyIdle: false, priority: 50));
            Assert.Equal(InteractionQueueLayout.ClickOutcome.Ignore,
                InteractionQueueLayout.OnEntryClicked(entryStillInQueue: true, notifyIdle: true, priority: 0));
        }

        [Fact]
        public void AClickOnAnEntryThatHasLeftTheQueueSendsNothing()
        {
            // The other thing that must not be sent, and the one the engine's own flag does not
            // cover: a queued action is gone from the queue within the tick the command lands in,
            // and the entry still on screen is the reason to check. Queue UIDs are a wrapping
            // 16-bit counter (`VMThread.ActionUID`), so a cancel sent against an entry that has
            // already gone can name a *different* action that has since been handed the same UID -
            // and the player would cancel that one instead.
            Assert.Equal(InteractionQueueLayout.ClickOutcome.Ignore,
                InteractionQueueLayout.OnEntryClicked(entryStillInQueue: false, notifyIdle: false, priority: 50));
        }

        // ---- the slots -------------------------------------------------------------------------

        [Fact]
        public void TheActiveEntryIsTheFirstSlotAndEverythingElseFollowsIt()
        {
            var slots = SlotsOf(VMQueueMode.Normal, VMQueueMode.Normal, VMQueueMode.Normal);

            Assert.Equal(new[] { 0, 1, 2 }, slots);
        }

        [Fact]
        public void AnIdleEntryIsNeverDrawn()
        {
            // The sim's own idle tree, and the hidden half of a run-immediately action, are in the
            // queue but not on screen. A slot of -1 is what keeps the second pass from adding an
            // entry for them and the first pass from moving one.
            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(new[] { VMQueueMode.Idle }, 0));
            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(
                new[] { VMQueueMode.Idle, VMQueueMode.Normal, VMQueueMode.Idle }, 2));
        }

        [Fact]
        public void AParentExitIsDrawnOnlyOnceItIsTheActiveEntry()
        {
            // A parent exit is the object's own "let the sim go" action, queued behind the
            // interaction that is using it. Showing it before then is a queue full of things the
            // player did not ask for; showing it afterwards is the point - it is a real action then.
            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(
                new[] { VMQueueMode.Normal, VMQueueMode.ParentExit }, 1));
            Assert.Equal(0, InteractionQueueLayout.DisplaySlot(
                new[] { VMQueueMode.ParentExit }, 0));
        }

        [Fact]
        public void AParentIdleBehindAnotherParentIdleIsNotDrawn()
        {
            // The idle an object pushes while an interaction on it runs. Only the first one is the
            // sim's idle; a second one belongs to an interaction that is already being idled away.
            var modes = new[] { VMQueueMode.ParentIdle, VMQueueMode.ParentIdle, VMQueueMode.ParentIdle };

            Assert.Equal(0, InteractionQueueLayout.DisplaySlot(modes, 0));
            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(modes, 1));
            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(modes, 2));
        }

        [Fact]
        public void APassedParentIdleDoesNotHideWhatComesAfterIt()
        {
            // The skip only suppresses parent idles. An action queued behind the first idle is the
            // player's own and stays in the queue - and hiding it would be a re-appearing action's
            // mirror image: an entry the player can no longer see or cancel.
            var modes = new[]
            {
                VMQueueMode.ParentIdle,
                VMQueueMode.Normal,
                VMQueueMode.ParentIdle,
            };

            Assert.Equal(new[] { 0, 1, -1 },
                new[] { 0, 1, 2 }.Select(i => InteractionQueueLayout.DisplaySlot(modes, i)).ToArray());
        }

        [Fact]
        public void EveryDrawnEntryHasASlotAndNoTwoDrawnEntriesShareOne()
        {
            // The property the display rests on: slots are handed out in queue order without gaps or
            // collisions, so "the entry at slot n" means one entry and the tween between two states
            // never has two entries crossing.
            var queue = new[]
            {
                VMQueueMode.Normal,
                VMQueueMode.ParentIdle,
                VMQueueMode.ParentExit,
                VMQueueMode.Idle,
                VMQueueMode.Normal,
                VMQueueMode.ParentIdle,
                VMQueueMode.ParentExit,
            };

            var drawn = Enumerable.Range(0, queue.Length)
                .Select(i => (Index: i, Slot: InteractionQueueLayout.DisplaySlot(queue, i)))
                .Where(entry => entry.Slot > -1)
                .ToArray();

            Assert.Equal(drawn.Length, drawn.Select(entry => entry.Slot).Distinct().Count());
            Assert.Equal(
                drawn.Select(entry => entry.Slot).OrderBy(slot => slot),
                drawn.Select(entry => entry.Slot));
            Assert.All(drawn, entry => Assert.True(entry.Slot < drawn.Length,
                "slot " + entry.Slot + " of " + drawn.Length + " drawn entries is a gap"));

            // and the specific shape: [normal, parent idle, exit, idle, normal, parent idle, exit]
            // draws the normal, the first parent idle and the later normal. The second parent idle
            // is hidden by the first one - which has set the skip for the rest of the queue, exactly
            // as the queue display has always read it - and the exits are hidden because neither is
            // the active entry.
            Assert.Equal(new[] { 0, 1, 4 }, drawn.Select(entry => entry.Index).ToArray());
            Assert.Equal(new[] { 0, 1, 2 }, drawn.Select(entry => entry.Slot).ToArray());
        }

        [Fact]
        public void AnEntryThatIsGoneOrOutOfRangeHasNoSlot()
        {
            // What the first display pass asks about a queue entry that has been cancelled and
            // dropped: nothing to place, so the entry leaves the display instead of being parked in
            // a slot that means nothing.
            var modes = new[] { VMQueueMode.Normal, VMQueueMode.Normal };

            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(modes, 2));
            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(modes, -1));
            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(new VMQueueMode[0], 0));
            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(null, 0));
        }

        [Fact]
        public void AnEmptyQueueDrawsNothing()
        {
            Assert.Equal(-1, InteractionQueueLayout.DisplaySlot(new VMQueueMode[0], 0));
        }

        // ---- coverage: the control asks this rule ----------------------------------------------

        [Fact]
        public void TheControlsClickHandlerAsksTheRuleRatherThanDecidingForItself()
        {
            // A rule nothing calls is not a rule, and a second copy of one is a rule that drifts. The
            // click handler is the only thing that decides whether the sim is told, so both gates
            // have to live in `InteractionQueueLayout.OnEntryClicked` and the handler has to be
            // feeding it the facts rather than re-asking the queue inline. Asserted on the source,
            // with the comments stripped first, since the reason for the rule is written next to it.
            //
            // The region is from the click handler onwards, which is where it is: the accept/decline
            // handler above it keeps a `NotifyIdle` gate of its own, and that one is a different
            // question - there is no point answering a prompt for an action that is being cancelled.
            var source = StripComments(ReadClientFile(Path.Combine("UI", "Panels", "UIInteractionQueue.cs")));
            var handler = source.IndexOf("private void InteractionClicked", StringComparison.Ordinal);
            Assert.True(handler > -1, "UIInteractionQueue no longer has an InteractionClicked handler");

            var click = source.Substring(handler);
            var body = MethodBody(click, "private void InteractionClicked(UIElement ui)");

            Assert.Contains("InteractionQueueLayout.OnEntryClicked", body);
            Assert.Contains("itemui.Interaction.NotifyIdle", body);
            Assert.Contains("GetElemPriority", body);
            Assert.Contains("VMNetInteractionCancelCmd", body);

            // and the click must not mark the entry cancelled itself. The red X is driven by the
            // engine's `NotifyIdle` in `Update`, so drawing it here would show a cancel the sim
            // never carried out.
            Assert.DoesNotContain("MarkCancelled", body);
        }

        [Fact]
        public void BothDisplayPassesPlaceEntriesWithTheSameRule()
        {
            // The two passes in `Update` - "is this entry still here, and where does it belong" and
            // "which queue entries have no entry on screen" - have to be placing entries with the
            // same definition of visible. They did not: each had its own copy of the rule, and a
            // cancelled action that one pass still believed in was left on screen as a ghost until
            // the queue shuffled.
            var source = StripComments(ReadClientFile(Path.Combine("UI", "Panels", "UIInteractionQueue.cs")));

            var calls = Regex.Matches(source, @"InteractionQueueLayout\.DisplaySlot\(").Count;
            Assert.Equal(2, calls);

            // neither pass is allowed to answer the visibility question any other way.
            var update = Regex.Match(source, @"public override void Update\(UpdateState state\)(?<body>.*?)\n        \}", RegexOptions.Singleline);
            Assert.True(update.Success, "could not find UIInteractionQueue.Update to check");
            Assert.DoesNotContain("skipParentIdle", update.Groups["body"].Value);
        }

        // ---- helpers ---------------------------------------------------------------------------

        private static int[] SlotsOf(params VMQueueMode[] modes)
        {
            return Enumerable.Range(0, modes.Length)
                .Select(i => InteractionQueueLayout.DisplaySlot(modes, i))
                .ToArray();
        }

        private static string StripComments(string source)
        {
            var code = Regex.Replace(source, @"/\*.*?\*/", "", RegexOptions.Singleline);
            return Regex.Replace(code, @"//[^\n]*", "");
        }

        /// <summary>
        /// The text of one method, from its signature to the brace that closes it, by counting
        /// braces. Used instead of a regex so a signature that happens to contain a brace, or a body
        /// whose closing brace sits at an unusual indentation, cannot silently produce an empty
        /// region - an empty region would satisfy every "does not contain" assertion in it.
        /// </summary>
        private static string MethodBody(string source, string signature)
        {
            var start = source.IndexOf(signature, StringComparison.Ordinal);
            if (start < 0) return "";

            var open = source.IndexOf('{', start);
            if (open < 0) return "";

            var depth = 0;
            for (var i = open; i < source.Length; i++)
            {
                if (source[i] == '{') depth++;
                else if (source[i] == '}' && --depth == 0) return source.Substring(start, i - start + 1);
            }
            return source.Substring(start);
        }

        /// <summary>
        /// A file from the Simitone client sources, found by walking up out of the test output until
        /// the project turns up. Fails loudly rather than skipping: a scan that silently found
        /// nothing would report every gate as gone.
        /// </summary>
        private static string ReadClientFile(params string[] relative)
        {
            var marker = Path.Combine(
                new[] { "Client", "Simitone", "Simitone.Client" }.Concat(relative).ToArray());
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, marker);
                if (File.Exists(candidate)) return File.ReadAllText(candidate);
                dir = dir.Parent;
            }

            throw new DirectoryNotFoundException(
                "Could not find " + marker + " walking up from " + AppContext.BaseDirectory +
                ". These tests read the client sources on purpose, so they cannot pass by finding nothing.");
        }
    }
}