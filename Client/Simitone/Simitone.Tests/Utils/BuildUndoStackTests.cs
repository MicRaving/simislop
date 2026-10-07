using System;
using System.Linq;
using FSO.LotView.Model;
using Simitone.Client.Utils;
using Xunit;

namespace Simitone.Tests.Utils
{
    /// <summary>
    /// Locks in the stack semantics of <see cref="BuildUndoStack"/>: what it accepts, what order
    /// it gives things back in, when the redo side is thrown away, and what happens when it runs
    /// out of room.
    ///
    /// <para>
    /// The stack is deliberately the whole of the undo state - a pair of lists and a capacity - so
    /// all of this is asserted directly with no VM, no content pipeline and no graphics device. The
    /// four properties that matter most to a player are each pinned below under their own heading:
    /// last in is first out, a new action throws away the redo side, an action that cannot be
    /// undone exactly never gets in at all, and a full stack loses its oldest rather than its
    /// newest.
    /// </para>
    /// </summary>
    public sealed class BuildUndoStackTests
    {
        /// <summary>
        /// An entry the stack will accept: a move, which moves no money and so is always exact, with
        /// an object id that is already known.
        /// </summary>
        private static BuildUndoEntry Move(int id, short x = 0, short y = 0, byte dir = 0)
        {
            return new BuildUndoEntry
            {
                Kind = BuildUndoKind.Move,
                Guid = 0x1000u,
                ObjectId = (short)id,
                X = x,
                Y = y,
                Level = 1,
                Dir = dir,
                FromX = 16,
                FromY = 32,
                FromLevel = 1,
                FromDir = 4,
                Settled = true,
                Exact = true
            };
        }

        /// <summary>An entry the stack will refuse: a placement that has not settled yet.</summary>
        private static BuildUndoEntry UnsettledPlace(int value = 300)
        {
            return new BuildUndoEntry
            {
                Kind = BuildUndoKind.Place,
                Guid = 0x2000u,
                X = 64,
                Y = 48,
                Level = 1,
                Dir = 0,
                Value = value,
                ObjectId = 0, //the engine has not made it yet
                Settled = false,
                Exact = false
            };
        }

        // --------------------------------------------------------------- empty

        [Fact]
        public void ANewStackHasNothingToUndoOrRedo()
        {
            var stack = new BuildUndoStack();
            Assert.False(stack.CanUndo);
            Assert.False(stack.CanRedo);
            Assert.True(stack.IsEmpty);
            Assert.Equal(0, stack.UndoCount);
            Assert.Equal(0, stack.RedoCount);
            Assert.Null(stack.PeekUndo);
            Assert.Null(stack.PeekRedo);
        }

        [Fact]
        public void UndoOnAnEmptyStackChangesNothingAndReportsNothing()
        {
            var stack = new BuildUndoStack();
            Assert.Null(stack.Undo());
            Assert.True(stack.IsEmpty);
            Assert.False(stack.CanRedo);
        }

        [Fact]
        public void RedoWithNothingUndoneChangesNothingAndReportsNothing()
        {
            var stack = new BuildUndoStack();
            Assert.Null(stack.Redo());
            Assert.True(stack.IsEmpty);
            Assert.False(stack.CanUndo);
        }

        // ---------------------------------------------------------- push / undo / redo

        [Fact]
        public void ARecordedActionCanBeUndoneAndRedone()
        {
            var stack = new BuildUndoStack();
            var a = Move(7);
            Assert.True(stack.Push(a));

            Assert.True(stack.CanUndo);
            Assert.False(stack.CanRedo);
            Assert.Equal(1, stack.UndoCount);

            Assert.Same(a, stack.Undo());
            Assert.False(stack.CanUndo);
            Assert.True(stack.CanRedo);
            Assert.Equal(1, stack.RedoCount);

            Assert.Same(a, stack.Redo());
            Assert.True(stack.CanUndo);
            Assert.False(stack.CanRedo);
            Assert.Equal(1, stack.UndoCount);
        }

        [Fact]
        public void UndoAndRedoAreLastInFirstOut()
        {
            var stack = new BuildUndoStack();
            var a = Move(1);
            var b = Move(2);
            var c = Move(3);
            stack.Push(a);
            stack.Push(b);
            stack.Push(c);

            Assert.Same(c, stack.Undo());
            Assert.Same(b, stack.Undo());
            Assert.Same(a, stack.Undo());
            Assert.Null(stack.Undo());

            //and back the other way, in the order they were taken
            Assert.Same(a, stack.Redo());
            Assert.Same(b, stack.Redo());
            Assert.Same(c, stack.Redo());
            Assert.Null(stack.Redo());
        }

