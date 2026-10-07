using FSO.Client.UI.Controls.Catalog;
using FSO.Content;
using FSO.Content.Interfaces;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Screens;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using FSO.Common.Rendering.Framework.Model;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Controls;
using FSO.Client;
using Simitone.Client.UI.Model;
using FSO.Client.UI.Panels.LotControls;
using Simitone.Client.UI.Services;
using FSO.Client.UI.Model;
using FSO.LotView.Model;

namespace Simitone.Client.UI.Panels.LiveSubpanels
{
    public class UIBuyBrowsePanel : UISubpanel
    {
        public UITouchScroll CatContainer;
        public List<UICatalogElement> FullCategory;

        /// <summary>
        /// The rows the strip is currently showing: the subcategory's slice of
        /// <see cref="FullCategory"/>, or the search's slice of that.
        ///
        /// <para>
        /// Deliberately a <see cref="IList{T}"/> and not an <see cref="IEnumerable{T}"/>. The
        /// scroll container asks for a row count every frame (to size the scrollbar) and asks for
        /// a row's element by index for each row it draws, and an <c>IEnumerable</c> answering
        /// through <c>Count()</c> and <c>ElementAt()</c> re-evaluates the whole predicate chain
        /// every single time. Against a category carrying a few thousand custom items that is
        /// thousands of comparisons per frame for a number the panel already knows, and another
        /// full sweep for each visible row. Every assignment below therefore materialises.
        /// </para>
        /// </summary>
        public IList<UICatalogElement> FilterCategory;
        public UICatalogMode Mode;
        public List<UICatButton> SelButtons = new List<UICatButton>();
        public List<UILabel> SelLabels = new List<UILabel>();
        public sbyte Category;
        public bool ChoosingSub;
        public int ItemID = -1;

        // -------------------------------------------------------------------------------------
        // Search box state.
        //
        // The box itself is drawn by UIMainPanel (it has to outlive this panel - switching category
        // builds a new one - and it is the main panel that owns the strip's background). What lives
        // here is the consequence of what is typed: which rows the strip shows.
        //
        // PreSearchFilterCategory is the *other* half of the save/restore and the reason the field
        // only saves once. Typing rebuilds FilterCategory from FullCategory, not from the previous
        // FilterCategory, so a second keystroke would otherwise narrow the already-narrowed list
        // and deleting a character would not bring a row back. One snapshot of "what the strip was
        // showing before the player touched the box" per search session is what makes the box
        // behave like a filter rather than an accumulator.
        // -------------------------------------------------------------------------------------

        private string ActiveSearchTerm;
        private IList<UICatalogElement> PreSearchFilterCategory;

        /// <summary>Shown over the strip when a search matches nothing.</summary>
        public UILabel NoResultsLabel;

        // ---------------------------------------------------------------------------------------
        // Catalogue data. These live in BuyCatalogService (UI/Services) and are forwarded here
        // under the names they have always had, because the shape of these tables is what the
        // catalogue tests assert and because a row count that stops agreeing with its icon table
        // is a hard crash the first time this panel is opened. The tables themselves are public
        // and mutable, exactly as they were; nothing mutates them after initialisation.
        // ---------------------------------------------------------------------------------------

        /// <inheritdoc cref="BuyCatalogService.RemapString"/>
        public static int[] RemapString { get { return BuyCatalogService.RemapString; } }

        /// <inheritdoc cref="BuyCatalogService.Categories"/>
        public static List<List<UICatalogSubcat>> Categories { get { return BuyCatalogService.Categories; } }

        /// <inheritdoc cref="BuyCatalogService.CatIcons"/>
        public static string[][] CatIcons { get { return BuyCatalogService.CatIcons; } }

        /// <inheritdoc cref="BuyCatalogService.BuildCategories"/>
        public static List<List<UICatalogSubcat>> BuildCategories { get { return BuyCatalogService.BuildCategories; } }

        /// <inheritdoc cref="BuyCatalogService.BuildIcons"/>
        public static string[][] BuildIcons { get { return BuyCatalogService.BuildIcons; } }

        /// <inheritdoc cref="BuyCatalogService.DTCategories"/>
        public static Dictionary<UICatalogMode, List<UICatalogSubcat>> DTCategories { get { return BuyCatalogService.DTCategories; } }

        /// <inheritdoc cref="BuyCatalogService.DTIcons"/>
        public static Dictionary<UICatalogMode, List<string>> DTIcons { get { return BuyCatalogService.DTIcons; } }

        /// <inheritdoc cref="BuyCatalogService.WallStyleIDs"/>
        public static short[] WallStyleIDs { get { return BuyCatalogService.WallStyleIDs; } }

        /// <inheritdoc cref="BuyCatalogService.WallStylePatterns"/>
        public static short[] WallStylePatterns { get { return BuyCatalogService.WallStylePatterns; } }

        public bool HoldingEvents;

        // -------------------------------------------------------------------------------------
        // Eyedropper support.
        //
        // A pick in the lot rarely belongs to the panel that is currently open: it can be a
        // different Buy category, a different Build category, or a different mode entirely. All
        // three of those replace this panel, so a pick that needs one of them is parked on
        // <see cref="UIMainPanel"/> and drained by whichever browse panel the switch lands on.
        //
        // This flag makes the drain one-shot per panel instance. That is what bounds the
        // redirect chain: each hop builds a new panel, each new panel drains once, and a panel
        // that finds itself still in the wrong category parks the pick and hops again rather than
        // looping on its own state.
        // -------------------------------------------------------------------------------------

