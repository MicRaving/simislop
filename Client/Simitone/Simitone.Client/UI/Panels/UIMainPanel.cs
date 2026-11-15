using FSO.Client;
using FSO.Client.UI.Framework;
using FSO.Common.Utils;
using FSO.Content;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.UI.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FSO.Common.Rendering.Framework.Model;
using Simitone.Client.UI.Panels.LiveSubpanels;
using Simitone.Client.UI.Screens;
using FSO.Client.UI.Controls;
using Simitone.Client.UI.Panels.Options;
using Simitone.Client.Utils;

namespace Simitone.Client.UI.Panels
{
    public class UIMainPanel : UIContainer
    {
        private float _CurWidth;
        public float CurWidth
        {
            get
            {
                return _CurWidth;
            }
            set
            {
                _CurWidth = value;
                UpdateWidth();
            }
        }

        private Texture2D Div;
        private Texture2D WhitePx;
        private Rectangle DivRect;
        private UIDiagonalStripe Diag;
        public UISubpanel SubPanel;
        public TS1GameScreen Game;

        public UIStencilButton FloorUpBtn;
        public UIStencilButton FloorDownBtn;
        public UILabel FloorLabel;
        public UILabel FloorLabelShadow;
        public UICategorySwitcher Switcher;
        public UIImage Divider;
        public UIStencilButton HideButton;
        public bool ShowingSelect;

        // -------------------------------------------------------------------------------------
        // Build/buy undo.
        //
        // Two plain caption buttons, in the left block under the floor arrows, shown only in Buy
        // and Build mode. Retail put its Undo/Redo pair on the cpanel for the same reason they are
        // here rather than in the catalogue strip: they act on the lot, not on the strip, and they
        // have to be reachable whatever the strip happens to be showing - a subcategory list, a
        // search, nothing at all.
        //
        // The stack behind them is UIObjectHolder.Undo (see BuildUndoStack); this panel only
        // presents it, drives it from the keyboard, and asks the holder to finish settling a
        // placement. Enabled state is read every frame rather than latched on click, so a
        // placement that is still in flight greys the button out for the frame or two it takes the
        // engine to create the object, and the same press cannot undo an action that has not
        // happened yet.
        // -------------------------------------------------------------------------------------

        /// <summary>Undo button. Null on the desktop panel, which has no floor block to sit in.</summary>
        public UIButton UndoButton;

        /// <summary>Redo button. Null on the desktop panel, as <see cref="UndoButton"/>.</summary>
        public UIButton RedoButton;

        /// <summary>X of the undo/redo pair, relative to the left block.</summary>
        public const int UndoButtonX = 8;

        /// <summary>Y of the undo button, relative to the left block.</summary>
        public const int UndoButtonY = 14;

        /// <summary>Y of the redo button, relative to the left block.</summary>
        public const int RedoButtonY = 62;

        /// <summary>Width of the undo and redo buttons.</summary>
        public const int UndoButtonWidth = 66;

        /// <summary>Height of the undo and redo buttons.</summary>
        public const int UndoButtonHeight = 44;

        public UISwitchAvatarPanel SwitchAvatar;
        public bool PanelActive;
        public UIMainPanelMode Mode;

        // -------------------------------------------------------------------------------------
        // Catalogue search box.
        //
        // It belongs here and not inside the browse panel because a browse panel is destroyed and
        // rebuilt on every category switch, every mode switch and every language change - a box
        // that lived in the panel would lose the half-typed term on each of those, and the caret
        // with it. The main panel outlives all of them, and owns the strip's background, so the box
        // is drawn over the lot view just above the panel and needs a plate of its own to be
        // readable (see Draw).
        //
        // What the term does to the catalogue is the browse panel's business; this side only feeds
        // it and reads one thing back off it - whether a search is meaningful yet, which decides
        // whether the box is live or explaining itself.
        // -------------------------------------------------------------------------------------

        /// <summary>English text of the placeholder shown while the box is empty and a search would work.</summary>
        private const string SearchHintEnglish = "Search selected category\u2026";

        /// <summary>English text of the placeholder shown in the one state where a search would not work yet.</summary>
        private const string SearchHintNoSubcatEnglish = "Select a subcategory first\u2026";

        /// <summary>
        /// The placeholder shown while the box is empty and a search would work: id 80 of
        /// Simitone's own string table, falling back to <see cref="SearchHintEnglish"/>.
        /// </summary>
        /// <remarks>
        /// Resolved once, on first use, rather than per frame. The lookup needs the string tables
        /// to be up - <see cref="SimitoneStrings"/> returns the fallback while they are not - and
        /// <see cref="UpdateCatalogSearch"/> reassigns the caption on every frame of an open
        /// browse panel, so a lookup there would be a dictionary hit and a fresh string 60 times a
        /// second for a caption that cannot change while the language does.
        /// </remarks>
        private static readonly string SearchHint = SimitoneStrings.Get("80", SearchHintEnglish);

