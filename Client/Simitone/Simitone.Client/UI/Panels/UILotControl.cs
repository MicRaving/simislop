/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Model;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework;
using FSO.HIT;

using FSO.LotView;
using FSO.SimAntics;
using FSO.LotView.Components;
using Microsoft.Xna.Framework.Input;
using FSO.LotView.Model;
using FSO.SimAntics.Primitives;
using FSO.SimAntics.NetPlay.Model.Commands;
using FSO.SimAntics.Utils;
using FSO.Common;
using FSO.Client;
using FSO.Content;
using FSO.Client.Debug;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using FSO.LotView.RC;
using Simitone.Client.UI.Panels.LotControls;
using FSO.Client.UI.Panels.LotControls;
using FSO.UI.Panels.LotControls;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Panels.Desktop;
using Simitone.Client.UI.Panels.EODs;

namespace Simitone.Client.UI.Panels
{
    /// <summary>
    /// Generates pie menus when the player clicks on objects.
    /// </summary>
    public class UILotControl : UIContainer, ILotControl
    {
        private UIMouseEventRef MouseEvt;
        public bool MouseIsOn;
        public I3DRotate Rotate { get { return World.State.Cameras.Camera3D; } }

        private UIPieMenu PieMenu;

        private bool ShowTooltip;
        private bool TipIsError;
        private Texture2D RMBCursor;

        public FSO.SimAntics.VM vm;
        public FSO.LotView.World World { get; set; }
        public VMEntity ActiveEntity { get; set; }
        public int Budget { get
            {
                return BudgetService.GetBudget();
            }
        }
        public uint SelectedSimID
        {
            get
            {
                return (vm == null) ? 0 : vm.MyUID;
            }
        }
        public short ObjectHover;
        public bool InteractionsAvailable;
        public UIInteractionQueue Queue;

        public bool LiveMode = true;
        public bool PanelActive = false;
        public UILotControlTouchHelper Touch;
        public UIArchTouchHelper ArchTouch;

        public int WallsMode = 1;

        /// <summary>
        /// Fired when a custom lot control (the floor, wallpaper, wall, roof and terrain build
        /// tools) is put down by ESC rather than by the player picking something else.
        ///
        /// The catalogue panel listens so it can clear its "an item is selected" bookkeeping -
        /// without it the query panel stays up describing a tool that is no longer active.
        /// </summary>
        public event Action OnCustomControlReleased;

        private int OldMX;
        private int OldMY;
        private bool FoundMe; //if false and avatar changes, center. Should center on join lot.

        public bool RMBScroll;
        private int RMBScrollX;
        private int RMBScrollY;

        //Middle mouse is the camera rotation button. Dragging it is already handled by the
        //camera controller (CameraController2D/3D.Update), so all we track here is where the
        //press started: a press that never moved is a click, and a click snaps the lot round a
        //quarter turn. Only the press position is needed - the drag itself reads the live
        //mouse, the same way RMBScroll does.
        private bool MMBScroll;
        private int MMBScrollX;
        private int MMBScrollY;

        //How far the mouse may travel between press and release and still count as a click
        //rather than a drag. Both RMB and MMB scroll their button the moment they move, so
        //this is what separates "I meant to click" from "I meant to drag the camera".
        private const int MouseClickTolerance = 5;

        //1 = near, 0.5 = med, 0.25 = far
        //"target" because we rescale the game target to fit this zoom level.
        //
        //This is the single authority for lot zoom: the mouse wheel and the pinch gesture
        //(<see cref="UILotControlTouchHelper"/>), the desktop zoom buttons and the desktop
        //zoom-hold (<see cref="Simitone.Client.UI.Panels.Desktop.UIDesktopUCP"/>) and the 3D
        //easing in Update all read and write this one field. <see cref="Update"/> is the only
        //place it becomes camera state (State.Zoom plus the backbuffer scale in 2D, Zoom3D in
        //3D), so a fractional value animates a zoom and a whole value is a resting zoom.
        public float TargetZoom = 1; 

        // NOTE: Blocking dialog system assumes that nothing goes wrong with data transmission (which it shouldn't, because we're using TCP)
        // and that the code actually blocks further dialogs from appearing while waiting for a response.
        // If we are to implement controlling multiple sims, this must be changed.
        private UIMobileDialog BlockingDialog;

        /// <summary>
        /// The lot picker shown when a sim asks to travel somewhere (go downtown, go to the
        /// studio, and so on). It is the <c>UITS1NeighborhoodVisitLotPanel</c> wrapper rather than
        /// a bare <see cref="UINeighborhoodSelectionPanel"/> because this is a <em>visit</em>, not
        /// a move: residential lots are not offered, and the "more" actions (evict, bulldoze) do
        /// not apply. The wrapper is what puts the destination caption on screen.
        /// </summary>
        private UITS1NeighborhoodVisitLotPanel TS1NeighSelector;
        private ulong LastDialogID;

        /// <summary>
        /// The per-sim auto-follow control: the two directions Downtown ships, offered for
        /// whichever sim the player is pointing at in live mode. It is a child of this control
        /// rather than a dialog because it tracks the mouse rather than waiting for a script to
        /// ask for it - unlike <see cref="TS1NeighSelector"/>, which exists to answer a
        /// <c>VMDialogType</c> raised by a running thread.
        /// </summary>
        /// <remarks>
        /// Named for the panel rather than for the feature on purpose: a field called
        /// <c>AutoFollow</c> would shadow the <see cref="Utils.AutoFollow"/> decision class for
        /// every unqualified <c>AutoFollow.</c> in this file.
        /// </remarks>
        private UIAutoFollowPanel AutoFollowPanel;

        private static uint GOTO_GUID = 0x000007C4;
        public VMEntity GotoObject;

        private Rectangle MouseCutRect = new Rectangle(-4, -4, 4, 4);
        private List<uint> CutRooms = new List<uint>();
        private HashSet<uint> LastCutRooms = new HashSet<uint>(); //final rooms, including those outside. used to detect dirty.
        public sbyte LastFloor = -1;
        public WorldRotation LastRotation = WorldRotation.TopLeft;
        private bool[] LastCuts; //cached roomcuts, to apply rect cut to.
        private int LastWallMode = -1; //invalidates last roomcuts
        private bool LastRectCutNotable = false; //set if the last rect cut made a noticable change to the cuts array. If true refresh regardless of new cut effect.

        public UIObjectHolder ObjectHolder;
        public UICustomLotControl CustomControl;
        public UIQueryPanel QueryPanel;
        public UIPickupPanel PickupPanel;

        /// <summary>
        /// The end-of-day minigame host: what puts up a window when the sim uses one of the
        /// retail TS1 EOD objects (a sign, the dance floor, a slot machine). Null when the
        /// framework did not start on this lot, in which case the lot behaves exactly as it did
        /// before EODs were wired in.
        ///
        /// <para>
        /// The host is created here and parented by the game screen, not by this control, because
        /// an EOD is modal over the whole lot: it has to draw above the frontend bars, and the lot
        /// control hides itself entirely while the neighbourhood picker is up. The field's name is
        /// deliberately not <c>EODHost</c> so that the screen's subscription reads the same as
        /// FreeSO's own two screens do.
        /// </para>
        ///
        /// <para>
        /// See <see cref="EODHost"/> for what "did not start" means and why it is safe, and
        /// <see cref="EODPluginPolicy"/> for which minigames are eligible and why the rest are not.
        /// </para>
        /// </summary>
        public EODHost EODs { get; private set; }

        /// <summary>
        /// Creates a new UILotControl instance.
        /// </summary>
        /// <param name="vm">A SimAntics VM instance.</param>
        /// <param name="World">A World instance.</param>
        public UILotControl(FSO.SimAntics.VM vm, FSO.LotView.World World)
        {
            this.vm = vm;
            this.World = World;
            
            MouseEvt = this.ListenForMouse(new Microsoft.Xna.Framework.Rectangle(0, 0,
                GlobalSettings.Default.GraphicsWidth, GlobalSettings.Default.GraphicsHeight), OnMouse);

            Queue = new UIInteractionQueue(ActiveEntity, vm);
            this.Add(Queue);

            ObjectHolder = new UIObjectHolder(vm, World, this);
            Touch = new UILotControlTouchHelper(this);
            Add(Touch);
            ArchTouch = new UIArchTouchHelper(this);
            Add(ArchTouch);
            SetupQuery();

            AutoFollowPanel = new UIAutoFollowPanel();
            AutoFollowPanel.Visible = false;
            AutoFollowPanel.OnAutoFollow += OnAutoFollowRequested;
            this.Add(AutoFollowPanel);


            RMBCursor = GetTexture(0x24B00000001); //exploreanchor.bmp

            //The minigame host is built last and is the one part of this constructor that is
            //allowed to decline: its own constructor cannot throw, and a host that did not start
            //is simply left null, which is how the lot looks today. It is not added to this
            //control's tree - the game screen parents it, so it can sit above the frontend.
            EODs = new EODHost(vm, this);

            vm.OnDialog += vm_OnDialog;
            vm.OnBreakpoint += Vm_OnBreakpoint;
        }

        public void SetupQuery()
        {
            QueryPanel = new UIQueryPanel(World);
            QueryPanel.X = 0;
            QueryPanel.Y = -114;

            PickupPanel = new UIPickupPanel();
            PickupPanel.OnResponse += PickupPanel_OnResponse;
        }

        private void PickupPanel_OnResponse(bool resp)
        {
            if (resp) ObjectHolder.SellBack(null);
            else ObjectHolder.Cancel();
        }

