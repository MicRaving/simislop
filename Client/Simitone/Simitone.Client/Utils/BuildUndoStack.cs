/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;

namespace Simitone.Client.Utils
{
    /// <summary>
    /// The build/buy actions that are on the undo stack, and the one that is never on it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Deliberately three, and deliberately not "architecture". Walls, floors, wallpapers, roofs
    /// and terrain are edited by <c>UICustomLotControl</c> subclasses that submit
    /// <c>VMNetArchitectureCmd</c> and a <c>VMNetSetRoofCmd</c> against the lot's
    /// <c>VMArchitecture</c>. Undoing those needs a diff of the architecture the player had before
    /// the edit, not a record of one object, and the architecture is shared state that a second
    /// player's own edit can invalidate underneath a stored snapshot. They are out of scope here
    /// and are said so in the panel's own tooltip rather than half-supported.
    /// </para>
    ///
    /// <para>
    /// <see cref="None"/> is the "no record at all" value. It is what a caller that decides an
    /// action cannot be undone exactly hands over, and <see cref="BuildUndoStack.Push"/> refuses
    /// it, so a caller that cannot undo something can say so without having to know whether the
    /// stack filters or not.
    /// </para>
    /// </remarks>
    public enum BuildUndoKind
    {
        /// <summary>Not an undoable action. Never accepted by the stack.</summary>
        None = 0,

        /// <summary>
        /// A catalogued object was bought and put down. Undo deletes it and takes the exact amount
        /// charged back; redo buys it again and charges the same amount.
        /// </summary>
        Place,

        /// <summary>
        /// A bought object was sold back. Undo buys it again at the exact amount the sale
        /// refunded; redo sells it again and takes the same amount back.
        /// </summary>
        Sell,

        /// <summary>
        /// A bought object was moved. Undo moves it back to the tile and direction it came from.
        /// No money changes hands, so this is the one kind that is always exact.
        /// </summary>
        Move
    }

    /// <summary>
    /// One build/buy action on the undo stack, in the shape the net commands need it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A record is <em>data about one net command and its inverse</em>, not a snapshot of the
    /// world. That is the whole reason the undo here can be a net operation rather than a
    /// client-side reach-in: everything needed to send the inverse command is in here, and nothing
    /// in here can be stale, because the engine is the only thing that ever mutates the lot.
    /// </para>
    ///
    /// <para>
    /// Positions are stored the way the net commands carry them - a tile position in 1/16ths of a
    /// tile (<c>LotTilePos</c>'s own units, not a tile index) and a level - so a record can be
    /// turned into a <c>VMNetBuyObjectCmd</c>, a <c>VMNetMoveObjectCmd</c> or a
    /// <c>VMNetDeleteObjectCmd</c> without re-deriving anything.
    /// </para>
    /// </remarks>
    public sealed class BuildUndoEntry
    {
        /// <summary>Which action this is. <see cref="BuildUndoKind.None"/> is never stored.</summary>
        public BuildUndoKind Kind;

        /// <summary>
        /// Catalog GUID of the object the action is about. Unused by
        /// <see cref="BuildUndoKind.Move"/>, which addresses an object by id.
        /// </summary>
        public uint Guid;

        /// <summary>X of the object's base tile, in 1/16ths of a tile, <em>after</em> the action.</summary>
        public short X;

        /// <summary>Y of the object's base tile, in 1/16ths of a tile, <em>after</em> the action.</summary>
        public short Y;

        /// <summary>Storey of the object's base tile <em>after</em> the action.</summary>
        public sbyte Level;

        /// <summary>Direction of the object <em>after</em> the action, as the net commands carry it.</summary>
        public byte Dir;