        /// <summary>
        /// The placeholder shown in the one state where a search would not work yet: id 81 of
        /// Simitone's own string table, falling back to <see cref="SearchHintNoSubcatEnglish"/>.
        /// </summary>
        /// <remarks>Resolved once, for the same reasons as <see cref="SearchHint"/>.</remarks>
        private static readonly string SearchHintNoSubcat = SimitoneStrings.Get("81", SearchHintNoSubcatEnglish);

        private UITextBox CatalogSearchField;
        private UILabel SearchPlaceholder;

        // The tooltips are strings built from the stack's top entry, so they are rebuilt only when that
        // entry changes (or after a language change, which clears _tooltipsBuilt in SetMode).
        private BuildUndoEntry _undoTooltipEntry;
        private BuildUndoEntry _redoTooltipEntry;
        private bool _tooltipsBuilt;

        /// <summary>Left mouse button state last frame, for the click-outside edge detector.</summary>
        private bool _lastLeftButtonDown;

        public event Action OnEndSelect;
        public event Action<UIMainPanelMode> ModeChanged;

        // Eyedropper hand-off. Picking something in the lot usually means changing category, or
        // even mode, first - and both of those replace this panel's subpanel. These three hold
        // the pick until the new subpanel comes up, which drains them on its first update and
        // then finishes the selection. They are only ever read by the freshly created browse
        // panel, so at most one deferred pick is in flight at a time (a chain of defers creates
        // a chain of new panels, each of which picks the deferred value up on its first frame).
        public uint? PendingEyedropperGUID;
        public ushort? PendingEyedropperPatternID;
        public ArchitectureType? PendingEyedropperArchType;

        // Ids 400 to 404 of Simitone's own string table, and the one array
        // UIDesktopUCP.FloorNames *is*: the desktop clock and this panel show the same floor side
        // by side, so a translation that answered one and not the other would put two different
        // words for the same number on screen at once. That is enforced by sharing the array
        // rather than by re-deriving it from the same ids; readonly because a shared array a
        // caller can splice is a shared array that can drift.
        public static readonly string[] FloorNames = new string[]
        {
            SimitoneStrings.Get("400", "1st"),
            SimitoneStrings.Get("401", "2nd"),
            SimitoneStrings.Get("402", "3rd"),
            SimitoneStrings.Get("403", "4th"),
            SimitoneStrings.Get("404", "5th")
        };
        public int LastFloor = -1;

        /// <summary>
        /// The live-mode strip's categories, and the order they appear in.
        /// </summary>
        /// <remarks>
        /// Static and readonly rather than per-panel, because it is a table rather than state:
        /// nothing in it is ever mutated, the switcher only enumerates it, and a category added
        /// to it has to be findable by a test that cannot build a <c>UIMainPanel</c> without a
        /// content pipeline and a graphics device. A category added to this list without a case
        /// in <see cref="Switcher_OnCategorySelect"/> renders as a permanently empty strip, and
        /// that is exactly the kind of gap a test can see and a play session cannot.
        /// </remarks>
        public static readonly List<UICategory> LiveCategories = new List<UICategory>()
        {
            new UICategory() { ID = LiveCategoryMotives, IconName = "live_motives.png" },
            new UICategory() { ID = LiveCategoryJob, IconName = "live_job.png" },
            new UICategory() { ID = LiveCategoryPersonality, IconName = "live_personality.png" },
            new UICategory() { ID = LiveCategoryRelationships, IconName = "live_relationships.png" },
            new UICategory() { ID = LiveCategoryInventory, IconName = "live_inventory.png" },
            new UICategory() { ID = LiveCategoryHouse, IconName = "live_house.png" },
            new UICategory() { ID = LiveCategoryFame, IconName = "live_fame.png" },
            new UICategory() { ID = LiveCategoryInterests, IconName = "live_interests.png" }
        };

        /// <summary>
        /// The live-mode category ids, as the switcher hands them to
        /// <see cref="Switcher_OnCategorySelect"/>. Named rather than written as bare numbers
        /// there, because this list has grown past the point where "case 5" says anything, and a
        /// category added to the switcher without a case here renders as an empty strip.
        /// </summary>
        public const int LiveCategoryMotives = 0,
            LiveCategoryJob = 1,
            LiveCategoryPersonality = 2,
            LiveCategoryRelationships = 3,
            LiveCategoryInventory = 4,
            LiveCategoryHouse = 5,
            LiveCategoryFame = 6,
            LiveCategoryInterests = 7;


        private List<UICategory> BuyCategories = new List<UICategory>()
        {
            new UICategory() { ID = 0, IconName = "cat_seat.png" },
            new UICategory() { ID = 1, IconName = "cat_surf.png" },
            new UICategory() { ID = 2, IconName = "cat_appl.png" },
            new UICategory() { ID = 3, IconName = "cat_elec.png" },
            new UICategory() { ID = 4, IconName = "cat_plum.png" },
            new UICategory() { ID = 5, IconName = "cat_deco.png" },
            new UICategory() { ID = 6, IconName = "cat_misc.png" },
            new UICategory() { ID = 7, IconName = "cat_ligt.png" },
        };