        /// <summary>
        /// Unsubscribes everything this control attached to objects that outlive it. The
        /// constructor subscribes to VM events and registers a mouse listener; without this the
        /// VM would keep the old lot control alive (and keep calling it) after the screen has
        /// moved on to another lot.
        ///
        /// Safe to call more than once, and safe when the constructor never completed: every
        /// step is null-guarded, and each unhook (delegate removal, List.Remove) is itself
        /// idempotent, so a second call changes nothing.
        /// </summary>
        public override void Removed()
        {
            base.Removed();

            if (vm != null)
            {
                vm.OnDialog -= vm_OnDialog;
                vm.OnBreakpoint -= Vm_OnBreakpoint;
            }

            if (MouseEvt != null)
            {
                // RemoveMouseListener is a plain List.Remove, so repeating this is a no-op and
                // MouseEvt is deliberately left in place: GameResized() still reads its Region.
                RemoveMouseListener(MouseEvt);
            }

            if (PickupPanel != null)
            {
                PickupPanel.OnResponse -= PickupPanel_OnResponse;
            }

            //Catalogue panels listen for a build tool being stood down. The panels are torn down
            //by the screen at the same time and unhook themselves, but clearing the field here
            //means this control cannot be the reason a removed panel stays alive either way.
            OnCustomControlReleased = null;

            //The minigame host is the game screen's child and is removed there, but dropping the
            //reference here means a removed lot control cannot keep a host - and through it the
            //panel tree it loaded - alive. Idempotent, like everything else in here.
            EODs = null;
        }

        public override void GameResized()
        {
            base.GameResized();
            MouseEvt.Region.Width = GlobalSettings.Default.GraphicsWidth;
            MouseEvt.Region.Height = GlobalSettings.Default.GraphicsHeight;

            //SetupQuery();
        }


        private void Vm_OnBreakpoint(VMEntity entity)
        {
            if (IDEHook.IDE != null) IDEHook.IDE.IDEBreakpointHit(vm, entity);
        }

        public static bool ShowSimanticsExceptions = true;
        void vm_OnDialog(FSO.SimAntics.Model.VMDialogInfo info)
        {
            if (info != null && ((info.DialogID == LastDialogID && info.DialogID != 0 && info.Block))) return;
            //return if same dialog as before, or not ours
            if ((info == null || info.Block) && BlockingDialog != null)
            {
                //cancel current dialog because it's no longer valid
                UIScreen.RemoveDialog(BlockingDialog);
                LastDialogID = 0;
                BlockingDialog = null;
            }
            if (info == null) return; //return if we're just clearing a dialog.

            if (!ShowSimanticsExceptions && info.Title == "SimAntics Exception!")
                return;

            var options = new UIAlertOptions
            {
                Title = info.Title,
                Message = info.Message,
                Width = 325 + (int)(info.Message.Length / 3.5f),
                Alignment = TextAlignment.Left,
                TextSize = 12
            };

            var b0Event = (info.Block) ? new ButtonClickDelegate(DialogButton0) : null;
            var b1Event = (info.Block) ? new ButtonClickDelegate(DialogButton1) : null;
            var b2Event = (info.Block) ? new ButtonClickDelegate(DialogButton2) : null;

            VMDialogType type = (info.Operand == null) ? VMDialogType.Message : info.Operand.Type;

            switch (type)
            {
                default:
                case VMDialogType.Message:
                    options.Buttons = new UIAlertButton[] { new UIAlertButton(UIAlertButtonType.OK, b0Event, info.Yes) };
                    break;
                case VMDialogType.YesNo:
                    options.Buttons = new UIAlertButton[]
                    {
                        new UIAlertButton(UIAlertButtonType.Yes, b0Event, info.Yes),
                        new UIAlertButton(UIAlertButtonType.No, b1Event, info.No),
                    };
                    break;
                case VMDialogType.YesNoCancel:
                    options.Buttons = new UIAlertButton[]
                    {
                        new UIAlertButton(UIAlertButtonType.Yes, b0Event, info.Yes),
                        new UIAlertButton(UIAlertButtonType.No, b1Event, info.No),
                        new UIAlertButton(UIAlertButtonType.Cancel, b2Event, info.Cancel),
                    };
                    break;
                case VMDialogType.TextEntry:
                    options.Buttons = new UIAlertButton[] { new UIAlertButton(UIAlertButtonType.OK, b0Event, info.Yes) };
                    options.TextEntry = true;
                    break;
                case VMDialogType.NumericEntry:
                    if (!vm.TS1) goto case VMDialogType.TextEntry;
                    else goto case VMDialogType.TS1Neighborhood;
                case VMDialogType.TS1Vacation:
                case VMDialogType.TS1Neighborhood:
                case VMDialogType.TS1StudioTown:
                case VMDialogType.TS1Magictown:
                    var gameScr = (TS1GameScreen)Parent;
                    TS1NeighSelector = new UITS1NeighborhoodVisitLotPanel((ushort)VMDialogPrivateStrings.TypeToNeighID[type]);
                    Parent.Add(TS1NeighSelector);
                    //the picker is a full screen overlay, so everything that would draw over it -
                    //the lot itself and the whole live-mode frontend - is put away until it closes.
                    //without hiding the frontend the player can still click the subpanel and the
                    //mode buttons through the map.
                    gameScr.Bg.Visible = true;
                    gameScr.LotControl.Visible = false;
                    gameScr.Frontend.Visible = false;
                    //placement is deferred to Awake() because the panel is not in the tree yet, and
                    //its own GameResized() needs the current screen width to lay the caption out.
                    TS1NeighSelector.Awake();
                    TS1NeighSelector.OnHouseSelect += HouseSelected;
                    return;
                case VMDialogType.TS1PhoneBook:
                    var phone = new UICallNeighborAlert(((VMAvatar)info.Caller).GetPersonData(FSO.SimAntics.Model.VMPersonDataVariable.NeighborId), vm);
                    BlockingDialog = phone;
                    UIScreen.GlobalShowDialog(phone, true);
                    phone.OnResult += (result) =>
                    {
                        vm.SendCommand(new VMNetDialogResponseCmd
                        {
                            ActorUID = info.Caller.PersistID,
                            ResponseCode = (byte)((result > 0) ? 1 : 0),
                            ResponseText = result.ToString()
                        });
                        BlockingDialog = null;
                    };
                    return;

                case VMDialogType.TS1PetChoice:
                case VMDialogType.TS1Clothes:
                    var ts1categories = new string[] { "b", "f", "s", "l", "w", "h" };
                    var pet = type == VMDialogType.TS1PetChoice;
                    var stackObj = info.Caller.Thread.Stack.Last().StackObject;
                    
                    var skin = new UISelectSkinAlert(pet?null:(info.Caller as VMAvatar), pet?((stackObj as VMAvatar).IsCat?"cat":"dog"):ts1categories[info.Caller.Thread.TempRegisters[0]], vm);
                    BlockingDialog = skin;
                    UIScreen.GlobalShowDialog(skin, true);
                    skin.OnResult += (result) =>
                    {
                        vm.SendCommand(new VMNetDialogResponseCmd
                        {
                            ActorUID = info.Caller.PersistID,
                            ResponseCode = (byte)((result > -1)?1:0),
                            ResponseText = result.ToString()
                        });
                        BlockingDialog = null;
                    };
                    return;

                // Makin' Magic's two book dialogs. The engine raises them like any other blocking
                // dialog and hands over nothing but the caller's title and message, so the panel
                // builds its own list out of GameData/magic.iff and the caller's inventory - see
                // UIMakingMagicAlert for the whole of that, and for what the answer is worth.
                case VMDialogType.TS1Spellbook:
                case VMDialogType.TS1Cookbook:
                    var book = MakingMagic.BookFor(type);
                    if (book == MakingMagicBook.None || info.Caller == null)
                    {
                        // nothing to build a book out of, so answer it straight away rather than
                        // stopping the lot on a dialog that cannot be shown
                        SimitoneLog.Warn($"magic book: {type} raised with no caller, answered as a plain message");
                        vm.SendCommand(new VMNetDialogResponseCmd
                        {
                            ActorUID = info.Caller?.PersistID ?? 0,
                            ResponseCode = MakingMagic.ResponseCancelled,
                            ResponseText = MakingMagic.ResponseTextFor(-1)
                        });
                        return;
                    }

                    var magic = new UIMakingMagicAlert(info, book);
                    BlockingDialog = magic;
                    LastDialogID = info.DialogID;
                    UIScreen.GlobalShowDialog(magic, true);
                    magic.OnResult += (result) =>
                    {
                        vm.SendCommand(new VMNetDialogResponseCmd
                        {
                            ActorUID = info.Caller.PersistID,
                            ResponseCode = MakingMagic.ResponseCodeFor(result),
                            ResponseText = MakingMagic.ResponseTextFor(result)
                        });
                        if (BlockingDialog == magic)
                        {
                            // only our own bookkeeping is cleared: a newer dialog that arrived
                            // while this one was up owns both of these by then, and the old
                            // thread still gets its answer either way
                            BlockingDialog = null;
                            LastDialogID = 0;
                        }
                    };
                    SimitoneLog.Info($"magic book: {book} opened for {info.Caller}, {magic.BookPanel.EntryCount} entries");
                    return;

                // The third Makin' Magic dialog, "transform me" (SpellTransform.iff), is left on
                // the plain-alert path deliberately: there is no table for it in magic.iff and no
                // client-side data behind it, so a book panel would be an empty list with extra
                // steps in it. The alert's OK answers it exactly as it always did.
                case VMDialogType.TS1TransformMe:
                    SimitoneLog.Info("magic book: transform-me dialog answered as a plain message - nothing exists client-side to list");
                    goto case VMDialogType.Message;
            }

            var alert = new UIMobileAlert(options);

            UIScreen.GlobalShowDialog(alert, true);

            if (info.Block)
            {
                BlockingDialog = alert;
                LastDialogID = info.DialogID;
            }

            var entity = info.Icon;
            if (entity is VMGameObject)
            {
                var objects = entity.MultitileGroup.Objects;
                ObjectComponent[] objComps = new ObjectComponent[objects.Count];
                for (int i = 0; i < objects.Count; i++)
                {
                    objComps[i] = (ObjectComponent)objects[i].WorldUI;
                }
                var thumb = World.GetObjectThumb(objComps, entity.MultitileGroup.GetBasePositions(), GameFacade.GraphicsDevice);
                alert.SetIcon(thumb, 256, 256);
            }
        }

        private void HouseSelected(int house)
        {
            if (ActiveEntity == null || TS1NeighSelector == null) return;
            vm.SendCommand(new VMNetDialogResponseCmd
            {
                ActorUID = ActiveEntity.PersistID,
                ResponseCode = (byte)((house > 0) ? 1 : 0),
                ResponseText = house.ToString()
            });
            //the picker's own parent, not ours: this control may have been removed from the screen
            //while the picker was still up, and the picker would then never be taken down
            TS1NeighSelector.Parent?.Remove(TS1NeighSelector);
            TS1NeighSelector = null;

            //A declined trip is the only outcome that leaves the current lot standing, so it is
            //the only one that has to put the UI back: the picker took the lot control and the
            //live-mode frontend away and showed the title background in their place. On an
            //accepted pick the lot is about to be torn down and rebuilt, and the rebuilt frontend
            //is visible of its own accord.
            if (house <= 0) RestoreLotUi();
        }