        /// <summary>
        /// The exact amount of money the action moved, in simoleons.
        /// <para>
        /// For <see cref="BuildUndoKind.Place"/> this is what the buy charged; for
        /// <see cref="BuildUndoKind.Sell"/> it is what the sale refunded. It is recorded rather
        /// than recomputed because the engine recomputes it from the catalogue and a save file, and
        /// the undo is only allowed when those two answers are the same number - see
        /// <see cref="BuildUndoPolicy.RefundIsExact"/>. Zero for
        /// <see cref="BuildUndoKind.Move"/>, which moves no money.
        /// </para>
        /// </summary>
        public int Value;

        /// <summary>
        /// The lot's object id for the object, as it was when the record was made. Zero means "not
        /// known yet".
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Diagnostic only, and deliberately not what undo acts on.</b> An object that has been
        /// deleted and bought back is a <em>different</em> object with a different id - the VM hands
        /// ids out monotonically and never reuses one - so after an undo/redo cycle the number
        /// stored here names something that is not there, and a net command sent to it is a silent
        /// no-op that would leave the budget out by the price of the object. Acting on an entry
        /// therefore finds the object by guid and tile, which is stable across that cycle.
        /// </para>
        ///
        /// <para>
        /// A placement's object does not exist at the moment the player clicks, anyway: the buy
        /// command is verified and charged first and only creates the object on a later tick. The
        /// caller leaves this zero and fills it in once the object turns up, which is also the
        /// moment the refund becomes readable and so the moment the entry's exactness can be
        /// decided.
        /// </para>
        /// </remarks>
        public short ObjectId;

        /// <summary>Base tile X <em>before</em> the action. Only meaningful for a move.</summary>
        public short FromX;

        /// <summary>Base tile Y <em>before</em> the action. Only meaningful for a move.</summary>
        public short FromY;

        /// <summary>Storey <em>before</em> the action. Only meaningful for a move.</summary>
        public sbyte FromLevel;

        /// <summary>Direction <em>before</em> the action. Only meaningful for a move.</summary>
        public byte FromDir;

        /// <summary>
        /// Whether the engine has finished applying the action and this entry knows everything it
        /// needs. False for a placement whose object has not been created yet.
        /// </summary>
        public bool Settled;

        /// <summary>
        /// Whether the money round-trips exactly: undoing and redoing this entry leaves the
        /// family's budget, the object's value and the lot's accounting identical to before.
        /// Always true for a move, which moves no money.
        /// </summary>
        /// <remarks>
        /// An entry that cannot be exact is not stored at all. The alternative - storing it and
        /// letting undo refund a slightly different number - would leave the budget permanently
        /// out by the difference, which is exactly the "half-applied edit" the stack exists to
        /// avoid. See <see cref="BuildUndoPolicy.RefundIsExact"/> for when the two differ.
        /// </remarks>
        public bool Exact;

        /// <summary>Where a placement's object ended up, for the caller that has to find it again.</summary>
        public FSO.LotView.Model.LotTilePos TilePos
        {
            get { return new FSO.LotView.Model.LotTilePos(X, Y, Level); }
        }

        /// <summary>Where the object stood before a move, for the caller undoing one.</summary>
        public FSO.LotView.Model.LotTilePos FromTilePos
        {
            get { return new FSO.LotView.Model.LotTilePos(FromX, FromY, FromLevel); }
        }

        /// <summary>
        /// Whether the stack may act on this entry.
        /// </summary>
        /// <remarks>
        /// Two conditions and both are needed. <see cref="Settled"/> because a placement's object
        /// does not exist yet for the first frame or two, and <see cref="Exact"/> because an
        /// inexact entry must never reach the net layer at all.
        /// </remarks>
        public bool IsActionable
        {
            get { return Kind != BuildUndoKind.None && Settled && Exact; }
        }

        /// <inheritdoc />
        public override string ToString()
        {
            return Kind + " guid=" + Guid.ToString("x8") + " at (" + X + ", " + Y + ", " + Level + ") value=" + Value;
        }
    }