        private List<UICategory> BuildCategories = new List<UICategory>()
        {
            new UICategory() { ID = 0, IconName = "cat_build_arch.png" },
            new UICategory() { ID = 1, IconName = "cat_build_outs.png" },
            new UICategory() { ID = 2, IconName = "cat_build_objs.png" },
        };

        private List<UICategory> OptionsCategories = new List<UICategory>()
        {
            new UICategory() { ID = 0, IconName = "cat_build_arch.png" },
        };

        /// <summary>
        /// Where the options-strip subpanel sits inside the main panel (see <see cref="SetSubpanel"/>).
        /// </summary>
        public const int OptionsStripLeft = 263;

        public UIMainPanel(TS1GameScreen game) : base()
        {
            Game = game;
            Diag = new UIDiagonalStripe(new Point(0, 128), UIDiagonalStripeSide.RIGHT, UIStyle.Current.Bg);
            Add(Diag);
            WhitePx = TextureGenerator.GetPxWhite(GameFacade.GraphicsDevice);
            var ui = Content.Get().CustomUI;
            Div = ui.Get("panel_div.png").Get(GameFacade.GraphicsDevice);

            if (!Game.Desktop)
            {
                FloorUpBtn = new UIStencilButton(ui.Get("level_up.png").Get(GameFacade.GraphicsDevice));
                FloorUpBtn.Position = new Vector2(80, 10);
                FloorUpBtn.OnButtonClick += (b) => { if (Game.Level < 5) Game.Level++; };
                Add(FloorUpBtn);

                FloorDownBtn = new UIStencilButton(ui.Get("level_down.png").Get(GameFacade.GraphicsDevice));
                FloorDownBtn.Position = new Vector2(80, 68);
                FloorDownBtn.OnButtonClick += (b) => { if (Game.Level > 1) Game.Level--; };
                Add(FloorDownBtn);

                FloorLabel = new UILabel();
                FloorLabel.CaptionStyle = FloorLabel.CaptionStyle.Clone();
                FloorLabel.CaptionStyle.Size = 15;
                FloorLabel.CaptionStyle.Color = UIStyle.Current.Text;
                FloorLabel.Alignment = TextAlignment.Middle | TextAlignment.Center;
                FloorLabel.Position = new Vector2(80, 64);
                FloorLabel.Size = new Vector2(51, 18);

                FloorLabelShadow = new UILabel();
                FloorLabelShadow.CaptionStyle = FloorLabel.CaptionStyle.Clone();
                FloorLabelShadow.Alignment = TextAlignment.Middle | TextAlignment.Center;
                FloorLabelShadow.Position = new Vector2(83, 67);
                FloorLabelShadow.Size = new Vector2(51, 18);
                FloorLabelShadow.CaptionStyle.Color = Color.Black * 0.5f;
                Add(FloorLabelShadow);
                Add(FloorLabel);

                Divider = new UIImage(ui.Get("divider.png").Get(GameFacade.GraphicsDevice));
                Divider.Position = new Vector2(146, 29);
                Add(Divider);

                // Undo / Redo. Captioned rather than illustrated on purpose: the retail artwork for
                // these two lives in UIGraphics.far as cpanel/Buttons/Undo.BMP and Redo.bmp, which
                // this tree does not read, and a button whose art is missing is worse than a
                // button with a word on it. The words are Simitone's own strings, so they can be
                // translated.
                UndoButton = MakeUndoRedoButton(BuildUndoPolicy.UndoCaption, UndoButtonY, (b) => UndoBuildAction());
                Add(UndoButton);

                RedoButton = MakeUndoRedoButton(BuildUndoPolicy.RedoCaption, RedoButtonY, (b) => RedoBuildAction());
                Add(RedoButton);
            }

            HideButton = new UIStencilButton(ui.Get("panel_hide.png").Get(GameFacade.GraphicsDevice));
            HideButton.X = Game.ScreenWidth - (50 + 64 + 15);
            HideButton.Y = 26;
            HideButton.OnButtonClick += (b) => { Close(); };
            Add(HideButton);

            Switcher = new UICategorySwitcher();
            Switcher.Position = new Vector2(164 - (Game.Desktop ? 16 : 0), 0);
            Switcher.InitCategories(LiveCategories);
            Switcher.OnCategorySelect += Switcher_OnCategorySelect;
            Switcher.OnOpen += Switcher_OnOpen;
            Add(Switcher);

            //Above the panel rather than inside it: the strip is 128px of tiles with no room of
            //its own, and the lot view behind the panel is the only uncluttered space there is.
            CatalogSearchField = new UITextBox();
            CatalogSearchField.SetSize(230, 32);
            CatalogSearchField.Position = new Vector2(263, -32);
            CatalogSearchField.TextMargin = new Rectangle(8, 8, 8, 8);
            CatalogSearchField.Visible = false;
            CatalogSearchField.OnChange += (elem) =>
            {
                (SubPanel as UIBuyBrowsePanel)?.ApplyNameFilter(CatalogSearchField.CurrentText);
            };
            Add(CatalogSearchField);

            //Drawn after the field, so it sits on top of the empty field's text position; it is
            //hidden the moment there is a term to show instead.
            SearchPlaceholder = new UILabel();
            SearchPlaceholder.Caption = SearchHint;
            SearchPlaceholder.Position = new Vector2(271, -24);
            SearchPlaceholder.Size = new Vector2(214, 20);
            SearchPlaceholder.CaptionStyle = SearchPlaceholder.CaptionStyle.Clone();
            SearchPlaceholder.CaptionStyle.Size = 12;
            SearchPlaceholder.CaptionStyle.Color = UIStyle.Current.Text * 0.4f;
            SearchPlaceholder.Visible = false;
            Add(SearchPlaceholder);

            foreach (var fade in GetFadeables())
            {
                fade.Opacity = 0;
            }

            Game.LotControl.QueryPanel.Position = new Vector2(53 + (Game.Desktop ? 25 : 0), -5);
            Add(Game.LotControl.QueryPanel);
            Game.LotControl.PickupPanel.Opacity = 0;
            Add(Game.LotControl.PickupPanel);

            CurWidth = 0;
        }