        /// <summary>
        /// Undoes the full-screen takeover a travel picker does, returning to the live lot.
        /// </summary>
        private void RestoreLotUi()
        {
            var gameScr = Parent as TS1GameScreen;
            if (gameScr == null) return;
            gameScr.Bg.Visible = false;
            gameScr.LotControl.Visible = true;
            if (gameScr.Frontend != null) gameScr.Frontend.Visible = true;
        }

        private void DialogButton0(UIElement button) { DialogResponse(0); }
        private void DialogButton1(UIElement button) { DialogResponse(1); }
        private void DialogButton2(UIElement button) { DialogResponse(2); }

        private void DialogResponse(byte code)
        {
            if (BlockingDialog == null || !(BlockingDialog is UIMobileAlert)) return;
            BlockingDialog.Close();
            var ma = (UIMobileAlert)BlockingDialog;
            LastDialogID = 0;
            vm.SendCommand(new VMNetDialogResponseCmd
            {
                ResponseCode = code,
                ResponseText = (ma.ResponseText == null) ? "" : ma.ResponseText
            });
            BlockingDialog = null;
        }

        private void OnMouse(UIMouseEventType type, UpdateState state)
        {
            if (!vm.Ready) return;

            if (type == UIMouseEventType.MouseOver)
            {
                if (QueryPanel.Mode == 1) QueryPanel.SetShown(false);
                MouseIsOn = true;
            }
            else if (type == UIMouseEventType.MouseOut)
            {
                MouseIsOn = false;
                GameFacade.Cursor.SetCursor(CursorType.Normal);
                Tooltip = null;
            }
            else if (type == UIMouseEventType.MouseDown)
            {
                if (!FSOEnvironment.SoftwareKeyboard)
                {
                    if (!LiveMode)
                    {
                        if (CustomControl != null) CustomControl.MouseDown(state);
                        else ObjectHolder.MouseDown(state);
                        return;
                    }
                }
                Touch.MiceDown.Add(state.CurrentMouseID);
            }
            else if (type == UIMouseEventType.MouseUp)
            {
                Touch.MiceDown.Remove(state.CurrentMouseID);
                if (!FSOEnvironment.SoftwareKeyboard)
                {
                    if (!LiveMode)
                    {
                        if (CustomControl != null) CustomControl.MouseUp(state);
                        else ObjectHolder.MouseUp(state);
                        return;
                    }
                }
                state.UIState.TooltipProperties.Show = false;
                state.UIState.TooltipProperties.Opacity = 0;
                ShowTooltip = false;
                TipIsError = false;
            }
        }

        public void SimulateMD(UpdateState state)
        {
            if (CustomControl != null) CustomControl.MouseDown(state);
            else ObjectHolder.MouseDown(state);
        }

        public void SimulateMU(UpdateState state)
        {
            if (CustomControl != null) CustomControl.MouseUp(state);
            else ObjectHolder.MouseUp(state);
        }

        public void AddModifier(UILotControlModifiers mod)
        {
            if (CustomControl != null)
                CustomControl.Modifiers |= mod;
        }

        public void RemoveModifier(UILotControlModifiers mod)
        {
            if (CustomControl != null)
                CustomControl.Modifiers &= ~mod;
        }

        /// <summary>
        /// Puts the active build tool (floor, wallpaper, wall, roof, terrain) down, exactly as ESC
        /// does, and tells listeners so the catalogue can clear its "something is selected"
        /// bookkeeping. Does nothing if no tool is active.
        ///
        /// Public because switching tools is not only the player's business: arming the eyedropper
        /// has to stand the current tool down, otherwise the custom control keeps taking the click
        /// and the eyedropper never sees it.
        /// </summary>
        public void ReleaseCustomControl()
        {
            if (CustomControl == null) return;

            CustomControl.Release();
            CustomControl = null;
            OnCustomControlReleased?.Invoke();
        }

        public void ShowPieMenu(Point pt, UpdateState state)
        {
            if (!LiveMode)
            {
                /*
                if (CustomControl != null) CustomControl.MouseDown(state);
                else ObjectHolder.MouseDown(state);
                */
                if (FSOEnvironment.SoftwareKeyboard && ObjectHolder.Holding == null)
                {
                    ObjectHolder.MouseDown(state);
                }
                return;
            }
            if (PieMenu == null && ActiveEntity != null)
            {
                VMEntity obj;
                //get new pie menu, make new pie menu panel for it
                var tilePos = World.EstTileAtPosWithScroll(new Vector2(pt.X, pt.Y) / FSOEnvironment.DPIScaleFactor);

                LotTilePos targetPos = LotTilePos.FromBigTile((short)tilePos.X, (short)tilePos.Y, World.State.Level);
                if (vm.Context.SolidToAvatars(targetPos).Solid) targetPos = LotTilePos.OUT_OF_WORLD;

                GotoObject.SetPosition(targetPos, Direction.NORTH, vm.Context);

                var newHover = World.GetObjectIDAtScreenPos(pt.X,
                    pt.Y,
                    GameFacade.GraphicsDevice);

                ObjectHover = newHover;

                bool objSelected = ObjectHover > 0;
                if (objSelected || (GotoObject.Position != LotTilePos.OUT_OF_WORLD && ObjectHover <= 0))
                {
                    if (objSelected)
                    {
                        obj = vm.GetObjectById(ObjectHover);
                    }
                    else
                    {
                        obj = GotoObject;
                    }
                    if (obj is VMAvatar && state.CtrlDown)
                    {
                        //debug switch to avatar
                        vm.SendCommand(new VMNetChangeControlCmd()
                        {
                            TargetID = obj.ObjectID
                        });
                    }
                    else if (obj != null)
                    {
                        obj = obj.MultitileGroup.GetInteractionGroupLeader(obj);
                        if (obj is VMGameObject && ((VMGameObject)obj).Disabled > 0)
                        {
                            var flags = ((VMGameObject)obj).Disabled;

                            if ((flags & VMGameObjectDisableFlags.ForSale) > 0)
                            {
                                //for sale
                                var retailPrice = obj.MultitileGroup.Price; //wrong... should get this from catalog
                                var salePrice = obj.MultitileGroup.SalePrice;
                                ShowErrorTooltip(state, 22, true, "$" + retailPrice.ToString("##,#0"), "$" + salePrice.ToString("##,#0"));
                            }
                            else if ((flags & VMGameObjectDisableFlags.LotCategoryWrong) > 0)
                                ShowErrorTooltip(state, 21, true); //category wrong
                            else if ((flags & VMGameObjectDisableFlags.TransactionIncomplete) > 0)
                                ShowErrorTooltip(state, 27, true); //transaction not yet complete
                            else if ((flags & VMGameObjectDisableFlags.ObjectLimitExceeded) > 0)
                                ShowErrorTooltip(state, 24, true); //object is temporarily disabled... todo: something more helpful
                            else if ((flags & VMGameObjectDisableFlags.PendingRoommateDeletion) > 0)
                                ShowErrorTooltip(state, 16, true); //pending roommate deletion
                        }
                        else
                        {
                            var menu = obj.GetPieMenu(vm, ActiveEntity, false, true);
                            if (menu.Count != 0)
                            {
                                HITVM.Get().PlaySoundEvent(UISounds.PieMenuAppear);
                                PieMenu = new UIPieMenu(menu, obj, ActiveEntity, this);
                                this.Add(PieMenu);
                                PieMenu.X = state.MouseState.X / FSOEnvironment.DPIScaleFactor;
                                PieMenu.Y = state.MouseState.Y / FSOEnvironment.DPIScaleFactor;
                                PieMenu.UpdateHeadPosition(state.MouseState.X, state.MouseState.Y);
                            }
                            else if (obj.TreeTable?.Interactions?.Length > 0)
                            {
                                // The object HAS interactions and every one of them was refused.
                                // That is a failure, not an empty table, and it used to be
                                // indistinguishable from clicking bare floor: no menu, no sound,
                                // no message. Say which object and how many entries it turned
                                // down - and point at the per-interaction reasons, which the
                                // engine now writes itself (VMPieRefusal), because the decision
                                // is made three frames below this and is gone by here.
                                SimitoneLog.Warn($"pie menu: object {obj.ObjectID} (id 0x{obj.Object?.OBJ?.GUID:X8}) "
                                    + $"has {obj.TreeTable.Interactions.Length} interactions but offered none to "
                                    + $"{ActiveEntity?.ObjectID}; the per-interaction reasons are on the "
                                    + $"\"pie menu refuse:\" lines just above this one.");
                                ShowErrorTooltip(state, 0, true);
                            }
                        }
                    }

                }
                else
                {
                    ShowErrorTooltip(state, 0, true);
                }
            }
            else
            {
                if (PieMenu != null) PieMenu.RemoveSimScene();
                this.Remove(PieMenu);
                PieMenu = null;
            }
        }

        private void ShowErrorTooltip(UpdateState state, uint id, bool playSound, params string[] args)
        {
            if (playSound) HITVM.Get().PlaySoundEvent(UISounds.Error);
            state.UIState.TooltipProperties.Show = true;
            state.UIState.TooltipProperties.Color = Color.Black;
            state.UIState.TooltipProperties.Opacity = 1;
            state.UIState.TooltipProperties.Position = new Vector2(state.MouseState.X,
                state.MouseState.Y);
            state.UIState.Tooltip = GameFacade.Strings.GetString("159", id.ToString(), args);
            state.UIState.TooltipProperties.UpdateDead = false;
            ShowTooltip = true;
            TipIsError = true;
        }

        public void ClosePie()
        {
            if (PieMenu != null)
            {
                PieMenu.RemoveSimScene();
                Queue.PieMenuClickPos = PieMenu.Position;
                this.Remove(PieMenu);
                PieMenu = null;
            }
        }

