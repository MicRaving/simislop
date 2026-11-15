/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FSO.LotView;
using FSO.SimAntics;
using FSO.Common.Rendering.Framework.Model;
using Microsoft.Xna.Framework;
using FSO.LotView.Components;
using FSO.SimAntics.Entities;
using FSO.LotView.Model;
using FSO.Client.UI.Model;
using FSO.HIT;
using FSO.SimAntics.Model;
using Microsoft.Xna.Framework.Input;
using FSO.Client.UI.Framework;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.Common;
using FSO.Client.UI.Controls;
using FSO.Common.Rendering.Framework;
using FSO.Content;
using FSO.Client;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels
{
    public class UIObjectHolder //controls the object holder interface
    {
        public VM vm;
        public World World;
        public UILotControl ParentControl;

        public int MouseDownX;
        public int MouseDownY;
        private bool MouseIsDown;
        private bool MouseWasDown;
        private bool MouseClicked;

        private UpdateState LastState; //state for access from Sellback and friends.
        public bool ShowTooltip;
        public bool Roommate = true;
        public bool UseNet = false;

        // Eyedropper tool support.
        //
        // When on, the next click on the lot is a "what is this?" rather than a pickup: it
        // reports the catalogued style of whatever is under the cursor and does not touch the
        // object. The flag is turned off by the panel that handles the pick (so the tool is
        // one-shot), and the desktop UCP mirrors it onto its button highlight each frame.
        public bool EyedropperMode;

        /// <summary>
        /// Raised when the eyedropper clicked a catalogued object. Carries the catalog GUID
        /// (multitile masters resolve to the master's GUID so the whole set is identified by the
        /// thing the player sees).
        /// </summary>
        public event EyedropperEventHandler OnEyedropperPick;
        public delegate void EyedropperEventHandler(uint guid);

        /// <summary>
        /// Raised when the eyedropper clicked architecture, which has no GUID and so is
        /// identified by pattern id plus kind.
        /// </summary>
        public event EyedropperArchitectureHandler OnEyedropperArchitecturePick;
        public delegate void EyedropperArchitectureHandler(ushort patternID, ArchitectureType type);

        public event HolderEventHandler OnPickup;
        public event HolderEventHandler OnDelete;
        public event HolderEventHandler OnPutDown;

        /// <summary>
        /// Raised after an undo or a redo has sent its net command.
        /// </summary>
        /// <remarks>
        /// A separate event from <see cref="OnDelete"/> because there is no
        /// <see cref="UIObjectSelection"/> to hand over: the action came off the stack, not off the
        /// mouse. What a panel needs to hear is only that the lot changed under it and that
        /// whatever it was holding is not what is on screen - the query panel has to stop
        /// describing an object undo has just removed, and the catalogue strip has to stop showing
        /// a chosen row.
        /// </remarks>
        public event Action OnUndoRedo;

        public UIObjectSelection Holding;

        // -------------------------------------------------------------------------------------
        // Build/buy undo.
        //
        // The stack itself is BuildUndoStack, which is pure and knows nothing about the lot; this
        // is the half that has to: it turns a player's gesture into a record, waits for the engine
        // to finish applying it, and turns an undo request back into the same net command the
        // gesture would have sent.
        //
        // It is a *net* undo. Nothing here creates, moves or deletes an entity: every action goes
        // through vm.SendCommand exactly as the player's own click does, so the lot, the money and
        // the object's provenance are changed by the same code path that changes them for real.
        // There is no client-side edit anywhere in this feature that the net layer does not know
        // about.
        //
        // What is deliberately NOT on the stack: walls, floors, wallpapers, roofs and terrain.
        // Those are UICustomLotControl tools that submit architecture commands against shared
        // VMArchitecture state, and undoing them needs a before/after of that state rather than a
        // record of one object. Half-supporting them - undoing a single tile, or a wall drag that
        // is not exactly inverted - would be worse than not offering it, so they are refused
        // outright. BuildUndoPolicy.ScopeNote is the sentence the panel shows the player.
        //
        // Also not on the stack: putting an object down out of a sim's inventory. That is a third
        // money path (it takes an item off a sim rather than charging a family) and TS1 has no
        // inventory to take it back out of.
        //
        // What "exact" means here, stated once so it is not over-claimed. Exact is about the
        // things the audit's concern is: the family's budget, the set of objects on the lot, and
        // each object's bought value and provenance all return to where they were. It is not a
        // promise of object identity - an object that a redo put back is a *new* instance bought
        // by a net command, with a new object id and a new persist id, exactly as if the player
        // had sold it and bought it again, and so with none of the original's per-object save-game
        // state (its wear, whatever was sitting on it, its thread). Restoring that would need an
        // engine-side object marshalling round trip, which is not a small change; without one, a
        // net re-buy is the finest undo available, and it is the operation the audit asked for.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// The build/buy undo stack for this lot. Lives on the object holder because the holder is
        /// what records the actions, and dies with it, which is right: a different lot is a
        /// different set of objects and a stack of ids that mean nothing there.
        /// </summary>
        public readonly BuildUndoStack Undo = new BuildUndoStack();

        /// <summary>
        /// Placements whose object the engine has not created yet, oldest first.
        /// </summary>
        /// <remarks>
        /// A buy is verified, charged and then executed on a later tick, so for the frame or two
        /// after the click the object does not exist and its id is unknown. A record cannot be
        /// completed until then, and it must not be pushed before then, so it waits here. Only the
        /// head is worked on, so the records reach the stack in the order the player made them -
        /// two placements in consecutive frames must not be able to overtake each other.
        /// </remarks>
        private readonly Queue<PendingPlacement> pending = new Queue<PendingPlacement>();

        /// <summary>A placement waiting for the engine to create the object it is about.</summary>
        private class PendingPlacement
        {
            public BuildUndoEntry Entry;

            /// <summary>
            /// The highest object id on the lot when the click was made. The VM hands out ids
            /// monotonically (<c>VM.AddEntity</c> assigns then increments), so the object a
            /// placement created is the one at the recorded tile whose id is above this. Without
            /// it, two placements of the same item on the same tile inside the settle window
            /// would both resolve to the same object.
            /// </summary>
            public short ObjectIdFloor;

            /// <summary>Frames waited. A buy that never lands must not block the stack forever.</summary>
            public int Frames;
        }

        /// <summary>
        /// How many frames a placement waits for its object before it is given up on.
        /// </summary>
        /// <remarks>
        /// The command path is a thread hop and a tick, so a couple of frames is the normal cost;
        /// sixty is a second or two of wall clock at 30Hz (less if the tick is called more than
        /// once a frame, which it is - see <see cref="TickUndo"/>), and is long past the point
        /// where a buy is going to appear. Past it the placement is dropped: the transaction
        /// either failed (and refunded itself) or the player has left the lot, and in both cases
        /// there is no object and nothing to undo.
        /// </remarks>
        private const int PlacementSettleFrames = 60;

        public UIObjectHolder(VM vm, World World, UILotControl parent)
        {
            this.vm = vm;
            this.World = World;
            ParentControl = parent;
        }

        public void SetSelected(VMMultitileGroup Group)
        {
            if (Holding != null) ClearSelected();
            Holding = new UIObjectSelection();
            Holding.Group = Group;
            if (!UseNet) Holding.Group.ExecuteEntryPoint(12, vm.Context); //User Pickup
            Holding.PreviousTile = Holding.Group.BaseObject.Position;
            Holding.Dir = Group.Objects[0].Direction;
            Holding.OriginalDir = Holding.Dir;
            VMEntity[] CursorTiles = new VMEntity[Group.Objects.Count];
            for (int i = 0; i < Group.Objects.Count; i++)
            {
                var target = Group.Objects[i];
                target.SetRoom(65535);
                if (target is VMGameObject) ((ObjectComponent)target.WorldUI).ForceDynamic = true;
                CursorTiles[i] = vm.Context.CreateObjectInstance(0x00000437, new LotTilePos(target.Position), FSO.LotView.Model.Direction.NORTH, true).Objects[0];
                CursorTiles[i].SetPosition(target.Position, Direction.NORTH, vm.Context);
                ((ObjectComponent)CursorTiles[i].WorldUI).ForceDynamic = true;
            }
            Holding.TilePosOffset = new Vector2(0, 0);
            Holding.CursorTiles = CursorTiles;

            uint guid;
            var bobj = Group.BaseObject;
            guid = bobj.Object.OBJ.GUID;
            if (bobj.MasterDefinition != null) guid = bobj.MasterDefinition.GUID;
            var catalogItem = Content.Get().WorldCatalog.GetItemByGUID(guid);
            if (catalogItem != null)
            {
                var price = (int)catalogItem.Value.Price;
                var dcPercent = VMBuildableAreaInfo.GetDiscountFor(catalogItem.Value, vm);
                //One formula, shared with the undo stack. The amount the engine will charge is the
                //amount an undo of this placement has to refund, and two copies of this
                //arithmetic is exactly how those two numbers drift apart.
                int finalPrice;
                if (!BuildUndoPolicy.TryChargedPrice(price, dcPercent, out finalPrice))
                {
                    //A catalogue price big enough to overflow the multiplication, which is not a
                    //real price. Keep the old answer rather than change what the budget gate does.
                    finalPrice = (price * (100 - dcPercent)) / 100;
                }
                Holding.Price = finalPrice;
            }
        }

        public void MoveSelected(Vector2 pos, sbyte level)
        {
            Holding.TilePos = pos;
            Holding.Level = level;

            //first, eject the object from any slots
            for (int i = 0; i < Holding.Group.Objects.Count; i++)
            {
                var obj = Holding.Group.Objects[i];
                if (obj.Container != null)
                {
                    obj.Container.ClearSlot(obj.ContainerSlot);
                }
            }

            //rotate through to try all configurations
            var dir = Holding.Dir;
            VMPlacementError status = VMPlacementError.Success;
            if (!Holding.IsBought && !vm.PlatformState.CanPlaceNewUserObject(vm)) status = VMPlacementError.TooManyObjectsOnTheLot;
            else
            {
                for (int i = 0; i < 4; i++)
                {
                    status = Holding.Group.ChangePosition(LotTilePos.FromBigTile((short)pos.X, (short)pos.Y, World.State.Level), dir, vm.Context, VMPlaceRequestFlags.UserPlacement).Status;
                    if (status != VMPlacementError.MustBeAgainstWall) break;
                    dir = (Direction)((((int)dir << 6) & 255) | ((int)dir >> 2));
                }
                if (Holding.Dir != dir) Holding.Dir = dir;
            }

            if (status != VMPlacementError.Success)
            {
                Holding.Group.ChangePosition(LotTilePos.OUT_OF_WORLD, Holding.Dir, vm.Context, VMPlaceRequestFlags.UserPlacement);

                // A refusal *onto another object* is the report that has no way to be acted on:
                // the tooltip says "cannot be placed here" and nothing says which rule said no.
                // The engine's own GetObjPlace is not reachable from here, so the one thing the
                // holder can add is the subject - what the cursor was over, and whether it is a
                // container - which is what turns the next report into a diagnosis.
                LogPlacementRefusal(status, pos);

                Holding.Group.SetVisualPosition(new Vector3(pos,
                (((Holding.Group.Objects[0].GetValue(VMStackObjectVariable.AllowedHeightFlags) & 1) == 1) ? 0 : 4f / 5f) + (World.State.Level - 1) * 2.95f),
                //^ if we can't be placed on the floor, default to table height.
                Holding.Dir, vm.Context);
            }

            for (int i = 0; i < Holding.Group.Objects.Count; i++)
            {
                var target = Holding.Group.Objects[i];
                var tpos = target.VisualPosition;
                tpos.Z = (World.State.Level - 1) * 2.95f;
                Holding.CursorTiles[i].MultitileGroup.SetVisualPosition(tpos, Holding.Dir, vm.Context);
            }
            // Forgotten on a success, so a player who is refused on one tile, moves somewhere the
            // placement works, and comes back to the first tile gets a second line rather than
            // silence.
            if (status == VMPlacementError.Success) LastPlacementRefusal = null;
            Holding.CanPlace = status;
        }

        /// <summary>
        /// Reports a refused placement, naming the object the cursor was over.
        /// </summary>
        /// <remarks>
        /// <para>
        /// "Placing a coffeemaker on a dishwasher does nothing" is not a report anybody can act on.
        /// The tooltip the player sees is the same one sentence for every rule that can refuse -
        /// and the rules live in <c>VMContext.GetObjPlace</c>, on the other side of an assembly
        /// boundary from this panel, and answer with a code rather than with a subject. What the
        /// holder can add, and the only thing missing from the report, is <em>what the cursor was
        /// over</em>: whether there was anything there at all, and if so whether it is a container
        /// (a counter, a table, a dishwasher) or just a solid object. That one word is the
        /// difference between "the surface height rule refused it" and "there was no dishwasher
        /// there".
        /// </para>
        /// <para>
        /// Throttled by the same reason string as the engine-side line, so a player holding the
        /// cursor still on a refused tile produces one line rather than one per frame. It is
        /// <c>Info</c> and not <c>Warn</c>: refusing a placement is a normal outcome of the game,
        /// not a defect, and the value here is entirely in what a bug report contains.
        /// </para>
        /// </remarks>
        private void LogPlacementRefusal(VMPlacementError status, Vector2 bigTilePos)
        {
            try
            {
                var tile = LotTilePos.FromBigTile((short)bigTilePos.X, (short)bigTilePos.Y, World.State.Level);
                var over = DescribePlacementSubject(tile);
                var line = "Build/buy placement refused: " + status + " at tile (" + (tile.x / 16) + ", "
                    + (tile.y / 16) + ", " + tile.Level + ") for object 0x"
                    + (Holding?.Group?.BaseObject?.Object?.OBJ?.GUID ?? 0).ToString("X8") + " - " + over;
                if (line == LastPlacementRefusal) return;
                LastPlacementRefusal = line;
                FSO.Content.Utils.ContentLog.Info(line);
            }
            catch (Exception e)
            {
                //A diagnostic must never be the reason a placement gesture fails.
                FSO.Content.Utils.ContentLog.Warn("Could not report a refused placement", e);
            }
        }

        /// <summary>The last refusal line written, so an unmoving cursor does not repeat itself.</summary>
        private string LastPlacementRefusal;

        /// <summary>
        /// What the cursor was over, as a sentence: nothing, a container, or a solid object, with
        /// the container's slot count and surface height.
        /// </summary>
        /// <remarks>
        /// The rule, over plain numbers, so it is pinned by tests without a lot: an empty tile and
        /// a tile holding something that is not a container read the same way, and the one that
        /// matters for "why will this not go on that" is whether the thing under the cursor has a
        /// slot and how high it is. <paramref name="slotCount"/> and <paramref name="slotHeight"/>
        /// are -1 for "there is no container here".
        /// </remarks>
        public static string DescribePlacementSubject(int slotCount, int slotHeight)
        {
            if (slotCount < 0) return "nothing on this tile";
            if (slotCount == 0) return "an object with no containment slots on this tile";
            return "a container with " + slotCount + " slot(s), surface height "
                + (slotHeight < 0 ? "unknown" : slotHeight.ToString()) + " - the height/weight/slot rules refused it";
        }

        /// <summary>
        /// The subject half of <see cref="DescribePlacementSubject"/>, for the lot. The container
        /// with the lowest object id wins so the answer does not change frame to frame when two
        /// objects share a tile.
        /// </summary>
        private string DescribePlacementSubject(LotTilePos tile)
        {
            VMEntity subject = null;
            var candidates = vm?.Context?.ObjectQueries?.GetObjectsAt(tile);
            if (candidates != null)
            {
                for (int i = 0; i < candidates.Count; i++)
                {
                    var candidate = candidates[i];
                    if (candidate == null || candidate is VMAvatar) continue;
                    if (subject == null || candidate.ObjectID < subject.ObjectID) subject = candidate;
                }
            }
            if (subject == null) return DescribePlacementSubject(-1, -1);

            return "object " + subject.ObjectID + " (objd 0x" + (subject.Object?.OBJ?.GUID ?? 0).ToString("X8")
                + ") - " + DescribePlacementSubject(subject.TotalSlots(), subject.GetSlotHeight(0));
        }

        public void ClearSelected()
        {            //TODO: selected items are only spooky ghosts of the items themselves.
            //      ...so that they dont cause serverside desyncs
            //      and so that clearing selections doesnt delete already placed objects.
            if (Holding != null)
            {
                //try to copy back visible flags from holding copy
                if (Holding.RealEnt != null && UseNet)
                {
                    RecursiveUnhide(Holding.Group.BaseObject, Holding.RealEnt);
                }

                if (UseNet || !Holding.IsBought)
                {
                    RecursiveDelete(vm.Context, Holding.Group.BaseObject);
                } else
                {
                    if (Holding.RealEnt == null) Holding.RealEnt = Holding.Group.BaseObject;
                    Holding.RealEnt.SetPosition(Holding.PreviousTile, Holding.Dir, vm.Context);
                    Holding.RealEnt.MultitileGroup.ExecuteEntryPoint(11, vm.Context); //User Placement
                }

                for (int i = 0; i < Holding.CursorTiles.Length; i++)
                {
                    Holding.CursorTiles[i].Delete(true, vm.Context);
                    ((ObjectComponent)Holding.CursorTiles[i].WorldUI).ForceDynamic = false;
                }
            }
            Holding = null;
            vm.Tick();
        }

        private void RecursiveDelete(VMContext context, VMEntity real)
        {
            var rgrp = real.MultitileGroup;
            for (int i = 0; i < rgrp.Objects.Count; i++)
            {
                var slots = rgrp.Objects[i].TotalSlots();
                var objs = new List<VMEntity>();
                for (int j = 0; j < slots; j++)
                {
                    var slot = rgrp.Objects[i].GetSlot(j);
                    if (slot != null)
                    {
                        objs.Add(slot);
                    }

                }
                foreach (var obj in objs) RecursiveDelete(context, obj);
            }
            rgrp.Delete(context);
        }

        public void MouseDown(UpdateState state)
        {
            MouseIsDown = true;
            MouseDownX = state.MouseState.X;
            MouseDownY = state.MouseState.Y;
        }

        public void MouseUp(UpdateState state)
        {
            MouseIsDown = false;
            if (Holding != null && Holding.Clicked)
            {
                if (Holding.CanPlace == VMPlacementError.Success)
                {
                    HITVM.Get().PlaySoundEvent((Holding.IsBought) ? UISounds.ObjectMovePlace : UISounds.ObjectPlace);
                    //ExecuteEntryPoint(11); //User Placement
                    var putDown = Holding;
                    //Whether this was picked up off the lot or taken from the catalogue, read
                    //before the branches below can change MoveTarget. IsBought is MoveTarget != 0,
                    //and the non-net branch assigns to it - so the answer has to be taken while it
                    //is still the answer.
                    var wasBought = Holding.IsBought;
                    var pos = Holding.Group.BaseObject.Position;
                    if (Holding.IsBought)
                    {
                        if (UseNet)
                        {
                            vm.SendCommand(new VMNetMoveObjectCmd
                            {
                                ObjectID = Holding.MoveTarget,
                                dir = Holding.Dir,
                                level = pos.Level,
                                x = pos.x,
                                y = pos.y
                            });
                        }
                        else
                        {
                            //otherwise we're kind of already here?
                            Holding.PreviousTile = new LotTilePos(pos.x, pos.y, pos.Level);
                        }
                    }
                    else if (Holding.InventoryPID > 0)
                    {
                        vm.SendCommand(new VMNetPlaceInventoryCmd
                        {
                            ObjectPID = Holding.InventoryPID,
                            dir = Holding.Dir,
                            level = pos.Level,
                            x = pos.x,
                            y = pos.y
                        });
                    }
                    else
                    {
                        var GUID = (Holding.Group.MultiTile) ? Holding.Group.BaseObject.MasterDefinition.GUID : Holding.Group.BaseObject.Object.OBJ.GUID;
                        if (UseNet || ParentControl.ActiveEntity != null)
                        {
                            vm.SendCommand(new VMNetBuyObjectCmd
                            {
                                GUID = GUID,
                                dir = Holding.Dir,
                                level = pos.Level,
                                x = pos.x,
                                y = pos.y
                            });
                        } else
                        {
                            Holding.MoveTarget = Holding.Group.BaseObject.ObjectID;
                            Holding.PreviousTile = new LotTilePos(pos.x, pos.y, pos.Level);
                        }
                    }
                    // Recorded after the command is queued, so the three cases have been told
                    // apart and the tile the object is going to is final. See RecordPutDown.
                    RecordPutDown(putDown, pos, wasBought);
                    ClearSelected();
                    if (OnPutDown != null) OnPutDown(putDown, state); //call this after so that buy mode etc can produce more.
                }
            }

            state.UIState.TooltipProperties.Show = false;
            state.UIState.TooltipProperties.Opacity = 0;
            ShowTooltip = false;
        }

        public void SellBack(UIElement button)
        {
            if (Holding == null || !Roommate) return;
            if (Holding.IsBought)
            {
                if (Holding.CanDelete)
                {
                    //Read the value off the object *before* the delete is queued. The delete
                    //refunds MultitileGroup.Price on the server, and after it has run the object
                    //is gone; this is the only moment that number is readable, and it is the
                    //number the undo has to match.
                    RecordSale(Holding);
                    vm.SendCommand(new VMNetDeleteObjectCmd
                    {
                        ObjectID = Holding.MoveTarget,
                        CleanupAll = true
                    });
                    HITVM.Get().PlaySoundEvent(UISounds.MoneyBack);
                }
                else
                {
                    ShowErrorAtMouse(LastState, VMPlacementError.CannotDeleteObject);
                    return;
                }
            }
            OnDelete?.Invoke(Holding, null); //TODO: cleanup callbacks which don't need updatestate into another delegate.
            ClearSelected();
        }

        public void Cancel()
        {
            if (Holding == null) return;
            Holding.Dir = Holding.OriginalDir;
            OnDelete?.Invoke(Holding, null);
            ClearSelected();
        }

        // -------------------------------------------------------------------------------------
        // Recording an action.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Records the sale of the held object, if - and only if - buying it back would cost
        /// exactly what selling it refunded.
        /// </summary>
        /// <remarks>
        /// The whole check is in <see cref="BuildUndoPolicy.CanUndoSale"/>; this is the part that
        /// needs the lot, namely reading the object's value and the catalogue's current price for
        /// it. When they disagree the sale is not recorded at all: an undo that re-bought the
        /// object for a different number would leave the family's budget permanently out by the
        /// difference, which is a far worse outcome than having nothing to undo.
        /// </remarks>
        private void RecordSale(UIObjectSelection holding)
        {
            var real = holding.RealEnt;
            if (real == null) return;

            var group = real.MultitileGroup;
            if (group == null || group.BaseObject == null) return;

            int refunded;
            if (!BuildUndoPolicy.TryRefundValue(group.InitialPrice, WearOf(group.BaseObject), out refunded)) return;

            uint guid = GroupGuid(group);
            if (guid == 0) return;

            var item = Content.Get().WorldCatalog.GetItemByGUID(guid);
            if (item == null) return; //not priced today, so a re-purchase would charge nothing

            if (!BuildUndoPolicy.CanUndoSale(refunded, (int)item.Value.Price, DiscountFor(item.Value))) return;


            var pos = group.BaseObject.Position;
            Undo.Push(new BuildUndoEntry
            {
                Kind = BuildUndoKind.Sell,
                Guid = guid,
                X = pos.x,
                Y = pos.y,
                Level = pos.Level,
                Dir = (byte)group.BaseObject.Direction,
                Value = refunded,
                ObjectId = holding.MoveTarget,
                Settled = true,
                Exact = true
            });
        }

        /// <summary>
        /// Records the placement of a bought object, or of a moved one, or of neither.
        /// </summary>
        /// <remarks>
        /// Called after the net command has been queued, because that is where the three cases
        /// have already been distinguished and the command's own fields are to hand.
        /// <list type="bullet">
        /// <item><b>Move</b> - an object that was already bought. Always exact: no money moves, so
        /// nothing can be inexact about it. Pushed immediately, because both the id and the tile it
        /// came from are known right now.</item>
        /// <item><b>Place</b> - a catalogue buy. Queued as pending, because the object does not
        /// exist yet; <see cref="TickUndo"/> completes it once the engine has made it and checked
        /// that it refunds what it charged.</item>
        /// <item><b>Nothing</b> - an object placed out of a sim's inventory. That is a different
        /// money path (it removes an item from a sim rather than charging a family) and TS1 has no
        /// inventory to take it back out of, so it is not recorded. Documented rather than
        /// approximated.</item>
        /// </list>
        /// </remarks>
        /// <param name="holding">The selection that was put down.</param>
        /// <param name="placedAt">The base tile it was put down on.</param>
        /// <param name="wasBought">Whether the object was picked up off the lot rather than from the catalogue.</param>
        private void RecordPutDown(UIObjectSelection holding, LotTilePos placedAt, bool wasBought)
        {
            if (wasBought)
            {
                var real = holding.RealEnt;
                if (real == null) return;

                // Where it came from is the real object's own tile and direction. Not
                // Holding.PreviousTile: that field is the *ghost's* base tile, which is the same
                // number today but is written by the put-down path itself, and using the real
                // object is the one that cannot drift.
                var from = real.Position;
                var fromDir = real.Direction;

                Undo.Push(new BuildUndoEntry
                {
                    Kind = BuildUndoKind.Move,
                    Guid = GroupGuid(real.MultitileGroup),
                    ObjectId = holding.MoveTarget,
                    X = placedAt.x,
                    Y = placedAt.y,
                    Level = placedAt.Level,
                    Dir = (byte)holding.Dir,
                    FromX = from.x,
                    FromY = from.y,
                    FromLevel = from.Level,
                    FromDir = (byte)fromDir,
                    Settled = true,
                    Exact = true
                });
                return;
            }

            if (holding.InventoryPID > 0) return; //see remarks: not undoable in TS1, and not recorded

            uint guid = GroupGuid(holding.Group);
            if (guid == 0) return;

            pending.Enqueue(new PendingPlacement
            {
                Entry = new BuildUndoEntry
                {
                    Kind = BuildUndoKind.Place,
                    Guid = guid,
                    X = placedAt.x,
                    Y = placedAt.y,
                    Level = placedAt.Level,
                    Dir = (byte)holding.Dir,
                    // What the buy is going to charge, worked out the same way the object holder
                    // already works it out to gate affordability on the budget. The settle step
                    // re-reads the engine's own number and throws the record away if the two
                    // differ, so this is a prediction that has to be confirmed, not a fact.
                    Value = holding.Price
                },
                ObjectIdFloor = HighestObjectId()
            });
        }

        // -------------------------------------------------------------------------------------
        // Settling, and undo/redo.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Completes any placement the engine has caught up with, and forgets the ones it never
        /// will. Safe to call more than once a frame and from more than one caller.
        /// </summary>
        /// <remarks>
        /// Only the head of the queue is worked on, so entries reach the stack in the order the
        /// player made them.
        /// </remarks>
        public void TickUndo()
        {
            while (pending.Count > 0)
            {
                var p = pending.Peek();
                p.Frames++;

                var found = FindPlacedObject(p.Entry, p.ObjectIdFloor);
                if (found != null)
                {
                    var group = found.MultitileGroup;
                    int refund;
                    bool exact = group != null
                        && BuildUndoPolicy.TryRefundValue(group.InitialPrice, WearOf(found), out refund)
                        && BuildUndoPolicy.RefundIsExact(p.Entry.Value, refund);

                    if (exact)
                    {
                        p.Entry.ObjectId = found.ObjectID;
                        p.Entry.Settled = true;
                        p.Entry.Exact = true;
                        Undo.Push(p.Entry);
                    }
                    // Not exact: the object exists but does not refund what it cost (a content
                    // file whose OBJD price and catalogue price disagree, or a buildable-area
                    // discount in play). Dropping the record is the only honest answer, and
                    // dropping it leaves the rest of the stack usable.
                    pending.Dequeue();
                    continue;
                }

                if (p.Frames >= PlacementSettleFrames)
                {
                    //The buy never landed - refused, refunded, or abandoned. Nothing is on the
                    //lot, so there is nothing to undo and the queue must not stall behind it.
                    pending.Dequeue();
                    continue;
                }
                break; //head is still waiting; the ones behind it are not looked at yet
            }
        }

        /// <summary>
        /// The object a pending placement created: a live game object standing on the recorded
        /// base tile, leading its own group, of the recorded guid, and with an id newer than
        /// anything that existed when the click was made.
        /// </summary>
        private VMEntity FindPlacedObject(BuildUndoEntry entry, short objectIdFloor)
        {
            return FindBaseObjectAt(entry.Guid, entry.X, entry.Y, entry.Level, objectIdFloor);
        }

        /// <summary>
        /// The live game object of <paramref name="guid"/> whose <em>base tile</em> is the given
        /// one and whose id is above <paramref name="objectIdFloorExclusive"/>, or null.
        /// </summary>
        /// <param name="objectIdFloorExclusive">
        /// The highest object id that existed before the action, or 0 for no floor. The VM hands
        /// ids out monotonically, so this is what tells "the object this action created" apart from
        /// "an object that was already standing there" when both are of the same kind on the same
        /// tile. The undo and redo paths pass 0, because by then the object they mean is the only
        /// one of its kind that can be on that tile.
        /// </param>
        private VMEntity FindBaseObjectAt(uint guid, short x, short y, sbyte level, short objectIdFloorExclusive)
        {
            if (guid == 0) return null;

            var candidates = vm.Context.ObjectQueries.GetObjectsAt(new LotTilePos(x, y, level));
            if (candidates == null) return null;

            for (int i = 0; i < candidates.Count; i++)
            {
                var obj = candidates[i];
                if (!(obj is VMGameObject) || obj.Dead) continue;
                if (obj.ObjectID <= objectIdFloorExclusive) continue;

                var group = obj.MultitileGroup;
                if (group == null || !ReferenceEquals(group.BaseObject, obj)) continue;
                if (GroupGuid(group) != guid) continue;

                return obj;
            }
            return null;
        }

        /// <summary>
        /// Undoes the top of the stack, by sending the net command that is its inverse.
        /// </summary>
        /// <returns>True when a command was sent.</returns>
        /// <remarks>
        /// <para>
        /// A held object is put down first. Undoing out from under a half-finished gesture would
        /// leave the ghost, the cursor tile and the query panel describing an object the stack
        /// has just removed; putting it down is the same thing Escape does and costs the player
        /// nothing they had not already signalled they did not want.
        /// </para>
        ///
        /// <para>
        /// A command the lot refuses - a tile that has been built on since, an object another
        /// player has taken - leaves the lot exactly as it was and the stack one step further
        /// along. It never leaves the lot half-edited, because the lot is the only thing that
        /// edits it.
        /// </para>
        /// </remarks>
        public bool UndoLast()
        {
            var entry = Undo.PeekUndo;
            if (entry == null) return false;

            if (Holding != null) ClearSelected();
            Undo.Undo();

            switch (entry.Kind)
            {
                case BuildUndoKind.Place:
                    // Undoing a placement is a sale of the object standing on the recorded tile, and
                    // the sale refunds MultitileGroup.Price - which the settle step already proved is
                    // the amount the buy charged. Resolved by position rather than by the id the
                    // record carries: a placement that has been undone and redone is a new object
                    // with a new id, and a delete sent to the old one would remove nothing and
                    // refund nothing, leaving the family a placement short.
                    SendDelete(FindObjectAt(entry.Guid, entry.X, entry.Y, entry.Level), UISounds.MoneyBack);
                    break;

                case BuildUndoKind.Sell:
                    // Undoing a sale is a buy, and it charges the catalogue price - which
                    // RecordSale already proved is the amount the sale refunded.
                    SendBuy(entry);
                    HITVM.Get().PlaySoundEvent(UISounds.ObjectPlace);
                    break;

                case BuildUndoKind.Move:
                    // Resolved at the tile the object is standing on now, which for an undo is the
                    // tile it was moved *to*.
                    SendMove(entry, FindObjectAt(entry.Guid, entry.X, entry.Y, entry.Level),
                        entry.FromX, entry.FromY, entry.FromLevel, entry.FromDir);
                    break;
            }

            OnUndoRedo?.Invoke();
            return true;
        }

        /// <summary>
        /// Repeats the top of the redo stack, by sending the net command that was the original
        /// action.
        /// </summary>
        /// <returns>True when a command was sent.</returns>
        /// <remarks>
        /// The same refusals as <see cref="UndoLast"/> and the same answer: a command the lot will
        /// not run changes nothing, and the object simply does not come back. In particular a
        /// re-purchase that the lot refuses refunds itself inside
        /// <c>VMNetBuyObjectCmd.TryPlace</c>, so the budget is left where it was rather than half
        /// spent.
        /// </remarks>
        public bool RedoLast()
        {
            var entry = Undo.PeekRedo;
            if (entry == null) return false;

            if (Holding != null) ClearSelected();
            Undo.Redo();

            switch (entry.Kind)
            {
                case BuildUndoKind.Place:
                case BuildUndoKind.Sell:
                    // Both redo as a placement: the object goes back on the tile it was on, and
                    // a Place re-buy charges what the original buy charged (the entry's Value) by
                    // the same formula, while a Sell re-buy charges what the sale refunded by
                    // construction. Either way the budget ends up where it was. The record's
                    // object id is now stale and is left alone - nothing acts on it.
                    SendBuy(entry);
                    HITVM.Get().PlaySoundEvent(UISounds.ObjectPlace);
                    break;

                case BuildUndoKind.Move:
                    // Resolved at the tile the object is standing on now, which for a redo is the
                    // tile it was moved *from*.
                    SendMove(entry, FindObjectAt(entry.Guid, entry.FromX, entry.FromY, entry.FromLevel),
                        entry.X, entry.Y, entry.Level, entry.Dir);
                    break;
            }

            OnUndoRedo?.Invoke();
            return true;
        }

        /// <summary>
        /// The lot's object id for the object, or 0 when there is not one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is how every undo and redo finds its subject, in place of the id stored on the
        /// record, and the reason is that the id is not stable. The VM hands object ids out
        /// monotonically and never reuses one, so an object that has been sold and bought back -
        /// which is exactly what undoing a sale does - is a different object afterwards, and a
        /// command addressed to the old id would be dropped by the lot as addressing nothing.
        /// </para>
        ///
        /// <para>
        /// The tile and the guid together are unambiguous. A placement only succeeds if every one of
        /// its tiles was free, so two live objects of one guid can never share a base tile, and
        /// base-object-at-that-tile is checked rather than any-object-at-that-tile so a multitile's
        /// sub-tile is never mistaken for the whole thing.
        /// </para>
        /// </remarks>
        private short FindObjectAt(uint guid, short x, short y, sbyte level)
        {
            var obj = FindBaseObjectAt(guid, x, y, level, 0);
            return obj == null ? (short)0 : obj.ObjectID;
        }

        private void SendMove(BuildUndoEntry entry, short objectId, short x, short y, sbyte level, byte dir)
        {
            if (objectId == 0) return; //nothing there to move; the lot is already in the target state
            vm.SendCommand(new VMNetMoveObjectCmd
            {
                ObjectID = objectId,
                dir = (Direction)dir,
                level = level,
                x = x,
                y = y
            });
            HITVM.Get().PlaySoundEvent(UISounds.ObjectMovePlace);
        }

        private void SendBuy(BuildUndoEntry entry)
        {
            vm.SendCommand(new VMNetBuyObjectCmd
            {
                GUID = entry.Guid,
                dir = (Direction)entry.Dir,
                level = entry.Level,
                x = entry.X,
                y = entry.Y
            });
        }

        private void SendDelete(short objectId, string sound)
        {
            if (objectId == 0) return;
            vm.SendCommand(new VMNetDeleteObjectCmd
            {
                ObjectID = objectId,
                CleanupAll = true
            });
            HITVM.Get().PlaySoundEvent(sound);
        }

        /// <summary>The catalog GUID of a multitile group: the master's for a multitile, the object's otherwise.</summary>
        private static uint GroupGuid(VMMultitileGroup group)
        {
            var baseObj = group?.BaseObject;
            if (baseObj == null) return 0;
            return (baseObj.MasterDefinition ?? baseObj.Object.OBJ).GUID;
        }

        /// <summary>
        /// A game object's TS1 wear, or 0 for anything that has none - which is the value
        /// <c>VMMultitileGroup.Price</c> itself falls back to.
        /// </summary>
        private static int WearOf(VMEntity obj)
        {
            return (obj as VMGameObject)?.ObjectState?.Wear ?? 0;
        }

        /// <summary>
        /// The buildable-area discount that applies to a catalogue item, as a percentage. Always 0
        /// in TS1, where <c>VMBuildableAreaInfo.GetDiscountFor</c> returns before it looks at the
        /// item at all - so the charge this undo has to match is the catalogue price, and the
        /// exactness test is a straight equality.
        /// </summary>
        private int DiscountFor(FSO.Content.Interfaces.ObjectCatalogItem item)
        {
            return VMBuildableAreaInfo.GetDiscountFor(item, vm);
        }

        /// <summary>
        /// The highest object id currently on the lot, or 0 on an empty one. Used as a floor for
        /// "which object did this placement create".
        /// </summary>
        private short HighestObjectId()
        {
            var entities = vm.Entities;
            if (entities == null || entities.Count == 0) return 0;
            // VM.Entities is kept sorted by object id (VM.AddToObjList inserts in order).
            return entities[entities.Count - 1].ObjectID;
        }

        public void Update(UpdateState state, bool scrolled)
        {
            UseNet = true;
            LastState = state;

            // A placement is recorded at the click and only becomes undoable once the engine has
            // made the object, so something has to finish the record. This is the natural place for
            // it - except that a held build tool (UIWallPlacer and friends) replaces this whole
            // method, which is why UIMainPanel.Update calls TickUndo as well. Both call sites are
            // idempotent, and a second call in the same frame simply finds nothing left to do.
            TickUndo();

            if (ShowTooltip) state.UIState.TooltipProperties.UpdateDead = false;
            MouseClicked = (MouseIsDown && (!MouseWasDown));

            CursorType cur = CursorType.SimsMove;
            if (Holding != null)
            {
                if (Roommate) cur = CursorType.SimsPlace;
                if (state.KeyboardState.IsKeyDown(Keys.Delete))
                {
                    SellBack(null);
                }
                else if (state.KeyboardState.IsKeyDown(Keys.Escape))
                {
                    Cancel();
                }
            }
            if (Holding != null && Roommate)
            {
                if (MouseClicked) Holding.Clicked = true;
                var mpos = FSOEnvironment.SoftwareKeyboard ? new Point(UIScreen.Current.ScreenWidth / 2, UIScreen.Current.ScreenHeight / 2) : state.MouseState.Position;
                if (MouseIsDown && Holding.Clicked)
                {
                    bool updatePos = MouseClicked;
                    int xDiff = mpos.X - MouseDownX;
                    int yDiff = mpos.Y - MouseDownY;
                    cur = CursorType.SimsRotate;
                    if (Math.Sqrt(xDiff * xDiff + yDiff * yDiff) > 64 && !FSOEnvironment.SoftwareKeyboard)
                    {
                        var from = World.EstTileAtPosWithScroll(new Vector2(MouseDownX, MouseDownY));
                        var target = World.EstTileAtPosWithScroll(mpos.ToVector2());

                        var vec = target - from;
                        var dir = Math.Atan2(vec.Y, vec.X);
                        dir += Math.PI / 2;
                        if (dir < 0) dir += Math.PI * 2;
                        var newDir = (Direction)(1 << (((int)Math.Round(dir / (Math.PI / 2)) % 4) * 2));

                        if (newDir != Holding.Dir || MouseClicked)
                        {
                            updatePos = true;
                            HITVM.Get().PlaySoundEvent(UISounds.ObjectRotate);
                            Holding.Dir = newDir;
                        }
                    }
                    if (updatePos)
                    {
                        MoveSelected(Holding.TilePos, Holding.Level);
                        //anything the player doesn't already own has to be paid for, so gate every
                        //held object on the remaining budget (free when the budget is unlimited,
                        //eg. no active family). Moving an already-bought object is always free.
                        if (!Holding.IsBought && Holding.CanPlace == VMPlacementError.Success &&
                            !BudgetService.CanAfford(Holding.Price))
                            Holding.CanPlace = VMPlacementError.InsufficientFunds;
                        if (Holding.CanPlace != VMPlacementError.Success)
                        {
                            state.UIState.TooltipProperties.Show = true;
                            state.UIState.TooltipProperties.Color = Color.Black;
                            state.UIState.TooltipProperties.Opacity = 1;
                            state.UIState.TooltipProperties.Position = new Vector2(MouseDownX,
                                MouseDownY);
                            state.UIState.Tooltip = GameFacade.Strings.GetString("137", ((int)Holding.CanPlace).ToString()); //"kPErr" + Holding.CanPlace.ToString()
                                //+ ((Holding.CanPlace == VMPlacementError.CannotPlaceComputerOnEndTable) ? "," : ""));
                            // comma added to curcumvent problem with language file. We should probably just index these with numbers?
                            state.UIState.TooltipProperties.UpdateDead = false;
                            ShowTooltip = true;
                            HITVM.Get().PlaySoundEvent(UISounds.Error);
                        }
                        else
                        {
                            state.UIState.TooltipProperties.Show = false;
                            state.UIState.TooltipProperties.Opacity = 0;
                            ShowTooltip = false;
                        }
                    }
                }
                else
                {
                    var tilePos = World.EstTileAtPosWithScroll(new Vector2(mpos.X, mpos.Y) / FSOEnvironment.DPIScaleFactor) + Holding.TilePosOffset;
                    MoveSelected(tilePos, 1);
                }
            }
            else if (MouseClicked)
            {
                //not holding an object, but one can be selected
                var newHover = World.GetObjectIDAtScreenPos(state.MouseState.X, state.MouseState.Y, GameFacade.GraphicsDevice);

                // Eyedropper mode - report the style under the cursor instead of picking it up.
                // Objects are tried first so that a door or window set into a wall reads as the
                // door rather than as the wall behind it; failing that, the floor tile. Wallpaper
                // and fences are deliberately not handled - see the enum below.
                if (EyedropperMode)
                {
                    bool handled = false;

                    if (newHover != 0 && vm.GetObjectById(newHover) is VMGameObject)
                    {
                        var obj = vm.GetObjectById(newHover);
                        uint guid = obj.Object.OBJ.GUID;
                        if (obj.MasterDefinition != null) guid = obj.MasterDefinition.GUID;

                        var catalogItem = Content.Get().WorldCatalog.GetItemByGUID(guid);
                        if (catalogItem != null)
                        {
                            OnEyedropperPick?.Invoke(guid);
                            handled = true;
                        }
                    }

                    if (!handled)
                    {
                        TryEyedropperFloorPick(state);
                    }

                    // An eyedropper click never becomes a pickup, so deliberately fall through
                    // without running the normal click handling below.
                }
                else if (MouseClicked && (newHover != 0) && (vm.GetObjectById(newHover) is VMGameObject))
                {
                    var objGroup = vm.GetObjectById(newHover).MultitileGroup;
                    var objBasePos = objGroup.BaseObject.Position;
                    var success = (Roommate || objGroup.SalePrice > -1) ? objGroup.BaseObject.IsUserMovable(vm.Context, false) : VMPlacementError.ObjectNotOwnedByYou;
                    if (GameFacade.EnableMod) success = VMPlacementError.Success;
                    if (objBasePos.Level != World.State.Level) success = VMPlacementError.CantEffectFirstLevelFromSecondLevel;
                    if (success == VMPlacementError.Success)
                    {
                        var ghostGroup = (UseNet) ? vm.Context.GhostCopyGroup(objGroup) : objGroup ;
                        ghostGroup.ChangePosition(objGroup.BaseObject.Position, objGroup.BaseObject.Direction, vm.Context, VMPlaceRequestFlags.Default);
                        var canDelete = GameFacade.EnableMod || (objGroup.BaseObject.IsUserMovable(vm.Context, true)) == VMPlacementError.Success;
                        SetSelected(ghostGroup);

                        Holding.RealEnt = objGroup.BaseObject;
                        if (UseNet) RecursiveHide(Holding.RealEnt);
                        Holding.CanDelete = canDelete;
                        Holding.MoveTarget = newHover;
                        var estBase = state.MouseState.Position; //FSOEnvironment.SoftwareKeyboard ? new Point(UIScreen.Current.ScreenWidth / 2, UIScreen.Current.ScreenHeight / 2) : state.MouseState.Position;
                        Holding.TilePosOffset = new Vector2(objBasePos.x / 16f, objBasePos.y / 16f) - World.EstTileAtPosWithScroll(new Vector2(estBase.X, estBase.Y) / FSOEnvironment.DPIScaleFactor);
                        if (OnPickup != null) OnPickup(Holding, state);
                        //ExecuteEntryPoint(12); //User Pickup
                        if (FSOEnvironment.SoftwareKeyboard) MouseIsDown = false;
                    }
                    else
                    {
                        ShowErrorAtMouse(state, success);
                    }
                }
            }

            if (ParentControl.MouseIsOn && !ParentControl.RMBScroll)
            {
                if (EyedropperMode && Holding == null)
                {
                    //Set every frame rather than once on toggle: CursorManager keeps its own idea
                    //of the current cursor and any element that asks for a different one will
                    //overwrite this until it is asked for again.
                    SimitoneCursors.SetEyedropperCursor();
                }
                else
                {
                    GameFacade.Cursor.SetCursor(cur);
                }
            }

            MouseWasDown = MouseIsDown;
        }

        /// <summary>
        /// The floor half of the eyedropper: reports the pattern of the floor tile under the
        /// cursor. Architecture has no catalog GUID to report, and a floor outside the lot (or
        /// outside the current storey) has no pattern worth reporting - both are left alone
        /// rather than clamped, so a click in the void off the edge of the lot does nothing.
        /// </summary>
        private void TryEyedropperFloorPick(UpdateState state)
        {
            var arch = vm.Context.Architecture;
            if (arch == null) return;

            var tilePos = World.EstTileAtPosWithScroll(
                new Vector2(state.MouseState.X, state.MouseState.Y) / FSOEnvironment.DPIScaleFactor);
            var tileX = (int)Math.Floor(tilePos.X);
            var tileY = (int)Math.Floor(tilePos.Y);
            var level = World.State.Level;

            if (tileX < 0 || tileY < 0 || tileX >= arch.Width || tileY >= arch.Height) return;
            if (level < 1 || level > arch.Stories) return;

            var floor = arch.GetFloor((short)tileX, (short)tileY, level);
            if (floor.Pattern > 0 && Content.Get().WorldFloors.Entries.ContainsKey(floor.Pattern))
            {
                OnEyedropperArchitecturePick?.Invoke(floor.Pattern, ArchitectureType.Floor);
            }
        }

        private void RecursiveHide(VMEntity ent)
        {
            ent.MultitileGroup.Objects.ForEach((x) => {
                x.SetValue(VMStackObjectVariable.Hidden, 1);

                var slots = x.TotalSlots();
                for (int i=0; i<slots; i++)
                {
                    var slot = x.GetSlot(i);
                    if (slot != null && slot.GetValue(VMStackObjectVariable.Hidden) == 0) RecursiveHide(slot);
                }
                });
        }

        private void RecursiveUnhide(VMEntity fake, VMEntity real)
        {
            var rgrp = real.MultitileGroup;
            for (int i = 0; i < rgrp.Objects.Count; i++)
            {
                rgrp.Objects[i].SetValue(VMStackObjectVariable.Hidden, fake.MultitileGroup.Objects[i].GetValue(VMStackObjectVariable.Hidden));
                var slots = rgrp.Objects[i].TotalSlots();
                for (int j = 0; j < slots; j++)
                {
                    var slot = rgrp.Objects[i].GetSlot(j);
                    var slot2 = fake.GetSlot(j);
                    if (slot != null && slot2 != null && slot.GetValue(VMStackObjectVariable.Hidden) != slot2.GetValue(VMStackObjectVariable.Hidden)) 
                        RecursiveUnhide(slot2, slot);
                }
            }
        }

        private void ShowErrorAtMouse(UpdateState state, VMPlacementError error)
        {
            if (Holding == null) return;
            state.UIState.TooltipProperties.Show = true;
            state.UIState.TooltipProperties.Color = Color.Black;
            state.UIState.TooltipProperties.Opacity = 1;
            state.UIState.TooltipProperties.Position = new Vector2(MouseDownX,
                MouseDownY);
            state.UIState.Tooltip = GameFacade.Strings.GetString("137", ((int)error).ToString()); //((int)Holding.CanPlace).ToString()); was this the desired effect?
            state.UIState.TooltipProperties.UpdateDead = false;
            ShowTooltip = true;
            HITVM.Get().PlaySoundEvent(UISounds.Error);
        }

        public void RotateObject(int notches)
        {
            Direction newDir;
            if (notches > 0)
            {
                var dir = (int)Holding.Dir << notches;
                if (dir > 255) dir >>= 8;
                newDir = (Direction)dir;
            } else
            {
                var dir = ((int)Holding.Dir << 8) >> (-notches);
                if (dir > 255) dir >>= 8;
                newDir = (Direction)dir;
            }
            HITVM.Get().PlaySoundEvent(UISounds.ObjectRotate);
            Holding.Dir = newDir;
        }

        public delegate void HolderEventHandler(UIObjectSelection holding, UpdateState state);
    }

    /// <summary>
    /// The kind of architecture an eyedropper pick found.
    ///
    /// Only <see cref="Floor"/> exists. Wallpaper and fences are deliberately unsupported: a wall
    /// tile does not record which wallpaper pattern is on it (only the wall style and its
    /// pattern, and a wall and its wallpaper are separate catalogue categories that cannot be
    /// told apart from a click), and there is no per-tile fence record to read either. Adding a
    /// member here means adding the matching read in <c>UIObjectHolder</c> and the matching
    /// subcategory in <c>UIBuyBrowsePanel.SelectArchitectureByPatternID</c>.
    /// </summary>
    public enum ArchitectureType
    {
        Floor
    }

    public class UIObjectSelection
    {
        public short MoveTarget = 0;

        public VMMultitileGroup Group;
        public VMEntity[] CursorTiles;
        public LotTilePos PreviousTile;
        public Direction OriginalDir = Direction.NORTH;
        public Direction Dir = Direction.NORTH;
        public Vector2 TilePos;
        public Vector2 TilePosOffset;
        public bool Clicked;
        public VMPlacementError CanPlace;
        public sbyte Level;
        public int Price;
        public uint InventoryPID = 0;
        public bool CanDelete;
        public VMEntity RealEnt;

        public bool IsBought
        {
            get
            {
                return (MoveTarget != 0);
            }
        }
    }
}