        /// <summary>
        /// Builds one of the undo/redo pair. Both are identical apart from their caption, their row
        /// and what they do, so this is where the one shape they share is written down.
        /// </summary>
        private static UIButton MakeUndoRedoButton(string caption, float y, ButtonClickDelegate onClick)
        {
            var button = new UIButton
            {
                Caption = caption,
                Position = new Vector2(UndoButtonX, y),
                Size = new Vector2(UndoButtonWidth, UndoButtonHeight)
            };
            // A cloned style with an explicit size rather than TextStyle.DefaultButton: the pair sit
            // in a 66px column, and a caption at the default button size is legible in a 50px one
            // only by luck of the word's length - which is a different word in every language.
            button.CaptionStyle = button.CaptionStyle.Clone();
            button.CaptionStyle.Size = 12;
            button.CaptionStyle.Color = UIStyle.Current.BtnTxt;
            button.CaptionStyle.SelectedColor = UIStyle.Current.BtnTxt;
            button.OnButtonClick += onClick;
            return button;
        }

        private void Switcher_OnOpen()
        {
            var panel = SubPanel as UIBuyBrowsePanel;
            if (panel != null)
            {
                panel.Reset();
                //Reset rebuilds the category list, which ends the search on that side. The box has
                //to stop claiming a term is narrowing something too, or Escape would be spent
                //clearing text that filters nothing and the strip would look unresponsive to it.
                ClearCatalogSearch();
            }
        }

        /// <summary>
        /// Empties the search box. Deliberately does not touch the browse panel: the two callers
        /// are the ones that have already resolved the panel's state (a rebuild, or a panel swap),
        /// and pushing the empty term back at the wrong moment would restore a list the player has
        /// already moved on from.
        /// </summary>
        private void ClearCatalogSearch()
        {
            if (CatalogSearchField == null) return;
            CatalogSearchField.CurrentText = "";
        }

        /// <summary>
        /// Switches the live-mode panel to <paramref name="mode"/>.
        /// </summary>
        /// <param name="mode">The mode to enter.</param>
        /// <param name="force">Re-enter the mode even when it is already the current one.
        /// Used when something outside the mode itself changed - currently only a language
        /// change - so the panel is rebuilt against the new strings instead of being left
        /// stale.</param>
        public void SetMode(UIMainPanelMode mode, bool force = false)
        {
            if (!force)
                if (mode == Mode) return;
            Mode = mode;
            _tooltipsBuilt = false; // a forced SetMode is the language-change rebuild: re-read the strings

            Game.LotControl.World.State.BuildMode = 0;
            switch (mode)
            {
                case UIMainPanelMode.LIVE:
                    Switcher.InitCategories(LiveCategories);
                    break;
                case UIMainPanelMode.BUY:
                    Switcher.InitCategories(BuyCategories);
                    Game.LotControl.World.State.BuildMode = 1;
                    break;
                case UIMainPanelMode.BUILD:
                    Switcher.InitCategories(BuildCategories);
                    Game.LotControl.World.State.BuildMode = 2;
                    break;
                case UIMainPanelMode.OPTIONS:
                    Switcher.InitCategories(OptionsCategories);
                    break;
            }

            var live = (mode == UIMainPanelMode.LIVE);
            Game.LotControl.LiveMode = live;
            HideButton.Visible = live;

            ModeChanged?.Invoke(mode);
        }