        private bool CheckedPendingEyedropper;

        public UIBuyBrowsePanel(TS1GameScreen screen, sbyte category, UICatalogMode mode) : base(screen) {
            CatContainer = new UITouchScroll(() => FilterCategory?.Count ?? 0, CatalogElemProvider);
            CatContainer.ItemWidth = 90;
            CatContainer.DrawBounds = false;
            CatContainer.Margin = 15;
            CatContainer.SetScroll(-15);
            CatContainer.Size = new Vector2(775, 128);
            Category = category;

            Add(CatContainer);
            Mode = mode;
            GameResized();

            //Built after GameResized (which sizes it) but before InitCategory, which is the call
            //that can first make it visible by finding no matches.
            NoResultsLabel = new UILabel();
            NoResultsLabel.Caption = SimitoneStrings.Get("82", "No items found");
            NoResultsLabel.Alignment = TextAlignment.Middle | TextAlignment.Center;
            NoResultsLabel.Position = new Vector2(0, 50);
            NoResultsLabel.Size = new Vector2(Size.X, 25);
            NoResultsLabel.CaptionStyle = NoResultsLabel.CaptionStyle.Clone();
            NoResultsLabel.CaptionStyle.Size = 14;
            NoResultsLabel.CaptionStyle.Color = UIStyle.Current.Text;
            NoResultsLabel.Visible = false;
            Add(NoResultsLabel);

            InitCategory(category, false);

            screen.LotControl.ObjectHolder.OnPickup += ObjectHolder_OnPickup;
            screen.LotControl.ObjectHolder.OnPutDown += ObjectHolder_OnPutDown;
            screen.LotControl.ObjectHolder.OnDelete += ObjectHolder_OnDelete;
            screen.LotControl.ObjectHolder.OnEyedropperPick += ObjectHolder_OnEyedropperPick;
            screen.LotControl.ObjectHolder.OnEyedropperArchitecturePick += ObjectHolder_OnEyedropperArchitecturePick;
            screen.LotControl.ObjectHolder.OnUndoRedo += ObjectHolder_OnUndoRedo;
            screen.LotControl.OnCustomControlReleased += LotControl_OnCustomControlReleased;
            HoldingEvents = true;
        }

        /// <summary>
        /// Maps a world catalog category to the Build mode UI category and the subcategory index
        /// within it, or null when the category is not something Build mode can select.
        ///
        /// The second value is an index into <c>BuildCategories[category]</c> - Build mode indexes
        /// its subcategory tables by position rather than by mask bit, and the order there is
        /// architecture (wall, wallpaper, floor, roof), outdoors (trees, terrain, water) and
        /// objects (door, window, staircase, fireplace).
        /// </summary>
        private static (int uiCategory, int subcatIndex)? MapCatalogToBuildCategory(int catalogCategory)
        {
            switch (catalogCategory)
            {
                // Objects (UI Category 2)
                case 8: return (2, 0);  // Doors
                case 9: return (2, 1);  // Windows
                case 10: return (2, 2); // Stairs
                case 12: return (2, 3); // Fireplaces

                // Outdoors (UI Category 1)
                case 11: return (1, 0); // Trees
                case 14: return (1, 2); // Water
                case 18: return (1, 1); // Terrain

                // Architecture (UI Category 0) - these are special rows, not clickable objects
                case 13: return (0, 0); // Walls
                case 15: return (0, 1); // Wallpaper
                case 16: return (0, 2); // Floors
                case 17: return (0, 3); // Roofs

                default: return null;
            }
        }

        /// <summary>
        /// Finishes a pick that was parked on the main panel while a category or mode switch was
        /// needed. Runs from <see cref="Update"/> rather than the constructor because
        /// <see cref="Parent"/> - and therefore the main panel the pick was parked on - is not set
        /// until after construction.
        /// </summary>
        private void CheckPendingEyedropperSelection()
        {
            if (CheckedPendingEyedropper) return;
            CheckedPendingEyedropper = true;

            var mainPanel = Parent as UIMainPanel;
            if (mainPanel == null) return;

            // Architecture first: it carries two pieces of state, so a stray guid left over from
            // an interrupted pick must not be able to pre-empt it.
            if (mainPanel.PendingEyedropperPatternID != null && mainPanel.PendingEyedropperArchType != null)
            {
                var patternID = mainPanel.PendingEyedropperPatternID.Value;
                var archType = mainPanel.PendingEyedropperArchType.Value;
                mainPanel.PendingEyedropperPatternID = null;
                mainPanel.PendingEyedropperArchType = null;

                SelectArchitectureByPatternID(patternID, archType);
                return;
            }

            if (mainPanel.PendingEyedropperGUID != null)
            {
                var guid = mainPanel.PendingEyedropperGUID.Value;
                mainPanel.PendingEyedropperGUID = null;

                SelectItemByGUID(guid);
            }
        }

        private void ObjectHolder_OnEyedropperPick(uint guid)
        {
            //One-shot tool. The desktop button's highlight is synced from this field every frame,
            //so there is nothing to update here.
            Game.LotControl.ObjectHolder.EyedropperMode = false;

            SelectItemByGUID(guid);
        }

