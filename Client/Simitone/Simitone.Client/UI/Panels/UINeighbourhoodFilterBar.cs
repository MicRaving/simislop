/*
This Source Code Form is subject to the terms of the Mozilla Public License, v. 2.0.
If a copy of the MPL was not distributed with this file, You can obtain one at
http://mozilla.org/MPL/2.0/.
*/

using FSO.Client;
using FSO.Client.UI.Controls;
using FSO.Client.UI.Framework;
using FSO.Client.UI.Model;
using FSO.Common.Rendering.Framework.IO;
using FSO.Common.Rendering.Framework.Model;
using FSO.Content;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Simitone.Client.UI.Controls;
using Simitone.Client.UI.Model;
using Simitone.Client.Utils;
using System;
using System.Collections.Generic;

namespace Simitone.Client.UI.Panels
{
    /// <summary>
    /// The strip of controls along the top of the neighbourhood map: the lot filters, the count
    /// they leave, the Add Lot button, and the Previous/Next pair that steps through the lots
    /// that are left.
    ///
    /// <para>
    /// <b>Where it comes from.</b> Retail's neighbourhood view had a filter bar - three
    /// <c>filter_toolbar*.bmp</c> plates in the shipped <c>UIGraphics.far</c>, one per map, plus
    /// thirteen four-state <c>Filter*Btn.bmp</c> toggles along it - and a <c>Next</c>/
    /// <c>Previous</c> pair of arrows at one end. The shape is copied; none of the artwork is.
    /// Simitone's plates and glyphs are its own, in the palette its other neighbourhood buttons
    /// already use, and the set of filters is four rather than thirteen because that is what this
    /// tree can answer a question about - see <see cref="NeighLotFilters"/>.
    /// </para>
    /// <para>
    /// <b>Why four states per button.</b> Not decoration, and not a copy of the retail layout for
    /// its own sake: <see cref="FSO.Client.UI.Controls.UIButton"/> picks frame 0 for normal, 1 for
    /// selected, 2 for hovered and 3 for disabled, so a four-frame strip is what a toggle with a
    /// "nothing here matches" state is made of. Retail's buttons are four wide for the same
    /// reason, and drawing them the other way round is a mistake this layout cannot make.
    /// </para>
    /// <para>
    /// <b>It is a child of the switcher, not of the map.</b> The map panel scales everything it
    /// contains with the camera, so a control that lived there would be half the size zoomed out
    /// and a third again too big on Unleashed, which draws its map at half scale and therefore
    /// focuses a lot at 3x. The switcher is the map screen's fixed-size chrome - the CAS button,
    /// the travel buttons, the neighbourhood page - and this is the rest of it.
    /// </para>
    /// <para>
    /// Everything the bar decides is a question about the map's lots, and every one of those
    /// questions is asked of the panel through a method that has a test behind it. The bar itself
    /// holds no state beyond its own widgets: it reads <see cref="UINeighborhoodSelectionPanel.ActiveLotFilters"/>
    /// and the button states back out of the panel on every refresh, so there is nothing here that
    /// can be out of step with the map.
    /// </para>
    /// </summary>
    public class UINeighbourhoodFilterBar : UIContainer
    {
        /// <summary>Side of one filter or Add Lot button, in pixels. The art's own size.</summary>
        public const int ButtonSize = 44;

        /// <summary>Side of one of the two arrow buttons, in pixels. The art's own size.</summary>
        public const int ArrowSize = 36;

        /// <summary>Inset from the bar's edge to the buttons, in pixels.</summary>
        public const int Padding = 8;

        /// <summary>Gap between two adjacent buttons, in pixels.</summary>
        public const int ButtonGap = 4;

        /// <summary>Gap either side of the count readout, in pixels.</summary>
        public const int SectionGap = 14;

        /// <summary>How far from the top of the window the bar sits.</summary>
        public const int TopMargin = 12;

        /// <summary>Size of the count readout's text.</summary>
        private const int CountTextSize = 18;

        /// <summary>Width left for the count readout, which is the widest thing it can say.</summary>
        private const int CountWidth = 190;

        private readonly UINeighborhoodSelectionPanel _panel;
        private readonly UIImage _backdrop;
        private readonly UILabel _count;
        private readonly List<UITwoStateButton> _filters = new List<UITwoStateButton>();
        private readonly List<NeighLotFilter> _filterKinds = new List<NeighLotFilter>();
        private UITwoStateButton _addLot;
        private UITwoStateButton _previous;
        private UITwoStateButton _next;