        public void Switcher_OnCategorySelect(int obj)
        {
            UISubpanel panel = null;
            switch (Mode)
            {
                case UIMainPanelMode.LIVE:
                    switch (obj)
                    {
                        case LiveCategoryMotives:
                            panel = new UIMotiveSubpanel(Game); break;
                        case LiveCategoryJob:
                            panel = new UIJobSubpanel(Game); break;
                        case LiveCategoryPersonality:
                            panel = new UIPersonalitySubpanel(Game); break;
                        case LiveCategoryRelationships:
                            panel = new UIRelationshipSubpanel(Game); break;
                        case LiveCategoryInventory:
                            panel = new UIInventorySubpanel(Game); break;
                        case LiveCategoryHouse:
                            panel = new UIHouseSubpanel(Game); break;
                        case LiveCategoryFame:
                            panel = new UIFameSubpanel(Game); break;
                        case LiveCategoryInterests:
                            panel = new UIInterestsSubpanel(Game); break;
                    }
                    break;
                case UIMainPanelMode.BUY:
                    panel = new UIBuyBrowsePanel(Game, (sbyte)obj, GetLotType(false));
                    break;
                case UIMainPanelMode.BUILD:
                    panel = new UIBuyBrowsePanel(Game, (sbyte)obj, UICatalogMode.Build);
                    break;
                case UIMainPanelMode.OPTIONS:
                    panel = new UIButtonSubpanel(Game, new UICatFunc[] {
                        new UICatFunc(GameFacade.Strings.GetString("145", "3"), "opt_save.png", () => { Game.Save(); }),
                        new UICatFunc(GameFacade.Strings.GetString("145", "1"), "opt_neigh.png", () => { Game.ReturnToNeighbourhood(); }),
                        // Id 20 of Simitone's own string table (the settings-strip block). It
                        // carries the English it replaced, so a build with no f900 table shows
                        // what it always showed.
                        new UICatFunc(SimitoneStrings.Get("20", "Settings"), "opt_setting.png", () => { Game.ShowOptionsDialog(); }),
                        new UICatFunc(GameFacade.Strings.GetString("145", "5"), "opt_quit.png", () => { Game.CloseAttempt(); }),
                    });
                    break;
            }
            SetSubpanel(panel);
        }

        public UICatalogMode GetLotType(bool music)
        {
            var house = Game.vm.GetGlobalValue(10);
            var zones = Content.Get().Neighborhood.ZoningDictionary;
            short result = 1;
            zones.TryGetValue(house, out result);
            var community = result == 1;
            var simlessBuildMode = Game.vm.GetGlobalValue(32) > 0;

            return ResolveLotType(house, community, music, simlessBuildMode);
        }

        /// <summary>
        /// Pure resolution of the buy-catalog lot type from the values the caller already read out
        /// of the VM. Split out of <see cref="GetLotType"/> so the house-number ranges (which have
        /// a historically broken overlap between the studio and magic-town ranges) are testable
        /// without a graphics device or a live VM.
        /// </summary>
        /// <param name="houseNumber">VM global 10 — the house/lot number.</param>
        /// <param name="isCommunity">Whether the lot is zoned community (zoning dictionary value 1).</param>
        /// <param name="music">True when the caller wants the music/magic-town interpretation of the lot.</param>
        /// <param name="simlessBuildMode">VM global 32 &gt; 0 — build mode on a lot with no resident sims.</param>
        public static UICatalogMode ResolveLotType(int houseNumber, bool isCommunity, bool music, bool simlessBuildMode)
        {
            if (houseNumber >= 21 && houseNumber <= 31) return UICatalogMode.Downtown;
            if (houseNumber >= 40 && houseNumber <= 49) return UICatalogMode.Vacation;
            if (houseNumber >= 81 && houseNumber <= 89) return UICatalogMode.Studiotown;
            if (houseNumber >= 90 && houseNumber <= 99 && (music || isCommunity)) return UICatalogMode.Magictown;
            if (isCommunity) return UICatalogMode.Community;
            return (music && simlessBuildMode) ? UICatalogMode.Downtown : UICatalogMode.Normal;
        }

        public void SetSubpanel(UISubpanel sub)
        {
            if (SubPanel != null)
            {
                //Clear the search on the way out. The panel holding the filtered list is about to be
                //killed, and leaving the term set would mean the next panel - built for a different
                //category - inherited a filter over rows it has not built yet.
                (SubPanel as UIBuyBrowsePanel)?.ApplyNameFilter("");
                SubPanel.Kill();
            }
            SubPanel = sub;

            //The box is the catalogue's, so it is only up while a browse panel is. Assigning
            //CurrentText does not raise OnChange, so the incoming panel starts unfiltered: a
            //search is scoped to the list it was typed against.
            var isBrowsePanel = sub is UIBuyBrowsePanel;
            ClearCatalogSearch();
            CatalogSearchField.Visible = isBrowsePanel;
            SearchPlaceholder.Visible = isBrowsePanel;

            if (sub != null)
            {
                SubPanel.Position = new Vector2(OptionsStripLeft, 0);
                Add(SubPanel);
            }
        }