        private void ObjectHolder_OnEyedropperArchitecturePick(ushort patternID, ArchitectureType archType)
        {
            Game.LotControl.ObjectHolder.EyedropperMode = false;

            // Architecture only exists in Build mode, so a pick made in Buy mode has to go there
            // first. Park the pick, switch, and let the new panel finish the job.
            if (Mode != UICatalogMode.Build)
            {
                var mainPanel = Parent as UIMainPanel;
                if (mainPanel != null)
                {
                    mainPanel.PendingEyedropperPatternID = patternID;
                    mainPanel.PendingEyedropperArchType = archType;
                    var frontend = Game.Frontend as UISimitoneFrontend;
                    frontend?.SwitchMode(UIMainPanelMode.BUILD);
                }
                return;
            }

            SelectArchitectureByPatternID(patternID, archType);
        }

        /// <summary>
        /// Selects an architecture style by its pattern id. Architecture rows have no guid -
        /// they are <see cref="UISpecialCatalogElement"/>s spliced into the category by
        /// <c>BuyCatalogService</c> - so they are matched on <c>Special.ResID</c>, which is where
        /// the pattern lives for floors, wallpapers and roofs alike.
        /// </summary>
        private void SelectArchitectureByPatternID(ushort patternID, ArchitectureType archType)
        {
            // Architecture is Build mode's UI category 0. Within it, the subcategory index is what
            // differs per kind (2 = floor, 1 = wallpaper). ArchitectureType only has Floor today,
            // so the wallpaper arm is unreachable and is kept only to name the slot it would take.
            int targetUICategory = 0;
            int targetSubcatIndex = (archType == ArchitectureType.Floor) ? 2 : 1;

            if (Category != targetUICategory)
            {
                var mainPanel = Parent as UIMainPanel;
                if (mainPanel != null)
                {
                    mainPanel.PendingEyedropperPatternID = patternID;
                    mainPanel.PendingEyedropperArchType = archType;
                    mainPanel.Switcher.Select(targetUICategory);
                }
                return;
            }

            // Choosing the subcategory is what actually builds the list of floors/wallpapers, so
            // it has to be open before the row can be found in it. It is opened whenever the
            // strip is still up *or* the list on screen is for a different subcategory: a player
            // already sitting in "roof" is looking at roofs only, and a floor pick would
            // otherwise find nothing and be silently dropped.
            var subcat = BuildCategories[targetUICategory][targetSubcatIndex];
            if (ChoosingSub || IndexOfPattern(FilterCategory, patternID) < 0)
            {
                OpenSubcategory(subcat);
            }

            var index = IndexOfPattern(FilterCategory, patternID);
            if (index < 0) return;

            Selected(index);
            CatContainer.ScrollToItem(index);
        }

        /// <summary>
        /// Index within <paramref name="items"/> of the architecture row carrying this pattern, or
        /// -1. Architecture rows are <see cref="UISpecialCatalogElement"/>s with no guid, so the
        /// pattern - the thing that actually identifies a floor/wallpaper/roof - is matched on
        /// <c>Special.ResID</c>.
        /// </summary>
        private static int IndexOfPattern(IEnumerable<UICatalogElement> items, ushort patternID)
        {
            if (items == null) return -1;

            int index = 0;
            foreach (var elem in items)
            {
                if (elem.Special != null && elem.Special.ResID == (ulong)patternID) return index;
                index++;
            }
            return -1;
        }

        /// <summary>
        /// Index within <paramref name="items"/> of the row for this catalog guid, or -1.
        /// </summary>
        private static int IndexOfGuid(IEnumerable<UICatalogElement> items, uint guid)
        {
            if (items == null) return -1;

            int index = 0;
            foreach (var elem in items)
            {
                if (elem.Item.GUID == guid) return index;
                index++;
            }
            return -1;
        }

        /// <summary>
        /// Selects a catalogued object by guid, switching mode and category as needed to make it
        /// reachable. Anything a mode switch or a category switch has to happen first is parked
        /// on the main panel rather than done inline, because both replace this panel.
        /// </summary>
        public void SelectItemByGUID(uint guid)
        {
            var catalogItem = Content.Get().WorldCatalog.GetItemByGUID(guid);
            if (catalogItem == null) return;

            var targetCategory = catalogItem.Value.Category;
            var mainPanel = Parent as UIMainPanel;

            // Buy owns catalog categories 0-7, Build everything from 8 up.
            bool isBuyItem = targetCategory >= 0 && targetCategory <= 7;
            bool isBuildItem = targetCategory >= 8;

            if (Mode == UICatalogMode.Build && isBuyItem)
            {
                if (mainPanel != null)
                {
                    mainPanel.PendingEyedropperGUID = guid;
                    (Game.Frontend as UISimitoneFrontend)?.SwitchMode(UIMainPanelMode.BUY);
                }
                return;
            }
            else if (Mode != UICatalogMode.Build && isBuildItem)
            {
                if (mainPanel != null)
                {
                    mainPanel.PendingEyedropperGUID = guid;
                    (Game.Frontend as UISimitoneFrontend)?.SwitchMode(UIMainPanelMode.BUILD);
                }
                return;
            }

            if (Mode == UICatalogMode.Build)
            {
                var mapping = MapCatalogToBuildCategory(targetCategory);
                if (mapping == null) return; // not something Build mode can select

                var (targetUICategory, targetSubcatIndex) = mapping.Value;

                if (targetUICategory != Category)
                {
                    if (mainPanel != null)
                    {
                        mainPanel.PendingEyedropperGUID = guid;
                        mainPanel.Switcher.Select(targetUICategory);
                    }
                    return;
                }

                // Open the subcategory this item lives under, if it is not the one already open.
                // In Build mode each subcategory loads its own catalog category, so without this
                // a pick of anything outside the currently open subcategory finds no row to
                // select and does nothing at all.
                if (ChoosingSub || IndexOfGuid(FilterCategory, guid) < 0)
                {
                    OpenSubcategory(BuildCategories[Category][targetSubcatIndex]);
                }

                SelectItemInCurrentCategory(guid);
                return;
            }

            // Buy mode
            if (targetCategory != Category)
            {
                if (mainPanel != null)
                {
                    mainPanel.PendingEyedropperGUID = guid;
                    mainPanel.Switcher.Select(targetCategory);
                }
                return;
            }

            SelectItemInCurrentCategory(guid);
        }