        public override Rectangle GetBounds()
        {
            return new Rectangle(0, 0, GlobalSettings.Default.GraphicsWidth, GlobalSettings.Default.GraphicsHeight);
        }

        public void LiveModeUpdate(UpdateState state, bool scrolled)
        {
            if (MouseIsOn && !RMBScroll && ActiveEntity != null && !FSOEnvironment.SoftwareKeyboard)
            {

                if (state.MouseState.X != OldMX || state.MouseState.Y != OldMY)
                {
                    OldMX = state.MouseState.X;
                    OldMY = state.MouseState.Y;
                    var newHover = World.GetObjectIDAtScreenPos(state.MouseState.X,
                        state.MouseState.Y,
                        GameFacade.GraphicsDevice);

                    if (ObjectHover != newHover)
                    {
                        ObjectHover = newHover;
                        if (ObjectHover > 0)
                        {
                            var obj = vm.GetObjectById(ObjectHover);
                            if (obj != null)
                            {
                                var menu = obj.GetPieMenu(vm, ActiveEntity, false, true);
                                InteractionsAvailable = (menu.Count > 0);
                            }
                        }
                    }

                    if (!TipIsError) ShowTooltip = false;
                    if (ObjectHover > 0)
                    {
                        var obj = vm.GetObjectById(ObjectHover);
                        if (!TipIsError && obj != null)
                        {
                            if (obj is VMAvatar)
                            {
                                state.UIState.TooltipProperties.Show = true;
                                state.UIState.TooltipProperties.Color = Color.Black;
                                state.UIState.TooltipProperties.Opacity = 1;
                                state.UIState.TooltipProperties.Position = new Vector2(state.MouseState.X,
                                    state.MouseState.Y);
                                state.UIState.Tooltip = GetAvatarString(obj as VMAvatar);
                                state.UIState.TooltipProperties.UpdateDead = false;
                                ShowTooltip = true;
                            }
                            else if (((VMGameObject)obj).Disabled > 0)
                            {
                                var flags = ((VMGameObject)obj).Disabled;
                                if ((flags & VMGameObjectDisableFlags.ForSale) > 0)
                                {
                                    //for sale
                                    //try to get catalog price
                                    var guid = obj.MasterDefinition?.GUID ?? obj.Object.OBJ.GUID;
                                    var item = Content.Get().WorldCatalog.GetItemByGUID(guid);

                                    var retailPrice = (int?)(item?.Price) ?? obj.MultitileGroup.Price;
                                    var salePrice = obj.MultitileGroup.SalePrice;
                                    ShowErrorTooltip(state, 22, false, "$" + retailPrice.ToString("##,#0"), "$" + salePrice.ToString("##,#0"));
                                    TipIsError = false;
                                }
                            }

                        }
                    }
                    if (!ShowTooltip)
                    {
                        state.UIState.TooltipProperties.Show = false;
                        state.UIState.TooltipProperties.Opacity = 0;
                    }
                }
            }
            else
            {
                ObjectHover = 0;
            }

            //The auto-follow control follows the same hover the tooltip and the cursor do, so
            //it is refreshed every frame rather than only on mouse movement: what it offers
            //must always describe the sim the rest of the live-mode UI is describing.
            UpdateAutoFollowTarget();

            if (!scrolled)
            { //set cursor depending on interaction availability
                CursorType cursor;

                if (PieMenu == null && MouseIsOn)
                {
                    if (ObjectHover == 0)
                    {
                        cursor = CursorType.LiveNothing;
                    }
                    else
                    {
                        if (InteractionsAvailable)
                        {
                            if (vm.GetObjectById(ObjectHover) is VMAvatar) cursor = CursorType.LivePerson;
                            else cursor = CursorType.LiveObjectAvail;
                        }
                        else
                        {
                            cursor = CursorType.LiveObjectUnavail;
                        }
                    }
                }
                else
                {

                    cursor = CursorType.Normal;
                }

                CursorManager.INSTANCE.SetCursor(cursor);
            }

        }

        private string GetAvatarString(VMAvatar ava)
        {
            return ava.ToString();
        }

        /// <summary>
        /// Points the auto-follow control at the sim under the cursor, or puts it away.
        /// </summary>
        /// <remarks>
        /// The eligibility rule itself is <see cref="AutoFollow.Available"/>, and whether the
        /// control should be up at all is <see cref="AutoFollowOffer.IsShown"/>; this is only the
        /// adapter that reads a live <c>VMAvatar</c> into the plain data they decide on, plus the
        /// handful of conditions that are about the screen rather than about the sim - a pie menu
        /// or a modal dialog owns the mouse, so the control stands down.
        /// <para>
        /// <b>Why a sim with nothing to offer shows nothing.</b> This used to raise the control
        /// for every avatar the cursor passed over. Hovering yourself, a pet, a ghost, the
        /// postman or a skeleton put a heading, a line of prose and two dead buttons in the
        /// middle of the lot, and none of the seven status strings those states can produce is
        /// something the player can act on. The control is a way to <em>do</em> something to a
        /// sim, so it is shown exactly when there is something to do, and the reason a sim is
        /// not offered is left to the cursor it already has - a sim the game will not let you
        /// auto-follow is not one it is inviting you to interact with either.
        /// </para>
        /// </remarks>
        private void UpdateAutoFollowTarget()
        {
            if (AutoFollowPanel == null) return;

            if (!LiveMode || PieMenu != null || BlockingDialog != null || TS1NeighSelector != null)
            {
                AutoFollowPanel.Hide();
                return;
            }

            var hovered = ObjectHover > 0 ? vm.GetObjectById(ObjectHover) as VMAvatar : null;
            if (hovered == null)
            {
                AutoFollowPanel.Hide();
                return;
            }

            var subject = DescribeAutoFollowSubject(hovered);
            var offer = AutoFollow.Offer(subject);

            if (!offer.IsShown)
            {
                AutoFollowPanel.Hide();
                return;
            }

            var gameScr = Parent as TS1GameScreen;
            AutoFollowPanel.ShowFor(subject, gameScr != null && gameScr.Downtown, offer);
        }

        /// <summary>
        /// Reads a sim off the lot into the plain description
        /// <see cref="AutoFollow.Available"/> decides on.
        /// </summary>
        /// <param name="target">The sim under the cursor.</param>
        /// <returns>
        /// A subject with <see cref="AutoFollowSubject.IsOnLot"/> left false when the sim has no
        /// neighbour record to hang a request on - which is every object a lot can spawn that is
        /// not a real neighbour, and a real neighbour standing in a lot with no neighbourhood
        /// loaded. The remaining flags are still filled in, because they are read from the sim
        /// itself and give the player a more useful reason than "not on the lot" would.
        /// </returns>
        private AutoFollowSubject DescribeAutoFollowSubject(VMAvatar target)
        {
            var neighbourhood = Content.Get().Neighborhood;
            var nid = target.GetPersonData(FSO.SimAntics.Model.VMPersonDataVariable.NeighborId);
            var neighbour = neighbourhood?.GetNeighborByID(nid);
            var inventory = neighbour != null ? neighbourhood.GetInventoryByNID(nid) : null;
            var currentFamily = vm.TS1State.CurrentFamily;

            //the engine's calls look the request up by neighbour ID, so without a record (or
            //without an inventory to park it in) there is nothing to act on however eligible
            //the sim otherwise looks
            var hasRecord = (neighbour != null && inventory != null);

            return new AutoFollowSubject()
            {
                IsSelf = (target == ActiveEntity || target.PersistID == SelectedSimID),
                //gender is bit-flagged in TS1: 0/1 are people, 8/9 dogs and 16/17 cats, which is
                //the same "gender < 2" cut the phone book uses to keep pets off the list
                IsPet = target.IsCat || target.GetPersonData(FSO.SimAntics.Model.VMPersonDataVariable.Gender) >= 2,
                IsGhost = target.GetPersonData(FSO.SimAntics.Model.VMPersonDataVariable.IsGhost) > 0,
                IsOnLot = hasRecord,
                InTravellingFamily = currentFamily != null
                    && target.GetPersonData(FSO.SimAntics.Model.VMPersonDataVariable.TS1FamilyNumber) == currentFamily.ChunkID,
                //which slot is occupied, not merely whether one is: the panel has to name the
                //direction that is queued and offer the one that is not, and a single-direction
                //lookup would report downtown for a sim whose only request is a take-home one
                Pending = hasRecord ? AutoFollow.FindPendingActions(inventory) : AutoFollowAction.None,
            };
        }

        /// <summary>
        /// Resolves the sim the auto-follow control was pointing at when the player pressed one
        /// of its buttons, and runs the request.
        /// </summary>
        /// <remarks>
        /// The sim is resolved from <see cref="ObjectHover"/> at press time rather than being
        /// carried on the panel, so a request can never be aimed at a sim the panel has since
        /// stopped describing - the lot control recomputes the target every frame, and a press
        /// lands on whichever sim the tooltip is currently naming.
        /// </remarks>
        private void OnAutoFollowRequested(AutoFollowAction action)
        {
            var target = ObjectHover > 0 ? vm.GetObjectById(ObjectHover) as VMAvatar : null;
            if (target == null)
            {
                SimitoneLog.Warn("auto-follow: no sim under the cursor, request refused");
                return;
            }

            RunAutoFollow(action, target);
        }