    /// <summary>
    /// The undo/redo stack for the build/buy mode object actions.
    ///
    /// <para>
    /// <b>Scope, stated once.</b> This stack holds the three net operations a player performs on
    /// objects in Buy and Build mode: placing one, selling one back, and moving one. It is
    /// <em>not</em> a placement undo and it is not a general command history - it has no entry for
    /// a wall, a floor, a wallpaper, a roof or a terrain edit, because those are architecture
    /// commands against shared lot state rather than object commands, and an "undo" for them
    /// cannot be derived from a record of one object. It also has no entry for putting an object
    /// down out of a sim's inventory, which is a different money path again.
    /// </para>
    ///
    /// <para>
    /// <b>Exactness is a precondition, not a hope.</b> Every entry carries the amount of money its
    /// action moved and a flag saying whether undoing and redoing it gives that money back
    /// precisely. An entry that cannot make that promise is refused at
    /// <see cref="Push"/>, so there is no such thing as a stored entry with a wrong number in it.
    /// <see cref="BuildUndoPolicy"/> decides that flag.
    /// </para>
    ///
    /// <para>
    /// <b>Pure and headless.</b> No graphics device, no content, no VM, no statics: the whole type
    /// is a pair of lists and a capacity, so the semantics below are pinned directly by tests.
    /// The imperative half - finding the object a placement created, sending the inverse net
    /// command - lives in <c>UIObjectHolder</c>.
    /// </para>
    /// </summary>
    public sealed class BuildUndoStack
    {
        /// <summary>
        /// How many actions are remembered when the caller does not say.
        /// </summary>
        /// <remarks>
        /// A buy-mode spree is a few dozen objects, not a few hundred, and every remembered entry
        /// holds a GUID, two tile positions and a price for as long as it is kept. 32 is past any
        /// sensible spree, and a player who fills it loses their oldest few actions - which is the
        /// right end to lose, because the newest is the one they are most likely to want back.
        /// </remarks>
        public const int DefaultCapacity = 32;

        private readonly List<BuildUndoEntry> undo = new List<BuildUndoEntry>();
        private readonly List<BuildUndoEntry> redo = new List<BuildUndoEntry>();
        private readonly int capacity;

        /// <summary>Creates a stack holding <see cref="DefaultCapacity"/> actions.</summary>
        public BuildUndoStack() : this(DefaultCapacity) { }

        /// <summary>Creates a stack holding <paramref name="capacity"/> actions.</summary>
        /// <param name="capacity">
        /// How many actions to remember. Clamped to at least 1, because a stack that remembers
        /// nothing can never be pushed to and would make every caller test for that.
        /// </param>
        public BuildUndoStack(int capacity)
        {
            this.capacity = Math.Max(1, capacity);
        }

        /// <summary>How many actions this stack remembers.</summary>
        public int Capacity
        {
            get { return capacity; }
        }

        /// <summary>How many actions are currently held for undo.</summary>
        public int UndoCount
        {
            get { return undo.Count; }
        }

        /// <summary>How many actions are currently held for redo.</summary>
        public int RedoCount
        {
            get { return redo.Count; }
        }

        /// <summary>True when there is nothing to undo or redo.</summary>
        public bool IsEmpty
        {
            get { return undo.Count == 0 && redo.Count == 0; }
        }

        /// <summary>
        /// The entry <see cref="Undo"/> would act on, or null when there is nothing to act on.
        /// </summary>
        /// <remarks>
        /// Strictly the <em>top</em> of the stack, never the nearest actionable entry below it.
        /// Actions come off in the order they went on, so an entry with something newer than it
        /// above it cannot be taken yet. In practice an entry that is not actionable cannot reach
        /// the top at all - the caller holds it until it is ready and <see cref="Push"/> refuses it
        /// otherwise - so the second half of the test is a backstop rather than a state a player
        /// can reach, and this returns null only for an empty stack.
        /// </remarks>
        public BuildUndoEntry PeekUndo
        {
            get { return (undo.Count > 0 && undo[undo.Count - 1].IsActionable) ? undo[undo.Count - 1] : null; }
        }