        /// <summary>
        /// Every control on the bar, in draw order. Held so <see cref="IsOverAControl"/> can tell a
        /// press that landed on a button from one that landed on the bar itself, and so the layout
        /// walks one list rather than three.
        /// </summary>
        private readonly List<UIElement> Controls = new List<UIElement>();

        /// <summary>
        /// Builds the bar for <paramref name="panel"/>. The panel is the only thing the bar talks
        /// to; it is not null and must stay alive for as long as the bar is on screen, which it
        /// does because the switcher holds both.
        /// </summary>
        public UINeighbourhoodFilterBar(UINeighborhoodSelectionPanel panel)
        {
            _panel = panel ?? throw new ArgumentNullException(nameof(panel));

            var ui = Content.Get().CustomUI;

            //The backdrop is added first so it is behind everything else in this container. It
            //has no mouse listener, so the map under it still takes the click that resets the
            //zoom: the bar only hit-tests where its own buttons are.
            _backdrop = new UIImage(ui.Get("ngbh_filterbar.png").Get(GameFacade.GraphicsDevice))
                .With9Slice(16, 16, 16, 16);

            foreach (var filter in NeighLotFilters.Each())
            {
                var name = NeighLotFilters.IconName(filter);
                if (name == null) continue;
                var button = MakeToggle(ui.Get(name).Get(GameFacade.GraphicsDevice));
                button.OnButtonClick += (b) =>
                {
                    _panel.ToggleLotFilter(filter);
                    Refresh();
                };
                _filters.Add(button);
                _filterKinds.Add(filter);
            }

            _addLot = MakeToggle(ui.Get("ngbh_addlot.png").Get(GameFacade.GraphicsDevice));
            _addLot.OnButtonClick += (b) =>
            {
                _panel.RequestAddLot();
                Refresh();
            };
            _addLot.Tooltip = SimitoneStrings.Get("252", "Add a lot");

            _previous = MakeToggle(ui.Get(NeighLotNavigator.IconName(NeighLotNavigator.Backward))
                .Get(GameFacade.GraphicsDevice));
            _previous.OnButtonClick += (b) =>
            {
                _panel.StepToAdjacentLot(NeighLotNavigator.Backward);
                Refresh();
            };

            _next = MakeToggle(ui.Get(NeighLotNavigator.IconName(NeighLotNavigator.Forward))
                .Get(GameFacade.GraphicsDevice));
            _next.OnButtonClick += (b) =>
            {
                _panel.StepToAdjacentLot(NeighLotNavigator.Forward);
                Refresh();
            };

            _count = new UILabel();
            _count.NewStyle(UIStyle.Current.Text, CountTextSize);
            _count.Alignment = TextAlignment.Middle | TextAlignment.Left;

            // add order is draw order: backdrop, then every control left to right
            Add(_backdrop);
            foreach (var button in _filters) { Add(button); Controls.Add(button); }
            Add(_addLot); Controls.Add(_addLot);
            Add(_count);
            Add(_previous); Controls.Add(_previous);
            Add(_next); Controls.Add(_next);

            Refresh();
            LayOut();

            //The backdrop is the bar's "show every lot" gesture, and the only one it has: a click
            //on any part of the bar that is not a control clears the filters. Retail's bar had
            //thirteen independent toggles and no clear, which is a trap when a second toggle is
            //enough to leave the map empty - this is the way out that is one click rather than one
            //click per filter.
            //
            //Registered last because it has to be in a known size: UIImage's single-argument
            //ListenForMouse takes the image's current width and height as the hit region, and
            //SetSize keeps that region in step afterwards, so registering it before the first
            //LayOut would have left a 48x48 hit area in the middle of the bar.
            _backdrop.Tooltip = NeighLotFilters.BarTooltip();
            _backdrop.ListenForMouse((evt, state) =>
            {
                if (evt != UIMouseEventType.MouseUp) return;
                //A press that landed on a control belongs to that control. The buttons are drawn
                //on top of the backdrop and are hit-tested independently, so without this every
                //filter press would also clear the set it is about to change.
                if (IsOverAControl(state.MouseState)) return;
                if (_panel.ClearLotFilters()) Refresh();
            });
        }

        /// <summary>
        /// Whether a point is over one of the bar's own controls, in the same local-to-global sense
        /// <c>HitTestArea</c> uses.
        /// </summary>
        private bool IsOverAControl(MouseState mouse)
        {
            var global = mouse.Position.ToVector2();
            foreach (var control in Controls)
            {
                if (control.GetBounds().Contains(control.GlobalPoint(global))) return true;
            }
            return false;
        }