        public void SetSubpanelPickup(float opacity)
        {
            //used to hide subpanels to make way for the PickupPanel
            if (SubPanel != null)
            {
                GameFacade.Screens.Tween.To(SubPanel, 0.3f, new Dictionary<string, float>() { { "Opacity", opacity } }, TweenQuad.EaseOut);
                // The tween leaves the subpanel at full opacity but still drawing once it is
                // out of the way, so a faded-out subpanel keeps eating clicks meant for the
                // lot behind it. Visibility is flipped at the ends of the tween to match.
                SubPanel.Visible = opacity == 0 ? false : true;
            }
            GameFacade.Screens.Tween.To(Switcher.MainButton, 0.3f, new Dictionary<string, float>() { { "Opacity", opacity } }, TweenQuad.EaseOut);
            GameFacade.Screens.Tween.To(Game.LotControl.PickupPanel, 0.3f, new Dictionary<string, float>() { { "Opacity", 1-opacity } }, TweenQuad.EaseOut);
            if (opacity == 0)
            {
                Switcher.Close();
            }
        }

        private void UpdateWidth()
        {
            //prepanel width is 167
            //div width is 52

            var iWidth = (int)CurWidth;
            if (iWidth < 211)
            {
                Diag.X = 0;
                Diag.BodySize = new Point(iWidth, 128);
                DivRect = new Rectangle();
            } else if (iWidth < 211+52)
            {
                Diag.X = iWidth;
                Diag.BodySize = new Point(0, 128);
                DivRect = new Rectangle(0, 0, iWidth - 211, 128);
            } else
            {
                Diag.X = 211 + 52;
                Diag.BodySize = new Point(iWidth - (211 + 52), 128);
                DivRect = new Rectangle(0, 0, 52, 128);
            }
        }

        public UIElement[] GetFadeables()
        {
            // Undo/Redo ride with the floor arrows because they live in the same left-hand block
            // and have to arrive with them; a button that fades in a fifth of a second after the
            // panel around it looks like it was left behind. They are only in the non-desktop
            // branch, which is also the only one that builds them.
            if (Game.Desktop)
            {
                return new UIElement[]
                {
                Switcher.MainButton,
                HideButton
                };
            }
            else
            {
                return new UIElement[]
                {
                FloorUpBtn,
                FloorDownBtn,
                FloorLabel,
                FloorLabelShadow,
                Switcher.MainButton,
                Divider,
                HideButton,
                UndoButton,
                RedoButton
                };
            }
        }

        public override void Draw(UISpriteBatch batch)
        {
            if (CatalogSearchField.Visible)
            {
                //Drawn first, and behind the box (the box is a child, so it draws after this), with
                //a couple of pixels of bleed: the field's own sliced background is the game's, and
                //over an arbitrary piece of lot it is not guaranteed to be opaque.
                DrawLocalTexture(batch, WhitePx, null,
                    new Vector2(CatalogSearchField.X - 2, CatalogSearchField.Y - 2),
                    new Vector2(CatalogSearchField.Size.X + 4, CatalogSearchField.Size.Y + 4),
                    UIStyle.Current.TitleBg);
            }

            if (CurWidth > 211)
            {
                if (ShowingSelect)
                {
                    DrawLocalTexture(batch, WhitePx, null, new Vector2(0, 0), new Vector2(211+52, 128), UIStyle.Current.Bg);
                }
                else
                {
                    DrawLocalTexture(batch, WhitePx, null, new Vector2(0, 0), new Vector2(211, 128), UIStyle.Current.Bg);
                    DrawLocalTexture(batch, Div, DivRect, new Vector2(211, 0), Vector2.One, UIStyle.Current.Bg);
                }
            }
            base.Draw(batch);

        }

        /// <summary>
        /// The language the panel's strings were last built with, so a language change can be
        /// spotted once and the panel rebuilt - rather than every frame.
        /// </summary>
        private byte? _currentLangCode = null;

