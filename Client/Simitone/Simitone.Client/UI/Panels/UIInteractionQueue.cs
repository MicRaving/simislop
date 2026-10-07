/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Model;
using FSO.Common.Rendering.Framework.Model;
using FSO.SimAntics.Engine;
using FSO.SimAntics;
using FSO.HIT;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.Common;
using Simitone.Client.UI.Controls;
using FSO.Client;
using Simitone.Client.UI.Model;
using FSO.SimAntics.Model;

namespace Simitone.Client.UI.Panels
{
    /// <summary>
    /// The queue display for ingame. Includes queue animations and control.
    /// </summary>
    public class UIInteractionQueue : UIContainer
    {

        private List<UIIQTrackEntry> QueueItems;
        public VMEntity QueueOwner;
        public VM vm;
        public Vector2 PieMenuClickPos = new Vector2(-1, -1);

        /// <summary>
        /// The queue's modes, copied once per <see cref="Update"/> and reused by both passes below.
        /// </summary>
        /// <remarks>
        /// The two passes disagree in what they are looking for - one asks "is this entry still in
        /// the queue, and where does it belong", the other asks "which queue entries have no entry
        /// on screen" - so both ask <see cref="InteractionQueueLayout.DisplaySlot"/> the same
        /// question about the same data, and both have to be given the same data. A pass that
        /// believed a different answer is how an entry ends up drawn in a slot the other pass does
        /// not, which is how a cancelled action lingers as a ghost and then jumps when the queue
        /// shuffles.
        /// </remarks>
        private readonly List<VMQueueMode> queueModes = new List<VMQueueMode>();

        public UIInteractionQueue(VMEntity QueueOwner, VM vm)
        {
            this.vm = vm;
            this.QueueOwner = QueueOwner;
            QueueItems = new List<UIIQTrackEntry>();
        }

        public short GetElemPriority(VMQueuedAction elem, int i)
        {
            return (elem == QueueOwner.Thread.ActiveAction) ?
                        (QueueOwner as VMAvatar)?.GetPersonData(VMPersonDataVariable.Priority) ?? elem.Priority
                        : elem.Priority;
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);
            if (QueueOwner == null) return;
            //detect any changes in the interaction queue.

            var queue = QueueOwner.Thread.Queue;
            SyncQueueModes(queue);

            for (int i = 0; i < QueueItems.Count; i++)
            {
                var itemui = QueueItems[i];
                int index = queue.IndexOf(itemui.Interaction);
                //-1 if the entry has left the queue, or is no longer something the display draws
                //(a parent idle that another parent idle has moved in front of, say). Both are the
                //same thing to this pass: the entry goes away, exactly as the other pass would have
                //decided if it had asked.
                int position = index > -1 ? InteractionQueueLayout.DisplaySlot(queueModes, index) : -1;
                bool found = position > -1;
                if (found)
                {
                    var elem = queue[index];
                    var priority = GetElemPriority(elem, index);
                    if (position != itemui.QueuePosition) itemui.TweenToPosition(position);
                    if (elem.NotifyIdle && priority <= 0) itemui.MarkCancelled();

                    if (elem.Name != itemui.Name)
                    {
                        itemui.Name = elem.Name;
                        itemui.UI.Tooltip = itemui.Name;
                    }
                    if (index == 0 && !itemui.Active)
                    {
                        itemui.Active = true;
                        itemui.UI.SetActive(true);
                    }
                    if (index != 0 && itemui.Active)
                    {
                        itemui.Active = false;
                        itemui.UI.SetActive(false);
                    }

                    if (itemui.IconOwner != elem.IconOwner)
                    {
                        itemui.IconOwner = elem.IconOwner;
                        itemui.UpdateInteractionIcon();
                    }

                    if (itemui.InteractionResult != elem.InteractionResult)
                    {
                        itemui.InteractionResult = elem.InteractionResult;
                        itemui.UpdateInteractionResult();
                    }
                }
                if (!found)
                {
                    itemui.UI.Kill();
                    QueueItems.RemoveAt(i--); //not here anymore
                }
                else itemui.Update();
            }

