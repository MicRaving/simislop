using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Model;
using FSO.Common;
using FSO.Common.Rendering.Framework.Model;
using FSO.Common.Utils;
using FSO.Content;
using FSO.HIT;
using FSO.LotView.RC;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Panels.Desktop
{
    public class UIDesktopUCP : UICachedContainer
    {
        public UIImage Background;
        public UIImage FriendIcon;

        /// <summary>
        /// Arms the eyedropper: the next click on the lot reports the style under the cursor
        /// instead of picking the object up. Only meaningful in Buy and Build mode, which is what
        /// <see cref="SetMode"/> uses to show and hide it - in Live mode a click is a pie menu,
        /// not a pick. <see cref="Game.LotControl.ObjectHolder.EyedropperMode"/> is the state; this
        /// button only mirrors it, and <see cref="Update"/> re-reads it every frame because the pick
        /// disarms the tool on its own.
        /// </summary>
        public UIButton EyedropperButton;

        public UIButton LiveButton;
        public UIButton BuyButton;
        public UIButton BuildButton;
        public UIButton OptionsButton;

        public UILabel MoneyLabel;
        public UILabel TimeLabel;
        public UILabel TimeLabelShadow;
        public UILabel FriendsLabel;
        public UILabel FriendsLabelShadow;
        public UILabel FloorLabel;
        public UILabel FloorLabelShadow;

        public UIButton RoofButton;
        public UIButton WallsUpButton;
        public UIButton WallsCutButton;
        public UIButton WallsDownButton;

        public UIButton FloorUpButton;
        public UIButton FloorDownButton;

        public UIButton ZoomInButton;
        public UIButton ZoomOutButton;
        public UIButton RotateCWButton;
        public UIButton RotateCCWButton;

        public UIStencilButton[] SpeedButtons;

        private TS1GameScreen Game;

        public Func<UIMainPanelMode, bool> OnModeClick;

        // static for the same reason RemapSpeed below is: this is a fixed lookup table,
        // not per-instance state. Update() indexes it with `LastFloor - 1` for levels 1..5.
        // The five words are ids 400 to 404 of Simitone's own string table, and this array is
        // UIMainPanel's array rather than a second set of literals, so the desktop clock and the
        // panel beside it can never be given different words for the same floor. The reference
        // is shared, so it is readonly: a caller that sorted or spliced it would silently change
        // the other panel's words too.
        public static readonly string[] FloorNames = UIMainPanel.FloorNames;

        /// <summary>
        /// The <c>VM.SpeedMultiplier</c> -> speed-button index table, as a bijection onto the four
        /// buttons: 0/1/3/10 become 4/1/2/3, where 4 is pause.
        ///
        /// <para>
        /// This is <see cref="GameplaySpeed.RemapSpeed"/> minus <see cref="GameplaySpeed.Disabled"/>.
        /// The -1 is dropped on purpose - it is not one of the four buttons, it is "gameplay may
        /// not advance" (Buy/Build, and a lot with no sims in it) and the highlight loop has
        /// nothing to say about it. The button state for that case is driven separately, by
        /// <see cref="UpdateBuildBuy"/> and the disabled/red pause button in <see cref="Update"/>.
        /// </para>
        ///
        /// <para>
        /// The names are kept because <c>UIClockPanel</c> publishes an identical table and the two
        /// are expected to agree exactly; deriving this one from the shared constants is what
        /// keeps them from drifting.
        /// </para>
        /// </summary>
        public static readonly Dictionary<int, int> RemapSpeed = GameplaySpeed.RemapSpeed
            .Where(kvp => kvp.Key != GameplaySpeed.Disabled)
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);

        /// <summary>The inverse of <see cref="RemapSpeed"/>: a speed-button index back to a VM multiplier.</summary>
        public static readonly Dictionary<int, int> ReverseRemap = RemapSpeed.ToDictionary(x => x.Value, x => x.Key);

        public UIDesktopUCP(TS1GameScreen screen)
        {
            Game = screen;
            var ui = Content.Get().CustomUI;
            var gd = GameFacade.GraphicsDevice;
            var sDir = new Vector3(0, 2, 0.5f);

            Background = new UIImage(ui.Get("d_live_bg.png").Get(gd));
            Add(Background);

            FriendIcon = new UIImage(ui.Get("d_live_friend.png").Get(gd)) { Position = new Vector2(156, 186) };
            Add(FriendIcon);

            //Eyedropper - shown in Buy and Build only, hidden until SetMode says otherwise.
            Add(EyedropperButton = new UIStencilButton(ui.Get("d_live_eyedropper.png").Get(gd))
            {
                Position = new Vector2(155, 164),
                Shadow = true,
                ShadowParam = sDir,
                Tooltip = SimitoneStrings.Get("158", "Eyedropper Tool (E)")
            });
            EyedropperButton.OnButtonClick += ToggleEyedropper;
            EyedropperButton.Visible = false;

            //The tooltips below are ids 140 to 157 of Simitone's own string table, with the
            //English kept as the second argument so a build with no table reads the same. TS1's
            //tables have no name for any of these controls - they are drawn as artwork.
            Add(LiveButton = new UIButton(ui.Get("d_live_live.png").Get(gd)) { Position = new Vector2(15, 2), Tooltip = SimitoneStrings.Get("140", "Live Mode") });
            Add(BuyButton = new UIButton(ui.Get("d_live_buy.png").Get(gd)) { Position = new Vector2(107, 27), Tooltip = SimitoneStrings.Get("141", "Buy Mode") });
            Add(BuildButton = new UIButton(ui.Get("d_live_build.png").Get(gd)) { Position = new Vector2(179, 80), Tooltip = SimitoneStrings.Get("142", "Build Mode") });
            Add(OptionsButton = new UIButton(ui.Get("d_live_opt.png").Get(gd)) { Position = new Vector2(242, 165), Tooltip = SimitoneStrings.Get("143", "Options") });

            Add(FloorUpButton = new UIStencilButton(ui.Get("d_live_floorup.png").Get(gd)) { Position = new Vector2(16, 150), Shadow = true, ShadowParam = sDir, Tooltip = SimitoneStrings.Get("144", "Floor Up") });
            Add(FloorDownButton = new UIStencilButton(ui.Get("d_live_floordown.png").Get(gd)) { Position = new Vector2(16, 192), Shadow = true, ShadowParam = sDir, Tooltip = SimitoneStrings.Get("145", "Floor Down") });

            Add(RoofButton = new UIStencilButton(ui.Get("d_live_w1.png").Get(gd)) { Position = new Vector2(15, 111), Shadow = true, ShadowParam = sDir, Tooltip = SimitoneStrings.Get("146", "Roof") });
            Add(WallsUpButton = new UIStencilButton(ui.Get("d_live_w2.png").Get(gd)) { Position = new Vector2(50, 107), Shadow = true, ShadowParam = sDir, Tooltip = SimitoneStrings.Get("147", "Walls Up") });
            Add(WallsCutButton = new UIStencilButton(ui.Get("d_live_w3.png").Get(gd)) { Position = new Vector2(86, 112), Shadow = true, ShadowParam = sDir, Tooltip = SimitoneStrings.Get("148", "Walls Cutaway") });
            Add(WallsDownButton = new UIStencilButton(ui.Get("d_live_w4.png").Get(gd)) { Position = new Vector2(117, 122), Shadow = true, ShadowParam = sDir, Tooltip = SimitoneStrings.Get("149", "Walls Down") });

            Add(ZoomInButton = new UIStencilButton(ui.Get("d_live_zoomp.png").Get(gd)) { Position = new Vector2(87, 154), Tooltip = SimitoneStrings.Get("150", "Zoom In") });
            Add(ZoomOutButton = new UIStencilButton(ui.Get("d_live_zoomm.png").Get(gd)) { Position = new Vector2(87, 196), Tooltip = SimitoneStrings.Get("151", "Zoom Out") });
            Add(RotateCWButton = new UIStencilButton(ui.Get("d_live_rotcw.png").Get(gd)) { Position = new Vector2(62, 175), Tooltip = SimitoneStrings.Get("152", "Rotate Clockwise") });
            Add(RotateCCWButton = new UIStencilButton(ui.Get("d_live_rotccw.png").Get(gd)) { Position = new Vector2(114, 175), Tooltip = SimitoneStrings.Get("153", "Rotate Counter-Clockwise") });

            SpeedButtons = new UIStencilButton[4];
            // 154 to 157, in the same order as the array. These name VM speed multipliers rather
            // than anything TS1 ever captioned, so the table is the only place they can live.
            var speedTooltips = new string[]
            {
                SimitoneStrings.Get("154", "Normal Speed"),
                SimitoneStrings.Get("155", "Fast"),
                SimitoneStrings.Get("156", "Ultra Fast"),
                SimitoneStrings.Get("157", "Pause")
            };
            for (int i=0; i<4; i++)
            {
                Add(SpeedButtons[i] = new UIStencilButton(ui.Get($"d_live_speed{i+1}.png").Get(gd))
                {
                    Position = new Vector2(158 + 30 * i, 246),
                    Shadow = true,
                    ShadowParam = sDir,
                    Tooltip = speedTooltips[i]
                });
                var speed = i + 1;
                SpeedButtons[i].OnButtonClick += (btn) =>
                {
                    SwitchSpeed(speed);
                };
            }

            var largeStyle = TextStyle.DefaultLabel.Clone();
            largeStyle.Size = 15;
            largeStyle.Color = UIStyle.Current.DialogTitle;

            var whiteStyle = TextStyle.DefaultLabel.Clone();
            whiteStyle.Size = 12;
            whiteStyle.Color = UIStyle.Current.Text;

            var shadowStyle = TextStyle.DefaultLabel.Clone();
            shadowStyle.Size = 12;
            shadowStyle.Color = Color.Black * 0.5f;

            var friendStyle = TextStyle.DefaultLabel.Clone();
            friendStyle.Size = 12;
            friendStyle.Color = UIStyle.Current.SecondaryText;

            Add(MoneyLabel = new UILabel()
            {
                Position = new Vector2(7, 241),
                Size = new Vector2(138, 30),
                Alignment = TextAlignment.Middle | TextAlignment.Center,
                Caption = "$0",
                CaptionStyle = largeStyle
            });

            Add(TimeLabelShadow = new UILabel()
            {
                Position = new Vector2(157, 221+2),
                Size = new Vector2(114, 16),
                Alignment = TextAlignment.Middle | TextAlignment.Center,
                Caption = "12:00AM",
                CaptionStyle = shadowStyle
            });

            Add(TimeLabel = new UILabel()
            {
                Position = new Vector2(157, 221),
                Size = new Vector2(114, 16),
                Alignment = TextAlignment.Middle | TextAlignment.Center,
                Caption = "12:00AM",
                CaptionStyle = whiteStyle
            });

            Add(FriendsLabelShadow = new UILabel()
            {
                Position = new Vector2(176, 184+2),
                Alignment = TextAlignment.Top | TextAlignment.Left,
                Caption = "0",
                CaptionStyle = shadowStyle
            });

            Add(FriendsLabel = new UILabel()
            {
                Position = new Vector2(176, 184),
                Alignment = TextAlignment.Top | TextAlignment.Left,
                Caption = "0",
                CaptionStyle = friendStyle
            });

            Add(FloorLabelShadow = new UILabel()
            {
                Position = new Vector2(22, 184+2),
                Size = new Vector2(24, 15),
                Alignment = TextAlignment.Center | TextAlignment.Middle,
                // the first entry rather than a literal: Update() replaces this on its first
                // frame, but a hard-coded "1st" here would still be English in whatever language
                // the readout is being drawn in for that one frame
                Caption = FloorNames[0],
                CaptionStyle = shadowStyle
            });

            Add(FloorLabel = new UILabel()
            {
                Position = new Vector2(22, 184),
                Size = new Vector2(24, 15),
                Alignment = TextAlignment.Center | TextAlignment.Middle,
                Caption = FloorNames[0],
                CaptionStyle = whiteStyle
            });

            RoofButton.OnButtonClick += (btn) => SetCut(3);
            WallsUpButton.OnButtonClick += (btn) => SetCut(2);
            WallsCutButton.OnButtonClick += (btn) => SetCut(1);
            WallsDownButton.OnButtonClick += (btn) => SetCut(0);

            LiveButton.OnButtonClick += (btn) => OnModeClick?.Invoke(UIMainPanelMode.LIVE);
            BuyButton.OnButtonClick += (btn) => OnModeClick?.Invoke(UIMainPanelMode.BUY);
            BuildButton.OnButtonClick += (btn) => OnModeClick?.Invoke(UIMainPanelMode.BUILD);
            OptionsButton.OnButtonClick += (btn) => OnModeClick?.Invoke(UIMainPanelMode.OPTIONS);

            ZoomInButton.OnButtonClick += ZoomControl;
            ZoomOutButton.OnButtonClick += ZoomControl;
            RotateCWButton.OnButtonClick += RotateClockwise;
            RotateCCWButton.OnButtonClick += RotateCounterClockwise;

            FloorUpButton.OnButtonClick += (b) => { if (Game.Level < 5) Game.Level++; };
            FloorDownButton.OnButtonClick += (b) => { if (Game.Level > 1) Game.Level--; };

            Size = new Vector2(Background.Width, Background.Height);

            UpdateBuildBuy();
            LastBuildBuyEnabled = Game.vm.Context.Architecture.BuildBuyEnabled;
            //seed the last-seen funds from the current ones, or the first Update reads the whole
            //balance as a gain and floats a "+§N" label
            LastMoney = GetMoney();
            UpdateMoneyDisplay();
            UpdateZoomButton();
        }

        private FSO.LotView.Utils.Camera.ICameraController GetActiveCamera()
        {
            //my god, why have you foresaken me?
            return Game.vm?.Context?.World?.State?.Cameras?.ActiveCamera;
        }

        private void ZoomControl(UIElement button)
        {
            if (GetActiveCamera()?.UseZoomHold != false && Game.InLot) return;
            StepZoomLevel((button == ZoomInButton) ? -1 : 1);
        }

        /// <summary>
        /// Move the lot camera one notch along the discrete zoom ladder (1 = near, 2 = medium,
        /// 3 = far).
        ///
        /// This writes <see cref="UILotControl.TargetZoom"/> - the single zoom authority - rather
        /// than a separate zoom state, so a button press and a mouse wheel notch are the same
        /// thing by the time the lot control sees them. The ladder is walked in whole steps
        /// because that is what the buttons are: the level is derived from where the zoom
        /// already is (so a wheel zoom part way between two zooms steps from the nearer one),
        /// then clamped to the ends of the ladder, and the canonical target for that level is
        /// written back.
        /// </summary>
        private void StepZoomLevel(int delta)
        {
            var lotControl = Game.LotControl;
            if (lotControl == null) return; //no lot to zoom

            var level = LotZoomMapping.DiscreteLevelFor(lotControl.TargetZoom) + delta;
            if (level < 1) level = 1;
            else if (level > 3) level = 3;

            lotControl.TargetZoom = LotZoomMapping.TargetZoomForLevel(level);
            UpdateZoomButton();
        }

        private void RotateCounterClockwise(UIElement button)
        {
            if (GetActiveCamera()?.UseRotateHold != false && Game.InLot) return;
            var newRot = (Game.WorldRotationStep - 1);
            if (newRot < 0) newRot = 3;
            Game.WorldRotationStep = newRot;
        }

        private void RotateClockwise(UIElement button)
        {
            if (GetActiveCamera()?.UseRotateHold != false && Game.InLot) return;
            Game.WorldRotationStep = (Game.WorldRotationStep + 1) % 4;
        }

        private string LastClock = "";
        private int LastSpeed = -1;
        private bool LastSpeedDisabled;
        private int LastCut;
        private int LastMoney = 0;
        private sbyte LastFloor = 0;
        private int LastZoom;

        /// <summary>
        /// The last build/buy state the Buy/Build buttons were set from, or null if they have not
        /// been set yet. Nullable for the same reason as <see cref="LastSpeed"/>'s -1: the
        /// constructor's <see cref="UpdateBuildBuy"/> is the first set, and "unset" has to be
        /// distinguishable from "enabled".
        /// </summary>
        private bool? LastBuildBuyEnabled;

        /// <summary>
        /// Re-read the lot's build/buy state and push it to the Buy/Build buttons, but only when
        /// it has actually changed.
        /// </summary>
        /// <remarks>
        /// <see cref="UpdateBuildBuy"/> is also called on <c>TS1BuildBuyChange</c>, from the two
        /// bhav global calls that take and give up the disable. That event is not the only way
        /// the state moves: the lot derives it from the objects holding it, and a hold is also
        /// released by the engine when its object is removed, or dropped outright when the lot
        /// reloads - neither of which runs bhav, and so neither of which signals. Without this
        /// the desktop's Buy/Build stayed greyed out for exactly the case it was meant to catch.
        /// One bool read per frame, buttons touched only on a change.
        /// </remarks>
        private void RefreshBuildBuyIfChanged()
        {
            var bbEnable = Game?.vm?.Context?.Architecture?.BuildBuyEnabled ?? true;
            if (LastBuildBuyEnabled.HasValue && LastBuildBuyEnabled.Value == bbEnable) return;
            LastBuildBuyEnabled = bbEnable;
            UpdateBuildBuy();
        }

        /// <summary>
        /// The speed button for a VM multiplier. The VM can hold a multiplier that is not one of
        /// the four the buttons name (a cheat or script can set any value), so an unknown value
        /// highlights the fastest button whose multiplier it reaches, and anything below the
        /// first step reads as normal speed, instead of failing the lookup every frame.
        /// </summary>
        private static int ButtonForMultiplier(int multiplier)
        {
            if (multiplier < 0) multiplier = 0;
            if (RemapSpeed.TryGetValue(multiplier, out var exact)) return exact;

            int best = -1, bestKey = int.MinValue;
            foreach (var kvp in RemapSpeed)
            {
                if (kvp.Key > 0 && kvp.Key <= multiplier && kvp.Key > bestKey)
                {
                    bestKey = kvp.Key;
                    best = kvp.Value;
                }
            }
            return best != -1 ? best : RemapSpeed[1];
        }

        public override void Update(UpdateState state)
        {
            var vm = Game.vm;
            var min = vm.Context.Clock.Minutes;
            var hour = vm.Context.Clock.Hours;

            string suffix = (hour > 11) ? "PM" : "AM";
            hour %= 12;
            if (hour == 0) hour = 12;

            var text = hour.ToString() + ":" + min.ToString().PadLeft(2, '0') + " " + suffix;

            if (text != LastClock)
            {
                LastClock = text;
                TimeLabel.Caption = text;
                TimeLabelShadow.Caption = text;
            }

            if (Game.Level != LastFloor)
            {
                LastFloor = Game.Level;
                FloorLabel.Caption = FloorNames[LastFloor - 1];
                FloorLabelShadow.Caption = FloorNames[LastFloor - 1];
                FloorDownButton.Disabled = LastFloor == 1;
                FloorUpButton.Disabled = LastFloor == 5;
            }

            //The VM's -1 is "gameplay may not advance" rather than a speed, so it is read as the
            //paused button (index 4) for the highlight and carried separately in `disabled`,
            //which is what the red/greyed treatment keys off. `disabled` is part of the change
            //test on purpose: entering Buy/Build from a real pause leaves the highlighted button
            //the same, and without it the lock would never be drawn.
            bool disabled = vm.SpeedMultiplier == GameplaySpeed.Disabled;
            var speed = disabled ? GameplaySpeed.Pause : ButtonForMultiplier(vm.SpeedMultiplier);
            if (speed != LastSpeed || disabled != LastSpeedDisabled)
            {

                for (int i = 0; i < 4; i++)
                {
                    bool isCurrent = (i + 1 == speed);
                    var button = SpeedButtons[i];
                    button.Selected = isCurrent;
                    //In Buy/Build the pause button is the one that is "in effect", but it is red
                    //rather than the normal active green: the game is not paused because the player
                    //asked for it, and the red is what says so. The other three are greyed out
                    //because pressing them would do nothing - ChangeSpeedTo refuses while disabled.
                    button.ActiveColor = (disabled && isCurrent) ? Color.Red : UIStyle.Current.BtnActive;
                    button.Disabled = disabled && !isCurrent;
                }
                LastSpeed = speed;
                LastSpeedDisabled = disabled;
            }
            
            RefreshBuildBuyIfChanged();

            var lotControl = Game.LotControl;
            if (lotControl != null && LastCut != lotControl.WallsMode)
            {
                LastCut = lotControl.WallsMode;
                RoofButton.Selected = LastCut == 3;
                WallsUpButton.Selected = LastCut == 2;
                WallsCutButton.Selected = LastCut == 1;
                WallsDownButton.Selected = LastCut == 0;
            }

            var money = GetMoney();
            if (LastMoney != money)
            {
                DisplayChange(money - LastMoney);
                LastMoney = money;
                UpdateMoneyDisplay();
            }

            if (LastZoom != Game.ZoomLevel) UpdateZoomButton();

            //The eyedropper disarms itself the moment it picks something, so mirror the holder's
            //flag onto the button every frame rather than only at the points the player can
            //change it. Only while visible - in Live mode the button is hidden and the tool is
            //not available, so there is nothing to reflect.
            if (EyedropperButton.Visible && lotControl?.ObjectHolder != null)
            {
                EyedropperButton.Selected = lotControl.ObjectHolder.EyedropperMode;
            }

            base.Update(state);

            //KEY SHORTCUTS
            var keys = state.NewKeys;
            //A text input holding keyboard focus (the cheat box, for one) is consuming
            //keystrokes, so the single-key shortcuts below must not also fire. Read it from the
            //input manager rather than assuming nothing has focus.
            var nofocus = state.InputManager.GetFocus() == null;
            if (Game.InLot)
            {
                if (keys.Contains(Keys.F1) && !LiveButton.Disabled) OnModeClick?.Invoke(UIMainPanelMode.LIVE);
                if (keys.Contains(Keys.F2) && !BuyButton.Disabled) OnModeClick?.Invoke(UIMainPanelMode.BUY);
                if (keys.Contains(Keys.F3) && !BuildButton.Disabled) OnModeClick?.Invoke(UIMainPanelMode.BUILD);
                if (keys.Contains(Keys.F4)) OnModeClick?.Invoke(UIMainPanelMode.OPTIONS); // Options Panel

                //E arms the eyedropper. Gated on the button being visible, which is the same
                //Buy/Build-only condition the button itself is shown under.
                if (nofocus && keys.Contains(Keys.E) && EyedropperButton.Visible)
                {
                    ToggleEyedropper(EyedropperButton);
                }

                if (nofocus)
                {
                    var world = Game.vm.Context.World;
                    var cameras = world.State.Cameras;
                    var activeCamera = cameras.ActiveCamera;
                    if (activeCamera.UseRotateHold)
                    {
                        //if the zoom or rotation buttons are down, gradually change their values.
                        var cam = Game.vm.Context.World.State.Cameras.Camera3D;
                        if (RotateCWButton.IsDown || state.KeyboardState.IsKeyDown(Keys.OemPeriod)) cam.RotationX += 2f / FSOEnvironment.RefreshRate;
                        if (RotateCCWButton.IsDown || state.KeyboardState.IsKeyDown(Keys.OemComma)) cam.RotationX -= 2f / FSOEnvironment.RefreshRate;
                    }
                    else
                    {
                        if (keys.Contains(Keys.OemComma)) RotateCounterClockwise(null);
                        if (keys.Contains(Keys.OemPeriod)) RotateClockwise(null);
                    }
                    if (activeCamera.UseZoomHold)
                    {
                        if (lotControl != null)
                        {
                            if (ZoomInButton.IsDown || (state.KeyboardState.IsKeyDown(Keys.OemPlus) && !state.CtrlDown)) lotControl.TargetZoom = Math.Max(0.25f, Math.Min(lotControl.TargetZoom + 1f / FSOEnvironment.RefreshRate, 2));
                            if (ZoomOutButton.IsDown || (state.KeyboardState.IsKeyDown(Keys.OemMinus) && !state.CtrlDown)) lotControl.TargetZoom = Math.Max(0.25f, Math.Min(lotControl.TargetZoom - 1f / FSOEnvironment.RefreshRate, 2));
                        }
                    }
                    else
                    {
                        if (keys.Contains(Keys.OemPlus) && !state.CtrlDown && !ZoomInButton.Disabled) StepZoomLevel(-1);
                        if (keys.Contains(Keys.OemMinus) && !state.CtrlDown && !ZoomOutButton.Disabled) StepZoomLevel(1);
                    }
                    if (keys.Contains(Keys.PageDown)) { if (Game.Level > 1) Game.Level--; }
                    if (keys.Contains(Keys.PageUp)) { if (Game.Level < 5) Game.Level++; }
                    if (keys.Contains(Keys.Home)) UpdateWallsViewKeyHandler(1);
                    if (keys.Contains(Keys.End)) UpdateWallsViewKeyHandler(0);
                }
            }
        }

        private void UpdateWallsViewKeyHandler(int type)
        {
            var lotControl = Game.LotControl;
            if (lotControl == null) return;
            var mode = lotControl.WallsMode;
            switch (type)
            {
                case 0:
                    if (mode > 0) lotControl.WallsMode -= 1;
                    break;
                case 1:
                    if (mode < 3) lotControl.WallsMode += 1;
                    break;
            }
        }

        public void UpdateZoomButton()
        {
            //Game.ZoomLevel is derived from the lot control's target zoom, so this reacts to a
            //mouse wheel or pinch zoom as well as to the buttons - and the buttons grey out at
            //the same two ends of the ladder the wheel can rest on.
            ZoomInButton.Disabled = (!Game.InLot) || (!FSOEnvironment.Enable3D && (Game.ZoomLevel == 1));
            ZoomOutButton.Disabled = (!FSOEnvironment.Enable3D && (Game.ZoomLevel == 3));
            LastZoom = Game.ZoomLevel;
        }

        private void ToggleEyedropper(UIElement btn)
        {
            var lotControl = Game.LotControl;
            if (lotControl?.ObjectHolder == null) return;

            lotControl.ObjectHolder.EyedropperMode = !lotControl.ObjectHolder.EyedropperMode;

            //Arming the tool stands any build tool down. While a custom lot control is active the
            //lot control routes clicks to it rather than to the object holder, so an armed
            //eyedropper behind one would never see the click it is waiting for.
            if (lotControl.ObjectHolder.EyedropperMode) lotControl.ReleaseCustomControl();

            EyedropperButton.Selected = lotControl.ObjectHolder.EyedropperMode;
        }

        public void SetMode(UIMainPanelMode mode)
        {
            LiveButton.Selected = mode == UIMainPanelMode.LIVE;
            BuyButton.Selected = mode == UIMainPanelMode.BUY;
            BuildButton.Selected = mode == UIMainPanelMode.BUILD;
            OptionsButton.Selected = mode == UIMainPanelMode.OPTIONS;

            //The eyedropper only makes sense where a click selects rather than interacts, so it
            //is a Buy/Build control.
            EyedropperButton.Visible = (mode == UIMainPanelMode.BUY || mode == UIMainPanelMode.BUILD);

            //And it does not survive a move to a mode that cannot use it: leaving it armed would
            //mean a Live-mode pie-menu click was swallowed as a pick.
            if (!EyedropperButton.Visible)
            {
                EyedropperButton.Selected = false;
                if (Game.LotControl?.ObjectHolder != null)
                    Game.LotControl.ObjectHolder.EyedropperMode = false;
            }
        }

        public void DisplayChange(int change)
        {
            var newLabel = new UILabel();
            newLabel.Position = MoneyLabel.Position;
            newLabel.Y += -20f;
            newLabel.CaptionStyle = MoneyLabel.CaptionStyle.Clone();
            newLabel.CaptionStyle.Size = 15;
            newLabel.CaptionStyle.Color = (change > 0) ? UIStyle.Current.Text : UIStyle.Current.NegMoney;
            newLabel.Alignment = FSO.Client.UI.Framework.TextAlignment.Right | FSO.Client.UI.Framework.TextAlignment.Middle;
            newLabel.Size = MoneyLabel.Size;

            newLabel.Caption = ((change > 0) ? "+" : "-") + "§" + Math.Abs(change);
            DynamicOverlay.Add(newLabel);

            GameFacade.Screens.Tween.To(newLabel, 1.5f, new Dictionary<string, float>() { { "Y", newLabel.Y-30 }, { "Opacity", 0 } });
            GameThread.SetTimeout(() => { Remove(newLabel); }, 1500);
        }

        private void UpdateMoneyDisplay()
        {
            MoneyLabel.Caption = "§" + LastMoney.ToString("##,#0");
            MoneyLabel.Visible = Game.vm.GetGlobalValue(32) == 0;
        }

        private int GetMoney()
        {
            return BudgetService.GetDisplayBudget();
        }

        public void UpdateBuildBuy()
        {
            var bbEnable = Game.vm.Context.Architecture.BuildBuyEnabled;
            BuyButton.Disabled = !bbEnable;
            BuildButton.Disabled = !bbEnable;
            LiveButton.Disabled = Game.vm.GetGlobalValue(32) != 0;
        }

        public void SetCut(int cut)
        {
            var lotControl = Game.LotControl;
            if (lotControl == null) return;
            lotControl.World.State.DrawRoofs = (cut == 3);
            lotControl.WallsMode = cut;
        }

        /// <summary>
        /// Handles a speed button press: the work is handed to <see cref="TS1GameScreen.ChangeSpeedTo"/>
        /// so there is one place that changes the speed (the number keys are the other caller).
        /// </summary>
        /// <param name="speed">The speed button index, 1..4, where 4 is pause.</param>
        public void SwitchSpeed(int speed)
        {
            Game.ChangeSpeedTo(speed);
        }
    }
}