        public override void Update(UpdateState state)
        {
            base.Update(state);
            Visible = _CurWidth > 0;

            if (!_currentLangCode.HasValue && GameFacade.Game is SimitoneGame startupGame)
                _currentLangCode = (byte)startupGame.CurrentLanguage;
            if (!Game.Desktop)
            {
                if (Game.Level != LastFloor)
                {
                    LastFloor = Game.Level;
                    FloorLabel.Caption = FloorNames[LastFloor - 1];
                    FloorLabelShadow.Caption = FloorNames[LastFloor - 1];
                    FloorDownBtn.Disabled = LastFloor == 1;
                    FloorUpBtn.Disabled = LastFloor == 5;
                }
            }
            else
            { // desktop
                //language update
                if (GameFacade.Game is SimitoneGame simGame && _currentLangCode != (byte)simGame.CurrentLanguage)
                { // language changed
                    SetMode(Mode, true); // reload the panel
                    // SetMode only re-lays the mode switcher out. The visible subpanel is a
                    // separate object whose captions were read out of the string tables when
                    // it was built, so it has to be rebuilt too for the new language to
                    // actually reach the screen.
                    Switcher_OnCategorySelect(Switcher.ActiveCategory);
                    _currentLangCode = (byte)simGame.CurrentLanguage;
                }
            }

            Game.LotControl.PickupPanel.Visible = Game.LotControl.PickupPanel.Opacity > 0;

            if (Mode != UIMainPanelMode.LIVE)
            { // when in BUILD or BUY, gameplay cannot proceed. Set multiplier to GameplaySpeed.Disabled to ensure this cannot be bypassed by changing speed using speed buttons.
                Game.LockSimSpeed(); // make it so play cannot continue
            }
            else if (Game.vm != null && Game.vm.SpeedMultiplier == GameplaySpeed.Disabled)
            { // pop prior speed
                Game.UnlockSimSpeed();
            }

            UpdateBuildUndo(state);

            UpdateCatalogSearch(state);
        }

        // -------------------------------------------------------------------------------------
        // Build/buy undo: the buttons' state, and the two keys.
        // -------------------------------------------------------------------------------------

        /// <summary>
        /// Asks the object holder to finish any placement the engine has caught up with.
        /// </summary>
        /// <remarks>
        /// The holder ticks itself from <c>UILotControl</c>, but a held build tool takes over that
        /// update entirely, so a placement made just before the player picked up the wall tool
        /// would otherwise never settle and the undo button would stay greyed out for as long as
        /// the tool was held. Calling it from here as well costs one dictionary lookup a frame
        /// when there is nothing pending, and the holder's own call makes the second one a no-op.
        /// </remarks>
        private void UpdateBuildUndo(UpdateState state)
        {
            var holder = Game?.LotControl?.ObjectHolder;
            if (holder != null) holder.TickUndo();

            var inBuildBuy = (Mode == UIMainPanelMode.BUY || Mode == UIMainPanelMode.BUILD);
            if (UndoButton != null)
            {
                UndoButton.Visible = inBuildBuy;
                UndoButton.Disabled = !holder?.Undo.CanUndo ?? true;
                var undoTop = holder?.Undo.PeekUndo;
                if (!_tooltipsBuilt || !ReferenceEquals(undoTop, _undoTooltipEntry))
                {
                    _undoTooltipEntry = undoTop;
                    UndoButton.Tooltip = BuildUndoPolicy.UndoTooltip(undoTop);
                }
            }
            if (RedoButton != null)
            {
                RedoButton.Visible = inBuildBuy;
                RedoButton.Disabled = !holder?.Undo.CanRedo ?? true;
                var redoTop = holder?.Undo.PeekRedo;
                if (!_tooltipsBuilt || !ReferenceEquals(redoTop, _redoTooltipEntry))
                {
                    _redoTooltipEntry = redoTop;
                    RedoButton.Tooltip = BuildUndoPolicy.RedoTooltip(redoTop);
                }
            }
            _tooltipsBuilt = true;

            if (!inBuildBuy) return;

            // A text input holding keyboard focus (the catalogue search box, the cheat console) is
            // consuming keystrokes, and Ctrl+Z inside a text box has to mean "undo my typing". Read
            // it from the input manager rather than assuming nothing has focus - the same rule
            // UIDesktopUCP applies to its single-key shortcuts.
            if (state.InputManager == null || state.InputManager.GetFocus() != null) return;
            if (!state.CtrlDown) return;

            if (state.NewKeys.Contains(Keys.Z)) UndoBuildAction();
            else if (state.NewKeys.Contains(Keys.Y)) RedoBuildAction();
        }

        /// <summary>
        /// Undoes the last buy/build object action. The button and Ctrl+Z both come here.
        /// </summary>
        /// <returns>True when a net command was sent.</returns>
        public bool UndoBuildAction()
        {
            var holder = Game?.LotControl?.ObjectHolder;
            if (holder == null) return false;

            // The object holder puts down anything half-placed and raises OnUndoRedo, which is what
            // makes the catalogue panel drop its own selection; nothing to add here.
            return holder.UndoLast();
        }

        /// <summary>
        /// Repeats the last undone buy/build object action. The button and Ctrl+Y both come here.
        /// </summary>
        /// <returns>True when a net command was sent.</returns>
        public bool RedoBuildAction()
        {
            var holder = Game?.LotControl?.ObjectHolder;
            if (holder == null) return false;

            return holder.RedoLast();
        }