        /// <summary>
        /// The entry <see cref="Redo"/> would act on, or null when there is nothing to act on.
        /// </summary>
        /// <remarks>
        /// The redo stack is held in the order the entries were undone, so its <em>last</em>
        /// element is the next one to come back.
        /// </remarks>
        public BuildUndoEntry PeekRedo
        {
            get { return (redo.Count > 0 && redo[redo.Count - 1].IsActionable) ? redo[redo.Count - 1] : null; }
        }

        /// <summary>True when <see cref="Undo"/> would do something.</summary>
        public bool CanUndo
        {
            get { return PeekUndo != null; }
        }

        /// <summary>True when <see cref="Redo"/> would do something.</summary>
        public bool CanRedo
        {
            get { return PeekRedo != null; }
        }

        /// <summary>
        /// Adds an action to the top of the undo stack and throws away the redo stack.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Refuses a null entry, a <see cref="BuildUndoKind.None"/> entry, and any entry that is
        /// not yet <see cref="BuildUndoEntry.IsActionable"/>. The third is the important one: an
        /// entry that has not settled (a placement whose object the engine has not created yet) or
        /// that cannot be undone exactly has no business on the stack, because the only thing the
        /// stack can do with it is hand it to the net layer.
        /// </para>
        ///
        /// <para>
        /// Discarding the redo stack is the other half of the contract and is not optional: after
        /// a new action the undone past is no longer a past that can be walked back into, because
        /// the new action did something the undone ones did not account for.
        /// </para>
        /// </remarks>
        /// <param name="entry">The action to remember.</param>
        /// <returns>True when the entry was accepted.</returns>
        public bool Push(BuildUndoEntry entry)
        {
            if (entry == null || !entry.IsActionable) return false;

            undo.Add(entry);
            //Oldest first out. Dropping from the front rather than refusing the push keeps the
            //most recent actions, which are the ones a player is most likely to want back.
            while (undo.Count > capacity) undo.RemoveAt(0);

            redo.Clear();
            return true;
        }

        /// <summary>
        /// Takes the top action off the undo stack and onto the redo stack, and returns it.
        /// </summary>
        /// <remarks>
        /// The caller sends the inverse net command. This only moves the record; it does not and
        /// cannot know whether the engine accepted that command, so a command the lot refuses
        /// leaves the stack one step along with the lot unchanged - never a record claiming an
        /// edit that did not happen.
        /// </remarks>
        /// <returns>The action, or null when there was nothing to take.</returns>
        public BuildUndoEntry Undo()
        {
            var top = PeekUndo;
            if (top == null) return null;
            undo.RemoveAt(undo.Count - 1);
            redo.Add(top);
            while (redo.Count > capacity) redo.RemoveAt(0);
            return top;
        }

        /// <summary>
        /// Takes the top action off the redo stack and back onto the undo stack, and returns it.
        /// </summary>
        /// <remarks>The mirror of <see cref="Undo"/>, and the caller re-sends the original net
        /// command.</remarks>
        /// <returns>The action, or null when there was nothing to take.</returns>
        public BuildUndoEntry Redo()
        {
            var top = PeekRedo;
            if (top == null) return null;
            redo.RemoveAt(redo.Count - 1);
            undo.Add(top);
            while (undo.Count > capacity) undo.RemoveAt(0);
            return top;
        }

        /// <summary>Forgets everything. Used when the lot behind the stack is replaced.</summary>
        public void Clear()
        {
            undo.Clear();
            redo.Clear();
        }

        /// <summary>
        /// The undo stack's contents, oldest first. For diagnostics and tests; the stack is not
        /// driven through this.
        /// </summary>
        public IReadOnlyList<BuildUndoEntry> PeekAll()
        {
            return undo;
        }
    }
}