        /// <summary>
        /// Selects a guid in the category this panel is already showing, scrolling it into view.
        ///
        /// If the player has not picked a subcategory yet there is no item list to search - only
        /// the subcategory buttons - so the subcategory strip is dismissed and the whole category
        /// shown, which is the same "all items" view the shared "all" subcategory button gives.
        /// Otherwise the filtered list is searched first (it is what the player can see) and the
        /// unfiltered list second, so an item hidden by the active subcategory still gets
        /// selected rather than silently dropped.
        /// </summary>
        private void SelectItemInCurrentCategory(uint guid)
        {
            if (ChoosingSub)
            {
                ChoosingSub = false;
                foreach (var btn in SelButtons) Remove(btn);
                foreach (var label in SelLabels) Remove(label);
                SelButtons.Clear();
                SelLabels.Clear();
                ((UIMainPanel)Parent)?.Switcher?.Close();

                FilterCategory = FullCategory.Where(x => GetSubsort(x.Item) > 0).ToList();
                CatContainer.Opacity = 1f;
                CatContainer.Reset();
            }

            if (FilterCategory != null)
            {
                var index = IndexOfGuid(FilterCategory, guid);
                if (index >= 0)
                {
                    CatContainer.SelectItem(index);
                    CatContainer.ScrollToItem(index);
                    return;
                }
            }

            var fullIndex = IndexOfGuid(FullCategory, guid);
            if (fullIndex >= 0)
            {
                FilterCategory = FullCategory;
                CatContainer.Reset();
                CatContainer.SelectItem(fullIndex);
                CatContainer.ScrollToItem(fullIndex);
            }
        }

        private void ObjectHolder_OnDelete(UIObjectSelection holding, UpdateState state)
        {
            Game.Frontend.MainPanel.SetSubpanelPickup(1f);
            Game.LotControl.QueryPanel.Active = false;
            ClearSelection();
        }

        private void ObjectHolder_OnPutDown(UIObjectSelection holding, UpdateState state)
        {
            Game.LotControl.QueryPanel.Active = false;
            Game.Frontend.MainPanel.SetSubpanelPickup(1f);
            if (ItemID != -1)
            {
                if (!holding.IsBought && (state.ShiftDown))
                {
                    //place another
                    var prevDir = holding.Dir;
                    Selected(ItemID);
                    Game.LotControl.QueryPanel.SetShown(false);
                    Game.LotControl.ObjectHolder.Holding.Dir = prevDir;
                }
                else
                {
                    ClearSelection();
                }
            }
        }

        /// <summary>
        /// A build/buy undo or redo changed the lot. The same three things a sale has to tidy up,
        /// for the same reasons: whatever this panel had chosen or described is not what the lot
        /// holds any more.
        /// </summary>
        /// <remarks>
        /// This is the whole of the panel's side of undo. The stack itself belongs to
        /// <c>Game.LotControl.ObjectHolder.Undo</c> and the buttons to <c>UIMainPanel</c>; all
        /// this has to do is notice that the lot moved under it.
        /// </remarks>
        private void ObjectHolder_OnUndoRedo()
        {
            Game.Frontend.MainPanel.SetSubpanelPickup(1f);
            Game.LotControl.QueryPanel.Active = false;
            ClearSelection();
        }

        private void ObjectHolder_OnPickup(UIObjectSelection holding, UpdateState state)
        {
            Game.LotControl.PickupPanel.SetInfo(Game.LotControl.vm, holding.RealEnt ?? holding.Group.BaseObject);
            Game.Frontend.MainPanel.SetSubpanelPickup(0f);
        }

        private void LotControl_OnCustomControlReleased()
        {
            //ESC put the build tool down. Nothing is selected any more, so the query panel's
            //description of it has to go too - otherwise it stays up describing a tool the player
            //is no longer holding.
            Game.LotControl.QueryPanel.Active = false;
            ClearSelection();
        }

        /// <summary>
        /// Records that this panel is no longer holding a catalogue selection.
        ///
        /// <para>
        /// <see cref="ItemID"/> alone was not enough: it is the panel's own bookkeeping, while
        /// the highlighted tile in the catalogue strip is owned by <see cref="CatContainer"/>. So
        /// after buying, placing or deleting an object the strip went on showing a selected
        /// outline around a row that nothing was holding any more, and clicking that row again
        /// started a new purchase from out of nowhere. Dropping the strip's selection here keeps
        /// the two in step - the two share the same rule and so share one place to say it.
        /// </para>
        ///
        /// <para>
        /// Public because build/buy undo needs it too. Undoing a placement or a sale leaves an
        /// object back on the lot that nothing is holding, and a strip still showing the old row
        /// as chosen would have the player's next click start a fresh purchase out of the
        /// catalogue instead of landing on the object they just undid.
        /// <c>UIMainPanel.UndoBuildAction</c> and its redo twin call it.
        /// </para>
        /// </summary>
        public void ClearSelection()
        {
            CatContainer?.Unselect();
            ItemID = -1;
        }