        [Fact]
        public void PeekReportsTheEntryThatWouldBeActedOn()
        {
            var stack = new BuildUndoStack();
            var a = Move(1);
            var b = Move(2);
            stack.Push(a);
            Assert.Same(a, stack.PeekUndo);

            stack.Push(b);
            Assert.Same(b, stack.PeekUndo);

            //Peeking does not consume.
            Assert.Equal(2, stack.UndoCount);
        }

        // ------------------------------------------- a new action kills the redo side

        [Fact]
        public void ANewActionThrowsAwayTheRedoSide()
        {
            var stack = new BuildUndoStack();
            var a = Move(1);
            var b = Move(2);
            stack.Push(a);
            stack.Undo();
            Assert.True(stack.CanRedo);

            stack.Push(b);
            Assert.False(stack.CanRedo);
            Assert.Equal(0, stack.RedoCount);
            Assert.Null(stack.Redo());
            Assert.Equal(1, stack.UndoCount);
        }

        [Fact]
        public void AnUnacceptedActionDoesNotThrowAwayTheRedoSide()
        {
            // Push refuses before it touches the redo list, so a caller that offers something the
            // stack will not take cannot cost the player their redo by trying.
            var stack = new BuildUndoStack();
            stack.Push(Move(1));
            stack.Undo();
            Assert.True(stack.CanRedo);

            Assert.False(stack.Push(UnsettledPlace()));
            Assert.True(stack.CanRedo);
        }

        [Fact]
        public void UndoThenRedoRepeatedlyReturnsTheSameEntriesInTheSameOrder()
        {
            var stack = new BuildUndoStack();
            for (int i = 1; i <= 5; i++) stack.Push(Move(i));
            var expected = stack.PeekAll().Select(x => x.ObjectId).ToArray();

            for (int i = 0; i < 5; i++) stack.Undo();
            Assert.False(stack.CanUndo);
            for (int i = 0; i < 5; i++) stack.Redo();

            Assert.Equal(expected, stack.PeekAll().Select(x => x.ObjectId).ToArray());
        }

        // ------------------------------------- non-undoable actions are ignored, not stored

        [Fact]
        public void ANullEntryIsIgnored()
        {
            var stack = new BuildUndoStack();
            Assert.False(stack.Push(null));
            Assert.True(stack.IsEmpty);
        }

        [Fact]
        public void AnEntryThatIsNotOneOfTheThreeKindsIsIgnored()
        {
            // BuildUndoKind.None is what a caller hands over when it has decided the action cannot
            // be undone - a wall, a floor, a roof, a terrain edit, an object out of an inventory.
            var stack = new BuildUndoStack();
            var entry = Move(1);
            entry.Kind = BuildUndoKind.None;
            Assert.False(entry.IsActionable);
            Assert.False(stack.Push(entry));
            Assert.True(stack.IsEmpty);
        }

        [Fact]
        public void AnActionTheEngineHasNotFinishedApplyingIsIgnored()
        {
            // A placement's object does not exist for the frame or two after the click, so its
            // record cannot be acted on yet. Storing it would hand the net layer an object id of 0.
            var stack = new BuildUndoStack();
            var entry = UnsettledPlace();
            entry.Exact = true; //exact, but not settled - still not actionable
            Assert.False(entry.IsActionable);
            Assert.False(stack.Push(entry));
            Assert.True(stack.IsEmpty);
        }

        [Fact]
        public void AnActionThatWouldNotRefundExactlyIsIgnored()
        {
            // The whole point of the Exact flag: an entry that cannot give the money back to the
            // simoleon must not reach the net layer, because the budget would then be permanently
            // out by the difference.
            var stack = new BuildUndoStack();
            var entry = Move(1);
            entry.Exact = false;
            Assert.False(entry.IsActionable);
            Assert.False(stack.Push(entry));
            Assert.True(stack.IsEmpty);
        }

        [Fact]
        public void AStackCannotBeGivenAnUnactionableTopEntryAtAll()
        {
            // Strict last-in-first-out is only a problem if an unactionable entry can reach the top
            // of the stack, and it cannot: Push is the only way in, and it refuses anything that is
            // not both settled and exact. A placement the player has just made is held by the
            // caller until the engine has created its object (UIObjectHolder's pending queue), so it
            // is never in this list while it is still unactionable.
            var stack = new BuildUndoStack(4);
            stack.Push(Move(1));
            Assert.False(stack.Push(UnsettledPlace()));

            Assert.True(stack.CanUndo);
            Assert.Equal((short)1, stack.PeekUndo.ObjectId);
            Assert.Equal(1, stack.UndoCount);
        }

        // ----------------------------------------------------------------- overflow