            //now detect if there are any interactions we're not displaying and add them.

            for (int i = 0; i < queue.Count; i++)
            {
                var elem = queue[i];
                int position = InteractionQueueLayout.DisplaySlot(queueModes, i);
                if (position < 0) continue; //nothing we draw: an idle tree, a hidden exit, or a parent idle behind another one.

                bool found = false; //is this interaction in the queue? if not, add it
                for (int j = 0; j < QueueItems.Count; j++)
                {
                    var itemui = QueueItems[j];
                    if (elem == itemui.Interaction)
                    {
                        found = true;
                        break;
                    }

                }
                if (!found) //new interaction!!!
                {
                    var itemui = new UIIQTrackEntry()
                    {
                        Interaction = elem,
                        IconOwner = elem.IconOwner,
                        SourcePos = (PieMenuClickPos.X < 0) ? (new Vector2(30 + position * 50, 30)) : PieMenuClickPos,
                        TweenProgress = 0,
                        UI = new UIInteraction(i == 0),
                        Active = (i == 0)
                    };
                    itemui.UI.Position = itemui.SourcePos;
                    itemui.UI.OnMouseEvent += new FSO.Client.UI.Controls.ButtonClickDelegate(InteractionClicked);
                    itemui.UI.OnInteractionResult += InteractionResult;
                    itemui.UI.ParentEntry = itemui;
                    itemui.Name = elem.Name;
                    itemui.UI.Tooltip = itemui.Name;
                    itemui.TweenToPosition(position);
                    itemui.UpdateInteractionIcon();
                    itemui.Update();
                    this.Add(itemui.UI);
                    QueueItems.Add(itemui);

                    PieMenuClickPos = new Vector2(-1, -1);
                }
            }

        }

        /// <summary>
        /// Copy the queue's modes for <see cref="InteractionQueueLayout"/> to read. Reused between
        /// frames so the two display passes do not allocate per update.
        /// </summary>
        private void SyncQueueModes(List<VMQueuedAction> queue)
        {
            queueModes.Clear();
            for (int i = 0; i < queue.Count; i++) queueModes.Add(queue[i].Mode);
        }