        private void RemoveEvents()
        {
            if (HoldingEvents)
            {
                HoldingEvents = false;
                Game.LotControl.ObjectHolder.OnPickup -= ObjectHolder_OnPickup;
                Game.LotControl.ObjectHolder.OnPutDown -= ObjectHolder_OnPutDown;
                Game.LotControl.ObjectHolder.OnDelete -= ObjectHolder_OnDelete;
                Game.LotControl.ObjectHolder.OnEyedropperPick -= ObjectHolder_OnEyedropperPick;
                Game.LotControl.ObjectHolder.OnEyedropperArchitecturePick -= ObjectHolder_OnEyedropperArchitecturePick;
                Game.LotControl.ObjectHolder.OnUndoRedo -= ObjectHolder_OnUndoRedo;
                Game.LotControl.OnCustomControlReleased -= LotControl_OnCustomControlReleased;
                //The tool is armed on the object holder, which outlives this panel, so a panel
                //that is torn down mid-pick would otherwise leave it armed with nothing listening.
                Game.LotControl.ObjectHolder.EyedropperMode = false;
                Game.LotControl.QueryPanel.Active = false;
                Game.Frontend.MainPanel.SetSubpanelPickup(1f);

                if (Game.LotControl.CustomControl != null)
                {
                    Game.LotControl.CustomControl.Release();
                    Game.LotControl.CustomControl = null;
                }

                if (Game.LotControl.ObjectHolder.Holding != null)
                {
                    //delete object that hasn't been placed yet
                    //TODO: all holding objects should obviously just be ghosts.
                    //Holder.Holding.Group.Delete(vm.Context);
                    Game.LotControl.ObjectHolder.ClearSelected();
                }
            }
        }

        public override void Kill()
        {
            RemoveEvents();

            base.Kill();
        }

        public void Selected(int itemID)
        {
            var holder = Game.LotControl.ObjectHolder;
            var control = Game.LotControl;
            holder.ClearSelected();
            var item = FilterCategory[itemID];

            //todo: check if over budget?

            // if (OldSelection != -1) Catalog.SetActive(OldSelection, false);
            //Catalog.SetActive(selection, true);

            if (control.CustomControl != null)
            {
                control.CustomControl.Release();
                control.CustomControl = null;
            }

            if (item.Special != null)
            {
                var res = item.Special.Res;
                var resID = item.Special.ResID;
                if (res != null && res.GetName(resID) != "")
                {
                    Game.LotControl.QueryPanel.SetInfo(res.GetThumb(resID), res.GetName(resID), res.GetDescription(resID), res.GetPrice(resID), res.DoDispose());
                    Game.LotControl.QueryPanel.Mode = 1;
                    //QueryPanel.Tab = 0;
                    Game.LotControl.QueryPanel.Active = true;
                    Game.LotControl.QueryPanel.SetShown(true);
                }
                control.CustomControl = (UICustomLotControl)Activator.CreateInstance(item.Special.Control, control.vm, control.World, control, item.Special.Parameters);
            }
            else
            {
                var BuyItem = control.vm.Context.CreateObjectInstance(item.Item.GUID, LotTilePos.OUT_OF_WORLD, Direction.NORTH, holder.UseNet);
                if (BuyItem.Objects.Count != 0)
                {
                    Game.LotControl.QueryPanel.SetInfo(Game.LotControl.vm, BuyItem.Objects[0], false);
                    Game.LotControl.QueryPanel.Mode = 1;
                    //QueryPanel.Tab = 0;
                    Game.LotControl.QueryPanel.Active = true;
                    Game.LotControl.QueryPanel.SetShown(true);
                    holder.SetSelected(BuyItem);
                }
            }

            ItemID = itemID;
        }

        /// <summary>
        /// Puts down whatever this panel currently has out: the object it was holding to be
        /// placed, the build tool it had started, and its own highlight in the strip.
        ///
        /// <para>
        /// The counterpart to <see cref="Selected"/>, and reachable from the player: clicking the
        /// row that is already active is the one gesture that means "no, not that one". It has to
        /// go through here rather than just dropping the outline, because by this point there is a
        /// live object instance held out of world by the lot, a query panel describing it and a
        /// custom lot control running - the query panel would otherwise stay up describing a tool
        /// the player no longer has, and the next click in the lot would be landing on a control
        /// that is still live.
        /// </para>
        /// </summary>
        public void Deselect()
        {
            var control = Game.LotControl;
            control.ObjectHolder.ClearSelected();
            control.QueryPanel.Active = false;
            Game.Frontend.MainPanel.SetSubpanelPickup(1f);

            if (control.CustomControl != null)
            {
                control.CustomControl.Release();
                control.CustomControl = null;
            }

            ClearSelection();
        }

        public override void Removed()
        {
            RemoveEvents();
            base.Removed();
            //Catalog.UICatalogItem.ClearIconCache(); //might want to be careful here...
        }