        /// <summary>
        /// Runs an auto-follow request for a sim: stands the request in the engine's slot, marks
        /// the sim as being followed, and then tries to hand the answer to a thread that is
        /// waiting for one.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the whole of what a client can do, and the split matters.
        /// <c>VMGenericTS1Call</c> modes 19 and 20 are <em>primitives</em>: they take their
        /// target from the calling thread's <c>TempRegisters[0]</c> as an in-world object ID and
        /// their direction from the slot in the target's neighbourhood inventory. Standing the
        /// request in that slot is the half that is data, and it is done here. Running the
        /// primitive is the half that needs a TS1 lot script thread, and there is no client-side
        /// entry point for a primitive at all - so the call itself is reachable only from the
        /// lot's own <c>.iff</c>, never from a click.
        /// </para>
        /// <para>
        /// The command path below is the one the travel picker and the phone book use, and the
        /// only dialog the engine routes into these two calls is
        /// <c>VMDialogType.TS1PhoneBook</c> - the phone book is what a sim uses to summon a
        /// neighbour, and the response is delivered exactly like this. So a response is sent
        /// only when a phone-book thread is genuinely live to answer, carrying the same neighbour
        /// ID the engine then turns into the object ID the primitive consumes. When no such
        /// thread exists there is nothing to answer, and the request stays in the slot waiting
        /// for a script to act on it.
        /// </para>
        /// <para>
        /// Queueing a direction withdraws the other one, so a sim never ends up carrying both a
        /// downtown request and a take-home one, and pressing the direction that is not queued
        /// is how a player takes a queued request back. Nothing has to undo the old request by
        /// hand: <see cref="AutoFollow.TryQueueRequest"/> drops it on the way in.
        /// </para>
        /// </remarks>
        /// <param name="action">Which direction the player asked for.</param>
        /// <param name="target">The sim the request is for.</param>
        private void RunAutoFollow(AutoFollowAction action, VMAvatar target)
        {
            var neighbourhood = Content.Get().Neighborhood;
            if (neighbourhood == null || target == null)
            {
                SimitoneLog.Warn("auto-follow: no neighbourhood loaded, request refused");
                return;
            }

            var nid = target.GetPersonData(FSO.SimAntics.Model.VMPersonDataVariable.NeighborId);
            var neighbour = neighbourhood.GetNeighborByID(nid);
            var inventory = neighbour != null ? neighbourhood.GetInventoryByNID(nid) : null;

            if (inventory == null)
            {
                SimitoneLog.Warn($"auto-follow: neighbour {nid} has no inventory to queue a request in");
                return;
            }

            uint slot;
            if (!AutoFollow.TrySlotFor(action, out slot))
            {
                SimitoneLog.Warn($"auto-follow: '{action}' is not a single direction, no request queued");
                return;
            }

            // re-check against the live inventory rather than trusting the frame the buttons were
            // enabled on. A second press of the direction already standing is a no-op: there is
            // nothing to re-write, and re-answering the phone book would be a second response to
            // the same thread. The whole set is compared rather than just this direction, so a
            // sim still carrying a request both ways - which only an older build's writes could
            // leave - is normalised to the one that was asked for instead of being skipped
            if (AutoFollow.FindPendingActions(inventory) == action)
            {
                SimitoneLog.Info($"auto-follow: {target} already has a slot {slot} request, not re-queued");
                return;
            }

            // queueing withdraws the other direction's request, which is how a queued request is
            // re-directed: this never leaves the sim booked both ways
            if (!AutoFollow.TryQueueRequest(inventory, action, nid))
            {
                SimitoneLog.Warn($"auto-follow: could not queue a slot {slot} request for {target} (nid {nid})");
                return;
            }

            // person data 77 is the field TS1 kept "who is following me" in; the engine
            // declares it and nothing reads it, so this is its only writer
            target.SetPersonData(AutoFollow.FollowPersonData, nid);

            var call = (action == AutoFollowAction.TakeHome) ? AutoFollow.TakeHomeCall : AutoFollow.DowntownCall;
            SimitoneLog.Info($"auto-follow: queued slot {slot} for {target} (nid {nid}) for {call}"
                + (AnswerAutoFollowThread(target, nid) ? " - phone book thread answered" : " - no thread to answer"));

            if (AutoFollowPanel != null) AutoFollowPanel.Refresh();
        }

        /// <summary>
        /// Hands a sim's neighbour ID back to a thread that is blocked on the phone book, which
        /// is the only path the engine has from a click to
        /// <c>SpawnDowntownDateOfPersonInTemp0</c> / <c>SpawnTakeBackHomeDataOfPersonInTemp0</c>.
        /// </summary>
        /// <param name="target">The sim whose thread would carry the call.</param>
        /// <param name="nid">The neighbour ID to answer with.</param>
        /// <returns><c>true</c> when a response was sent; <c>false</c> when there was no live
        /// phone-book thread to answer.</returns>
        private bool AnswerAutoFollowThread(VMAvatar target, short nid)
        {
            var thread = target.Thread;
            if (thread == null) return false;

            var dialog = thread.BlockingState as VMDialogResult;
            if (dialog == null || dialog.Responded || dialog.Type != VMDialogType.TS1PhoneBook) return false;

            vm.SendCommand(new VMNetDialogResponseCmd()
            {
                ActorUID = target.PersistID,
                ResponseCode = 1,
                ResponseText = nid.ToString()
            });
            return true;
        }

        public void RefreshCut()
        {
            LastFloor = -1;
            LastWallMode = -1;

            if (vm.Context.Blueprint != null && LastCuts != null)
            {
                vm.Context.Blueprint.Cutaway = LastCuts;
                vm.Context.Blueprint.Changes.SetFlag(BlueprintGlobalChanges.WALL_CUT_CHANGED);
            }

            //MouseCutRect = new Rectangle(0,0,0,0);
        }

        public void SetTargetZoom(WorldZoom zoom)
        {
            TargetZoom = LotZoomMapping.TargetZoomFor(zoom);
            LastZoom = World.State.Zoom;
        }

        public override void Draw(UISpriteBatch batch)
        {
            //DrawLocalTexture(batch, World.State.Light.LightMap, new Rectangle(0,0, World.State.Light.LightMap.Width/3, World.State.Light.LightMap.Height/2), new Vector2());
            if (RMBScroll)
            {
                DrawLocalTexture(batch, RMBCursor, new Vector2(RMBScrollX - RMBCursor.Width / 2, RMBScrollY - RMBCursor.Height / 2));
            }
            base.Draw(batch);
        }

        private WorldZoom LastZoom;

        /// <summary>
        /// Points the camera at something recognisable on a lot that has no sims on it at all,
        /// which is the state a lot is briefly in between arriving and the player's sim being
        /// created. Without this the camera is left wherever the world starts up, which is tile
        /// (0,0) - the top left of the lot - and the player has to scroll to find anything.
        /// <para>
        /// Tries the mailbox, which is on every residential lot and is the one object a player
        /// can always recognise, then any object standing on the ground floor (community lots
        /// have no mailbox but are rarely bare), and finally the middle of the lot itself so
        /// that an entirely empty lot still opens on something.
        /// </para>
        /// </summary>
        private void CenterOnLotLandmark()
        {
            //the mailbox, as it appears in the two flavours the game stores it in
            const uint MAILBOX_GUID = 0xEF121974;
            const uint MAILBOX_ALT_GUID = 0x1D95C9B0;

            var landmark = vm.Entities.FirstOrDefault(x => x.Object.OBJ.GUID == MAILBOX_GUID || x.Object.OBJ.GUID == MAILBOX_ALT_GUID);

            if (landmark == null)
                landmark = vm.Entities.FirstOrDefault(x => x.Position != LotTilePos.OUT_OF_WORLD && x.Position.Level == 1);

            if (landmark != null)
                vm.Context.World.State.CenterTile = new Vector2(landmark.VisualPosition.X, landmark.VisualPosition.Y);
            else
            {
                var lotSize = vm.Context.Architecture.Width;
                vm.Context.World.State.CenterTile = new Vector2(lotSize / 2f, lotSize / 2f);
            }
        }