        private void InteractionResult(UIElement ui, bool accepted)
        {
            if (QueueOwner == null) return;
            UIInteraction item = (UIInteraction)ui;
            var itemui = item.ParentEntry;
            var queue = QueueOwner.Thread.Queue;
            for (int i = 0; i < queue.Count; i++)
            {
                if (queue[i] == itemui.Interaction)
                {
                    HITVM.Get().PlaySoundEvent(UISounds.CallSend);
                    var priority = GetElemPriority(itemui.Interaction, i);
                    if (!(itemui.Interaction.NotifyIdle && priority <= 0))
                    {
                        vm.SendCommand(new VMNetInteractionResultCmd
                        {
                            ActionUID = itemui.Interaction.UID,
                            ActorUID = QueueOwner.PersistID,
                            Accepted = accepted
                        });
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// The X on a queue entry: tell the sim to cancel it.
        /// </summary>
        /// <remarks>
        /// <para>
        /// The decision is <see cref="InteractionQueueLayout.OnEntryClicked"/>'s, so it can be
        /// asked without a graphics device; this handler only gathers the facts and sends or does
        /// not. Two of them, and both are reasons to send nothing:
        /// </para>
        /// <list type="bullet">
        /// <item>the entry is no longer in the queue. Queue UIDs are a wrapping 16-bit counter, so
        /// a cancel naming an entry that has gone can hit a different action that has since been
        /// handed the same UID;</item>
        /// <item>the engine has already cancelled it - <c>NotifyIdle</c> with a priority of 0 is
        /// what <c>VMThread.CancelAction</c> leaves behind when it interrupts an entry rather than
        /// dropping it - and there is nothing left for the sim to do about it. This gate is the
        /// retail behaviour and it stays: sending a second cancel against an entry the engine is
        /// already unwinding is how a click ends up cancelling whatever action takes that UID
        /// next.</item>
        /// </list>
        /// <para>
        /// The entry is not marked cancelled here. <c>UIIQTrackEntry.MarkCancelled</c> is driven by
        /// the engine's own <c>NotifyIdle</c> in <see cref="Update"/>, which is what the display
        /// has always used to draw the red X, and doing it from the click would show a cancel that
        /// the sim never carried out.
        /// </para>
        /// </remarks>
        private void InteractionClicked(UIElement ui)
        {
            if (QueueOwner == null) return;
            UIInteraction item = (UIInteraction)ui;
            var itemui = item.ParentEntry;
            if (itemui == null || itemui.Interaction == null) return;
            var queue = QueueOwner.Thread.Queue;
            int index = queue.IndexOf(itemui.Interaction);
            var priority = GetElemPriority(itemui.Interaction, index);
            if (InteractionQueueLayout.OnEntryClicked(index > -1, itemui.Interaction.NotifyIdle, priority) != InteractionQueueLayout.ClickOutcome.SendCancel) return;

            HITVM.Get().PlaySoundEvent(UISounds.QueueDelete);
            vm.SendCommand(new VMNetInteractionCancelCmd
            {
                ActionUID = itemui.Interaction.UID,
                ActorUID = QueueOwner.PersistID
            });
        }
    }

    /// <summary>
    /// What a click on a queue entry should do. Split out from the control so the rule can be
    /// tested without a graphics device, an entity or a running sim.
    /// </summary>
    public static class InteractionQueueLayout
    {
        /// <summary>What a click on an entry amounts to.</summary>
        public enum ClickOutcome
        {
            /// <summary>
            /// The entry is not in the queue any more (it was cancelled, or the sim's queue was
            /// rebuilt under us). Nothing is sent: the UID on the entry may already have been handed
            /// out again to a different action, and cancelling by it would hit the wrong one.
            /// </summary>
            Ignore,

            /// <summary>Send the cancel command.</summary>
            SendCancel,
        }

        /// <summary>
        /// What a click on a queue entry should do.
        /// </summary>
        /// <param name="entryStillInQueue">Is the clicked entry still one of the queue's entries?</param>
        /// <param name="notifyIdle">
        /// The entry's <c>VMQueuedAction.NotifyIdle</c>. Set by <c>VMThread.CancelAction</c>, and
        /// the queue display draws it as the red X.
        /// </param>
        /// <param name="priority">
        /// The entry's effective priority - <see cref="UIInteractionQueue.GetElemPriority"/>, which
        /// reads person data for the active entry because cancelling zeroes it there.
        /// </param>
        /// <remarks>
        /// Two independent reasons to send nothing, and the order they are asked in matters only for
        /// which one is reported; both answer <see cref="ClickOutcome.Ignore"/>.
        ///
        /// <para>
        /// The second is the retail gate: an entry the engine has already cancelled
        /// (<c>NotifyIdle</c> at priority 0 is what <c>CancelAction</c> leaves behind when it
        /// interrupts rather than drops the entry) does not need telling. Sending anyway is not
        /// harmless - the UID is a wrapping 16-bit counter, so a cancel that lands after the queue
        /// has moved on names whatever action holds that UID now.
        /// </para>
        /// </remarks>
        public static ClickOutcome OnEntryClicked(bool entryStillInQueue, bool notifyIdle, int priority)
        {
            if (!entryStillInQueue) return ClickOutcome.Ignore;
            if (notifyIdle && priority <= 0) return ClickOutcome.Ignore;
            return ClickOutcome.SendCancel;
        }

        /// <summary>
        /// The queue slot an entry is drawn in, or -1 if it is not drawn at all.
        /// </summary>
        /// <param name="modes">Every entry's <see cref="VMQueueMode"/>, in queue order.</param>
        /// <param name="index">Which entry to place.</param>
        /// <remarks>
        /// <para>
        /// Three kinds of entry are in the queue but not on screen:
        /// <see cref="VMQueueMode.Idle"/> (the sim's idle tree, and the hidden half of a
        /// run-immediately action), a <see cref="VMQueueMode.ParentExit"/> that has not started
        /// (the object's own "let the sim go" action, which the display only shows once it becomes
        /// the active entry), and a <see cref="VMQueueMode.ParentIdle"/> sitting behind another
        /// parent idle - the idle belongs to an interaction that is already being idled away, so
        /// only the first one is shown.
        /// </para>
        /// <para>
        /// The slot of a visible entry is the number of visible entries in front of it, so the
        /// answer is a count over the queue rather than a property of the entry: two entries swap
        /// slots when one of them goes away. Both display passes ask this, and it has to be one
        /// function - the pass that adds entries and the pass that moves them disagreed about what
        /// "visible" means often enough to leave a cancelled action drawn in a slot nothing else
        /// believed in.
        /// </para>
        /// </remarks>
        public static int DisplaySlot(IReadOnlyList<VMQueueMode> modes, int index)
        {
            if (modes == null || index < 0 || index >= modes.Count) return -1;

            bool skipParentIdle = false;
            int slot = 0;
            for (int i = 0; i <= index; i++)
            {
                var mode = modes[i];
                bool visible = mode != VMQueueMode.Idle
                    && (i == 0 || mode != VMQueueMode.ParentExit)
                    && (!skipParentIdle || mode != VMQueueMode.ParentIdle);
                if (visible && i == index) return slot;
                if (visible) slot++;
                if (mode == VMQueueMode.ParentIdle) skipParentIdle = true;
            }
            return -1; //the entry asked about is one of the hidden ones.
        }
    }

    public class UIIQTrackEntry //this class basically keeps track of states to determine if things have changed.
    {
        public VMQueuedAction Interaction;
        public UIInteraction UI;
        public VMEntity IconOwner;
        public int QueuePosition;
        public bool Active;
        public bool Cancelled;
        public string Name;

        public sbyte InteractionResult = -1;

        public double TweenProgress;
        public Vector2 TargetPos;
        public Vector2 SourcePos;
        public double MotionPerFrame = 1.0 / 25.0; //default to finishing in 25 frames

        public void TweenToPosition(int pos)
        {
            Vector2 realPos;
            if (pos == 0) realPos = new Vector2(73, 73);
            else realPos = new Vector2(37 + 68 + pos*60, 41.5f);

            GameFacade.Screens.Tween.To(UI, 0.5f, new Dictionary<string, float>() { { "X", realPos.X }, { "Y", realPos.Y } }, TweenQuad.EaseOut);
            QueuePosition = pos;
            /*
            SourcePos = GetTweenPosition();
            TargetPos = new Vector2(30 + pos * 50, 30);
            TweenProgress = 0;*/
        }

        private Vector2 GetTweenPosition()
        {
            return TargetPos * (float)TweenProgress + SourcePos * (1 - (float)TweenProgress);
        }

        public void Update()
        {
            if (TweenProgress < 1)
            {
                TweenProgress = Math.Min(TweenProgress + MotionPerFrame * (60.0 / FSOEnvironment.RefreshRate), 1);
                UI.Position = GetTweenPosition();
            }
        }

        public void UpdateInteractionIcon()
        {
            UI.Icon = UIIconCache.GetObject(IconOwner);
        }

        public void UpdateInteractionResult()
        {
            UI.UpdateInteractionResult(InteractionResult);
        }

        /// <summary>
        /// Show the entry as cancelled, once. Driven by the engine's own
        /// <c>VMQueuedAction.NotifyIdle</c> from <see cref="UIInteractionQueue.Update"/>, so the X
        /// appears exactly when the sim says the entry is cancelled - and only the first time,
        /// because the X fades in over half a second and re-running that tween every frame the flag
        /// is set would have it pulsing.
        /// </summary>
        public void MarkCancelled()
        {
            if (Cancelled) return;
            Cancelled = true;
            UI.SetCancelled();
        }
    }
}