        public void Reset()
        {
            GameFacade.Screens.Tween.To(CatContainer, 0.5f, new Dictionary<string, float>() { { "Opacity", 0f } }, TweenQuad.EaseOut);
            InitCategory(Category, false);
        }

        public void InitCategory(sbyte category, bool build)
        {
            //start by populating with entries from the catalog
            if (!build) ((UIMainPanel)Parent)?.Switcher?.MainButton?.RestoreImage();
            var catalog = Content.Get().WorldCatalog;

            var items = catalog.GetItemsByCategory(category);
            
            FullCategory = items.Select(x => new UICatalogElement()
            {
                Item = x,
                CalcPrice = (int)x.Price
            }).ToList();

            //pull from other categories

            if (category == 15) AddWallpapers();
            if (category == 14 || category == 16) AddFloors(category);
            if (category == 17) AddRoofs();
            if (category == 18) AddTerrainTools();

            FullCategory = FullCategory.OrderBy(x => (int)x.Item.Price).ToList();
            if (category == 13) AddWallStyles();

            //NO eager name resolution here, and that is the point.
            //
            //The name a row is sold under lives in the object's catalogue strings, so producing it
            //for a whole category means one object-resource read per row - a file read per row,
            //synchronously, every time the player opens a tab. With a large custom content set a
            //single category runs to thousands of rows, and all of that was being paid for rows
            //the player might never scroll to, let alone read.
            //
            //Names are now resolved per row, on first use, and remembered (see
            //UICatalogElement.DisplayName): opening a tab costs nothing, scrolling costs one read
            //per row that actually comes on screen, and the first keystroke in the search box costs
            //one read per row in the category - after which every later keystroke is free, because
            //the names are already in memory. So the same names are available to the search, and
            //the case that never needed them never pays for them.

            //Any rebuild of the list ends the search session: the rows the term was filtering are
            //gone. Clearing ActiveSearchTerm is also what makes the next keystroke take a fresh
            //snapshot rather than offer to restore a list belonging to the old category. The box's
            //own text is UIMainPanel's to clear (Switcher_OnOpen does it), so that Escape is never
            //spent clearing a term that is not narrowing anything.
            ActiveSearchTerm = null;
            PreSearchFilterCategory = null;
            if (NoResultsLabel != null) NoResultsLabel.Visible = false;

            //if we're not build mode, init the subcategory selection
            if (build) return;

            foreach (var btn in SelButtons) Remove(btn);
            foreach (var label in SelLabels) Remove(label);
            SelButtons.Clear();
            SelLabels.Clear();

            ChoosingSub = true;

            // Work on a local copy of the subcategory list. The tables in Categories/BuildCategories/
            // DTCategories are public static data shared by every instance of this panel, and this
            // method used to append the shared "other"/"all" entries straight onto DTCategories[Mode],
            // permanently growing it to 6 entries and desyncing it from the 4-entry DTIcons table.
            // A local copy renders identically on first open (and on every open afterwards, which is
            // what the mutated table used to amount to) while leaving the static tables pristine.
            List<UICatalogSubcat> cats;
            if (Mode == UICatalogMode.Build) cats = new List<UICatalogSubcat>(BuildCategories[category]);
            else if (Mode != UICatalogMode.Normal)
            {
                cats = new List<UICatalogSubcat>(DTCategories[Mode]);
                if (cats.Count == 4) //haven't added other or all subcats yet
                {
                    cats.Add(new UICatalogSubcat()
                    {
                        StrTable = 210,
                        MaskBit = 7,
                        StrInd = 1, //other
                    });

                    cats.Add(new UICatalogSubcat()
                    {
                        StrTable = 210,
                        MaskBit = 8,
                        StrInd = 2, //all
                    });
                }
            }
            else
            {
                cats = new List<UICatalogSubcat>(Categories[category]);
            }

            var boff = CatContainer.Size.X/(cats.Count + 0.5f) / 2f;

            for (int i=0; i<cats.Count; i++)
            {
                var cat = cats[i];
                var str = GameFacade.Strings.GetString(cat.StrTable.ToString(), cat.StrInd.ToString());

                var label = new UILabel();
                label.Caption = str;
                label.Alignment = TextAlignment.Middle | TextAlignment.Center;
                label.Wrapped = true;
                label.Position = new Vector2(boff * (1.5f+i*2) - (120/2), 106);
                label.Size = new Vector2(120, 1);
                label.CaptionStyle = label.CaptionStyle.Clone();
                label.CaptionStyle.Size = 12;
                label.CaptionStyle.Color = UIStyle.Current.Text;
                SelLabels.Add(label);
                Add(label);

                var name = "";
                if (Mode == UICatalogMode.Build) {
                    name = BuildIcons[category][i];
                }
                else if (Mode != UICatalogMode.Normal) {
                    if (cat.MaskBit == 7) name = "other";
                    else if (cat.MaskBit == 8) name = "all";
                    else name = DTIcons[Mode][cat.MaskBit];
                }
                else
                {
                    if (cat.MaskBit == 7) name = "other";
                    else if (cat.MaskBit == 8) name = "all";
                    else name = CatIcons[category][cat.MaskBit];
                }

                cat.IconName = name; 

                var subbutton = new UICatButton(Content.Get().CustomUI.Get("cat_"+name+".png").Get(GameFacade.GraphicsDevice));
                subbutton.OnButtonClick += (btn) => { InitSubcategory(cat); };
                subbutton.Position = new Vector2(boff * (1.5f + i * 2) - (65 / 2), 16);
                subbutton.Disabled = SubcatIsEmpty(cat);
                SelButtons.Add(subbutton);
                Add(subbutton);
            }

            //InitSubcategory(0);
        }