        /// <summary>
        /// A four-state toggle. <see cref="UITwoStateButton"/> is a two-state one, so the state
        /// count is put back to four here rather than in a subclass: <c>UIButton.Draw</c> already
        /// picks the right frame for every state this bar has, including the "dead" one.
        /// </summary>
        private static UITwoStateButton MakeToggle(Texture2D texture)
        {
            //The caption is left null on purpose: these buttons are glyphs, and UIButton.Draw
            //skips the caption when there is none. The tooltip carries the name.
            return new UITwoStateButton(texture) { ImageStates = 4 };
        }

        /// <summary>
        /// Reads the panel's state back into the widgets. Called after every press, and whenever
        /// the switcher rebuilds itself after a neighbourhood change.
        ///
        /// <para>
        /// <b>Why the panel is asked to re-read the lots first.</b> Occupancy and zoning both
        /// change under a standing filter - an eviction empties a lot, a rezone moves one across
        /// the residential/community line - and a filter that had quietly gone stale would show the
        /// wrong lots with nothing on screen to say so.
        /// </para>
        /// </summary>
        public void Refresh()
        {
            _panel.RefreshLotState();

            var facts = _panel.LotFacts;
            var hasLots = _panel.TotalLotCount > 0;
            Visible = hasLots;

            for (int i = 0; i < _filters.Count; i++)
            {
                var filter = _filterKinds[i];
                var active = (_panel.ActiveLotFilters & filter) != 0;
                var available = NeighLotFilters.IsAvailable(filter, facts);

                _filters[i].Selected = active;
                //A filter that is on stays clickable even when nothing matches it, because the
                //press that gets the map back is the one that turns it off. Only a filter that is
                //off and cannot be turned on is dead.
                _filters[i].Disabled = !active && !available;
                _filters[i].Tooltip = (active || available)
                    ? NeighLotFilters.Tooltip(filter)
                    : NeighLotFilters.UnavailableTooltip(filter);
            }

            _count.Caption = NeighLotFilters.CountText(_panel.ActiveLotFilters, facts);

            //The arrows step through the lots that are on show, so they are dead when a filter has
            //left nothing to step to. A single lot is not dead: Next then re-selects it, which is
            //also the only way to get its panel back after a filter has hidden the rest.
            var anyLots = _panel.VisibleLotCount > 0;
            _previous.Disabled = !anyLots;
            _next.Disabled = !anyLots;
            _previous.Tooltip = NeighLotNavigator.Caption(NeighLotNavigator.Backward);
            _next.Tooltip = NeighLotNavigator.Caption(NeighLotNavigator.Forward);

            _addLot.Disabled = !hasLots;
            LayOut();
        }

        /// <summary>
        /// Lays the bar out along its own top edge, centred on the window's width, and sizes the
        /// backdrop to whatever the controls came to.
        /// </summary>
        /// <remarks>
        /// The width is summed rather than measured off the widgets so that it is a function of
        /// the layout constants alone: the count readout's caption is a translated string, and a
        /// bar that resized itself to its own text would move the buttons under the player's mouse
        /// when the language changed.
        /// </remarks>
        public void LayOut()
        {
            var y = TopMargin;
            var height = ButtonSize + (Padding * 2);

            var width = (Padding * 2);
            for (int i = 0; i < _filters.Count; i++) width += ButtonSize + ButtonGap;
            width -= ButtonGap;
            width += SectionGap + ButtonSize + SectionGap + CountWidth + SectionGap + ArrowSize + ButtonGap + ArrowSize;

            var screen = UIScreen.Current;
            var windowWidth = screen?.ScreenWidth ?? 0;
            Position = new Vector2((windowWidth - width) / 2, y);

            _backdrop.SetSize(width, height);
            _backdrop.Position = Vector2.Zero;

            var x = Padding;
            var buttonY = Padding;
            foreach (var button in _filters)
            {
                button.Position = new Vector2(x, buttonY);
                x += ButtonSize + ButtonGap;
            }

            x += SectionGap - ButtonGap;
            _addLot.Position = new Vector2(x, buttonY);
            x += ButtonSize;

            x += SectionGap;
            _count.Position = new Vector2(x, Padding);
            _count.Size = new Vector2(CountWidth, ButtonSize);
            x += CountWidth;

            x += SectionGap;
            //the arrows are shorter than the buttons, so they are centred on the same row
            var arrowY = Padding + ((ButtonSize - ArrowSize) / 2);
            _previous.Position = new Vector2(x, arrowY);
            x += ArrowSize + ButtonGap;
            _next.Position = new Vector2(x, arrowY);
        }

        /// <summary>
        /// The bar is anchored to the top centre of the window, so a resize has to re-centre it.
        /// </summary>
        public override void GameResized()
        {
            base.GameResized();
            LayOut();
        }
    }
}
