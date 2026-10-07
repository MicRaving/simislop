/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Content;
using Microsoft.Xna.Framework;
using Simitone.Client.UI.Controls;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Simitone.Client.UI.Panels
{
    public class UINeighbourhoodSwitcher : UIContainer
    {
        public List<UIElasticButton> LeftBtns = new List<UIElasticButton>();
        public List<UIElasticButton> RightBtns = new List<UIElasticButton>();
        private UINeighborhoodSelectionPanel Panel;
        private ushort Mode;
        public bool MoveInMode;

        /// <summary>
        /// Where the "create a family" button has to sit, and how far apart the column's buttons are.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The CAS button is the first thing added to <see cref="LeftBtns"/> and therefore the top
        /// one, and that is a contract rather than an accident.</b> §8.1's fourth requirement keeps the
        /// family-creation button in the top-left corner, and <see cref="SetMode"/> rebuilds the whole
        /// column every time a neighbourhood is opened - so "it happens to be first today" is exactly
        /// the kind of thing that stops being true the next time somebody adds a button. The index and
        /// the geometry are therefore named, and <see cref="SetMode"/> inserts the button at
        /// <see cref="CreateFamilyButtonIndex"/> rather than merely appending it first.
        /// </para>
        /// <para>
        /// <b>The left column now holds only the CAS button.</b> The button back to the base
        /// neighbourhood and the neighbourhood-management page used to sit under it; both moved to
        /// the right column, which is the column of places to go rather than of ways to leave.
        /// </para>
        /// </remarks>
        public const int CreateFamilyButtonIndex = 0;

        /// <summary>The left column's first button's x, in unscaled screen pixels.</summary>
        public const float LeftColumnX = 17 + 48;

        /// <summary>The left column's first button's y, in unscaled screen pixels.</summary>
        public const float LeftColumnY = 14 + 48;

        /// <summary>Vertical pitch between two buttons in the left column.</summary>
        public const float LeftColumnPitch = 110f;

        /// <summary>
        /// Where the button at <paramref name="index"/> in the left column goes, in unscaled screen
        /// pixels. Pure arithmetic, so "the create-a-family button is top-left" is an assertion rather
        /// than a screenshot.
        /// </summary>
        /// <param name="index">The button's position in the column, counting from zero.</param>
        public static Vector2 LeftButtonPosition(int index)
        {
            return new Vector2(LeftColumnX, LeftColumnY + LeftColumnPitch * index);
        }

        /// <summary>
        /// The right column's x, in unscaled screen pixels: one button-width in from the right edge.
        /// </summary>
        public static float RightColumnX(int screenWidth)
        {
            return screenWidth - (17 + 96) + 48;
        }

        /// <summary>Vertical pitch between two buttons in the right column.</summary>
        public const float RightColumnPitch = 110f;

        /// <summary>
        /// Where the button at <paramref name="index"/> in the right column goes, in unscaled screen
        /// pixels.
        /// </summary>
        /// <remarks>
        /// The column holds the base view, five travel views and the management page - seven
        /// buttons - which does not fit a 110px pitch on a short window. The pitch therefore shrinks
        /// when it has to, so the last button stays on screen instead of running off the bottom.
        /// </remarks>
        /// <param name="index">The button's position in the column, counting from zero.</param>
        /// <param name="count">How many buttons the column has.</param>
        /// <param name="screenWidth">The window's width, in unscaled screen pixels.</param>
        /// <param name="screenHeight">The window's height, in unscaled screen pixels.</param>
        public static Vector2 RightButtonPosition(int index, int count, int screenWidth, int screenHeight)
        {
            var pitch = RightColumnPitch;
            if (count > 1)
            {
                var room = screenHeight - (LeftColumnY * 2);
                pitch = Math.Min(pitch, room / (float)(count - 1));
            }
            return new Vector2(RightColumnX(screenWidth), LeftColumnY + pitch * index);
        }

        /// <summary>
        /// The map's lot filter bar: the lot filters and the count they leave.
        ///
        /// <para>
        /// A child of the switcher rather than of the map panel, and that is a layout decision
        /// rather than an ownership one: the map panel scales its children with the camera, so
        /// chrome inside it would be half-size zoomed out and far too large on Unleashed, which
        /// draws its map at half scale and therefore focuses a lot at 3x. The switcher is the map
        /// screen's fixed-size chrome, so anything that has to stay the same size at every zoom
        /// belongs here.
        /// </para>
        /// <para>
        /// It is not removed in <see cref="SetMode"/>. The bar is a member rather than one of the
        /// per-mode buttons, and a neighbourhood change re-reads it instead - see the end of
        /// <see cref="SetMode"/>, which is also what keeps its filter state honest across a trip.
        /// </para>
        /// </summary>
        public UINeighbourhoodFilterBar FilterBar { get; private set; }

        public UINeighbourhoodSwitcher(UINeighborhoodSelectionPanel panel, ushort mode, bool moveIn)
        {
            Panel = panel;
            FilterBar = new UINeighbourhoodFilterBar(panel);
            Add(FilterBar);
            SetMode(mode, moveIn);
        }

        public void SetMode(ushort mode, bool moveIn)
        {
            // entering CAS is a screen change like any other, so it gets the same wipe the
            // neighbourhood buttons do
            UITransDialog WithPresetTransition(string icon, Action action) => new UITransDialog(icon, action);

            MoveInMode = moveIn;
            foreach (var btn in LeftBtns) Remove(btn);
            foreach (var btn in RightBtns) Remove(btn);
            LeftBtns.Clear(); RightBtns.Clear();

            AddBtn(LeftBtns, "ngbh_cas.png", (btn) =>
            {
                WithPresetTransition("cas", () =>
                {
                    GameController.EnterCAS();
                });
            });
            //Asserted rather than assumed: the column above is rebuilt on every neighbourhood switch,
            //so "the family button is at index 0" is the thing that has to stay true, and a test reads
            //it from here rather than from a screenshot. See CreateFamilyButtonIndex.
            if (LeftBtns.Count <= CreateFamilyButtonIndex)
            {
                SimitoneLog.Warn("The create-a-family button was not added to the left column of the"
                    + " neighbourhood switcher, so it is not where §8.1 puts it.");
            }

            //The right column, top to bottom: the base neighbourhood, the travel views, then the
            //neighbourhood page. The base button is first because it is the way home from any other
            //view; the page is last because it is the one button that leaves the map altogether.
            //
            //"Back to the base neighbourhood" now means what it says. It used to go to view 4,
            //the Unleashed map, because Simitone's hybrid mode opened there and called that the
            //residential neighbourhood - which left view 1, The Sims 1's own map, with no way to
            //be reached at all. Unleashed has a button of its own below, so nothing was traded
            //away to get here; see NeighbourhoodModeMap, which is where the numbers come from.
            var back = AddBtn(RightBtns, "ngbh_back.png", (btn) =>
            {
                //Already on the base map: the button is the active-view indicator, not a
                //destination, so pressing it does nothing.
                if (mode == NeighbourhoodModeMap.BaseResidential) return;
                PopMode(NeighbourhoodModeMap.TransitionIcon(NeighbourhoodModeMap.BaseResidential),
                    NeighbourhoodModeMap.BaseResidential);
            });
            back.Active = mode == NeighbourhoodModeMap.BaseResidential;
            back.Tooltip = NeighbourhoodModeMap.TravelTooltip(NeighbourhoodModeMap.BaseResidential);

            //The travel views, in view order. The view the player is already on is added too and
            //marked active, so the column always shows every view and which one is on screen -
            //that is the indicator the active view's button carries. The three marked hidden are
            //the ones the original hid while a family was waiting to move in: a family about to be
            //given a lot should not travel to another expansion first. Unleashed and MagicTown
            //were not hidden before this change either, and Unleashed in particular could not be
            //given one without losing its button: the map opens on it, so the only button that
            //reached it was the "back" one, which now goes to the base map.
            AddTravel(RightBtns, mode, NeighbourhoodModeMap.Downtown, hiddenDuringMoveIn: true);
            AddTravel(RightBtns, mode, NeighbourhoodModeMap.Vacation, hiddenDuringMoveIn: true);
            AddTravel(RightBtns, mode, NeighbourhoodModeMap.Unleashed, hiddenDuringMoveIn: false);
            AddTravel(RightBtns, mode, NeighbourhoodModeMap.Studiotown, hiddenDuringMoveIn: true);
            AddTravel(RightBtns, mode, NeighbourhoodModeMap.Magictown, hiddenDuringMoveIn: false);

            //The management page is the way into creating, copying in, renaming and deleting whole
            //neighbourhoods, and into opening one instead of the one that is open. It sits below
            //the travel buttons because it is not travel: the travel buttons move between the views
            //inside the slot that is open, while the page is about the slots themselves. Shown in
            //every mode including move-in, which hides the travel buttons, because the page is safe
            //to use there - and it refuses its own Open button while a family is waiting to move in.
            //The tooltip is what makes a "..." button findable at all.
            var manageBtn = AddBtn(RightBtns, "ngbh_opt.png", (btn) => UINeighbourhoodManagementPanel.Show());
            manageBtn.Tooltip = UINeighbourhoodManagementPanel.Title;

            Mode = mode;

            //The bar describes the map that is now on screen, not the one that was: which lots
            //exist, which filters match anything, and how many of each there are are all answers
            //that changed with the switch. Its filter set deliberately survives - a player who
            //asked to see the empty lots gets the empty lots of the map they travelled to, and
            //the button for a filter this map cannot satisfy draws itself dead.
            FilterBar.Refresh();

            LayBtns();
        }

        /// <summary>
        /// Adds one travel button, unless the view is hidden in this context.
        /// </summary>
        /// <param name="target">The buttons to add to.</param>
        /// <param name="from">The view the player is on. The button for this view is the active
        /// indicator and is not a destination.</param>
        /// <param name="mode">The view to travel to.</param>
        /// <param name="hiddenDuringMoveIn">
        /// True for the views the original hid while a family was waiting to move in.
        /// </param>
        private void AddTravel(List<UIElasticButton> target, ushort from, ushort mode, bool hiddenDuringMoveIn)
        {
            if (hiddenDuringMoveIn && MoveInMode) return;
            if (!NeighbourhoodModeMap.HasView(mode)) return;

            var active = mode == from;
            var btn = AddBtn(target, NeighbourhoodModeMap.TravelIcon(mode),
                (b) =>
                {
                    //The view already on screen is the active-view indicator, not a destination.
                    if (active) return;
                    PopMode(NeighbourhoodModeMap.TransitionIcon(mode), mode);
                });
            btn.Active = active;
            var tooltip = NeighbourhoodModeMap.TravelTooltip(mode);
            if (tooltip != null) btn.Tooltip = tooltip;
        }

        /// <summary>
        /// Switches the neighbourhood view to <paramref name="mode"/>, behind a wipe that shows
        /// the button that was pressed.
        /// </summary>
        /// <param name="neighborhoodIcon">CustomUI image to draw over the wipe. The "back to
        /// the base neighbourhood" button has no plate of its own for the wipe, so it passes the
        /// generic transition plate; the other travel buttons pass their own.</param>
        /// <param name="mode">The neighbourhood to show.</param>
        public void PopMode(string neighborhoodIcon, ushort mode)
        {
            UITransDialog WithTransition(string icon, Action action) => UITransDialog.CreateWithCustomUIIcon(icon, action);
            WithTransition(neighborhoodIcon, () =>
            {
                Panel.PopulateScreen(mode);
                SetMode(mode, MoveInMode);
            });
        }

        private void LayBtns()
        {
            int i = 0;
            foreach (var btn in LeftBtns)
            {
                //through the named helper rather than the arithmetic written out again, so the
                //position the test asserts is the position the button is given
                btn.Position = LeftButtonPosition(i);
                i++;
            }

            var screen = UIScreen.Current;
            var width = screen?.ScreenWidth ?? 0;
            var height = screen?.ScreenHeight ?? 0;
            for (i = 0; i < RightBtns.Count; i++)
            {
                RightBtns[i].Position = RightButtonPosition(i, RightBtns.Count, width, height);
            }

            //The filter bar is anchored to the top centre, so the window's width changing moves it
            //too. It re-lays itself out for its own sake; this is only about the switcher asking.
            FilterBar?.LayOut();
        }

        public override void GameResized()
        {
            base.GameResized();
            // the right hand column is anchored to the right edge of the window, so it has to
            // be re-laid-out when that edge moves
            LayBtns();
        }

        private UIElasticButton AddBtn(List<UIElasticButton> targ, string imgname, ButtonClickDelegate onClick)
        {
            var ui = Content.Get().CustomUI;
            var btn = new UIElasticButton(ui.Get(imgname).Get(GameFacade.GraphicsDevice))
            {
                // lifts the button off the flat neighbourhood art behind it
                ElementShadow = new UIShadow(ui.Get("ngbh_shadow.png").Get(GameFacade.GraphicsDevice)) { ShadowDepth = 5 }
            };
            btn.OnButtonClick += onClick;
            targ.Add(btn);
            Add(btn);
            return btn;
        }
    }
}