        public override void Update(UpdateState state)
        {
            base.Update(state);

            if (!vm.Ready || vm.Context.Architecture == null) return;

            //handling smooth scaled zoom
            var camType = World.State.Cameras.ActiveType;
            Touch._3D = camType != FSO.LotView.Utils.Camera.CameraControllerType._2D;
            if (World.State.Cameras.ActiveType == FSO.LotView.Utils.Camera.CameraControllerType._3D)
            {
                if (World.BackbufferScale != 1) World.BackbufferScale = 1;
                var s3d = World.State.Cameras.Camera3D;
                if (TargetZoom < -0.25f)
                {
                    TargetZoom -= (TargetZoom - 0.25f) * (1f - (float)Math.Pow(0.975f, 60f / FSOEnvironment.RefreshRate));
                }
                s3d.Zoom3D += ((9.75f - (TargetZoom - 0.25f) * 5.7f) - s3d.Zoom3D) / 10;
            }
            else if (World.State.Cameras.ActiveType == FSO.LotView.Utils.Camera.CameraControllerType._2D)
            {
                if (World.State.Zoom != LastZoom)
                {
                    //zoom has been changed by something else. inherit the value
                    SetTargetZoom(World.State.Zoom);
                    LastZoom = World.State.Zoom;
                }

                LotZoomMapping.Resolve2D(TargetZoom, out WorldZoom targetZoom, out float BaseScale);
                World.BackbufferScale = LotZoomMapping.BackbufferScale(TargetZoom, BaseScale);
                if (World.State.Zoom != targetZoom) World.State.Zoom = targetZoom;

                //TargetZoom is the single authority for zoom here - the wheel, the pinch, the
                //desktop zoom buttons and the 3D easing all write it, and the lines above are
                //the only place it becomes camera state. Smooth zoom in 2D is the fractional
                //backbuffer scale this branch applies, so there is nothing to gain from
                //switching the world's own PreciseZoom easing off (and forcing it off here
                //would also throw away whatever WorldConfig was configured with).
            }
            
            if (ActiveEntity == null || ActiveEntity.Dead || ActiveEntity.PersistID != SelectedSimID)
            {
                ActiveEntity = vm.Entities.FirstOrDefault(x => x is VMAvatar && x.PersistID == SelectedSimID); //try and hook onto a sim if we have none selected.
                //if (ActiveEntity == null) ActiveEntity = vm.Entities.FirstOrDefault(x => x is VMAvatar);

                if (!FoundMe)
                {
                    //First frame we have a controllable sim on a lot. Simply moving the camera to
                    //it (which is all this used to do) is not enough to make the lot playable:
                    //nothing is actually handed control, so no sim is selected and every
                    //interaction is refused until the player goes and clicks one.
                    //VMNetChangeControlCmd is the command that does the whole job - it assigns
                    //the PersistID we are matching against above, registers the avatar in
                    //ObjectQueries, writes the "selected sim" global (3) and centers the camera.
                    if (ActiveEntity != null)
                    {
                        vm.SendCommand(new VMNetChangeControlCmd() { TargetID = ActiveEntity.ObjectID });
                    }
                    else
                    {
                        //No sim of ours on this lot yet. If anybody at all is standing around,
                        //take the first of them so the lot is immediately playable - the command
                        //gives them our PersistID, so the block above picks them up next frame.
                        var fallbackAvatar = vm.Context.ObjectQueries.Avatars.FirstOrDefault();
                        if (fallbackAvatar != null)
                        {
                            vm.SendCommand(new VMNetChangeControlCmd() { TargetID = fallbackAvatar.ObjectID });
                        }
                        else
                        {
                            //Nobody at all: there is nothing to select, so at least point the
                            //camera at somewhere recognisable instead of leaving it at (0,0).
                            CenterOnLotLandmark();
                        }
                    }
                    //All three of the above move the camera, and a ScrollAnchor left over from a
                    //previous lot re-centers on it every frame in World.Update, which would drag
                    //the camera straight back.
                    vm.Context.World.State.ScrollAnchor = null;
                    FoundMe = true;
                }
                Queue.QueueOwner = ActiveEntity;
            }

            //The build/buy tools (UIWallPlacer, UIFloorPainter, UIWallPainter, UIGrassPaint,
            //UIRoofer, UITerrainFlatten/Raiser) all gate affordability on
            //Parent.ActiveEntity.TSOState.Budget.Value, which nothing updates in TS1 mode
            //(VMNetAsyncResponseCmd skips TS1), leaving it stuck at the seeded 1000000. Push the
            //family's real money - the same figure BudgetService shows in the HUD - into that
            //field so buying and building are gated the same way. See BuildBudget for the mapping
            //and for why "no family" means unlimited rather than zero.
            var budgetAvatar = ActiveEntity as VMAvatar;
            if (budgetAvatar?.TSOState?.Budget != null)
            {
                var tsoBudget = BuildBudget.TsoBudgetFor(BudgetService.CurrentFamily?.Budget);
                if (budgetAvatar.TSOState.Budget.Value != tsoBudget) budgetAvatar.TSOState.Budget.Value = tsoBudget;
            }

            if (GotoObject == null) GotoObject = vm.Context.CreateObjectInstance(GOTO_GUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH, true).Objects[0];


            //update plumbbob
            //
            // Nothing in this loop reads the cursor, and nothing here should. The head object is
            // set from two facts about the sim alone: whether it is the one being controlled, and
            // whether it is category 87 (TS1 "at work or at school", the crowd TS1 gave a star
            // rather than a diamond). It is a standing decoration attached to sims, not a
            // response to the player pointing at one.
            //
            // That is the whole answer to "should the motive spin be scoped to hover": it was
            // never hover-scoped, and gating it on the cursor would be a regression rather than a
            // fix. A diamond that only started spinning while hovered would stop answering the
            // question it exists to answer - "is that sim in trouble", asked of the whole lot at
            // once, without finding and pointing at anybody. It is also the opposite failure the
            // hover report was about: the spin appears for every sim that has a diamond whether
            // or not there is anything to do about it, and it is worth having, because spinning
            // faster is a statement about the sim rather than a panel of dead buttons about the
            // player's cursor.
            var plumb = Content.Get().RCMeshes.Get("arrow.fsom");
            var motiveState = PlumbbobMotive.Enabled;
            foreach (VMAvatar avatar in vm.Context.ObjectQueries.Avatars)
            {
                if (avatar.Avatar == null) continue;
                var isActive = (avatar == ActiveEntity);
                if ((avatar.Avatar.HeadObject == plumb) != isActive)
                {
                    avatar.Avatar.HeadObject = (avatar == ActiveEntity) ? plumb : null;
                    avatar.Avatar.HeadObjectSpeedyVel = 0.2f;
                }
                avatar.Avatar.HeadObjectRotation += 3f / FSOEnvironment.RefreshRate;
                if (isActive)
                {
                    avatar.Avatar.HeadObjectRotation += avatar.Avatar.HeadObjectSpeedyVel;
                    avatar.Avatar.HeadObjectSpeedyVel *= 0.98f;
                } else if (avatar.GetValue(FSO.SimAntics.Model.VMStackObjectVariable.Category) == 87)
                {
                    avatar.Avatar.HeadObject = Content.Get().RCMeshes.Get("star.fsom");
                }

                // The three lines above are the resting plumbbob and are untouched. This only
                // adds to the spin, and only for a sim that actually has a diamond over it - the
                // controlled sim's arrow, or the stars above the category-87 crowd - so a sim with
                // no head object costs eight motive reads and draws nothing. That test is the
                // whole gate: a sim with no diamond gets nothing, a sim with one gets whatever
                // its own needs say, and the cursor is not consulted either way.
                // PlumbbobMotive.ForAvatar is where the eight needs become a spin rate, and is a
                // pure function of them - so the answer is the same sim's answer however the loop
                // got to it, and no sim inherits its neighbour's spin.
                if (motiveState && avatar.Avatar.HeadObject != null)
                {
                    var presentation = PlumbbobMotive.ForAvatar(avatar);
                    if (presentation.SpinBoost > 0f)
                    {
                        avatar.Avatar.HeadObjectRotation += presentation.SpinBoost / FSOEnvironment.RefreshRate;
                    }
                }
            }
            if (Visible)
            {
                if (ShowTooltip) state.UIState.TooltipProperties.UpdateDead = false;

                bool scrolled = false;

                World.State.Cameras.CameraFirstPerson.CaptureMouse = true;

                if (RMBScroll)
                {
                    World.State.ScrollAnchor = null;
                    Vector2 scrollBy = new Vector2();
                    if (state.TouchMode)
                    {
                        scrollBy = new Vector2(RMBScrollX - state.MouseState.X, RMBScrollY - state.MouseState.Y);
                        RMBScrollX = state.MouseState.X;
                        RMBScrollY = state.MouseState.Y;
                        scrollBy /= 128f;
                        scrollBy /= FSOEnvironment.DPIScaleFactor;
                    }
                    else
                    {
                        scrollBy = new Vector2(state.MouseState.X - RMBScrollX, state.MouseState.Y - RMBScrollY);
                        scrollBy *= 0.0005f;

                        var angle = (Math.Atan2(state.MouseState.X - RMBScrollX, (RMBScrollY - state.MouseState.Y) * 2) / Math.PI) * 4;
                        angle += 8;
                        angle %= 8;

                        CursorType type = CursorType.ArrowUp;
                        switch ((int)Math.Round(angle))
                        {
                            case 0: type = CursorType.ArrowUp; break;
                            case 1: type = CursorType.ArrowUpRight; break;
                            case 2: type = CursorType.ArrowRight; break;
                            case 3: type = CursorType.ArrowDownRight; break;
                            case 4: type = CursorType.ArrowDown; break;
                            case 5: type = CursorType.ArrowDownLeft; break;
                            case 6: type = CursorType.ArrowLeft; break;
                            case 7: type = CursorType.ArrowUpLeft; break;
                        }
                        GameFacade.Cursor.SetCursor(type);
                    }
                    World.Scroll(scrollBy * (60f / FSOEnvironment.RefreshRate));
                    scrolled = true;
                }
                if (MouseIsOn)
                {
                    if (state.MouseState.RightButton == ButtonState.Pressed)
                    {
                        if (RMBScroll == false)
                        {
                            RMBScroll = true;
                            RMBScrollX = state.MouseState.X;
                            RMBScrollY = state.MouseState.Y;
                        }
                    }
                    else
                    {
                        if (!scrolled && GlobalSettings.Default.EdgeScroll && !state.TouchMode) scrolled = World.TestScroll(state);
                    }
                }

                if (state.MouseState.RightButton != ButtonState.Pressed)
                {
                    if (RMBScroll)
                    {
                        GameFacade.Cursor.SetCursor(CursorType.Normal);

                        //The right button is already spoken for - holding it and moving pans the
                        //camera - so a right click that never moved used to do nothing at all,
                        //leaving no way to switch sim except the lot view. Treat that as what it
                        //looks like: hand control to the sim under the cursor. Restricted to the
                        //player's own household, since only those sims can be controlled, and
                        //skipped on touch, where a second finger emulates the right button and
                        //the two-finger gestures have their own handling.
                        if (LiveMode && ActiveEntity != null && !FSOEnvironment.SoftwareKeyboard
                            && Math.Abs(state.MouseState.X - RMBScrollX) < MouseClickTolerance
                            && Math.Abs(state.MouseState.Y - RMBScrollY) < MouseClickTolerance)
                        {
                            var clickedId = World.GetObjectIDAtScreenPos(RMBScrollX, RMBScrollY, GameFacade.GraphicsDevice);
                            var clicked = vm.GetObjectById(clickedId);
                            if (clicked is VMAvatar && vm.TS1State.CurrentFamily?.RuntimeSubset.Contains(clicked.Object.OBJ.GUID) == true)
                            {
                                vm.SendCommand(new VMNetChangeControlCmd() { TargetID = clicked.ObjectID });
                                //Apply the switch locally instead of waiting for the command to
                                //come back around: the pie menu the player is about to open is
                                //built against ActiveEntity, and the plumbbob and interaction
                                //queue both follow it.
                                ActiveEntity = clicked;
                                Queue.QueueOwner = ActiveEntity;
                                if (PieMenu != null)
                                {
                                    //The tap helper treats any button as a tap, so this same click
                                    //has just opened a pie menu for the sim we clicked. It was
                                    //built as the sim we have just given up - whose PersistID the
                                    //command above has cleared - so an item picked from it would be
                                    //queued by nobody. Selecting the sim is what the click was
                                    //for, so drop the menu rather than leave it half live.
                                    PieMenu.RemoveSimScene();
                                    this.Remove(PieMenu);
                                    PieMenu = null;
                                }
                                HITVM.Get().PlaySoundEvent(UISounds.Click);
                            }
                        }
                    }
                    RMBScroll = false;
                }

                //Middle mouse rotates the camera. The free rotation that happens while the
                //button is held is the camera controller's job; this only has to notice a press
                //that never turned into a drag and snap the lot a quarter turn clockwise. Like
                //the right click, the drag has already moved things by the time the button comes
                //back up, so the press position is what tells the two apart.
                if (state.MouseState.MiddleButton == ButtonState.Pressed)
                {
                    if (!MMBScroll)
                    {
                        MMBScroll = true;
                        MMBScrollX = state.MouseState.X;
                        MMBScrollY = state.MouseState.Y;
                    }
                }
                else if (MMBScroll)
                {
                    MMBScroll = false;
                    if (!FSOEnvironment.SoftwareKeyboard
                        && World.State.Cameras.ActiveType != FSO.LotView.Utils.Camera.CameraControllerType._3D
                        && Math.Abs(state.MouseState.X - MMBScrollX) < MouseClickTolerance
                        && Math.Abs(state.MouseState.Y - MMBScrollY) < MouseClickTolerance)
                    {
                        //2D only: in 3D the middle button is a free look, and snapping the
                        //rotation to a quarter turn underneath it would yank the camera. Goes
                        //through the screen's rotation step rather than straight to the world, so
                        //the value the lot view and the desktop rotate buttons keep in step with
                        //the rotation actually on screen. The screen only nulls its World after
                        //it has removed this control, so it is safe to use from here.
                        var screen = Parent as TS1GameScreen;
                        if (screen != null) screen.WorldRotationStep = (screen.WorldRotationStep + 1) % 4;
                        else World.State.Rotation = (WorldRotation)(((int)World.State.Rotation + 1) % 4);
                        HITVM.Get().PlaySoundEvent(UISounds.ObjectRotate);
                    }
                }

                if (!LiveMode && PieMenu != null)
                {
                    PieMenu.RemoveSimScene();
                    this.Remove(PieMenu);
                    PieMenu = null;
                }

                if (state.NewKeys.Contains(Keys.F11))
                {
                    var utils = new FSO.SimAntics.Test.CollisionTestUtils();
                    utils.VerifyAllCollision(vm);
                }

                if (state.NewKeys.Contains(Keys.F8))
                {
                    UIMobileAlert alert = null;
                    alert = new UIMobileAlert(new UIAlertOptions()
                    {
                        Title = "Debug Lot Thumbnail",
                        Message = "Arch Value: "+VMArchitectureStats.GetArchValue(vm.Context.Architecture),
                        Buttons = UIAlertButton.Ok((btn) => UIScreen.RemoveDialog(alert))
                    });
                    Texture2D roofless = null;
                    var thumb = World.GetLotThumb(GameFacade.GraphicsDevice, (tex) => roofless = FSO.Common.Utils.TextureUtils.Decimate(tex, GameFacade.GraphicsDevice, 2, false));
                    thumb = FSO.Common.Utils.TextureUtils.Decimate(thumb, GameFacade.GraphicsDevice, 2, false);
                    alert.SetIcon(thumb, thumb.Width, thumb.Height);
                    UIScreen.GlobalShowDialog(alert, true);
                }
                if (LiveMode) LiveModeUpdate(state, scrolled);
                else if (CustomControl != null)
                {
                    //ESC puts the build tool down. Nothing else does: switching category or mode
                    //goes through the catalogue panel releasing it explicitly, and a held object
                    //has its own escape handling in the object holder below.
                    if (state.KeyboardState.IsKeyDown(Keys.Escape))
                    {
                        ReleaseCustomControl();
                    }
                    else
                    {
                        if (FSOEnvironment.SoftwareKeyboard) CustomControl.MousePosition = new Point(UIScreen.Current.ScreenWidth / 2, UIScreen.Current.ScreenHeight / 2);
                        else
                        {
                            CustomControl.Modifiers = 0;
                            if (state.CtrlDown) CustomControl.Modifiers |= UILotControlModifiers.CTRL;
                            if (state.ShiftDown) CustomControl.Modifiers |= UILotControlModifiers.SHIFT;
                            CustomControl.MousePosition = state.MouseState.Position;
                        }
                        CustomControl.Update(state, scrolled);
                    }
                }
                else ObjectHolder.Update(state, scrolled);

                //set cutaway around mouse
                UpdateCutaway(state);

                if (RMBScrollX == int.MinValue) Dummy(); //cannon fodder for mono AOT compilation: never called but gives these constructors a meaning in life
            }
        }