        /// <summary>
        /// Drives the catalogue search box for one frame: whether it is live, what its placeholder
        /// says, and the two keyboard/mouse gestures that end a search.
        /// </summary>
        private void UpdateCatalogSearch(UpdateState state)
        {
            var leftDown = state.MouseState.LeftButton == ButtonState.Pressed;

            if (CatalogSearchField.Visible)
            {
                var browsePanel = SubPanel as UIBuyBrowsePanel;

                //Build mode has no list of the right things to search until a subcategory is open,
                //and the subcategory buttons are covering the strip anyway. Rather than let the box
                //look like it is ignoring the player, it says what it is waiting for and refuses
                //input.
                if (browsePanel != null && !BuyCatalogService.SearchApplies(browsePanel.Mode, browsePanel.ChoosingSub))
                {
                    SearchPlaceholder.Caption = SearchHintNoSubcat;
                    CatalogSearchField.Mode = UITextEditMode.ReadOnly;
                    CatalogSearchField.Opacity = 0.45f;
                    SearchPlaceholder.Visible = true;
                }
                else
                {
                    SearchPlaceholder.Caption = SearchHint;
                    CatalogSearchField.Mode = UITextEditMode.Editor;
                    CatalogSearchField.Opacity = 1f;
                    //HasText, not CurrentText.Length: a box holding only a space reads as empty, and
                    //MatchesSearch agrees, so the hint and the filter must agree too.
                    SearchPlaceholder.Visible = !CatalogSearchField.HasText;
                }

                var searchFocused = state.InputManager.GetFocus() == CatalogSearchField;

                //A click outside the box drops its focus. Without this the caret stays in a box the
                //player has walked away from, and every single-key shortcut stays gated off by
                //UIDesktopUCP's "something has focus" check for as long as it is left there.
                if (searchFocused && leftDown && !_lastLeftButtonDown && !MouseOverSearchBox(state))
                {
                    state.InputManager.SetFocus(null);
                }

                //Escape clears the term and gives the focus back. It deliberately does not close the
                //panel or drop the subcategory choice: the one thing a search must never do is trap
                //the player in it, and clearing the text is the whole of the way out.
                if (searchFocused && state.NewKeys.Contains(Keys.Escape) && CatalogSearchField.HasText)
                {
                    ClearCatalogSearch();
                    browsePanel?.ApplyNameFilter("");
                    state.InputManager.SetFocus(null);
                }
            }

            //Tracked whether or not the box is up, so that a box appearing while the button is
            //already down does not read that press as the start of a click on it.
            _lastLeftButtonDown = leftDown;
        }

        private bool MouseOverSearchBox(UpdateState state)
        {
            var localMouse = GlobalPoint(new Vector2(state.MouseState.X, state.MouseState.Y));
            var bounds = new Rectangle(
                (int)CatalogSearchField.X, (int)CatalogSearchField.Y,
                (int)CatalogSearchField.Size.X, (int)CatalogSearchField.Size.Y);

            return bounds.Contains((int)localMouse.X, (int)localMouse.Y);
        }

        public void Open()
        {
            Visible = true;
            GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "CurWidth", GameFacade.Screens.CurrentUIScreen.ScreenWidth-X} }, TweenQuad.EaseOut);
            foreach (var fade in GetFadeables())
            {
                GameFacade.Screens.Tween.To(fade, 0.3f, new Dictionary<string, float>() { { "Opacity", 1f } });
            }
            PanelActive = true;
        }

        public void Close()
        {
            GameFacade.Screens.Tween.To(this, 0.5f, new Dictionary<string, float>() { { "CurWidth", 0 } }, TweenQuad.EaseOut);
            SetSubpanel(null);
            foreach (var fade in GetFadeables())
            {
                GameFacade.Screens.Tween.To(fade, 0.3f, new Dictionary<string, float>() { { "Opacity", 0f } });
            }
            if (Switcher.CategoryExpand > 0) Switcher.Close();

            SwitchAvatar?.Kill();
            SwitchAvatar = null;
            PanelActive = false;
        }

        public void ShowSelect()
        {
            var add = new UISwitchAvatarPanel(Game);
            Add(add);

            SetSubpanel(null);
            foreach (var fade in GetFadeables())
            {
                GameFacade.Screens.Tween.To(fade, 0.3f, new Dictionary<string, float>() { { "Opacity", 0f } });
            }
            if (Switcher.CategoryExpand > 0) Switcher.Close();
            ShowingSelect = true;
            SwitchAvatar = add;

            add.OnEnd += () =>
            {
                Open();
                Switcher_OnCategorySelect(Switcher.ActiveCategory);
                SwitchAvatar = null;
                ShowingSelect = false;
                OnEndSelect?.Invoke();
            };
        }

        public override void GameResized()
        {
            base.GameResized();
            if (PanelActive) CurWidth = Game.ScreenWidth - X;
            HideButton.X = Game.ScreenWidth - (50 + X);
        }
    }

    public enum UIMainPanelMode
    {
        LIVE,
        BUY,
        BUILD,
        OPTIONS
    }
}