        // -------------------------------------------------------------------------------------
        // Category row builders. The construction of the special rows lives in BuyCatalogService
        // (UI/Services) with the tables above; these pass this panel's FullCategory list, which is
        // the one thing the builders still need from the panel.
        // -------------------------------------------------------------------------------------

        private void AddFloors(sbyte category)
        {
            BuyCatalogService.AddFloors(FullCategory, category);
        }

        private void AddWallpapers()
        {
            BuyCatalogService.AddWallpapers(FullCategory);
        }

        private void AddRoofs()
        {
            BuyCatalogService.AddRoofs(FullCategory);
        }

        private void AddWallStyles()
        {
            BuyCatalogService.AddWallStyles(FullCategory);
        }

        private void AddTerrainTools()
        {
            BuyCatalogService.AddTerrainTools(FullCategory);
        }

        public override void GameResized()
        {
            base.GameResized();
            CatContainer.Size = new Vector2(Size.X, 128);
            //null until the constructor has got past its own GameResized call.
            if (NoResultsLabel != null) NoResultsLabel.Size = new Vector2(Size.X, 25);
            if (ChoosingSub) Reset();
        }

        public override void Update(UpdateState state)
        {
            //Finish a pick that had to wait for this panel to exist. Runs on the first update
            //rather than in the constructor because the main panel the pick was parked on is only
            //reachable through Parent.
            CheckPendingEyedropperSelection();

            Invalidate();
            var first = SelButtons.FirstOrDefault();
            if (first != null && first.Opacity == 0)
            {
                foreach (var btn in SelButtons) Remove(btn);
                foreach (var label in SelLabels) Remove(label);
                SelButtons.Clear();
                SelLabels.Clear();
            }
            base.Update(state);
        }

        public UITSContainer CatalogElemProvider(int index)
        {
            var elem = new Catalog.UICatalogItem(FilterCategory[index], this);
            return elem;
        }

        public byte GetSubsort(ObjectCatalogItem item)
        {
            return CatalogQueryService.GetSubsort(item, Mode);
        }

        private bool SubcatIsEmpty(UICatalogSubcat cat)
        {
            return CatalogQueryService.SubcatIsEmpty(cat, Mode, FullCategory);
        }

        /// <summary>
        /// Narrows the strip to the rows whose name contains <paramref name="term"/>, or puts the
        /// strip back the way it was when the term is emptied.
        ///
        /// <para>
        /// Called on every keystroke, so the term's empty-ness is the signal for "clear" and
        /// everything else is "narrow". A clear that arrives when no search is in progress does
        /// nothing at all: <c>SetSubpanel</c> clears the outgoing panel's search on the way past,
        /// and a panel that was never searched must come out of that with its list intact.
        /// </para>
        /// </summary>
        public void ApplyNameFilter(string term)
        {
            //Nothing on screen is a list of this category's items yet (see SearchApplies), so
            //there is nothing to narrow. UIMainPanel shows that state on the box itself.
            if (!BuyCatalogService.SearchApplies(Mode, ChoosingSub)) return;

            if (string.IsNullOrWhiteSpace(term))
            {
                if (ActiveSearchTerm == null) return; //no search in progress, nothing to undo

                ActiveSearchTerm = null;
                //May legitimately be null: a search run before any subcategory was chosen replaces a
                //list that did not exist yet, and putting that null back is what brings the
                //subcategory buttons' "nothing chosen yet" state back.
                FilterCategory = PreSearchFilterCategory;
                PreSearchFilterCategory = null;

                //The buttons are hidden while searching so the results are not behind them.
                foreach (var btn in SelButtons) btn.Visible = true;
                foreach (var label in SelLabels) label.Visible = true;

                if (NoResultsLabel != null) NoResultsLabel.Visible = false;
                CatContainer.Reset();
                return;
            }

            //First keystroke of a search session: remember what the strip was showing, and get the
            //subcategory buttons out of the way of the results.
            if (ActiveSearchTerm == null)
            {
                PreSearchFilterCategory = FilterCategory;
                foreach (var btn in SelButtons) btn.Visible = false;
                foreach (var label in SelLabels) label.Visible = false;
            }

            ActiveSearchTerm = term;

            //Always rebuilt from the whole category, never from the previous result set - that is
            //what lets a character be deleted and its rows come back.
            //
            //This is also the one moment in the panel that can resolve row names for the whole
            //category, because the search matches on them (BuyCatalogService.SearchCategory counts
            //and logs how many rows that was). It is paid once per category: the rows it resolves
            //remember their answer, so the next keystroke is a pass over strings already in memory.
            FilterCategory = BuyCatalogService.SearchCategory(FullCategory, term);

            if (NoResultsLabel != null) NoResultsLabel.Visible = FilterCategory.Count == 0;
            CatContainer.Reset();
        }

        public void InitSubcategory(UICatalogSubcat cat)
        {
            //One-shot: the subcategory strip is gone once a subcategory has been opened, so a
            //late click on one of its buttons (or a stray event) must not re-open it.
            if (!ChoosingSub) return;
            OpenSubcategory(cat);
        }