        [Fact]
        public void TheOldestActionsAreDroppedWhenTheStackIsFull()
        {
            var stack = new BuildUndoStack(3);
            for (int i = 1; i <= 5; i++) stack.Push(Move(i));

            Assert.Equal(3, stack.UndoCount);
            //The three newest survive, in the order they were made.
            Assert.Equal(new short[] { 3, 4, 5 }, stack.PeekAll().Select(x => x.ObjectId).ToArray());
            Assert.Equal((short)5, stack.PeekUndo.ObjectId);
            Assert.Equal((short)5, stack.Undo().ObjectId);
        }

        [Fact]
        public void TheNewestActionsAreNeverDropped()
        {
            // A full stack must not refuse the action the player has just made: that is the one
            // they are most likely to want back.
            var stack = new BuildUndoStack(2);
            stack.Push(Move(1));
            stack.Push(Move(2));
            Assert.True(stack.Push(Move(3)));

            Assert.Equal(2, stack.UndoCount);
            Assert.Equal((short)3, stack.Undo().ObjectId);
            Assert.Equal((short)2, stack.Undo().ObjectId);
            Assert.Null(stack.Undo());
        }

        [Fact]
        public void TheOldestActionIsTheFirstToGoWhenTheStackIsFull()
        {
            // Which end overflow trims from is the whole of the difference between "undo the last
            // few things" and "undo the last few things, and lose the one before them".
            var stack = new BuildUndoStack(2);
            stack.Push(Move(1));
            stack.Push(Move(2));
            stack.Push(Move(3));

            Assert.DoesNotContain(stack.PeekAll(), x => x.ObjectId == 1);
            Assert.Contains(stack.PeekAll(), x => x.ObjectId == 3);
        }

        [Fact]
        public void TheRedoSideCannotGrowPastTheCapacityEither()
        {
            // The undo list is capped, so there are never more entries to undo than the capacity and
            // the redo list cannot exceed it either. A player who holds Ctrl+Z down therefore gets
            // the same bounded set back rather than an unbounded list.
            var stack = new BuildUndoStack(2);
            stack.Push(Move(1));
            stack.Push(Move(2));
            stack.Push(Move(3));

            stack.Undo();
            stack.Undo();
            Assert.Equal(2, stack.RedoCount);

            //The last one undone is the next one to come back.
            Assert.Equal((short)2, stack.PeekRedo.ObjectId);
            stack.Undo();
            Assert.Equal(2, stack.RedoCount);
        }

        [Fact]
        public void ADefaultStackRemembersABuySpreeAndNoMore()
        {
            var stack = new BuildUndoStack();
            Assert.Equal(BuildUndoStack.DefaultCapacity, stack.Capacity);
            Assert.Equal(32, stack.Capacity);

            for (int i = 0; i < 100; i++) stack.Push(Move(i + 1));
            Assert.Equal(32, stack.UndoCount);
            Assert.Equal((short)100, stack.PeekUndo.ObjectId);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(int.MinValue)]
        public void AStackIsNeverGivenLessThanOneSlot(int requested)
        {
            // A capacity of zero would make every Push fail, and a caller would then have to test
            // for that before using the stack at all.
            var stack = new BuildUndoStack(requested);
            Assert.Equal(1, stack.Capacity);
            Assert.True(stack.Push(Move(1)));
            Assert.Equal(1, stack.UndoCount);
        }

        // -------------------------------------------------------------------- clear

        [Fact]
        public void ClearForgetsBothSides()
        {
            var stack = new BuildUndoStack();
            stack.Push(Move(1));
            stack.Push(Move(2));
            stack.Undo();

            stack.Clear();
            Assert.True(stack.IsEmpty);
            Assert.False(stack.CanUndo);
            Assert.False(stack.CanRedo);
        }

        // ------------------------------------------------------------- the geometry

        [Fact]
        public void PositionsAreCarriedInSixteenthsOfATileLikeTheNetCommandsCarryThem()
        {
            var entry = Move(1);
            entry.X = 40; entry.Y = 72; entry.Level = 2;
            entry.FromX = 8; entry.FromY = 16; entry.FromLevel = 1;

            Assert.Equal(new LotTilePos(40, 72, 2), entry.TilePos);
            Assert.Equal(new LotTilePos(8, 16, 1), entry.FromTilePos);
        }

        [Fact]
        public void AnEntryDescribesItselfWithoutThrowing()
        {
            // ToString ends up in diagnostics, and a record with nothing resolved in it yet is a
            // normal state rather than an error.
            var entry = UnsettledPlace(1250);
            var text = entry.ToString();
            Assert.Contains("Place", text);
            Assert.Contains("1250", text);
        }
    }
}