        private void Dummy()
        {
            CustomControl = new UIWallPlacer(vm, World, this, new List<int>());
            CustomControl = new UIFloorPainter(vm, World, this, new List<int>());
            CustomControl = new UIWallPainter(vm, World, this, new List<int>());
            CustomControl = new UIGrassPaint(vm, World, this, new List<int>());
            CustomControl = new UIRoofer(vm, World, this, new List<int>());
            CustomControl = new UITerrainFlatten(vm, World, this, new List<int>());
            CustomControl = new UITerrainRaiser(vm, World, this, new List<int>());
        }

        private void UpdateCutaway(UpdateState state)
        {
            if (vm.Context.Blueprint != null)
            {
                World.State.DynamicCutaway = (WallsMode == 1);
                //first we need to cycle the rooms that are being cutaway. Keep this up even if we're in all-cut mode.
                var mouseTilePos = World.EstTileAtPosWithScroll(new Vector2(state.MouseState.X, state.MouseState.Y) / FSOEnvironment.DPIScaleFactor);
                var roomHover = vm.Context.GetRoomAt(LotTilePos.FromBigTile((short)(mouseTilePos.X), (short)(mouseTilePos.Y), World.State.Level));
                var outside = (vm.Context.RoomInfo[roomHover].Room.IsOutside);
                if (!outside && !CutRooms.Contains(roomHover))
                    CutRooms.Add(roomHover); //outside hover should not persist like with other rooms.
                while (CutRooms.Count > 3) CutRooms.Remove(CutRooms.ElementAt(0));

                if (LastWallMode != WallsMode)
                {
                    if (WallsMode == 0) //walls down
                    {
                        LastCuts = new bool[vm.Context.Architecture.Width * vm.Context.Architecture.Height];
                        vm.Context.Blueprint.Cutaway = LastCuts;
                        vm.Context.Blueprint.Changes.SetFlag(BlueprintGlobalChanges.WALL_CUT_CHANGED);
                        for (int i = 0; i < LastCuts.Length; i++) LastCuts[i] = true;
                    }
                    else if (WallsMode == 1)
                    {
                        MouseCutRect = new Rectangle();
                        LastCutRooms = new HashSet<uint>() { uint.MaxValue }; //must regenerate cuts
                    }
                    else //walls up or roof
                    {
                        LastCuts = new bool[vm.Context.Architecture.Width * vm.Context.Architecture.Height];
                        vm.Context.Blueprint.Cutaway = LastCuts;
                        vm.Context.Blueprint.Changes.SetFlag(BlueprintGlobalChanges.WALL_CUT_CHANGED);
                    }
                    LastWallMode = WallsMode;
                }

                if (WallsMode == 1)
                {
                    HashSet<uint> finalRooms;
                    int recut = 0;
                    if (FSOEnvironment.SoftwareKeyboard)
                    {
                        finalRooms = new HashSet<uint>();
                        foreach (var room in vm.Context.RoomInfo)
                        {
                            if (!room.Room.IsOutside && room.Room.Floor == World.State.Level-1) finalRooms.Add(room.Room.RoomID);
                        }
                    }
                    else
                    {
                        if (RMBScroll || !MouseIsOn) return;
                        finalRooms = new HashSet<uint>(CutRooms);
                        var newCut = CutawayDecision.ComputeMouseCutRect((int)mouseTilePos.X, (int)mouseTilePos.Y, World.State.CutRotation);
                        if (newCut != MouseCutRect)
                        {
                            MouseCutRect = newCut;
                            recut = 1;
                        }
                    }

                    if (CutawayDecision.ShouldRegenerate(finalRooms, LastCutRooms, World.State.Level, LastFloor, World.State.CutRotation, LastRotation))
                    {
                        LastCuts = VMArchitectureTools.GenerateRoomCut(vm.Context.Architecture, World.State.Level, World.State.CutRotation, finalRooms);
                        recut = 2;
                        LastFloor = World.State.Level;
                        LastRotation = World.State.CutRotation;
                    }
                    LastCutRooms = finalRooms;

                    if (recut > 0)
                    {
                        var finalCut = new bool[LastCuts.Length];
                        Array.Copy(LastCuts, finalCut, LastCuts.Length);
                        var notableChange = VMArchitectureTools.ApplyCutRectangle(vm.Context.Architecture, World.State.Level, finalCut, MouseCutRect);
                        if (CutawayDecision.ShouldPushCut(recut, notableChange, LastRectCutNotable))
                        {
                            vm.Context.Blueprint.Cutaway = finalCut;
                            vm.Context.Blueprint.Changes.SetFlag(BlueprintGlobalChanges.WALL_CUT_CHANGED);
                        }
                        LastRectCutNotable = notableChange;
                    }
                }
            }
        }
    }

    /// <summary>
    /// The per-sim auto-follow control: while the player points at a sim in live mode, this
    /// offers the two directions TS1's Downtown expansion ships - "Invite to Downtown" and
    /// "Take Home" - and names the one that is already queued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// It knows nothing about the lot, the VM, or the engine. <see cref="UILotControl"/> hands it
    /// a plain <see cref="AutoFollowSubject"/> and the pure <see cref="AutoFollowOffer"/> that
    /// <see cref="AutoFollow.Offer"/> decided on, once a frame, so the eligibility rule is one
    /// pure function with a test file rather than a set of conditions smeared across button
    /// states. The lot control does the actual work when a button is pressed, and asks for a
    /// <see cref="Refresh"/> afterwards.
    /// </para>
    /// <para>
    /// <b>It is up only when it has something to press.</b> A sim that cannot be sent anywhere
    /// - yourself, a pet, a ghost, somebody already coming along with the family - gets no panel
    /// at all, rather than the panel this used to show: a heading, a sentence explaining the
    /// refusal, and two buttons that cannot be pressed. Every string the status line can produce
    /// for a refused sim is a restatement of a refusal the player cannot act on, so the whole
    /// panel was cost with no information in it. See
    /// <see cref="AutoFollowOffer.IsShown"/> for the rule.
    /// </para>
    /// <para>
    /// Lives in this file rather than beside the travel picker because it is a different kind of
    /// thing: <c>UITS1NeighborhoodVisitLotPanel</c> is raised by a script through
    /// <c>VMDialogType</c> and answers a thread, whereas this tracks the mouse and is polled.
    /// </para>
    /// </remarks>
    internal class UIAutoFollowPanel : UIContainer
    {
        private const int PanelWidth = 520;
        private const int PanelHeight = 152;
        private const int Margin = 16;
        private const int ButtonHeight = 48;
        private const int ButtonGap = 8;