        /// <summary>
        /// Opens <paramref name="cat"/>'s item list: dismisses the subcategory strip, points the
        /// switcher's main button at the chosen subcategory, and builds the list itself.
        ///
        /// Split out of <see cref="InitSubcategory"/> so the eyedropper can open the *right*
        /// subcategory even when one is already open. The guard on that method is about reacting
        /// twice to one click; re-opening a subcategory deliberately is a different thing, and
        /// Build mode needs it - each subcategory there loads its own catalog category, so a
        /// player already sitting in "door" is looking at doors only.
        ///
        /// Safe to call when no subcategory was open: it does not build the strip, only tears it
        /// down, so there is nothing to tear down.
        /// </summary>
        private void OpenSubcategory(UICatalogSubcat cat)
        {
            var index = cat.MaskBit;
            ChoosingSub = false;
            ((UIMainPanel)Parent).Switcher.Close();
            ((UIMainPanel)Parent).Switcher.MainButton.ReplaceImage(Content.Get().CustomUI.Get("cat_" + cat.IconName + ".png").Get(GameFacade.GraphicsDevice));

            foreach (var btn in SelButtons)
            {
                GameFacade.Screens.Tween.To(btn, 0.5f, new Dictionary<string, float>() { { "Opacity", 0f } }, TweenQuad.EaseOut);
            }
            foreach (var label in SelLabels)
            {
                GameFacade.Screens.Tween.To(label, 0.5f, new Dictionary<string, float>() { { "Opacity", 0f } }, TweenQuad.EaseOut);
            }

            CatContainer.Opacity = 0f;
            GameFacade.Screens.Tween.To(CatContainer, 0.5f, new Dictionary<string, float>() { { "Opacity", 1f } }, TweenQuad.EaseOut);

            if (Mode == UICatalogMode.Build)
            {
                InitCategory((sbyte)index, true);
                FilterCategory = FullCategory;
            }
            else
            {
                //Materialised - see FilterCategory. (CatalogQueryService.Filter hands back a
                //deferred Where for every subcategory except the two that pass it through.)
                FilterCategory = CatalogQueryService.Filter(cat, Mode, FullCategory).ToList();
            }
            CatContainer.Reset();
        }
    }

    public enum UICatalogMode
    {
        Normal,
        Downtown,
        Community,
        Vacation,
        Studiotown,
        Magictown,
        Build
    }

    public class UICatalogElement
    {
        public ObjectCatalogItem Item;
        public int CalcPrice;
        public UISpecialCatalogElement Special;

        private string _displayName;
        private bool _displayNameResolved;

        /// <summary>
        /// The name this row is actually sold under, read from the object's catalogue strings (or
        /// from its resource provider, for the non-object rows).
        ///
        /// <para>
        /// <b>Resolved on first read, and remembered.</b> It used to be a plain field filled in
        /// for every row the moment a category was built, which meant one object-resource read per
        /// row per category open - a file read each, up front, for rows the player might never
        /// look at. Resolving it here instead moves that cost to the rows that are actually used
        /// and caps it at one read per row for the row's whole life, however many times it is
        /// drawn, searched, recounted or deselected.
        /// </para>
        ///
        /// <para>
        /// A <em>null</em> result is remembered too, which is the part that matters for a broken
        /// custom content set: a row whose content will not load is precisely the row that would
        /// otherwise re-read, and re-throw, on every frame it is on screen. If it cannot be named
        /// once it cannot be named again, and it is searched and labelled on its OBJD name
        /// instead.
        /// </para>
        ///
        /// <para>
        /// Assigning null is <em>not</em> how you say "this row has no name" - it means "I have
        /// not told you the name", and the next read goes and looks. Only a real assignment (or a
        /// read that finds nothing) marks the row as settled.
        /// </para>
        /// </summary>
        public string DisplayName
        {
            get
            {
                if (_displayNameResolved) return _displayName;

                //Settled *before* resolving, not after: a resolver that throws, or that somehow
                //re-enters, must not leave the row asking again on the next frame.
                _displayNameResolved = true;
                _displayName = BuyCatalogService.DisplayNameFor(this, DisplayNameSource);
                return _displayName;
            }
            set
            {
                _displayName = BuyCatalogService.NormalizeName(value);
                _displayNameResolved = value != null;
            }
        }

        /// <summary>
        /// Whether this row's name has been settled - either read once, or assigned. Diagnostics
        /// only; nothing branches on it.
        /// </summary>
        public bool DisplayNameResolved
        {
            get { return _displayNameResolved; }
        }

        /// <summary>
        /// Where to read this row's name from, or null to read the row's own content. The seam
        /// exists so a caller that already knows the name (a cache, a name table built alongside a
        /// content set) can supply it, and so the lazy behaviour above is reachable from a test
        /// without a loaded world. See <see cref="BuyCatalogService.DisplayNameFor"/>.
        /// </summary>
        public Func<UICatalogElement, string> DisplayNameSource;

        public override string ToString()
        {
            return DisplayName ?? Item.Name ?? "(unknown)";
        }
    }

    public class UISpecialCatalogElement
    {
        public Type Control;
        public ulong ResID;
        public UICatalogResProvider Res;
        public List<int> Parameters;
    }

    public class UICatalogSubcat
    {
        public int StrTable;
        public int StrInd;
        public int MaskBit;
        public string IconName;
    }
}