        /// <summary>Raised with the direction the player pressed.</summary>
        public event Action<AutoFollowAction> OnAutoFollow;

        private UIDiagonalStripe background;
        private UILabel caption;
        private UILabel status;
        private UIBigButton inviteButton;
        private UIBigButton homeButton;

        /// <summary>The sim currently described, kept so <see cref="Refresh"/> can redraw without one.</summary>
        private AutoFollowSubject current;
        private AutoFollowAction currentAvailable;
        private AutoFollowReason currentReason;
        private bool currentLotIsDowntown;

        public UIAutoFollowPanel()
        {
            background = new UIDiagonalStripe(new Point(PanelWidth, PanelHeight), UIDiagonalStripeSide.RIGHT, Color.Black * 0.6f);
            Add(background);

            caption = NewLabel(16, Color.White, 16);
            Add(caption);

            status = NewLabel(44, Color.LightGray, 12);
            Add(status);

            inviteButton = NewButton(DirectionName(AutoFollowAction.InviteToDowntown), true);
            homeButton = NewButton(DirectionName(AutoFollowAction.TakeHome), false);
            Add(inviteButton);
            Add(homeButton);

            Visible = false;
        }

        private UILabel NewLabel(int y, Color color, int size)
        {
            // UILabel hands out the shared default style, so it has to be cloned before it is
            //restyled or every other label in the game inherits this size and colour
            var label = new UILabel()
            {
                Position = new Vector2(Margin, y),
                CaptionStyle = TextStyle.DefaultLabel.Clone()
            };
            label.CaptionStyle.Size = size;
            label.CaptionStyle.Color = color;
            return label;
        }

        private UIBigButton NewButton(string text, bool green)
        {
            // height comes from the plate artwork (a three-slice 485x48 on desktop, 600x100 on
            // touch), so only the width is set - UIBigButton is what fits the caption to it
            var button = new UIBigButton(green)
            {
                Caption = text,
                Width = PanelWidth - (2 * Margin)
            };
            var action = green ? AutoFollowAction.InviteToDowntown : AutoFollowAction.TakeHome;
            button.OnButtonClick += (btn) => OnAutoFollow?.Invoke(action);
            return button;
        }

        /// <summary>
        /// Points the control at a sim and works out what it can offer.
        /// </summary>
        /// <param name="subject">The sim under the cursor, described without the lot.</param>
        /// <param name="lotIsDowntown">Whether the current lot is the Downtown neighbourhood.</param>
        /// <param name="offer">
        /// The decision for this sim, from <see cref="AutoFollow.Offer"/>. Passed in rather than
        /// recomputed so that what the control shows and whether it is up at all are decided by
        /// one call.
        /// </param>
        /// <remarks>
        /// The control is only ever put on screen for a sim with a direction to press. That is
        /// <see cref="AutoFollowOffer.IsShown"/>'s decision, and the lot control has already
        /// applied it; the check here is a second line of the same rule rather than a rule of its
        /// own, so a future caller that forgets cannot end up with a panel of dead buttons.
        /// </remarks>
        public void ShowFor(AutoFollowSubject subject, bool lotIsDowntown, AutoFollowOffer offer)
        {
            if (!offer.IsShown)
            {
                Hide();
                return;
            }

            current = subject;
            currentLotIsDowntown = lotIsDowntown;
            currentAvailable = offer.Available;
            currentReason = offer.Reason;
            Visible = true;
            Refresh();
        }

        /// <summary>Puts the control away, e.g. when the mouse leaves the sim or a dialog opens.</summary>
        public void Hide()
        {
            Visible = false;
        }

        /// <summary>
        /// Redraws the caption, the status line and the two buttons from the last sim shown.
        /// </summary>
        /// <remarks>
        /// Called by the lot control after a request is queued, so the buttons change on the same
        /// frame the request lands rather than waiting for the next hover to re-evaluate.
        /// </remarks>
        public void Refresh()
        {
            if (!Visible) return;

            var pending = current?.Pending ?? AutoFollowAction.None;
            caption.Caption = AutoFollowCaption(current, currentLotIsDowntown);
            status.Caption = AutoFollowStatus(currentAvailable, currentReason, pending, currentLotIsDowntown);

            inviteButton.Disabled = (currentAvailable & AutoFollowAction.InviteToDowntown) == AutoFollowAction.None;
            homeButton.Disabled = (currentAvailable & AutoFollowAction.TakeHome) == AutoFollowAction.None;

            // the direction the current lot implies gets the green plate, so the two offers read
            // as "this one here" and "the other one" rather than as two equal options
            var preferred = AutoFollow.Preferred(currentAvailable, currentLotIsDowntown);
            var preferredIsDowntown = (preferred == AutoFollowAction.InviteToDowntown);
            // qualified: this file also imports FSO.Client.UI.Model, so the short name is not
            // safely ours to claim
            var style = Simitone.Client.UI.Model.UIStyle.Current;
            inviteButton.CaptionStyle.Color = preferredIsDowntown ? style.GreenBtnTxt : style.BtnTxt;
            homeButton.CaptionStyle.Color = preferredIsDowntown ? style.BtnTxt : style.GreenBtnTxt;

            Place();
        }

        /// <summary>
        /// The heading, naming the sim's relationship to the player when that is what decides
        /// eligibility.
        /// </summary>
        private static string AutoFollowCaption(AutoFollowSubject subject, bool lotIsDowntown)
        {
            if (subject == null) return SimitoneStrings.Get("410", "Auto-follow");
            if (subject.InTravellingFamily) return SimitoneStrings.Get("411", "Auto-follow - travelling with you");
            return lotIsDowntown
                ? SimitoneStrings.Get("412", "Auto-follow - Downtown")
                : SimitoneStrings.Get("410", "Auto-follow");
        }

        /// <summary>
        /// The line under the heading: what is already queued for this sim.
        /// </summary>
        /// <param name="available">What <see cref="AutoFollow.Available"/> decided.</param>
        /// <param name="reason">The reason it gave.</param>
        /// <param name="pending">The directions the sim already has a request standing in.</param>
        /// <param name="lotIsDowntown">Whether the current lot is the Downtown neighbourhood.</param>
        /// <remarks>
        /// The refusals are still spelled out below, but the panel is no longer up for any of
        /// them: a sim with no direction on offer is not shown at all
        /// (<see cref="AutoFollowOffer.IsShown"/>), so that the player is never handed a sentence
        /// about a sim they cannot act on. What is left is kept rather than deleted because it is
        /// a total function over <see cref="AutoFollowReason"/> - a reason added to the enum
        /// without a line here must still produce a caption, not a null.
        /// </remarks>
        private static string AutoFollowStatus(AutoFollowAction available, AutoFollowReason reason, AutoFollowAction pending, bool lotIsDowntown)
        {
            // Every line below is a table entry (ids 413 to 423) with the English kept as the
            // second argument, so a build with no table says exactly what it always said. 413
            // takes two arguments and both are direction names, so it is composed rather than
            // concatenated: a language that puts the instruction first still has one to put it in.
            //
            // one request queued: name the direction that is standing, and say that the other
            // button is what changes it, so a greyed-out button reads as "already done that way"
            // rather than as a dead end. This is the status line doing the job it is there for:
            // naming which of two independent slots is occupied, which is the one thing the
            // player cannot work out from the lot.
            if (reason == AutoFollowReason.PendingInOtherDirection)
            {
                return SimitoneStrings.Format("413", "Already queued: " + DirectionName(pending)
                    + ". Press " + DirectionName(available) + " to change it.",
                    DirectionName(pending), DirectionName(available));
            }

            // both slots occupied, which only an older build's writes could leave: either button
            // collapses it back to a single request, so neither is closed
            if (pending == AutoFollowAction.Both)
            {
                return SimitoneStrings.Get("414", "Requests are queued both ways - press one to pick.");
            }

            // No offer, so no panel. Kept only as the total-function fallback described above.
            if (available == AutoFollowAction.None)
            {
                switch (reason)
                {
                    case AutoFollowReason.IsSelf:
                        return SimitoneStrings.Get("415", "That is you - you are already here.");
                    case AutoFollowReason.IsPet:
                        return SimitoneStrings.Get("416", "Pets cannot be auto-followed.");
                    case AutoFollowReason.IsGhost:
                        return SimitoneStrings.Get("417", "Ghosts are not travelling.");
                    case AutoFollowReason.NotOnLot:
                        return SimitoneStrings.Get("418", "Only sims on this lot can be followed.");
                    case AutoFollowReason.AlreadyTravelling:
                        return SimitoneStrings.Get("419", "Already coming along with the family.");
                    // reached only if a queued-both-ways sim were ever closed, which the branch
                    // above deliberately does not do
                    case AutoFollowReason.AlreadyPending:
                        return SimitoneStrings.Get("420", "This sim has a request queued both ways.");
                    default:
                        return SimitoneStrings.Get("421", "Not available.");
                }
            }

            return lotIsDowntown
                ? SimitoneStrings.Get("422", "They are away from home.")
                : SimitoneStrings.Get("423", "They are not in your family.");
        }

        /// <summary>
        /// The direction as the buttons name it, so the status line and the button captions cannot
        /// drift apart. Only ever asked about a single direction.
        /// </summary>
        private static string DirectionName(AutoFollowAction action)
        {
            return action == AutoFollowAction.TakeHome
                ? SimitoneStrings.Get("424", "Take Home")
                : SimitoneStrings.Get("425", "Invite to Downtown");
        }

        /// <summary>
        /// Lays the controls out. Positions are absolute, so this is re-applied on a window
        /// resize rather than being set once in the constructor.
        /// </summary>
        private void Place()
        {
            var screen = UIScreen.Current;
            if (screen == null) return;

            background.BodySize = new Point(PanelWidth, PanelHeight);
            X = screen.ScreenWidth - PanelWidth - Margin;
            Y = screen.ScreenHeight - PanelHeight - Margin;

            var buttonY = PanelHeight - ButtonHeight - Margin;
            inviteButton.Position = new Vector2(Margin, buttonY);
            homeButton.Position = new Vector2(Margin, buttonY - (ButtonHeight + ButtonGap));
        }

        public override void GameResized()
        {
            base.GameResized();
            Refresh();
        }
    }
}
